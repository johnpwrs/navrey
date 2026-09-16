---
name: uo-dungeon-entry
description: Get inside a dungeon, cave, mine or sewer whose entrance goto cannot reach, by screenshotting the game window to see the ladder/stairs/cave mouth and stepping onto it. Use when a dungeon POI is "arrived" but the character is still outside, when goto or gotoexact will not enter a dungeon, cave, mine or sewer, or when the user asks to go down into one.
---

# Entering a dungeon

Requires a logged-in character with a **visible window** (`uo-login`; `--headless` has nothing to
screenshot). Read `uo-navigation` first for how `goto` and `.nav` behave.

This is a **narrow skill for one stubborn problem**. Do not reach for the screenshot anywhere else -
`/tmp/cuostate.json`, `/tmp/cuoworld.json` and the `tiles` command answer everything a normal task
needs, far faster. The picture earns its place here and essentially nowhere else.

## Why goto can never do this

A dungeon entrance is a **server-side teleporter, and nothing in the tile data marks it**. There is
no flag to query, so the client cannot plan a route through it and `canwalk` cannot explain the
refusal. Worse, the POI coordinate points at the *hole*, which is genuinely impassable:

| Tile | Data | On screen |
|---|---|---|
| (1492, 1641) — what `findpoi sewer` returns | land `0x0244` **Wall, Impassable**, no surface | the black hole |
| (1492, 1640) — **the actual entrance** | land `0x0244` **Wall, Impassable** + static `0x089F` z=22 h=10 `Surface, Bridge, StairRight` | the wooden ladder |

So `goto <dungeon poi>` reports `arrived` while the character stands outside, forever. The entrance
is the **ladder/stair static beside the hole**, and you get in by walking onto that tile.

## The procedure

**1. Walk to the POI.** `findpoi sewer` / `closest dungeon`, then a plain `goto`. It will settle
next to the hole; that is the right place to start.

**2. Find the candidate tiles.**

```bash
python3 .claude/skills/uo-dungeon-entry/find_entrance.py --radius 3
```

It scans a box around the character and ranks every `Surface, Bridge, Stair*` static, marking
`ENTRANCE?` for the ones standing on the impassable void land tile `0x0244`. Entrance graphics seen
so far: `0x089F`, `0x08A1`, `0x07A6`.

**3. Screenshot, and look.**

```bash
.claude/skills/uo-dungeon-entry/window_shot.sh /tmp/cuoshot.png
```

Then Read that PNG. Step 2 routinely returns **four candidates** (a symmetric pair of ladders at
each end of the hole) and the tile data cannot tell them apart - the picture can. Find the character
sprite, then the ladder / stone steps / cave mouth touching it, and match it to a listed coordinate.
Screen geometry: **+X is right-and-down, +Y is left-and-down**, one tile ≈ 44 px (88 px on a retina
display).

**4. Step onto it.** The teleport fires on *entering* the tile:

```bash
echo "gotoexact <ladder x> <ladder y>" >> /tmp/cuocmd
```

Confirm by reading the position - it jumps to dungeon coordinates (Britain sewers: 6032, 1499,
z=31), far outside the overworld range.

**5. Dismiss the entry gump before doing anything else.** Arriving pops *"Warning: monsters may
attack you on sight down here in the dungeons!"*. **A `walk` sent while it is up is silently
dropped** - a step that looks blocked for no reason is usually this.

```bash
echo "gumpresponse 0" >> /tmp/cuocmd
```

## Getting back out

Identical move in reverse, and usually no screenshot needed. From the landing tile:

```bash
echo "canwalk" >> /tmp/cuocmd        # no arguments
```

`canwalk` lists all eight directions with the z you would land on. **The way out is the direction
with a large z jump** - out of the Britain sewers it reads `walk west -> ok (6031, 1499, 42)` from
z=31. One `walk w` and the character is back outside.

## The trap to avoid

**`canwalk <x> <y>` does not test that tile.** Both forms report *the player's own eight step
options*; the coordinates only change the header line. `canwalk 1492 1640` tells you nothing about
(1492, 1640). Use the no-argument form from an adjacent tile, or just step and read the position.

## Where the pieces live

| | |
|---|---|
| `find_entrance.py` | scans a box, ranks `Bridge/Stair` statics, flags those on void land |
| `window_shot.sh` | captures the ClassicUO window alone (`-x -o -l <id>`) |
| `winlist.swift` | window id via CGWindowList - pyobjc is absent and System Events is refused ("not allowed assistive access"), swift needs no permission |

The window id changes every client run, so `window_shot.sh` looks it up on each call.
