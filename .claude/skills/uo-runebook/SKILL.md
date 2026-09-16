---
name: uo-runebook
description: Get and use a runebook with the UO client - fill it with marked recall runes, charge it with recall scrolls, mark a rune where you stand, and recall to a stored spot from its gump. Use whenever the user asks to recall, mark a rune, set up a runebook, travel home instantly, or add a location to the book.
---

# Runebooks

Requires a logged-in character (`uo-login`). A runebook is instant travel without a moongate: it
holds up to 16 marked recall runes and up to 18 charges, and one button recalls to a stored rune.

## 1. Getting one

Runebooks are crafted by scribes, so NPC vendors rarely sell them; player vendors do. On shards
with a global vendor market the fastest route is the search gump from your own context menu
(`contextmenu <charID>` -> `contextpick <charID> <entry>` for "Vendor Search"), then
`gumpresponse 1 text:1=runebook gump:<serial>` and Buy Now on a result - see the memory notes for
the shard's exact ids. Otherwise `uo-buy-items` at a scribe or a player vendor.

Buy the consumables while there: **recall scrolls** (each becomes one charge) and **mark scrolls**
(each marks one rune), and a **recall rune** or two to mark. A rune sold as "Shop Recall Rune" or
similar is already marked to the seller's shop; it still works, and can be re-marked.

## 2. Reading the book: `use <book>`, then `gumps`

```
[GUMP] local 0x... server 0x00000059 Gump
  [button 0]                       <- close
  Charges  Max Charges  5  18
  [button 1]  Rename book
  [button 10] ... [button 25]      <- the 16 rune slots: press one to RECALL to that rune
  [button 10]  83° 24'S, 152° 9'E  <- a filled slot repeats its button beside the rune's sextant coords
  [button 200]  Drop rune          <- 200 + slot: take that rune back out
  [button 400]                     <- 400 + slot: REPLACE that rune (opens a target cursor - `canceltarget` if unwanted)
```

The gump's server serial is the runebook gump's own type id, not the item's; pass it with `gump:`
when other server gumps are open (a shard's chat window, a vendor search). The travel buttons
carry localized captions the dump cannot show, so the numbers above are the map. Measured
2026-09-11: button 10 cast Recall and consumed one charge; button 400 asked for a rune to replace.

## 3. Charging and filling it

Both are plain item moves onto the book - the server does the rest:

```bash
echo "drop <recall scroll stack> <book> <n>" >> /tmp/cuocmd   # +n charges, up to 18
echo "drop <marked rune> <book>" >> /tmp/cuocmd               # fills the next free slot
```

Confirm with `use <book>` and `gumps`: the charge count and a new coordinate line.

## 4. Marking a rune where you stand

A Mark scroll targets a rune and stamps your current location on it:

```python
uo.use_on(mark_scroll_serial, rune_serial)   # waits for the target cursor, answers it
```

The log shows `[SPELL] <name>: Kal Por Ylem`, and the rune's tooltip in the state file changes to
`A Recall Rune For <place>`. Whether a scroll cast succeeds at low Magery is a shard rule: the
standard rules fizzle scroll casts below the scroll's skill floor, but the shard this was
measured on marked fine at 0.0 Magery. Read the rune's name afterwards rather than assuming.
Mark **before** dropping the rune in the book; a rune inside the book is not targetable.

## 5. Recalling

```bash
echo "use <book>" >> /tmp/cuocmd; sleep 2
echo "gumpresponse 10 gump:0x00000059" >> /tmp/cuocmd     # slot 1; 11 = slot 2, ...
sleep 2; jq -c '{x:.charPosX,y:.charPosY,map}' /tmp/cuostate.json
```

The proof is the position jump; nothing announces the arrival. The cast is Recall, so it obeys
Recall's rules: not while overweight, not out of a house you do not own, not across facets it
forbids, and the usual casting delay - stand still for the seconds it takes. A charge is used
whether or not the character has Magery; with zero Magery it still worked here (charges are
scroll-strength casts). Check the charge count when a press seems to do nothing.

## Gotchas

- **Pressing a rune slot recalls immediately.** There is no detail page - the first press is
  the cast. Read the coordinate line beside the button before pressing.
- **Coordinates are sextant, not tiles.** `83° 24'S, 152° 9'E` is what the gump shows; the tile
  it maps to is whatever you marked. Keep your own note of slot -> place.
- **`drop` moves, `use_on` targets.** Charging and filling are drops onto the book; marking is a
  scroll used *on* a rune. Mixing them up wastes a scroll.
- **The rune name is the truth.** After a mark, read the rune's `props` in the state file's
  backpack list; a failed mark leaves the old name.
