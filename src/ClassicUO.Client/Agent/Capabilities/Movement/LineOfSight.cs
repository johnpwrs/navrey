// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Agent.Capabilities
{
    /// <summary>
    /// Whether one tile can see another.
    ///
    /// ClassicUO has no line-of-sight helper at all - it never needs one, because a human player
    /// simply looks at the screen. An agent cannot, and it matters: archery and spells require line
    /// of sight, so "attack the nearest hostile" picks a target behind a wall without this and then
    /// stands there missing.
    ///
    /// Ported from the standalone client: walk the tiles between the two points, interpolating Z,
    /// and block on anything flagged NoShoot or Wall within a band around the sight line.
    /// </summary>
    internal static class LineOfSight
    {
        /// <summary>Vertical slack below the sight line before an obstruction stops mattering.</summary>
        private const int BELOW_SLACK = 2;

        /// <summary>How far above the sight line an obstruction must start to be overhead.</summary>
        private const int ABOVE_SLACK = 6;

        public static bool HasLineOfSight(World world, int fromX, int fromY, int fromZ, int toX, int toY, int toZ)
        {
            if (world?.Map == null)
            {
                return false;
            }

            int dx = Math.Abs(toX - fromX);
            int dy = Math.Abs(toY - fromY);
            int steps = Math.Max(dx, dy);

            if (steps == 0)
            {
                return true;
            }

            // Eye level rather than foot level - a character sees over a low fence.
            int fromEye = fromZ + 14;
            int toEye = toZ + 14;

            for (int i = 1; i < steps; i++)
            {
                int x = fromX + (int)Math.Round((toX - fromX) * (double)i / steps);
                int y = fromY + (int)Math.Round((toY - fromY) * (double)i / steps);
                int z = fromEye + (int)Math.Round((toEye - fromEye) * (double)i / steps);

                if (Blocks(world, x, y, z))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Describes each tile along the sight line - the `debuglos` command.</summary>
        public static string Describe(World world, int fromX, int fromY, int fromZ, int toX, int toY, int toZ, int step, int steps)
        {
            int x = fromX + (int)Math.Round((toX - fromX) * (double)step / steps);
            int y = fromY + (int)Math.Round((toY - fromY) * (double)step / steps);
            int z = fromZ + 14 + (int)Math.Round((toZ - fromZ) * (double)step / steps);

            return $"  ({x}, {y}) z~{z}  {(Blocks(world, x, y, z) ? "BLOCKED" : "clear")}";
        }

        private static bool Blocks(World world, int x, int y, int z)
        {
            var head = world.Map.GetTile(x, y, true);

            if (head == null)
            {
                // Unknown terrain: treat as clear rather than inventing a wall.
                return false;
            }

            var obj = head;

            while (obj.TPrevious != null)
            {
                obj = obj.TPrevious;
            }

            for (; obj != null; obj = obj.TNext)
            {
                int bottom;
                int height;
                bool blocking;

                switch (obj)
                {
                    case Static st:
                        blocking = (st.ItemData.IsNoShoot || st.ItemData.IsWall)
                                   && !st.ItemData.IsSurface && !st.ItemData.IsBridge;
                        bottom = st.Z;
                        height = Math.Max(1, (int)st.ItemData.Height);
                        break;

                    case Multi multi:
                        blocking = (multi.ItemData.IsNoShoot || multi.ItemData.IsWall)
                                   && !multi.ItemData.IsSurface && !multi.ItemData.IsBridge;
                        bottom = multi.Z;
                        height = Math.Max(1, (int)multi.ItemData.Height);
                        break;

                    case Item item when !item.IsDestroyed:
                        // A closed door blocks sight even though the planner walks through it.
                        blocking = (item.ItemData.IsNoShoot || item.ItemData.IsWall)
                                   && !item.ItemData.IsSurface && !item.ItemData.IsBridge;
                        bottom = item.Z;
                        height = Math.Max(1, (int)item.ItemData.Height);
                        break;

                    default:
                        continue;
                }

                if (!blocking)
                {
                    continue;
                }

                int top = bottom + height;

                // Only things that actually occupy the sight line block it. A floor is below it and
                // a roof is above it, and both are flagged NoShoot - treating either as an
                // obstruction (which a simple "within N of eye level" band does) makes every
                // indoor sight line fail, since you are always standing between the two.
                if (top <= z - BELOW_SLACK || bottom >= z + ABOVE_SLACK)
                {
                    continue;
                }

                return true;
            }

            return false;
        }
    }
}
