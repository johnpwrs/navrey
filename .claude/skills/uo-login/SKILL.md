---
name: uo-login
description: Start the ClassicUO client with its CLI agent, log in, and confirm the character is in the world. Use whenever the user asks to run/start/log in to the UO client, or before any goto/pos/canwalk/tiles/items/closest/findpoi command.
---

# Starting the client and logging in

There is one process and one connection. The ClassicUO client owns both; the CLI is a thin front
end that talks to it through two files:

- Commands go in: `/tmp/cuocmd` (append a line)
- Output streams to: `/tmp/cuolog`

Append commands with `echo`/`printf` — much faster than shelling out to `cli/navrey <cmd>`,
which spawns a fresh .NET process per call (50-100ms). Read the reply back out of `/tmp/cuolog`,
bracketed by `[CMD]` / `[CMD-END]`.

## 1. Check for an existing client first

Never start a second one: two clients means two connections and two characters.

```bash
ps aux | grep "[n]et10.0/cuo"
```

If one is running, skip to step 3. To restart cleanly:

```bash
pkill -f "net10.0/cuo"
```

## 2. Start it

The account lives in `.env` at the repo root (`UO_USER`, `UO_PASS` — nothing else). Shard host and
port, client version, UO data directory, POI directory and shard/character selection come from
`settings.json` at the repo root. Neither can be overridden from the command line — the
`-clientversion` / `-uopath` / `-ip` / `-username` flags no longer exist, after a stray
`-clientversion` flag kept beating the shard's real version. The client reads both files from its
working directory, so start it from the repo root.

```bash
cd "$(git rev-parse --show-toplevel)"

# With the game window, so a human can watch and play alongside the CLI:
nohup ~/.dotnet/dotnet run --project src/ClassicUO.Client/ClassicUO.Client.csproj -- -agent \
  > /tmp/cuostdout.log 2>&1 &

# Or with no window at all:
nohup ~/.dotnet/dotnet run --project src/ClassicUO.Client/ClassicUO.Client.csproj -- -headless \
  > /tmp/cuostdout.log 2>&1 &

disown
```

Then wait for login rather than guessing at a sleep — **and wait on the state file, not the log**:

```bash
# backgrounded, like anything that loops
until [ "$(jq -r 'if (.inGame == true) and ((now*1000 - .updatedAtMs) < 5000)
                  then "yes" else "no" end' /tmp/cuostate.json 2>/dev/null)" = "yes" ]; do
  sleep 2
done
jq -c '{charName,hits,maxHits,charPosX,charPosY}' /tmp/cuostate.json
```

**Do not** wait by grepping `/tmp/cuolog` for `*** Entered the world! ***`. The client truncates the
log only once it starts writing, and `dotnet run` spends minutes building first — so the grep
matches the *previous* session's line and reports success while the client is still compiling.
Confirmed live, twice. The state file cannot lie the same way: `inGame` true plus a timestamp under
five seconds old means the client is up **now**.

Same trap when stopping it: the client process is `bin/Debug/net10.0/cuo`, so `pkill -f
"net10.0/cuo"` is the pattern that works. `pkill -f "ClassicUO.Client.csproj"` kills only the
`dotnet run` wrapper and leaves the client happily serving — after which a `ps | grep` on that same
wrong pattern will cheerfully report the client is down. There is no `kill` command inside the
client; `pkill` is the way.

Server and character selection are automatic. A gump (MOTD, "client out of date", etc.) commonly
pops up the instant you enter the world — it is **not** dismissed automatically, and it can sit
there eating clicks/targets meant for the game underneath it, so close it before doing anything
else:

```bash
echo "gumps" >> /tmp/cuocmd
sleep 1
tail -15 /tmp/cuolog
```

