"""When combat last happened, from the log.

Answers "am I in a fight?" and "how long since anything hit me?" - the questions behind a decision
to disengage, to stop healing, to conclude a target is gone. Neither is in the state file: HP tells
you that you *were* hurt, never when, and a target's `exists` says nothing about whether the two of
you are still trading blows.

The source is narration, because that is where the packets land. The client already writes a line
for every swing (0x2F) and every damage packet (0x0B):

    Swing: Auto -> a wild boar: HIT
    Swing: a wild boar -> Auto: MISS
    Damage: Auto takes 7

Two details make this trustworthy rather than approximate.

**Times come from the line's own stamp, not from when a script read it.** A loop polling every
second would otherwise report every event as up to a second stale, and the whole point is measuring
a gap.

**A fresh script backfills from the log it has not read.** LogTail follows from wherever the file
currently ends, which is right for waiting on something about to happen and wrong here: a script
launched mid-fight would report "no combat ever" until the next swing, and a script restarted
during a lull would forget the lull. So the first read scans backwards through the tail of the file
for what already happened. This is what lets `since_combat` mean the same thing to every script
regardless of when it started - which a per-process tail alone could not do.

Attribution is by name, since that is what the narration carries. Two consequences worth knowing:
a mobile sharing the player's name would be miscounted, and a swing involving a mobile the client
cannot name shows as `?` and still counts as combat, just not attributed.

`last_attacker_name` is the name off the most recent `Swing: X -> us` line - only a name, never a
serial, since that is all a swing line carries. Resolving it to a serial (`uo.mobiles(named=...,
within=...)`) and deciding whether to `attack` it is a script's job, not this module's - this stays
mechanism, the same as everything else here.

The HIT/MISS on a swing line is ignored on purpose - see `since_damage_dealt`. It is read from a
byte the protocol defines as constant, so it says MISS for every swing ever narrated.
"""

from __future__ import annotations

import os
import re
import time
from typing import Callable, Optional

from .events import Event, LogTail, stamp_time

# How far back the first read looks. 256KB of narration is far more than any plausible lull.
BACKFILL_BYTES = 256 * 1024

# The trailing ": HIT"/": MISS" is optional only so an older log still parses. Current narration
# carries no outcome, because packet 0x2F has none to carry - see since_damage_dealt.
_SWING = re.compile(r"^Swing: (?P<attacker>.+?) -> (?P<defender>.+?)(?::\s*(?:HIT|MISS))?$")
_DAMAGE = re.compile(r"^Damage: (?P<who>.+?) takes (?P<amount>\d+)$")


class Combat:
    """
    The most recent moment of each kind of combat activity, as epoch seconds (None if never seen).

    Compare against `time.time()`, or use the `since_*` helpers, which read better in a condition
    and return `inf` rather than None so a comparison never has to guard for it.
    """

    __slots__ = (
        "last_swing_by_us", "last_swing_at_us",
        "last_damage_taken", "last_damage_dealt", "last_damage_amount",
        "last_attacker_name",
    )

    def __init__(self):
        self.last_swing_by_us: Optional[float] = None
        self.last_swing_at_us: Optional[float] = None
        self.last_damage_taken: Optional[float] = None
        self.last_damage_dealt: Optional[float] = None
        self.last_damage_amount: int = 0
        self.last_attacker_name: Optional[str] = None

    @property
    def last_combat_time(self) -> Optional[float]:
        """The most recent of anything above: a swing either way, or damage either way."""
        seen = [t for t in (self.last_swing_by_us, self.last_swing_at_us,
                            self.last_damage_taken, self.last_damage_dealt) if t is not None]

        return max(seen) if seen else None

    @property
    def since_combat(self) -> float:
        return _since(self.last_combat_time)

    @property
    def since_damage_taken(self) -> float:
        return _since(self.last_damage_taken)

    @property
    def since_attacked(self) -> float:
        """Seconds since something swung at us, hit or miss."""
        return _since(self.last_swing_at_us)

    @property
    def since_swing(self) -> float:
        """Seconds since *we* swung. A growing gap here is the sign of a fight that has stalled."""
        return _since(self.last_swing_by_us)

    @property
    def since_damage_dealt(self) -> float:
        """
        Seconds since we hurt something - the only trustworthy evidence that swings are connecting.

        Deliberately not derived from the HIT/MISS in the swing line. That word comes from the
        second byte of packet 0x2F, which the protocol defines as a constant 0x00, so every swing
        narrates as MISS whatever happened; measured live, four swings all read MISS while three of
        them dealt damage and took the target from 25 HP to 3. Damage lines are the ground truth.
        """
        return _since(self.last_damage_dealt)

    def __repr__(self):
        return (f"<Combat since_combat={self.since_combat:.1f}s "
                f"since_damage={self.since_damage_taken:.1f}s>")


