#!/usr/bin/env python3
"""Melee reflex: a three-state machine - kill, loot, escape.

    python3 .claude/skills/uo-combat-loop/combat_movement_melee.py --avoid species:<a> --avoid species:<b>
    python3 .claude/skills/uo-combat-loop/combat_movement_melee.py --include species:<the spawn to fight>

Run it in the background. With no target set it acquires the nearest non-avoided threat within
`--acquire-range` on its own, so launching it is a combat decision, not a passive safety net.

**`autoheal.py` must be running alongside it.** This script never bandages, and an HP escape does
not end until HP is back above `--resume-hp-pct`.

Every iteration builds one threat picture, runs `decide_state()` once, and dispatches to exactly
one handler. Handlers never assign the state, which is what keeps "escape always wins" true.

  escape  low HP, an avoided mobile aggro'd on us or too close, or a swarm - distance, nothing else
  loot    drain our own kills' corpses, one round trip per iteration, swinging at anything adjacent
  kill    acquire, pursue, attack - the default

Every `goto` is sent directly, never through `safe_goto.py` (the in-combat exception in CLAUDE.md).
There is no `time.sleep` anywhere here; `uo.every()` and `Looter.next_action` are the only pacing.

Exit codes come from @script: 0 finished, 1 stopped on a reported condition, 2 no live client,
3 already running, 130 Ctrl-C, 143 terminated.
"""

from __future__ import annotations

import argparse
import os
import sys
import time
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Sequence, Set, Tuple

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "cli"))

from uo import Client, script                                          # noqa: E402
from uo.avoidance import (                                             # noqa: E402
    add_avoid_args,
    avoided_mobiles,
    chebyshev,
    describe,
    is_aggro,
    is_avoided,
    is_threat,
    resolve_avoid_args,
)
from uo.entities import Item, Mobile                                   # noqa: E402
from uo.escape import (                                                # noqa: E402
    HANDOFF_BERTH,
    THREAT_CLEAR_SECS,
    ThreatMemory,
    escalate,
    handoff_message,
    handoff_summary,
)
from uo.geometry import (                                              # noqa: E402,F401
    _COMPASS,
    KeepAway,
    away_direction,
    pick_escape_point,
    pick_pursuit_point,
    safe_point,
)
from uo.locks import running as locks_running                          # noqa: E402

RETALIATE_WINDOW = 2.0        # seconds a "Swing: X -> us" line still means "something is fighting us"
OPEN_PAUSE = 1.5              # after `use <container>` before its contents can be listed
GET_PAUSE = 1.5               # the server's action throttle between `get`s
LIST_PAUSE = 0.3              # keeps `container` queries from busy-spinning
MAX_GET_ATTEMPTS = 3          # per item, before giving up on it
TARGET_BLACKLIST_SECS = 30.0  # how long a lost (fled) target stays un-acquirable
# An unreachable target stays un-acquirable much longer than a fled one. 30s was shorter than the
# time it took to strike out every candidate in range, so the blacklist expired on the first zombie
# before the last one was tried, and the reflex cycled through an unreachable spawn forever without
# the empty-ground handoff ever firing (measured 2026-09-10, Old Haven's fenced zombie pen).
UNREACHABLE_BLACKLIST_SECS = 300.0
WORLD_RANGE = 18              # tiles the world file carries (WorldFile.cs RANGE_TILES)
STUCK_LOG_INTERVAL = 5.0      # how often to say "not moving" while escaping

KILL, LOOT, ESCAPE = "kill", "loot", "escape"
Point = Tuple[int, int]


class Mover:
    """The only path to `uo.goto`. Re-issues only when the point changes or the walk ended, since a
    fresh `goto` every poll cancels the walk before it gets anywhere."""

    def __init__(self, uo: Client):
        self.uo = uo
        self.aiming_at: Optional[Point] = None

    def to(self, point: Optional[Point]) -> None:
        if point is None:
            return
        if point != self.aiming_at or not self.uo.nav.active:
            self.uo.goto(*point)
            self.aiming_at = point

    def clear(self) -> None:
        self.aiming_at = None

    def stop(self) -> None:
        self.uo.cancel_walk()
        self.aiming_at = None


# ---------------------------------------------------------------------------------------------
# The per-iteration threat picture
# ---------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class Threats:
    """One snapshot per iteration; everything below is derived without further file reads."""

    now: float
    me: Point
    hp_pct: float
    mobiles: List[Mobile]
    threatening: List[Mobile]
    avoided: List[Mobile]
    aggro: Set[str]
    corpses: List[Item]

    def keepaway(self, aggro_range: int, idle_range: int) -> List[Tuple[Point, int]]:
        """Where not to walk: a wide berth for an aggro'd avoided mobile, a small one for an idle one."""
        return [((m.x, m.y), aggro_range if m.serial in self.aggro else idle_range)
                for m in self.avoided]

    @property
    def threat_pts(self) -> List[Point]:
        return list({m.serial: (m.x, m.y) for m in [*self.threatening, *self.avoided]}.values())

    def find(self, serial: str) -> Optional[Mobile]:
        return next((m for m in self.mobiles if m.serial == serial), None)

    def within(self, mobs: Sequence[Mobile], radius: int) -> List[Mobile]:
        return [m for m in mobs if chebyshev((m.x, m.y), self.me) <= radius]

    def avoided_close(self, radius: int) -> List[Mobile]:
        return self.within(self.avoided, radius)

    def avoided_idle_close(self, radius: int) -> List[Mobile]:
        return [m for m in self.avoided_close(radius) if m.serial not in self.aggro]

    def avoided_aggro(self) -> List[Mobile]:
        return [m for m in self.avoided if m.serial in self.aggro]

    def aggro_threats_within(self, radius: int) -> List[Mobile]:
        return [m for m in self.within(self.threatening, radius) if m.serial in self.aggro]

    def swarm(self, count: int, radius: int) -> bool:
        return len(self.within(self.threatening, radius)) >= count

    def nearest_threat_distance(self) -> int:
        return min((chebyshev(p, self.me) for p in self.threat_pts), default=999)


