"""The facade a script actually uses."""

from __future__ import annotations

import contextlib
import time
from typing import Callable, Iterable, List, Optional, Union

from .commands import CommandError, Commands
from .entities import Item, Mobile, Nav, Target
from .combat import Combat, CombatWatch
from .events import Event, LogEvent, LogTail
from .locks import InstanceLock, single_instance
from .state import JsonFile

DEFAULT_STATE = "/tmp/cuostate.json"
DEFAULT_WORLD = "/tmp/cuoworld.json"
DEFAULT_CMD = "/tmp/cuocmd"
DEFAULT_LOG = "/tmp/cuolog"

# Anything with a serial: a raw string, or an object carrying one.
Serial = Union[str, int, Item, Mobile, Target]


def serial_of(thing: Serial) -> str:
    if isinstance(thing, str):
        return thing
    if isinstance(thing, int):
        return f"0x{thing:08X}"
    value = getattr(thing, "serial", None)
    if value is None:
        raise ValueError(f"no serial on {thing!r}")
    return value


class NotLiveError(RuntimeError):
    pass


class Walk:
    """Handle for a walk in progress, so `uo.goto(x, y).wait()` reads naturally."""

    def __init__(self, client: "Client", target):
        self.client = client
        self.target = target

    def wait(self, timeout: float = 300.0, poll: float = 0.1) -> str:
        """
        Block until the walk ends. Returns the final status:
        arrived | nopath | stopped | failed.

        Checks the status rather than only that it stopped - `nopath` and `arrived` both end a walk,
        and a character that never moved looks the same either way from its coordinates.
        """
        deadline = time.monotonic() + timeout

        # The walk may not have registered yet; goto returns before the nav thread starts.
        for _ in range(20):
            if self.client.nav.active:
                break
            time.sleep(0.02)

        while time.monotonic() < deadline:
            nav = self.client.nav

            if not nav.active:
                return nav.status

            time.sleep(poll)

        return "timeout"


