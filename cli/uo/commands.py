"""Sending commands to the client.

Two shapes, and the difference matters:

  send()  appends a line and returns. ~0.01ms. This is the normal way to act - the client's command
          worker is serial, so ordering is preserved, and for anything with a state footprint you
          confirm by watching the state file rather than by waiting here.

  call()  appends and then waits for that command's [CMD-END], returning the lines it printed. Only
          for the commands whose whole purpose is output - shop, gumps, tiles, path, los, container
          for something other than your own backpack. Costs a round trip (~6ms after the poll fix).

Never shells out to `navrey`: that spawns a .NET process, which measured 50-100ms - an order of
magnitude more than the work itself.
"""

from __future__ import annotations

import os
import re
import threading
import time
from typing import List, Optional

_STAMP = re.compile(r"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] ")


class CommandError(RuntimeError):
    pass


class Commands:
    def __init__(self, cmd_file: str, log_file: str):
        self.cmd_file = cmd_file
        self.log_file = log_file

        # The client runs one command at a time, so two concurrent call()s could not be told apart:
        # correlation is by echoing the command text and there are no request ids. Serialising here
        # keeps a threaded script honest.
        self._lock = threading.Lock()

    def send(self, line: str) -> None:
        """Queue a command. Returns immediately; does not wait for it to run."""
        with open(self.cmd_file, "a") as handle:
            handle.write(line.rstrip("\n") + "\n")

    def call(self, line: str, timeout: float = 10.0, poll: float = 0.002) -> List[str]:
        """
        Run a command and return the lines it printed.

        Raises CommandError on timeout or on an `error:` completion.

        The returned lines can include unrelated narration: packets arrive on their own schedule and
        the client deliberately does not attribute them to a command, so anything that happened to
        land inside the brackets comes along. Filter by what you expect, not by position.
        """
        line = line.strip()

        with self._lock:
            try:
                start = os.path.getsize(self.log_file)
            except OSError:
                start = 0

            self.send(line)

            deadline = time.monotonic() + timeout
            marker = f"[CMD-END] {line} "
            echo = f"[CMD] {line}"
            seen: List[str] = []
            offset = start
            carry = ""

            while time.monotonic() < deadline:
                try:
                    size = os.path.getsize(self.log_file)
                except OSError:
                    time.sleep(poll)
                    continue

                if size > offset:
                    with open(self.log_file, "rb") as handle:
                        handle.seek(offset)
                        chunk = handle.read().decode("utf-8", "replace")
                        offset = handle.tell()

                    chunk = carry + chunk
                    parts = chunk.split("\n")
                    carry = parts.pop()

                    for raw in parts:
                        # Anchor on the start of the line after the fixed timestamp. A substring
                        # search would let server-authored text end the wait early - `say [CMD-END]
                        # pos ok` is echoed into this same log, as is arbitrary gump and item text.
                        text = _STAMP.sub("", raw).rstrip()

                        if text.startswith(marker):
                            outcome = text[len(marker):]

                            if outcome.startswith("error:"):
                                raise CommandError(f"{line}: {outcome[6:].strip()}")

                            return seen

                        if text == echo or not text:
                            continue

                        seen.append(text)

                time.sleep(poll)

            raise CommandError(f"{line}: timed out after {timeout}s")