def survey(uo: Client, args, st: "Brain") -> Threats:
    with uo.frozen():
        me = uo.xy
        hp, max_hp = uo.hp, uo.max_hp

    mobiles = uo.mobiles()
    skip = st.looter.done | st.looter.unreachable
    return Threats(
        now=time.monotonic(),
        me=me,
        hp_pct=(100.0 * hp / max_hp) if max_hp else 100.0,
        mobiles=mobiles,
        threatening=[m for m in mobiles if is_threat(m)],   # not `hostile`: that includes Gray wildlife
        avoided=avoided_mobiles(uo, st.avoid, st.include),
        aggro={m.serial for m in mobiles if is_aggro(m, me, args.aggro_range)},
        corpses=[c for c in uo.containers(within=args.loot_radius)
                 if c.is_corpse and c.serial not in skip],
    )


def update_wanted(uo: Client, args, t: Threats, st: "Brain") -> None:
    """Keep the running list of what this run wants to fight: every threatening, non-avoided,
    non-blacklisted mobile within --acquire-range. An entry leaves the moment its mobile leaves
    the world file (dead or gone), is blacklisted (fled or unreachable), turns out to be avoided,
    or drifts past acquire range (acquisition never looks that far, so it would idle forever).

    The list is what `empty_ground` reads: when it is empty there is nothing here for us and the
    run hands off at once. It used to wait out a 25s combat grace and then a 10s empty timer
    before saying so - measured 2026-09-11 at a fenced zombie pen as half a minute of standing
    still after the last candidate was struck out, and the same again after every kill on thin
    ground."""
    seen = {m.serial: m for m in t.mobiles}
    for serial, (name, _) in list(st.wanted.items()):
        m = seen.get(serial)
        why = None
        if m is None:
            why = "gone"
        elif st.blacklisted(serial, t.now):
            why = "unreachable" if serial in st.unreachable else "given up"
        elif is_avoided(m, st.avoid, st.include):
            why = "avoid-listed"
        elif chebyshev((m.x, m.y), t.me) > args.acquire_range:
            why = f"out of range ({chebyshev((m.x, m.y), t.me)} tiles)"
        if why is not None:
            del st.wanted[serial]
            uo.log(f"wanted: -{name} {serial} {why} ({len(st.wanted)} left)")
        else:
            st.wanted[serial] = (name, (m.x, m.y))

    for m in t.within(t.threatening, args.acquire_range):
        if (m.serial in st.wanted or st.blacklisted(m.serial, t.now)
                or is_avoided(m, st.avoid, st.include)):
            continue
        st.wanted[m.serial] = (m.name or "unnamed", (m.x, m.y))
        uo.log(f"wanted: +{m.name or 'unnamed'} {m.serial} at ({m.x}, {m.y}) "
               f"({len(st.wanted)} to fight)")


def locate(uo: Client, serial: str, me: Optional[Point] = None,
           max_range: int = WORLD_RANGE) -> Optional[Point]:
    """Where the target is: the world file first, then `lastAttack`'s remembered position, but only
    within the world file's own radius - beyond that it is a memory, not an observation."""
    for mob in uo.mobiles():
        if mob.serial == serial:
            return (mob.x, mob.y)

    target = uo.last_attack
    if target.exists and target.pos:
        where = (target.pos[0], target.pos[1])
        if me is None or chebyshev(where, me) <= max_range:
            return where
    return None


def select_target(candidates: Sequence[Mobile], avoid: list, include: list,
                  keepaway: KeepAway, blacklist: Dict[str, float], now: float) -> Optional[Mobile]:
    """Nearest candidate that is not avoided, not blacklisted, and not standing inside a berth."""
    for m in candidates:
        if is_avoided(m, avoid, include) or blacklist.get(m.serial, 0.0) > now:
            continue
        if keepaway and not safe_point((m.x, m.y), keepaway):
            continue
        return m
    return None


def find_corpse_near(uo: Client, pos: Point, radius: int, skip: Set[str]) -> Optional[Item]:
    """Nearest corpse to `pos` (the dead target's last position) - this confirms *our* kill."""
    candidates = [c for c in uo.containers()
                  if c.is_corpse and c.serial not in skip and chebyshev((c.x, c.y), pos) <= radius]
    return min(candidates, key=lambda c: chebyshev((c.x, c.y), pos)) if candidates else None


# ---------------------------------------------------------------------------------------------
# The looter
# ---------------------------------------------------------------------------------------------

@dataclass
class _Level:
    """One open container in the drain: the corpse at depth 0, a bag inside it at 1."""
    serial: str
    label: str
    depth: int
    opened: bool = False
    contents: Optional[List[Item]] = None   # None means "needs (re-)listing"
    attempts: Dict[str, int] = field(default_factory=dict)


