// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using Microsoft.Xna.Framework;

namespace ClassicUO.Agent.Capabilities
{
    /// <summary>
    /// A copy of the map the planner can read from any thread.
    ///
    /// Route planning used to run on the game thread because every walkability question went to
    /// the live world - chunk tiles, the item list, the player's own Pathfinder - and only the game
    /// thread may touch those. That put a 60,000-node search on the frame loop: sliced into 50ms
    /// pieces so nothing froze outright, but 48 stretched frames in a row against an unreachable
    /// target (measured 2026-09-10, a fenced spawn pen). This class is what lets the search leave
    /// the frame: the game thread copies each chunk's tile objects into plain structs once
    /// (<see cref="Fill"/>), and the search reads the copies.
    ///
    /// What is copied is exactly what <see cref="Pathfinder.CalculateNewZ"/> looks at when it
    /// builds its object list - land, statics, items, house multis, mobiles, and the tiledata flags
    /// of each - so <see cref="Walkability"/> can be a line-for-line port of that engine logic
    /// over the copy rather than a re-derivation of it. The port is the whole correctness story:
    /// the old planner asked the engine directly precisely because a bespoke walkability check
    /// had disagreed with it (shard-scripted stairs built from stacked items), and this keeps
    /// asking the same questions of the same data, only against a snapshot.
    ///
    /// Two layers, because they change on different clocks. Land and statics are build-time data
    /// (map0.mul / statics0.mul): a chunk's copy is made once per session, on the game thread,
    /// and kept - it can never go stale, and a plan over ground already copied costs the game
    /// thread nothing for it. Items, mobiles and house pieces do change, so they are not cached
    /// here at all: <see cref="DynamicSnapshot"/> copies them per plan straight out of the world's
    /// own lists (World.Items, World.Mobiles, HouseManager), which needs no chunk to be loaded and
    /// costs one linear pass over what the client tracks (~2,200 items in a town, well under a
    /// millisecond). Doors are handled per plan by <see cref="GridPathfinder.DoorIndex"/> as before.
    /// </summary>
    internal static class PathGrid
    {
        /// <summary>Everything the walkability port needs to know about one object on a tile.</summary>
        public readonly struct TileObj
        {
            public const byte KIND_LAND = 0, KIND_STATIC = 1, KIND_ITEM = 2, KIND_MULTI = 3, KIND_MOBILE = 4;

            public const ushort TD_IMPASSABLE = 1, TD_SURFACE = 2, TD_BRIDGE = 4, TD_WET = 8,
                                TD_NO_DIAGONAL = 16, TD_DOOR = 32, TD_INTERNAL = 64;

            public const ushort F_IS_MULTI = 1, F_IS_LOCKED = 2, F_CUSTOM = 4, F_GENERIC_INTERNAL = 8,
                                F_IGNORE_IN_RENDER = 16, F_HOUSE_PREVIEW = 32, F_MOB_DEAD = 64,
                                F_MOB_IGNORE_CHARS = 128, F_LAND_STRETCHED = 256;

            public TileObj(byte kind, ushort graphic, sbyte z, ushort tdFlags, byte height, byte weight,
                           ushort flags, sbyte landMinZ, sbyte landAvgZ, int yRight, int yBottom, int yLeft)
            {
                Kind = kind;
                Graphic = graphic;
                Z = z;
                TdFlags = tdFlags;
                Height = height;
                Weight = weight;
                Flags = flags;
                LandMinZ = landMinZ;
                LandAvgZ = landAvgZ;
                YRight = yRight;
                YBottom = yBottom;
                YLeft = yLeft;
            }

            public readonly byte Kind;
            public readonly ushort Graphic;
            public readonly sbyte Z;
            public readonly ushort TdFlags;
            public readonly byte Height;
            public readonly byte Weight;
            public readonly ushort Flags;
            public readonly sbyte LandMinZ;
            public readonly sbyte LandAvgZ;
            public readonly int YRight, YBottom, YLeft;

            public bool Has(ushort td) => (TdFlags & td) != 0;
            public bool Is(ushort f) => (Flags & f) != 0;
        }

