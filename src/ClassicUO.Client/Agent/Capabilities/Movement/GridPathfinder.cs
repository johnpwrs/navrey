// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Agent.Capabilities
{
    /// <summary>
    /// Route planning over the map and static data, with one deliberate difference from the
    /// client's own pathfinder: <b>doors are passable</b>.
    ///
    /// ClassicUO's Pathfinder treats a closed door like any other blocking object, which is correct
    /// for click-to-walk (you can see the door, so you open it) but useless for "walk to the bank"
    /// issued from indoors - the search finds no route out of the building at all. The old
    /// standalone client solved this by planning through doors and opening them during execution,
    /// and that is what this does.
    ///
    /// Planning only. Every step this produces is still executed through PlayerMobile.Walk, so the
    /// walk sequence, the pending-step ring and the server's own validation are untouched - the
    /// thing that actually has to stay in sync with the game window is not involved here.
    ///
    /// Reads the map through <see cref="PathGrid"/>, a per-chunk copy the game thread makes of the
    /// loaded chunks (themselves a direct read of map0/statics0, see <see cref="MapResidency"/>),
    /// so the search itself runs off the game thread - nothing here depends on what is on screen,
    /// and nothing here holds up a frame.
    /// </summary>
    internal static class GridPathfinder
    {
        /// <summary>Largest step up or down we will plan across, in Z units.</summary>
        private const int MAX_STEP_Z = 16;

        /// <summary>
        /// How far a node's height may differ from a requested goal Z and still count as arrived.
        /// A floor is one flat surface with a few units of variation across it; the next floor up
        /// is 20 units away. Eight separates those without demanding an exact number.
        /// </summary>
        public const int GOAL_Z_TOLERANCE = 8;

        /// <summary>Height a character needs to fit through.</summary>
        private const int PLAYER_HEIGHT = 16;

        private const int MAX_NODES = 60000;

        /// <summary>
        /// How long one <see cref="Planner.Step"/> runs before yielding, so a caller can check
        /// cancellation and progress. The search runs on the caller's thread against
        /// <see cref="PathGrid"/> copies, so this is no longer game-thread time: a slice only ever
        /// costs the game thread the chunk copies the search asks for between slices. It used to
        /// bound how long a slice could freeze the frame loop, when planning read the live world.
        /// </summary>
        public const int MAX_PLAN_MS = 50;

        /// <summary>
        /// Expansions between clock and cancellation checks; both are cheap but not free. Sixty-
        /// four keeps the overshoot past a slice's budget to a few milliseconds even when every
        /// node is loading a chunk (measured ~70us per node then, 256 overshot by ~25ms).
        /// </summary>
        private const int CHECK_EVERY = 64;

        /// <summary>
        /// Chunks copied around each one the search asks for (a ring of this radius, in chunks).
        /// Two means a 5x5 block of chunks, 40x40 tiles, per round trip.
        /// </summary>
        private const int PREFETCH_RING = 2;

        /// <summary>
        /// Longest one <see cref="Planner.FillPending"/> may hold the game thread. This is the
        /// whole game-thread cost of planning now, so it is the number that bounds how much a
        /// plan can stretch a frame. Half a 60fps frame.
        /// </summary>
        private const int FILL_BUDGET_MS = 8;

        /// <summary>How a plan ended - the `ended=` token in the `[NAV]` line.</summary>
        public enum Outcome
        {
            /// <summary>Reached the goal (or the accept radius on the goal floor).</summary>
            Goal,

            /// <summary>Every reachable tile was explored and none was the goal - a real dead end.</summary>
            Exhausted,

            /// <summary>Hit MAX_NODES.</summary>
            Nodes,

            /// <summary>The walk was superseded or stopped mid-plan.</summary>
            Cancelled
        }

        public readonly struct PlanResult
        {
            public PlanResult(List<Step> path, int reachedX, int reachedY, sbyte reachedZ,
                              int nodesExplored, long elapsedMs, int fills, Outcome ended)
            {
                Path = path;
                ReachedX = reachedX;
                ReachedY = reachedY;
                ReachedZ = reachedZ;
                NodesExplored = nodesExplored;
                ElapsedMs = elapsedMs;
                Fills = fills;
                Ended = ended;
            }

            /// <summary>Null when not even one step away from the start could be planned.</summary>
            public List<Step> Path { get; }
            public int ReachedX { get; }
            public int ReachedY { get; }
            public sbyte ReachedZ { get; }
            public int NodesExplored { get; }
            /// <summary>Search time, on whichever thread ran it - no longer game-thread time.</summary>
            public long ElapsedMs { get; }

            /// <summary>
            /// Times the search had to stop and ask the game thread to copy chunks into
            /// <see cref="PathGrid"/>. Zero means every tile it looked at was already copied.
            /// </summary>
            public int Fills { get; }
            public Outcome Ended { get; }
        }

        /// <summary>
        /// Every door the client knows about, keyed by tile, built once per plan.
        ///
        /// Doors are dynamic Items, not statics, so the only way to find one is to look through
        /// World.Items - and TryStep asks "is there a door here?" for every neighbour of every
        /// node. Asking the world directly each time made a plan cost nodes × 8 × items: in a town
        /// (~2,200 tracked items) an unreachable target came to ~10⁹ comparisons on the game
        /// thread. One pass over the items up front makes each ask a dictionary probe.
        /// </summary>
        public sealed class DoorIndex
        {
            private readonly Dictionary<int, List<sbyte>> _byTile = new();

            public static DoorIndex Build(World world)
            {
                var index = new DoorIndex();

                foreach (var item in world.Items.Values)
                {
                    if (item.IsDestroyed || !item.ItemData.IsDoor)
                    {
                        continue;
                    }

                    int key = TileKey(item.X, item.Y);

                    if (!index._byTile.TryGetValue(key, out var heights))
                    {
                        heights = new List<sbyte>(1);
                        index._byTile[key] = heights;
                    }

                    heights.Add(item.Z);
                }

                return index;
            }

            /// <summary>Same ±15 Z tolerance the item scan used: a door on this floor, not the one above.</summary>
            public bool Contains(int x, int y, int z)
            {
                if (!_byTile.TryGetValue(TileKey(x, y), out var heights))
                {
                    return false;
                }

                foreach (sbyte dz in heights)
                {
                    if (dz - 15 <= z && dz + 15 >= z)
                    {
                        return true;
                    }
                }

                return false;
            }

            private static int TileKey(int x, int y) => (x << 16) | (y & 0xFFFF);
        }

        public readonly struct Step
        {
            public Step(Direction direction, int x, int y, sbyte z)
            {
                Direction = direction;
                X = x;
                Y = y;
                Z = z;
            }

            public Direction Direction { get; }
            public int X { get; }
            public int Y { get; }
            public sbyte Z { get; }
        }

        /// <summary>
        /// Plans a route to (<paramref name="tx"/>, <paramref name="ty"/>).
        ///
        /// <paramref name="acceptDistance"/> lets the search finish on any tile within that many
        /// tiles of the goal, which is what makes "go to this POI" work when the coordinate itself
        /// marks a building's interior rather than a floor tile.
        ///
        /// When the goal cannot be reached at all, returns the route to the closest tile the search
        /// actually explored (<paramref name="reachedX"/>/<paramref name="reachedY"/>) rather than
        /// nothing - walking most of the way and reporting where you stopped is far more useful
        /// than refusing to move.
        /// </summary>
        public static List<Step> FindPath(
            World world,
            int tx,
            int ty,
            int acceptDistance,
            HashSet<(int x, int y)> avoid,
            out int reachedX,
            out int reachedY)
            => FindPath(world, tx, ty, null, acceptDistance, avoid, out reachedX, out reachedY, out _, out _);

        /// <summary>
        /// Same search, but also reports how many nodes the search actually expanded
        /// (<paramref name="nodesExplored"/>) - the diagnostic that tells "genuinely no route exists
        /// short of MAX_NODES tiles away" apart from "gave up early because every reachable tile from
        /// here really is a dead end or a small pocket". A search that returns "closest reachable"
        /// after exploring only a few hundred nodes, in a world with tens of thousands of walkable
        /// tiles in range, did not exhaust its budget - it ran out of anywhere left to go.
        /// </summary>
        public static List<Step> FindPath(
            World world,
            int tx,
            int ty,
            int acceptDistance,
            HashSet<(int x, int y)> avoid,
            out int reachedX,
            out int reachedY,
            out int nodesExplored)
            => FindPath(world, tx, ty, null, acceptDistance, avoid, out reachedX, out reachedY, out _, out nodesExplored);

        /// <summary>
        /// The full search. <paramref name="tz"/> is an optional goal height: with it, the goal is
        /// the tile at (tx, ty) on *that* floor - a roof at Z 20 is a different destination from
        /// the street at Z 0 beneath it, and the search will climb the stairs to get there.
        /// Without it, any height at (tx, ty) counts, which is what a walk across open ground wants.
        ///
        /// The search has always carried Z on every node (that is how it steps up stairs at all);
        /// what makes the goal height work is that the visited set is keyed by height too, so
        /// reaching the ground under a roof cannot prune the route that reaches the roof.
        /// </summary>
        public static List<Step> FindPath(
            World world,
            int tx,
            int ty,
            int? tz,
            int acceptDistance,
            HashSet<(int x, int y)> avoid,
            out int reachedX,
            out int reachedY,
            out sbyte reachedZ,
            out int nodesExplored)
        {
            var result = Plan(world, tx, ty, tz, acceptDistance, avoid, CancellationToken.None);

            reachedX = result.ReachedX;
            reachedY = result.ReachedY;
            reachedZ = result.ReachedZ;
            nodesExplored = result.NodesExplored;

            return result.Path;
        }

        /// <summary>
        /// One whole search on the calling thread, in MAX_PLAN_MS slices. Diagnostics and the
        /// FindPath wrappers use this; the walker steps a <see cref="Planner"/> itself so the
        /// frame loop runs between slices.
        /// </summary>
        public static PlanResult Plan(
            World world,
            int tx,
            int ty,
            int? tz,
            int acceptDistance,
            HashSet<(int x, int y)> avoid,
            CancellationToken cancel)
        {
            var planner = new Planner(world, tx, ty, tz, acceptDistance, avoid);

            while (true)
            {
                switch (planner.Step(MAX_PLAN_MS, cancel))
                {
                    case StepResult.Done:
                        return planner.Result;

                    case StepResult.NeedChunks:
                        planner.FillPending(world);
                        break;
                }
            }
        }

        /// <summary>How one <see cref="Planner.Step"/> call ended.</summary>
        public enum StepResult
        {
            /// <summary>Budget spent; call again.</summary>
            Paused,

            /// <summary>The search is over and <see cref="Planner.Result"/> is set.</summary>
            Done,

            /// <summary>
            /// The search reached ground <see cref="PathGrid"/> has no copy of. Have the game
            /// thread run <see cref="Planner.FillPending"/> (or <see cref="PathGrid.Fill"/> for
            /// each of <see cref="Planner.PendingChunks"/>), then call again.
            /// </summary>
            NeedChunks
        }

        /// <summary>
        /// A route search that runs on any thread.
        ///
        /// Construct it on the game thread - the constructor copies what a plan needs from the
        /// live world (<see cref="PlanContext"/>, the door index, the player's tile) - then drive
        /// <see cref="Step"/> from wherever, typically the walk's own thread. The search reads
        /// map data only through <see cref="PathGrid"/>; when it reaches a chunk that has not been
        /// copied it returns <see cref="StepResult.NeedChunks"/> and waits for the game thread to
        /// copy it. That is the only game-thread work a plan costs now: a handful of chunk copies,
        /// each a linear pass over 64 tiles, instead of the whole search in 50ms slices.
        ///
        /// History, because the shape matters: the first bound on plan cost was a single time
        /// budget that returned "no path" for routes that existed (measured 2026-09-08: `nopath`
        /// after 5,376 nodes for a target five tiles away behind a ruin wall). The second was
        /// slicing the game-thread search across frames, which stopped freezes but still
        /// stretched 48 consecutive frames against an unreachable target (2026-09-10). Taking the
        /// search off the game thread is the third, and it keeps the slice budget only as a
        /// yield point for cancellation.
        /// </summary>
        public sealed class Planner
        {
            private readonly PlanContext _ctx;
            private readonly int _tx, _ty, _acceptDistance;
            private readonly int? _tz;
            private readonly HashSet<(int x, int y)> _avoid;
            private readonly DoorIndex _doors;
            private readonly PathGrid.DynamicSnapshot _dyn;

            private readonly PriorityQueue<Node, int> _open = new();
            private readonly Dictionary<long, int> _best = new();
            private readonly List<(int cx, int cy)> _pending = new();

            private readonly Node _start;
            private Node _closest;
            private int _closestDistance;
            private int _expanded;
            private long _elapsedMs;
            private int _fills;

            /// <summary>Game thread only: captures the world state the search will run against.</summary>
            public Planner(World world, int tx, int ty, int? tz, int acceptDistance, HashSet<(int x, int y)> avoid)
            {
                _ctx = PlanContext.Capture(world);
                _tx = tx;
                _ty = ty;
                _tz = tz;
                _acceptDistance = acceptDistance;
                _avoid = avoid;
                _doors = DoorIndex.Build(world);
                _dyn = PathGrid.DynamicSnapshot.Capture(world);

                _start = new Node(_ctx.PlayerX, _ctx.PlayerY, _ctx.PlayerZ, Direction.North, 0,
                    Heuristic(_ctx.PlayerX, _ctx.PlayerY, tx, ty), null);
                _closest = _start;
                _closestDistance = GoalDistance(_start.X, _start.Y, _start.Z, tx, ty, tz);

                _open.Enqueue(_start, _start.Cost + _start.Estimate);
                _best[Key(_start.X, _start.Y, _start.Z)] = 0;

                // The start's own neighbourhood is needed on the first expansion; copying it here,
                // while already on the game thread, saves the first round trip.
                PathGrid.FillAround(world, _start.X, _start.Y);
            }

            public bool Done { get; private set; }

            /// <summary>Valid once <see cref="Done"/>.</summary>
            public PlanResult Result { get; private set; }

            /// <summary>Chunks to copy after a <see cref="StepResult.NeedChunks"/>, as (cx, cy).</summary>
            public IReadOnlyList<(int cx, int cy)> PendingChunks => _pending;

            /// <summary>
            /// Game thread only: copies every pending chunk, then as many of the chunks around
            /// them as <see cref="FILL_BUDGET_MS"/> allows.
            ///
            /// Each call is a round trip from the search thread to the game thread and back -
            /// measured at ~3.8ms each, so a 60,000-node search that asked for its 946 chunks one
            /// at a time took 3.6s of wall clock against 2.1s for the old in-frame search.
            /// Copying a whole 5x5 ring per trip brought that to 2.5s but cost single frames up to
            /// 32ms, because a chunk that is not resident is loaded from disk on the way. So the
            /// pending chunks are always copied (the search cannot continue without them) and
            /// the ring is copied only while the budget lasts; whatever is left is asked for on
            /// the next trip, which is one frame later.
            /// </summary>
            public void FillPending(World world)
            {
                var clock = Stopwatch.StartNew();

                foreach (var (cx, cy) in _pending)
                {
                    PathGrid.Fill(world, cx, cy);
                }

                foreach (var (cx, cy) in _pending)
                {
                    for (int dx = -PREFETCH_RING; dx <= PREFETCH_RING; dx++)
                    {
                        for (int dy = -PREFETCH_RING; dy <= PREFETCH_RING; dy++)
                        {
                            if (clock.ElapsedMilliseconds >= FILL_BUDGET_MS)
                            {
                                _pending.Clear();

                                return;
                            }

                            int x = cx + dx, y = cy + dy;

                            if (x < 0 || y < 0 || PathGrid.IsResident(_ctx.Map, x << 3, y << 3))
                            {
                                continue;
                            }

                            PathGrid.Fill(world, x, y);
                        }
                    }
                }

                _pending.Clear();
            }

            /// <summary>
            /// Expands nodes for up to <paramref name="budgetMs"/>. Any thread.
            /// </summary>
            public StepResult Step(int budgetMs, CancellationToken cancel)
            {
                if (Done)
                {
                    return StepResult.Done;
                }

                var clock = Stopwatch.StartNew();

                while (true)
                {
                    if (_expanded >= MAX_NODES)
                    {
                        return Finish(clock, Outcome.Nodes);
                    }

                    if (_expanded % CHECK_EVERY == 0 && _expanded > 0)
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            return Finish(clock, Outcome.Cancelled);
                        }

                        if (clock.ElapsedMilliseconds >= budgetMs)
                        {
                            _elapsedMs += clock.ElapsedMilliseconds;

                            return StepResult.Paused;
                        }
                    }

                    if (!_open.TryPeek(out var node, out _))
                    {
                        return Finish(clock, Outcome.Exhausted);
                    }

                    // Everything a node's expansion reads lies within one tile of it: the eight
                    // neighbours, and for each the tile behind it (which is this node). Make sure
                    // those chunks are copied before touching the node, so an expansion never has
                    // to be unwound half way.
                    if (!Resident(node.X, node.Y))
                    {
                        _fills++;
                        _elapsedMs += clock.ElapsedMilliseconds;

                        return StepResult.NeedChunks;
                    }

                    _open.Dequeue();
                    _expanded++;

                    if (Chebyshev(node.X, node.Y, _tx, _ty) <= _acceptDistance && OnGoalFloor(node.Z, _tz))
                    {
                        _closest = node;

                        return Finish(clock, Outcome.Goal);
                    }

                    int goalDistance = GoalDistance(node.X, node.Y, node.Z, _tx, _ty, _tz);

                    if (goalDistance < _closestDistance)
                    {
                        _closestDistance = goalDistance;
                        _closest = node;
                    }

                    foreach (var dir in Directions.Clockwise)
                    {
                        var (dx, dy) = Directions.Delta(dir);

                        int nx = node.X + dx;
                        int ny = node.Y + dy;

                        if (nx < 0 || ny < 0 || _avoid != null && _avoid.Contains((nx, ny)))
                        {
                            continue;
                        }

                        // Resident() above guarantees no Missing here short of a copy expiring
                        // between the check and the read; a Missing then is treated as blocked
                        // for this expansion, and the next plan sees the refreshed copy.
                        if (TryStep(_ctx, _doors, _dyn, node.X, node.Y, node.Z, nx, ny, dir, dx, dy, out sbyte nz) != Walkability.Probe.Ok)
                        {
                            continue;
                        }

                        // Diagonals cost more, and matching UO's own cost ratio keeps routes
                        // looking like the ones the client would produce.
                        int stepCost = dx != 0 && dy != 0 ? 14 : 10;
                        int cost = node.Cost + stepCost;
                        long key = Key(nx, ny, nz);

                        if (_best.TryGetValue(key, out int known) && known <= cost)
                        {
                            continue;
                        }

                        _best[key] = cost;

                        var next = new Node(nx, ny, nz, dir, cost, Heuristic(nx, ny, _tx, _ty), node);

                        _open.Enqueue(next, cost + next.Estimate);
                    }
                }
            }

            /// <summary>
            /// True when every chunk the 3x3 neighbourhood of (x, y) touches is copied and fresh;
            /// otherwise queues the missing ones in <see cref="PendingChunks"/>.
            /// </summary>
            private bool Resident(int x, int y)
            {
                _pending.Clear();

                int minCx = Math.Max(0, (x - 1) >> 3), maxCx = (x + 1) >> 3;
                int minCy = Math.Max(0, (y - 1) >> 3), maxCy = (y + 1) >> 3;

                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    for (int cy = minCy; cy <= maxCy; cy++)
                    {
                        if (!PathGrid.IsResident(_ctx.Map, cx << 3, cy << 3))
                        {
                            _pending.Add((cx, cy));
                        }
                    }
                }

                return _pending.Count == 0;
            }

            private StepResult Finish(Stopwatch clock, Outcome ended)
            {
                _elapsedMs += clock.ElapsedMilliseconds;

                // Nothing reached the goal; hand back the best partial route we found - or null
                // when not even one step away from the start was any closer.
                var path = ended == Outcome.Goal || !ReferenceEquals(_closest, _start)
                    ? Reconstruct(_closest)
                    : null;

                Result = new PlanResult(path, _closest.X, _closest.Y, _closest.Z,
                    _expanded, _elapsedMs, _fills, ended);
                Done = true;

                return StepResult.Done;
            }
        }

        /// <summary>True when no goal height was asked for, or this height is on that floor.</summary>
        public static bool OnGoalFloor(sbyte z, int? tz) =>
            tz == null || Math.Abs(z - tz.Value) <= GOAL_Z_TOLERANCE;

        /// <summary>
        /// "How far from the goal" for choosing the closest tile when the goal is unreachable.
        /// Tiles first, then height: the ground under a roof is not close to the roof, but it is
        /// closer than the ground two streets over.
        /// </summary>
        private static int GoalDistance(int x, int y, sbyte z, int tx, int ty, int? tz) =>
            Chebyshev(x, y, tx, ty) * 64 + (tz == null ? 0 : Math.Min(63, Math.Abs(z - tz.Value)));

        /// <summary>
        /// Whether a character standing at (<paramref name="fromX"/>, <paramref name="fromY"/>,
        /// <paramref name="fromZ"/>) can step to (<paramref name="toX"/>, <paramref name="toY"/>),
        /// and at what Z they would end up. Any thread; reads <see cref="PathGrid"/> only.
        ///
        /// The answer comes from <see cref="Walkability.CalculateNewZ"/>, a port of the same
        /// walkability engine the game window uses, over the grid copy. A bespoke surface walk
        /// used to live here and disagreed with the engine on anything unusual - a shard-scripted
        /// staircase built from stacked dynamic Items planned as blocked while `canwalk`, which
        /// asked the real engine, showed it walkable - so the planner asks the engine's logic and
        /// nothing else.
        ///
        /// The one deliberate difference is preserved: a closed door is skipped past rather than
        /// asked about, since the real engine treats it as an ordinary blocking object (right for
        /// click-to-walk, wrong for planning a route out of a building nobody has opened yet) and
        /// GameActions.OpenDoor already handles opening it on approach (Navigation.WalkPath).
        /// </summary>
        public static Walkability.Probe TryStep(
            in PlanContext ctx,
            DoorIndex doors,
            PathGrid.DynamicSnapshot dyn,
            int fromX,
            int fromY,
            sbyte fromZ,
            int toX,
            int toY,
            Direction direction,
            int dx,
            int dy,
            out sbyte toZ)
        {
            toZ = fromZ;

            if (doors.Contains(toX, toY, fromZ))
            {
                var probe = TryGetSurfaceZ(ctx.Map, dyn, toX, toY, fromZ, out sbyte doorSurface);

                if (probe != Walkability.Probe.Ok)
                {
                    return probe;
                }

                if (Math.Abs(doorSurface - fromZ) > MAX_STEP_Z)
                {
                    return Walkability.Probe.Blocked;
                }

                toZ = doorSurface;
            }
            else
            {
                sbyte z = fromZ;
                var probe = Walkability.CalculateNewZ(toX, toY, ref z, (int)direction, ctx, dyn);

                if (probe != Walkability.Probe.Ok)
                {
                    return probe;
                }

                toZ = z;
            }

            // A diagonal is only legal if both orthogonal neighbours are also open - otherwise the
            // character would be squeezing through the corner between two walls, which the server
            // rejects. This check is intentionally left as the simpler surface probe rather than a
            // second CalculateNewZ call: it only needs to know that *some* standable surface exists
            // at each neighbour, not resolve an exact walk there.
            if (dx != 0 && dy != 0)
            {
                var a = TryGetSurfaceZ(ctx.Map, dyn, fromX + dx, fromY, fromZ, out _);

                if (a != Walkability.Probe.Ok)
                {
                    return a;
                }

                var b = TryGetSurfaceZ(ctx.Map, dyn, fromX, fromY + dy, fromZ, out _);

                if (b != Walkability.Probe.Ok)
                {
                    return b;
                }
            }

            return Walkability.Probe.Ok;
        }

        /// <summary>
        /// Highest standable surface at (x, y) that a character at <paramref name="refZ"/> could
        /// actually step onto; Blocked when the tile is, Missing when its chunk is not copied.
        ///
        /// <paramref name="refZ"/> matters: a building's roof and its floor are both "surfaces" at
        /// the same (x, y), and picking the highest unconditionally is what makes a character
        /// standing indoors appear to be on the roof.
        /// </summary>
        public static Walkability.Probe TryGetSurfaceZ(int map, PathGrid.DynamicSnapshot dyn, int x, int y, sbyte refZ, out sbyte z)
        {
            z = refZ;

            var statics = PathGrid.TryGetStatic(map, x, y);

            if (statics == null)
            {
                return Walkability.Probe.Missing;
            }

            var dynamics = dyn.At(x, y);

            bool found = false;
            int bestZ = int.MinValue;

            // First pass: find candidate surfaces near our own elevation.
            foreach (ref readonly var o in statics.AsSpan())
            {
                Surface(o, refZ, ref bestZ, ref found);
            }

            foreach (ref readonly var o in dynamics)
            {
                Surface(o, refZ, ref bestZ, ref found);
            }

            if (!found)
            {
                return Walkability.Probe.Blocked;
            }

            // Second pass: anything solid in the space we would occupy blocks the step. Doors are
            // exempt by design - the walker opens them.
            foreach (ref readonly var o in statics.AsSpan())
            {
                if (Blocks(o, bestZ))
                {
                    return Walkability.Probe.Blocked;
                }
            }

            foreach (ref readonly var o in dynamics)
            {
                if (Blocks(o, bestZ))
                {
                    return Walkability.Probe.Blocked;
                }
            }

            z = (sbyte)bestZ;

            return Walkability.Probe.Ok;
        }

        private static void Surface(in PathGrid.TileObj o, sbyte refZ, ref int bestZ, ref bool found)
        {
            if (!IsSurface(o, out int top) || Math.Abs(top - refZ) > MAX_STEP_Z)
            {
                return;
            }

            if (top > bestZ)
            {
                bestZ = top;
                found = true;
            }
        }

        private static bool Blocks(in PathGrid.TileObj o, int bestZ)
        {
            if (!IsBlocking(o, out int bottom, out int height))
            {
                return false;
            }

            int top = bottom + height;

            return top > bestZ && bottom < bestZ + PLAYER_HEIGHT;
        }

        private static bool IsSurface(in PathGrid.TileObj o, out int top)
        {
            top = 0;

            switch (o.Kind)
            {
                case PathGrid.TileObj.KIND_LAND:
                    if (o.Has(PathGrid.TileObj.TD_IMPASSABLE))
                    {
                        return false;
                    }

                    top = o.Z;

                    return true;

                case PathGrid.TileObj.KIND_ITEM when o.Is(PathGrid.TileObj.F_IS_MULTI):
                    return false;

                case PathGrid.TileObj.KIND_STATIC:
                case PathGrid.TileObj.KIND_ITEM:
                case PathGrid.TileObj.KIND_MULTI:
                    if (!o.Has(PathGrid.TileObj.TD_SURFACE) && !o.Has(PathGrid.TileObj.TD_BRIDGE))
                    {
                        return false;
                    }

                    // A bridge (stairs, ramp) is walked at half its height, which is how the server
                    // models a sloped surface.
                    top = o.Z + (o.Has(PathGrid.TileObj.TD_BRIDGE) ? o.Height / 2 : o.Height);

                    return true;

                default:
                    return false;
            }
        }

        private static bool IsBlocking(in PathGrid.TileObj o, out int bottom, out int height)
        {
            bottom = o.Z;
            height = 0;

            switch (o.Kind)
            {
                case PathGrid.TileObj.KIND_STATIC:
                case PathGrid.TileObj.KIND_ITEM:
                case PathGrid.TileObj.KIND_MULTI:
                    // Doors are planned through and opened on the way - see the class remarks.
                    if (o.Has(PathGrid.TileObj.TD_DOOR) || !o.Has(PathGrid.TileObj.TD_IMPASSABLE) ||
                        o.Has(PathGrid.TileObj.TD_SURFACE) || o.Has(PathGrid.TileObj.TD_BRIDGE))
                    {
                        return false;
                    }

                    height = Math.Max(1, (int)o.Height);

                    return true;

                default:
                    // Mobiles are not treated as obstacles: they move, and routing around every
                    // townsperson produces worse routes than simply bumping and re-pathing.
                    return false;
            }
        }

        private static List<Step> Reconstruct(Node node)
        {
            var steps = new List<Step>();

            for (var n = node; n?.Parent != null; n = n.Parent)
            {
                steps.Add(new Step(n.Direction, n.X, n.Y, n.Z));
            }

            steps.Reverse();

            return steps;
        }

        // Height is part of the key, in 8-unit bands: a slope's few units of drift stays one node,
        // while a floor and the roof above it (20 apart) are two. Without this the ground under a
        // building and its roof shared a key, and whichever was reached first pruned the other.
        private static long Key(int x, int y, sbyte z) =>
            (((long)x << 20) | (uint)y) << 8 | (byte)((z + 128) >> 3);

        private static int Heuristic(int x, int y, int tx, int ty) => Chebyshev(x, y, tx, ty) * 10;

        private static int Chebyshev(int x1, int y1, int x2, int y2) =>
            Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

        private sealed class Node
        {
            public Node(int x, int y, sbyte z, Direction direction, int cost, int estimate, Node parent)
            {
                X = x;
                Y = y;
                Z = z;
                Direction = direction;
                Cost = cost;
                Estimate = estimate;
                Parent = parent;
            }

            public int X { get; }
            public int Y { get; }
            public sbyte Z { get; }
            public Direction Direction { get; }
            public int Cost { get; }
            public int Estimate { get; }
            public Node Parent { get; }
        }
    }
}