class Looter:
    """Drains our own kills' corpses, at most one server round trip per `step()`, never sleeping.
    The cursor (queue, open-container stack, `next_action`) survives state changes, so an escape
    that interrupts a drain resumes mid-corpse. Nested containers are opened in place, never taken."""

    LOGGED = frozenset({"opened", "got", "corpse-done", "level-done", "level-skipped"})

    def __init__(self, uo: Client, args):
        self.uo = uo
        self.args = args
        self.queue: List[str] = []
        self.done: Set[str] = set()
        self.unreachable: Set[str] = set()
        self.gave_up: Set[str] = set()       # item serials this run refuses to retry
        self.seen: Dict[str, Item] = {}      # serial -> corpse Item, for position
        self.stack: List[_Level] = []
        self.next_action: float = 0.0
        self.detail: str = ""
        self.corpse_started: float = 0.0
        self.approach_started: float = 0.0

    def enqueue(self, corpse: Item) -> bool:
        """The only way work gets in, called only on a corpse confirmed to be our kill."""
        self.seen[corpse.serial] = corpse
        if (corpse.serial in self.done or corpse.serial in self.unreachable
                or corpse.serial in self.queue or self.current() == corpse.serial):
            return False
        self.queue.append(corpse.serial)
        return True

    def has_work(self) -> bool:
        return bool(self.stack or self.queue)

    def current(self) -> Optional[str]:
        return self.stack[0].serial if self.stack else None

    def current_item(self) -> Optional[Item]:
        serial = self.current()
        return self.seen.get(serial) if serial else None

    def abandon_current(self, reason: str) -> None:
        serial = self.current()
        if serial is None:
            return
        self.uo.log(f"loot: giving up on {serial} - {reason}")
        self.unreachable.add(serial)
        self.stack = []

    def step(self, now: float) -> str:
        """At most one action, then return."""
        if now < self.next_action:
            return "waiting"

        if not self.stack:
            while self.queue:
                serial = self.queue.pop(0)
                if serial in self.done or serial in self.unreachable:
                    continue
                item = self.seen.get(serial)
                self.stack = [_Level(serial, item.name if item else serial, depth=0)]
                self.corpse_started = self.approach_started = now
                return "picked"
            return "idle"

        if now - self.corpse_started > self.args.loot_corpse_timeout:
            self.abandon_current(f"still not empty after {self.args.loot_corpse_timeout:.0f}s")
            return "acted"

        level = self.stack[-1]

        if not level.opened:
            self.uo.use(level.serial)
            level.opened = True
            self.detail = f"{level.label} ({level.serial}) at depth {level.depth}"
            self.next_action = now + OPEN_PAUSE
            return "opened"

        if level.contents is None:
            try:
                listed = self.uo.container(level.serial)
            except Exception as exc:
                self.uo.log(f"loot: could not list {level.label} ({exc}) - skipping it")
                self._pop(level)
                return "acted"
            level.contents = [i for i in listed if i.serial not in self.gave_up]
            self.next_action = now + LIST_PAUSE
            return "listed"

        if not level.contents:
            self._pop(level)
            self.detail = f"{level.label} ({level.serial}) empty"
            return "corpse-done" if level.depth == 0 else "level-done"

        item = level.contents[0]

        if item.is_container:      # cannot be picked up out of a corpse - open it in place instead
            self.detail = f"{item.name} inside {level.label}"
            if level.depth < self.args.max_loot_depth:
                self.stack.append(_Level(item.serial, item.name, level.depth + 1))
                return "descend"
            self.uo.log(f"loot: {item.name} is nested deeper than {self.args.max_loot_depth} "
                        f"- leaving it")
            self.gave_up.add(item.serial)
            level.contents = None
            return "level-skipped"

        self.uo.get_item(item.serial)
        self.detail = f"{item.name} from {level.label}"
        level.attempts[item.serial] = level.attempts.get(item.serial, 0) + 1
        if level.attempts[item.serial] >= MAX_GET_ATTEMPTS:
            self.uo.log(f"loot: {item.serial} ({item.name}) wouldn't leave {level.label} after "
                        f"{MAX_GET_ATTEMPTS} tries - leaving it")
            self.gave_up.add(item.serial)
        level.contents = None      # re-list: the only real confirmation the item left
        self.next_action = now + GET_PAUSE
        return "got"

    def _pop(self, level: _Level) -> None:
        self.stack.pop()
        if level.depth == 0:
            self.done.add(level.serial)
        else:
            self.gave_up.add(level.serial)   # a drained bag stays on the corpse - never re-enter it
            if self.stack:
                self.stack[-1].contents = None


# ---------------------------------------------------------------------------------------------
# Cross-iteration state
# ---------------------------------------------------------------------------------------------

@dataclass
class Escape:
    reason: str
    distance: int
    started_at: float                   # re-based by the stuck timer
    start_pos: Point                    # re-based by the stuck timer
    origin: Optional[Point] = None      # where the escape began, never re-based
    origin_at: float = 0.0
    holding: bool = False
    excluded: Set[Point] = field(default_factory=set)
    aim_dir: Optional[Point] = None
    aim_point: Optional[Point] = None   # latched destination - see escape_aim
    aimed_at: float = 0.0
    resets: int = 0
    last_stuck_log: float = 0.0
    best_hp: float = -1.0
    hp_improved_at: float = 0.0
    handoff: bool = False               # hand off rather than resume when it clears
    threat: ThreatMemory = field(default_factory=ThreatMemory)

    def __post_init__(self):
        self.origin = self.origin or self.start_pos
        self.origin_at = self.origin_at or self.started_at
        self.handoff = self.reason.startswith("avoid")


@dataclass
class Brain:
    """Everything that survives an iteration."""
    mover: Mover
    looter: Looter
    avoid: list
    include: list
    state: str = KILL
    escape: Optional[Escape] = None
    current: Optional[str] = None                 # tracked target serial
    lost_since: Optional[float] = None
    last_pos: Optional[Point] = None
    anchor: Optional[Point] = None                # where the fighting was, to come back to
    finish: Optional[Tuple[str, int]] = None      # a handler asking main to end the run
    empty_since: Optional[float] = None
    wanted: Dict[str, Tuple[str, Point]] = field(default_factory=dict)   # serial -> (name, last pos)
    avoid_seen: Dict[str, Tuple[str, Point, float]] = field(default_factory=dict)
    last_attack_sent: float = 0.0
    pursuit_nopaths: int = 0
    blacklist: Dict[str, float] = field(default_factory=dict)
    unreachable: Dict[str, Point] = field(default_factory=dict)   # serial -> where it stood when given up on
    escape_suppressed_until: float = 0.0
    giveups: int = 0
    throttles: Dict[str, float] = field(default_factory=dict)

    def throttle(self, key: str, secs: float, now: float) -> bool:
        """True at most once per `secs` for `key`."""
        if now - self.throttles.get(key, 0.0) > secs:
            self.throttles[key] = now
            return True
        return False

    def attack(self, uo: Client, serial: str, now: float, interval: float) -> bool:
        if now - self.last_attack_sent < interval:
            return False
        uo.war(True)
        uo.attack(serial)
        self.last_attack_sent = now
        return True

    def drop_target(self, serial: str, now: float) -> None:
        """Blacklisting is what makes giving up stick - `lastAttack` still names the mobile."""
        self.blacklist[serial] = now + TARGET_BLACKLIST_SECS
        self.current = None

    def drop_unreachable(self, serial: str, where: Point, now: float) -> None:
        self.blacklist[serial] = now + UNREACHABLE_BLACKLIST_SECS
        self.unreachable[serial] = where
        self.current = None

    def blacklisted(self, serial: str, now: float) -> bool:
        return self.blacklist.get(serial, 0.0) > now


