---
name: uo-combat-loop
description: Fight a single focused target with the UO client — pursue it, watch its HP and your own, keep melee range, loot the kill, and hand back when the ground is empty. Use whenever the user asks to fight, kill, attack, or engage a monster/mobile in combat (as opposed to a one-off `attack` command).
---

# Running a combat loop against one target

Requires a logged-in character with the map loaded (`uo-login`). Pursuit uses the same
`goto` mechanics as `uo-navigation` — read that first if you haven't already, especially
the part about `goto` not remembering blocked tiles between calls.

**Every `goto` in this skill (pursuit, kiting, the step 5 flee point) is sent directly, not through
`safe_goto.py`.** `uo-navigation` requires routing all other walks through `safe_goto.py` so a
hostile gets caught before the character walks up to it — but here the hostile is the point. Once a
target is engaged (`attack` sent, war mode on), `safe_goto.py` would immediately trip on the very
thing being fought. This exception ends with the fight: once disengaged, dead, or fled clear, go
back to `safe_goto.py` for the next walk.

## How this runs: reflexes below, judgment above

Three layers, and keeping them apart is what makes a fight manageable:

| Layer | Owns | Example |
|---|---|---|
| `cli/uo` | mechanism — state, actions, events, bounded waits | `uo.hp`, `uo.attack()`, `uo.wait_for()` |
| **background scripts** | *reflexes* — continuous, fast, no judgment | `autoheal.py`, `combat_movement_melee.py` |
| **you, orchestrating** | judgment — which target, when to disengage, what else is around | this document |

Reflexes have to react faster than a turn of thought and never need a decision: stay in weapon
range, keep swinging, bandage when hurt. Put those in a background script and leave them running.
Everything that needs a decision — is this the right target, is a second mobile closing, is the
damage race being lost, should we run — stays with you, because you are the only layer that can
weigh it.

**The script does not need to be told the target.** `combat_movement_melee.py` follows
`lastAttack` in the state file, which is the client's own record of who you are fighting. You
change target by sending `attack <serial>`; the script picks it up within a frame. No separate
channel, nothing to keep in sync, because the game state already is one.

**Melee gets its own script, not a hand-driven loop.** `combat_movement_melee.py` is written for
a weapon that needs to *be* adjacent: it only ever closes the gap toward the target's own tile and
holds once inside `--range` (default 1). Aiming at the target's own tile rather than a point held
short of it is deliberate: a held-short point is computed from the target's position *this
instant*, and a fleeing target has already moved again by the time that walk lands, so each leg
re-aims at a spot that's stale before it's reached and the character never gains ground — `goto`
already settles for the closest reachable tile when the destination itself is occupied, so there
is no adjacency overshoot risk. Launch it in the background before attacking. It is the only
shipped movement reflex: a bow, a spellbook or a pet needs its own script written the same way
(see "Writing your own loop" below) — a bow in particular needs a kiting branch this script
deliberately does not have. Driving melee pursuit by hand, one `goto` at a time, is what step 3c below describes as the
underlying mechanism — useful for understanding what the script does, but not how a real fight
should be run: it is exactly the shape of thing `safe_goto.py`'s post-arrival watching (see
`uo-navigation`) cannot tell apart from an unattended fight, and it competes with your own attention
for every single step of the pursuit.

**It is a three-state machine: kill, loot, escape.** Every iteration builds one threat picture, runs
one transition table over it, and dispatches to exactly one handler; any state can become any other
on any iteration, and the handlers never choose the state themselves. Transitions are logged as
`kill -> loot`, `loot -> escape (hp)` and so on — that line is the primary thing to watch in its
background output.

| State | Entered when | Does |
|---|---|---|
| `escape` | HP below `--flee-hp-pct` (45); an avoid-listed mobile aggro'd on us at any range; an *idle* avoid-listed mobile within `--avoid-idle-range` (2); `--swarm-count` (4) threats within `--swarm-range` (2) **and** HP at or below `--swarm-hp-pct` (60) | Runs for distance and nothing else — no attacking, no war toggle, no looting |
| `loot` | There is an unlooted corpse and fewer than 2 aggro'd threats are on top of us | Drains corpses; swings at anything adjacent without moving |
| `kill` | Otherwise | Acquire, pursue, attack |

A swarm is the one escape with a health gate: being surrounded at full HP is a busy fight, not an
emergency, so it is fought through down to `--swarm-hp-pct` (60). The other three fire on their
own conditions regardless of health.

Escape always wins, and low HP pre-empts an escape already running for a laxer reason — it runs
further (`--hp-flee-distance` 25 vs `--flee-distance` 15) and, unlike the others, will not end on
distance alone: HP has to climb back to `--resume-hp-pct` (85). **That makes `autoheal.py` a hard
dependency.** This script never bandages, so with nothing healing the character an HP escape runs
forever by design. Any threat within `--pursuit-range` (10) also keeps an escape alive, which is how
"still being chased" is expressed without a second rule that could disagree with the first.

**Aggro is read off the WAR flag.** `inWarMode` in the world file is the only real "this thing is
fighting" signal the client carries — there is no per-mobile "is targeting me" — so `is_aggro` in
`cli/uo/avoidance.py` is war mode *plus* being within `--aggro-range` (12). War mode alone means
it's fighting something, not necessarily us; the range window is what makes it about us, and a guard
duelling a thief across the field would otherwise trigger a permanent flee.

**Looting is non-blocking, and it drains every kill we make.** Not just the corpse of the kill that
led into it — several kills leave several corpses and it works through all of them, remembered by
serial so nothing is opened twice. Only corpses *we killed* are ever queued: a corpse someone else
made is their loot, and a worked hunting ground is covered in them. One server round trip per iteration, never a `sleep`, which is what lets it keep
swinging at a mobile standing on it while the server's 1.5s action throttle runs down. Nested
containers are opened in place and emptied down to `--max-loot-depth` (3, counting the corpse as 0)
— never picked up, because a bag inside a corpse cannot be taken and a drain that tried would loop
on it forever.

**`combat_movement_melee.py` self-selects targets and can be told to avoid or restrict them.**
It doesn't just react to `attack <serial>` — with no target set it
actively picks the nearest threat within `--acquire-range` and engages it, so launching it is itself
a combat decision, not a passive safety net. Two mutually exclusive flags shape which mobiles it
will ever target, fight back against, or knowingly walk near:

- `--avoid TERM` (repeatable) — a blocklist. Everything is fair game except mobiles matching one of
  these. Use for "fight anything except X": `--avoid species:<a> --avoid species:<b>`.
- `--include TERM` (repeatable) — an allowlist, the inverse. *Nothing* is fair game except mobiles
  matching one of these; every other **threat** is treated exactly like an avoid-listed one. Use
  for "fight only X": `--include species:<type>`. Note it inverts over threats only, not over every
  mobile in view — under an allowlist "not on the list" would otherwise describe town NPCs, vendors
  and Gray wildlife, and the reflex would flinch away from a rabbit.

**A term matches on name, species, or body graphic.** Four forms, in both flags:

