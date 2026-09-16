"""Typed events decoded from the client's log.

Most of what a script needs is in the state and world files, but some things only ever arrive as a
line of narration: a bandage finishing, speech, a container opening, a death. This is the one place
that knows their wording, so a script never carries its own regex.

That mattered: two copies of the bandage patterns existed in the skills and had drifted apart. One
matched 5 of the 9 completion messages, so a poison cure never tripped its early break and the
caller burned its full 15-second timeout every time. There is one list now.
"""

from __future__ import annotations

import os
import re
import time
from enum import Enum
from typing import Iterator, List, Optional

# The client stamps every line "[HH:mm:ss.fff] " before the content.
_STAMP = re.compile(r"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] ")
_STAMP_AT = re.compile(r"^\[(\d{2}):(\d{2}):(\d{2})\.(\d{3})\]")


class Event(str, Enum):
    ENTERED_WORLD = "entered_world"
    DIED = "died"
    BANDAGE_DONE = "bandage_done"
    BANDAGE_FAILED = "bandage_failed"
    CONTAINER_OPENED = "container_opened"
    CONTAINER_CONTENTS = "container_contents"
    SHOP_READY = "shop_ready"
    NAV_DONE = "nav_done"
    SWING = "swing"
    DAMAGE = "damage"
    SPEECH = "speech"
    SYSTEM = "system"
    TARGET_REQUEST = "target_request"
    WALK_DENIED = "walk_denied"
    FROZEN = "frozen"
    OTHER = "other"


# A bandage resolves with exactly one of these. Five successes, four failures.
_BANDAGE_DONE = (
    "You bandage your wounds",
    "You successfully heal",
    "You have cured the target of all poisons",
    "You have been cured of all poisons",
    "You bind the wound",
)

_BANDAGE_FAILED = (
    "You apply the bandages, but they barely help",
    "You fail to apply the bandages properly",
    "You have failed to cure your target!",
    "You did not stay close enough to heal your target",
    # Not a failure to bandage so much as nothing to bandage, but it ends the attempt the same way
    # and the bandage is not consumed. Observed live; it was missing from every earlier copy of this
    # list, so a caller at full health waited out its whole timeout instead.
    "That being is not damaged",
)

# Deliberately neither: "your fingers slip" means the attempt is still running (you took a hit
# mid-bandage, which extends it). Treating it as failure starts a second bandage over the first.
_BANDAGE_PENDING = ("fingers slip",)


class LogEvent:
    """
    One decoded line. `text` has the client's timestamp stripped; `raw` still carries it, which is
    the only record of *when* the event happened rather than when a script got round to reading it.
    """

    __slots__ = ("kind", "text", "raw")

    def __init__(self, kind: Event, text: str, raw: str = None):
        self.kind = kind
        self.text = text
        self.raw = text if raw is None else raw

    @property
    def at(self) -> Optional[float]:
        """Epoch seconds from the line's own stamp, or None if it has none. See stamp_time."""
        return stamp_time(self.raw)

    def __repr__(self):
        return f"<{self.kind.value}: {self.text[:60]}>"


def stamp_time(raw: str, now: Optional[float] = None) -> Optional[float]:
    """
    Epoch seconds for a log line, read from its own `[HH:MM:SS.mmm]` stamp.

    The stamp carries no date, so it is resolved against today. A line stamped ahead of now by more
    than a minute is read as yesterday's: that is a log written at 23:59 and read at 00:01, not a
    clock that runs backwards. The one-minute tolerance absorbs sub-second skew without turning a
    just-written line into a 24-hour-old one.
    """
    match = _STAMP_AT.match(raw)

    if match is None:
        return None

    now = time.time() if now is None else now

    # Local midnight, derived from the wall clock rather than from UTC arithmetic - the stamp is
    # local time, so a UTC-based day boundary would be off by the offset in most of the world.
    local = time.localtime(now)
    midnight = now - (local.tm_hour * 3600 + local.tm_min * 60 + local.tm_sec) - (now % 1)

    hour, minute, second, millis = (int(g) for g in match.groups())
    stamped = midnight + hour * 3600 + minute * 60 + second + millis / 1000

    if stamped > now + 60:
        stamped -= 86400

    return stamped


