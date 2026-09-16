#!/usr/bin/env python3
"""Walk via `goto`/`travel`, stopping the instant a threat comes within range.

    python3 .claude/skills/uo-navigation/safe_goto.py <x> <y>
    python3 .claude/skills/uo-navigation/safe_goto.py bank
    python3 .claude/skills/uo-navigation/safe_goto.py bank --watch    # stay on as the aggro watch

Run it in the background and wait for it. **Every exit ends on one `RESUME:` line** - emitted from
a `finally`, so also on SIGTERM, ghost, client gone and `--for` - with the outcome, the phase,
where the character actually is, its HP and how far short of the destination it stopped. The
`HOSTILE STOP:`/`AVOID STOP:` (walking) and `* SEEN:` (watching) lines above it name what tripped
the check. This is the only way `goto`/`travel` should be invoked in this project.

The check runs every poll, not once at the end, with no guard-zone exception. "Threat" is a
notoriety Criminal/Enemy/Murderer or known-monster mobile (not Gray wildlife), or an avoid-listed
(under `--include`, non-included) one; `--range` is the single clearance for both.

A threat during the walk **always retreats before returning** - standing still beside something
that may be approaching while the orchestrator answers at model latency is the worst option.
Something fighting us (war mode within `--aggro-range`) gets the full escape: retreat, confirm it
stays gone for `--threat-clear-secs`, escalate, give up after `--retreat-attempts`, hand off. An
avoided creature merely in the way gets one back-off leg to `--stop-berth`. Anything else is a
plain stop-short report.

With `--watch` it stays on after arriving with the same check running, plus a reactive flee on an
unexpected HP drop, until combat becomes ready - a combat-movement script holds its lock *and*
`.lastAttack` is live - then it steps aside for good. A `--watch` run never returns on its own.
A fresh invocation always takes over from the one running (`default_replace=True`).

Exit codes: 0 arrived (under `--watch`: combat became ready); 1 the walk could not be completed
(nopath/stopped/failed, or a threat came within `--range`); 2 no live client; 130 Ctrl-C;
143 terminated by a takeover or a plain kill.
"""

from __future__ import annotations

import argparse
import os
import sys
import time
from typing import List, Optional, Tuple

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "cli"))

from uo import Client, script  # noqa: E402
from uo.avoidance import (  # noqa: E402
    add_avoid_args,
    avoided_mobiles,
    chebyshev,
    describe,
    is_aggro,
    is_threat,
    resolve_avoid_args,
)
from uo.entities import Mobile  # noqa: E402
from uo.escape import (  # noqa: E402
    HANDOFF_BERTH,
    THREAT_CLEAR_SECS,
    ThreatMemory,
    escalate,
    handoff_message,
    handoff_summary,
)
from uo.geometry import pick_escape_point  # noqa: E402
from uo.locks import running as locks_running  # noqa: E402

HOSTILE_RANGE = 15      # tiles - stop/flee once any threat is this close; the world file carries 18
SURPRISE_RANGE = 5      # tiles - a threat already this close when spotted is worth saying so
RETREAT_TIMEOUT = 6.0   # seconds per retreat leg
FLEE_MARGIN = 5         # tiles past the clearance a flee aims for, so arrival is not on the boundary
REAIM_INTERVAL = 1.5    # seconds between flee `goto`s - a fresh goto cancels the leg in flight
READY_SCRIPTS = ("combat_movement_melee",)

Sighting = Tuple[Mobile, str]     # (mobile, "HOSTILE" | "AVOID")


class Scanner:
    """The threat check both phases share: hostiles plus avoided mobiles, within a range."""

    def __init__(self, uo: Client, avoid, include):
        self.uo, self.avoid, self.include = uo, avoid, include

    def threats(self, within: int) -> List[Sighting]:
        me = self.uo.xy
        hits = [(m, "HOSTILE") for m in self.uo.mobiles() if is_threat(m)]
        hits += [(m, "AVOID") for m in avoided_mobiles(self.uo, self.avoid, self.include)]
        hits = [(m, k) for m, k in hits if chebyshev((m.x, m.y), me) <= within]
        return sorted(hits, key=lambda h: chebyshev((h[0].x, h[0].y), me))


def chasers(threats: List[Sighting], me, aggro_range: int) -> List[Sighting]:
    return [(m, k) for m, k in threats if is_aggro(m, me, aggro_range)]


