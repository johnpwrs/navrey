---
name: uo-moongate
description: Travel between cities and facets through a UO moongate - get adjacent to or onto the gate, use it, read the destination gump (single-list on old shards, facet tabs plus a radio list on multi-facet shards), select and press the right controls on the right gump, and close it if you change your mind. Use whenever a destination is in another city, on another island or on another facet (Felucca, Trammel, Ilshenar, Malas, Tokuno, Ter Mur), when a walk would cross water, when findpoi says a place is "on another facet", or when the user mentions a moongate or gate travel.
---

# Travelling by moongate

Requires a logged-in character (see `uo-login`) and a walk to the gate (`uo-navigation`).

A moongate is the only practical way to reach another island - `travel` chains overland legs and
cannot cross water, so a destination on its own island (a couple of thousand tiles away, across
open sea) is a gate trip or nothing.

## 1. Walk to the gate, then get adjacent to it - or stand on it

Gates are POIs:

```bash
echo "closest moongate" >> /tmp/cuocmd; sleep 1; tail -3 /tmp/cuolog
# [POI] <city> Moongate (moongate) at (<x>, <y>, 0)
```

Route there with `safe_goto.py`. **The POI coordinate is the gate's own tile**, so arriving "at"
it usually leaves the character one tile off, which is close enough.

**Distance matters and the error is explicit.** Measured: at `d=1` the gate opens normally; at
`d=2` the server answers

```
[SYSTEM] That is too far away.
```

and nothing happens. If that appears, step onto the gate itself and retry:

```bash
echo "gotoexact <x> <y>" >> /tmp/cuocmd; sleep 2    # the gate's own tile, from `items`
```

`gotoexact`, not `goto` - `goto` settles for the closest reachable tile, which is exactly the tile
that was already too far. Get the gate's real coordinate from `items`, since the POI entry can be
a tile or two out:

```bash
echo "items 5" >> /tmp/cuocmd; sleep 1; tail -6 /tmp/cuolog
#   [fixed] 0x40019CA8 blue moongate    d=1   (<x>, <y>, 5)
```

## 2. Use it (or walk into it)

```bash
echo "use 0x40019CA8" >> /tmp/cuocmd; sleep 2
```

Double-clicking the gate is the reliable form. Walking onto the gate tile also triggers it on most
shards - the same "step into the transition" idea as a dungeon mouth (see `uo-dungeon-entry`) - but
`use` is what this project should do, because it produces the destination gump deterministically
instead of depending on which tile the walk finished on.

## 3. Read the gump - it is label-then-button, and the order is not alphabetical

```bash
echo "gumps" >> /tmp/cuocmd; sleep 1; tail -40 /tmp/cuolog
```

```
[GUMP] local 0x0015FD8B server 0x30638C0D Gump
  Moongate Travel Menu
  <first destination>
  [button 1]
  <another destination>
  [button 8]
  ...
  <another destination>
  [button 7]
  <the one you want>
  [button 13]
```

**The button belongs to the label *above* it.** Getting this backwards sends you somewhere else
entirely: reading the pairs the other way makes the destination you want take the *previous*
entry's button. The safe check is the first entry - `Moongate Travel Menu` is a title with no
button, then the first destination is followed by `[button 1]`.

Do not assume the numbering: it is neither alphabetical nor stable across shards, and the list can
carry shard-specific entries (one shard listed a non-city event destination as the last button).
Always read the gump you actually have rather than reusing a remembered number - this skill
deliberately records no destination-to-button table for that reason.

## 4. Press the button, and confirm by the coordinate jump

```bash
echo "gumpresponse 13" >> /tmp/cuocmd; sleep 2
jq -c '{xy:[.charPosX,.charPosY]}' /tmp/cuostate.json
```

Nothing announces the trip; the proof is the position changing - the departure coordinates are
replaced by the destination's, thousands of tiles away.

**The gump closes itself when a destination is taken** - verified 2026-08-30 by travelling between
two cities and immediately asking: the position jumped to the destination city, and `gumps`
answered `No server gumps open`. So there is nothing to clean up after a successful trip; the
button-0 close below is only for changing your mind.

## 4b. Multi-facet shards: a tabbed gump with radios, and *several* gumps open at once

A multi-facet shard (Felucca, Trammel, Ilshenar, Malas, Tokuno, Ter Mur - whichever of these the
shard has) does not use the flat list above. Measured at a public gate on such a shard:

```
[GUMP] local 0x<serial> server 0x<serial> Gump
  Public Moongate
  [button 100]            <- here the BUTTON comes first and its label follows: these are tabs
  <facet>
  [button 101]
  <facet>
  [button 102]
  <facet>
  [button 103]
  <facet>
  [button 104]
  <facet>
  [button 105]
  <facet>
  [button 106]
  <shard-specific extra tab, e.g. special locations>
  ...
  [radio 200]             <- the destination list for the CURRENT tab: radio then its label
  <destination>
  [radio 201]
  <destination>
  ...
  [button 1]
  GO
```