| Term | Matches |
|---|---|
| `species:<type>` | exact species — `<Type>`, never `<Type> Mage`. **Usually what you want.** |
| `name:<name>` | exact name |
| `graphic:<n>`, `graphic:0x<n>` | exact body graphic |
| `any:<word>` | substring, case-insensitive, of the name **or** the species |

**Every term must name its axis — there is no bare form, and `--avoid <type>` is a launch error**
telling you which prefix to add. Guessing the axis is what made the old bare form dangerous, and
silently so: `--include <type>` also substring-matched `a <type> mage`, so an allowlist whose whole
purpose was keeping casters away from a character with little or no magic resistance admitted one.

`species` is the body graphic resolved to a word by the client (`Capabilities/MobNames.cs`), so
`species:<type>` catches a creature of that type carrying a *personal* name — which name matching
could not. Under
`--include <type>` such a monster used to match nothing, get treated as avoided, and, since
include-mode makes an avoided creature an escape trigger, **the reflex fled the very target it had
just acquired**. Measured live 2026-08-31: `target is now 0x0000723C (<personal name>)` immediately
followed by `kill -> escape (avoid-aggro)`, repeatedly, with no swing landing.

**Read the vocabulary out of the files rather than guessing at it** — `species` is on world-file
mobiles and on the state file's `lastAttack`/`lastCursorTarget` alike:

```bash
jq -c '.mobiles[] | {name, species, graphic}' /tmp/cuoworld.json
{"name":"<personal name>","species":"<Type>","graphic":<n>}
{"name":"a <name>","species":"<OtherType>","graphic":<m>}
```

**Name and species genuinely disagree, and that is the point.** That second line is the shape of a
real reading, measured at a graveyard spawn 2026-09-02: the server calls it `a <name>` while its body
is `<OtherType>`. `name:` and `species:` pick different sets there, and `any:<name>` or
`any:<othertype>` catches it either way — which is what `any:` is for. Prefer `species:` when you
know the type, `any:` when both spellings are in play.

`species` is `null` for a graphic the client's table doesn't list (it stops at 403 and has gaps), and
for ground/static targets, which have no body graphic at all. A `species:` term never matches those;
a `null` showing up for something worth naming is the signal to add a line to that table.

Under `--avoid` in open country, the target pool is `is_threat`, which already excludes harmless
wildlife, so a passive animal is neither fought nor fled — it is scenery. Under an allowlist it is
an escape trigger, which is how a walk through open country turns into a flight from a housecat.

Either way, an avoided/non-included mobile is never just skipped as a target — it's a keep-away
point for *every* movement the script makes (pursuit, walking to a corpse, and the escape route
itself), and one aggro'd on us, or merely within `--avoid-range`, is an escape trigger rather than
something to fight through. Passing both flags together is a config error the script refuses to
start with. See the module docstring for the exact semantics, including why an unresolved name
defaults to "avoided" under `--include` but not under `--avoid`.

**An avoided creature gets two different berths.** One that is *aggro'd on us* is a thing to get
away from — full `--avoid-range` (5), and an escape trigger at any range up to `--aggro-range`. One
that is merely standing there is a thing not to bump into — `--avoid-idle-range` (2). Giving both
the wide berth made the character refuse perfectly good ground in a dense spawn and abandon fights
over a hostile that was ignoring it. Note even the aggro'd 5 is below `safe_goto.py`'s
`AVOID_PROXIMITY_RANGE` of 10: a fight already has `autoheal.py` and this script's own
HP/swarm/aggro checks watching.

Anti-stuck bounds exist for each state and are worth knowing when reading its output: a target that
can't be pathed to is blacklisted for 5 minutes after `--nopath-strikes` (default 2) (and once everything in
acquire range is, the empty-ground handoff fires and says so - see below), a corpse that won't finish is given up on
after `--loot-corpse-timeout`, an item that won't move is left after three tries, and an escape that
covers no ground for two `--escape-timeout` windows concludes it is cornered and fights back for
`--escape-giveup` seconds rather than sending `goto`s that never execute. HP escapes are exempt from
that last one.

**Check the equipped weapon before launching — don't infer it from context.**
`jq -c '.equipped[] | select(.layer=="OneHanded" or .layer=="TwoHanded")' /tmp/cuostate.json` is a
cheap, authoritative read. A `Swing:` log line looks identical for melee and archery, so guessing
the weapon type from combat narration (rather than checking `equipped`) is how a bow ends up being
fought as melee with no kiting reflex, and the melee script is written for adjacency only. This
matters most for fights that start *without* a deliberate engagement — a mobile auto-retaliating
the moment you arrive somewhere still needs the movement script running immediately; don't wait
for a consciously-chosen target to add it, only `autoheal.py` isn't enough on its own.

A normal fight therefore looks like:

```bash
# once, in the background - Bash tool with run_in_background: true
python3 "$(git rev-parse --show-toplevel)/.claude/skills/uo-combat-loop/autoheal.py" --threshold 90
python3 "$(git rev-parse --show-toplevel)/.claude/skills/uo-combat-loop/combat_movement_melee.py"

# then you drive, one line at a time, watching between each
echo "attack 0x0001D3E5" >> /tmp/cuocmd
```

From there you read state and decide; the reflex handles the rest — holding weapon range or closing
to adjacency, kiting or chasing as appropriate to the weapon, never backing into a second hostile
while doing it, and dropping everything to retreat once HP falls to/below its own threshold (45% of
max by default; see the constants at the top of each file). **This HP check runs unconditionally,
every iteration, whether or not there is a tracked target** — it has to: the gap between one target
dying and the next `attack` being sent used to leave the reflex fully idle (no HP check at all),
which is exactly the window an unrelated hostile closed in and killed a full-HP character in, with
the reflex running the whole time and never once looking at HP. Stop it when the fight is over; it
idles harmlessly with no target, so it can be left running between fights — it is still watching
HP even then.

**Launching one that is already running is safe — it refuses and exits 3.** Both are wrapped in
`@script`, which holds a single-instance lock under the script's filename, so a duplicate exits
before touching the character. Check first rather than guessing, especially after a compaction,
when you have no memory of what you launched:

```bash
python3 "$(git rev-parse --show-toplevel)/cli/uo/locks.py"   # what is running
```