def remember(threats: List[Sighting]) -> ThreatMemory:
    threat = ThreatMemory()
    for m, _ in threats:
        threat.saw(m.serial, m.name, (m.x, m.y), time.monotonic())
    return threat


def here(uo: Client) -> str:
    x, y = uo.xy
    return f"({x},{y})"


def log_threats(uo: Client, threats: List[Sighting], word: str) -> None:
    me = uo.xy
    for m, kind in threats:
        uo.log(f"{kind} {word}: {m.name or 'unknown'} ({m.serial}) at ({m.x},{m.y}) "
               f"d={chebyshev((m.x, m.y), me)} {m.notoriety}")


def is_ready(uo: Client) -> bool:
    """A combat-movement script is running AND a live target is tracked."""
    live = locks_running(uo.commands.cmd_file)
    t = uo.last_attack
    return any(name in live for name in READY_SCRIPTS) and t.is_set and t.exists


def retreat(scan: Scanner, clearance: int, timeout: float) -> Tuple[bool, List[Mobile]]:
    """One flee leg: `goto` away from everything inside `clearance` until clear or `timeout`.
    Returns `(clear, still_inside)`."""
    uo = scan.uo
    deadline = time.monotonic() + timeout
    next_aim, aim_dir, excluded = 0.0, None, set()

    while True:
        if uo.ghost or not uo.is_live():
            return False, []
        threats = scan.threats(clearance)
        if not threats:
            uo.cancel_walk()
            return True, []
        now = time.monotonic()
        if now >= deadline:
            return False, [m for m, _ in threats]
        if now >= next_aim:
            if aim_dir is not None and uo.nav.status == "nopath":
                excluded.add(aim_dir)
            point, aim_dir = pick_escape_point(uo.xy, [(m.x, m.y) for m, _ in threats],
                                               clearance + FLEE_MARGIN, frozenset(excluded))
            uo.goto(*point)
            next_aim = now + REAIM_INTERVAL
        time.sleep(0.2)


def confirm_clear(scan: Scanner, args, clearance: int) -> Optional[Sighting]:
    """Out of range is not gave up: keep scanning for `--threat-clear-secs` after a retreat clears,
    and return the first thing that comes back."""
    deadline = time.monotonic() + args.threat_clear_secs
    while time.monotonic() < deadline:
        back = scan.threats(clearance)
        if back:
            return back[0]
        time.sleep(args.poll)
    return None


def escape(scan: Scanner, args, threat: ThreatMemory) -> bool:
    """Retreat until genuinely clear, escalating the clearance each time the pursuer comes back.
    Returns False after `--retreat-attempts` legs still pursued."""
    uo = scan.uo
    clearance, attempts = args.range, 0
    while True:
        clear, remaining = retreat(scan, clearance, args.retreat_timeout)
        attempts += 1
        for m, _ in scan.threats(clearance):
            threat.saw(m.serial, m.name, (m.x, m.y), time.monotonic())

        if clear:
            back = confirm_clear(scan, args, clearance)
            if back is None:
                uo.log(f"retreated clear to {here(uo)} after {attempts} leg(s), still clear "
                       f"{args.threat_clear_secs:.0f}s later")
                return True
            m, kind = back
            threat.saw(m.serial, m.name, (m.x, m.y), time.monotonic())
            uo.log(f"{m.name or 'unknown'} came back during the {args.threat_clear_secs:.0f}s "
                   f"confirmation ({kind}, d={chebyshev((m.x, m.y), uo.xy)}) - it has not "
                   f"broken off, running on")

        if attempts >= args.retreat_attempts:
            uo.log(f"retreat gave up at {here(uo)} after {attempts} legs with {len(remaining)} "
                   f"threat(s) still within range - orchestrator: this one will not break off, "
                   f"take a look now")
            return False
        clearance = escalate(clearance, args.retreat_escalation, args.retreat_max_distance)
        uo.log(f"still pursued at {here(uo)} - running on, clearance now {clearance}")


def back_off(scan: Scanner, args, clearance: int, what: str) -> bool:
    """One leg away from something that is not chasing us - nothing to out-last."""
    clear, remaining = retreat(scan, clearance, args.retreat_timeout)
    scan.uo.log(f"backed off to {here(scan.uo)} from {what} "
                f"({'clear' if clear else f'{len(remaining)} still inside {clearance} tiles'})")
    return clear