def classify(line: str) -> LogEvent:
    """One log line to a typed event. `line` should already have the timestamp stripped."""
    if "*** Entered the world!" in line:
        return LogEvent(Event.ENTERED_WORLD, line, line)
    if "[DEATH]" in line or "You have died" in line:
        return LogEvent(Event.DIED, line, line)

    for pattern in _BANDAGE_PENDING:
        if pattern in line:
            return LogEvent(Event.OTHER, line, line)
    for pattern in _BANDAGE_DONE:
        if pattern in line:
            return LogEvent(Event.BANDAGE_DONE, line, line)
    for pattern in _BANDAGE_FAILED:
        if pattern in line:
            return LogEvent(Event.BANDAGE_FAILED, line, line)

    if line.startswith("[TARGET]"):
        return LogEvent(Event.TARGET_REQUEST, line, line)
    if line.startswith("[CONTAINER] Opened"):
        return LogEvent(Event.CONTAINER_OPENED, line, line)
    if line.startswith("[CONTAINER] Contents"):
        return LogEvent(Event.CONTAINER_CONTENTS, line, line)
    if line.startswith("[SHOP]"):
        return LogEvent(Event.SHOP_READY, line, line)
    if line.startswith("[NAV]"):
        return LogEvent(Event.NAV_DONE, line, line)
    if line.startswith("Swing:"):
        return LogEvent(Event.SWING, line, line)
    if line.startswith("Damage:"):
        return LogEvent(Event.DAMAGE, line, line)
    if line.startswith("[SYSTEM]"):
        return LogEvent(Event.SYSTEM, line, line)
    if "Walk denied" in line:
        return LogEvent(Event.WALK_DENIED, line, line)
    if "frozen and cannot move" in line:
        return LogEvent(Event.FROZEN, line, line)
    # `[SAY]`/`[GUILD]`/`[ALLIANCE]`/`[PARTY]` come from the packet's own MessageType byte, which
    # the narrator used to discard for ordinary speech - it printed a bare `<speaker>: <text>`,
    # indistinguishable from `Swing: ...` or an object label, so every reader had to guess.
    # `[LABEL]` is kept here for compatibility, though an object's name tag is not really speech.
    if line.startswith(("[SAY]", "[GUILD]", "[ALLIANCE]", "[PARTY]",
                        "[LABEL]", "[YELL]", "[whisper]")):
        return LogEvent(Event.SPEECH, line, line)

    return LogEvent(Event.OTHER, line, line)


class LogTail:
    """
    Follows the log from wherever it currently ends.

    Pull-based on purpose: no background thread means no lifecycle to manage, no CPU burned while a
    script is doing something else, and the polling interval is the caller's to choose.
    """

    def __init__(self, path: str, from_start: bool = False):
        self.path = path
        self._offset = 0
        self._carry = ""

        if not from_start:
            try:
                self._offset = os.path.getsize(path)
            except OSError:
                self._offset = 0

    def poll(self) -> List[LogEvent]:
        """Every event since the last call. Empty when nothing new."""
        try:
            size = os.path.getsize(self.path)
        except OSError:
            return []

        # The client truncates the log on startup, so the file getting shorter means a restart,
        # not corruption - start over rather than seeking past the end forever.
        if size < self._offset:
            self._offset = 0
            self._carry = ""

        if size == self._offset:
            return []

        try:
            with open(self.path, "rb") as handle:
                handle.seek(self._offset)
                chunk = handle.read().decode("utf-8", "replace")
                self._offset = handle.tell()
        except OSError:
            return []

        chunk = self._carry + chunk
        lines = chunk.split("\n")

        # A trailing partial line is held back until the rest of it arrives.
        self._carry = lines.pop()

        out = []

        for raw in lines:
            line = _STAMP.sub("", raw).rstrip()

            if line:
                event = classify(line)
                event.raw = raw.rstrip()

                out.append(event)

        return out

    def drain(self) -> None:
        """Discard anything pending, so a later wait only sees what happens from now on."""
        self.poll()