Three things differ from the single-list gump:

1. **Facet tabs are buttons, destinations are radios.** Pressing a tab button (`gumpresponse 103`)
   makes the server *re-send the gump* with that facet's list; nothing moves. Only then does the
   radio list show the other facet's places - a different facet's tab showed a different, shorter
   list (`[radio 200] <destination>`, `[radio 201] <destination>`). Read it fresh after every tab
   press; the radio ids restart at 200 on every tab, so `201` means a different city on each one.

2. **The trip is a radio plus GO in one response.** Extra numeric arguments to `gumpresponse` are
   the switch ids to send as selected - exactly what the game window sends when a radio is ticked
   and GO is clicked:

   ```bash
   echo "gumpresponse 1 201 gump:0x<server serial>" >> /tmp/cuocmd    # GO (button 1) with the destination you read (radio 201 here)
   sleep 2; jq -c '{x:.charPosX,y:.charPosY,map}' /tmp/cuostate.json
   # {"x":<x>,"y":<y>,"map":3}   <- map changed: 0 Felucca, 1 Trammel, 2 Ilshenar, 3 Malas, 4 Tokuno, 5 Ter Mur
   ```

   The proof is `map` changing in the state file, not just the coordinates.

3. **Name the gump.** Such shards often keep other server gumps open all the time - a global chat
   history window is a common one, and it re-opens itself on every new chat line. With two gumps
   open, a bare `gumpresponse` refuses and lists them; pass `gump:<server serial>` from the `[GUMP]
   local ... server ...` header of the one you mean. This is not optional: measured, the first
   attempt guessed, a tab button landed on the chat gump, and **the server dropped the
   connection**. The `gump:` form is what made the second attempt work.

Putting it together, from any public gate to a town on another facet:

```bash
echo "use <gate serial>" >> /tmp/cuocmd; sleep 2
echo "gumps" >> /tmp/cuocmd; sleep 1                     # find the Public Moongate gump's server serial S
echo "gumpresponse 103 gump:S" >> /tmp/cuocmd; sleep 2   # tab: the facet you want (read the tab buttons, don't assume)
echo "gumps" >> /tmp/cuocmd; sleep 1                     # now read that facet's radios
echo "gumpresponse 1 201 gump:S" >> /tmp/cuocmd; sleep 2 # GO with the radio you read
jq -c '{x:.charPosX,y:.charPosY,map}' /tmp/cuostate.json
```

The POI commands know facets too: `closest` only ever answers for the facet you are on, and
`findpoi` reports a match elsewhere as `on another facet ... not walkable from here` - that line
is the cue to come here. A `goto <name>` for a place on another facet refuses with the same
message rather than walking to a lookalike.

Not every town is in the gate list - a town you expect may be absent from a facet's list, while a
dungeon or a lesser town is present. Gate to the nearest listed destination on that facet and walk
from there.

## 5. Changing your mind: close the gump with button 0

**A gump left open blocks other things** (see the resurrection-gump freeze in
`uo-death-recovery`), so never walk away from an open one. There is no `closegump` command -
button 0 is the close:

```bash
echo "gumpresponse 0" >> /tmp/cuocmd; sleep 1
echo "gumps" >> /tmp/cuocmd; sleep 1; tail -3 /tmp/cuolog
# No server gumps open
```

Verified 2026-08-30 at a city gate: `gumpresponse 0` left the character where it stood,
unmoved, with no gump open. Button 0 is the standard "closed without choosing" response for any
server gump, not just this one.

## Gotchas

- **`d=2` is too far.** The only symptom is `[SYSTEM] That is too far away.` in the log - the
  command itself reports `ok`, because a command that found nothing is still a command that ran.
  Check the log, not the exit code.
- **Read the gump every time.** Button numbers are per-shard and the list can change; a remembered
  number is how you end up in the wrong city. On a tabbed gump the radio ids restart at 200 on
  every tab, so the same number is a different place per facet.
- **Two gumps open means `gump:<serial>`, always.** Pressing a moongate button on some other gump
  (a chat window) disconnected the client outright. `gumpresponse` now refuses to guess; do not
  work around that by closing the other gump blind - it may be something the shard wants open.
- **Getting back is the same trip in reverse**, and the gate at the far end is its own POI -
  `closest moongate` from wherever you land.
- **Bank before a long trip, not after.** Bank boxes are shared between cities, so gold deposited
  in one city is available from the bank in another - but carrying it across an unknown island is
  a loss waiting to happen if the character dies out there.