def stop_for(uo: Client, args, scan: Scanner, run: dict, threats: List[Sighting]) -> int:
    """A threat came within range mid-walk: halt, put distance between us, report. Always exit 1."""
    me = uo.xy
    log_threats(uo, threats, "STOP")
    uo.cancel_walk()
    nearest_d = chebyshev((threats[0][0].x, threats[0][0].y), me)

    chasing = chasers(threats, me, args.aggro_range)
    if chasing:
        threat = remember(chasing)
        close = " - too close to just stop" if nearest_d <= args.surprise_range else ""
        uo.log(f"{threat.label} is fighting us at {nearest_d} tiles{close} - retreating before "
               f"handing back rather than standing here while it closes")
        escape(scan, args, threat)
        uo.log(handoff_message(threat, uo.xy, args.handoff_berth))
        uo.log(handoff_summary(threat, args.handoff_berth))
        run["outcome"] = (f"fled {threat.label} (last seen {threat.where}) - walk abandoned, "
                          f"keep {args.handoff_berth} tiles clear of it")
        return 1

    avoided = [(m, k) for m, k in threats if k == "AVOID"]
    if avoided:
        threat = remember(avoided)
        uo.log(f"{threat.label} is in the way at {nearest_d} tiles and is not fighting us - "
               f"backing off to {args.stop_berth} tiles before handing back")
        back_off(scan, args, args.stop_berth, threat.label)
        uo.log(handoff_message(threat, uo.xy, args.handoff_berth))
        run["outcome"] = (f"blocked by {threat.label} (avoided, not fighting us) - backed off, "
                          f"cannot route past it alone")
        return 1

    uo.log(f"stopped at {here(uo)} - {len(threats)} threatening creature(s) within range, none "
           f"of them avoided or fighting us - orchestrator: route around them")
    run["outcome"] = (f"stopped short - {len(threats)} threatening creature(s) within "
                      f"{args.range} tiles: "
                      + ", ".join(f"{m.name or 'unknown'} at ({m.x},{m.y})"
                                  for m, _ in threats[:4]))
    return 1


def walk(uo: Client, args, scan: Scanner, run: dict) -> Optional[int]:
    """Follow the walk to its end, checking for threats every poll. None if `every()` ended it."""
    seen_active, began = False, time.monotonic()

    for _ in uo.every(args.poll):
        nav = uo.nav
        if nav.active:
            seen_active = True
        elif seen_active or time.monotonic() - began > 2.0:   # goto registers a beat late
            reason = f" ({nav.reason})" if nav.reason else ""
            uo.log(f"nav finished: {nav.status}{reason}")
            if nav.status != "arrived":
                run["outcome"] = f"walk ended in {nav.status}{reason} - did not arrive"
                return 1
            run["phase"], run["outcome"] = "arrived", "arrived at the destination"
            return 0
        else:
            continue

        threats = scan.threats(args.range)
        if threats:
            return stop_for(uo, args, scan, run, threats)
    return None


def watch(uo: Client, args, scan: Scanner, run: dict) -> Optional[int]:
    """The standing aggro watch: flee anything in range or any HP drop, until combat is ready."""
    uo.log(f"arrived - watching for hostiles within {args.range} tiles and for combat becoming ready")
    last_hp = uo.hp

    for _ in uo.every(args.poll):
        if is_ready(uo):
            run["outcome"] = "combat became ready - stepped aside"
            return uo.stop("combat is ready (movement script + live target) - stepping aside",
                           code=0)

        threats = scan.threats(args.range)
        if threats:
            log_threats(uo, threats, "SEEN")
            uo.cancel_walk()
            chasing = chasers(threats, uo.xy, args.aggro_range)
            if chasing:
                escape(scan, args, remember(chasing))
            else:
                back_off(scan, args, args.range, "something not chasing us")
            uo.log("resuming watch")
            last_hp = uo.hp
            continue

        hp = uo.hp
        if hp < last_hp:
            uo.log(f"took damage while watching ({last_hp} -> {hp}) - fleeing")
            uo.cancel_walk()
            back_off(scan, args, args.range, "whatever hit us")
            uo.log("resuming watch")
            hp = uo.hp
        last_hp = hp
    return None


