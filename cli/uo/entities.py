"""Views over the things the client reports: items, mobiles, targets, the walk.

These wrap plain dicts from the JSON files rather than copying them, so building one is free and a
list of them costs nothing to throw away every loop iteration.
"""

from __future__ import annotations

from typing import Optional


class _Record:
    """A dict with attribute access, so `m.name` reads better than `m["name"]` in a loop."""

    __slots__ = ("_d",)

    def __init__(self, data: dict):
        self._d = data or {}

    def __getitem__(self, key):
        return self._d.get(key)

    def get(self, key, default=None):
        return self._d.get(key, default)

    @property
    def raw(self) -> dict:
        """The underlying dict, for fields these classes do not name."""
        return self._d


class Item(_Record):
    @property
    def serial(self) -> str:
        return self._d.get("serial")

    @property
    def name(self) -> str:
        return self._d.get("name") or ""

    @property
    def graphic(self) -> int:
        return self._d.get("graphic", 0)

    @property
    def amount(self) -> int:
        return self._d.get("amount", 1)

    @property
    def hue(self) -> int:
        return self._d.get("hue", 0)

    @property
    def layer(self) -> Optional[str]:
        return self._d.get("layer")

    @property
    def pos(self):
        if "x" not in self._d:
            return None
        return (self._d["x"], self._d["y"], self._d.get("z", 0))

    @property
    def x(self) -> Optional[int]:
        return self._d.get("x")

    @property
    def y(self) -> Optional[int]:
        return self._d.get("y")

    @property
    def distance(self) -> Optional[int]:
        return self._d.get("distance")

    @property
    def is_corpse(self) -> bool:
        """From the world file's `containers` list - a corpse is graphic 0x2006, tagged
        server-side rather than guessed from its name (which may not even contain "corpse")."""
        return bool(self._d.get("isCorpse"))

    @property
    def is_container(self) -> bool:
        """
        Whether this item can be opened - `Item.ItemData.IsContainer` on the client side.

        Set on ground items from the world file's `containers` list, and on items found *inside*
        an already-opened container from `Client.container()`'s `[container]`/`[item]` tag (see
        `CommandEngine.Actions.cs`'s `container` handler) - the same deterministic signal at both
        levels, so a nested bag/crate found while looting a corpse never has to be guessed from
        its name either.
        """
        return bool(self._d.get("isContainer"))

    def __repr__(self):
        amount = f" x{self.amount}" if self.amount > 1 else ""
        return f"<Item {self.serial} {self.name}{amount}>"


class Mobile(_Record):
    @property
    def serial(self) -> str:
        return self._d.get("serial")

    @property
    def name(self) -> str:
        return self._d.get("name") or ""

    @property
    def pos(self):
        return (self._d.get("x"), self._d.get("y"), self._d.get("z"))

    @property
    def x(self) -> int:
        return self._d.get("x")

    @property
    def y(self) -> int:
        return self._d.get("y")

    @property
    def distance(self) -> int:
        return self._d.get("distance", 999)

    @property
    def notoriety(self) -> str:
        return self._d.get("notoriety", "Unknown")

    @property
    def graphic(self) -> int:
        return self._d.get("graphic", 0)

    @property
    def species(self) -> Optional[str]:
        """
        The body graphic as a word - "Orc", "Ratman" - or None when the client's table has no
        entry for this graphic.

        `name` is whatever the *server* sent; not every mobile of a type is named after its type,
        so an orc carrying a personal name ("Gruuk") has "orc" nowhere in its name and only the
        graphic gives it away. This is that graphic resolved through the same table `known_monster`
        uses (`Capabilities/MobNames.cs`), which is why avoid/include can match a creature by type
        without anything on this side owning a copy of the table.

        None is meaningfully different from `name`'s "": the table stops at graphic 403 and has
        gaps, so an unlisted creature is "type unknown", not "type is empty string".
        """
        return self._d.get("species")

    @property
    def known_monster(self) -> bool:
        """
        Body-graphic based classification (a shade, a zombie, ...), independent of notoriety.

        Notoriety alone can't separate animals from monsters that will fight: both read Gray while
        passive, and a monster can stay Gray right up until the moment it commits to attacking -
        by then it may already be adjacent. The graphic id is known the instant the mobile is seen,
        well before any of that, so this is the only signal available in advance rather than after
        the fact. See `Capabilities/MobNames.cs` on the client side for the underlying table.
        """
        return bool(self._d.get("knownMonster"))

    @property
    def is_human(self) -> bool:
        """
        Human body graphic (400/401 and the equivalents), from the client's own `Mobile.IsHuman`.

        Worth being clear about what this does and does not settle: it separates people from
        monsters and animals, and nothing more. The UO protocol carries no "this is a player"
        flag, and neither does the client - `PlayerMobile` is only ever *us*, and `IsRenamable`
        is about pets. A vendor, a town guard and another player all read human here. Combined
        with `known_monster` being false and a name that does not start with an article, this is
        the closest thing to "a person" available without a client change.
        """
        return bool(self._d.get("isHuman"))

    @property
    def hits(self) -> int:
        return self._d.get("hits", 0)

    @property
    def max_hits(self) -> int:
        return self._d.get("maxHits", 0)

    @property
    def hp_pct(self) -> float:
        return 100.0 * self.hits / self.max_hits if self.max_hits else 0.0

    @property
    def is_dead(self) -> bool:
        return bool(self._d.get("isDead"))

    @property
    def in_war_mode(self) -> bool:
        return bool(self._d.get("inWarMode"))

    @property
    def hostile(self) -> bool:
        """
        Whether `attack` will actually accept this target.

        Blue Innocents are refused by the client - attacking one flags you criminal, so it makes you
        confirm - and Invulnerable cannot be harmed at all. Everything else is fair game.
        """
        return self.notoriety in ("Gray", "Criminal", "Enemy", "Murderer")

    def __repr__(self):
        return f"<Mobile {self.serial} {self.name} d={self.distance} {self.notoriety}>"