class Client:
    """
    Reads state from the client's JSON files, sends actions through its command file.

    Cheap to construct and cheap to keep: state access is gated on the files' mtimes, so a property
    read costs a stat unless something actually changed.
    """

    def __init__(
        self,
        state_file: str = DEFAULT_STATE,
        world_file: str = DEFAULT_WORLD,
        cmd_file: str = DEFAULT_CMD,
        log_file: str = DEFAULT_LOG,
    ):
        self._state = JsonFile(state_file)
        self._world = JsonFile(world_file)
        self.commands = Commands(cmd_file, log_file)
        self.events = LogTail(log_file)

        # Its own tail, not self.events: wait_for consumes that one, and two readers sharing a
        # tail would each see half the lines.
        self._combat = CombatWatch(log_file, lambda: self.name)

        # Installed by @script for the duration of a wrapped run; see uo.script.
        self.runtime = None

    # ---- raw access -------------------------------------------------------

    @property
    def state(self) -> dict:
        return self._state.read()

    @property
    def world(self) -> dict:
        return self._world.read()

    @contextlib.contextmanager
    def frozen(self):
        """
        Pin both files for a block.

        Reading `hp` and `pos` as two accesses can straddle a rewrite and mix two moments. Inside
        this block every read comes from one snapshot, so several fields are guaranteed to agree.
        """
        self._state.freeze()
        self._world.freeze()
        try:
            yield self
        finally:
            self._state.thaw()
            self._world.thaw()

    def is_live(self, max_age_ms: float = 5000) -> bool:
        """Whether a client is actually running. See JsonFile.is_live for why not `in_game`."""
        return self._state.is_live(max_age_ms)

    def require_live(self) -> None:
        if not self.is_live():
            raise NotLiveError(
                f"state file is {self._state.age_ms:.0f}ms stale - is the client running?"
            )

    # ---- combat activity ---------------------------------------------------

    @property
    def combat(self) -> Combat:
        """
        When combat last happened, from the log's swing and damage narration.

        Reading this polls the log, so it is current as of the call. Times are taken from each
        line's own stamp rather than from when it was read, and a script starting mid-fight
        backfills from the log it never saw - so `since_combat` means the same thing whether the
        script has been running for an hour or a second. See uo.combat.

            uo.combat.last_combat_time      epoch seconds, or None if nothing yet
            uo.combat.since_combat          seconds since, or inf
            uo.combat.since_damage_taken    seconds since we were hurt
            uo.combat.since_attacked        seconds since something swung at us
            uo.combat.since_swing           seconds since *we* swung
        """
        return self._combat.update()

    @property
    def last_combat_time(self) -> Optional[float]:
        """Epoch seconds of the last swing or damage either way; None if none seen."""
        return self.combat.last_combat_time

    def in_combat(self, within: float = 8.0) -> bool:
        """
        Whether anything combat-shaped happened in the last `within` seconds.

        The default is generous on purpose: a weapon swings on a timer of several seconds, and a
        gap between swings is not the end of a fight. Tighten it for "is it happening right now",
        widen it for "have we been left alone long enough to sit down".
        """
        return self.combat.since_combat <= within

    # ---- looping (inside @script only) -------------------------------------

    def every(self, seconds: float = 0.5, on_ghost: str = "stop"):
        """
        The loop for a background script: `for _ in uo.every(0.3): ...`

        It stays in the script's body rather than being owned by the decorator, so the script keeps
        its state in ordinary locals and writes ordinary `break` and `continue` against its own
        conditions. What it takes off the script's hands is the cadence and the three stops that
        are nobody's opinion:

          the client going away (`updatedAtMs`, not `inGame` - a crash leaves `inGame` true)
          the character becoming a ghost, unless on_ghost="run"
          the --for / --iterations bounds

        When one of those ends the iteration, falling out of the `for` and returning None is enough
        - the wrapper already knows which it was and reports it. Use `uo.stop(reason)` for the
        script's own conditions.

        The interval is measured from the start of one iteration to the start of the next, so a
        slow body does not add to the wait. `--every` overrides `seconds`.
        """
        return self._runtime("every").every(seconds, on_ghost)

    def stop(self, reason: str, code: int = 1) -> int:
        """
        End this run for a reason the script decided on: `return uo.stop("out of bandages")`.

        Logs the reason and returns the exit code. The reason is the last line of the run, which is
        what someone looking for a background script that is no longer there will read.
        """
        return self._runtime("stop").stop(reason, code)

    @property
    def log(self):
        """Timestamped logger, tagged with the script's name. Inside @script only."""
        return self._runtime("log").log

    def _runtime(self, what: str):
        if self.runtime is None:
            raise RuntimeError(
                f"uo.{what}() works only inside a function wrapped in @script - it needs the "
                "parsed arguments, the run's name and its single-instance lock. See uo.script.")

        return self.runtime

    def single_instance(self, name: str, takeover: bool = False) -> InstanceLock:
        """
        Claim `name` so a second copy of this script refuses to start. See uo.locks.

        Scoped to this client's command file, so scripts driving two clients do not block each
        other. Keep the returned lock alive for the life of the loop.
        """
        return single_instance(name, cmd_file=self.commands.cmd_file, takeover=takeover)

    # ---- player -----------------------------------------------------------

    @property
    def in_game(self) -> bool:
        return bool(self.state.get("inGame"))

    @property
    def name(self) -> str:
        return self.state.get("charName") or ""

    @property
    def hp(self) -> int:
        return self.state.get("hits", 0)

    @property
    def max_hp(self) -> int:
        return self.state.get("maxHits", 0)

    @property
    def hp_pct(self) -> float:
        return 100.0 * self.hp / self.max_hp if self.max_hp else 0.0

    @property
    def mana(self) -> int:
        return self.state.get("mana", 0)

    @property
    def stamina(self) -> int:
        return self.state.get("stamina", 0)

    @property
    def x(self) -> int:
        return self.state.get("charPosX", 0)

    @property
    def y(self) -> int:
        return self.state.get("charPosY", 0)

    @property
    def z(self) -> int:
        return self.state.get("charPosZ", 0)

    @property
    def pos(self):
        s = self.state
        return (s.get("charPosX"), s.get("charPosY"), s.get("charPosZ"))

    @property
    def xy(self):
        s = self.state
        return (s.get("charPosX"), s.get("charPosY"))

    @property
    def direction(self) -> str:
        return self.state.get("charDir", "")

    @property
    def map(self) -> int:
        return self.state.get("map", -1)

    @property
    def ghost(self) -> bool:
        """The client's own IsDead - set from the ghost body graphic, so it is true even before a
        status packet has updated hit points. A better death signal than hp == 0."""
        return bool(self.state.get("charGhost"))

    @property
    def war_mode(self) -> bool:
        return bool(self.state.get("warMode"))

    @property
    def hidden(self) -> bool:
        return bool(self.state.get("isHidden"))

    @property
    def poisoned(self) -> bool:
        return bool(self.state.get("isPoisoned"))

    @property
    def paralyzed(self) -> bool:
        return bool(self.state.get("isParalyzed"))

    @property
    def mounted(self) -> bool:
        return bool(self.state.get("isMounted"))

    @property
    def gold(self) -> int:
        return self.state.get("gold", 0)

    @property
    def weight(self) -> int:
        return self.state.get("weight", 0)

    @property
    def max_weight(self) -> int:
        return self.state.get("maxWeight", 0)

    @property
    def overloaded(self) -> bool:
        """
        Carrying more than the character can bear.

        This state is worth checking explicitly because it fails *silently* in
        every direction: the server refuses every attempt to move an item (drop,
        equip, loot - each one is answered by putting the item straight back in
        the pack, with no error the command layer can see), and once stamina has
        drained to zero it refuses movement too, so `goto` reports `nopath` as if
        the ground were walled off rather than the pack being too heavy. Measured
        2026-09-13: a loot-heavy grind hit 294/289 stones and read as a pathing
        bug for several minutes. The stamina drain is *caused* by the overload,
        so it does not recover while the pack is still too heavy: movement comes
        back only in stop-start single steps as stamina trickles in, and reads as
        a stuck walk. Nothing clears it but shedding weight, and since the
        character cannot lift anything to drop it, that means reaching a vendor,
        a bank or an already-open container - or being lighter before it happens.
        Watch this during a grind rather than after: looted armour and weapons
        add up fast, and the failure mode looks like a pathing bug, not a full pack.
        """
        cap = self.max_weight
        return bool(cap) and self.weight > cap

    # ---- targets and navigation -------------------------------------------

    @property
    def nav(self) -> Nav:
        return Nav(self.state.get("nav"))

    @property
    def last_attack(self) -> Target:
        return Target(self.state.get("lastAttack"))

    @property
    def last_cursor_target(self) -> Target:
        return Target(self.state.get("lastCursorTarget"))

    # ---- inventory --------------------------------------------------------

    @property
    def backpack_opened(self) -> bool:
        """
        Whether the server has actually sent the backpack's contents.

        Containers are never populated for free - until the backpack has been double-clicked once
        this session, `backpack` is empty regardless of what is really in there. That has been
        misread as "nothing to deposit" before; `open_backpack()` fixes it.
        """
        return bool(self.state.get("backpackOpened"))

    @property
    def backpack(self) -> List[Item]:
        return [Item(d) for d in self.state.get("backpack") or []]

    @property
    def equipped(self) -> List[Item]:
        return [Item(d) for d in self.state.get("equipped") or []]

    @property
    def backpack_serial(self) -> Optional[str]:
        return self.state.get("backpackID")

    def find(self, name: str, exact: bool = False) -> Optional[Item]:
        """First backpack item whose name matches. Case-insensitive substring unless `exact`."""
        needle = name.lower()

        for item in self.backpack:
            label = item.name.lower()

            if (label == needle) if exact else (needle in label):
                return item

        return None

    def find_all(self, name: str) -> List[Item]:
        needle = name.lower()
        return [i for i in self.backpack if needle in i.name.lower()]

    def open_backpack(self, wait: float = 2.0) -> bool:
        """Double-click the backpack so its contents populate. No-op if already open."""
        if self.backpack_opened:
            return True

        serial = self.backpack_serial

        if not serial:
            return False

        self.use(serial)

        return self.wait(lambda: self.backpack_opened, timeout=wait)

    # ---- world ------------------------------------------------------------

    def mobiles(
        self,
        within: Optional[int] = None,
        notoriety: Optional[Union[str, Iterable[str]]] = None,
        hostile: Optional[bool] = None,
        named: Optional[str] = None,
    ) -> List[Mobile]:
        """Nearby mobiles, nearest first. All filters are optional and combine."""
        out = [Mobile(d) for d in self.world.get("mobiles") or []]

        if within is not None:
            out = [m for m in out if m.distance <= within]

        if notoriety is not None:
            wanted = {notoriety} if isinstance(notoriety, str) else set(notoriety)
            out = [m for m in out if m.notoriety in wanted]

        if hostile is not None:
            out = [m for m in out if m.hostile == hostile]

        if named is not None:
            needle = named.lower()
            out = [m for m in out if needle in m.name.lower()]

        return out

    def nearest(self, **filters) -> Optional[Mobile]:
        found = self.mobiles(**filters)
        return found[0] if found else None

    def items(self, within: Optional[int] = None, named: Optional[str] = None) -> List[Item]:
        """
        Movable ground items, nearest first.

        Immovable scenery is excluded by the client, so this is loot rather than map dressing.
        """
        out = [Item(d) for d in self.world.get("items") or []]

        if within is not None:
            out = [i for i in out if (i.distance if i.distance is not None else 999) <= within]

        if named is not None:
            needle = named.lower()
            out = [i for i in out if needle in i.name.lower()]

        return out

    def containers(self, within: Optional[int] = None) -> List[Item]:
        """
        Nearby openable ground items - chests, crates, barrels, and corpses - nearest first.

        A corpse is not Movable, so it never appears in `items()` - this is the one place a kill's
        corpse can be found without a round trip: `Item.ItemData.IsContainer` (not a name guess)
        gates this list server-side, same mtime-gated JSON read as everything else here.
        """
        out = [Item(d) for d in self.world.get("containers") or []]

        # `distance` is 0 for the tile under our feet - exactly where a melee kill's corpse
        # lands - so it must be tested against None, never truthiness. Measured 2026-09-03:
        # `(distance or 999)` scored every under-foot corpse as out of range, and the looter
        # abandoned each zombie while draining every skeleton that fell one tile away.
        if within is not None:
            out = [i for i in out if (i.distance if i.distance is not None else 999) <= within]

        return out

    # ---- actions (fire and forget) ----------------------------------------

    def goto(self, x: int, y: int, z: Optional[int] = None) -> Walk:
        """`z` names the floor: a roof at z 20 is a different destination from the street below."""
        self.commands.send(f"goto {x} {y}" if z is None else f"goto {x} {y} {z}")
        return Walk(self, (x, y))

    def goto_poi(self, query: str) -> Walk:
        self.commands.send(f"goto {query}")
        return Walk(self, query)

    def gotoexact(self, x: int, y: int, z: Optional[int] = None) -> Walk:
        self.commands.send(f"gotoexact {x} {y}" if z is None else f"gotoexact {x} {y} {z}")
        return Walk(self, (x, y))

    def cancel_walk(self) -> None:
        """Send the `stop` command - halts the current walk. Not to be confused with the
        script-exit `stop(reason, code)` above; a second `def stop` here used to shadow that one
        entirely, silently breaking every script's `return uo.stop("reason")` exit path."""
        self.commands.send("stop")

    def walk(self, direction: str, run: bool = False) -> None:
        self.commands.send(f"walk {direction}{' run' if run else ''}")

    def attack(self, target: Serial) -> None:
        self.commands.send(f"attack {serial_of(target)}")

    def use(self, thing: Serial) -> None:
        self.commands.send(f"use {serial_of(thing)}")

    def click(self, thing: Serial) -> None:
        self.commands.send(f"click {serial_of(thing)}")

    def request_hp(self, target: Serial) -> None:
        """
        Ask the server for a mobile's status.

        Another mobile's HP is a placeholder until this has been sent; after that it tracks live.
        An unchanging target HP usually means you are not damaging it, not that the reading is stale.
        """
        self.commands.send(f"hp {serial_of(target)}")

    def war(self, on: Optional[bool] = None) -> None:
        """Toggle war mode, or set it - `war(True)` is a no-op when already in war mode."""
        if on is None or on != self.war_mode:
            self.commands.send("war")

    def say(self, text: str) -> None:
        self.commands.send(f"say {text}")

    def target_self(self) -> None:
        self.commands.send("target self")

    def target(self, thing: Serial) -> None:
        self.commands.send(f"target {serial_of(thing)}")

    def use_on(self, item: Serial, target: Union[Serial, str] = "self",
               timeout: float = 5.0) -> bool:
        """
        Use an item and answer the target cursor it raises. Returns False if no cursor appeared.

        The two halves cannot just be fired back to back. `use` returns as soon as the client has
        sent the packet, but the *server* raises the cursor, and that reply lands a frame or two
        later - measured at 33ms. Sending `target` into that gap gets "nothing is asking for a
        target", the request is wasted, and the cursor is then left open until it times out.

        So this waits for the server to actually ask. Bandages, scissors, spells and anything else
        that targets all need it.
        """
        self.events.drain()
        self.use(item)

        if self.wait_for(Event.TARGET_REQUEST, timeout=timeout) is None:
            return False

        if target == "self":
            self.target_self()
        else:
            self.target(target)

        return True

    def drop(self, item: Serial, container: Serial, amount: Optional[int] = None) -> None:
        suffix = f" {amount}" if amount is not None else ""
        self.commands.send(f"drop {serial_of(item)} {serial_of(container)}{suffix}")

    def get_item(self, item: Serial, amount: Optional[int] = None) -> None:
        """
        Pick an item up into the backpack - `get <serial> [amount]`. Fire and forget, same
        discipline as `drop`: confirming arrival is the caller's job.

        Not `drop`/`move` - those move between two named containers and need a destination.
        `get` takes none at all; it always lands in the backpack (see `CommandEngine.Actions.cs`'s
        `get` handler), which is exactly what looting a corpse needs and `move()` is the wrong
        shape for.
        """
        suffix = f" {amount}" if amount is not None else ""
        self.commands.send(f"get {serial_of(item)}{suffix}")

    def move(self, item: Serial, container: Serial, amount: Optional[int] = None,
             timeout: float = 8.0) -> bool:
        """
        Move an item into a container and confirm it arrived. Returns False if it did not.

        Prefer this over bare `drop` whenever the move matters, and note *what* it verifies: that
        the item is present in the destination, never that it left the source.

        Those are not the same check, and getting it wrong looks like success. An item is briefly
        absent from the source while it is being carried, so "gone from the backpack" is satisfied
        by a move that then fails and bounces straight back - observed doing exactly that against a
        bank box the client no longer had open. Only the destination can confirm a move.
        """
        target = serial_of(container)

        def arrived() -> bool:
            wanted = serial_of(item)

            if target == self.backpack_serial:
                return any(i.serial == wanted for i in self.backpack)

            return any(i.serial == wanted for i in self.container(target))

        self.drop(item, target, amount)

        # A container read costs a round trip, so poll gently; the backpack is free either way.
        return self.wait(arrived, timeout=timeout, poll=0.4)

    def equip(self, item: Serial, layer: str) -> None:
        self.commands.send(f"equip {serial_of(item)} {layer}")

    def unequip(self, item: Serial) -> None:
        self.commands.send(f"unequip {serial_of(item)}")

    # ---- synchronous queries ----------------------------------------------

    def call(self, line: str, timeout: float = 10.0) -> List[str]:
        """
        Run a command and return its printed lines.

        For the commands whose output is not in the state or world files - shop, gumps, tiles, path,
        los, container for something other than your own backpack.

        A returned result does not mean the command achieved anything: the client reports a
        not-found as `ok`, because only a hard failure sets its error flag. Verify by observing
        state, never by the fact that this returned.
        """
        return self.commands.call(line, timeout=timeout)

    def container(self, serial: Serial, timeout: float = 5.0) -> List[Item]:
        """Contents of any container. Your own backpack is free via `backpack` instead."""
        lines = self.call(f"container {serial_of(serial)}", timeout=timeout)
        out: List[Item] = []

        for line in lines:
            text = line.strip()
            is_container = False

            # Each child row is tagged `[container]`/`[item]` ahead of its serial (see
            # CommandEngine.Actions.cs's `container` handler) - the same deterministic signal
            # `containers()` gets for ground items, so a nested bag/crate is never guessed from
            # its name. Tolerate an untagged line (an older client build) by just falling through
            # to the "not 0x" check below, rather than requiring the tag.
            if text.startswith("[container]"):
                is_container = True
                text = text[len("[container]"):].strip()
            elif text.startswith("[item]"):
                text = text[len("[item]"):].strip()

            if not text.startswith("0x"):
                continue

            parts = text.split(None, 1)
            name = parts[1] if len(parts) > 1 else ""
            amount = 1

            if " x" in name:
                head, _, tail = name.rpartition(" x")
                digits = tail.split()[0] if tail.split() else ""

                if digits.isdigit():
                    name, amount = head, int(digits)

            out.append(Item({
                "serial": parts[0], "name": name.strip(), "amount": amount,
                "isContainer": is_container,
            }))

        return out

    # ---- waiting ----------------------------------------------------------

    def wait(
        self,
        predicate: Callable[[], bool],
        timeout: float = 10.0,
        poll: float = 0.05,
    ) -> bool:
        """Poll until `predicate()` is true. Returns False on timeout rather than raising."""
        deadline = time.monotonic() + timeout

        while time.monotonic() < deadline:
            if predicate():
                return True

            time.sleep(poll)

        return predicate()

    def wait_for(
        self,
        kinds: Union[Event, Iterable[Event]],
        timeout: float = 15.0,
        poll: float = 0.05,
    ) -> Optional[LogEvent]:
        """
        Wait for one of these event kinds. Returns the event, or None on timeout.

        Only sees events from now on - anything already in the log is skipped, so a stale line from
        a previous action cannot satisfy this call.
        """
        wanted = {kinds} if isinstance(kinds, Event) else set(kinds)
        deadline = time.monotonic() + timeout

        self.events.drain()

        while time.monotonic() < deadline:
            for event in self.events.poll():
                if event.kind in wanted:
                    return event

            time.sleep(poll)

        return None
