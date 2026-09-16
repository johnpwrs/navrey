"""One instance of a looping script at a time.

Two copies of the same loop running against one character is not a small problem, and it does not
announce itself. Two autoheal.py both see the same low HP, both start a bandage, and the second
`use` lands on a target cursor the first one opened - one bandage is consumed for nothing and the
cursor is left dangling. Two combat_movement.py each re-issue `goto` at the target every ~0.4s,
and since a new walk cancels the running one (last-wins, by design), the character stutters in
place instead of closing. Neither shows up as an error: the log looks like a script that is simply
doing badly.

The failure is easy to cause. A background script has no window, `ps` output is noisy, and after a
compaction or a resumed session the orchestrator has no memory of what it launched - so
"relaunching" a healer that never stopped is the normal accident, not an exotic one.

So the loop claims a lock at startup and refuses to run if something already holds it. This mirrors
AgentLock on the C# side, which stops two clients owning one command file, and for the same reason:
refuse up front, because the alternative failure is a mystery.

    from uo import Client, single_instance, AlreadyRunning

    uo = Client()

    try:
        lock = single_instance("autoheal", cmd_file=uo.commands.cmd_file)
    except AlreadyRunning as exc:
        print(exc)          # "autoheal is already running (pid 4821)"
        return 1

flock, not a pid file. The lock lives on an open file descriptor, so the kernel drops it when the
process ends - including on `kill -9`, a crash, or a terminal that went away. There is no stale
lock to detect, no liveness check to get wrong, and no window where a script refuses to start
because its predecessor died badly. The pid written into the file body is for the error message
only; the flock is the truth.

The file is never unlinked. Deleting a locked file races - another process can be holding the same
path open, and the next acquirer would lock an unlinked inode that nobody else can see. A leftover
unlocked file costs nothing and means exactly "nothing is running".

Locks are scoped to a command file, so two clients driven with -cmdfile do not block each other's
scripts.
"""

from __future__ import annotations

import errno
import fcntl
import glob
import os
import signal
import time
from typing import Dict, Optional

DEFAULT_CMD = "/tmp/cuocmd"


class AlreadyRunning(RuntimeError):
    """Raised when another live process already holds the named lock."""

    def __init__(self, name: str, pid: Optional[int], path: str):
        self.name = name
        self.pid = pid
        self.path = path

        who = f"pid {pid}" if pid else "another process"

        super().__init__(
            f"{name} is already running ({who}). "
            f"Stop it first (kill {pid or '<pid>'}), or pass a different --name. Lock: {path}")


def lock_path(name: str, cmd_file: str = DEFAULT_CMD) -> str:
    return f"{cmd_file}.{_slug(name)}.lock"


def _slug(name: str) -> str:
    return "".join(c if c.isalnum() or c in "-_" else "_" for c in name) or "script"


class InstanceLock:
    """
    A held lock. Keep the object alive for as long as the script runs.

    Dropping the last reference closes the descriptor and releases the lock, so binding it to a
    local that goes out of scope silently unlocks a still-running loop. The module keeps its own
    reference for exactly that reason; `release()` is the way to give one up early.
    """

    def __init__(self, name: str, path: str, fd: int):
        self.name = name
        self.path = path
        self._fd = fd

    @property
    def held(self) -> bool:
        return self._fd is not None

    def release(self) -> None:
        if self._fd is None:
            return

        fd, self._fd = self._fd, None

        _HELD.pop(self.path, None)

        try:
            os.close(fd)        # closing drops the flock
        except OSError:
            pass

    def __enter__(self) -> "InstanceLock":
        return self

    def __exit__(self, *exc) -> None:
        self.release()

    def __repr__(self) -> str:
        return f"<InstanceLock {self.name} {'held' if self.held else 'released'}>"


# Holds a reference so a lock cannot be garbage-collected out from under a running loop.
_HELD: Dict[str, InstanceLock] = {}