# ---------------------------------------------------------------------------------------------
# The transition table
# ---------------------------------------------------------------------------------------------

def escape_reason(args, t: Threats, st: Brain) -> Optional[str]:
    """Which escape the picture calls for; order is precedence. HP is never suppressed."""
    if t.hp_pct < args.flee_hp_pct:
        return "hp"
    if t.now < st.escape_suppressed_until:
        return None
    if t.avoided_aggro():
        return "avoid-aggro"
    if t.avoided_idle_close(args.avoid_idle_range):
        return "avoid-near"
    if t.swarm(args.swarm_count, args.swarm_range) and t.hp_pct <= args.swarm_hp_pct:
        return "swarm"
    return None


def flee_distance_for(args, reason: Optional[str]) -> int:
    return {"hp": args.hp_flee_distance,
            "avoid-aggro": args.aggro_flee_distance,
            "swarm": args.swarm_flee_distance}.get(reason, args.flee_distance)


def pursuit_range_for(args, reason: Optional[str]) -> int:
    """How close a chaser may be before an escape has to keep running. An avoid-aggro escape uses
    a range wider than the world file, so it holds "nothing hostile in view at all"."""
    return args.aggro_pursuit_range if reason == "avoid-aggro" else args.pursuit_range


def resumed(args, t: Threats, st) -> bool:
    """Is the escape over? Every gate is wider than the trigger, so a mobile on the boundary
    cannot flip the state every tick."""
    e = st.escape
    if e is None:
        return False
    if t.now - e.started_at < args.escape_min_dwell:
        return False
    if chebyshev(t.me, e.origin or e.start_pos) < e.distance:
        return False
    if t.avoided_aggro() or t.avoided_close(args.aggro_range + args.escape_hysteresis):
        return False
    if t.hp_pct < args.resume_hp_pct:          # never stand still hurt - keep something steering
        return False
    if e.reason == "swarm":                    # over once the pack is broken up, whatever else is around
        return not t.swarm(args.swarm_count, args.swarm_range)
    if not e.threat.confirmed_clear(t.now, args.threat_clear_secs):   # out of view is not shaken off
        return False
    return t.nearest_threat_distance() >= pursuit_range_for(args, e.reason)


def decide_state(args, t: Threats, st: Brain) -> Tuple[str, Optional[str]]:
    """The whole transition table, in precedence order. Issues no commands."""
    reason = escape_reason(args, t, st)
    if reason is not None:
        return ESCAPE, reason
    if st.escape is not None and not resumed(args, t, st):
        return ESCAPE, st.escape.reason
    if args.loot and st.looter.has_work():      # LOOT defends itself; only pursuit loses the loot
        return LOOT, None
    return KILL, None


# ---------------------------------------------------------------------------------------------
# Escape
# ---------------------------------------------------------------------------------------------

def hand_off(uo: Client, args, t: Threats, escaped: Escape):
    """Give the decision back: the reflex knows only the ground it just fled."""
    uo.log(handoff_message(escaped.threat, t.me, args.handoff_berth))
    return uo.stop(handoff_summary(escaped.threat, args.handoff_berth), code=1)


def autoheal_running(uo) -> bool:
    try:
        return any("autoheal" in name for name in locks_running(uo.commands.cmd_file))
    except Exception:
        return False


def track_escaped_threat(t: Threats, e: Escape) -> None:
    """Record what we are running from while it is still visible, locked to the first serial."""
    if e.threat.serial is not None:
        m = next((m for m in t.avoided if m.serial == e.threat.serial), None)
        if m is not None:
            e.threat.saw(m.serial, m.name, (m.x, m.y), t.now)
        return
    candidates = t.avoided_aggro() or t.avoided
    if candidates:
        m = min(candidates, key=lambda m: chebyshev((m.x, m.y), t.me))
        e.threat.saw(m.serial, m.name, (m.x, m.y), t.now)


def give_up_escape(uo: Client, args, t: Threats, st: Brain, why: str) -> None:
    """Release the latch and fight back briefly; after `--escape-giveup-limit` times, hand off."""
    e = st.escape
    st.escape = None
    st.escape_suppressed_until = t.now + args.escape_giveup
    st.giveups += 1
    track_escaped_threat(t, e)

    if st.giveups >= args.escape_giveup_limit:
        uo.log(handoff_message(e.threat, t.me, args.handoff_berth))
        st.finish = (f"gave up escaping {st.giveups}x ({why}) at {t.me}, hp {t.hp_pct:.0f}% - "
                     f"cannot break away and cannot win here, handing off to pick new ground", 1)
        return
    uo.log(f"escape ({e.reason}): {why} - fighting back for {args.escape_giveup:.0f}s "
           f"(give-up {st.giveups}/{args.escape_giveup_limit})")


def escape_hold(uo: Client, args, t: Threats, st: Brain, e: Escape) -> bool:
    """Hold once clear, confirmed, and far enough; escalate the target when a pursuer returns."""
    flown = chebyshev(t.me, e.origin or e.start_pos)
    near = t.nearest_threat_distance()
    clear = (e.threat.confirmed_clear(t.now, args.threat_clear_secs)
             and near >= pursuit_range_for(args, e.reason)
             and flown >= e.distance)

    if clear:
        if not e.holding:
            uo.log(f"escape ({e.reason}): {flown} tiles out, nearest threat "
                   f"{'none in view' if near > 900 else f'{near} tiles'} - holding"
                   + (f" (hp {t.hp_pct:.0f}%, need {args.resume_hp_pct:.0f}%)"
                      if e.reason == "hp" else ""))
            e.holding = True
            st.mover.stop()
        return True

    if e.holding:
        before = e.distance
        e.distance = escalate(e.distance, args.escape_escalation, args.escape_max_distance)
        e.aim_point = None
        uo.log(f"escape ({e.reason}): pursued to {near} tiles - running on"
               + (f", target {before} -> {e.distance}" if e.distance != before
                  else f", target capped at {e.distance}"))
    e.holding = False
    return False


