// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Data;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Compass names for UO's direction ordinals. The enum's own names are unusable in output -
    /// the diagonals are called Right/Down/Left/Up, and Up (7) shares its value with Mask, so
    /// ToString() prints "Mask" for north-west.
    /// </summary>
    internal static class Directions
    {
        public static bool TryParse(string text, out Direction direction)
        {
            direction = Direction.North;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            switch (text.ToLowerInvariant())
            {
                case "n":
                case "north": direction = Direction.North; return true;
                case "ne":
                case "northeast": direction = Direction.Right; return true;
                case "e":
                case "east": direction = Direction.East; return true;
                case "se":
                case "southeast": direction = Direction.Down; return true;
                case "s":
                case "south": direction = Direction.South; return true;
                case "sw":
                case "southwest": direction = Direction.Left; return true;
                case "w":
                case "west": direction = Direction.West; return true;
                case "nw":
                case "northwest": direction = Direction.Up; return true;
                default: return false;
            }
        }

        public static string Name(Direction direction)
        {
            switch (direction & Direction.Mask)
            {
                case Direction.North: return "north";
                case Direction.Right: return "northeast";
                case Direction.East: return "east";
                case Direction.Down: return "southeast";
                case Direction.South: return "south";
                case Direction.Left: return "southwest";
                case Direction.West: return "west";
                case Direction.Up: return "northwest";
                default: return "?";
            }
        }

        /// <summary>Tile delta for one step in <paramref name="direction"/>.</summary>
        public static (int dx, int dy) Delta(Direction direction)
        {
            switch (direction & Direction.Mask)
            {
                case Direction.North: return (0, -1);
                case Direction.Right: return (1, -1);
                case Direction.East: return (1, 0);
                case Direction.Down: return (1, 1);
                case Direction.South: return (0, 1);
                case Direction.Left: return (-1, 1);
                case Direction.West: return (-1, 0);
                case Direction.Up: return (-1, -1);
                default: return (0, 0);
            }
        }

        /// <summary>All eight directions in clockwise order, starting at north.</summary>
        public static readonly Direction[] Clockwise =
        {
            Direction.North, Direction.Right, Direction.East, Direction.Down,
            Direction.South, Direction.Left, Direction.West, Direction.Up
        };
    }
}
