---
name: uo-equip-items
description: Equip or unequip items on the headless UO character, including opening the backpack so its contents are visible. Use whenever the user asks to wear/equip/unequip/remove gear, check inventory, or move items to/from the backpack.
---

# Equipping and unequipping items

Requires a logged-in character with the map loaded (see the `uo-login` skill).

## 1. Open the backpack before trusting `inv`

UO never sends a container's contents automatically — the server only sends them
(packet `0x3C`) after the container is actually opened (double-clicked). Until
you do that, the client's local item list has no idea what's inside the
backpack, and `inv` will report `Backpack [...] contents (0):` even when it's
full.

Find the backpack's serial from the `inv` equipped list (layer `15`), then
open it:

```bash
echo "inv" >> /tmp/cuocmd            # shows equipped items, including the backpack's own serial (layer 15)
sleep 1
tail -15 /tmp/cuolog
echo "use <backpack-serial>" >> /tmp/cuocmd   # double-click = open
sleep 2
echo "inv" >> /tmp/cuocmd            # now contents will actually populate
sleep 1
tail -20 /tmp/cuolog
```

The `inv` command prints each backpack item's graphic, hue, and (once
tiledata-name lookup is in place) its resolved `Layer` and `Name` — use those
to figure out what a raw graphic ID actually is instead of guessing.

## 2. Know the real layer numbers

Layers are **not** what you'd guess from casual naming. This is the actual
enum (`src/ClassicUO.Client/Game/Data/Layers.cs`) — a common trap is assuming
`0x0B` is a ring or a robe; it's actually **Hair**, and hair can't be moved to
a backpack (it isn't a real removable item), so trying to `unequip` it will
silently do nothing forever.

| Hex  | Layer      | Hex  | Layer     |
|------|-----------|------|-----------|
| 0x01 | OneHanded | 0x0F | Face      |
| 0x02 | TwoHanded | 0x10 | Beard     |
| 0x03 | Shoes     | 0x11 | Tunic     |
| 0x04 | Pants     | 0x12 | Earrings  |
| 0x05 | Shirt     | 0x13 | Arms      |
| 0x06 | Helmet    | 0x14 | Cloak     |
| 0x07 | Gloves    | 0x15 | Backpack  |
| 0x08 | Ring      | 0x16 | Robe      |
| 0x09 | Talisman  | 0x17 | Skirt     |
| 0x0A | Necklace  | 0x18 | Legs      |
| **0x0B** | **Hair (not ring/robe!)** | 0x19 | Mount |
| 0x0C | Waist     |      |           |
| 0x0D | Torso     |      |           |
| 0x0E | Bracelet  |      |           |

If unsure what's actually equipped where, don't assume from the layer number
alone — cross-check the item's `Graphic` against tiledata (the `inv` output's
`Name` column) before deciding it's junk or a real removable item.

## 3. Unequip an item

```bash
echo "unequip <serial>" >> /tmp/cuocmd   # alias: topack — moves the item to the backpack
sleep 2
echo "inv" >> /tmp/cuocmd
sleep 1
tail -10 /tmp/cuolog   # confirm it actually moved — see caveat below
```

**Always verify with a follow-up `inv`.** The command logs "Moving X to
backpack" unconditionally the moment it's sent — that's just an echo of
intent, not confirmation the server accepted it. If the item is still listed
under "Equipped" afterward, it didn't actually move (commonly because it's not
a real wearable at that layer — see the Hair trap above — or because of
network lag; retry once before concluding something's wrong).

## 4. Equip an item from the backpack

```bash
echo "wear <serial> <layer_hex>" >> /tmp/cuocmd
sleep 2
echo "inv" >> /tmp/cuocmd
sleep 1
tail -15 /tmp/cuolog
```

The layer you pass is a hint, not gospel — the server places the item on
whatever layer its own item data says it belongs to. For example, requesting
layer `01` (OneHanded) for a two-handed weapon (a bow, say) gets silently corrected
to `02` (TwoHanded) server-side. Always confirm the actual placement with `inv` afterward rather
than trusting the layer you requested.

**Always sleep at least 1.5s between equip commands when equipping more than
one item.** The server throttles actions taken too close together and answers
with `System: You must wait to perform another action.` — the command still
logs `[CMD-END] ... ok`, so nothing about the reply flags the failure, and the
item is silently left in the backpack. This has been observed even when the
next line in the log claims `Equipped <serial>` for a *later* command in the
same burst — that message is not reliable proof that specific `wear` landed;
only a follow-up `inv` is. Never queue several `wear`/`unequip` commands back
to back with `printf` and one shared `sleep` — space each one out:

```bash
echo "wear <serial1> <layer1>" >> /tmp/cuocmd
sleep 1.5
echo "wear <serial2> <layer2>" >> /tmp/cuocmd
sleep 1.5
echo "inv" >> /tmp/cuocmd   # confirm both actually landed
sleep 1
tail -20 /tmp/cuolog
```

## Typical full workflow

```bash
printf 'inv\n' >> /tmp/cuocmd; sleep 1
printf 'use <backpack-serial>\n' >> /tmp/cuocmd; sleep 2   # open it so contents show up
printf 'inv\n' >> /tmp/cuocmd; sleep 1                      # now see what's actually in there
printf 'unequip <old-item-serial>\n' >> /tmp/cuocmd; sleep 2
printf 'wear <new-item-serial> <layer_hex>\n' >> /tmp/cuocmd; sleep 2
printf 'inv\n' >> /tmp/cuocmd; sleep 1                      # confirm final equipped state
tail -20 /tmp/cuolog
```