        private static readonly ConcurrentDictionary<long, TileObj[][]> _static = new();
        private static readonly TileObj[] _empty = Array.Empty<TileObj>();

        public static long ChunkKey(int map, int cx, int cy) => ((long)map << 40) | ((long)cx << 20) | (uint)cy;

        /// <summary>
        /// The land and statics on tile (x, y) of map <paramref name="map"/>, or null when that
        /// chunk has not been copied yet. A null means "ask the game thread to <see cref="Fill"/>
        /// it", never "blocked". Any thread.
        /// </summary>
        public static TileObj[] TryGetStatic(int map, int x, int y)
        {
            if (x < 0 || y < 0)
            {
                return _empty;
            }

            return _static.TryGetValue(ChunkKey(map, x >> 3, y >> 3), out var tiles)
                ? tiles[(x & 7) | ((y & 7) << 3)]
                : null;
        }

        /// <summary>True when the chunk holding (x, y) has been copied. Any thread.</summary>
        public static bool IsResident(int map, int x, int y) =>
            x < 0 || y < 0 || _static.ContainsKey(ChunkKey(map, x >> 3, y >> 3));

        /// <summary>Drops every copy. Any thread. Not needed on a map change - the key carries the map.</summary>
        public static void Clear() => _static.Clear();

        /// <summary>How many chunks are copied, for diagnostics.</summary>
        public static int ChunkCount => _static.Count;

        /// <summary>
        /// Copies one chunk's land and statics out of the live world. Game thread only. Loads the
        /// chunk if it is not resident - the one moment planning still costs the game thread a
        /// disk read, and it happens once per chunk per session. A no-op for a chunk already copied.
        /// </summary>
        public static void Fill(World world, int cx, int cy)
        {
            var map = world?.Map;

            if (map == null)
            {
                return;
            }

            long key = ChunkKey(map.Index, cx, cy);

            if (_static.ContainsKey(key))
            {
                return;
            }

            var chunk = map.GetChunk2(cx, cy, true);
            var tiles = new TileObj[64][];
            var scratch = new List<TileObj>(16);

            for (int ty = 0; ty < 8; ty++)
            {
                for (int tx = 0; tx < 8; tx++)
                {
                    scratch.Clear();

                    if (chunk != null && !chunk.IsDestroyed)
                    {
                        for (var obj = chunk.GetHeadObject(tx, ty); obj != null; obj = obj.TNext)
                        {
                            if (obj is Land or Static && TryCopy(obj, out var copy))
                            {
                                scratch.Add(copy);
                            }
                        }
                    }

                    tiles[tx | (ty << 3)] = scratch.Count == 0 ? _empty : scratch.ToArray();
                }
            }

            _static[key] = tiles;
        }

        /// <summary>Fills every chunk a 3x3 tile neighbourhood of (x, y) touches. Game thread only.</summary>
        public static void FillAround(World world, int x, int y)
        {
            int minCx = Math.Max(0, (x - 1) >> 3), maxCx = (x + 1) >> 3;
            int minCy = Math.Max(0, (y - 1) >> 3), maxCy = (y + 1) >> 3;

            for (int cx = minCx; cx <= maxCx; cx++)
            {
                for (int cy = minCy; cy <= maxCy; cy++)
                {
                    Fill(world, cx, cy);
                }
            }
        }

        /// <summary>
        /// Everything on the map that moves, copied once per plan on the game thread: items
        /// (including multi items), mobiles (the player included - the engine's tile walk includes
        /// it too), and every house component. Keyed by tile. Any thread may read it afterwards.
        /// </summary>
        public sealed class DynamicSnapshot
        {
            private readonly Dictionary<int, List<TileObj>> _byTile = new();
            private static readonly TileObj[] _none = Array.Empty<TileObj>();

