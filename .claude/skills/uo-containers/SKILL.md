---
name: uo-containers
description: List a container's contents and move items into/out of any container (backpack, bank box, chest, another container) with the UO client. Use whenever the user asks to put/store/move/drop an item into something, list what's inside a container/chest/box, or otherwise manage inventory beyond the player's own backpack.
---

# Working with containers

Requires a logged-in character with the map loaded (see the `uo-login` skill).

## 1. A container's contents are unknown until you open it

UO never sends a container's contents automatically — the server only sends them
(packet `0x3C`) after the container is actually opened (double-clicked, `use`). Until
that happens, the client has no idea what's inside, and `container <serial>` will
report 0 items even if it's actually full. **Always open a container at least once
this session before concluding from a listing that it's empty** — including your own
backpack via plain `inv`; "0 items" from an unopened container is not evidence of
anything.

```bash
echo "use <container_serial>" >> /tmp/cuocmd   # double-click = open
sleep 2
echo "container <container_serial>" >> /tmp/cuocmd
sleep 1
tail -10 /tmp/cuolog
```

This is exactly the same caveat the `uo-equip-items` skill documents for the
player's own backpack — it applies identically to any other container.

## 2. Finding a container's serial

- **Your own backpack/equipped items** — `inv` (layer `0x15` is the backpack).
- **Any container you just opened** — since [EQUIP]/[CONTAINER] events log at Info
  level by default (no `verbose` needed), the serial appears right in the log the
  moment you interact with it:
  ```
  [INFO ] [CONTAINER] Opened: 4046C557 gump=004A
  [INFO ] [CONTAINER] Contents: 1 items
  ```
  or, for a bank box specifically opened via `say bank` (see the `uo-bank` skill),
  an `[EQUIP] [...] layer=1D` line right before it — the bank box is attached as a
  virtual layer-`0x1D` item on your character while it's open.
- **A ground item's container** (e.g. a chest) — `items [range]` lists ground items
  with their serials; open one with `use <serial>` to reveal its contents/own serial
  the same way.

## 3. List what's inside

```bash
echo "container <serial>" >> /tmp/cuocmd    # alias: contents
sleep 1
tail -10 /tmp/cuolog
```
Prints every tracked item whose `Container` is that serial — graphic, amount, hue,
and resolved name, e.g.:
```
Container [4046C557] contents (1):
  [4046977E] Graphic:0x0E21 Amt:10 Hue:0000 "clean bandage%s%"
```
This works for **any** container by serial, not just your own — `inv` is limited to
your own equipped items + backpack specifically.

## 4. Move an item into a container

```bash
echo "drop <item_serial> <container_serial> [amount]" >> /tmp/cuocmd
sleep 2
echo "container <container_serial>" >> /tmp/cuocmd
sleep 1
tail -10 /tmp/cuolog
```
`amount` defaults to 1 — pass it explicitly to move a whole stack (e.g. `drop
4046977E 4046C557 10` for a stack of 10 bandages). Under the hood this is a
pickup-then-drop (packets `0x07` then `0x08`) with a short delay between them, same
mechanics as a real client dragging an item between windows.

**Always verify with a follow-up `container <serial>`** (or `inv` if the
destination is your own backpack) — like `unequip`/`wear` in the equip-items skill,
`drop` logs "Moved X into Y" the instant it's sent, which is only confirmation the
packets went out, not that the server accepted the move. Watch for a fresh
`[CONTAINER] Item added to <container>: [...]` log line as the real confirmation.

To move an item into **your own** backpack specifically (e.g. unequipping something
loose, or grabbing a ground item), the shorter dedicated commands already do this:
- `get`/`pickup`/`grab <serial>` — ground item → your backpack
- `unequip`/`topack <serial>` — equipped item → your backpack

`drop` is the general form for everything else (container → container, backpack →
someone else's container, etc.).

## Typical full workflow

```bash
printf 'use <src_container>\n' >> /tmp/cuocmd; sleep 2       # open source, populate its contents
printf 'container <src_container>\n' >> /tmp/cuocmd; sleep 1 # see what's in it, get item serials
tail -20 /tmp/cuolog

printf 'drop <item_serial> <dst_container> <amt>\n' >> /tmp/cuocmd; sleep 2
printf 'container <dst_container>\n' >> /tmp/cuocmd; sleep 1 # confirm it actually landed
tail -10 /tmp/cuolog
```
