"""How a looping script is written.

A background reflex is always the same program around its one small opinion: claim the name so a
second copy refuses to start, connect to the client, loop until something says stop, give the name
back on the way out. Written per script that is thirty lines of boilerplate, and the parts most
easily left out - the lock, the liveness check - are exactly the ones whose absence does not look
like a bug. Two healers running is a wasted bandage and a cursor left dangling. A loop that keeps
going after the client exits is a script appending to a file nobody reads.

Two pieces, because they answer different questions.

`@script` wraps main. It owns everything outside the loop: the name this run is tracked under, the
single-instance lock, the Client, the live check, signal handling, and the exit code.

`uo.every(seconds)` is the loop itself, and it stays in the body so the script can write ordinary
`break` and `continue` against its own conditions and keep its state in ordinary locals. It owns
the cadence and the exits that are nobody's opinion: the client going away, the character becoming
a ghost, and the --for / --iterations bounds.

    import argparse, sys
    from uo import script

    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--threshold", type=int, default=90)

    @script(ap)
    def main(uo, args):
        for _ in uo.every(0.3):
            if uo.hp * 100 >= uo.max_hp * args.threshold:
                continue

            bandage = uo.find("bandage")

            if bandage is None:
                return uo.stop("out of bandages - restock")

            uo.use_on(bandage, "self")

    if __name__ == "__main__":
        sys.exit(main())

Falling out of the `for` means `every` ended it - the client went away, the character died, the
time limit elapsed - and main can just return; the wrapper already knows which it was and reports
it. `uo.stop(reason)` is for the script's own conditions: it logs the reason and returns the exit
code, so `return uo.stop(...)` is one line and the reason ends up in the log where an orchestrator
looking for a script that is no longer running will find it.

The name is the script's filename, so tracking needs nothing kept in sync: two launches of
autoheal.py collide because they are both autoheal.py. `python3 cli/uo/locks.py` lists what is
currently running.

Every wrapped script gets these flags for free:

    --replace     stop the copy already running and take over from it
    --every       override the loop interval
    --for         stop after N seconds
    --iterations  stop after N iterations
    --verbose     log more
    --cmdfile     drive a second client; the lock scopes to it automatically

Exit codes are uniform, so a caller can branch on them without reading the output:

    0  the loop finished what it was asked for (--for / --iterations elapsed)
    1  it stopped on a condition it reported (dead, out of bandages, target gone)
    2  no live client
    3  already running
  130  Ctrl-C
  143  SIGTERM (a --replace takeover, or a plain kill)
"""

from __future__ import annotations

import argparse
import functools
import os
import signal
import sys
import time
import traceback
from typing import Callable, Iterator, Optional

from .client import DEFAULT_CMD, DEFAULT_LOG, DEFAULT_STATE, DEFAULT_WORLD
from .client import Client, NotLiveError
from .locks import AlreadyRunning

EXIT_OK = 0
EXIT_STOPPED = 1
EXIT_NO_CLIENT = 2
EXIT_ALREADY_RUNNING = 3
EXIT_INTERRUPTED = 130
EXIT_TERMINATED = 143


class _Terminated(BaseException):
    """SIGTERM, raised so the loop unwinds through its finally instead of being killed outright."""


class Runtime:
    """
    The loop machinery behind `uo.every()` and `uo.stop()`, installed by @script.

    It lives on the Client only for the duration of a wrapped run: outside @script there is no
    lock, no parsed args and no name, so the methods raise rather than half-work.
    """

    def __init__(self, uo: Client, args: argparse.Namespace, name: str, log: Callable[[str], None]):
        self.uo = uo
        self.args = args
        self.name = name
        self.log = log

        self.exit_code = EXIT_OK
        self.iterations = 0

    def every(self, seconds: float = 0.5, on_ghost: str = "stop") -> Iterator[int]:
        """
        Yield once per tick until a universal stop condition trips. See Client.every.
        """
        if on_ghost not in ("stop", "run"):
            raise ValueError("on_ghost must be 'stop' or 'run'")

        interval = self.args.every if self.args.every is not None else seconds
        budget = getattr(self.args, "for")
        limit = self.args.iterations

        started = time.monotonic()
        deadline = started + budget if budget else None

        self.log(f"running every {interval}s" + (f" for {budget}s" if budget else ""))

        while True:
            began = time.monotonic()

            if deadline and began >= deadline:
                self.log("time limit reached")
                self.exit_code = EXIT_OK
                return

            if limit and self.iterations >= limit:
                self.log(f"{limit} iterations done")
                self.exit_code = EXIT_OK
                return

            # Liveness first: everything after this reads state a dead client is no longer writing.
            # `updatedAtMs`, not `inGame` - a crash leaves inGame true on a client that is gone.
            if not self.uo.is_live():
                self.log("client went away - stopping")
                self.exit_code = EXIT_NO_CLIENT
                return

            if not self.uo.in_game:
                time.sleep(max(interval, 1.0))
                continue

            # Ghost before the body reads HP: charGhost comes from the body graphic, so it is
            # already true in the moment before a status packet has zeroed hit points.
            if on_ghost == "stop" and self.uo.ghost:
                self.log("character is a ghost - stopping, hand off to uo-death-recovery")
                self.exit_code = EXIT_STOPPED
                return

            self.iterations += 1

            yield self.iterations

            # Measured from the start of the iteration, so a slow body does not add to the wait.
            remaining = began + interval - time.monotonic()

            if remaining > 0:
                time.sleep(remaining)

    def stop(self, reason: str, code: int = EXIT_STOPPED) -> int:
        """Log why this run is ending and return its exit code. See Client.stop."""
        self.log(reason)
        self.exit_code = code

        return code


