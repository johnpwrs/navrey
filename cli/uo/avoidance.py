"""Shared avoid/include-list mechanism: which mobiles a script should never target, attack,
retaliate against, or knowingly approach.

Originated in `combat_movement_melee.py`; `safe_goto.py` reuses it verbatim so the two scripts
can't drift on what "avoided" means - a creature named on `--avoid` (or excluded by `--include`)
is the same creature to both, whether the decision is "don't attack it" or "don't walk near it".

A term matches on a mobile's name, its species, or its body graphic - see `Term`. Matching used to
be name-only, which meant a monster carrying a personal name instead of its type's name could not
be named at all: `--avoid ratman` never saw a ratman called "Gruuk". The client now resolves the
body graphic to a species in both the world file and the state file's target blocks, so the type is
matchable however the creature is called, and that resolution stays on the client side - there is
deliberately no copy of the graphic->name table here to drift from `Capabilities/MobNames.cs`.
"""

from __future__ import annotations

import argparse
from typing import List, Tuple

from uo.client import Client
from uo.entities import Mobile

# Default "stay this far from an avoided/non-included mobile" - an unconditional escape/stop
# trigger, independent of whether the mobile currently reads hostile or has landed a hit. Wider
# than a plain pursuit/retreat-point margin on purpose: this has to catch a ranged/spell attacker
# that never produces a "Swing:" line to attribute anything to.
#
# No longer used by `safe_goto.py`, which folded its `--avoid-range` into the single `--range` (15) -
# one clearance for hostiles and avoided mobiles alike, so a stop-short has one number behind it
# rather than two the log could not tell apart. `combat_movement_melee.py` keeps its own
# `--avoid-range` (default 5), because a fight already has `autoheal.py` plus its own
# HP/swarm/aggro checks watching, and a 10-tile keep-away during combat rules out most of a
# graveyard. Nothing reads this constant now; it is kept as the documented rationale for why a
# keep-away default is wider than a pursuit margin.
AVOID_PROXIMITY_RANGE = 10

# Default window for `is_aggro`. See its docstring for why war mode alone is not enough.
AGGRO_RANGE = 12


def chebyshev(a: Tuple[int, int], b: Tuple[int, int]) -> int:
    return max(abs(a[0] - b[0]), abs(a[1] - b[1]))


# Notorieties under which a creature may actually come at us. `Mobile.hostile` uses the same set
# as "what `attack` will accept"; here it is one half of a two-part test, see `is_threat`.
THREAT_NOTORIETY = {"Gray", "Criminal", "Enemy", "Murderer"}


def is_threat(m: Mobile) -> bool:
    """
    A threat is BOTH a known-monster body AND a notoriety that can fight us - never one alone.

    Notoriety alone over-flags: a red-named *player* reads Murderer, and on a Trammel-ruleset
    facet cannot touch us at all (measured: a Murderer player standing in New Haven stopped a walk
    three tiles in). Gray alone covers passive wildlife - boars, rabbits, ravens, dogs.

    Body alone over-flags the other way: a tamed pet with a monster body is Innocent and is
    somebody's follower, not a fight waiting to happen.

    Body is still what makes the test early enough to matter: a monster (a shade, among others)
    can stay Gray right up until it commits to attacking - by then it's often adjacent, which is
    exactly how a shade in Britain Cemetery went unflagged until d=1 and killed a full-HP
    character. `known_monster` is a body-graphic lookup, known the instant the mobile is seen -
    see `Mobile.known_monster` - and Gray is in the notoriety set for the same reason.

    The cost: a human-bodied NPC that will attack (a brigand, an evil mage) is not caught here,
    because nothing on the wire tells a brigand from a red player. `--avoid name:...` covers a
    known one.
    """
    return m.known_monster and m.notoriety in THREAT_NOTORIETY


def is_aggro(m: Mobile, me: Tuple[int, int], within: int = AGGRO_RANGE) -> bool:
    """
    Is `m` fighting *us*, as opposed to merely fighting?

    `Mobile.in_war_mode` (world file `inWarMode`, `WorldFile.cs:160`) means this mobile has its
    weapon out - it is fighting something, not necessarily us. There is no per-mobile "is
    targeting me" field anywhere in what the client serializes, and no aggressor serial either,
    so war mode is the closest real signal available.

    Distance is what makes it about us: a monster in war mode standing near us is, in practice,
    fighting us - if it were fighting something else it would be standing next to that instead.
    Past `within` we assume it is someone else's fight, otherwise a town guard duelling a thief
    across the field would trigger a permanent flee.

    Deliberately *not* combined with `Combat.last_attacker_name` here: that only ever gets set
    from a "Swing: X -> us" narration line, so it is unavailable for the ranged/spell attackers
    this most needs to catch, and mixing an attribution signal into a proximity one makes the
    predicate mean two different things at two different ranges. Callers that want attribution
    (melee's re-acquire path, say) should ask `Combat` directly.
    """
    return m.in_war_mode and chebyshev((m.x, m.y), me) <= within


