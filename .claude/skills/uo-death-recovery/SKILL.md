---
name: uo-death-recovery
description: What to do when the headless UO character dies — detecting ghost state, getting resurrected via a healer, and checking equipment afterward. Use whenever the user asks about death, being a ghost, resurrection, or the character shows HP:0.
---

# Handling character death and resurrection

Requires a logged-in character with the map loaded (see the `uo-login` skill).

**This is a bounded few-step sequence, not a loop — drive it with plain `bash`/`jq` commands per
this file, the same as any other one-off interaction, not a `.py` script run in the background.**
Every command below already exists (`goto`, `gumps`, `gumpresponse`, `inv`), and the rest is a
world-file read; nothing
here needs a script's cadence, lock, or exit-code handling. Writing one adds a race the raw
commands don't have on their own — a script that calls `uo.goto()` and immediately checks
`uo.nav.active` can read the _previous_ walk's leftover "arrived" status before the new walk has
registered, exits its polling loop instantly, and reports the walk as done when it has barely
started (observed live: a script printed "arrived" a few seconds into a 151-tile walk that was
still very much in progress). Sending each command and reading the reply/state file back — the
`uo-combat-loop`/`uo-navigation` "one-off actions stay in bash" convention — sidesteps this
entirely, because there is no loop to race against.

## 1. Detect death

Read it straight from the state file — no command, no sleep, no log parsing:

```python
import sys; sys.path.insert(0, "cli")
from uo import Client, Event
uo = Client(); uo.require_live()

uo.ghost       # True = ghost
```

`charGhost` is the client's own `IsDead`, which is a better signal than `HP:0`:
it is set from the ghost body graphic, so it is true even in the moment before a
status packet has updated hit points. `.hits` and `.maxHits` are in the same file
if you want the numbers.

The file is rewritten whenever anything changes and at least every 250ms, so this
is safe to poll in a tight loop — that is the point of using it here, since every
step of this recovery is "has it happened yet?".

Check it is actually live before trusting a `false`, or a dead client reads as a
healthy character:

```python
uo.require_live()      # raises NotLiveError if the file is stale
```

The log also passively announces the moment it happens:

```
[WARN ] [DEATH] You have died!
```

## 2. Always accept the resurrection gump yourself

There is no auto-accept for this gump — you must send the confirm response
yourself every time:

```bash
echo "gumps" >> /tmp/cuocmd
sleep 1
tail -15 /tmp/cuolog
```

If it shows an open gump titled "Resurrection" with a CONTINUE button (see the
table below for the ID), accept it explicitly:

```python
uo.call("gumpresponse 1")
uo.wait(lambda: not uo.ghost, timeout=10)   # should become False
```

Don't stop checking until `charGhost` is `false` — if it's still `true` after
`gumpresponse 1`, re-check `gumps`; the gump may have re-opened or a new one may
have replaced it. Because reading the file costs nothing, wait on the transition
rather than sleeping a fixed time and hoping:

```python
uo.wait(lambda: not uo.ghost, timeout=10)
```

**The gump can appear mid-walk, before you ever reach the healer's
coordinates — start checking for it as soon as you're near her, not after
you've "arrived."** A healer's resurrection offer fires off her own proximity
check the moment you pass within range, which can be several tiles before
whatever destination you told `goto` to walk to. The instant this fires, your
movement stops working and the log fills with `System: You are frozen and
cannot move.` / `Walk denied ... resync to (x,y)` — that freeze _is_ the gump
sitting open waiting on you, not a pathing hiccup. So: once you're
approaching the healer's area, check `gumps` after every leg rather than
waiting to see a string of freeze messages first (see the chase loop below).

### Don't be fooled by similarly-shaped gump IDs

Two other gumps show up around the same time and are red herrings if you're
trying to identify "the resurrection gump" by eye. The IDs below were **observed on one
shard** — gump serials differ between shards and server versions, so read the gump's text
(`gumps` prints it) to tell them apart; do not rely on the id:

| Gump ID (one shard) | What it actually is            | Handling                                   |
| ------------------- | ------------------------------ | ------------------------------------------ |
| `0xB04C9A31`        | **Real resurrection confirm**  | Send `gumpresponse 1` yourself (see above) |
| `0x11775C2E`        | "Client is out of date" notice | Auto-dismissed                             |
| `0xCECA8EAD`        | Server news / MOTD popup       | Auto-dismissed                             |

If `client_version` in `settings.json` doesn't match what the shard expects
correctly, the server sends the out-of-date notice, which can visually look
like it's blocking resurrection — it isn't; it's just a decoy dialog getting
auto-dismissed.

## 3. Walk to the area, then hunt down the healer specifically

A coordinate is only a starting _area_, not a guaranteed resurrection spot —
the healer is a wandering NPC, not a static object, so "arrive at (x,y)" is
not the same as "be next to the healer." Don't hardcode a coordinate from a
past session — the death happens wherever it happens, and a spot that was
"nearby" once can be thousands of tiles from the current corpse. Find the
nearest healer POI from wherever the ghost actually is instead:

```bash
echo "closest healer" >> /tmp/cuocmd   # nearest healer POI, from current position
sleep 1
tail -5 /tmp/cuolog                    # [POI] <name> (healer) at (x, y, z) - N tiles away
```

```python
walk = uo.goto_poi("healer")        # returns at once - watch while the character walks
while uo.nav.active:
    if any("resurrect" in l.lower() for l in uo.call("gumps")):
        print("offer fired mid-walk"); break
    time.sleep(1)
print(uo.nav.status)
```

`GoTo` doesn't answer the resurrection gump for you — if one appears mid-walk,
`goto` just sees the ghost as frozen/blocked (`System: You are frozen and
cannot move.` / `Walk denied ... resync to (x,y)`) and can loop on that for a
while or time out, because the freeze _is_ the game waiting on the gump
response, not a pathing problem. Once you're closing in on the healer's area,
check `gumps` proactively rather than waiting for a `goto` timeout or a string
of freeze messages to conclude something's stuck — see the chase loop below.

Note this freeze looks identical in the log to a separate ~3s throttle the
server applies to ghost movement generally (`GoTo: frozen, waiting 3s...`,
expected and not a bug) — `gumps` is what tells them apart.

**A ghost can walk through doors.** Unlike a living character (see
`uo-navigation`'s door-hunting steps), a ghost isn't blocked by closed doors —
don't bother with `opendoor` or entrance-hunting here. If `goto` still reports
`no path` for a ghost, treat it as a genuine unreachable/off-map tile, not a
door problem.

### Find the healer by name, not by guessing a spot

Nearby mobiles are already in `/tmp/cuoworld.json`, so read them there rather than
issuing a `mobiles` command — no round trip through the command-file poll and the
log, and it is safe to re-read as often as you like while chasing her. What neither
carries is an NPC job title: a healer shows up as a generic `Human (Male)`/
`Human (Female)`, indistinguishable from any other townsperson at a glance:

```bash
jq -c '.mobiles[] | select(.distance <= 15) | {serial, name, x, y, distance}' \
   /tmp/cuoworld.json
```

Identify which one is actually the healer:

- **Ambient name-barks are the easy signal.** Nearby NPCs periodically say
  their own name/title unprompted in the log, e.g. `<Name>: <Name> the healer`.
  Watch a few seconds of log output for a line containing "healer" — that
  gives you the name to match.
- Once you have a name, correlate it to a serial with `click <serial>` on
  the candidate Human NPCs from the world file — the response echoes the
  full name (`Paperdoll opened for [...]: '<Name> the healer'` if you use
  `use <serial>` instead, which also works for identification).

**Don't rely on the built-in `findhealer`/`fh` command** — it walks a fixed,
hardcoded sequence of far-off waypoints that can be entirely unrelated to (and
unreachable from) your current location. In practice it can fail every single
waypoint with `no path ... 0 extra blocked` and conclude `Pattern complete —
no healer found` even while a real healer is wandering a few tiles from you.
Treat it as unreliable and use the world-file + name-bark approach instead.

### Chase the healer down — she keeps moving, and check for the gump every leg

Once you have her serial, don't just `goto` her last-seen position once —
she wanders continuously, including while you're walking toward her, so a
single `goto` will often "arrive" at a spot she's already left. And since the
resurrection gump can fire from proximity alone partway through any of these
legs (see step 2), check `gumps` after every `goto`, not just once you think
you've arrived:

`goto` returns immediately, so you can finally do what this step always wanted: watch for the
gump _during_ the walk rather than only between legs. The offer fires on her proximity check,
which can trip at any point along the way.

```python
healer = uo.nearest(named="healer") or uo.nearest(within=15)
uo.goto(*healer.pos[:2])

while uo.nav.active:
    if any("resurrect" in l.lower() for l in uo.call("gumps")):
        break                       # accept it now, step 2
    if not uo.ghost:
        break                       # already resurrected
    time.sleep(1)

print(uo.nav.status, uo.ghost)
```

If `gumps` shows the Resurrection gump open, accept it now (step 2) instead of
continuing the chase — don't finish walking to her current tile first. If not,
repeat: re-check her position, `goto` again, check `gumps` again:

```bash
# she's moved — get the updated (hx, hy) straight from the world file
jq -c '.mobiles[] | select(.serial == "<her-serial>") | {x, y, distance}' /tmp/cuoworld.json
echo "goto <hx> <hy>" >> /tmp/cuocmd   # a second goto cancels the first; no need to stop it
sleep 2
echo "gumps" >> /tmp/cuocmd
sleep 1
tail -8 /tmp/cuolog
```

Re-aiming mid-walk is free: one walk runs at a time and the newest wins, so you can re-issue
`goto` at her updated position every couple of seconds rather than waiting out each leg.

Keep this re-check/re-goto/re-gumps cycle going until either the gump appears
(accept it and stop) or you're **adjacent to her exact current tile** — don't
settle for "same room" or "within world-file range." The resurrection
proximity check is tight; arriving at a tile she already vacated does nothing
even if you're only a few tiles off by then.

### If the gump doesn't appear once adjacent

Sometimes walking straight into range doesn't trigger the confirm dialog
immediately. First check whether it's just sitting there unanswered — that's
the common case, since nothing accepts it automatically:

```bash
echo "gumps" >> /tmp/cuocmd
sleep 1
tail -15 /tmp/cuolog
```

If a Resurrection gump is open, send `gumpresponse 1` (step 2). Only if
`gumps` shows nothing open _and_ `.charGhost` is still `true` a couple of
seconds after you're adjacent, the proximity check needs to be re-triggered —
and a single-tile step away and back **is not enough to do that reliably** (a
`walk n` immediately followed by `walk s` while already facing north, for
example, can just turn you in place without moving a tile at all, and even a
real one-tile hop back onto her tile has been observed not to re-fire it).
Walk at least **10 tiles away**, then back:

```python
x, y = uo.xy                                  # note current position

uo.goto(x - 12, y).wait()                     # any direction, just get real distance

healer = uo.nearest(named="healer") or uo.nearest(within=15)
uo.goto(*healer.pos[:2])

while uo.nav.active:                          # watch for the offer during the walk back
    if any("resurrect" in l.lower() for l in uo.call("gumps")):
        break
    time.sleep(1)

print(uo.ghost, uo.hp, uo.max_hp, uo.nav.status)
```

**Here the walk-away leg genuinely must complete before you turn around** — that is the whole
point of it — so this is one of the places you do wait on `nav.active`.

If the gump still hasn't appeared after that, repeat once more before suspecting
something else is wrong (wrong NPC identified as the healer, or she wandered out of
the area entirely — re-verify with the name-bark check).

## 4. After resurrection, check equipment

Resurrection doesn't restore lost gear — confirm what the character is
actually wearing before assuming anything (including whether a "death robe"
survived). See the `uo-equip-items` skill for the correct layer table
(notably: layer `0x0B` is **Hair**, not a robe — don't mistake it for stuck
equipment when investigating what's on the character).

```bash
print(uo.ghost, uo.hp, uo.max_hp)   # confirm no longer a ghost
echo "inv" >> /tmp/cuocmd; sleep 1
tail -15 /tmp/cuolog
```

**Don't conclude gear was lost just because the backpack shows empty.** `inv`'s
backpack listing only reflects what the server has actually sent, which is nothing
until the backpack itself has been double-clicked open at least once this session —
see the `uo-equip-items` skill's step 1. If anything looks missing, open it before
reporting a loss:

```bash
echo "use <backpack_serial>" >> /tmp/cuocmd; sleep 2   # serial from the inv equipped list, layer 15
echo "inv" >> /tmp/cuocmd; sleep 1
tail -15 /tmp/cuolog
```

- **A bandage on a poisoned character tries to _cure_, not to heal.** The log line is
  `You have failed to cure your target!` rather than the usual "barely help", and while that is
  failing HP is not being restored at all. When the character's Healing (and the Anatomy it is
  checked against) is low the cure fails repeatedly, so the healer reflex can be working perfectly
  and still restore nothing.
- **The damage does not stop when you break off.** Poison ticks on its own schedule regardless of
  distance, so `escape` running the character further away does not slow the bleed - and a low-Dex
  character walks, so the escape never terminates either. A character can go from half HP to dead
  mid-flight without a single further hit landing.

**Recognising it early is the whole game.** Poison ticks are conspicuously regular - the same
damage on the same interval, a few seconds apart - where melee damage is irregular in both size
and timing.
`isPoisoned` in the state file is the direct check, but note it reads `false` between ticks and
after it wears off, so a single sample is not proof of anything; grep the log for
`[SYSTEM] The poison seems to have worn off.` to confirm after the fact.

**What to do instead of fleeing into open ground:** head for an NPC healer. They cure poison as
well as resurrect, and the walk is bounded. Guards are the other answer when a _pursuer_ is the
problem - a Criminal-notoriety pursuer (check its notoriety in the world file) can be led into a
guarded town, where the guards kill it for free. Fleeing away from town solves neither and is what
turns a survivable fight into a corpse.

## Death costs time, not progress

Worth knowing before deciding whether a risky training ground is worth it: **skill and stat gains
survive death.** Compare the skill readings in the state file before and after: they are the
same. What a death actually costs is the recovery: walk the ghost to a healer, accept the gump,
walk back, re-equip, re-loot the corpse - roughly ten minutes. So a spawn that trains fast and kills occasionally can still beat
a safe spawn that trains slowly; price it in minutes, not in lost progress.

Items are not automatically lost either - which items stay with the character and which drop to
the corpse is a shard rule, not a constant (on one shard observed, the weapon, shield and clothing
were still in the backpack after resurrection, while consumables, gold and all worn armor were on
the corpse). Check `inv` before assuming anything is gone, and go to the corpse before assuming it
is not.