def script(
    parser: Optional[argparse.ArgumentParser] = None,
    name: Optional[str] = None,
    require_live: bool = True,
    default_replace: bool = False,
):
    """
    Wrap a script's main so it runs once at a time, under a tracked name, against a live client.

    The wrapped function is called as fn(uo, args). Return an int to exit with it (`uo.stop()`
    gives you one), or None to accept whatever ended the loop.

    `default_replace` flips `--replace`'s own default to on, for the rare script whose normal
    operating model is "one instance, redirected by calling it again" rather than "a second
    instance is a bug" - see safe_goto.py, the one caller of this today. Every other caller is
    unaffected: the flag still defaults to off unless a script opts in.
    """

    def decorate(fn: Callable) -> Callable:
        default_name = name or _script_name(fn)

        @functools.wraps(fn)
        def wrapper(argv=None) -> int:
            ap = parser or argparse.ArgumentParser(description=fn.__doc__)

            _add_standard_arguments(ap, default_replace=default_replace)

            args = ap.parse_args(argv)
            tag = args.name or default_name

            def log(message: str) -> None:
                line = f"{time.strftime('%H:%M:%S')} [{tag}] {message}"
                print(line, flush=True)
                # Also lands in the client's own log, so any script's announcements are tailable
                # the same way client narration already is (LogTail/uo.events) - no separate
                # per-script log file needed. Best-effort: logging must never be what crashes the
                # script it's instrumenting.
                try:
                    with open(args.logfile, "a") as f:
                        f.write(line + "\n")
                except OSError:
                    pass

            # A takeover sends SIGTERM, whose default action kills the process outright. Raising
            # instead lets the evicted script unwind through its finally, say so, and exit 143
            # rather than vanishing mid-iteration.
            signal.signal(signal.SIGTERM, _terminate)

            uo = Client(
                state_file=args.statefile,
                world_file=args.worldfile,
                cmd_file=args.cmdfile,
                log_file=args.logfile,
            )

            if require_live:
                try:
                    uo.require_live()
                except NotLiveError as exc:
                    log(str(exc))
                    return EXIT_NO_CLIENT

            # Before anything with a side effect, so a duplicate exits without having touched the
            # character at all.
            try:
                lock = uo.single_instance(tag, takeover=args.replace)
            except AlreadyRunning as exc:
                log(str(exc))
                return EXIT_ALREADY_RUNNING

            runtime = Runtime(uo, args, tag, log)
            uo.runtime = runtime

            try:
                outcome = fn(uo, args)

                return runtime.exit_code if outcome is None else int(outcome)
            except KeyboardInterrupt:
                log("interrupted")
                return EXIT_INTERRUPTED
            except _Terminated:
                log("terminated - handing over")
                return EXIT_TERMINATED
            except Exception as exc:
                log(f"failed: {exc}")
                traceback.print_exc()

                return EXIT_STOPPED
            finally:
                uo.runtime = None
                lock.release()

        return wrapper

    return decorate


def _terminate(*_) -> None:
    raise _Terminated()


def _script_name(fn: Callable) -> str:
    """
    The name this run is tracked under: the script's filename.

    Deliberately not the function's name - every one of these is called `main`. argv[0] is what was
    typed to launch it and what `ps` shows, so it is also what someone will recognise in the
    "already running" message.
    """
    path = sys.argv[0] if sys.argv and sys.argv[0] else ""

    if path:
        return os.path.splitext(os.path.basename(path))[0]

    module = getattr(fn, "__module__", "script")

    return "script" if module == "__main__" else module.rsplit(".", 1)[-1]


def _add_standard_arguments(ap: argparse.ArgumentParser, default_replace: bool = False) -> None:
    """Idempotent: a parser reused across wrapper calls must not raise on a duplicate option."""
    if getattr(ap, "_uo_standard", False):
        return

    ap._uo_standard = True

    # Docstrings carry their own line breaks; the default formatter reflows them into a paragraph.
    # Only when the script has not chosen a formatter of its own.
    if ap.formatter_class is argparse.HelpFormatter:
        ap.formatter_class = argparse.RawDescriptionHelpFormatter

    ap.add_argument("--replace", action="store_true", default=default_replace,
                    help="stop the copy already running and take over from it"
                         + (" (default: on for this script)" if default_replace else ""))
    ap.add_argument("--every", type=float, default=None,
                    help="override the loop interval, in seconds")
    ap.add_argument("--for", type=float, default=None, metavar="SECONDS",
                    help="stop after this long")
    ap.add_argument("--iterations", type=int, default=None,
                    help="stop after this many iterations")
    ap.add_argument("--name", default=None,
                    help="lock name; defaults to this script's filename")

    if not any(action.dest == "verbose" for action in ap._actions):
        ap.add_argument("--verbose", action="store_true")

    ap.add_argument("--cmdfile", default=DEFAULT_CMD, help=argparse.SUPPRESS)
    ap.add_argument("--logfile", default=DEFAULT_LOG, help=argparse.SUPPRESS)
    ap.add_argument("--statefile", default=DEFAULT_STATE, help=argparse.SUPPRESS)
    ap.add_argument("--worldfile", default=DEFAULT_WORLD, help=argparse.SUPPRESS)