def resume_line(uo: Client, dest_label: str, target, phase: str, outcome: str) -> str:
    """The one line every exit ends on. Read defensively: the client may be gone."""
    try:
        mx, my = uo.xy
        where = f"({mx},{my})"
        hp = f"{uo.hp}/{uo.max_hp}"
    except Exception:
        mx = None
        where = hp = "unknown (no live state file)"

    if target is None:
        remaining = "distance unknown (destination given as a name, not coordinates)"
    elif mx is None:
        remaining = "distance unknown"
    else:
        d = chebyshev((mx, my), target)
        remaining = "reached" if d <= 1 else f"{d} tiles short"
    return f"RESUME: {outcome}; phase={phase}; at {where} hp={hp}; destination {dest_label} - {remaining}"


def _looks_like_coords(tokens) -> bool:
    return len(tokens) == 2 and all(t.lstrip("-").isdigit() for t in tokens)


ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
ap.add_argument("dest", nargs="+", help="x y coordinates, or a POI name/category")
ap.add_argument("--command", choices=("goto", "travel"), default="goto",
                help="underlying walk command (default goto - it already chains legs)")
ap.add_argument("--avoid-tile", action="append", default=[], metavar="X,Y[,R]",
                help="a tile the PLANNER must route around, passed through to goto as "
                     "avoid:X,Y[,R] - repeatable. Distinct from --avoid, which is a creature")
ap.add_argument("--range", type=int, default=HOSTILE_RANGE,
                help=f"tiles - stop/flee once any threat (hostile or avoided) is this close, "
                     f"walking or watching (default {HOSTILE_RANGE})")
ap.add_argument("--surprise-range", type=int, default=SURPRISE_RANGE,
                help=f"tiles - a threat already this close when spotted mid-walk is reported as "
                     f"such; every stop retreats regardless (default {SURPRISE_RANGE})")
ap.add_argument("--retreat-timeout", type=float, default=RETREAT_TIMEOUT,
                help=f"seconds per retreat leg (default {RETREAT_TIMEOUT})")
ap.add_argument("--stop-berth", type=int, default=15,
                help="tiles to put between us and an AVOIDED creature that is in the way but not "
                     "fighting us, before handing back (default 15)")
ap.add_argument("--aggro-range", type=int, default=12,
                help="tiles - a threat in war mode this close counts as fighting us, which turns "
                     "a stop into a full escape (default 12)")
ap.add_argument("--handoff-berth", type=int, default=HANDOFF_BERTH,
                help=f"tiles of clearance the handoff asks to keep from the last sighting "
                     f"(default {HANDOFF_BERTH})")
ap.add_argument("--threat-clear-secs", type=float, default=THREAT_CLEAR_SECS,
                help="seconds to keep scanning after a retreat reports clear before handing back "
                     "- the world file only carries 18 tiles, so out of view is not gone (default 15)")
ap.add_argument("--retreat-attempts", type=int, default=7,
                help="retreat legs to run before handing back anyway (default 7)")
ap.add_argument("--retreat-escalation", type=int, default=10,
                help="tiles to widen the clearance by after a leg that failed to shed the pursuer")
ap.add_argument("--retreat-max-distance", type=int, default=60,
                help="cap on the escalated clearance")
ap.add_argument("--poll", type=float, default=0.4, help="seconds between checks")
ap.add_argument("--watch", action="store_true",
                help="after arriving, stay running as the standing aggro watch instead of exiting")
add_avoid_args(ap)


@script(ap, default_replace=True)
def main(uo, args):
    try:
        avoid, include = resolve_avoid_args(args)
    except ValueError as e:
        return uo.stop(str(e))

    dest = args.dest
    target = (int(dest[0]), int(dest[1])) if _looks_like_coords(dest) else None
    query = f"{target[0]} {target[1]}" if target else " ".join(dest)
    dest_label = f"({target[0]},{target[1]})" if target else query
    avoid_tiles = "".join(f" avoid:{t}" for t in args.avoid_tile)

    uo.commands.send(f"{args.command} {query}{avoid_tiles}")
    mode = describe(avoid, include, verb="approaching")
    uo.log(f"{args.command} {query} - stopping short of any hostile within {args.range} tiles"
           + (f", {mode}" if mode else "")
           + (f", planner routing around {' '.join(args.avoid_tile)}" if args.avoid_tile else ""))

    scan = Scanner(uo, avoid, include)
    run = {"phase": "walking",
           "outcome": "ended without reporting a reason (killed, ghost, client gone, or --for)"}
    try:
        code = walk(uo, args, scan, run)
        if code != 0 or not args.watch:
            return code
        return watch(uo, args, scan, run)
    finally:
        uo.log(resume_line(uo, dest_label, target, run["phase"], run["outcome"]))


if __name__ == "__main__":
    sys.exit(main())