            public static int TileKey(int x, int y) => (x << 16) | (y & 0xFFFF);

            /// <summary>Game thread only.</summary>
            public static DynamicSnapshot Capture(World world)
            {
                var snap = new DynamicSnapshot();

                foreach (var item in world.Items.Values)
                {
                    if (!item.IsDestroyed && item.OnGround && TryCopy(item, out var copy))
                    {
                        snap.Add(item.X, item.Y, copy);
                    }
                }

                foreach (var mobile in world.Mobiles.Values)
                {
                    if (!mobile.IsDestroyed && TryCopy(mobile, out var copy))
                    {
                        snap.Add(mobile.X, mobile.Y, copy);
                    }
                }

                if (world.HouseManager != null)
                {
                    foreach (var house in world.HouseManager.Houses)
                    {
                        foreach (var multi in house.Components)
                        {
                            if (!multi.IsDestroyed && TryCopy(multi, out var copy))
                            {
                                snap.Add(multi.X, multi.Y, copy);
                            }
                        }
                    }
                }

                return snap;
            }

            private void Add(int x, int y, in TileObj obj)
            {
                int key = TileKey(x, y);

                if (!_byTile.TryGetValue(key, out var list))
                {
                    list = new List<TileObj>(2);
                    _byTile[key] = list;
                }

                list.Add(obj);
            }

            public ReadOnlySpan<TileObj> At(int x, int y) =>
                _byTile.TryGetValue(TileKey(x, y), out var list)
                    ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)
                    : _none;
        }

        private static bool TryCopy(GameObject obj, out TileObj copy)
        {
            copy = default;

            switch (obj)
            {
                case Land land:
                {
                    ref var td = ref land.TileData;
                    ushort f = 0;

                    if (td.IsImpassable) f |= TileObj.TD_IMPASSABLE;
                    if (td.IsWet) f |= TileObj.TD_WET;
                    if (td.IsNoDiagonal) f |= TileObj.TD_NO_DIAGONAL;

                    copy = new TileObj(TileObj.KIND_LAND, land.Graphic, land.Z, f, 0, 0,
                        land.IsStretched ? TileObj.F_LAND_STRETCHED : (ushort)0,
                        land.MinZ, land.AverageZ,
                        land.YOffsets.Right, land.YOffsets.Bottom, land.YOffsets.Left);

                    return true;
                }

                case GameEffect:
                    return false;

                case Mobile mobile:
                {
                    ushort f = 0;

                    if (mobile.IsDead) f |= TileObj.F_MOB_DEAD;
                    if (mobile.IgnoreCharacters) f |= TileObj.F_MOB_IGNORE_CHARS;

                    copy = new TileObj(TileObj.KIND_MOBILE, mobile.Graphic, mobile.Z, 0, 0, 0, f, 0, 0, 0, 0, 0);

                    return true;
                }

                case Static st:
                    copy = new TileObj(TileObj.KIND_STATIC, st.Graphic, st.Z, TdFlags(ref st.ItemData),
                        st.ItemData.Height, (byte)Math.Min(255, (int)st.ItemData.Weight), 0, 0, 0, 0, 0, 0);

                    return true;

                case Multi multi:
                {
                    ushort f = 0;

                    if (multi.IsCustom) f |= TileObj.F_CUSTOM;
                    if ((multi.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL) != 0) f |= TileObj.F_GENERIC_INTERNAL;
                    if ((multi.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_IGNORE_IN_RENDER) != 0) f |= TileObj.F_IGNORE_IN_RENDER;
                    if (multi.IsHousePreview) f |= TileObj.F_HOUSE_PREVIEW;

                    copy = new TileObj(TileObj.KIND_MULTI, multi.Graphic, multi.Z, TdFlags(ref multi.ItemData),
                        multi.ItemData.Height, (byte)Math.Min(255, (int)multi.ItemData.Weight), f, 0, 0, 0, 0, 0);

                    return true;
                }

                case Item item:
                {
                    if (item.IsDestroyed)
                    {
                        return false;
                    }

                    // The engine reads a multi item's flags through its MultiGraphic, not its own.
                    ushort graphic = item.IsMulti ? item.MultiGraphic : item.Graphic;
                    ref var td = ref Client.Game.UO.FileManager.TileData.StaticData[graphic];
                    ushort f = 0;

                    if (item.IsMulti) f |= TileObj.F_IS_MULTI;
                    if (item.IsLocked) f |= TileObj.F_IS_LOCKED;

                    copy = new TileObj(TileObj.KIND_ITEM, graphic, item.Z, TdFlags(ref td),
                        td.Height, (byte)Math.Min(255, (int)td.Weight), f, 0, 0, 0, 0, 0);

                    return true;
                }

                default:
                    return false;
            }
        }

