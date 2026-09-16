# Navrey — an agent-driven ClassicUO fork

A fork of the [ClassicUO](https://github.com/ClassicUO/ClassicUO) Ultima Online client with a CLI
command engine hosted **inside the client process**, so a shell or an agent drives the same
character the game window does. One process, one connection, one character.

## Build and run

```bash
~/.dotnet/dotnet build src/ClassicUO.Client/ClassicUO.Client.csproj
~/.dotnet/dotnet build cli/Navrey.Cli.csproj        # the navrey front end

cli/navrey              # start client (window up) + navrey> prompt
cli/navrey --headless   # same, no window
cli/navrey --attach     # attach to a client already running
```

**There is one way to configure the client.** `settings.json` at the repo root carries the shard
host and port, client version, UO data directory (`uo_directory`), POI directory
(`poi_directory`) and shard/character selection. `.env` at the repo root (gitignored; copy
`.env.example`) carries only `UO_USER` and `UO_PASS`. No command-line flag and no other env key
overrides any of it — the `-ip`/`-port`/`-username`/`-password`/`-uopath`/`-clientversion`/
`-settings`/`-envfile` flags and the `UO_HOST`/`UO_MAP_DIR`/`UO_POI_DIR` keys were removed on
purpose, after a stray override kept beating the tracked settings. Change the file, not the launch.
The client never writes `settings.json` back (`Settings.Save()` is a no-op): edit it by hand, or
when asked to. POI data loads from its `poi_directory` only - nothing ships in the repo; the
`uo-poi-data` skill builds a shard's set.

`cli/navrey --no-autologin` stops at the login screen for a manual session. Starting `cli/navrey` twice
attaches rather than launching a rival client. **To restart onto a new build, stop it by pid:**
`kill $(cat /tmp/cuocmd.pid)` and confirm with `pgrep -f bin/Debug/net10.0/cuo` before launching
again. The launcher passes `-cmdfile … -logfile …` before `-agent`, so a `pkill -f "cuo -agent"`
matches nothing, and the next `cli/navrey` then attaches to the old build and reports login "done"
off the stale log (measured 2026-09-11: a new command was missing for a whole cycle). Startup failures (wrong `client_version`, wrong data
directory, bad `.env`) leave a plain message in `/tmp/cuostdout.log` and `/tmp/cuolog`.

To start the client without the launcher, run it from the repo root (both config files are read
from the working directory):

```bash
~/.dotnet/dotnet run --project src/ClassicUO.Client/ClassicUO.Client.csproj -- -agent   # or -headless
```

| Flag | Meaning |
|---|---|
| `-agent` | Host the command engine and its file daemon |
| `-headless` | As `-agent`, plus start with the window hidden |
| `-cmdfile` / `-logfile` / `-statefile` / `-worldfile <path>` | Override the four files below |

**`navrey` is a separate binary and does not rebuild with the client.** If commands start failing
in ways that look like a protocol mismatch, rebuild it — a stale `navrey` against a current client
has already caused exactly that.

## The three files

```
/tmp/cuocmd          commands in    (append a line)
/tmp/cuolog          narration out  (tail it)
/tmp/cuostate.json   player state   (jq it)
/tmp/cuoworld.json   nearby mobiles + movable ground items
```

Overridable with `-cmdfile` / `-logfile` / `-statefile` / `-worldfile`. Running two clients at once
needs all of them overridden, or they fight over one character and stomp each other's files.

## Reading state: prefer the file over a command

`/tmp/cuostate.json` is rewritten whenever anything changes, and at least every 250 ms. Everything
about the *local character* is in it — position, direction, map, hit points, stamina, mana, stats,
resistances, weight, gold, followers, the status booleans (`charGhost`, `isHidden`, `isPoisoned`,
`warMode`, `isMounted`, `isParalyzed`, …), `equipped` and `backpack` contents, plus `lastAttack` /
`lastCursorTarget` and `nav`.

`/tmp/cuoworld.json` carries what is around you: `mobiles` in view and `items` on the ground.
Ground items are filtered to movable ones — a town has ~125 tracked items and almost all of it is
bulletin boards and barrels no script will act on, so this is the `items` command's `[take]` subset.
A mobile that walks out of view disappears rather than going stale; `lastAttack`/`lastCursorTarget` give
continuity for your actual target via `exists: false`.

Each mobile carries **both** identities: `name` is whatever the server sent, `species` is its body
`graphic` resolved through the client's own table (`Capabilities/MobNames.cs`). They differ exactly
when it matters — a monster with a personal name has `{"name":"Gruuk","species":"Orc"}`, and only
`species` says what it is. Filter on it rather than re-deriving type from a name; `species` is
`null` for a graphic the table doesn't list (it stops at 403 and has gaps), which is the cue to add
a line there rather than to guess.

`backpack` is empty until the backpack has been opened once this session — `backpackOpened` says
which, so an empty list is never mistaken for "nothing there".

```bash
jq .charPosX /tmp/cuostate.json
jq -c '{hp:.hits, max:.maxHits, ghost:.charGhost, war:.warMode}' /tmp/cuostate.json
jq -c '.lastAttack | {name,x,y,hits,exists}' /tmp/cuostate.json
```

Do **not** reach for `pos` / `status` / `dead` and parse the log. That is a round trip through the
200 ms command-file poll, the game thread and the log, it occupies the single serialized command
slot (so it queues behind a running `goto`), and it hands back English. Reading the file costs
none of that and is safe to poll tightly.

Writes land via a rename, so a reader never sees a half-written document and never needs to retry.

**Liveness is `updatedAtMs`, not `inGame`.** A clean exit or a `kill` leaves
`{"updatedAtMs":…,"inGame":false}`, but nothing can run on `kill -9` or a crash — a file still
saying `inGame: true` may belong to a client that is gone. Compare the timestamp to now before
trusting it.

Commands remain the way to *do* things, and to read what the file does not carry: nearby mobiles,
container contents, vendor stock, line of sight.

## Walking is fire and forget

`goto` / `gotoexact` / `travel` start a walk on their own thread and return immediately — they
report that it *began*, never whether it arrived. The command worker is serial, so a blocking walk
used to stall everything else for its whole duration and nothing could act mid-journey.

The outcome is in `.nav`: `{active, status, target, reason}`, where `status` is `idle` | `walking` |
`arrived` | `nopath` | `stopped` | `failed`. Wait for arrival by polling `.nav.active`; do not size
a `--timeout` for it, and do not read `goto`'s exit code as the arrival result. One walk at a time,
newest wins — re-issuing `goto` is how you chase a moving target.

**By default, send `goto` directly and read `.nav` for the outcome.** The guarded walker,
`.claude/skills/uo-navigation/safe_goto.py`, is opt-in: use it when the user asks for it, or when a
walk after leaving town shows it is needed (the character took damage, or a walk stopped next to
something hostile) - then stay on it for the rest of the trip. Launched in the background and
waited on, it sends the command and watches `/tmp/cuoworld.json` every poll, stopping the walk
the moment a hostile comes within range. Never wait on `.nav.active` with an inline loop either
way - a one-shot `jq`, or `uo.goto(x, y).wait()` from a script.

**It exits as soon as the walk resolves** — arrived (0), or established it can go no further (1) —
and every exit ends on one `RESUME:` line carrying the outcome, where the character actually is,
its HP, and how far short of the destination it stopped. That line comes from a `finally`, so a
run killed by a takeover or ended by the client going away still reports it. Read that last line
rather than polling `.nav`; the `HOSTILE STOP:` lines above it carry the detail behind a stop.

Pass `--watch` to keep it running after arrival as the standing aggro watch — proactively for a
threat closing to range, reactively for an unexpected HP drop — filling the same gap a separate
`aggro_monitor.py` used to (now retired), until a combat script is genuinely fighting something, at
which point it steps aside. A `--watch` run never returns on its own, so background that form and
do not wait on it. See `uo-navigation`'s `SKILL.md` for the full lifecycle, exit codes, and how to
handle a hostile-triggered stop.

**While already in combat, it is always a direct `goto`.** `safe_goto.py`'s whole job is
catching an *unexpected* hostile before the walk reaches it — once a target is actually engaged
(`attack` sent, war mode on), being near a hostile is the intended state, not a surprise to flag, and
`safe_goto.py` would immediately trip on the very thing being fought. This covers both a reflex
script's own tactical `goto` (kiting, pursuit, a flee point — see `uo-combat-loop`) and the
orchestrator's own direct `goto` calls for melee pursuit while a fight is already running.

## Python: the `uo` library

For anything beyond a one-off command, drive the client from Python rather than bash:

```python
import sys; sys.path.insert(0, "cli")
from uo import Client, Event

uo = Client()
uo.require_live()                      # updatedAtMs age, not inGame

uo.hp, uo.max_hp, uo.xy, uo.ghost      # ~4us each - read as often as you like
uo.goto(1425, 1690).wait()             # fire-and-forget, or block for the outcome
uo.mobiles(within=10, hostile=True)    # from the world file, no round trip
uo.use_on(uo.find("bandage"), "self")  # waits for the server's target cursor
uo.move(item, bank_serial)             # confirms arrival, not departure
```

| Operation | Cost |
|---|---|
| state / world read | ~0.07 ms, mtime-gated so unchanged files cost a `stat` |
| send an action | ~0.01 ms, does not wait |
| `uo.call()` synchronous query | ~3 ms |

**The library is mechanism only.** Reading state, sending actions, decoding events, bounded waits.
Loops and policy live in scripts that import it — see
`.claude/skills/uo-combat-loop/autoheal.py`. "Heal whenever I drop below 90%" is an opinion about
how to play; putting it in the library would make every script inherit it and put a `while True:`
inside an imported module.

## Which tool for which job

| Doing | Use |
|---|---|
| A one-off action (equip, say, drop, toggling war) | `echo "war" >> /tmp/cuocmd` |
| Walking anywhere | `echo "goto <x> <y>" >> /tmp/cuocmd`, then read `.nav`; `safe_goto.py` (backgrounded) only when asked for or once a walk shows it is needed |
| Reading own state or surroundings | `jq` the state / world file |
| Reading what is not in a file (`shop`, `gumps`, `los`, `tiles`) | append the command, then `tail`/`grep` `/tmp/cuolog` for its `[CMD]`/`[CMD-END]` block |
| Anything that loops | a `.py` script importing `uo`, run via the Bash tool's `run_in_background` — never `nohup … &` |

**Never invoke `navrey <cmd>` directly** — not from Python, not from bash, not even for a command
whose output you need. It spawns a .NET process — measured at 50–100 ms — to do what appending a
line does in ~0.01 ms. Appending and then reading the reply out of `/tmp/cuolog` costs nothing
extra over invoking it directly and works the same for actions and reads alike. Confirm actions by
reading state, not by an exit code — `navrey`'s exit code is a weak signal regardless: a command
that found nothing still reports `ok`, because only a hard failure sets the error flag.

**Never run a loop inline** (`python3 - <<'PY'` with a `while`, a bash `until` poll, a "watch it for
30 seconds" monitor). It blocks the session for its whole duration, so nothing can be checked or
reacted to while it runs, and a hang leaves nothing to inspect. Put loops in a file and run them in
the background; several can run at once, since the client serialises commands itself.

**"In the background" means the Bash tool's `run_in_background`, never `nohup … &`.** Run the
script in the *foreground* of that call — no `nohup`, no trailing `&`. The harness tracks the *Bash
invocation*, not what it spawns: `nohup … &` returns immediately, so the tracked task completes in
seconds while the reflex runs on unwatched. It never appears in the background-task list, its
output is not streamed, and it cannot be stopped from there — `ps` shows it reparented to `PPID 1`.

**The real cost is the handoff.** A reflex hands a decision back by *exiting* — `@script` returns 1
with the `HANDOFF:` or empty-ground line as its last words. A tracked process exiting re-invokes
you automatically, carrying that line; a detached one leaves it in a file you have to remember to
poll. Measured: three handoffs in a row (`nothing to fight within 10 tiles …`, `HANDOFF: lost a
shade …`) went unnoticed for minutes each because the loop was detached. Do not wrap a reflex in a
bash relauncher either — that consumes the exit and re-runs the script, turning the decision point
into a silent retry loop and swallowing the reason entirely.

Only exits notify. Mid-run lines — `escape (swarm)`, `HOSTILE STOP:` — do not, so watch those with
the Monitor tool or a one-shot read while a fight is live.

The tradeoff: a tracked process dies with the session, a detached one survives it. For a reflex
whose whole purpose is to hand a decision back, that is not a loss worth having.

**This covers throwaway diagnostics, not just scripts you intend to keep.** A monitoring loop is
the most tempting case and the worst one: it exists precisely because something needs watching, and
running it inline is what makes you unable to act on what you see. Measured cost — a 70-second
inline watcher during a live fight, while the character failed to approach its target for the whole
70 seconds, invisible until someone else pointed it out. Write it to the scratchpad, background it,
and check on it with one-shot `jq` and short `python3 -c` reads.

**Every looping script is wrapped in `@script`.** Not a convention — it is what makes a second copy
refuse to start:

```python
from uo import script

@script(ap)                          # owns the lock, the client, the exit code
def main(uo, args):
    for _ in uo.every(0.3):          # owns the cadence, and the exits nobody chooses:
        ...                          #   client gone, character dead, --for / --iterations
        if out_of_bandages:
            return uo.stop("restock and relaunch")
```

Two copies of one reflex is the failure this exists to prevent, and it never looks like a duplicate
process: two healers both bandage the same wound and the second answers the cursor the first opened;
two pursuit loops each re-issue `goto`, which cancels the other's walk, so the character stutters in
place and it reads as bad pathing. The lock is `flock`-based, so the kernel releases it even on
`kill -9` — there is no stale lock to clear.

```bash
python3 cli/uo/locks.py           # what is running right now
python3 …/autoheal.py --replace        # take over from the copy already running
```

Exit codes are uniform: `0` finished, `1` stopped on a reported condition, `2` no live client,
`3` already running, `130` Ctrl-C, `143` terminated.

Every `@script` gets these flags for free: `--replace` (SIGTERM the running copy and take over),
`--every <secs>` (override the cadence), `--for <secs>` and `--iterations <n>` (bounded runs),
`--name` (the lock name, default the filename), `--verbose`, and `--cmdfile` / `--logfile` /
`--statefile` / `--worldfile` (drive a client whose files were overridden). `uo.every(on_ghost="run")` keeps a loop alive through death for recovery
scripts; the default stops it.

## Commands

`cli/navrey help` lists every command with a one-line summary. To run one, append it to
`/tmp/cuocmd` and read the reply back out of `/tmp/cuolog`. What the one-liners leave out:

| Command | Behaviour worth knowing |
|---|---|
| `goto` / `gotoexact` / `travel` | Start a walk and return at once. `goto` opens doors, runs, splits long trips into legs, re-plans around refused steps, and settles for the closest reachable tile; `gotoexact` fails instead of settling. `avoid:<x>,<y>[,<r>]` keeps the planner out of a circle. Outcome is in `.nav`, never in the reply. |
| `stop` | Cancels the walk or long-running command and **jumps the serial queue** - the one command that does. |
| `closest` / `findpoi` / `poi` | Same-facet only; a place on another facet is reported as such, not as missing. Data comes from `poi_directory` and nowhere else. |
| `canwalk` / `tiles` / `path` | Diagnose a refused walk: why a tile is blocked, what is on it, and the route the planner would take without walking it. `path` prints the same `[PERF] plan` numbers a real walk would. |
| `los` / `debuglos` | Line of sight per mobile, or tile by tile to a point. The client has no LOS of its own; archery needs this. |
| `attacknearest` | Nearest hostile **with line of sight**, never a blue Innocent. |
| `hp <serial>` | Primes a mobile's HP; until then the state file's number for it is a placeholder. |
| `read <serial>` | Narrates a book as `[BOOK]` lines: a header with title, author and page count, then `[BOOK] 0x.. page N:` with indented lines or `(blank)`. The book gump is client-side, so this is the only way a script can read one. |
| `gear <serial\|self>` | Worn items with hues plus the paperdoll title. The title needs `use <serial>` first to open the paperdoll. |
| `use` / `target` | `use` opens a target cursor about 33 ms later; a `target` sent before it arrives is discarded with "nothing is asking for a target" and the cursor stays open until it times out. `uo.use_on()` waits for it; by hand, wait between them. |
| `drop` / `get` | Confirm by listing the **destination**, not the source: an item is briefly absent from the source in transit, so a move that bounces back still looks like it left. |
| `gumps` / `gumpresponse` / `contextmenu` / `contextpick` / `prompt` | Server gumps, right-click menus and text prompts; everything in the game that is a dialog rather than an action goes through these. |
| `state` / `world` | The state file's path and current contents; login state, map and entity counts. |

`[CMD-END]` means **the handler returned**, not that the server has finished reacting. Everything
outside the `[CMD]` / `[CMD-END]` brackets is packet narration arriving on its own schedule and is
deliberately not attributed to a command — a packet may be a consequence of something the CLI did,
something done in the game window, or neither.

**Targets.** The state file carries the client's two target fields, unchanged in meaning from
any UO client: `lastAttack` is the last mobile attacked, whoever started it (CLI or game window);
`lastCursorTarget` is the last thing a *targeting cursor* was pointed at, so it stays `none` in a
session that only attacks, and a mage's fight target lives here rather than in `lastAttack`.
Positions are resolved from the world every frame, so they follow a moving mobile. `exists: false` with null
coordinates means the target left view; `kind: "none"` means nothing was ever targeted.

**`[PERF]` lines.** A quiet log has none. The agent's game-thread work is timed per frame, and a
frame where it crosses 8 ms gets one line (at most one a second):

```
[PERF] frame agent=9.7ms residency=0.0 dispatch=9.7 (max PathGrid.Fill 9.7ms) state=0.0 world=0.0
[PERF] plan 270ms nodes=60000 fills=238 ended=nodes
[NAV] goto 1425 1690 -> arrived plans=3 nodes=812 planMs=4 maxPlanMs=2
```

`dispatch` is command work marshalled onto the game thread and `max` names the slowest piece.
Route search runs on the walk's own thread: `planMs` is its time, `fills` is how often it waited
on the game thread for map chunk copies (made once per session, then kept), and `ended` is `goal`,
`exhausted` (every reachable tile explored - a real dead end), `nodes` (budget of 60,000 hit, the
fenced-pen signature) or `cancelled` (superseded mid-plan). Items, mobiles and house pieces are
snapshotted per plan from the world's own lists, so a plan over already-copied ground costs the
game thread well under a millisecond.

## Layout

The agent layer is `src/ClassicUO.Client/Agent/`:

| | |
|---|---|
| `AgentHost` / `GameDispatcher` (root) | Process lifecycle: the worker thread runs commands; every touch of the world or socket is marshalled onto the game thread. `AgentLock`, `AgentSettings` and `HeadlessWindow` sit alongside |
| `Commands/` | The command table (`CommandEngine` and its partials) and `CommandContext`, what a handler is given |
| `Files/` | The file protocol: `Daemon` tails `/tmp/cuocmd`; `Output` is the single log writer and `Narrator` feeds it by wrapping packet handlers at runtime; `StateFile` / `WorldFile` publish the JSON snapshots through `JsonFileWriter`,  |
| `Text/` | Naming and formatting shared by commands and files: `Describe`, `ItemProps`, `Directions` |
| `Capabilities/Movement/` | Route planning and walking: `Navigator` (the walk thread), `Navigation`, `GridPathfinder`, `PathGrid`, `MapResidency`, `LineOfSight` |
| `Capabilities/Poi/` | Point-of-interest data and its lookups |
| `Capabilities/` | `MobNames` and `VendorStock`, our own tables kept apart from ClassicUO's |

Operational guides live in `.claude/skills/uo-*`. The full command list is `cli/navrey help`; the
root `README.md` is written for players, not developers, and carries none of the internals above.

## Conventions that matter

**Keep changes inside `Agent/`.** ClassicUO proper is touched in seven places only (`Main.cs`,
`GameController.cs`, `Client.cs`, `CUOEnviroment.cs`, one accessor in `PacketHandlers.cs`, the
`poi_directory` property and the no-op `Save()` in `Configuration/Settings.cs`, one csproj entry). Nothing in ClassicUO references `Agent.Capabilities`. New agent features should add
zero engine touchpoints — `AgentHost.PumpGameThread()` is already called every frame, and
`AgentSettings.ApplyDotEnv` already pre-scans argv, so a new per-frame feature or flag needs
neither a new hook nor a `CUOEnviroment` field.

**Only the game thread may touch `NetClient` or `World`.** `NetClient.Send` encrypts with
streaming, order-dependent cipher state outside its own lock, so an off-thread send silently
corrupts the connection. Command handlers reach the game only through `ctx.Game(w => …)`.

**Movement goes through `PlayerMobile.Walk`.** It owns the walk sequence byte, the pending-step
ring and the step throttle, all of which the server validates. `Capabilities/Movement/GridPathfinder` plans
its own routes (it treats doors as passable) but execution is still one step at a time through
`Walk`, so the CLI moves the character exactly the way the game window does.

**Don't change `Output` line shapes.** The `uo-*` skills grep for them (`*** Entered the world!
***`, `[CONTAINER]`, `[SHOP]`, `[SKILL]`, `[BOOK]`, `Swing:`).

**Prefer observing over instrumenting.** `Narrator` wraps packet handlers at runtime rather than
editing them, and the state file reads `TargetManager` per frame rather than hooking each CLI
command — which also catches actions taken in the game window.

**`PublishAot=true`.** No reflection-based JSON. Use a source-generated `JsonSerializerContext` or
write with `Utf8JsonWriter` directly, as `StateFile` does.

## Gotchas

- The command worker is **strictly serial** — one command at a time. A long `goto` blocks every
  later command except `stop`.
- `Output` truncates the log on every client start, so long-lived readers must handle the length
  going backwards.
- Mobile HP is a placeholder until the server sends a status for it; `hp <serial>` primes it. An
  unchanging target HP usually means your swings aren't landing, not that the reading is stale.
- Container contents are empty until the container has been double-clicked open once this session.
  An empty `inv` backpack listing is not evidence that gear was lost.
