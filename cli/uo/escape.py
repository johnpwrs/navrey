"""Breaking off from something we will not fight, and handing the decision back.

Both movement reflexes need the same three-part sequence, and they had drifted into two
implementations of it - which is how one of them ended up missing the part that keeps the
character alive:

    1. run,                      until distance is actually gained, escalating if that fails;
    2. confirm it is over,       by watching for a while *after* the screen goes quiet;
    3. hand back,                naming what it was, where it was last seen, and where we were
                                 headed - so the next walk can go around it.

Step 2 is the one that is easy to leave out and fatal to leave out. The world file carries 18
tiles, so a pursuer that merely stepped past the edge of view reads exactly like one that broke
off. Ending on that silence hands a relentless chaser a stationary target, because handing back
stops every mover and the next actor able to move the character is the orchestrator at model
latency - about thirty seconds, measured. Two characters-worth of evidence:

  - `combat_movement_melee.py` ended a spectre escape the instant the mobile left view, handed
    off, every script stood down, and the spectre walked back in and killed a stationary character
    at 43% HP.
  - `safe_goto.py` left a revenant 20 tiles behind and exited; in the 13 seconds before anyone
    reacted it covered all 20 tiles, and the character went 82 -> 4 HP.

So the timing rules and the handoff wording live here once, and both callers use them. What does
*not* live here is how each script actually moves - melee aims through its `Mover` and its own
state machine, `safe_goto` through `retreat` - because those are genuinely different
mechanisms, and pretending otherwise would be a worse abstraction than the duplication it removed.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional, Tuple

# Seconds an avoided creature must stay OUT of the world file before its escape counts as over.
# Measured from the last sighting, never from the start of the escape: a pursuer flickering at the
# edge of view resets the clock rather than accruing credit toward it.
THREAT_CLEAR_SECS = 15.0

# Tiles of clearance to ask the orchestrator to keep from the last sighting on the way back.
# Deliberately small. The point is not to re-aggro, not to cross the map - an earlier wording said
# "wide berth" and got an 80-tile trek that cost more grinding time than the creature ever did.
HANDOFF_BERTH = 10


@dataclass
class ThreatMemory:
    """The creature an escape is running from, and the freshest tile we saw it on.

    Locked to one serial. The first avoided creature seen during an escape is the one that drove
    us off the ground, and the one the return trip has to route around. Without the lock this
    tracked whichever avoided creature happened to be nearest *now*, and a long flight past
    unrelated spawn overwrote the answer: an escape triggered by a creature at (1371, 1453) handed
    off naming a shade at (1425, 1544) - a different mobile, 90 tiles from the hunting ground, that
    we had merely run past on the way out.

    Deliberately not persisted anywhere. The creature wanders, so the coordinate is only good in
    the moment it is handed over; a stale danger point is worse than none because it looks
    authoritative.
    """

    serial: Optional[str] = None
    name: Optional[str] = None
    pos: Optional[Tuple[int, int]] = None
    seen_at: float = 0.0

    def saw(self, serial: str, name: Optional[str], pos: Tuple[int, int], now: float) -> None:
        """Record a sighting, locking onto the first serial offered and ignoring the rest."""
        if self.serial is None:
            self.serial = serial
        if serial != self.serial:
            return
        self.name, self.pos, self.seen_at = name or self.name, pos, now

    def confirmed_clear(self, now: float, window: float = THREAT_CLEAR_SECS) -> bool:
        """Has it stayed out of view long enough to call the escape over?"""
        return not self.seen_at or (now - self.seen_at) >= window

    def unseen_for(self, now: float) -> float:
        return (now - self.seen_at) if self.seen_at else float("inf")

    @property
    def label(self) -> str:
        return self.name or "an avoided creature"

    @property
    def where(self) -> str:
        return str(self.pos) if self.pos else "unknown"


def escalate(clearance: int, step: int, cap: int) -> int:
    """Widen a clearance target that already failed to shed the pursuer.

    Repeating a distance that did not work just repeats the failure - a chaser matching our speed
    is only ever beaten by out-lasting it, and without escalation the escape settles into a stable
    orbit at whatever range the first attempt reached.
    """
    return min(clearance + step, cap)


def handoff_message(threat: ThreatMemory, me: Tuple[int, int],
                    berth: int = HANDOFF_BERTH) -> str:
    """The one wording both scripts hand back, carrying the two coordinates a route needs."""
    return (f"HANDOFF: lost {threat.label}. we are at {me}; it was last seen at {threat.where}. "
            f"Pick up where we left off, keeping at least {berth} tiles from {threat.where} - "
            f"that clearance is the whole detour, do not take a long way round, it costs more "
            f"grinding time than the creature does. Just do not retrace the exact line we ran. "
            f"Its position is a last sighting, not a fixture: re-scan on arrival.")


def handoff_summary(threat: ThreatMemory, berth: int = HANDOFF_BERTH) -> str:
    """The one-line exit reason - the last line of the run, which is what gets read first."""
    return (f"escaped {threat.label} (last seen {threat.where}) - handing off; keep {berth} tiles "
            f"clear of it on the way back")