def escape_bounds(uo: Client, args, t: Threats, st: Brain, e: Escape) -> bool:
    """Stuck, stalled and over-long escapes. False when the escape was ended here."""
    moved = chebyshev(t.me, e.start_pos)

    if t.now - e.started_at > args.escape_timeout:
        if moved > 2:
            e.started_at, e.start_pos = t.now, t.me
        elif e.resets == 0:
            uo.log(f"escape ({e.reason}): {args.escape_timeout:.0f}s and only {moved} tiles - "
                   f"resetting blocked directions")
            e.excluded.clear()
            e.resets += 1
            e.started_at, e.start_pos = t.now, t.me
            st.mover.clear()
        elif e.reason != "hp":
            give_up_escape(uo, args, t, st, "cornered, not moving")
            return False

    if e.reason == "hp":     # bounded on HP progress, not time - nothing else ends an HP escape
        if t.hp_pct > e.best_hp:
            e.best_hp, e.hp_improved_at = t.hp_pct, t.now
        elif e.hp_improved_at and t.now - e.hp_improved_at > args.hp_stall_secs:
            healer = "autoheal is running" if autoheal_running(uo) else "AUTOHEAL IS NOT RUNNING"
            uo.log(handoff_message(e.threat, t.me, args.handoff_berth))
            st.escape = None
            st.finish = (f"hp escape stalled at {t.hp_pct:.0f}% for {args.hp_stall_secs:.0f}s "
                         f"(need {args.resume_hp_pct:.0f}%, {healer}) - not recovering, "
                         f"handing off: check bandages", 1)
            return False

    if moved == 0 and t.now - e.last_stuck_log > STUCK_LOG_INTERVAL:
        uo.log(f"escape ({e.reason}): still at {t.me}, nav={uo.nav.status}")
        e.last_stuck_log = t.now

    if e.reason != "hp" and t.now - (e.origin_at or e.started_at) > args.escape_max_seconds:
        give_up_escape(uo, args, t, st, f"{args.escape_max_seconds:.0f}s and still pursued")
        return False
    return True


def escape_aim(uo: Client, args, t: Threats, st: Brain, e: Escape) -> None:
    """Aim once and keep it: re-picking from the moving position every poll cancels the walk."""
    stale = t.now - e.aimed_at >= args.escape_reaim
    if e.aim_point is None or not uo.nav.active or stale:
        e.aim_point, e.aim_dir = pick_escape_point(t.me, t.threat_pts, e.distance,
                                                   frozenset(e.excluded))
        e.aimed_at = t.now
    st.mover.to(e.aim_point)


def do_escape(uo: Client, args, t: Threats, st: Brain) -> None:
    e = st.escape
    if e is None:
        return
    if e.aim_dir is not None and uo.nav.status == "nopath":
        uo.log(f"escape ({e.reason}): direction {e.aim_dir} unreachable - trying another")
        e.excluded.add(e.aim_dir)
        st.mover.clear()
    if escape_hold(uo, args, t, st, e):
        return
    if escape_bounds(uo, args, t, st, e):
        escape_aim(uo, args, t, st, e)


# ---------------------------------------------------------------------------------------------
# Loot
# ---------------------------------------------------------------------------------------------

def do_loot(uo: Client, args, t: Threats, st: Brain) -> None:
    adjacent = [m for m in t.aggro_threats_within(args.loot_defend_range)
                if not is_avoided(m, st.avoid, st.include)]
    if adjacent:     # defend without moving
        st.attack(uo, adjacent[0].serial, t.now, args.attack_interval)

    looter = st.looter
    corpse = looter.current_item()
    if corpse is None and looter.has_work():
        looter.step(t.now)
        corpse = looter.current_item()
    if corpse is None:
        return

    if looter.current() not in {c.serial for c in t.corpses}:
        looter.abandon_current("corpse no longer in view")
        return

    if chebyshev((corpse.x, corpse.y), t.me) > args.loot_reach:
        if not adjacent:
            st.mover.to(pick_pursuit_point(t.me, (corpse.x, corpse.y), 1,
                                           t.keepaway(args.avoid_range, args.avoid_idle_range)))
        if t.now - looter.approach_started > args.loot_approach_timeout:
            looter.abandon_current("could not reach it")
        return

    st.mover.clear()
    looter.approach_started = t.now
    result = looter.step(t.now)
    if result in Looter.LOGGED:
        uo.log(f"loot: {result} - {looter.detail}")


# ---------------------------------------------------------------------------------------------
# Kill
# ---------------------------------------------------------------------------------------------

def adopt_target(uo: Client, t: Threats, st: Brain):
    """`lastAttack` if it is worth pursuing. It outlives our interest - a fled or given-up target
    is blacklisted, and a visible avoid-listed one (set by an earlier run or by hand) is dropped."""
    target = uo.last_attack
    if not target.is_set or st.blacklist.get(target.serial, 0.0) > t.now:
        return None
    seen = t.find(target.serial)
    if seen is not None and is_avoided(seen, st.avoid, st.include):
        uo.log(f"{target.serial} ({seen.name}) is avoid-listed - dropping it as a target "
               f"rather than fighting it")
        st.drop_target(target.serial, t.now)
        return None
    return target