**Don't assume any fixed button number closes it — different gumps use different IDs, and
guessing risks pressing something other than close/continue.** `gumps` prints each button as a
`[button N]` marker interleaved with the label text positioned next to it (e.g. an "OK" or "I
Agree" caption right before or after `[button N]` in the dump), so read that output and pick the
`N` whose nearby text actually reads as close/OK/continue:

```bash
echo "gumpresponse <N>" >> /tmp/cuocmd
sleep 1
echo "gumps" >> /tmp/cuocmd      # confirm it's gone
sleep 1
tail -15 /tmp/cuolog
```

`gumpresponse` always targets whichever server gump is currently open, so no serial is needed. If
the text dump is ambiguous (no obvious close/continue label near any button), re-check `gumps`'
raw output before guessing — sending the wrong button can trigger something other than dismissal.

If a human is present, `cli/navrey` does all of the above and then drops into an interactive
prompt; `cli/navrey --headless` does it without a window.

The window can be shown or hidden at any time without reconnecting:

```bash
echo "show" >> /tmp/cuocmd    # pop the game window up mid-session
echo "hide" >> /tmp/cuocmd
```

## 3. Confirm and go

There is no `loadmap` or `loadpoi` step any more — the client loads its map data at startup, and
POI data loads on first use. Open the backpack too, so its contents are actually known —
containers are never populated for free; the server only sends contents after the container is
opened (see `uo-equip-items` step 1):

```bash
echo "inv" >> /tmp/cuocmd                     # equipped items, including the backpack's own serial
sleep 1
tail -15 /tmp/cuolog
echo "use <backpack-serial>" >> /tmp/cuocmd   # double-click = open
sleep 2
echo "inv" >> /tmp/cuocmd                     # now backpack contents actually populate
sleep 1
tail -15 /tmp/cuolog
```

Then confirm world state and position:

```bash
printf 'world\npoi\n' >> /tmp/cuocmd      # InGame/map/entity counts, POI count and categories
sleep 1
tail -15 /tmp/cuolog
jq -c '{inGame,charName,charPosX,charPosY,charPosZ,charDir,map,hits,maxHits}' /tmp/cuostate.json
```

`inGame: true` and a sensible position mean everything is ready.

## 4. Start the standing scripts — part of logging in, not an extra

Login is not finished until the reflexes that are meant to always run are running. Launch these
backgrounded, then confirm with `python3 cli/uo/locks.py` that they actually took their lock
rather than exiting quietly:

```bash
# always: someone may speak to us at any moment, including while we are dead
python3 "$(git rev-parse --show-toplevel)/.claude/skills/uo-communication/listen.py"

# always, whenever there are bandages: nothing else heals the character
python3 "$(git rev-parse --show-toplevel)/.claude/skills/uo-combat-loop/autoheal.py" --threshold 90
```

Then watch the listener with the Monitor tool filtered to `\[TALK\]`, and answer anyone who speaks
to us — either hand the line to the `uo-conversationalist` subagent or reply directly. See
`uo-communication` for both routes, and for which channel to reply on.

**Deliberately not started here:** `combat_movement_melee.py`.
Launching a combat reflex is a decision to go fighting, not part of getting logged in — see
`uo-combat-loop`.

A subagent or hook added during a session is only picked up by sessions that start *after* it
exists. If `uo-conversationalist` is not an available agent type, reply directly instead.

## Reading state without a command

The client continuously publishes the character's state to `/tmp/cuostate.json`. Anything about
your *own* character — position, direction, map, hit points, stamina, mana, stats, weight, gold,
war mode, whether you're a ghost, hidden, poisoned, mounted — is there already, so there is no
reason to run `pos` or `status` and parse the reply:

```bash
jq .charPosX /tmp/cuostate.json
jq -r '"\(.charName) \(.hits)/\(.maxHits) hp at (\(.charPosX), \(.charPosY)) facing \(.charDir)"' /tmp/cuostate.json
```

It is rewritten whenever anything changes, and at least every 250ms, so it is safe to poll in a
tight loop — much better than `command; sleep 1; tail /tmp/cuolog`. Writes land via a rename, so a
reader never sees a half-written file and never needs to retry.

**Check liveness with `updatedAtMs`, not `inGame`.** A clean exit or a `kill` leaves
`{"updatedAtMs":…,"inGame":false}`, but nothing can run on `kill -9` or a crash — so a file that
still says `inGame: true` may belong to a client that is gone. Compare the timestamp to now:

```bash
python3 -c "
import json,time; d=json.load(open('/tmp/cuostate.json'))
age=time.time()*1000-d['updatedAtMs']
print('live' if age<2000 else 'STALE (%.0fs) - client down?'%(age/1000))"
```

`/tmp/cuoworld.json` is the same idea for the surroundings — `mobiles` in view and movable
`items` on the ground, so those are reads too rather than commands.

Commands remain the way to *do* things, and to read what the files don't carry — container
contents, vendor stock, gumps, line of sight.

## Scripting it: the `uo` library

For anything with a loop, drive the client from Python rather than bash:

```python
import sys; sys.path.insert(0, "cli")
from uo import Client, Event

uo = Client(); uo.require_live()

uo.hp, uo.max_hp, uo.xy, uo.ghost, uo.war_mode   # ~4us each
uo.goto(<x>, <y>).wait()                        # or fire it and carry on
uo.mobiles(within=10, hostile=True)
uo.use_on(uo.find("bandage"), "self")             # waits for the target cursor
uo.move(item, bank_serial)                        # confirms arrival, not departure
```

State reads cost ~4us and actions ~0.01ms, so polling tightly is free. It never shells out to
`navrey` — that spawns a .NET process, 50-100ms per call — it appends to the command file directly.

The library is mechanism only: state, actions, events, bounded waits. Loops and policy live in
scripts that import it, like `.claude/skills/uo-combat-loop/autoheal.py`.

### Which tool for which job

| What you are doing | Use |
|---|---|
| A one-off **action** — equip, say, drop, use, a single `goto` | **append to the command file**: `echo "equip 0x40612D85 01" >> /tmp/cuocmd` |
| Reading **your own state or what is nearby** | **jq the files**: `jq .charPosX /tmp/cuostate.json`, `jq .mobiles /tmp/cuoworld.json` |
| Reading something not in a file — `shop`, `gumps`, `los`, `tiles`, `path`, another container | **append the command, then read `/tmp/cuolog`**: `echo "shop" >> /tmp/cuocmd; sleep 1; tail -15 /tmp/cuolog` |
| Anything that **loops** — combat, healing, chasing, waiting on a condition | **a `.py` script, run in the background** |

**Always append to `/tmp/cuocmd` — never invoke `cli/navrey <cmd>` directly**, whether or not
you need the output. `navrey` spawns a .NET process every call — measured at 50-100ms — to do what
an append does in ~0.01ms. When you need the reply, `sleep` briefly and `tail`/`grep` it out of
`/tmp/cuolog`; that costs nothing extra over waiting on `navrey` to return.

```bash
echo "war" >> /tmp/cuocmd                       # instant
printf 'unequip 0x40612D84\nequip 0x40612D85 01\n' >> /tmp/cuocmd   # several at once, in order
```

Commands run in the order they arrive, so a batch like that is safe. Confirm an action landed by
reading the state file, not by the fact the append succeeded — the append only means the client
will see it. And note that even the log reply is a weak signal for success: a command that found
nothing still reports `ok`, because only a hard failure sets the client's error flag.

**Do not write a long inline `python3 - <<'PY'` block that loops.** It blocks the session for its
whole duration, which is exactly what the async `goto` work was meant to stop: you cannot check
anything, react to anything, or send another command while it runs, and if it hangs there is
nothing to inspect.

Put the loop in a file and run it detached instead, the way `autoheal.py` is run:

```bash
python3 .claude/skills/uo-combat-loop/killtarget.py 0x0003F0AC &
```

Run it via the Bash tool with `run_in_background: true`. That way it prints progress you can read
as it goes, keeps running while you do other things, and can be stopped independently. Several such
scripts can run at once — a healer and a combat loop together is the normal case, and they compose
because the client serialises commands itself.

## Reading output

A command's reply lands in `/tmp/cuolog` bracketed by `[CMD]` / `[CMD-END]` shortly after you
append it. To watch the game live — speech, container and shop events, combat — tail the log:

```bash
tail -f /tmp/cuolog
```

Lines that are not bracketed by `[CMD]` / `[CMD-END]` are packets arriving on their own schedule.
They are never attributable to a particular command, so don't try to pair them up: `[CMD-END]`
means the command handler finished, not that the server has finished reacting.

## Interactive use

`cli/navrey` with no arguments gives a `navrey>` prompt that streams the same output. The human
can use this at the same time as an agent — both go through the same daemon and the same character.
