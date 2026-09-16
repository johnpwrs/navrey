"""Cached readers for the client's JSON files.

The client rewrites /tmp/cuostate.json and /tmp/cuoworld.json whenever anything changes, and at
least every 250ms. Reading them is what replaces asking the client a question: a full parse of the
state file costs ~0.06ms against ~6ms for a command round trip, so state is effectively free and
should be read freely.

Two details make it cheap enough to poll in a tight loop:

  Reads are gated on mtime. os.stat costs ~0.003ms; the parse only happens when the file actually
  changed. A 20Hz loop therefore costs microseconds per second, not milliseconds.

  The client writes via a rename, which is atomic, so a read never sees a half-written document.
  A parse failure is still caught and the last good value kept - a torn read should be impossible,
  and if one ever happens it is not worth crashing a script over.
"""

from __future__ import annotations

import json
import os
import time


class JsonFile:
    """One JSON file, re-parsed only when it changes on disk."""

    def __init__(self, path: str):
        self.path = path
        self._key = None
        self._data: dict = {}
        self._frozen = False

    def read(self) -> dict:
        if self._frozen:
            return self._data

        try:
            st = os.stat(self.path)
        except OSError:
            return self._data

        key = (st.st_mtime_ns, st.st_size)

        if key != self._key:
            try:
                with open(self.path, "rb") as handle:
                    self._data = json.loads(handle.read())
                self._key = key
            except (OSError, ValueError):
                # Keep the last good document. The atomic rename should make this unreachable.
                pass

        return self._data

    def freeze(self) -> None:
        """Pin the current contents so repeated reads cannot straddle a rewrite."""
        self.read()
        self._frozen = True

    def thaw(self) -> None:
        self._frozen = False

    @property
    def age_ms(self) -> float:
        """
        Milliseconds since the client last proved it is alive. The authoritative liveness test.

        Prefers `heartbeatAtMs`, written by the client's own writer thread, over `updatedAtMs`,
        which is stamped when the game thread built the snapshot. The two differ exactly when the
        game thread stalls - a long frame, a texture upload, a GC pause - and treating a stalled
        game thread as a dead client is what used to kill autoheal and the speech listener mid-fight
        while the client was perfectly alive. Falls back to `updatedAtMs` for a client built before
        the heartbeat existed.
        """
        doc = self.read()
        stamp = doc.get("heartbeatAtMs", doc.get("updatedAtMs"))

        if stamp is None:
            return float("inf")

        return time.time() * 1000 - stamp

    @property
    def snapshot_age_ms(self) -> float:
        """Milliseconds since the *data* was last rebuilt, as opposed to since the process last
        proved it is alive. Use this to ask "is what I am reading current?", never to ask "is the
        client still there?"."""
        stamp = self.read().get("updatedAtMs")

        if stamp is None:
            return float("inf")

        return time.time() * 1000 - stamp

    def is_live(self, max_age_ms: float = 5000) -> bool:
        """
        Whether a client is actually running behind this file.

        Deliberately not `inGame`: a clean exit or a `kill` leaves `inGame: false`, but nothing can
        run on `kill -9`, a crash, or a lost machine - so a file still claiming `inGame: true` may
        belong to a client that is gone. Only the timestamp can tell you.

        The tolerance is wider than the 250ms heartbeat cadence because a heartbeat can still be
        missed under load, but it no longer has to absorb whole game-thread stalls: the heartbeat
        is written by a thread that does not touch the game loop, so it keeps ticking through a
        stalled frame. 5s is ~20 missed heartbeats, which no running client produces.
        """
        return self.age_ms <= max_age_ms