def acquire(uo: Client, args, t: Threats, st: Brain) -> None:
    st.current = st.lost_since = None
    st.mover.clear()

    combat = uo.combat      # an incoming swing is ground truth even when lastAttack was cleared
    if combat.since_attacked <= RETALIATE_WINDOW:
        name = combat.last_attacker_name
        attacker = uo.nearest(named=name, hostile=True) if name else None
        if attacker is not None and not is_avoided(attacker, st.avoid, st.include):
            if st.attack(uo, attacker.serial, t.now, args.attack_interval):
                uo.log(f"under attack from {name} with no target set - re-attacking {attacker.serial}")
        return

    keepaway = t.keepaway(args.avoid_range, args.avoid_idle_range)
    picked = select_target(t.within(t.threatening, args.acquire_range), st.avoid, st.include,
                           keepaway, st.blacklist, t.now)
    if picked is not None:
        st.anchor, st.empty_since = (picked.x, picked.y), None
        if st.attack(uo, picked.serial, t.now, args.attack_interval):
            uo.log(f"acquiring target {picked.serial} ({picked.name})")
        return

    if st.anchor and chebyshev(t.me, st.anchor) <= args.acquire_range:
        uo.log(f"nothing left around {st.anchor} - dropping anchor")
        st.anchor = None
        st.mover.clear()     # and fall through: an empty wanted list hands off this iteration

    if st.anchor and args.return_to_fight:
        for name, pos, seen_at in st.avoid_seen.values():
            if (t.now - seen_at <= args.avoid_memory_secs
                    and chebyshev(pos, st.anchor) <= args.avoid_memory_range):
                uo.log(f"not returning to {st.anchor} - {name} was seen at {pos} "
                       f"{t.now - seen_at:.0f}s ago, {chebyshev(pos, st.anchor)} tiles from it")
                st.anchor = None
                st.mover.clear()
                break

    if st.anchor and args.return_to_fight:
        if st.throttle("return", 5.0, t.now):
            uo.log(f"nothing in range - returning to {st.anchor} "
                   f"({chebyshev(t.me, st.anchor)} tiles)")
        st.mover.to(pick_pursuit_point(t.me, st.anchor, 0, keepaway))
        st.empty_since = None
        return

    empty_ground(uo, args, t, st)


def empty_ground(uo: Client, args, t: Threats, st: Brain) -> None:
    """The wanted list is empty and nothing is engaged: hand off to pick new ground, now.

    `st.wanted` is maintained by `update_wanted` every iteration, so this reads a list rather than
    re-deriving one. A blacklisted mobile is never on it - a spawn the character can see but never
    path to (zombies behind a fence) used to reset an empty clock every poll and the reflex cycled
    through them for as long as it was left running. `--empty-exit-secs` is an optional grace on
    top; the default is none, because every second here is a second standing still.
    """
    if st.wanted:
        st.empty_since = None
        return
    if args.empty_exit_secs > 0:
        if st.empty_since is None:
            st.empty_since = t.now
            return
        if t.now - st.empty_since < args.empty_exit_secs:
            return

    in_range = [m for m in t.within(t.threatening, args.acquire_range)
                if not is_avoided(m, st.avoid, st.include)]

    def nearest_of(mobs):
        m = min(mobs, key=lambda m: chebyshev(t.me, (m.x, m.y)))
        return f"{m.name} at ({m.x}, {m.y}), {chebyshev(t.me, (m.x, m.y))} tiles"

    notes = []
    unreachable = [m for m in in_range if m.serial in st.unreachable]
    if unreachable:
        notes.append(f"{len(unreachable)} fightable within {args.acquire_range} but UNREACHABLE - "
                     f"no path to any of them from here, nearest {nearest_of(unreachable)}")
    avoided_near = len(t.within(t.avoided, args.acquire_range))
    if avoided_near:
        notes.append(f"{avoided_near} avoided creature(s) within {args.acquire_range}")
    out_of_reach = [m for m in t.threatening
                    if not is_avoided(m, st.avoid, st.include)
                    and m not in in_range and not st.blacklisted(m.serial, t.now)]
    if out_of_reach:
        notes.append(f"{len(out_of_reach)} fightable in view but beyond acquire range, nearest "
                     f"{nearest_of(out_of_reach)}")
    if unreachable:
        verdict = ("handing off: this spawn cannot be walked to from here - find another way in "
                   "(a gate, a different side) or pick different ground")
    else:
        verdict = "handing off to pick new ground"
    st.finish = (f"nothing left to fight within {args.acquire_range} tiles at {t.me} "
                 f"(hp {t.hp_pct:.0f}%"
                 + (", " + "; ".join(notes) if notes else ", nothing in view at all")
                 + f") - {verdict}", 1)


def lost_target(uo: Client, args, t: Threats, st: Brain, target) -> None:
    """Unlocatable: look for its corpse right away, and give up after `--lost-after`."""
    serial = target.serial
    if st.lost_since is None:
        st.lost_since = t.now
    corpse = (find_corpse_near(uo, st.last_pos, args.corpse_search_radius,
                               st.looter.done | st.looter.unreachable)
              if st.last_pos else None)
    if corpse is not None:
        uo.log(f"{target.name or serial} confirmed dead - corpse {corpse.serial}")
        st.looter.enqueue(corpse)
        st.current = st.lost_since = None
        st.mover.clear()
    elif t.now - st.lost_since > args.lost_after:
        if st.last_pos is None:
            # Never located once since adoption: a stale lastAttack (a war-mode double-click on a
            # vendor, a target from before a relog), not something that fled. Re-adopting it every
            # 30s kept resetting the empty timer, so a hunt with nothing around never handed off.
            uo.log(f"{serial} never seen since adoption - stale lastAttack, ignoring it for good")
            st.blacklist[serial] = float("inf")
            st.current = None
        else:
            uo.log(f"{serial} unlocatable for {args.lost_after:.0f}s - treating as gone (fled), "
                   f"ignoring it for {TARGET_BLACKLIST_SECS:.0f}s")
            st.drop_target(serial, t.now)


def pursue(uo: Client, args, t: Threats, st: Brain, target) -> None:
    serial = target.serial
    st.empty_since = None

    if serial != st.current:
        st.current, st.lost_since, st.last_pos, st.pursuit_nopaths = serial, None, None, 0
        uo.log(f"target is now {serial} ({target.name or 'unresolved'})")

    where = locate(uo, serial, t.me)
    if where is None:
        lost_target(uo, args, t, st, target)
        return

    st.last_pos = st.anchor = where
    if chebyshev(t.me, where) > args.range:
        if uo.nav.status == "nopath":
            st.pursuit_nopaths += 1
            st.mover.clear()
            if st.pursuit_nopaths >= args.nopath_strikes:
                uo.log(f"{serial} unreachable after {st.pursuit_nopaths} nopaths - "
                       f"ignoring it for {UNREACHABLE_BLACKLIST_SECS:.0f}s")
                st.drop_unreachable(serial, where, t.now)
                return
        st.mover.to(pick_pursuit_point(t.me, where, 0,
                                       t.keepaway(args.avoid_range, args.avoid_idle_range)))
    else:
        st.mover.clear()

    if is_avoided(target, st.avoid, st.include):     # set by hand in the game window
        uo.log(f"{serial} ({target.name}) is avoid-listed - not attacking")
        return
    st.attack(uo, serial, t.now, args.attack_interval)