class Term:
    """
    One parsed `--avoid`/`--include` value, matched against a mobile's name, species or graphic.

    A mobile has three identities and the useful one depends on the spawn. `name` is whatever the
    *server* sent, and not every mobile of a type is named after its type - one may carry a personal
    name, with the type visible only in its body graphic. `species` is that graphic resolved to a
    word by the client's own table - the `species` field on a world-file mobile (`WorldFile.cs`) and
    on a state-file target (`StateFile.cs`) alike, so `--avoid orc` catches an orc called "Gruuk"
    whether it is being scanned for or already engaged. `graphic` is the raw body id, the identity
    that never lies but that nobody wants to type.

    Every term names its axis. There is no bare form:

        species:orc    exact species - "Orc" only, never "Orcish Mage"
        name:gruuk     exact name, case-insensitive
        graphic:7      exact body graphic; `graphic:0x07` is the same id in hex
        any:orc        substring, case-insensitive, against name OR species

    The prefix is required because guessing the axis is what made the old bare form dangerous, and
    silently so in both directions. `--include orc` also substring-matched "an orcish mage", so an
    allowlist meant to keep casters away from a no-Resist character admitted one. And a bare number
    had to be guessed at: `140` could be a body id or part of a name, and only one of those guesses
    is right for any given spawn.

    `any:` still exists because "either identity" is sometimes exactly what is meant - Britain
    cemetery has a creature the server calls "a spectre" whose body graphic is Shade, so `any:` is
    the honest way to say "whatever it is calling itself today". It is a choice now, not a default.
    """

    __slots__ = ("kind", "text", "graphic")

    def __init__(self, kind: str, text: str = "", graphic: int = -1):
        self.kind = kind          # "any" | "name" | "species" | "graphic"
        self.text = text          # lower-cased; unused for "graphic"
        self.graphic = graphic

    def matches(self, m: Mobile) -> bool:
        if self.kind == "graphic":
            return m.graphic == self.graphic

        # `name` is "" when absent and `species` is None - both mean "no value", and neither may be
        # substring-matched: `"" in anything` is True, which would make every term match everything.
        if self.kind == "name":
            return bool(m.name) and m.name.lower() == self.text
        if self.kind == "species":
            return bool(m.species) and m.species.lower() == self.text

        return ((bool(m.name) and self.text in m.name.lower())
                or (bool(m.species) and self.text in m.species.lower()))

    def __str__(self) -> str:
        """The term as it would be typed - what the scripts' startup lines print.

        Those lines are how a run says out loud what it will and won't fight, so they have to read
        back as something you could paste into the next launch.
        """
        if self.kind == "graphic":
            return f"graphic:{self.graphic}"
        return f"{self.kind}:{self.text}"

    def __repr__(self) -> str:
        return f"Term({self})"


def parse_term(raw: str) -> Term:
    """
    Parse one `--avoid`/`--include` value into a `Term`. See `Term` for the forms.

    Raises `ValueError` on a malformed term rather than returning something that never matches -
    a typo'd `graphic:abc` in an `--include` list would otherwise silently avoid-list the entire
    world, which reads as the reflex refusing to fight rather than as a bad argument.
    """
    raw = raw.strip()

    if not raw:
        raise ValueError("empty avoid/include term")

    for kind in ("name", "species", "graphic", "any"):
        prefix = kind + ":"

        if raw.lower().startswith(prefix):
            value = raw[len(prefix):].strip()

            if not value:
                raise ValueError(f"{prefix} term with no value: {raw!r}")

            if kind == "graphic":
                return Term("graphic", graphic=_parse_graphic(value, raw))

            return Term(kind, text=value.lower())

    raise ValueError(
        f"{raw!r} does not say what to match on - prefix it with the identity you mean: "
        f"species:{raw} (exact type), name:{raw} (exact name), any:{raw} (substring of either), "
        f"or graphic:<id>"
    )


def _parse_graphic(value: str, raw: str) -> int:
    try:
        return int(value, 16) if value.lower().startswith("0x") else int(value, 10)
    except ValueError:
        raise ValueError(f"not a graphic id: {raw!r} - use graphic:<decimal> or 0x<hex>") from None


