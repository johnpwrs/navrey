---
name: uo-navigation
description: Walk the UO character to a target (x, y) or a named place, diagnose blocked paths, and travel long distances. Doors are opened automatically. Use whenever the user asks to walk/go/move/navigate/travel somewhere, when a goto reports "no path", or when the user mentions a moongate. Getting *inside* a dungeon, cave, mine or sewer, or up/down a staircase, is the `uo-dungeon-entry` skill instead - goto cannot make those transitions.
---

# Navigating

Requires a logged-in character (see the `uo-login` skill).

## Default: send `goto` directly. `safe_goto.py` is opt-in

**Every walk is a plain `goto` unless the user asks for the guarded walker, or a walk after
leaving town shows it is needed.** `goto` is fire-and-forget: append it, and the outcome lands in
`.nav` in the state file.

```bash
echo "goto <x> <y>" >> /tmp/cuocmd            # or: goto <poi>   |   goto <x> <y> avoid:X,Y[,R]
jq -c .nav /tmp/cuostate.json                 # idle | walking | arrived | nopath | stopped | failed
```

Wait for arrival with a one-shot `jq` between other work, or from a script via
`uo.goto(x, y).wait()` - never an inline loop in the session. Keep `autoheal.py` running as
always; it is what covers a walk that runs into something.

**Switch to `safe_goto.py` when one of these is true**, and stay on it for the rest of the trip:

- the user asked for it ("walk carefully", "use the safe walker", "watch for hostiles");
- the character took damage on a walk, or a walk ended in `stopped` with something hostile
  next to it - the ground past town is not indifferent to us, and the next leg should stop short
  of what hit us instead of finding out again;