def single_instance(
    name: str,
    cmd_file: str = DEFAULT_CMD,
    takeover: bool = False,
    wait: float = 5.0,
) -> InstanceLock:
    """
    Claim `name` for this process, or raise AlreadyRunning.

    Call it once, early - before the loop and before anything with a side effect, so a duplicate
    exits without having touched the character.

    takeover=True asks the holder to stop (SIGTERM) and waits up to `wait` seconds for its lock to
    drop, which is the "restart the healer" case: relaunching is what the caller meant, and making
    them find the pid first is friction with no safety in it. It still refuses to evict a process
    it cannot signal.
    """
    path = lock_path(name, cmd_file)

    if path in _HELD:
        raise AlreadyRunning(name, os.getpid(), path)

    fd = os.open(path, os.O_RDWR | os.O_CREAT, 0o644)

    try:
        if not _try_lock(fd):
            other = _read_pid(fd)

            if not takeover:
                raise AlreadyRunning(name, other, path)

            _evict(name, other, path)

            deadline = time.monotonic() + wait

            while not _try_lock(fd):
                if time.monotonic() >= deadline:
                    raise AlreadyRunning(name, other, path)

                time.sleep(0.05)
    except BaseException:
        os.close(fd)
        raise

    # Only now is the lock ours: record who, for the next process's error message.
    #
    # The lseek is not decoration. A failed first attempt reads the previous holder's pid, which
    # leaves the offset past it, and ftruncate does not rewind - so without this the write lands at
    # that offset and the file is NUL-padded up to it. That is not a cosmetic defect: NUL is not
    # whitespace, so str.split() hands back one unparseable token and the pid reads as unknown.
    os.ftruncate(fd, 0)
    os.lseek(fd, 0, os.SEEK_SET)
    os.write(fd, f"{os.getpid()} {name} {int(time.time())}\n".encode())
    os.fsync(fd)

    lock = InstanceLock(name, path, fd)
    _HELD[path] = lock

    return lock


def holder(name: str, cmd_file: str = DEFAULT_CMD) -> Optional[int]:
    """
    The pid running `name`, or None if nothing is.

    Tests the flock rather than trusting the file's contents, so a lock file left behind by a
    process that died reads as free - which it is.

    Two caveats, neither of which affects the cross-process answer this exists to give:

      The probe takes the lock for the microsecond it takes to test it, so a `single_instance` call
      landing in that exact window would be refused. Diagnostics are agent-paced and startups are
      rare; this is not worth a second lock file to arbitrate.

      On BSD/macOS a second flock from the *same* process does not conflict with the first, so a
      holder probing its own lock would see it free. _HELD is consulted first for that reason.
    """
    return _probe(lock_path(name, cmd_file))[1]


def is_running(name: str, cmd_file: str = DEFAULT_CMD) -> bool:
    """
    Whether anything holds `name`.

    Separate from holder() on purpose: "held" and "held by pid N" are different questions, and
    conflating them once already reported a live script as absent when its pid could not be parsed.
    Ask this when you want the yes/no; ask holder() when you want someone to kill.
    """
    return _probe(lock_path(name, cmd_file))[0]


def running(cmd_file: str = DEFAULT_CMD) -> Dict[str, Optional[int]]:
    """
    Every script currently holding a lock for this client, as {name: pid}.

    A pid of None means the lock is held but the file does not say by whom - still running, just
    unidentified. Never omitted on that basis.
    """
    prefix, suffix = f"{cmd_file}.", ".lock"
    found: Dict[str, Optional[int]] = {}

    for path in sorted(glob.glob(f"{prefix}*{suffix}")):
        held, pid = _probe(path)

        if held:
            found[path[len(prefix):-len(suffix)]] = pid

    return found


def _probe(path: str) -> "tuple[bool, Optional[int]]":
    """(held, pid). Tests the flock, so a file left behind by a dead process reads as free."""
    mine = _HELD.get(path)

    if mine is not None and mine.held:
        return True, os.getpid()

    try:
        fd = os.open(path, os.O_RDONLY)
    except OSError:
        return False, None

    try:
        if _try_lock(fd):
            # Nobody held it. Release at once - this was a probe, not a claim.
            fcntl.flock(fd, fcntl.LOCK_UN)
            return False, None

        return True, _read_pid(fd)
    finally:
        os.close(fd)


def _try_lock(fd: int) -> bool:
    try:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        return True
    except OSError as exc:
        if exc.errno in (errno.EACCES, errno.EAGAIN):
            return False
        raise


def _read_pid(fd: int) -> Optional[int]:
    try:
        os.lseek(fd, 0, os.SEEK_SET)
        text = os.read(fd, 64).decode("utf-8", "replace").replace("\0", " ").split()

        return int(text[0]) if text else None
    except (OSError, ValueError):
        return None


def _evict(name: str, pid: Optional[int], path: str) -> None:
    if pid is None or pid == os.getpid():
        raise AlreadyRunning(name, pid, path)

    try:
        os.kill(pid, signal.SIGTERM)
    except OSError:
        # Gone already, or not ours to signal. Either way the retry loop settles it.
        pass


if __name__ == "__main__":
    import sys

    cmd = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_CMD
    live = running(cmd)

    if not live:
        print(f"no scripts running against {cmd}")
    else:
        for script, pid in live.items():
            print(f"{script:<20} pid {pid if pid is not None else '?'}")
