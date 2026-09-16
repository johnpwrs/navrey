// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Keeps the map chunks the CLI needs loaded.
    ///
    /// ClassicUO only loads terrain chunks as a side effect of building the render list
    /// (GameScene.FillGameObjectList, inside Draw). Everything that reasons about the map reads
    /// with load: false and simply sees nothing where a chunk is absent - Map.GetTile(x, y, false)
    /// in Pathfinder.CreateItemList returns null, which A* treats as impassable. Two consequences:
    ///
    ///   - Headless, with drawing suppressed, nothing loads at all, so the client refuses to walk
    ///     a single tile. Hence the base residency around the player.
    ///
    ///   - Windowed or not, only roughly what is on screen is ever resident, so pathfinding to
    ///     anywhere further than the viewport reports "no path" for a perfectly walkable route.
    ///     That is why this is not a headless-only concern: `goto` needs a corridor of chunks
    ///     between here and the destination, which is what <see cref="SetFocus"/> reserves.
    ///
    /// Loading a chunk is render-free (Chunk.Load only reads map0/statics0; it is Mesh.Build that
    /// needs a GraphicsDevice), so touching chunks here changes nothing about how the client draws.
    /// GameScene.Update still calls Map.ClearUnusedBlocks(), and GetChunk2 refreshes LastAccessTime,
    /// so anything we stop touching is evicted normally.
    /// </summary>
    internal static class MapResidency
    {
        /// <summary>Base radius around the player, in tiles, kept resident in headless mode.</summary>
        private const int BASE_MARGIN_TILES = 8;

        /// <summary>
        /// Largest focus region we will hold, per axis. Loading a chunk is a direct read of
        /// map0/statics0 - the same data the old standalone client held for the whole facet - so
        /// this is a memory ceiling, not a visibility one: 700 tiles is ~7,700 chunks. Beyond it,
        /// `goto` chains hops rather than reserving an ever-larger rectangle, which also keeps each
        /// A* search inside its 10,000-node budget.
        /// </summary>
        public const int MAX_FOCUS_SPAN = 700;

        /// <summary>Padding for a walk long enough to need the full detour allowance.</summary>
        private const int MAX_PADDING = 60;

        /// <summary>
        /// Padding floor. Twelve still covers walking round a house that sits between you and a
        /// target ten tiles away.
        /// </summary>
        private const int MIN_PADDING = 12;

        /// <summary>
        /// How often the focus rectangle is re-touched to keep it resident. Chunks are evicted
        /// after CLEAR_TEXTURES_DELAY (3000ms) without a touch, so once a second holds them with
        /// room to spare - and costs one pass over the rectangle per second instead of sixty.
        /// </summary>
        private const long FOCUS_TOUCH_MS = 1000;

        private static readonly object _lock = new();

        private static bool _hasFocus;
        private static int _focusMinX, _focusMinY, _focusMaxX, _focusMaxY;
        private static long _lastTouchMs;

        /// <summary>
        /// Marks the rectangle spanning the player and (<paramref name="tx"/>, <paramref name="ty"/>)
        /// as worth keeping: chunks inside it that are resident stay resident.
        ///
        /// Nothing is loaded here. It used to be - the whole rectangle, up front, so a plan could
        /// see every tile - and that was measured at 60-800ms per travel hop on the game thread,
        /// before the first step of the hop was taken. The planner now loads chunks itself as its
        /// search reaches them (<see cref="GridPathfinder"/>), which is bounded by the plan budget
        /// and only ever touches ground a route actually crosses. What remains here is retention:
        /// without a periodic touch the engine evicts a chunk three seconds after the last read,
        /// and a route planned across it would go blind mid-walk.
        ///
        /// The padding scales with the walk so a long route has room to detour; a rectangle already
        /// inside the current focus is a no-op, since the walker calls this on every replan.
        /// Returns false when the target is too far to reserve in one go.
        /// </summary>
        public static bool SetFocus(World world, int tx, int ty, int? padding = null)
        {
            if (world?.Player == null)
            {
                return false;
            }

            int px = world.Player.X;
            int py = world.Player.Y;
            int span = Math.Max(Math.Abs(tx - px), Math.Abs(ty - py));
            int pad = padding ?? Math.Clamp(span / 2, MIN_PADDING, MAX_PADDING);

            int minX = Math.Min(px, tx) - pad;
            int minY = Math.Min(py, ty) - pad;
            int maxX = Math.Max(px, tx) + pad;
            int maxY = Math.Max(py, ty) + pad;

            if (maxX - minX > MAX_FOCUS_SPAN || maxY - minY > MAX_FOCUS_SPAN)
            {
                return false;
            }

            lock (_lock)
            {
                if (_hasFocus && minX >= _focusMinX && minY >= _focusMinY &&
                    maxX <= _focusMaxX && maxY <= _focusMaxY)
                {
                    return true;
                }

                _hasFocus = true;
                _focusMinX = minX;
                _focusMinY = minY;
                _focusMaxX = maxX;
                _focusMaxY = maxY;
                _lastTouchMs = Environment.TickCount64;
            }

            return true;
        }

        public static void ClearFocus()
        {
            lock (_lock)
            {
                _hasFocus = false;
            }
        }

        /// <summary>Called once per frame on the game thread.</summary>
        public static void Update(World world)
        {
            if (world?.Map == null || !world.InGame || world.Player == null)
            {
                return;
            }

            // Windowed, the renderer already keeps the viewport resident; only headless needs the
            // base ring. The focus region is honoured in both cases.
            if (HeadlessWindow.IsHidden)
            {
                int range = world.ClientViewRange + BASE_MARGIN_TILES;

                Load(world,
                    world.Player.X - range, world.Player.Y - range,
                    world.Player.X + range, world.Player.Y + range);
            }

            int minX, minY, maxX, maxY;
            long now = Environment.TickCount64;

            lock (_lock)
            {
                if (!_hasFocus || now - _lastTouchMs < FOCUS_TOUCH_MS)
                {
                    return;
                }

                _lastTouchMs = now;
                minX = _focusMinX;
                minY = _focusMinY;
                maxX = _focusMaxX;
                maxY = _focusMaxY;
            }

            Touch(world, minX, minY, maxX, maxY);
        }

        /// <summary>Refreshes the access time of every resident chunk in the rectangle; loads nothing.</summary>
        private static void Touch(World world, int minX, int minY, int maxX, int maxY)
        {
            var map = world?.Map;

            if (map == null)
            {
                return;
            }

            int minChunkX = Math.Max(0, minX) >> 3;
            int minChunkY = Math.Max(0, minY) >> 3;
            int maxChunkX = Math.Max(0, maxX) >> 3;
            int maxChunkY = Math.Max(0, maxY) >> 3;

            for (int cx = minChunkX; cx <= maxChunkX; cx++)
            {
                for (int cy = minChunkY; cy <= maxChunkY; cy++)
                {
                    map.GetChunk2(cx, cy, false);
                }
            }
        }

        private static void Load(World world, int minX, int minY, int maxX, int maxY)
        {
            var map = world?.Map;

            if (map == null)
            {
                return;
            }

            int minChunkX = Math.Max(0, minX) >> 3;
            int minChunkY = Math.Max(0, minY) >> 3;
            int maxChunkX = Math.Max(0, maxX) >> 3;
            int maxChunkY = Math.Max(0, maxY) >> 3;

            for (int cx = minChunkX; cx <= maxChunkX; cx++)
            {
                for (int cy = minChunkY; cy <= maxChunkY; cy++)
                {
                    map.GetChunk2(cx, cy, true);
                }
            }
        }
    }
}
