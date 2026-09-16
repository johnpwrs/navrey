"""A Python layer for driving the ClassicUO agent client.

    import sys; sys.path.insert(0, "cli")
    from uo import Client

    uo = Client()
    uo.require_live()

    print(uo.name, uo.hp, "/", uo.max_hp, "at", uo.xy)

    for mob in uo.mobiles(within=10, hostile=True):
        uo.attack(mob)
        break

This library is mechanism only - reading state, sending actions, decoding events, waiting on
conditions. Policy belongs in scripts that import it: a behaviour like "heal whenever I drop below
max" is one opinion about how to play, and baking it in here would make every future script inherit
that opinion.

`uo.script` is the one place a `while True` lives, and it is not an exception to that rule. It
yields control back to the script once per tick and holds no view on what to do with it; what it
owns is the part that is identical in every background loop and dangerous to omit - the
single-instance lock, and stopping when the client is gone or the character is dead. A loop script
is written as:

    @script(ap)
    def main(uo, args):
        for _ in uo.every(0.3):
            ...
            if nothing_left:
                return uo.stop("out of bandages")

The loop stays in the script's body so it keeps ordinary locals and ordinary `break`. See
`.claude/skills/uo-combat-loop/autoheal.py`.

Reads are free and actions are cheap:

    state / world read     ~0.07 ms   (mtime-gated; re-parsed only when the client rewrites)
    send an action         ~0.01 ms   (append a line; does not wait)
    synchronous query      ~6 ms      (round trip through the client's command worker)

So read state as often as you like, act without waiting, and only pay a round trip for the handful
of commands whose output is not in a file.
"""

from .client import Client, CommandError, NotLiveError, Walk, serial_of
from .combat import Combat, CombatWatch
from .entities import Item, Mobile, Nav, Target
from .events import Event, LogEvent, LogTail
from .layers import Layer
from .locks import AlreadyRunning, InstanceLock, holder, is_running, running, single_instance
from .script import Runtime, script
from .state import JsonFile

__all__ = [
    "Client",
    "CommandError",
    "NotLiveError",
    "Walk",
    "serial_of",
    "Item",
    "Mobile",
    "Nav",
    "Target",
    "Event",
    "LogEvent",
    "LogTail",
    "Layer",
    "Combat",
    "CombatWatch",
    "JsonFile",
    "AlreadyRunning",
    "InstanceLock",
    "single_instance",
    "holder",
    "is_running",
    "running",
    "script",
    "Runtime",
]
