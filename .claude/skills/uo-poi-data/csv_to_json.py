#!/usr/bin/env python3
"""
Convert hand-written CSV point lists into the grid-indexed JSON the client's POI index loads
(see src/ClassicUO.Client/Agent/Capabilities/Poi/PoiIndex.cs). Point "poi_directory" in the
repo-root settings.json at the directory the .json files land in.

CSV format (no header), one row per point, standard CSV quoting:
    x,y,z,name,category,color[,major]
`major` is optional and only ever "1" when present.

Usage:
    python3 csv_to_json.py [--grid-size N] [file.csv ...]

With no file arguments, converts every *.csv in the current directory. Re-run this any
time a CSV changes - it always regenerates the matching .json from scratch, beside the CSV.
"""

import argparse
import csv
import json
import os


def convert(csv_path: str, json_path: str, grid_size: int) -> None:
    points = []
    min_x = min_y = float('inf')
    max_x = max_y = float('-inf')

    with open(csv_path, newline='', encoding='utf-8') as f:
        reader = csv.reader(f)
        for row in reader:
            if not row or len(row) < 6:
                continue
            x = int(row[0])
            y = int(row[1])
            z = int(row[2])
            name = row[3]
            category = row[4]
            color = row[5]
            major = len(row) >= 7 and row[6].strip() == '1'

            min_x = min(min_x, x); max_x = max(max_x, x)
            min_y = min(min_y, y); max_y = max(max_y, y)

            point = {"x": x, "y": y, "z": z, "name": name, "category": category, "color": color}
            if major:
                point["major"] = True
            points.append(point)

    cells = {}
    for p in points:
        cx = p["x"] // grid_size
        cy = p["y"] // grid_size
        key = f"{cx},{cy}"
        cells.setdefault(key, []).append(p)

    out = {
        "gridSize": grid_size,
        "bounds": {"minX": min_x, "maxX": max_x, "minY": min_y, "maxY": max_y},
        "pointCount": len(points),
        "cellCount": len(cells),
        "cells": cells,
    }

    with open(json_path, 'w', encoding='utf-8') as f:
        json.dump(out, f, indent=1)

    print(f"{os.path.basename(csv_path)} -> {os.path.basename(json_path)}: "
          f"{len(points)} points, {len(cells)} cells")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("files", nargs="*", help="CSV files to convert (default: all *.csv in this directory)")
    parser.add_argument("--grid-size", type=int, default=100, help="spatial grid cell size (default: 100)")
    args = parser.parse_args()

    here = os.getcwd()
    csv_paths = args.files if args.files else sorted(
        os.path.join(here, f) for f in os.listdir(here) if f.endswith(".csv")
    )

    if not csv_paths:
        print(f"No .csv files found in {here}")
        return

    for csv_path in csv_paths:
        json_path = os.path.splitext(csv_path)[0] + ".json"
        convert(csv_path, json_path, args.grid_size)


if __name__ == "__main__":
    main()