- the destination is somewhere that has killed this character before (see the `uo-*` memory
  notes and `uo-combat-loop`'s hazard sections).

A character with new-player protection (Young status on shards that have it; the duration is
shard-specific) is the strongest case for the default: monsters do not aggro it, so every stop
`safe_goto.py` makes is for scenery. Measured 2026-09-10 at a hunting ground: three consecutive
`safe_goto.py` walks stopped at the 15-tile edge for hard-hitting creatures that never once turned
toward the protected character, and `--range 8` legs made no better progress. A direct `goto`
walked the same line into a spawn, none of which aggro'd. `You are no longer young` in the log
ends that protection.

## `safe_goto.py`: the guarded walker, when it is wanted

When the guarded walker is in use, `goto`/`travel` are not sent by hand and never driven by a
one-off inline wait loop. Every walk goes through:

```bash
python3 .claude/skills/uo-navigation/safe_goto.py <x> <y> &
python3 .claude/skills/uo-navigation/safe_goto.py bank &
python3 .claude/skills/uo-navigation/safe_goto.py "blacksmith" --range 15 &
```

Launch it in the background and wait for the completion notification: it exits as soon as the walk
resolves — arrived (0), or it can go no further (1). There is no run-length timeout; it runs until
the walk is settled.

**Every exit ends on one `RESUME:` line**, which is all you need to pick up where it left off:

```
RESUME: arrived at the destination; phase=arrived; at (<x>,<y>) hp=<n>/<max>; destination (<x>,<y>) - reached
RESUME: stopped short - 2 threatening creature(s) within 15 tiles: <name> at (<x>,<y>), ...;
        phase=walking; at (<x>,<y>) hp=<n>/<max>; destination (<x>,<y>) - 41 tiles short
```

It is emitted from a `finally`, so it is there for the exits the script never sees coming — the
client going away, the character becoming a ghost, SIGTERM from a takeover — as well as for its own
returns. Read that instead of polling `.nav`. The `HOSTILE STOP:` lines above it carry *why* it
stopped short — a hostile's name/serial/position/distance/notoriety.

Add `--watch` to keep it running after arrival as the standing aggro watch (see "It keeps watching
after arrival" below). That form never returns on its own, so background it and do not wait on it.

**`safe_goto.py` also takes `--avoid`/`--include`, the same avoid/allowlist flags as
`combat_movement_melee.py` (shared implementation in `cli/uo/avoidance.py`)** — name whatever
the walk ahead should route around and it stops/flees 15 tiles clear of it — the same `--range`
that governs an ordinary hostile, since one clearance number is easier to reason about than two —
independent of whether the mobile currently reads hostile:

```bash
python3 .claude/skills/uo-navigation/safe_goto.py <x> <y> --avoid species:<a> --avoid species:<b> &
```

A term matches a mobile's **name, species or body graphic**, and **must say which** — there is no
bare form, and `--avoid <word>` is a launch error naming the prefix to add:

- `species:<type>` — exact species; usually what you want, and it catches a creature of that type
  carrying a personal name, because `species` is the body graphic resolved to a word by the client
- `name:<name>` — exact name
- `graphic:<n>` / `graphic:0x<hex>` — exact body graphic
- `any:<word>` — substring of either name or species

`jq -c '.mobiles[] | {name, species, graphic}' /tmp/cuoworld.json` shows what is actually there to
match — worth checking, because name and species do disagree (a spawn can hold a creature the
server names `<name>` whose body resolves to a different `<species>`, e.g.
`{"name":"a <server name>","species":"<Type>"}`). See `uo-combat-loop`'s SKILL.md for the full
semantics, including why `--include` is the riskier of the two out in open country.

Logged as `AVOID STOP:`/`AVOID SEEN:` rather than `HOSTILE STOP:`/`HOSTILE SEEN:`, otherwise handled
identically — same stop-during-walk / flee-during-watch behavior, same `HOSTILE STOP` handling
instructions below apply to an `AVOID STOP` line too.

**Exception: while already in combat, skip `safe_goto.py` and send `goto` directly.** The whole
point of `safe_goto.py` is catching an *unexpected* hostile before a walk reaches it. Once a target
is actually engaged (`attack` sent, war mode on), being near a hostile is the intended state, not a
surprise — `safe_goto.py` would immediately trip on the very thing being fought, on the first poll.
This covers a reflex script's own tactical `goto` (kiting, pursuit, a flee point — see
`uo-combat-loop`) and the orchestrator's own direct pursuit `goto` while a fight is running. Once
the fight is over (target dead, disengaged, or fled clear), go back to routing every walk through
`safe_goto.py` again.

**Second exception: leaving a spawn you walked into on purpose.** Standing in a graveyard or a
dungeon you chose to grind, every mobile in view is a known quantity, not a surprise - and
`safe_goto.py` cannot tell the difference. Measured 2026-09-03 at a graveyard spawn: after the grind
was stopped, the walk to the bank halted at once ("3 threatening creature(s) within 15 tiles") on
the very creatures the character had just been farming, and lowering `--range` to squeeze
out is exactly what the section below forbids. Send `goto <dest>` directly to get clear of the spawn
- the character is at full HP, the reflexes have just been fighting these things, and the spawn is
the reason the walk is leaving rather than a reason to stop it. Once out of the spawn area (nothing
`knownMonster` in the world file), switch back to `safe_goto.py` for the rest of the trip, where a
hostile *would* be a surprise.

## Never weaken the ranges — assess the threat and route around it instead

**`--range` (15) and `--surprise-range` (5) are not tuning knobs. Do not lower them.** They are the
whole safety net, and the pressure to lower them is constant and plausible-sounding: on a long ride
the walk halts for perfectly harmless wildlife (a passive animal 12 tiles off the road stopped an
entire journey twice in one session), and dropping `--range` makes that stop happening. It also makes the
character walk much closer to the things that kill it.

Measured cost, 2026-08-31: the ranges were lowered to `--range 6 --surprise-range 3` to stop the
wildlife halts. The next walk noticed a hard-hitting caster at **d=5** instead of d=15, and because 5 was outside
the lowered `--surprise-range` it **stopped in place rather than retreating** — parking a
full-health character five tiles from a caster and handing the decision back at model latency. The
character was dead about ninety seconds later. At the defaults it would have stopped at 15 tiles
and actively retreated.

So when something blocks a route, the answer is never a smaller range. In order of preference:

1. **Route around it deterministically.** Both `safe_goto.py --avoid-tile X,Y[,R]` and the raw
   command's `avoid:X,Y[,R]` suffix are passed to the planner, which then never plans a path across
   that circle. This is the supported lever and it costs nothing elsewhere. Repeatable — pass one
   per known threat.
2. **Re-scan and pick a different approach vector.** `/tmp/cuoworld.json` gives you every threat's
   name and position for free. Plan legs that keep clearance from all of them rather than one long
   leg that happens to thread between two.
3. **Walk it in short legs with a scan between each**, so a threat that moves into the route is
   seen while there is still room to react.

**A deliberate override is a raw `goto`, and only for that one leg.** When you have looked at the
threat picture and decided to accept the risk — a corpse recovery with a caster nearby, say —
express that by sending `goto` yourself for that specific short leg, then go straight back to
`safe_goto.py` at its defaults. That keeps the override visible, bounded, and attached to a
decision you actually made, instead of silently disarming the safety net for every walk that
follows. Never encode "I accept the risk this once" as a lowered `--range`.

## `--watch`: keeping the aggro watch after arrival — there is no second script anymore

**The standing watch is part of the opt-in.** Under the default (direct `goto`, see the top of
this file) nothing runs a watch between fights; `autoheal.py` and the combat reflex's own HP check
are the cover. The rules below apply once the guarded walker is in use for a trip.


By default the script exits when the walk resolves, which leaves a gap between legs, after arrival,
and during any stretch walked by hand into somewhere dangerous (a spawn area, a dungeon mouth) —
nothing is looking at the world file, and that gap is exactly where a fight can start without
anyone noticing until the log fills with damage. A separate `aggro_monitor.py` used to fill it. It
has been retired: `--watch` makes `safe_goto.py` fall straight into the same watching behavior once
a walk resolves, instead of exiting, so there is exactly one process to launch or relaunch, not two
to keep in sync.

Use it whenever the character is going to sit somewhere exposed. Leave it off for an ordinary walk
whose outcome you want to wait on.

While watching, it does two things every poll, the same as while walking:

- **Proactively** flees anything matching `is_threat()` (Criminal/Enemy/Murderer notoriety, or a
  known-dangerous monster by body graphic even while still reading Gray) that comes within
  `--range` tiles — the exact same check the walking phase already runs, just still running once
  there's no walk in flight.
- **Reactively** flees an unexpected HP drop, in case something got adjacent or landed a hit before
  the proactive check caught it.

Both use the same `retreat` leg the walking phase uses — one implementation of "what does fleeing
mean," not two that can drift.

Under `--watch` it keeps watching (and keeps fleeing repeat trouble) until combat becomes genuinely
ready, at which point it steps aside for good (exit 0) — it does not resume watching once that
fight ends.

**"Ready" is two things together, not a flag anyone sets by hand:** a combat-movement script
(`combat_movement_melee.py` — see `uo-combat-loop`) running, *and*
`.lastAttack` set to a live target. Neither alone proves a fight was chosen on purpose — a live
`.lastAttack` can be the client's own auto-retaliate answering something that just ambushed the
character (the exact case this script exists to catch), and a combat-movement script is fine to
leave running idle between fights, so its mere presence proves nothing about this moment. But
nothing auto-starts a background script, so both being true at once only happens when this session
deliberately attacked something and then launched its support for it. The lock registry
(`uo/locks.py`) and `.lastAttack` are both already crash-safe for this (a held flock releases on
`kill -9`; a target either is or isn't tracked).

**Every fight needs its movement reflex running, melee included** — `combat_movement_melee.py`
exists specifically so melee pursuit isn't driven by hand, both because a hand-driven fight is
exactly the kind of thing `safe_goto.py`'s watch mode can't see as "ready" and because it is the
same reflex-vs-judgment split every other fight already follows (see `uo-combat-loop`). Launch it
before attacking.

**Standing rule: `safe_goto.py` is always running whenever no combat-movement script is up** —
before the first engagement of a trip, or any time one has been deliberately stopped, relaunch
`safe_goto.py` (toward the current spot, or wherever comes next) rather than leaving the character
unwatched. It notices "ready" within one poll and steps aside on its own once a combat-movement
script is launched — nothing needs manually stopping first. Concretely: after a kill, once picking
the next target is not immediate, or after intentionally stopping a combat-movement script for any
reason, relaunch `safe_goto.py` before doing anything else — `combat_movement_melee.py` also runs
its own unconditional HP check every iteration (see
`uo-combat-loop`), so a target-less idle period is no longer completely unwatched either, but
`safe_goto.py`'s watch mode is still what actually flees a threat that hasn't touched HP yet.

**Re-issuing a destination while it's already watching (or still walking somewhere else) is just
invoking it again.** A fresh `safe_goto.py` call always takes over from whatever instance is
currently running — no `--replace` needed, it's on by default for this script — the same clean
SIGTERM/unwind path `--replace` gives every other script.

**Why this exists**: a raw `goto` returns instantly and leaves the walk running server-side with
nobody watching it. The orchestrator used to fill that gap with an inline `sleep`/`until` loop,
which is worse than doing nothing — it blocks the orchestrator from responding to anything (the
user, a `stop`) for the walk's entire duration, and it still doesn't see what the character is
walking past, only whether it eventually arrived. That combination is exactly what got a character
killed once already: a walk toward a spawn area finished, and by the time anyone read the world file,
41 damage had already landed. `safe_goto.py` checks every poll instead of only at the end, and now
keeps checking after arrival too, rather than handing back a result and going silent.

**Handling the result:**

- **Exit 0** — combat became ready while watching; the orchestrator now owns the fight.
- **Exit 1, no `HOSTILE STOP` line** — the walk itself ended in `nopath`/`stopped`/`failed`; see
  "When it does not work" below.
- **Exit 1 with a `HOSTILE STOP` line** — a hostile came within range while the walk was still in
  progress. `safe_goto.py` has already sent `stop` (movement has actually halted, not just been
  asked to) and logged the hostile's name, serial, position, distance, and notoriety, plus where
  the character stopped (or, if it retreated — see below — where it ended up after that). **This
  is a decision for whoever is driving the walk, not something `safe_goto.py` resolves on its
  own** — it never picks a reroute or a new destination. Read the logged position/distance from its
  background stdout, decide whether to route around it (a different destination or waypoint) or
  handle it as a fight, and then call `safe_goto.py` again yourself once you've decided. Don't just
  re-run the same destination unchanged and hope — the hostile is very likely still there.
  - **`--range` (the stop threshold) defaults to 15 tiles**, not 10. Ten is too close against
    anything that hits hard: measured against a hard-hitting spawn, the walk stopped at d=9 and the
    character still lost over 40% of its HP before the handoff finished, because those hit for 10-19
    where the weak spawn this was tuned against hits for 1-5. Stopping only helps if it happens far
    enough out that stopping is what prevents contact. The world file carries 18 tiles, so 15 is
    about as early as this can see to react at all.
  - **A wide `--range` can stop the walk before it starts.** The check runs on the first poll, so
    if something threatening is already inside `--range` when you launch, it stops immediately and
    hands back without moving a tile - measured with a parked hostile at d=14 against `--range 15`,
    which made departure impossible. Two ways out: lower `--range` for that leg (8 is plenty past
    something that is not chasing), or step clear by hand first. This is the cost of the wider
    default, and it is worth paying while walking *toward* danger, not while leaving it.
  - **`--avoid-tile X,Y[,R]` routes the planner around a fixed obstruction**, passed through to
    `goto` as `avoid:X,Y[,R]`. Use it when a creature is parked in the only corridor: picking an
    intermediate waypoint by eye does not work there, because every route still comes back through
    the same gate. Repeatable, and distinct from `--avoid`, which matches a creature *name* this
    script watches for; `--avoid-tile` is a map coordinate the pathfinder never plans across.
  - **What happens after the stop depends on the creature**, in three cases:
    1. **Something is fighting us** (`is_aggro` - war mode within `--aggro-range` 12): the full
       escape - retreat, escalate, confirm, hand off. See below.
    2. **An avoided creature is in the way but not fighting us**: back off to `--stop-berth`
       (15 tiles) in one leg, then hand off. No escalation and no confirmation window, because
       nothing is coming for us - there is nothing to out-last. The point is not to leave the
       character parked just outside the reach of something it has been told never to meet, and
       then stop every mover while the orchestrator answers.
    3. **Anything else threatening**: stop, report, hand off. It is not coming for us and it is
       not on the avoid list, so distance is the orchestrator's call.
  - **After a retreat reports clear it keeps scanning for `--threat-clear-secs` (15s) before
    handing back**, and resumes retreating if anything reappears. Out of range is not the same as
    gave up: the world file carries only 18 tiles, so a pursuer that stepped past the edge of view
    looks identical to one that broke off. This is the same window `combat_movement_melee.py` uses
    to end an avoid-escape, and for the same reason.
  - **It retreats until genuinely clear, not for one attempt**, and escalates the clearance
    target each leg that fails to shed the pursuer (`--retreat-attempts` 7,
    `--retreat-escalation` 10, `--retreat-max-distance` 60) - the same escalation
    `combat_movement_melee.py` uses for an avoid-aggro escape. One leg then exit is what handed a
    relentless pursuer a stationary target: measured 2026-08-30, a fast pursuer was left **20 tiles**
    behind, `safe_goto` exited, and in the **13 seconds** before the orchestrator could react it
    walked the whole 20 tiles back onto the character; a second attempt then hit
    `--retreat-timeout` still being chased and exited again, and the character went from full HP
    to nearly dead.
    The distance was never the problem - handing back while something is still coming is handing
    back to the slowest actor in the system.
  - **It always retreats before returning**, whatever the distance was when the
    hostile was spotted. Halting in place is only safe against something that is not approaching,
    and nothing here can tell the difference — the world file carries no "is it coming for me"
    field. Returning stops every mover, and the next actor able to react is the orchestrator at
    model latency (~30s), so standing still beside an approaching hostile is the worst option
    available. Measured 2026-08-30: a fast pursuer spotted at **9 tiles** — outside `--surprise-range`,
    so the older code merely stopped and exited — closed to adjacent and took the character from
    full to under half HP with nothing steering. `--surprise-range` (default 5) now only distinguishes the
    wording, flagging that it was already on top of the character when first seen.
    The retreat uses `goto` toward a computed point beyond `--range` tiles, extending the
    line from the threat through the character (same idea as `uo-combat-loop` step 5's flee point),
    re-aimed every ~1.5s in case the threat moves — not single-tile `walk` stepping, which is both
    slower to issue and blind to obstacles a pathfound route would go around. The log shows a
    "retreating toward N+ tiles" line followed by either "fled clear"/"retreated clear" or
    "retreat timed out" (default 6s, `--retreat-timeout`) with whatever is still close. Either way
    this is still just distance, not evasion or a fight — the orchestrator still decides what
    happens next from there. Watch-mode behaves identically, and always has.
- **Still running, neither of the above** — the common case: still walking, or arrived and
  watching. Check `.nav.status` for the walking outcome; the process itself keeps running until one
  of the exits above.

There is no guard-zone exception to this check. Town guards intervene once a fight actually starts,
not before a walk gets close enough to start one, and there is no reliable client-side signal for
"is this ground guarded" to gate the check on even if there were a reason to want one (a real
system message exists for entering/leaving town guard protection — cliloc IDs 500112–500115 — but
it isn't sent on login, only on a status *change*, so it can't establish an initial guarded/
unguarded state at the start of a walk).

## What safe_goto.py is wrapping

Under the hood this is still the `goto`/`travel` commands the client always had — useful to
understand for diagnosing an unexpected stop, even though you should not be sending them directly.

**`goto` starts a walk and returns immediately.** It reports that the walk began, never whether it
arrived. This is deliberate: the command worker is serial, so a blocking walk would stall every
other command for its whole duration. The outcome lands in the `nav` block of
`/tmp/cuostate.json`:

```json
{"active": true, "status": "walking", "target": [<x>,<y>], "reason": null}
```

`status` is one of `idle`, `walking`, `arrived`, `nopath`, `stopped`, `failed`. `reason` carries the
planner's explanation when it isn't `arrived`. This is exactly the field `safe_goto.py` polls so
you don't have to.

`goto` handles the awkward parts itself:

- **It settles for the closest reachable tile.** A POI coordinate usually marks a building, not a
  floor tile you can stand on, and someone may be standing where you aimed. `goto` walks as close
  as it can and tells you where it stopped. Use `gotoexact` only when the precise tile matters.
- **It opens doors.** Routes are planned *through* doors deliberately and opened on approach —
  including walking out of a building you started inside.
- **It splits long journeys into legs** automatically, so there is no separate long-distance
  command to remember. `travel` still exists and does the same thing — `safe_goto.py --command
  travel` if you specifically want it.
- **It runs rather than walks**, and drops to walking on its own when stamina runs out.
- **It re-plans when the server refuses a step**, remembering the tile so it routes around it.

**One walk at a time, newest wins.** A second movement command cancels the first rather than
queueing behind it — a character cannot walk two places at once. This is what makes `safe_goto.py`'s
own implicit takeover work (a fresh invocation's `goto` simply supersedes whatever the previous
instance had running), and it also means something else issuing `goto` concurrently — a combat
reflex script's own kiting/pursuit `goto`, or a raw `stop` — wins over whatever `safe_goto.py` had
queued.

## Finding a target first

Only needed when you want to inspect before committing, or the target is something that moves.
These are plain reads, not walks, so they're fine to send directly:

```bash
echo "closest healer" >> /tmp/cuocmd          # nearest POI of a category - use this for "the closest X"
echo 'findpoi "<city> bank"' >> /tmp/cuocmd   # a specific, named POI you already know by name
echo "poi" >> /tmp/cuocmd                     # what categories exist
sleep 1
tail -10 /tmp/cuolog
```

These answer different questions - pick by what you actually know:

- **`closest <category>`** — "find me the nearest healer/bank/tailor/etc, wherever it is." Compares
  every POI in that category by distance and returns the true nearest one. Use this whenever the
  ask is "closest" / "nearest".
- **`findpoi <text>`** — "take me to *this specific place*", e.g. `"<city> bank"` or `"<city>
  blacksmith"`. It's a free-text name search (matched against each POI's name, not distance), so if
  the query is generic enough to match several same-category POIs (e.g. just `findpoi tailor` when
  multiple tailors exist), it isn't guaranteed to return the nearest one — it may return a
  same-scoring match anywhere on the map. For "nearest of a category", use `closest`, not `findpoi`.

Both are facet-aware. `closest` answers only for the facet you are on (`map` in the state file:
0 Felucca, 1 Trammel, 2 Ilshenar, 3 Malas, 4 Tokuno, 5 Ter Mur). `findpoi` searches every facet,
prefers yours on a tie, and flags a match elsewhere as `on another facet ... not walkable from
here` - no walk can cross facets, so that line means a moongate trip first (`uo-moongate`, section
4b). `goto <name>` refuses a place on another facet with the same message instead of settling for a
lookalike on this one.

POI data covers static landmarks only — shops, banks, moongates, dungeon entrances. For anything
that moves, scan live:

```bash
# both live in the world file - no command, no round trip, safe to poll tightly
jq -c '.mobiles[] | {serial, name, x, y, z, distance, notoriety}' /tmp/cuoworld.json
jq -c '.items[]   | {serial, name, x, y, z, distance}'            /tmp/cuoworld.json
```

For a mobile, aim at a tile *next to* it rather than the tile it occupies.

## When it does not work

```bash
printf 'canwalk\ncanwalk <x> <y>\ntiles <x> <y>\npath <x> <y>\n' >> /tmp/cuocmd
sleep 1
tail -20 /tmp/cuolog
```

Read the output in this order:

1. `No path from (a, b) to (x, y)` — nothing walkable connects them at all. Check `canwalk` on the
   destination; if the tile itself is unusable, that is expected and `goto` will already have
   settled nearby. If `goto` moved nowhere, you are probably sealed in — check whether a door is
   involved, and that `canwalk` on your own tile shows at least one open direction.
2. `Blocked at (x, y) - re-planning` — normal. The planner reads map and static data; the server
   also enforces things it cannot see. A handful of these per journey is healthy.
3. `Stopped at (x, y) - closest reachable` — it got as near as the map allows. Usually the answer,
   not a failure.
4. `Giving up at (x, y)` — 25 consecutive refused steps. Something is genuinely wedged: check
   `canwalk`, plus the state file for where you actually are and whether the character is a
   ghost or frozen — `uo.xy`, `uo.direction`, `uo.ghost`, `uo.paralyzed`.

## Dungeon entrances, and stairs: use the `uo-dungeon-entry` skill

**A dungeon entrance is not a door, a gump, or an object you double-click.** It is a spot that
teleports you when you step onto it, and **nothing in the tile data marks it**. None of the usual
tools find it: nothing for `items` to list, nothing for `use` to open, and **`goto` can never take
you in** — it plans through `GridPathfinder`, which correctly refuses an impassable tile, so it
settles for the closest reachable tile and reports `arrived` while the character stands outside.

**Hand this to `uo-dungeon-entry`.** It locates the entrance tile positively — scanning for the
`Surface, Bridge, Stair*` static that draws the ladder or cave mouth, then screenshotting the game
window to see which one the character is actually beside — and steps onto it. The same skill covers
getting back out, and the entry gump that silently swallows the next `walk`.

The same applies at the top or bottom of a **staircase**: a stair transition is the identical kind
of spot, found the same way.

**Do not step blindly into the rock.** Taking a few `walk` steps in the facing direction and hoping
the transition fires is guesswork: it only works when you are already on the correct tile facing the
correct way, it fails silently when you are not, and at the Britain sewers (measured 2026-09-13) the
POI returns the *hole* — impassable — while the real entrance was one of **four** candidate stair
statics around it, indistinguishable without looking at the screen. Stepping further into the map on
a miss just walks the character somewhere it did not mean to go.

**Confirm by the coordinate jump, because nothing announces it.** A successful transition lands the
character on dungeon-map coordinates, far outside overland range (Britain sewers: 6032, 1499, z=31).

### Do not hunt for the entrance in the POI index

The `down`, `up`, `stairs` and `teleporter` categories describe the *far side* of a transition, at
**dungeon-map coordinates**, so querying them from outside returns something that looks absurdly far
away and is not somewhere you can walk:

```
closest down       ->  [POI] <dungeon name> (down) at (<dx>, <dy>) - 2850 tiles away
closest teleporter ->  [POI] gate to mainland (teleporter) at (<tx>, <ty>) - 995 tiles away
```

`(<dx>, <dy>)` is not a place on the overland map — it is where you *come out* inside the dungeon,
and it matched the arrival tile above to within the room. Read a four-digit coordinate in the 5000-7000
range as "dungeon-side", not "far away"; chasing it wastes a trip.

### Once inside, shrink the ranges

`--range` 15 is sized for open ground. A dungeon corridor is often narrower than that, so a walk
that stops 15 tiles short of anything threatening never gets anywhere, and the combat reflex's own
berths (`--avoid-range` 5, `--avoid-idle-range` 2) can exceed the width of the room. The character
ends up walking constantly and landing nothing - measured underground. Underground, run
`safe_goto --range 6` to `8`, and give the melee script `--avoid-range 3 --avoid-idle-range 1`.
Less warning is the deliberate trade for being able to fight at all.

### Once inside

Scan before committing — `jq -c '[.mobiles[]|{n:.name,d:.distance}]' /tmp/cuoworld.json` on the
first tile — and blocklist the dungeon's own hazards before launching a combat reflex, since they
differ from the overland ones. Blocklist by the names you actually read off that first scan -
especially anything that poisons, which is the failure mode for a bandage-only healer. You are also a long way from a
healer now, so a death costs far more than the ten minutes it costs near town
(`uo-death-recovery`).

## Long distance

`goto` already chains legs, so `safe_goto.py` handles a long trip the same way as a short one —
launch it in the background and poll `.nav.status`. Moongates are POIs like anything else:

```bash
python3 .claude/skills/uo-navigation/safe_goto.py moongate &
echo "say vas rel por" >> /tmp/cuocmd    # once .nav.status reads arrived (or use the gate directly)
```

Another facet is never a walk, however long: the destination gump at the gate is the only way
across, and on a multi-facet shard it is a tabbed gump with radio destinations - see `uo-moongate`
section 4b for reading it and for the `gump:<serial>` form that keeps a press off the wrong gump.

## Mounts: war mode silently turns "mount" into "attack"

A mount is ridden by double-clicking it (`use <serial>`) while standing on or beside it. The trap is
that **`use` on a mobile means "attack" when war mode is on**, so the exact same command either
mounts the animal or swings at it depending on a flag nothing in the reply mentions:

```
[CMD] use 0x0005C141
Used 0x0005C141
[CMD-END] use 0x0005C141 ok      <- identical output in both cases
```

Measured live at a stable: `use` returned `ok`, `isMounted` stayed `false`, and the
attack attempt left a **target cursor open** (visible in the game window as a targeting flag with a
cancel option). Nothing in the log said "you cannot attack that" or "you are in war mode".

So the sequence is three commands, not one:

```bash
echo "war" >> /tmp/cuocmd           # toggle war mode OFF - check .warMode, do not assume
echo "canceltarget" >> /tmp/cuocmd  # clear the cursor a prior attack attempt opened
echo "use <mount_serial>" >> /tmp/cuocmd
```

**Confirm with `.isMounted` in the state file, never with the exit code** - `ok` is what a failed
mount looks like too. Check `.warMode` first rather than assuming it is off: a combat script that
handed off or was killed mid-fight leaves war mode set, which is exactly the situation in which
someone walks to a stable and buys a mount.

### Finding your own mount again: `isPet` in the world file

`followers` in the state file is a **count**, not a list - it says a pet exists, not which creature
it is. The per-mobile `isPet` flag in `/tmp/cuoworld.json` is what identifies it:

```bash
jq -c '[.mobiles[]|select(.isPet)|{name,serial,distance}]' /tmp/cuoworld.json
```

Verified outside a stable with several other animals and the vendor in view, where exactly one
mobile reported `isPet: true` and its serial matched the <mount> we had just bought. It comes
from the server's rename flag, which is set for creatures under our
control and nothing else.

**It does not work while mounted.** A ridden mount is merged into the player as a Mount-layer item
and does not appear in the world file's `mobiles` at all, so `isPet` has nothing to match against - `isMounted` is
the only signal then. Dismount first if the goal is to identify or re-find the animal.

A leftover cursor is worth cancelling on its own even when not mounting - it is answered by the next
`target`-consuming action, so it can silently absorb an unrelated command later.
