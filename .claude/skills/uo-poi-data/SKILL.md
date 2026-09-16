---
name: uo-poi-data
description: Build or extend the point-of-interest (POI) data the client's `poi` / `closest` / `findpoi` / `goto <name>` commands use, in the grid-indexed JSON the client loads. Use whenever a shard has no POI data yet, `closest <category>` says it has nothing, a landmark is missing from lookups, or the user asks to generate, import, convert or add POI/map-marker data.
---

# POI data: what the client loads, and how to make it

`closest <category>`, `findpoi <text>`, `poi` and `goto <name>` all read one index built from the
directory named by `poi_directory` in the repo-root `settings.json`. That is the only source: there
is no built-in fallback, so an unset or missing directory means every lookup answers "no POI data"
and `poi` says which of the two it is. Nothing is downloaded and nothing is shard-aware; the data
must describe the shard you are on.

No POI data ships with the repo at all; the shard packs and the CSVs are yours. The two
converters below live in this skill's directory.

## The format

Every `*.json` in the directory is loaded; anything else is ignored. Each file is one object with a
`cells` map from `"<x/gridSize>,<y/gridSize>"` to an array of points:

```json
{
 "gridSize": 100,
 "bounds": {"minX": 303, "maxX": 5855, "minY": 42, "maxY": 3998},
 "pointCount": 2,
 "cellCount": 1,
 "cells": {
  "13,16": [
   {"x": 1336, "y": 1997, "z": 0, "map": 1, "name": "<city> Moongate", "category": "moongate", "color": "yellow", "major": true},
   {"x": 1332, "y": 1690, "z": 0, "map": 1, "name": "<city> Bank",     "category": "bank",     "color": "yellow"}
  ]
 }
}
```

Per point, the loader (`Capabilities/Poi/PoiIndex.cs`) reads:

| field | required | meaning |
|---|---|---|
| `x`, `y` | yes | world tile |
| `z` | no (0) | floor; only matters for stacked buildings |
| `name` | no | what `findpoi` matches and `goto <name>` accepts |
| `category` | no | what `closest <category>` and `poi` group by - lower-case, one word |
| `map` | no (**any facet**) | 0 Felucca, 1 Trammel, 2 Ilshenar, 3 Malas, 4 Tokuno, 5 Ter Mur. Omit only for single-facet shards; a point without it matches every facet, so a Trammel lookup can land on a Felucca-only landmark. |
| `major` | no | `true` for the notable entries (cosmetic; kept from the marker packs) |
| `color` | no | cosmetic, nothing reads it |

`gridSize`, `bounds`, `pointCount`, `cellCount` are for the generators' own bookkeeping; the loader
walks `cells` and flattens every point into one list, so the cell keys never matter - a
hand-written file with a single cell holding everything is valid.

**Categories are a vocabulary the skills already use.** The names the `uo-*` skills send to
`closest` are `bank`, `healer`, `moongate`, `stables`, `down` (dungeon stairs, at *dungeon-side*
coordinates) and `teleporter`; keep those exact so the skills keep working on a new shard. Any
other category is free-form but should be one lower-case word, consistent within the directory -
the shipped data has both `blacksmith` and `blacksmiths`, `mage` and `mages`, and `closest` treats
those as different categories. `poi` prints the categories the loaded index actually has.

## Generating it

### From a UOAM-style marker pack (most shards)

Many shards publish map markers for the classic world-map tools in UOAM `.map` format, and
ClassicUO's own world map reads the same files. One file per theme (`common.map`, `dungeons.map`,
`atlas.map`, ...), each line:

```
+INN: 5165 30 1 Seeker's Inn
^type   x    y  facet name
```

`+` marks the notable entries (they become `major: true`), `-` the rest. Convert the pack with this
skill's script; the marker type becomes `category` (lower-cased, with a small alias table in the
script for the names the skills expect - `STAIRSDOWN` -> `down`, `STABLE` -> `stables`, ...):

```bash
python3 .claude/skills/uo-poi-data/map_to_poi.py --src <dir of .map files> --out <poi dir>
```

It writes one `<name>.json` per `.map`, prints the point/category counts, and reports every line
it could not parse on stderr. Facet comes from the file, so multi-facet shards work unchanged.

### From a CSV you wrote yourself

For a shard with no marker pack, or to add the handful of places you actually need, write
`x,y,z,name,category,color[,major]` rows (no header; standard CSV quoting) and convert:

```bash
python3 .claude/skills/uo-poi-data/csv_to_json.py <file.csv> ...      # or no args: every *.csv in the cwd
```

It writes `<file>.json` next to each CSV. That converter has no facet column - add
`"map": <n>` to the points afterwards, or hand-write the JSON, on a multi-facet shard.

### Where to point the client

Put the `.json` files in a directory of their own and set, in the repo-root `settings.json`:

```json
"poi_directory": "/absolute/path/to/that/dir"
```

The index loads on first use, not at startup, so a change to the directory needs a client
restart, or at least a first lookup after the files landed.

## Checking it

```bash
printf 'poi\nclosest bank\nfindpoi moongate\n' >> /tmp/cuocmd; sleep 1; tail -12 /tmp/cuolog
```

- `poi` lists the categories and counts that actually loaded, and the load message (`No POI data:
  set "poi_directory"`, `does not exist`, `No .json POI files found in`, or a parse error naming
  the file).
- `closest <category>` answers only for the facet the character is on (`map` in the state file);
  a shard whose points carry no `map` field answers for every facet at once.
- `findpoi` searches every facet and flags a match elsewhere as `on another facet` - the cue that
  a moongate trip is needed (`uo-moongate`).

## Adding a place by hand

`goto <name>` needs the name in the index, so a landmark you found on foot is worth recording:
read the tile from `/tmp/cuostate.json` while standing on it, append a row to your CSV (or a point
to the JSON), reconvert, restart. Shard-specific coordinates belong in the shard's own POI
directory or in a memory note, never in a skill.
