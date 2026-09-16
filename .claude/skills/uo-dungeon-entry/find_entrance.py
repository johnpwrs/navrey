"""Scan the tiles around the character for a dungeon-entrance ladder/stair static.

A dungeon entrance is a server-side teleporter with nothing in the tile data marking it. What *is*
in the tile data is the art you can see in a screenshot: a `Surface, Bridge, Stair*` static, usually
standing on an impassable "void" land tile next to the hole the POI actually points at.

This finds the candidates and ranks them; the screenshot is what confirms which one you are looking
at. It walks nowhere - see SKILL.md for the step that enters.
"""
import argparse
import re
import sys

sys.path.insert(0, "cli")
from uo import script

ap = argparse.ArgumentParser(description=__doc__)
ap.add_argument("--radius", type=int, default=3, help="box half-width to scan around the character")
ap.add_argument("--at", help="scan around x,y instead of the character's own tile")

# `tiles` output lines, e.g.
#   land   0x0244 z=20   Wall, Impassable
#   static 0x089F z=22   h=10  Surface, Bridge, ArticleA, StairRight
TILE_LINE = re.compile(
    r"\s*(land|static)\s+(0x[0-9A-Fa-f]{4})\s+z=(-?\d+)\s+(?:h=(-?\d+)\s+)?(.*)"
)

# The void land tile the hole is drawn as - impassable, so pathing can never touch it.
VOID_LAND = "0x0244"


def scan_tile(uo, x, y):
    land, stairs = None, []
    for line in uo.call(f"tiles {x} {y}", timeout=10.0):
        m = TILE_LINE.search(line)
        if not m:
            continue
        kind, graphic, z, h, flags = m.group(1), m.group(2), int(m.group(3)), m.group(4), m.group(5)
        if kind == "land":
            land = (graphic, flags)
        elif "Bridge" in flags and "Stair" in flags:
            stairs.append((graphic, z, int(h or 0), flags.strip()))
    return land, stairs


@script(ap, require_live=True)
def main(uo, args):
    if args.at:
        cx, cy = (int(v) for v in args.at.replace(",", " ").split())
    else:
        cx, cy = uo.xy

    print(f"scanning {2 * args.radius + 1}x{2 * args.radius + 1} around ({cx}, {cy})\n", flush=True)

    found = []
    for dx in range(-args.radius, args.radius + 1):
        for dy in range(-args.radius, args.radius + 1):
            x, y = cx + dx, cy + dy
            land, stairs = scan_tile(uo, x, y)
            if not stairs:
                continue
            void = land is not None and land[0] == VOID_LAND
            for graphic, z, h, flags in stairs:
                found.append((void, x, y, graphic, z, h, flags, land))

    if not found:
        return uo.stop(f"no Bridge/Stair static within {args.radius} tiles of ({cx}, {cy})")

    # A stair standing on the impassable void land tile is the entrance signature; rank those first.
    found.sort(key=lambda f: (not f[0], abs(f[1] - cx) + abs(f[2] - cy)))
    print("candidates (best first):")
    for void, x, y, graphic, z, h, flags, land in found:
        mark = "ENTRANCE?" if void else "         "
        landtxt = f"land {land[0]} {land[1]}" if land else "land -"
        print(f"  {mark} ({x}, {y})  {graphic} z={z} h={h}  [{flags}]  {landtxt}", flush=True)

    print(
        "\nNext: screenshot the window to confirm which of these is the ladder/cave mouth you can\n"
        "see, then `canwalk` (no arguments) from an adjacent tile - the direction showing a large z\n"
        "jump is the one to `walk`.",
        flush=True,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