def _since(when: Optional[float]) -> float:
    return float("inf") if when is None else max(0.0, time.time() - when)


class CombatWatch:
    """
    Keeps a Combat up to date from the log. Pull-based, like everything else here.

    It owns its own LogTail rather than sharing the client's: the client's is consumed by
    `wait_for`, and two readers taking lines off one tail would each see half of them.
    """

    def __init__(self, log_path: str, player_name: Callable[[], str]):
        self.path = log_path
        self.state = Combat()

        self._name = player_name
        self._tail = LogTail(log_path)
        self._backfilled = False

    def update(self) -> Combat:
        name = self._name() or ""

        if not self._backfilled and name:
            # Only once we know who we are - attribution is by name, and backfilling without it
            # would file every swing as unattributed and then never look again.
            self._backfill(name)
            self._backfilled = True

        for event in self._tail.poll():
            if event.kind in (Event.SWING, Event.DAMAGE):
                self._record(event.text, event.at, name)

        return self.state

    def _record(self, text: str, at: Optional[float], name: str) -> None:
        if at is None:
            at = time.time()

        swing = _SWING.match(text)

        if swing:
            if swing["attacker"] == name:
                self.state.last_swing_by_us = _later(self.state.last_swing_by_us, at)
            elif swing["defender"] == name:
                if at >= (self.state.last_swing_at_us or 0):
                    self.state.last_attacker_name = swing["attacker"]

                self.state.last_swing_at_us = _later(self.state.last_swing_at_us, at)

            return

        damage = _DAMAGE.match(text)

        if damage:
            if damage["who"] == name:
                if at != self.state.last_damage_taken:
                    self.state.last_damage_amount = int(damage["amount"])

                self.state.last_damage_taken = _later(self.state.last_damage_taken, at)
            else:
                self.state.last_damage_dealt = _later(self.state.last_damage_dealt, at)

    def _backfill(self, name: str) -> None:
        """Read the tail of the log for combat that happened before this script started."""
        try:
            size = os.path.getsize(self.path)

            with open(self.path, "rb") as handle:
                handle.seek(max(0, size - BACKFILL_BYTES))
                chunk = handle.read().decode("utf-8", "replace")
        except OSError:
            return

        lines = chunk.split("\n")

        # A partial first line if the seek landed mid-line; it cannot be parsed safely.
        if size > BACKFILL_BYTES and lines:
            lines.pop(0)

        for raw in lines:
            text = raw[15:].rstrip() if raw[:1] == "[" else raw.rstrip()

            if text.startswith("Swing:") or text.startswith("Damage:"):
                self._record(text, stamp_time(raw), name)


def _later(current: Optional[float], candidate: float) -> float:
    """
    Never let a timestamp go backwards.

    The log is chronological, but a backfill and a live poll can overlap, and a stamp with no date
    is resolved against the current day - so this is what keeps a boundary case from reporting
    combat as older than something already recorded.
    """
    return candidate if current is None else max(current, candidate)
