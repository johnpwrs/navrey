---
name: uo-bank
description: Open the headless UO character's bank box and deposit/withdraw/list items in it. Use whenever the user asks to go to the bank, bank something, store/withdraw items from the bank, or check bank contents.
---

# Using the bank

Requires a logged-in character with the map loaded (see `uo-login`). Also uses the
`uo-navigation` skill (getting there) and the `uo-containers` skill (once the bank
box is open, it's just a container like any other).

## 1. Walk to a bank

```bash
echo "closest bank" >> /tmp/cuocmd
sleep 1
tail -3 /tmp/cuolog
```
This is a standard POI category (see `uo-navigation` step 1) — returns the nearest
bank's `(x,y,z)`. `goto` there as usual — remembering it returns immediately, so wait for it
(`uo.goto(x, y).wait()`) before expecting to have arrived — or better, use `gonear` (see the
`uo-navigation` skill), since a bank's POI coordinate is commonly a point inside the
building rather than a tile you can actually stand on, and `gonear` walks you to the
closest reachable tile instead of just failing.

**You don't need to get inside the building or find a banker NPC.** Banks are
enclosed structures and `goto`/`gonear` will typically stop just outside, flush
against the exterior wall, unable to route further in — that's fine and expected,
not a failure to fix. Being adjacent to the wall is enough for the next step.

## 2. Say "bank"

```bash
echo "say bank" >> /tmp/cuocmd
sleep 2
tail -8 /tmp/cuolog
```
Practically every shard recognizes `bank` as a keyword speech command (same mechanism
documented in `uo-buy-items` for `<name> buy`) that opens your bank box, no banker NPC
interaction needed — just proximity to the bank building. A successful open shows:
```
<charName>: bank
<charName>: Bank container has N items, M stones
[INFO ] [EQUIP] [<bank_box_serial>] gfx=0x0E7C layer=1D on <player_serial>
[CONTAINER] Opened <bank_box_serial> (gump 0x004A)
[CONTAINER] Contents: N item(s)
```
The bank box briefly attaches to your character as a virtual layer-`0x1D` item while
open — that `[EQUIP]` line is where its serial comes from; grab it from there (or
from the `[CONTAINER] Opened` line right after — same serial either way).

If nothing happens (no reply, no `[CONTAINER]` logs), you're not close enough to a
bank — re-check `closest bank` and `goto` closer to the wall, then retry `say bank`.

## 3. List / deposit / withdraw

**Before trusting what's in the backpack, open it — don't conclude "nothing to
deposit" from a plain `inv`.** The bank box itself arrives already open (its
contents were pushed by the `say bank` reply), but your own backpack's contents are
a separate container that the server only sends once *it's* been double-clicked this
session; until then `inv` reports `Backpack [...] contents (0):` even when it's
full. See the `uo-equip-items` skill's step 1 for the full explanation — the fix is
one extra command:
```bash
echo "inv" >> /tmp/cuocmd; sleep 1                    # get the backpack's own serial (layer 0x15)
echo "use <backpack_serial>" >> /tmp/cuocmd; sleep 2  # open it — this is the step that's easy to skip
echo "inv" >> /tmp/cuocmd; sleep 1                    # now the real contents show up
tail -20 /tmp/cuolog
```

From here it's just a container — hand the bank box serial to the `uo-containers`
skill's commands:
```python
import sys; sys.path.insert(0, "cli")
from uo import Client
uo = Client()

uo.container(bank_serial)                            # list what's in it
uo.move(item, bank_serial, item.amount)              # deposit, confirmed
uo.move(item, uo.backpack_serial, item.amount)       # withdraw, confirmed
```

**`move` confirms the item arrived in the destination, not that it left the source.** Those are not
the same check, and the difference is not academic: an item is briefly absent from the backpack
while being carried, so "gone from the backpack" is also satisfied by a deposit that bounces
straight back — which is exactly what happened here against a bank box the client no longer had
open. Only the destination can confirm a move.
Withdrawing is the same `drop` command in reverse — move an item out of the bank box
serial and into your own backpack serial (from `inv`, layer `0x15`). Always follow
up with `container <bank_box_serial>` (or `inv` for withdrawals) to confirm the move
actually landed, per the `uo-containers` skill's verification step.

**If more than one item matches what the user asked for** (e.g. two robes of
different hues), don't guess — ask which one before dropping anything into the bank.

## Typical full workflow

```bash
echo "closest bank" >> /tmp/cuocmd; sleep 1
tail -3 /tmp/cuolog                              # get (x,y)

echo "gonear <x> <y>" >> /tmp/cuocmd; sleep 5    # walk there (poll as usual) — see uo-navigation

echo "say bank" >> /tmp/cuocmd; sleep 2
tail -8 /tmp/cuolog                               # confirm open, get bank box serial

echo "inv" >> /tmp/cuocmd; sleep 1                    # get backpack's own serial (layer 0x15)
echo "use <backpack_serial>" >> /tmp/cuocmd; sleep 2  # open the backpack — don't skip this
echo "inv" >> /tmp/cuocmd; sleep 1                    # now see what's actually in there

echo "drop <item_serial> <bank_box_serial> <amt>" >> /tmp/cuocmd; sleep 2
echo "container <bank_box_serial>" >> /tmp/cuocmd; sleep 1
tail -10 /tmp/cuolog                              # confirm deposit - check the BANK, not the backpack
```