def do_kill(uo: Client, args, t: Threats, st: Brain) -> None:
    target = adopt_target(uo, t, st)
    if target is None:
        acquire(uo, args, t, st)
    else:
        pursue(uo, args, t, st, target)


HANDLERS = {KILL: do_kill, LOOT: do_loot, ESCAPE: do_escape}


# ---------------------------------------------------------------------------------------------
# Main loop
# ---------------------------------------------------------------------------------------------

def update_escape(uo: Client, args, t: Threats, st: Brain, state: str,
                  reason: Optional[str]) -> Optional[int]:
    """Start, upgrade or clear the latched escape. Returns an exit code on handoff."""
    if state == ESCAPE:
        e = st.escape
        if e is None:
            st.escape = Escape(reason, flee_distance_for(args, reason), t.now, t.me)
        elif reason == "hp" and e.reason != "hp":
            uo.log(f"escape (hp) - overriding the {e.reason} escape, running harder")
            e.reason = "hp"
            e.distance = max(flee_distance_for(args, "hp"), e.distance)
            e.started_at, e.start_pos = t.now, t.me
            e.holding, e.aim_point = False, None
        track_escaped_threat(t, st.escape)
        return None

    if st.escape is None:
        return None
    escaped, st.escape = st.escape, None
    if escaped.handoff:
        return hand_off(uo, args, t, escaped)
    uo.log(f"escape ({escaped.reason}) clear - resuming")
    return None


def enter_state(uo: Client, st: Brain, new_state: str) -> None:
    suffix = f" ({st.escape.reason})" if new_state == ESCAPE and st.escape else ""
    uo.log(f"{st.state} -> {new_state}{suffix}")
    if new_state == ESCAPE:
        st.current = st.lost_since = None
        st.mover.clear()
        if st.escape and st.escape.handoff and st.anchor:
            uo.log(f"dropping anchor {st.anchor} - an avoided creature is there")
            st.anchor = None
    elif new_state == LOOT:
        st.mover.stop()     # don't let a pursuit leg drag us back off the corpse
    else:
        st.mover.clear()
    st.state = new_state


def check_config(uo: Client, args) -> None:
    if args.flee_hp_pct >= args.resume_hp_pct:
        uo.log("warning: --flee-hp-pct is not below --resume-hp-pct - HP escapes will not settle")
    if not autoheal_running(uo):
        uo.log("warning: autoheal.py is NOT running - this script never bandages, so HP will not "
               "recover. An HP escape will stall and hand off after --hp-stall-secs")
    if args.swarm_flee_distance >= min(args.hp_flee_distance, args.aggro_flee_distance):
        uo.log(f"warning: --swarm-flee-distance ({args.swarm_flee_distance}) is not below the hp "
               f"({args.hp_flee_distance}) and avoid-aggro ({args.aggro_flee_distance}) "
               f"distances - a swarm break will cover as much ground as a real emergency")


# ---------------------------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------------------------

ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])

ap.add_argument("--range", type=int, default=1, help="weapon reach to close to and hold (default 1)")
ap.add_argument("--poll", type=float, default=0.4, help="seconds between iterations")
ap.add_argument("--acquire-range", type=int, default=10,
                help="tiles - how far to look for a new target when none is engaged")
ap.add_argument("--lost-after", type=float, default=6.0,
                help="seconds a target may stay unlocatable before it is treated as gone (fled)")
ap.add_argument("--attack-interval", type=float, default=1.0,
                help="minimum seconds between `attack` commands")

add_avoid_args(ap)
ap.add_argument("--avoid-range", type=int, default=5,
                help="tiles - berth for an avoided creature that IS aggro'd on us (default 5)")
ap.add_argument("--avoid-idle-range", type=int, default=2,
                help="tiles - berth for an avoided creature that is NOT fighting us: a thing not "
                     "to bump into, not a thing to flee (default 2)")
ap.add_argument("--aggro-range", type=int, default=12,
                help="tiles - a mobile in war mode within this counts as aggro'd on us")
ap.add_argument("--flee-distance", type=int, default=10,
                help="tiles to put between us and an idle avoided creature we strayed too close "
                     "to (default 10)")
ap.add_argument("--swarm-flee-distance", type=int, default=6,
                help="tiles to run to break up a swarm - the shortest escape on purpose: the aim "
                     "is to string the pack out, not to get away (default 6)")
ap.add_argument("--aggro-flee-distance", type=int, default=40,
                help="tiles to put between us and an avoided creature aggro'd on us - it follows, "
                     "so anything short is simply re-closed (default 40)")
ap.add_argument("--pursuit-range", type=int, default=10,
                help="tiles - while escaping, any threat within this means keep running")
ap.add_argument("--escape-escalation", type=int, default=15,
                help="tiles added to the flee target each time a pursuer we thought we had shed "
                     "comes back into view")
ap.add_argument("--escape-max-distance", type=int, default=100,
                help="hard ceiling on the escalated flee target")
ap.add_argument("--escape-max-seconds", type=float, default=120.0,
                help="hard ceiling on a single non-HP escape; past it the escape is given up and "
                     "suppressed for --escape-giveup seconds. HP escapes are exempt")
ap.add_argument("--escape-reaim", type=float, default=6.0,
                help="seconds to keep running to the same flee point before recomputing it")
ap.add_argument("--aggro-pursuit-range", type=int, default=19,
                help="tiles - the pursuit range for an avoid-aggro escape. Above 18 (the world "
                     "file's view) it means 'keep running while anything hostile is visible', "
                     "which is what actually sheds a pursuer (default 19)")

ap.add_argument("--flee-hp-pct", type=float, default=45.0,
                help="HP%% below which ESCAPE takes over unconditionally")
ap.add_argument("--hp-flee-distance", type=int, default=25,
                help="tiles of clearance to reach for an HP escape")
ap.add_argument("--resume-hp-pct", type=float, default=80.0,
                help="HP%% that must be reached before an HP escape can end (autoheal.py has to "
                     "be running, or it never does)")