To restart one, pass `--replace` — it SIGTERMs the copy already running and takes over, rather than
making you find the pid. Do not work around the refusal by passing `--name`: two of these running
is not a duplicate that wastes CPU, it is a character that stutters in place (two pursuit loops each
cancel the other's `goto`) or a wasted bandage answering a cursor the other opened.

## Writing your own loop

**This whole skill is a loop, so it belongs in a `.py` file run in the background** — never as a
long inline `python3 - <<'PY'` block. An inline loop blocks the session for its entire duration:
you cannot check anything, react to anything, or send another command while it runs, and if it
hangs there is nothing to inspect. That defeats the point of `goto` being asynchronous.

Write it to a file and launch it detached, the way the two above are. Run it via the Bash tool
with `run_in_background: true`, then watch its output and the state file while it works. Several
can run at once — they compose because the client serialises commands itself.

**Wrap it in `@script` and let `uo.every()` be the loop.** Not style — it is what stops a second
copy from starting, and it removes the parts that are the same in every one of these:

```python
import argparse, sys
from uo import script

ap = argparse.ArgumentParser(description=__doc__)
ap.add_argument("--range", type=int, default=1)

@script(ap)
def main(uo, args):
    for _ in uo.every(0.4):          # cadence, client-gone, ghost, --for/--iterations
        target = uo.last_attack
        if not target.is_set:
            continue
        ...
        if done:
            return uo.stop("target dead")
```

The loop stays in the body on purpose, so your state lives in ordinary locals and you write ordinary
`break` and `continue` against your own conditions. What you no longer write: the lock, the
`Client`, the live check, the ghost check, the sleep, the exit codes. See `autoheal.py` (small) and
`combat_movement_melee.py` (a full state machine) for the two worked examples.

**One-off actions stay in bash, appended straight to the command file** — swapping a weapon, a
single `say`, toggling war. `navrey` spawns a .NET process (50-100ms) to do what an append does in
~0.01ms, and you are going to confirm the result from the state file regardless:

```bash
printf 'unequip 0x40612D84\nequip 0x40612D85 01\n' >> /tmp/cuocmd
jq -r '.equipped[] | select(.layer=="OneHanded") | .name' /tmp/cuostate.json
```

For the handful of reads whose output is not in a file — `los`, `gumps`, `shop` — append the
command and read the reply out of `/tmp/cuolog`, same as any other command. Never shell out to
`navrey` directly; Python is for the loop, not for every interaction.

The scripts import `cli/uo`, which gives state at ~4us a read, actions that do not block, and
one canonical set of event patterns:

```python
import sys, time; sys.path.insert(0, "cli")
from uo import Client, Event

uo = Client(); uo.require_live()
```

The library holds no opinions — it will not decide when to heal or who to fight. Those are this
skill's judgment calls, and they stay below as prose, to be encoded in whichever script you write.

The core idea: **pick one target and stick with it.** Don't use `attacknearest` inside
the loop — that re-picks a target every call, which fights whatever's closest instead
of finishing the fight you started. Use `attacknearest`/`an` (or a world-file scan) only
once, to *choose* the target, then drive the rest of the loop entirely off that one
serial.

## Optional: run the auto-healer alongside the fight

`autoheal.py` (in this skill's directory) is a standalone background loop that watches HP and
bandages on its own whenever it drops below 90% of max — it exists because a manual polling loop
(checking HP, deciding, then bandaging on the *next* iteration) has actually lost a fight
full-HP-to-dead without ever sending a single bandage command (see the Gotchas section). Launch it
once before engaging, in the background, and let it run for the rest of the session:

```bash
python3 "$(git rev-parse --show-toplevel)/.claude/skills/uo-combat-loop/autoheal.py" --threshold 90
```

Run this via the Bash tool with `run_in_background: true`. It re-reads the bandage from the
backpack each time (so a restock is picked up automatically) and stops itself when the character
becomes a ghost (hand off to `uo-death-recovery`, don't restart the healer on a ghost) or no
bandages remain in the backpack (restock, then relaunch it).

It is a *script*, not part of `uo` — it imports the library and writes its own loop. "Heal below
90%" is one opinion about how to play, and the library deliberately holds none, so a different
policy is a different script rather than a flag on a shared function.
It does **not** replace step 5's retreat logic below — it only heals, it never moves the
character, so a fight that's losing the damage race still needs an explicit flee
decision.

## 1. Pick the target

```python
uo.mobiles(within=10)                       # from the world file, no round trip
uo.mobiles(within=10, hostile=True)         # only what `attack` will accept
```

Pick a mobile whose notoriety is `CanBeAttacked` (or `Criminal`/`Enemy`/`Murderer` —
anything `attack` will actually accept). Note its serial — call it `<target>` for the
rest of this loop. Everything below is scoped to that one serial.

Mobile HP always reads `0/0` in the world file until you explicitly request it —
the client never gets a health bar for free:

```python
uo.request_hp(target)
uo.wait(lambda: target.max_hits, timeout=3)
```

## 2. Engage

**`attack` does *not* turn on war mode for you.** It only sends the attack-request
packet (`GameActions.Attack` in the client) — it never toggles war mode as a side
effect. If you're out of war mode, the attack request is easy to lose (no swings ever
start, or the server just ignores it) with nothing in the log to explain why. Always
check and force war mode on before attacking:

```python
uo.war(True)                                   # no-op if already on
uo.wait(lambda: uo.war_mode, timeout=2)
```

Then issue the attack:

```python
uo.attack(target)
```

Once the server accepts the attack, it auto-swings on its own timer as long as you stay
in range and LOS — but treat both `war` and `attack` as commands to **re-issue on every
loop iteration** where you're not sure they landed (see step 3d), not one-time setup.
Resending `war` when already in war mode, or `attack` when already fighting, is
harmless — the cost of under-sending is a fight that silently never starts.

## 3. The loop

Each iteration, in this order:

**a. Check your own status first.** A dead player can't do anything else meaningfully.
Your own state comes from `/tmp/cuostate.json`, not a command — no round trip, no sleep,
and it is rewritten whenever anything changes (and at least every 250ms), so you can read
it as often as you like:

```python
import sys; sys.path.insert(0, "cli")
from uo import Client, Event
uo = Client(); uo.require_live()

uo.ghost, uo.hp, uo.max_hp, uo.war_mode     # ~4us each
```

If `ghost` is `true`, stop the combat loop entirely and hand off to `uo-death-recovery` —
don't keep sending combat commands to a ghost. If alive, check `hp` against `max`:
- **Below half HP** — stop fighting and retreat per step 5 before doing anything else
  this iteration. Don't keep trading swings hoping the next bandage catches up.
- **Hurt at all, even one point below max** — apply a bandage per step 4 **right now,
  before doing anything else this iteration** (before re-checking the target, before
  pursuing, before resending `attack`). This is not optional busywork to fit in when
  convenient — a real fight has been lost end-to-end (full HP to 0, character dead)
  without a single bandage ever being sent, simply because each loop iteration kept
  moving on to the next status/position/attack check instead of actually healing.
  Treat "I saw I was hurt" and "I applied a bandage" as the same action, not two
  separate decisions.

**Damage can arrive faster than your polling interval.** Against a single weak mobile,
checking every iteration is fine. Against multiple attackers or anything that hits hard
(a caster, several mobiles converging, anything unfamiliar), HP can go from full to
critical between two checks — a couple of loop iterations is enough to go from "fine" to
"dead" with no iteration in between ever reporting "below half" cleanly. If you see more
than one hostile adjacent or in LOS, or a single hit takes a large chunk of max HP,
shorten your polling interval and bias toward bandaging early rather than waiting for a
clean "hurt but not critical" reading to act on.

Reading HP from the state file costs nothing, so there is no reason to poll it slowly —
watch for the threshold instead of sampling once per iteration:

```python
# trip as soon as HP crosses half, rather than finding out next iteration
uo.wait(lambda: uo.hp < uo.max_hp / 2, timeout=300, poll=0.2)
```

Use `with uo.frozen():` when several fields must describe the same moment — separate reads can
straddle a rewrite and mix two instants.

**b. Check the target's position and HP.** Once you have sent `attack <target>`, the
state file tracks that target for you under `lastAttack`, with its position resolved
live from the world every frame:

```python
uo.last_attack        # .name .pos .hits .max_hits .exists .is_set
```

This is the cheapest and freshest source for **where the target is** — it follows the
mobile as it walks, with no command and no round trip, which is exactly what step 3c's
pursuit needs.

Check `kind` and `exists` before trusting the rest — the sub-fields all read `null` when
there is nothing to resolve, and `null` is not the same as zero:

| Reading | Means |
|---|---|
| `{"kind":"none"}` | No attack target set at all — you haven't attacked this session. Send `attack <target>`. |
| `kind: "entity"`, `exists: false`, coords `null` | A target is set but the client can no longer see it: it died and became a corpse, or it walked out of range (this also happens when *you* walk away). The fight with *this* target is over; don't keep waiting on it. |
| `kind: "entity"`, `exists: true` | Live target — coordinates are current as of this frame. |
| HP 0 | Dead, or about to be. |

```python
t = uo.last_attack
if not t.is_set:   print("no target set - attack first")
elif not t.exists: print("target gone - pick a new one")
else:              print(t.name, t.pos, t.hits, "/", t.max_hits)
```

**Target HP does track live once you are actually fighting it** — the server pushes
attribute updates as damage lands, and the file reflects them within a frame. Watching
`.lastAttack.hits` fall to 0 is a valid way to know the target died.

The caveat is the *start* of a fight, not the middle: until the server has sent a status
for that mobile, HP reads as a placeholder (the same `hits: 25`-style value the world file
shows) and will sit there unchanged. Two situations look identical from the outside — a
target you haven't primed yet, and a target you are hitting for no damage. `hp <target>`
forces the status request that primes it:

```python
uo.request_hp(target)          # primes it; after this the HP tracks live
uo.wait(lambda: uo.last_attack.max_hits, timeout=3)
```

So: prime once with `hp` when you pick a target, then read HP from the file for free
every iteration after that. Position never needs priming — it is resolved live from the
world every frame from the moment you attack.

**c. Pursue if needed.** Both positions are in the one file, so the comparison is a
single read with nothing to parse:

```python
with uo.frozen():
    print(uo.xy, uo.last_attack.pos, uo.last_attack.exists)
```

`exists` False with a `None` position means there is nothing to pursue (see 3b) — pick a new
target rather than trying to path to a null.

- **Melee** (weapon layer `01`/`02`, not a bow/crossbow): you need to be within 1 tile.
  If not, `goto <target_x> <target_y>` to close the gap. `goto` returns immediately and a
  second `goto` cancels the first, so chasing a moving target is just re-issuing it against
  the target's current position from `.lastAttack` — no waiting, no cancelling by hand:

  ```python
  # re-aim every second while closing; attack/bandage still work throughout
  while uo.last_attack.exists:
      with uo.frozen():
          tx, ty = uo.last_attack.pos[:2]
          mx, my = uo.xy
      if max(abs(tx - mx), abs(ty - my)) <= 1:
          break
      uo.goto(tx, ty)        # a second goto cancels the first; no need to stop it
      time.sleep(1)
  ```
- **Archery** (bow/crossbow equipped): you don't need adjacency, just line of sight and
  being within weapon range. Check with:
  ```python
  uo.call("los")      # not in any file - pays a ~3ms round trip
  ```
  If `<target>` shows `NO`, move closer or reposition (`goto` toward it, then re-check
  `los`) rather than closing to melee distance.

**d. Resume the attack if you aren't the one swinging.** A `Swing:` line in the log
does **not** by itself mean you're attacking — it fires for *either* direction of
combat and looks almost identical either way:

```
Swing: <YourName> -> <target>     <- you attacking the target (what you want)
Swing: <target> -> <YourName>     <- the target attacking you
```

If the mobile is aggressive it will happily swing at you while you're out of war mode
or never actually attacking back — seeing *any* `Swing:` line and concluding "combat is
active, no need to resend attack" is the main way this loop silently turns into you
standing there taking hits without fighting back. Always check which name is on the
**left** side of the arrow.

Even that isn't the full check, though: the character can also swing back
reflexively while being attacked — a `Swing: <YourName> -> <target>` line or two can
show up **without ever having sent `attack` or being in full war mode.** This
retaliation is real damage, but it's noticeably worse than being properly engaged: it
doesn't reliably repeat on the weapon's timer, doesn't pursue, and can stop the moment
the target steps out of melee reach. So don't treat a lone/occasional outgoing swing as
proof you're fully engaged either. Confirm all three together before assuming the fight
is running properly:

1. `uo.war_mode` is `True`.
2. You've sent `attack <target>` for this target (not just relying on reflex swings).
   `uo.last_attack.serial` confirms which serial the client currently has as your target.
3. `Swing: <YourName> -> <target>` lines keep recurring at a steady cadence, not a
   single isolated hit.

If any of the three is missing — war mode off, no attack sent, or outgoing swings that
fired once and then stopped — re-read the state file and resend `war` (if `warMode` is
false) and `attack <target>`, even if the mobile is still landing occasional retaliation
hits for you.

**A swing line carries no outcome — judge by damage, not by the swing.** The swing packet
(0x2F) says only that a swing happened; it has no hit/miss field. The narration used to print
one anyway, read from a byte the protocol defines as constant, so *every* swing showed as
`MISS` — measured live, four consecutive `MISS` lines while three of them dealt damage and
took the target from 25 HP to 3. Any rule of the form "a run of misses means X" was reading a
fiction.

What actually tells you swings are connecting:

```
Damage: <target> takes 6          <- the ground truth, one line per landed hit
```

or in Python, without parsing anything:

```python
uo.combat.since_damage_dealt      # seconds since we last hurt something
uo.combat.since_swing             # seconds since we last swung at all
```

Read them together, because they separate two different failures:

- **Swinging, but no damage** (`since_swing` small, `since_damage_dealt` large or `inf`) — the
  swings cannot connect: an archer with no arrows, or no line of sight. Check ammo and `los`
  rather than waiting it out. This is the real version of the old "unbroken MISS run" rule.
- **Not swinging at all** (`since_swing` growing) — you are not engaged. Re-send `war` and
  `attack <target>` per the three checks above.

Repeat a–d until the target dies (or you decide to disengage).

## 4. Healing with bandages (self)

When you've taken damage, use a bandage on yourself rather than letting HP ride down —
see the trigger in step 3a. Find a bandage's serial from `inv` (see `uo-equip-items`
step 1 if the backpack hasn't been opened this session), then:

```python
uo.use_on(uo.find("bandage"), "self")
```

`use_on` waits for the server's target cursor before answering it. Sending `use` and `target self`
back to back loses the race — the cursor arrives ~33ms later and the answer is thrown away with
"nothing is asking for a target", leaving the cursor open until it times out.

**Only one bandage can be applied at a time, and you must wait for it to finish before
starting another.** Bandaging takes several real seconds; sending a second `use` while
one is already in progress doesn't queue it usefully and just wastes a bandage /
confuses the state. Poll the log rather than guessing a fixed sleep:

```python
result = uo.wait_for([Event.BANDAGE_DONE, Event.BANDAGE_FAILED], timeout=20)
```

The library owns the message set, so no script carries its own copy — the two hand-written copies
that used to exist had drifted, and one was missing every poison cure.

Treat the two outcomes differently:

- **Success** — `You bandage your wounds`, `You successfully heal [Player Name]`,
  `You have cured the target of all poisons`, `You have been cured of all poisons`,
  `You bind the wound`. The bandage resolved and (if wounded) HP should have ticked up
  — re-check `uo.hp` against `uo.max_hp` and apply another bandage if still below max.
- **Failure** — `You apply the bandages, but they barely help`,
  `You fail to apply the bandages properly`, `You have failed to cure your target!`,
  `You did not stay close enough to heal your target`. The bandage is consumed either
  way; if you're still hurt, just start another one.

**A "fingers slipping" message is neither of those — it's not completion.** It means
the attempt is still in progress (you took a hit mid-bandage, which extends it), so
keep polling rather than treating it as a failure or starting a new bandage over it.

If HP is critical and healing can't keep up with incoming damage, that's the same
retreat-vs-fight judgment call as before — bandaging doesn't remove the need to
disengage if you're losing the race.

## 5. Escaping when badly hurt

Triggered from step 3a whenever `.hits` drops below half of `.maxHits`. The goal is
distance, not finesse — get at least 20 tiles clear of the threat and heal there,
rather than trying to bandage through an ongoing beatdown.

**a. Stop re-engaging.** Don't send another `attack` or `war` — you're not trying to
land one more hit, you're trying to leave. (You don't need to explicitly turn war mode
off either; it doesn't matter once you're not adjacent or in LOS anymore.)

**b. Pick a flee point at least 20 tiles away, roughly opposite the threat.** Grab your
own position and the target's last known position:

```python
with uo.frozen():
    me, target = uo.xy, uo.last_attack.pos
```

Extend the vector from the target through you by 20-25 tiles — e.g. if you're at
(px, py) and the target is at (tx, ty), the away-direction is roughly
(+10, +5); normalize and scale that to length ~25 and add it to your position to get
the flee target. The exact math doesn't need to be precise — any point ≥20 tiles from
where you are *now*, generally away from the target rather than past it, is good
enough. If working out the vector isn't worth the trouble, running any ~20+ tile
distance while checking behind you (the world file's `mobiles`) works too.

**c. Start a bandage, then go — in that order, every time.** Bandaging resolves
server-side and isn't cancelled by movement, so it keeps ticking while you run; there is
no cost to starting one before you leave, and every real point of healing matters when
you're already below half HP. Don't treat this as something to do only "if convenient" —
send it unconditionally as part of fleeing, not as an afterthought:

```python
uo.goto(flee_x, flee_y)                        # returns at once - you are now running
uo.use_on(uo.find("bandage"), "self")          # ...and this happens *while* fleeing
```

`goto` handles the pathing and already runs rather than walks.

**Order no longer matters here, and that is the point.** `goto` returns immediately instead of
occupying the command worker for the whole flight, so the bandage genuinely goes out while the
character is running rather than before it sets off. Start the flight first — every tile of
separation counts more than the ordering did.

Wait for the flight to finish before deciding you are clear:

```python
status = walk.wait(timeout=120)     # arrived | nopath - nopath means you are boxed in
```

**d. Confirm you're actually clear.** After arriving:

```python
uo.mobiles(within=10, hostile=True)
```

If the original target (or anything hostile) is still adjacent, still in LOS, or still
closing distance, 20 tiles wasn't enough separation — fire another `goto` leg further
out rather than stopping to bandage next to something that's still chasing you. Since it
returns immediately, you can keep bandaging while that next leg runs.

**e. Heal up before doing anything else.** Once clear, repeat the bandage loop from
step 4 until `.hits` is back to a healthy level (at least back above half, ideally
full) — don't resume fighting or re-approach the target while still critical. Only
after that, decide whether to return and finish the fight or move on.

## 6. Archery: running out of arrows

Check remaining ammo via inventory — count `"arrow"` entries in the backpack listing
(or crossbow bolts, if that's the ammo type). **If the backpack hasn't been opened
yet this session, `inv` will show it as empty regardless of what's actually in
there** (see `uo-equip-items` step 1) — don't read that as "out of ammo" and
disengage prematurely:

```python
uo.open_backpack()                  # no-op once already open
arrows = uo.find("arrow")
print(arrows.amount if arrows else 0)
```

When ammo hits 0 (or you notice swings have silently stopped landing and you're at
range with an empty quiver), you have two options:

- **Switch to melee, if you already have a melee weapon in the backpack.** Unequip the
  bow and wear the melee weapon (see `uo-equip-items` for the unequip → wear → confirm
  pattern). Stop any archery reflex you wrote first — one that kites away as the
  target closes is the opposite of what melee needs — and launch
  `combat_movement_melee.py` in its place before resuming the attack.
- **Otherwise, go restock.** Disengage (the target will likely wander off — that's
  fine), navigate back to a vendor that sells the ammo (`uo-navigation` to get there,
  `uo-buy-items` for the `<VendorName> buy` → `shop` → `buy` flow), then return and
  re-engage. Re-read step 1's world-file scan when you get back — your original target
  may have moved, died to something else, or no longer be around, in which case pick a
  new target rather than chasing a serial that isn't there anymore.

## Gotchas

- **Monitoring HP is not the same as healing it — this has actually gone wrong.** A real
  fight went full HP → 0 (character killed) without ever sending a single `use
  <bandage_serial>` command, because every loop iteration kept moving on to the next
  status/mobiles/attack check instead of acting on "I'm hurt." Checking `status` and
  seeing a lower number accomplishes nothing by itself — the loop only helps if a hurt
  reading is immediately followed by an actual bandage command that iteration, per step
  3a.
- **Below half HP means disengage, not "bandage while still swinging."** Trying to
  out-heal an active fight rarely wins the race — step 5's retreat trigger is
  deliberately blunt (any time HP < 50% max) rather than a judgment call, so it fires
  before things get critical. But disengaging doesn't mean *not* bandaging — start one
  before/while you flee (step 5c), don't wait until you're clear to begin healing.
- **`attack` never turns on war mode by itself, and it's easy to send `attack` while
  still out of war mode and never notice.** Check `uo.war_mode`
  before and periodically during the fight — don't assume it stuck from one earlier
  `war` call. It costs nothing, so check it every iteration.
- **Any `Swing:` line means combat is happening, not that *you* are the one fighting.**
  A target attacking you produces a line that reads almost the same as you attacking
  it — always check which name is left of the arrow (`Swing: <you> -> <target>` vs.
  `Swing: <target> -> <you>`) before treating a swing as confirmation your own attack
  landed.
- **Even an outgoing swing isn't proof you're fully engaged.** The character can throw
  a reflexive counter-swing while being attacked without ever being sent into war mode
  or having `attack` issued — real damage, but it won't repeat reliably or pursue the
  way a proper attack does. Require `warMode: true` *and* a sent `attack` *and* a
  recurring (not one-off) `Swing: <you> -> <target>` cadence before trusting that the
  fight is actually running, not just a lucky reflex hit.
- `attacknearest` picks whatever's closest *right now* — never call it mid-loop or
  you'll abandon your focused target for something that wandered closer.
- **A single `exists: false` is not proof the target is gone.** It reads false transiently right
  after an attack is issued, and for a mobile at the very edge of view, while the mobile is still
  present in `uo.mobiles()`. A script that quit on one sample ended a fight three seconds after
  starting it, against a target 18 tiles away that was very much alive. Check the world scan too,
  and require the absence to persist for several seconds before concluding anything.
- **An unchanging target HP usually means you aren't damaging it, not that the reading
  is stale.** Once a fight is genuinely underway, `.lastAttack.hits` tracks the target
  live. If it sits at a round placeholder (`25/25`) while you swing, suspect the swings
  rather than the number: no line of sight, no ammo, or you never primed it with
  `hp <target>`. `uo.combat.since_damage_dealt` staying `inf` while `since_swing` keeps
  resetting is the same signal, and is the reliable form of it — the `MISS` in a swing line is
  fabricated and means nothing.
- A target's own wandering (most town/field mobiles patrol even out of combat) means
  melee pursuit can look like constant micro-adjustment — that's normal, not a bug.
- **Being in range isn't enough for archery — you need line of sight too.** A target
  can be well within bow/crossbow range and swings will still not land (or won't start)
  if a wall, fence, or other obstruction sits between you and it. Don't just check
  distance — check `los` (step 3c) before assuming a stalled ranged attack means the
  target is out of range; it may just be blocked, and the fix is repositioning, not
  necessarily closing distance.

## Casters are the thing an allowlist is for

A pure melee build has no defence against spells but magic resistance, and a character that has not
trained it has none at all. Measured on a character with little or no magic resistance: **a single
caster took it from full HP to well under half in seconds**, while the ordinary melee spawn on the
same ground hits for a few points at a time. The difference is not size - that caster had a smaller
max HP pool than the melee spawn - so nothing in a world-file scan distinguishes it. Its HP, its
notoriety and its distance all look harmless.

So the target filter is not a nice-to-have for a melee character with little or no magic resistance,
it is the safety
mechanism, and `--include` is the right shape for it rather than `--avoid`:

```bash
python3 .../combat_movement_melee.py \
    --include "species:<type a>" --include "species:<type b>" --include "species:<type c>"
```

`species:` rather than a bare term here on purpose: an allowlist built to exclude casters should not
substring-match its way into admitting `a <type> mage`.

`--avoid` is a blocklist over things you have already been surprised by. `--include` is the
inverse and fails safe: a caster nobody has met yet - whatever the next spawn tick produces - is
excluded because it is not on the list, not because someone remembered to add it. Confirmed
working: with the allowlist in place the same caster aggro'd again later and
the reflex broke off at `escape (avoid-aggro)` with no HP lost at all.

## Thin spawn is a silent throughput problem, and repositioning is the orchestrator's job

`combat_movement_melee.py` only acquires within `--acquire-range`; it never goes looking. In a thin
spawn that means the character empties a clearing and then stands in it,
fully alive, fully healthy, gaining nothing - and every log line still looks fine. Watch for it in
the ordinary check-ins: no `confirmed dead` lines and an unmoving skill number is what it looks
like.

**The fix is you moving the character, one decision at a time** - read the world file, pick ground
with spawn on it, walk there (`safe_goto.py`), and let the reflex hold it. There is deliberately no
roaming script:

A `patrol.py` that walked a fixed circuit of waypoints used to live here and was **deleted**,
because next to a caster it reliably made things worse rather than better. Its waypoints are a
decision made once and then executed for the rest of the session, so every time the combat reflex
escaped an aggro'd caster, patrol steered straight back to the waypoint that caster was standing on
and the identical escape fired again. Measured live on a caster-heavy hunting ground: escape, walk back, escape,
walk back, with no swing landing in between and every log line looking healthy. Dropping the
combat anchor was not enough to break the loop, because patrol was not reading the anchor.

Two smaller traps it also carried, worth knowing if anyone is tempted to rebuild it:

- **A yield range above melee's acquire range is a standstill.** A target inside patrol's yield
  range but outside melee's acquire range means patrol stands down and melee never engages, and
  neither script logs anything alarming.
- **Two scripts sending `goto` is the classic stutter** - each cancels the other's walk, and it
  reads as bad pathing rather than as a conflict.


## Tuning notes worth knowing before a long grind

- **In close quarters, shrink every range or the character just walks.** The defaults are sized
  for open ground: `safe_goto --range` 15, melee `--avoid-range` 5 aggro'd / `--avoid-idle-range`
  2, `--aggro-range` 12. A dungeon corridor is often narrower than any of those, so "keep 15 tiles
  from anything threatening" and "never come within 5 of an avoided creature" together describe
  more floor than the room contains - and the reflex spends the whole visit repositioning without
  landing a swing. Measured underground: four minutes, near-continuous escapes, almost no
  fighting. Underground, try roughly half: `--range 6..8`, `--avoid-range 3`,
  `--avoid-idle-range 1`. The trade is real - less warning against the thing you are avoiding -
  which is why it is a deliberate change for tight ground, not a new default.

- **An HP escape can get stuck in a band it cannot climb out of.** It deliberately does not end on
  distance: HP has to reach `--resume-hp-pct`. With `--flee-hp-pct 55` and `--resume-hp-pct 80`,
  a character being chased while bandaging oscillates between them and never reaches the top, so
  the escape runs indefinitely - and HP escapes are exempt from the give-up timeout by design.
  Observed: 90+ seconds of unbroken `escape (hp) ... still at ...` with nothing being fought.
  Keep the two thresholds close (45/65 rather than 55/80) so there is a band the character can
  actually heal across while moving.


- **The heal threshold is a bandage-economy decision, not just a safety one.** At `--threshold 90`
  on a character with a small HP pool a bandage is applied with only a handful of HP missing, so most
  of a successful heal is wasted overheal. Dropping to 80 cut consumption from ~12 bandages per 6
  minutes to ~1 per 7.
- **Bandages are slow and unreliable at low Dex/Anatomy.** On a low-Dex character one can take well
  over ten seconds, and with low Anatomy a large share come back "You apply the bandages, but they
  barely help" - a failed heal that still costs the bandage and the time. This is what caps how hard
  a target can hit before the fight stops being sustainable; it is not a reason to raise the
  threshold back up.
- **The swarm rule can oscillate against weak spawn.** The original defaults (3 within 2 tiles at
  <=85% HP) had the character flipping `escape (swarm)` -> resume every ~10 seconds against spawn
  hitting for 1-4, costing swing time for no real safety. `--swarm-count 4 --swarm-hp-pct 60`
  settled it, and **those are now the defaults** - the flags remain for tuning the other direction;
  `--flee-hp-pct` is still the real floor.

### A caster that will not break off is a stalemate, not a fight you are winning

An avoided creature does not necessarily give up. Measured: a caster aggro'd on the hunting ground
and pursued the character **57 tiles** without ever dropping past `--pursuit-range`, so
`escape (avoid-aggro)` kept re-firing "pursued to 9 tiles - running on" indefinitely. HP never
dropped - the escape was working perfectly - and the character gained nothing at all for minutes.

This is worse than it looks in the log, because every line says the safety machinery is doing its
job. Watch for `escape ... running on` repeating with the distance-out number climbing and the
nearest-threat number *not* climbing: that is a chase the character cannot win.

**Correction (2026-08-30): a low-Dex character *can* outrun these.** This section previously said
it could not - that max stamina equals Dex, so the character walks and fleeing never terminates.
Measured against a pursuer that had been adjacent and matching pace: `isRunning: true` in the state
file, stamina **still nearly full** at the end of the flight, ~59 tiles covered in about thirty
seconds, and the pursuer lost from view entirely. `goto` runs rather than walks, and a flight of this
length does not come close to exhausting even a very small stamina pool.

So fleeing *is* a plan that terminates, and the escape machinery is the thing to trust - do not
hand-drive a retreat with one-off `goto`s on the belief that it cannot work, which is a mistake this
file previously encouraged. What remains true is that a pursuer which never breaks off can hold an
escape open indefinitely (see the distance-climbing/nearest-not-climbing signature above); these two
still help when that happens:

- **Guards.** A Criminal-notoriety pursuer (check the notoriety in the world file) can be led into
  a guarded town, where the guards kill it for free. This is usually the cheapest answer and it costs only the walk.
- **Relocating the grind** away from the corner the casters spawn in, and keeping
  `--acquire-range` small enough that target pursuit does not drag the character back into it.
  A wide acquire range is good for a thin spawn and bad next to something you are avoiding.

### Casters poison, debuff, and burst - and the spawn is often all on their half

Measured 2026-08-30 across ~25 minutes on a caster-heavy hunting ground with a character that had
little or no magic resistance, light armor and a small HP pool. Two different casters were resident
and both hunted the character. What they actually did, none of which the `--include` allowlist
prevents, because they cast from outside melee range:

- **Poison.** `*You feel a bit nauseous*` in the log, `isPoisoned` true in the state file. HP went
  from full to well under half before a bandage cured it. A bandage-only healer has no other answer
  to poison, and it came from a caster, not from a poisonous creature - so "avoid poisonous ground"
  does not mean "avoid dungeons", it means avoid casters too.
- **Weaken and Clumsy.** Str down by several points (and max HP with it) and Dex cut to a fraction.
  Dex is swing speed, so a Clumsy roughly halves the character's whole reason for being there. Both
  expire on their own.
- **Burst.** From full to under half in about twelve seconds, with the escape machinery running
  correctly the whole time. `--flee-hp-pct 45` is not enough margin against that; 60 is.

**The escape does outrun them** (see the correction above - `isRunning: true`, stamina nearly full
after a 59-tile flight), but it does not stop the casting while they are still in range, and it ends
the grinding either way. Over the session the casters cost almost no *health* on average (43 incoming
damage events, all but two of them small) - what they collapsed was the skill gain rate, to roughly a
third, through escapes alone.

**And a hunting ground does not always offer an alternative corner.** The far half of that one was
patrolled for eight minutes and produced zero spawn: every melee target was on the same half the
casters lived on. Splitting the difference was not an option - the choice was the casters' half or
no targets. Check for this before settling in: patrol the quiet half briefly and count spawn.

Adjacent open ground often has spawn of its own and no casters. That is the cheap relocation to
try before anything further afield.

### The contract: the combat loop exits only after escaping something, and the model is invoked

`combat_movement_melee.py` is the only thing that can see its own state, so it is the only thing
that decides when a decision is due - and there is exactly one such moment: **it escaped an
avoided creature and is genuinely clear** (see the three gates below). Then it exits 1 with a
report, and the notification invokes the orchestrator.

**Running out of targets is not a handoff.** A kill must never produce one: the moment a corpse
finishes draining the anchor is dropped, and if nothing happens to be in range that instant a
"ran dry" exit would fire while the spawn it was working is still repopulating around it. Thin
spawn is what the orchestrator's ordinary check-ins are for, not what the reflex should interrupt
a productive session over. An earlier version did exit on idle; it was removed for exactly this.

**Nothing watches this script from the outside either.** A separate watcher was tried and removed:
it had to guess at the combat loop's internal state and could not see `ESCAPE`, so it announced
"idle" eight seconds *before* the escape it was sitting inside actually finished, racing the real
handoff with a worse answer. One authority, one exit, one handoff.

### An avoid-escape ends by handing the decision back, not by resuming

**This is the default now.** When an escape triggered by an avoided creature finally clears,
`combat_movement_melee.py` prints a `HANDOFF:` line and exits 1 instead of returning to `kill`.

It has to, because the reflex cannot solve the problem it is left with. It knows exactly one piece
of ground - the ground it just ran away from - so "resume" means walking back up the corridor it
fled down, past the creature still standing in it. Measured live, that loop ran
for minutes without a swing landing, and every log line looked healthy. (The deleted `patrol.py`
made it strictly worse: it steered back to a waypoint the same creature was sitting on, so dropping
the combat anchor did not break the loop either.)

```
HANDOFF: lost a hostile. we are at (<x>, <y>); it was last seen at (<tx>, <ty>).
Pick up where we left off, keeping at least 10 tiles from (<tx>, <ty>) - that clearance is
the whole detour, do not take a long way round, it costs more grinding time than the
creature does. Just do not retrace the exact line we ran. Its position is a last sighting,
not a fixture: re-scan on arrival.
```

**The detour is `--handoff-berth` (10 tiles), not a journey.** The first wording said "wide berth"
and got what it deserved: an 80-tile trek to the far side of the map and back, which cost more
grinding time than the caster ever did. Ten tiles of clearance is enough not to re-aggro, and
leaves the hunting ground reachable in a single leg.

There is no `--hunt-ground` flag. It used to exist purely to echo a string back into this line,
and it read like "go hunt here" - which the script never did: it fights what comes within
`--acquire-range` and walks nowhere. Where to hunt next is the caller's own context, so the
handoff reports the two coordinates a route actually needs and leaves the destination to whoever
sent the character here.

**The creature it names is the one that drove us off, locked by serial** - the nearest avoided
creature at the moment the escape began, then followed for as long as it stays visible. It is
deliberately not "whichever avoided creature is nearest now": the first live run of this handed
off naming a hostile 90 tiles away that the character had merely run *past* during a
73-tile flight, instead of the creature standing on the hunting ground. Routing around that would
have been routing around nothing.

**Three gates stand between "the screen went quiet" and the handoff**, and all three were paid for
with a death - the character ended an escape the instant the pursuer crossed the 18-tile edge
of the world file, handed off, every mover stood down, and the pursuer (which had never stopped
following) walked back in and killed it where it stood, at 43% HP:

- **`--threat-clear-secs` (15s).** The locked creature must stay *out of the world file* for this
  long. The clock runs from the last sighting, not from the start of the escape, so a pursuer
  flickering at the view edge resets it rather than accruing credit.
- **The character keeps running for that whole window**, rather than holding position the moment
  the screen clears. Distance kept up during the window is what makes the two cases diverge - a
  pursuer reappears, a creature that gave up does not - and standing still is precisely what lets
  the first one close the gap again.
- **HP must be back to `--resume-hp-pct` (80%).** Handing off stops every mover: melee exits and
  the only thing still running is `autoheal.py`, which never moves the character. The next actor able to react is you, at model latency - about
  thirty seconds, measured. Thirty stationary seconds at low HP is not a decision point, it is a
  death. So while hurt the escape simply keeps running, which keeps something steering.

**What to do when you see it**, in order:

0. **Launch `safe_goto.py` immediately, before deciding anything.** This is `uo-navigation`'s
   standing rule - it is always running whenever no combat-movement script is up - and the handoff
   is exactly such a moment. Skipping it is what left a character stationary and unwatched for
   thirty seconds. Its watch mode flees a threat that reappears within a poll; you cannot.
1. **Plan a route that goes around the last sighting**, not through it. Approach the hunting
   ground from a different side than the one you fled toward.
2. **Re-scan on arrival** before relaunching. The coordinate is a *last sighting* of something
   that was walking when you lost it - it is the direction the danger was in, not a fixture. Give
   it a wide berth rather than a tight detour, and expect it to have moved. This is deliberately
   not written to a file anywhere: a stale danger point is worse than none, because it looks
   authoritative. It is good only in the moment it is handed over.
4. If the creature owns the only ground with spawn on it, that is the relocation signal - see the
   caster-heavy-ground note above.

**An avoid-escape always hands off. There is no flag to turn that off** - a
`--no-escape-handoff` existed and was removed, because resuming in place is never the right answer
once there is nothing that walks the character back to spawn. With the roaming script gone (see the
thin-spawn section), "resume" means resuming *wherever the escape ended*, which is routinely tens of
tiles out in empty ground: measured 2026-08-31, an escape ended 68 tiles from the hunting ground and the
reflex then sat in an empty field for minutes, war mode on, re-adopting an unresolvable target every
30s, with every log line looking healthy. Handing off is what makes that a notification instead of a
silent standstill.

HP and swarm escapes are unaffected and still resume on their own: those end because *we*
recovered, not because something is standing somewhere we have to route around.

### Empty ground: the wanted list, and an immediate handoff when it empties

The reflex keeps a running list of what it wants to fight - `Brain.wanted`, refreshed by
`update_wanted` every iteration: every threatening, non-avoided, non-blacklisted mobile **within
`--acquire-range`**. Changes are logged as they happen, so the list is readable in `/tmp/cuolog`:

```
wanted: +a zombie 0x4a2 at (3661, 2580) (3 to fight)
wanted: -a zombie 0x4a2 gone (2 left)
wanted: -a zombie 0x4b0 unreachable (1 left)
```

An entry leaves when its mobile leaves the world file (dead or walked off), is blacklisted (fled,
or struck out as unreachable after `--nopath-strikes`), turns out to be avoid-listed, or drifts
past `--acquire-range` - acquisition never looks further than that, so keeping it would idle
forever (measured live at a graveyard: a skeleton at 15 tiles, the character standing still
indefinitely, no report of any kind).

When the list is empty and nothing is engaged, the run hands off **at once**. There is no combat
grace and no empty timer any more: the old shape waited out 25s since the last swing and then a
10s empty clock, which at a fenced zombie pen (2026-09-11) meant half a minute standing still
after the last candidate was struck out, and the same again after every kill on thin ground.
`--empty-exit-secs` survives as an optional grace on top, default 0.

The handoff says which case it is, because the answers differ:

```
nothing left to fight within 10 tiles at (<x>, <y>) (hp 100%, 1 fightable in view but beyond
acquire range, nearest a <name> at (<tx>, <ty>), 15 tiles) - handing off to pick new ground
```

- **UNREACHABLE** - the spawn is in acquire range but no path exists to any of it (creatures behind
  a fence, a pen, the far bank of a stream). Each one was tried, struck out after
  `--nopath-strikes` (2: one try plus one retry), and blacklisted for 5 minutes; a blacklisted mobile
  is never on the wanted list. Do not relaunch in place - find the way in (a gate, the other side)
  or pick different ground. Measured 2026-09-10 at a fenced spawn pen: before this case existed the
  reflex struck out every creature in the pen, dropped its anchor, waited for the 30s blacklist to
  lapse and started over, indefinitely, with no handoff.
- **beyond acquire range** - the spawn is right there and just needs walking to. Send a `goto`.
- **avoided creature(s) within N** - the spawn here is the tier we refuse to fight. Move on.
- **nothing in view at all** - the ground is genuinely dead.

An engaged target (`lastAttack` still resolvable), an incoming swing in the last 2s, and an anchor
still worth walking back to all take precedence, so a fight in progress never triggers it. Looting
does too: LOOT outranks KILL while the corpse queue has work, so the last kill is drained before
the empty list is read.

### A caster you fled stays the client's target, and gets re-adopted every 30s

`lastAttack` is the client's record, not ours, and fleeing does not clear it. The blacklist that
`do_kill` sets when a target goes unlocatable expires after 30 seconds, and the next iteration
re-adopts the same serial straight off `lastAttack` - logging `target is now 0x... (unresolved)`,
spending `--lost-after` (6s) failing to locate it, and blacklisting it again. Measured live: that
cycle repeated indefinitely against a caster the character had already escaped, so
roughly a fifth of otherwise-idle time went to re-chasing a mobile that was not there.

It is self-limiting rather than fatal - the moment a real target is attacked, `lastAttack` names
that instead and the cycle ends - so it only bites when the fled caster was the *last* thing
attacked and the spawn is thin. Read `target is now <serial> (unresolved)` repeating on a ~30s
cadence as this, not as a live fight, and fix it by getting the character onto a real target
(walk it to fresh spawn yourself) rather than by restarting the script - a restart clears the blacklist but
not `lastAttack`, so the cycle simply resumes.

Do **not** answer it by adding the caster to the allowlist to "just kill it". Such a caster often
has a small max HP pool, which reads as trivial, but each cast takes a large fraction of a small HP
pool while a low-skill melee character needs many swings (tens of seconds) to kill it - the
arithmetic loses badly.

### Scouting a new hunting ground: check the hazard type, not the recommendation

A dungeon level widely recommended as the right difficulty for the character's weapon skill was
exactly that - but the *entrance* level you actually arrive on held two kinds of spawn that
**poison**. For a character whose failure mode is poison (see `uo-death-recovery`), that made it
worse ground than the one it was meant to replace, not better.

Two things worth copying from that trip:

- **A guide describes a destination; the route is yours to survive.** `goto` inside the dungeon
  repeatedly planned straight through one of them - twice to adjacent range - because pathfinding
  optimises distance and knows nothing about what a given character cannot fight. In a dungeon,
  move in short legs and re-scan, rather than issuing one long `goto` across an unmapped level.
- **Scout before committing, and price the scout.** Walking in, scanning, and walking back out
  cost about ten minutes and 4 HP, and it replaced a guess with a fact. That is a good trade; what
  would have been a bad trade is launching the combat reflex there first and finding out during a
  fight.

The general rule: match the ground to the character's *specific* weakness, not to its skill number.
"Correct difficulty for this skill level" was true of that dungeon and irrelevant, because
difficulty was never what was killing the character.
