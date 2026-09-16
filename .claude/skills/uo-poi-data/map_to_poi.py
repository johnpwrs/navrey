#!/usr/bin/env python3
"""
Convert ClassicUO world-map marker files (UOAM ".map" format) into the grid-indexed POI JSON
the headless client loads (see src/ClassicUO.Client/Agent/Capabilities/Poi/PoiIndex.cs).

Source line shape (first line of every file is a bare "3", the UOAM version marker):

    +TERRAIN: 2500 42 0 Crescent Mountain
    -INN: 5165 30 1 Seeker's Inn
    ^^^^^^^^  ^^^^ ^^ ^ ^^^^^^^^^^^^
    |  type   x    y  facet name (rest of line)
    +/- : "+" markers are the ones UOAM shows by default - the notable ones. They become major:true.

Facet is the map index the client reports as `map` in /tmp/cuostate.json (0 Felucca, 1 Trammel,
2 Ilshenar, 3 Malas, 4 Tokuno, 5 Ter Mur). The source files list a Felucca/Trammel landmark twice,
once per facet; both are kept, each carrying its own `map`, so a lookup on Trammel never lands on
an Ilshenar coordinate.

Output: one <name>.json per input .map, in the same shape csv_to_json.py produces, plus a `map`
field on every point. Category is the marker type lower-cased, with a small alias table so the
names the uo-* skills already use (`closest bank`, `closest moongate`, `closest healer`,
`closest down`, `closest teleporter`, `closest stables`) keep working.

Usage:
    python3 map_to_poi.py --src <dir of .map files> --out <poi dir> [--grid-size N]

Point "poi_directory" in the repo-root settings.json at --out. Re-run it whenever the marker pack
updates; it regenerates every .json from scratch.
"""

import argparse
import json
import os
import re
import sys

LINE = re.compile(r'^([+-])([A-Za-z]+):\s+(-?\d+)\s+(-?\d+)\s+(\d+)\s*(.*)$')

# Marker type (upper-cased) -> category. Anything not listed is the type lower-cased.
ALIASES = {
    "STAIRSDOWN": "down",
    "STAIRSUP": "up",
    "STABLE": "stables",
    "JEWELLER": "jeweler",
    "THEATRE": "theater",
    "POINTOFINTEREST": "interest",
    "BODYOFWATER": "water",
    "CHAMPIDOL": "champion",
    "TENT": "camp",
}

# Colour is cosmetic (nothing in the client reads it); keep the convention the hand-made data
# used - dungeon points red, everything else yellow.
FILE_COLORS = {"dungeons": "red"}


def parse(path):
    """Yield (major, type, x, y, facet, name) for every marker line; report the ones we skip."""
    with open(path, encoding="utf-8", errors="replace") as f:
        for lineno, raw in enumerate(f, 1):
            line = raw.rstrip("\r\n")
            if not line.strip() or line.strip() == "3":
                continue
            m = LINE.match(line)
            if not m:
                print(f"  skip {os.path.basename(path)}:{lineno}: {line!r}", file=sys.stderr)
                continue
            sign, typ, x, y, facet, name = m.groups()
            name = " ".join(name.split())
            if not name:
                name = typ.title()
            yield sign == "+", typ.upper(), int(x), int(y), int(facet), name


def convert(map_path, out_dir, grid_size):
    base = os.path.splitext(os.path.basename(map_path))[0].lower()
    color = FILE_COLORS.get(base, "yellow")

    points = []
    seen = set()
    for major, typ, x, y, facet, name in parse(map_path):
        key = (x, y, facet, typ, name)
        if key in seen:
            continue  # the packs contain a few literal duplicate lines
        seen.add(key)
        point = {
            "x": x, "y": y, "z": 0, "map": facet,
            "name": name,
            "category": ALIASES.get(typ, typ.lower()),
            "color": color,
        }
        if major:
            point["major"] = True
        points.append(point)

    if not points:
        print(f"{os.path.basename(map_path)}: no points, nothing written")
        return 0

    cells = {}
    for p in points:
        cells.setdefault(f"{p['x'] // grid_size},{p['y'] // grid_size}", []).append(p)

    out = {
        "source": os.path.basename(map_path),
        "gridSize": grid_size,
        "bounds": {
            "minX": min(p["x"] for p in points), "maxX": max(p["x"] for p in points),
            "minY": min(p["y"] for p in points), "maxY": max(p["y"] for p in points),
        },
        "pointCount": len(points),
        "cellCount": len(cells),
        "cells": cells,
    }

    out_path = os.path.join(out_dir, base + ".json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1)

    cats = sorted({p["category"] for p in points})
    print(f"{os.path.basename(map_path)} -> {os.path.basename(out_path)}: "
          f"{len(points)} points, {len(cells)} cells, {len(cats)} categories")
    return len(points)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--src", required=True, help="directory holding the UOAM-format .map files")
    ap.add_argument("--out", required=True, help="directory to write <name>.json into (the poi_directory)")
    ap.add_argument("--grid-size", type=int, default=100)
    args = ap.parse_args()

    maps = sorted(os.path.join(args.src, f) for f in os.listdir(args.src) if f.lower().endswith(".map"))
    if not maps:
        sys.exit(f"no .map files in {args.src}")
    os.makedirs(args.out, exist_ok=True)

    total = sum(convert(m, args.out, args.grid_size) for m in maps)
    print(f"total: {total} points from {len(maps)} file(s) -> {args.out}")


if __name__ == "__main__":
    main()
