"""Tile geometry shared by the movement reflexes: where to run to, where to walk to.

Everything is chebyshev (king-move) distance, the metric the game uses for range.
"""

from __future__ import annotations

from typing import Optional, Sequence, Tuple

from uo.avoidance import chebyshev

Point = Tuple[int, int]
# (position, clearance to keep from it)
KeepAway = Sequence[Tuple[Point, int]]

_COMPASS = [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)]
ESCAPE_PROBE_TILES = 6   # tiles of an escape route sampled for threats, beyond its endpoint


def away_direction(me: Point, threat: Point) -> Point:
    """Unit compass step from `threat` through `me`. East if standing on it."""
    dx = (me[0] > threat[0]) - (me[0] < threat[0])
    dy = (me[1] > threat[1]) - (me[1] < threat[1])
    return (dx, dy) if (dx or dy) else (1, 0)


def safe_point(point: Point, keepaway: KeepAway) -> bool:
    return all(chebyshev(point, spot) >= clearance for spot, clearance in keepaway)


def pick_pursuit_point(me: Point, target: Point, stop_distance: int,
                       keepaway: KeepAway = ()) -> Optional[Point]:
    """A point on the line toward `target`, `stop_distance` short of it, that clears `keepaway`.

    Falls back to the compass direction that makes the most progress while clearing; None when
    nothing clears (hold for a tick rather than crowd an avoided mobile)."""
    mx, my = me
    raw_dx, raw_dy = target[0] - mx, target[1] - my
    dist = max(abs(raw_dx), abs(raw_dy))
    steps = max(dist - stop_distance, 0)

    if dist == 0:
        straight = (mx, my)
    else:
        t = steps / dist
        straight = (mx + round(raw_dx * t), my + round(raw_dy * t))

    if not keepaway or safe_point(straight, keepaway):
        return straight

    def candidate(d):
        return (mx + d[0] * steps, my + d[1] * steps)

    for direction in sorted(_COMPASS, key=lambda d: chebyshev(candidate(d), target)):
        if safe_point(candidate(direction), keepaway):
            return candidate(direction)
    return None


def pick_escape_point(me: Point, threats: Sequence[Point], distance: int,
                      exclude: frozenset = frozenset()) -> Tuple[Point, Point]:
    """`(point, direction)` `distance` tiles out along the compass direction whose whole route
    stays furthest from every threat; ties prefer opening the picture up, then straight back.

    `exclude` holds directions already found unreachable; when all are excluded it starts over."""
    mx, my = me

    def candidate(d):
        return (mx + d[0] * distance, my + d[1] * distance)

    ideal = away_direction(me, min(threats, key=lambda q: chebyshev(q, me))) if threats else (0, 0)

    def score(d):
        if not threats:
            return (999, 999, 0)
        probe = [(mx + d[0] * k, my + d[1] * k)
                 for k in range(1, min(distance, ESCAPE_PROBE_TILES) + 1)] + [candidate(d)]
        clearance = min(chebyshev(p, q) for p in probe for q in threats)
        spread = sum(chebyshev(candidate(d), q) for q in threats)
        alignment = -(abs(d[0] - ideal[0]) + abs(d[1] - ideal[1]))
        return (clearance, spread, alignment)

    usable = [d for d in _COMPASS if d not in exclude] or list(_COMPASS)
    best = max(usable, key=score)
    return candidate(best), best