ap.add_argument("--swarm-hp-pct", type=float, default=60.0,
                help="HP%% at/below which a swarm is worth running from; above it a swarm is just "
                     "a busy fight (default 60)")
ap.add_argument("--swarm-count", type=int, default=4,
                help="threatening mobiles within --swarm-range that constitute a swarm (default 4)")
ap.add_argument("--swarm-range", type=int, default=2, help="tiles, for --swarm-count")

ap.add_argument("--no-loot", dest="loot", action="store_false", default=True,
                help="fight only - never enter the loot state")
ap.add_argument("--loot-radius", type=int, default=12,
                help="tiles - how far one of our own corpses may be before it is treated as out "
                     "of view and given up on. Corpses we did not kill are never looted")
ap.add_argument("--corpse-search-radius", type=int, default=3,
                help="tiles from a dead target's last known position to look for its corpse")
ap.add_argument("--loot-reach", type=int, default=2,
                help="tiles - close to this before opening a corpse")
ap.add_argument("--loot-defend-range", type=int, default=1,
                help="tiles - attack (but never chase) an aggro'd threat this close while looting")
ap.add_argument("--loot-approach-timeout", type=float, default=10.0,
                help="seconds to reach a corpse before giving up on it")
ap.add_argument("--loot-corpse-timeout", type=float, default=45.0,
                help="seconds spent on one corpse before giving up on it")
ap.add_argument("--max-loot-depth", type=int, default=3,
                help="how deep to open nested containers (the corpse itself is depth 0)")

ap.add_argument("--escape-min-dwell", type=float, default=3.0,
                help="minimum seconds an escape runs before it may resume")
ap.add_argument("--escape-hysteresis", type=int, default=3,
                help="tiles added to --aggro-range when deciding an escape is over")
ap.add_argument("--escape-timeout", type=float, default=20.0,
                help="seconds in one escape before blocked directions are reset and it re-picks")
ap.add_argument("--escape-giveup", type=float, default=10.0,
                help="seconds to suppress a non-HP escape after it is given up - cornered or "
                     "out-lasted, so fight rather than freeze")
ap.add_argument("--handoff-berth", type=int, default=HANDOFF_BERTH,
                help="tiles of clearance the handoff asks the orchestrator to keep from the last "
                     "sighting on the way back. Deliberately small")
ap.add_argument("--avoid-memory-secs", type=float, default=90.0,
                help="how long a sighting of an avoided creature still counts when deciding "
                     "whether to walk back to an anchor")
ap.add_argument("--avoid-memory-range", type=int, default=15,
                help="tiles - an anchor this close to a remembered avoided creature is abandoned")
ap.add_argument("--escape-giveup-limit", type=int, default=2,
                help="how many times one run may give up an escape and turn back to fight before "
                     "handing off instead")
ap.add_argument("--hp-stall-secs", type=float, default=80.0,
                help="seconds an HP escape may go without HP improving before handing off; any "
                     "real HP gain restarts it")
ap.add_argument("--empty-exit-secs", type=float, default=0.0,
                help="optional grace: seconds the wanted list (fightable mobiles within "
                     "--acquire-range) may stay empty before handing off. Default 0 - hand off "
                     "the moment there is nothing left to fight and nothing engaged")
ap.add_argument("--threat-clear-secs", type=float, default=THREAT_CLEAR_SECS,
                help="seconds an avoided creature must stay OUT of the world file before its "
                     "escape counts as over; the character keeps running for the whole window")
ap.add_argument("--no-return", dest="return_to_fight", action="store_false", default=True,
                help="after an escape, stay where you fled to instead of walking back")
ap.add_argument("--nopath-strikes", type=int, default=2,
                help="pursuit nopaths before a target is blacklisted as unreachable - the first "
                     "attempt plus one retry, since each attempt costs the planner its full node "
                     "budget (~2.4s of game thread against a fenced spawn)")


@script(ap)
def main(uo, args):
    try:
        avoid, include = resolve_avoid_args(args)
    except ValueError as e:
        return uo.stop(str(e))

    mode = describe(avoid, include)
    uo.log(
        f"melee: reach {args.range}, acquire within {args.acquire_range}; "
        f"escape below {args.flee_hp_pct:.0f}% HP to {args.hp_flee_distance} tiles clear, "
        f"resume at {args.resume_hp_pct:.0f}%; "
        f"avoid-range {args.avoid_range} aggro'd / {args.avoid_idle_range} idle, "
        f"aggro-range {args.aggro_range}; "
        f"flee: swarm {args.swarm_flee_distance} < avoid-idle {args.flee_distance} "
        f"< avoid-aggro {args.aggro_flee_distance} / hp {args.hp_flee_distance} tiles; "
        f"swarm is {args.swarm_count}+ within {args.swarm_range} at or below "
        f"{args.swarm_hp_pct:.0f}%% HP"
        + (f"; {mode}" if mode else "")
        + (f"; looting our own kills, nested containers to depth {args.max_loot_depth}"
           if args.loot else "; not looting")
    )
    check_config(uo, args)

    st = Brain(mover=Mover(uo), looter=Looter(uo, args), avoid=avoid, include=include)

    for _ in uo.every(args.poll):
        t = survey(uo, args, st)
        for m in t.avoided:
            st.avoid_seen[m.serial] = (m.name or "an avoided creature", (m.x, m.y), t.now)
        update_wanted(uo, args, t, st)

        new_state, reason = decide_state(args, t, st)
        code = update_escape(uo, args, t, st, new_state, reason)
        if code is not None:
            return code
        if new_state != st.state:
            enter_state(uo, st, new_state)

        if args.verbose and st.throttle("report", 3.0, t.now):
            uo.log(f"[{st.state}] hp={t.hp_pct:.0f}% threats={len(t.threatening)} "
                   f"wanted={len(st.wanted)} aggro={len(t.aggro)} avoided={len(t.avoided)} "
                   f"corpses={len(t.corpses)} nearest={t.nearest_threat_distance()}")

        HANDLERS[st.state](uo, args, t, st)

        if st.finish is not None:     # only main ends the run
            reason, code = st.finish
            return uo.stop(reason, code=code)


if __name__ == "__main__":
    sys.exit(main())