        private static ushort TdFlags(ref StaticTiles td)
        {
            ushort f = 0;

            if (td.IsImpassable) f |= TileObj.TD_IMPASSABLE;
            if (td.IsSurface) f |= TileObj.TD_SURFACE;
            if (td.IsBridge) f |= TileObj.TD_BRIDGE;
            if (td.IsWet) f |= TileObj.TD_WET;
            if (td.IsNoDiagonal) f |= TileObj.TD_NO_DIAGONAL;
            if (td.IsDoor) f |= TileObj.TD_DOOR;
            if (td.IsInternal) f |= TileObj.TD_INTERNAL;

            return f;
        }
    }

    /// <summary>
    /// The player-side facts <see cref="Pathfinder.CalculateNewZ"/> consults, captured once per
    /// plan on the game thread so the search can run without it.
    /// </summary>
    internal readonly struct PlanContext
    {
        public const int PSS_NORMAL = 0, PSS_DEAD_OR_GM = 1, PSS_ON_SEA_HORSE = 2, PSS_FLYING = 3;

        public readonly int StepState;
        public readonly bool IgnoreGameCharacters;
        public readonly bool IsGM;
        public readonly bool SmoothDoors;
        public readonly bool InCustomHouse;
        public readonly Rectangle CustomHouseRect;
        public readonly int PlayerX, PlayerY;
        public readonly sbyte PlayerZ;
        public readonly int Map;

        private PlanContext(int stepState, bool ignoreChars, bool isGM, bool smoothDoors, bool inHouse,
                            Rectangle rect, int px, int py, sbyte pz, int map)
        {
            Map = map;
            StepState = stepState;
            IgnoreGameCharacters = ignoreChars;
            IsGM = isGM;
            SmoothDoors = smoothDoors;
            InCustomHouse = inHouse;
            CustomHouseRect = rect;
            PlayerX = px;
            PlayerY = py;
            PlayerZ = pz;
        }

        /// <summary>Game thread only. Mirrors the top of CalculateNewZ and CreateItemList.</summary>
        public static PlanContext Capture(World world)
        {
            var p = world.Player;
            int stepState = PSS_NORMAL;

            if (p.IsDead || p.Graphic == 0x03DB)
            {
                stepState = PSS_DEAD_OR_GM;
            }
            else if (p.IsGargoyle && p.IsFlying)
            {
                stepState = PSS_FLYING;
            }
            else
            {
                var mount = p.FindItemByLayer(Layer.Mount);

                if (mount != null && mount.Graphic == 0x3EB3)
                {
                    stepState = PSS_ON_SEA_HORSE;
                }
            }

            var profile = ProfileManager.CurrentProfile;

            bool ignoreChars = (profile?.IgnoreStaminaCheck ?? false) || stepState == PSS_DEAD_OR_GM ||
                               p.IgnoreCharacters || !(p.Stamina < p.StaminaMax && world.Map.Index == 0);

            bool inHouse = world.CustomHouseManager != null;
            var rect = inHouse
                ? new Rectangle(world.CustomHouseManager.StartPos.X, world.CustomHouseManager.StartPos.Y,
                                world.CustomHouseManager.EndPos.X, world.CustomHouseManager.EndPos.Y)
                : Rectangle.Empty;

            return new PlanContext(stepState, ignoreChars, p.Graphic == 0x03DB,
                profile?.SmoothDoors ?? false, inHouse, rect, p.X, p.Y, p.Z, world.Map.Index);
        }
    }

    /// <summary>
    /// <see cref="Pathfinder.CalculateNewZ"/>, <c>CalculateMinMaxZ</c> and <c>CreateItemList</c>
    /// ported to run over <see cref="PathGrid"/> copies instead of live objects. Kept structurally
    /// identical to the engine so a diff against Pathfinder.cs is the review: same flags, same
    /// ordering, same magic graphics. Any thread.
    /// </summary>
    internal static class Walkability
    {
        public enum Probe
        {
            Ok,
            Blocked,
            /// <summary>A chunk the answer depends on is not copied; fill it and ask again.</summary>
            Missing
        }

        private const uint POF_IMPASSABLE_OR_SURFACE = 1, POF_SURFACE = 2, POF_BRIDGE = 4, POF_NO_DIAGONAL = 8;

        private static readonly int[] _offsetX = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] _offsetY = { -1, -1, 0, 1, 1, 1, 0, -1 };

        private struct PathObj : IComparable<PathObj>
        {
            public uint Flags;
            public int Z, AverageZ, Height;
            public bool StretchedLand;
            public int YRight, YBottom, YLeft, LandZ;

            public int CompareTo(PathObj other)
            {
                int c = Z - other.Z;

                return c != 0 ? c : Height - other.Height;
            }
        }

        [ThreadStatic] private static List<PathObj> _listA;
        [ThreadStatic] private static List<PathObj> _listB;

        /// <summary>
        /// Port of Pathfinder.CreateItemList over the static layer and the plan's dynamic
        /// snapshot. Returns false when the list is empty.
        ///
        /// Order: statics first, then dynamics. The engine walks the tile's own list, where the
        /// two are interleaved by the chunk's sort; the only place order can matter afterwards is
        /// the (Z, Height) sort in CalculateNewZ, and then only for exact ties between a static
        /// and a dynamic object at the same height - accepted.
        /// </summary>
        private static bool CreateItemList(List<PathObj> list, ReadOnlySpan<PathGrid.TileObj> statics,
                                           ReadOnlySpan<PathGrid.TileObj> dynamics, in PlanContext c)
        {
            list.Clear();
            Append(list, statics, c);
            Append(list, dynamics, c);

            return list.Count != 0;
        }

        private static void Append(List<PathObj> list, ReadOnlySpan<PathGrid.TileObj> objs, in PlanContext c)
        {
            foreach (ref readonly var obj in objs)
            {
                if (c.InCustomHouse && obj.Z < c.PlayerZ)
                {
                    continue;
                }

                ushort graphicHelper = obj.Graphic;

                switch (obj.Kind)
                {
                    case PathGrid.TileObj.KIND_LAND:
                        if (graphicHelper < 0x01AE && graphicHelper != 2 || graphicHelper > 0x01B5 && graphicHelper != 0x01DB)
                        {
                            uint flags = POF_IMPASSABLE_OR_SURFACE;

                            if (c.StepState == PlanContext.PSS_ON_SEA_HORSE)
                            {
                                if (obj.Has(PathGrid.TileObj.TD_WET))
                                {
                                    flags = POF_IMPASSABLE_OR_SURFACE | POF_SURFACE | POF_BRIDGE;
                                }
                            }
                            else
                            {
                                if (!obj.Has(PathGrid.TileObj.TD_IMPASSABLE))
                                {
                                    flags = POF_IMPASSABLE_OR_SURFACE | POF_SURFACE | POF_BRIDGE;
                                }

                                if (c.StepState == PlanContext.PSS_FLYING && obj.Has(PathGrid.TileObj.TD_NO_DIAGONAL))
                                {
                                    flags |= POF_NO_DIAGONAL;
                                }
                            }

                            int landMinZ = obj.LandMinZ;
                            int landAverageZ = obj.LandAvgZ;

                            list.Add(new PathObj
                            {
                                Flags = flags, Z = landMinZ, AverageZ = landAverageZ, Height = landAverageZ - landMinZ,
                                StretchedLand = obj.Is(PathGrid.TileObj.F_LAND_STRETCHED),
                                YRight = obj.YRight, YBottom = obj.YBottom, YLeft = obj.YLeft, LandZ = obj.Z
                            });
                        }

                        break;

                    default:
                    {
                        bool canBeAdd = true;
                        bool dropFlags = false;

                        switch (obj.Kind)
                        {
                            case PathGrid.TileObj.KIND_MOBILE:
                                if (!c.IgnoreGameCharacters && !obj.Is(PathGrid.TileObj.F_MOB_DEAD) && !obj.Is(PathGrid.TileObj.F_MOB_IGNORE_CHARS))
                                {
                                    list.Add(new PathObj
                                    {
                                        Flags = POF_IMPASSABLE_OR_SURFACE, Z = obj.Z,
                                        AverageZ = obj.Z + Constants.DEFAULT_CHARACTER_HEIGHT,
                                        Height = Constants.DEFAULT_CHARACTER_HEIGHT
                                    });
                                }

                                canBeAdd = false;

                                break;

                            case PathGrid.TileObj.KIND_ITEM when obj.Is(PathGrid.TileObj.F_IS_MULTI) || obj.Has(PathGrid.TileObj.TD_INTERNAL):
                                break;

                            case PathGrid.TileObj.KIND_ITEM:
                                if (c.StepState == PlanContext.PSS_DEAD_OR_GM && (obj.Has(PathGrid.TileObj.TD_DOOR) || obj.Weight <= 0x5A || c.IsGM && !obj.Is(PathGrid.TileObj.F_IS_LOCKED)))
                                {
                                    dropFlags = true;
                                }
                                else if (c.SmoothDoors && obj.Has(PathGrid.TileObj.TD_DOOR))
                                {
                                    dropFlags = true;
                                }
                                else
                                {
                                    dropFlags = graphicHelper >= 0x3946 && graphicHelper <= 0x3964 || graphicHelper == 0x0082;
                                }

                                break;

                            case PathGrid.TileObj.KIND_MULTI:
                                if (c.InCustomHouse && obj.Is(PathGrid.TileObj.F_CUSTOM) && !obj.Is(PathGrid.TileObj.F_GENERIC_INTERNAL) || obj.Is(PathGrid.TileObj.F_HOUSE_PREVIEW))
                                {
                                    canBeAdd = false;
                                }

                                if (obj.Is(PathGrid.TileObj.F_IGNORE_IN_RENDER))
                                {
                                    dropFlags = true;
                                }

                                break;
                        }

                        if (canBeAdd)
                        {
                            uint flags = 0;

                            if (c.StepState == PlanContext.PSS_ON_SEA_HORSE)
                            {
                                if (obj.Has(PathGrid.TileObj.TD_WET))
                                {
                                    flags = POF_SURFACE | POF_BRIDGE;
                                }
                            }
                            else
                            {
                                if (obj.Has(PathGrid.TileObj.TD_IMPASSABLE) || obj.Has(PathGrid.TileObj.TD_SURFACE))
                                {
                                    flags = POF_IMPASSABLE_OR_SURFACE;
                                }

                                if (!obj.Has(PathGrid.TileObj.TD_IMPASSABLE))
                                {
                                    if (obj.Has(PathGrid.TileObj.TD_SURFACE))
                                    {
                                        flags |= POF_SURFACE;
                                    }

                                    if (obj.Has(PathGrid.TileObj.TD_BRIDGE))
                                    {
                                        flags |= POF_BRIDGE;
                                    }
                                }

                                if (c.StepState == PlanContext.PSS_DEAD_OR_GM)
                                {
                                    if (graphicHelper <= 0x0846)
                                    {
                                        if (!(graphicHelper != 0x0846 && graphicHelper != 0x0692 && (graphicHelper <= 0x06F4 || graphicHelper > 0x06F6)))
                                        {
                                            dropFlags = true;
                                        }
                                    }
                                    else if (graphicHelper == 0x0873)
                                    {
                                        dropFlags = true;
                                    }
                                }

                                if (dropFlags)
                                {
                                    flags &= 0xFFFFFFFE;
                                }

                                if (c.StepState == PlanContext.PSS_FLYING && obj.Has(PathGrid.TileObj.TD_NO_DIAGONAL))
                                {
                                    flags |= POF_NO_DIAGONAL;
                                }
                            }

                            if (flags != 0)
                            {
                                int objZ = obj.Z;
                                int staticHeight = obj.Height;
                                int staticAverageZ = staticHeight;

                                if (obj.Has(PathGrid.TileObj.TD_BRIDGE))
                                {
                                    staticAverageZ /= 2;
                                }

                                list.Add(new PathObj
                                {
                                    Flags = flags, Z = objZ, AverageZ = staticAverageZ + objZ, Height = staticHeight
                                });
                            }
                        }

                        break;
                    }
                }
            }
        }

        private static int DirectionZ(in PathObj land, int d) => d switch
        {
            1 => land.YRight >> 2,
            2 => land.YBottom >> 2,
            3 => land.YLeft >> 2,
            _ => land.LandZ
        };

        /// <summary>Port of Land.CalculateCurrentAverageZ over the copied offsets.</summary>
        private static int CurrentAverageZ(in PathObj land, int direction)
        {
            int result = DirectionZ(land, ((byte)(direction >> 1) + 1) & 3);

            if ((direction & 1) != 0)
            {
                return result;
            }

            return (result + DirectionZ(land, direction >> 1)) >> 1;
        }

        /// <summary>Port of Pathfinder.CalculateMinMaxZ. Returns Missing when the tile behind is not copied.</summary>
        private static Probe CalculateMinMaxZ(ref int minZ, ref int maxZ, int newX, int newY, int currentZ,
                                              int newDirection, in PlanContext c, PathGrid.DynamicSnapshot dyn,
                                              List<PathObj> list)
        {
            minZ = -128;
            maxZ = currentZ;
            newDirection &= 7;
            int direction = newDirection ^ 4;
            newX += _offsetX[direction];
            newY += _offsetY[direction];

            var statics = PathGrid.TryGetStatic(c.Map, newX, newY);

            if (statics == null)
            {
                return Probe.Missing;
            }

            if (!CreateItemList(list, statics, dyn.At(newX, newY), c) || list.Count == 0)
            {
                return Probe.Ok;
            }

            foreach (var obj in list)
            {
                int averageZ = obj.AverageZ;

                if (averageZ <= currentZ && obj.StretchedLand)
                {
                    int avgZ = CurrentAverageZ(obj, newDirection);

                    if (minZ < avgZ)
                    {
                        minZ = avgZ;
                    }

                    if (maxZ < avgZ)
                    {
                        maxZ = avgZ;
                    }
                }
                else
                {
                    if ((obj.Flags & POF_IMPASSABLE_OR_SURFACE) != 0 && averageZ <= currentZ && minZ < averageZ)
                    {
                        minZ = averageZ;
                    }

                    if ((obj.Flags & POF_BRIDGE) != 0 && currentZ == averageZ)
                    {
                        int z = obj.Z;
                        int height = z + obj.Height;

                        if (maxZ < height)
                        {
                            maxZ = height;
                        }

                        if (minZ > z)
                        {
                            minZ = z;
                        }
                    }
                }
            }

            maxZ += 2;

            return Probe.Ok;
        }

        /// <summary>
        /// Port of Pathfinder.CalculateNewZ: can a character at height <paramref name="z"/> step
        /// onto (x, y) arriving from <paramref name="direction"/>, and at what height. Any thread.
        /// </summary>
        public static Probe CalculateNewZ(int x, int y, ref sbyte z, int direction, in PlanContext c,
                                          PathGrid.DynamicSnapshot dyn)
        {
            var listA = _listA ??= new List<PathObj>(16);
            var listB = _listB ??= new List<PathObj>(16);

            int minZ = -128;
            int maxZ = z;

            var probe = CalculateMinMaxZ(ref minZ, ref maxZ, x, y, z, direction, c, dyn, listA);

            if (probe == Probe.Missing)
            {
                return probe;
            }

            if (c.InCustomHouse && !c.CustomHouseRect.Contains(x, y))
            {
                return Probe.Blocked;
            }

            var statics = PathGrid.TryGetStatic(c.Map, x, y);

            if (statics == null)
            {
                return Probe.Missing;
            }

            var list = listB;

            if (!CreateItemList(list, statics, dyn.At(x, y), c) || list.Count == 0)
            {
                return Probe.Blocked;
            }

            list.Sort();
            list.Add(new PathObj { Flags = POF_IMPASSABLE_OR_SURFACE, Z = 128, AverageZ = 128, Height = 128 });

            int resultZ = -128;

            if (z < minZ)
            {
                z = (sbyte)minZ;
            }

            int currentTempObjZ = 1000000;
            int currentZ = -128;

            for (int i = 0; i < list.Count; i++)
            {
                var obj = list[i];

                if ((obj.Flags & POF_NO_DIAGONAL) != 0 && c.StepState == PlanContext.PSS_FLYING)
                {
                    int objAverageZ = obj.AverageZ;
                    int delta = Math.Abs(objAverageZ - z);

                    if (delta <= 25)
                    {
                        resultZ = objAverageZ != -128 ? objAverageZ : currentZ;

                        break;
                    }
                }

                if ((obj.Flags & POF_IMPASSABLE_OR_SURFACE) != 0)
                {
                    int objZ = obj.Z;

                    if (objZ - minZ >= Constants.DEFAULT_BLOCK_HEIGHT)
                    {
                        for (int j = i - 1; j >= 0; j--)
                        {
                            var tempObj = list[j];

                            if ((tempObj.Flags & (POF_SURFACE | POF_BRIDGE)) != 0)
                            {
                                int tempAverageZ = tempObj.AverageZ;

                                if (tempAverageZ >= currentZ && objZ - tempAverageZ >= Constants.DEFAULT_BLOCK_HEIGHT &&
                                    (tempAverageZ <= maxZ && (tempObj.Flags & POF_SURFACE) != 0 ||
                                     (tempObj.Flags & POF_BRIDGE) != 0 && tempObj.Z <= maxZ))
                                {
                                    int delta = Math.Abs(z - tempAverageZ);

                                    if (delta < currentTempObjZ)
                                    {
                                        currentTempObjZ = delta;
                                        resultZ = tempAverageZ;
                                    }
                                }
                            }
                        }
                    }

                    int averageZ = obj.AverageZ;

                    if (minZ < averageZ)
                    {
                        minZ = averageZ;
                    }

                    if (currentZ < averageZ)
                    {
                        currentZ = averageZ;
                    }
                }
            }

            z = (sbyte)resultZ;

            return resultZ != -128 ? Probe.Ok : Probe.Blocked;
        }
    }
}