def is_avoided(m: Mobile, avoid: List[Term], include: List[Term]) -> bool:
    """
    True if `m` should never be targeted/attacked/approached.

    Exclude mode (`include` empty): avoided iff some `avoid` term matches. A mobile matching nothing
    is never avoided - permissive default, matching the no-list behavior of "fight/walk anywhere".

    Include mode (`include` non-empty): avoided iff NO `include` term matches - the allowlist is
    inverted into an avoid-everything-else list. A mobile matching nothing IS avoided here -
    conservative default, the opposite of exclude mode's, because "unidentified" cannot be confirmed
    as one of the wanted types.

    Those polarities are unchanged from when this matched names only; what changed is that "matching
    nothing" is now much rarer, because a term also sees the body graphic and the species it
    resolves to. A named orc used to fall into the unmatched case and, under `--include orc`, get
    fled as an unknown - measured live 2026-08-31.

    Terms are expected already parsed - callers own that (once, at startup, via
    `resolve_avoid_args`).
    """
    if include:
        return not any(t.matches(m) for t in include)
    return any(t.matches(m) for t in avoid)


def avoided_mobiles(uo: Client, avoid: List[Term], include: List[Term]) -> List[Mobile]:
    """
    Every currently-visible mobile that counts as avoided under the active mode.

    Exclude mode scans *every* mobile regardless of notoriety/threat - an avoided mobile is
    steered clear of whether or not it currently reads as a threat, since the point of avoiding it
    is not finding out the hard way (a monster can sit Gray right up until the moment it commits
    to attacking).

    Include mode scans only mobiles `is_threat` flags. Under an allowlist, "not on the list"
    describes most of the world (town NPCs, vendors, neutral Gray wildlife - a rabbit, a dog) -
    treating all of that as a keep-away point would make the reflex flinch away from harmless
    things standing nearby, not just the unwanted threats it's actually meant to route around. This
    uses `is_threat` rather than `Mobile.hostile` specifically because `hostile` includes Gray
    (matches what `attack` will accept, not what will actually fight back) - a Gray rabbit was
    observed tripping an avoid-stop under `--include` before this was narrowed to `is_threat`.
    """
    pool = [m for m in uo.mobiles() if is_threat(m)] if include else uo.mobiles()
    return [m for m in pool if is_avoided(m, avoid, include)]


def avoid_points(uo: Client, avoid: List[Term], include: List[Term]) -> List[Tuple[int, int]]:
    return [(m.x, m.y) for m in avoided_mobiles(uo, avoid, include)]


def describe(avoid: List[Term], include: List[Term], verb: str = "fighting") -> str:
    """The active mode as one clause for a startup line, or "" when no list is set."""
    if include:
        return f"ONLY {verb} {', '.join(map(str, include))} (avoiding everything else)"
    if avoid:
        return f"avoiding {', '.join(map(str, avoid))}"
    return ""


_TERM_HELP = ("Every term must say which identity it matches on - there is no bare form. "
              "`species:orc` is an exact species (`Orc`, never `Orcish Mage`) and is usually what "
              "you want, since it catches an orc carrying a personal name like `Gruuk`; "
              "`name:gruuk` is an exact name; `graphic:7` (or `graphic:0x07`) is an exact body "
              "graphic; `any:orc` is a case-insensitive substring of either the name or the "
              "species, for when both spellings are in play. Species comes from the `species` "
              "field - `jq '.mobiles[] | {name, species, graphic}' /tmp/cuoworld.json` shows what "
              "is actually there to match. Repeatable.")


def add_avoid_args(ap: argparse.ArgumentParser) -> argparse.ArgumentParser:
    """`--avoid`/`--include`, worded identically everywhere they appear."""
    ap.add_argument("--avoid", action="append", default=[], metavar="TERM",
                     help="a mobile to never target, attack, retaliate against, or knowingly "
                          "move near. " + _TERM_HELP)
    ap.add_argument("--include", action="append", default=[], metavar="TERM",
                     help="the ONLY mobiles to target, attack, retaliate against, or knowingly "
                          "move near - everything else is treated as avoid-listed. Mutually "
                          "exclusive with --avoid. " + _TERM_HELP)
    return ap


def resolve_avoid_args(args) -> Tuple[List[Term], List[Term]]:
    """
    Parses `args.avoid`/`args.include` into `Term`s and refuses to start if both are set.

    Parsing here rather than per-tick means a malformed term is a launch failure with a usable
    message, not a filter that quietly never matches, and keeps matching down to a comparison in
    the loop.
    """
    if args.avoid and args.include:
        raise ValueError("--avoid and --include are mutually exclusive - pick one mode")
    return [parse_term(a) for a in args.avoid], [parse_term(i) for i in args.include]