class Target(_Record):
    """
    A target from the state file's lastAttack / lastCursorTarget.

    Read `kind` before anything else: every other field is None when there is nothing to resolve,
    and None is not zero. `exists` False means a target is set but the client can no longer see it -
    it died, or it walked out of range (which also happens when *you* walk away).
    """

    @property
    def kind(self) -> str:
        return self._d.get("kind", "none")

    @property
    def is_set(self) -> bool:
        return self.kind != "none"

    @property
    def exists(self) -> bool:
        return bool(self._d.get("exists"))

    @property
    def serial(self) -> Optional[str]:
        return self._d.get("serial")

    @property
    def name(self) -> str:
        return self._d.get("name") or ""

    @property
    def pos(self):
        if self._d.get("x") is None:
            return None
        return (self._d["x"], self._d["y"], self._d.get("z"))

    @property
    def hits(self) -> Optional[int]:
        return self._d.get("hits")

    @property
    def max_hits(self) -> Optional[int]:
        return self._d.get("maxHits")

    @property
    def graphic(self) -> Optional[int]:
        return self._d.get("graphic")

    @property
    def species(self) -> Optional[str]:
        """
        The target's body graphic as a word - "Orc" - or None.

        Same field and same meaning as `Mobile.species`, which is what lets a Target be passed to
        `avoidance.is_avoided` alongside a Mobile and match on every term form rather than only the
        ones a name and graphic can answer.

        None here means one of three things, and a caller that cares should check `kind` and
        `exists` rather than reading anything into the None on its own: no target set, a target the
        client can no longer see, a ground/static target (terrain and items have no body graphic),
        or a body graphic the client's table does not list.
        """
        return self._d.get("species")

    @property
    def source(self) -> Optional[str]:
        """'attack' or 'cursor' - how the engagement was established."""
        return self._d.get("source")

    def __repr__(self):
        if not self.is_set:
            return "<Target none>"
        return f"<Target {self.serial} {self.name} exists={self.exists} pos={self.pos}>"


class Nav(_Record):
    """
    The current or most recent walk.

    `goto` is fire and forget - it returns as soon as the walk starts - so this is where the outcome
    lands. Position cannot substitute: "still walking", "arrived" and "gave up, no path" all look
    like a coordinate that may or may not be changing.
    """

    @property
    def active(self) -> bool:
        return bool(self._d.get("active"))

    @property
    def status(self) -> str:
        """idle | walking | arrived | nopath | stopped | failed"""
        return self._d.get("status", "idle")

    @property
    def arrived(self) -> bool:
        return self.status == "arrived"

    @property
    def target(self):
        t = self._d.get("target")
        return tuple(t) if t else None

    @property
    def reason(self) -> Optional[str]:
        return self._d.get("reason")

    def __repr__(self):
        return f"<Nav {self.status} target={self.target}>"
