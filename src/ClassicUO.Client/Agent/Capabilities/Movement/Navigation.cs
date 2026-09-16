// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.Data;

namespace ClassicUO.Agent.Capabilities
{
    /// <summary>
    /// Goal-directed movement: walk to a coordinate, keep going when the route turns out to be
    /// wrong, and cross a continent.
    ///
    /// Planning is done by <see cref="GridPathfinder"/> over the map and static data - notably
    /// treating doors as passable, so a route out of a building exists at all. Execution is one
    /// step at a time through PlayerMobile.Walk, which is the client's own movement path and owns
    /// the walk sequence the server validates. That split is the whole trick: the CLI plans its own
    /// route but moves the character exactly the way the game window does, so the two can never
    /// disagree about where it is.
    ///
    /// The supervision around it is carried over from the old standalone client, where it was tuned
    /// against this shard:
    ///
    ///   - Doors are opened on approach. The planner routed through them deliberately; something
    ///     has to actually open them.
    ///   - Progress is judged by the player's actual tile, never by a "step accepted" signal.
    ///     Turning to face a new direction is acknowledged exactly like a move, so trusting the
    ///     acknowledgement makes a stuck character look like a walking one.
    ///   - A tile the server refuses is remembered and routed around on the next attempt; the
    ///     planner cannot see everything the server enforces.
    ///   - Long trips are split into hops, keeping each search small and each failure local.
    /// </summary>
    internal static class Navigation
    {
        /// <summary>
        /// Longest single planned leg before the trip is split into hops. Generous on purpose:
        /// GridPathfinder has its own large node budget and reads map data directly, so the only
        /// real cost of a long leg is the map region held in memory - and fewer waypoints means a
        /// far more direct route, since each waypoint is a point on the straight line that the
        /// journey is forced to detour through.
        /// </summary>
        private const int HOP_TILES = 250;

        /// <summary>How long one step may take before we treat it as refused.</summary>
        private const int STEP_TIMEOUT_MS = 1200;

        /// <summary>Failed steps tolerated before giving up on a destination.</summary>
        private const int MAX_FAILURES = 25;

        /// <summary>
        /// Tiles around a caller-supplied obstacle to treat as unwalkable when no radius is given.
        /// Wide enough that a route does not brush past a creature standing at the centre - the
        /// point of naming an obstacle is not to clip its exact tile, it is not to meet it.
        /// </summary>
        public const int DEFAULT_AVOID_RADIUS = 8;

        /// <summary>
        /// Walks to (<paramref name="tx"/>, <paramref name="ty"/>), finishing on any tile within
        /// <paramref name="acceptDistance"/>.
        /// </summary>
        public static bool GoTo(CommandContext ctx, int tx, int ty, int acceptDistance = 0,
                                HashSet<(int x, int y)> avoid = null, int? tz = null)
        {
            try
            {
                return Run(ctx, tx, ty, tz, acceptDistance, settleForNearest: false, avoid);
            }
            finally
            {
                MapResidency.ClearFocus();
            }
        }

        /// <summary>
        /// Like <see cref="GoTo"/>, but walks as close as it can when the target itself cannot be
        /// stood on - the common case for a POI coordinate marking a building rather than a floor
        /// tile, or a tile something else is standing on.
        /// </summary>
        public static bool GoToNear(CommandContext ctx, int tx, int ty, int acceptDistance = 1,
                                    HashSet<(int x, int y)> avoid = null, int? tz = null)
        {
            try
            {
                return Run(ctx, tx, ty, tz, acceptDistance, settleForNearest: true, avoid);
            }
            finally
            {
                MapResidency.ClearFocus();
            }
        }

        /// <summary>
        /// Crosses a long distance as a chain of hops along the line to the target, each walked
        /// with nearest-acceptable semantics so one awkward waypoint cannot abort the journey.
        /// </summary>
        public static bool Travel(CommandContext ctx, int tx, int ty,
                                  HashSet<(int x, int y)> avoid = null, int? tz = null)
        {
            while (!ctx.Cancel.IsCancellationRequested)
            {
                var here = Position(ctx);

                double remaining = Math.Sqrt(Math.Pow(tx - here.x, 2) + Math.Pow(ty - here.y, 2));

                if (remaining <= 1 && GridPathfinder.OnGoalFloor(here.z, tz))
                {
                    ctx.Print($"Arrived at ({here.x}, {here.y})");

                    return true;
                }

                bool finalHop = remaining <= HOP_TILES;

                int wx = tx;
                int wy = ty;

                if (!finalHop)
                {
                    double t = HOP_TILES / remaining;

                    wx = here.x + (int)Math.Round((tx - here.x) * t);
                    wy = here.y + (int)Math.Round((ty - here.y) * t);
                }

                ctx.Info($"Travel: heading toward ({wx}, {wy}) - {remaining:F0} tiles remaining to ({tx}, {ty})");

                // The goal height only means anything on the final hop; a waypoint on the way is
                // just a point on the ground.
                bool hopOk = GoToNear(ctx, wx, wy, avoid: avoid, tz: finalHop ? tz : null);

                if (ctx.Cancel.IsCancellationRequested)
                {
                    return false;
                }

                var after = Position(ctx);

                if (finalHop)
                {
                    // GoToNear settles for the closest reachable tile, so false here means it
                    // could not take a single step - a "No path" with the character exactly where
                    // it started. Reporting that as arrived (which this did) left `.nav` saying
                    // `arrived` 73 tiles short of the gate. Observed 2026-09-08.
                    if (!hopOk)
                    {
                        return false;
                    }

                    ctx.ClearFailure();
                    ctx.Print($"Travel finished at ({after.x}, {after.y}) - target ({tx}, {ty})");

                    return true;
                }

                // Only give up when a whole hop achieved nothing; partial progress is normal.
                if (after.x == here.x && after.y == here.y)
                {
                    ctx.Fail($"Travel: no progress toward ({wx}, {wy}) - stopped at ({after.x}, {after.y})");

                    return false;
                }

                ctx.ClearFailure();
            }

            return false;
        }

        private static bool Run(CommandContext ctx, int tx, int ty, int? tz, int acceptDistance,
                                bool settleForNearest, HashSet<(int x, int y)> avoid = null)
        {
            var start = Position(ctx);

            if (Chebyshev(start.x, start.y, tx, ty) > HOP_TILES)
            {
                return Travel(ctx, tx, ty, avoid, tz);
            }

            string target = tz == null ? $"({tx}, {ty})" : $"({tx}, {ty}, z {tz})";

            // Tiles the server refused. The planner works from map and static data; the server also
            // enforces things it cannot see (multis, other players, region rules), and being told
            // no is the only way to learn about those.
            //
            // Seeded with whatever the caller named as an obstacle (`avoid:x,y[,r]`), which is how
            // a route is made to go *around* a creature rather than through it. Both kinds of
            // blockage mean the same thing to the planner, so they share one set.
            var blocked = avoid == null
                ? new HashSet<(int x, int y)>()
                : new HashSet<(int x, int y)>(avoid);
            int failures = 0;

            // A search that ran out of nodes without reaching the goal has already looked
            // everywhere it can. Its partial route is still worth walking once, but a refused
            // step on it must not trigger another full search: that was 25 replans of 60,000
            // nodes each against a sealed compound at Old Haven, several seconds of game-thread
            // work in slices, for a target that was never reachable.
            bool exhausted = false;

            while (!ctx.Cancel.IsCancellationRequested)
            {
                var here = Position(ctx);

                if (Reached(here.x, here.y, here.z, tx, ty, tz, acceptDistance))
                {
                    ctx.Print($"Arrived at ({here.x}, {here.y}, z {here.z}) - target {target}");

                    return true;
                }

                // Make sure the map data for the area is in memory before planning across it.
                ctx.Game(w => MapResidency.SetFocus(w, tx, ty), "SetFocus");

                // The planner is built on the game thread (it copies the plan's context out of
                // the live world) and then stepped here, on the walk's own thread, against the
                // PathGrid copies. The only game-thread work left is copying chunks the search
                // reaches for the first time. The walk's own token goes in so a superseded walk
                // stops planning the moment its replacement is issued.
                var planner = ctx.Game(
                    w => new GridPathfinder.Planner(w, tx, ty, tz, acceptDistance, blocked), "Plan");

                while (true)
                {
                    var step = planner.Step(GridPathfinder.MAX_PLAN_MS, ctx.Cancel);

                    if (step == GridPathfinder.StepResult.Done)
                    {
                        break;
                    }

                    if (ctx.Cancel.IsCancellationRequested)
                    {
                        ctx.Fail("Movement cancelled");

                        return false;
                    }

                    if (step == GridPathfinder.StepResult.NeedChunks)
                    {
                        ctx.Game(w => planner.FillPending(w), "PathGrid.Fill");
                    }
                }

                var plan = planner.Result;
                Navigator.RecordPlan(plan);

                exhausted = plan.Ended == GridPathfinder.Outcome.Nodes;

                int reachedX = plan.ReachedX;
                int reachedY = plan.ReachedY;
                sbyte reachedZ = plan.ReachedZ;
                var path = plan.Path;

                if (path == null || path.Count == 0)
                {
                    if (settleForNearest && (here.x != start.x || here.y != start.y))
                    {
                        ctx.ClearFailure();
                        ctx.Print($"Stopped at ({here.x}, {here.y}) - closest reachable to {target}");

                        return true;
                    }

                    ctx.Fail($"No path from ({here.x}, {here.y}) to {target}");

                    return false;
                }

                bool exact = Reached(reachedX, reachedY, reachedZ, tx, ty, tz, acceptDistance);

                if (!exact)
                {
                    ctx.Info($"{target} is not directly reachable - closest is ({reachedX}, {reachedY}, z {reachedZ})");
                }

                if (!WalkPath(ctx, path, blocked, ref failures))
                {
                    if (ctx.Cancel.IsCancellationRequested)
                    {
                        ctx.Fail("Movement cancelled");

                        return false;
                    }

                    if (failures >= MAX_FAILURES || exhausted)
                    {
                        var stuck = Position(ctx);

                        if (settleForNearest && (stuck.x != start.x || stuck.y != start.y))
                        {
                            ctx.ClearFailure();
                            ctx.Print($"Stopped at ({stuck.x}, {stuck.y}) - could not get closer to ({tx}, {ty})");

                            return true;
                        }

                        ctx.Fail($"Giving up at ({stuck.x}, {stuck.y}) - could not reach ({tx}, {ty})");

                        return false;
                    }

                    // Re-plan from wherever we actually are, avoiding whatever just refused us.
                    continue;
                }

                var end = Position(ctx);

                if (Reached(end.x, end.y, end.z, tx, ty, tz, acceptDistance))
                {
                    ctx.Print($"Arrived at ({end.x}, {end.y}, z {end.z}) - target {target}");

                    return true;
                }

                if (!exact)
                {
                    if (!settleForNearest)
                    {
                        ctx.Fail($"Reached ({end.x}, {end.y}); ({tx}, {ty}) is not reachable - try gonear");

                        return false;
                    }

                    ctx.ClearFailure();
                    ctx.Print($"Stopped at ({end.x}, {end.y}) - closest reachable to ({tx}, {ty})");

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Walks a planned route. Returns false as soon as a step fails, so the caller re-plans
        /// from the real position instead of continuing along a route that no longer holds.
        /// </summary>
        private static bool WalkPath(CommandContext ctx, List<GridPathfinder.Step> path,
            HashSet<(int x, int y)> blocked, ref int failures)
        {
            foreach (var step in path)
            {
                if (ctx.Cancel.IsCancellationRequested)
                {
                    return false;
                }

                var before = Position(ctx);

                // Open a door standing in the next tile before trying to walk into it. The route
                // was planned through doors on purpose, so this is expected, not a recovery.
                if (ctx.Game(w => IsDoorAt(w, step.X, step.Y, before.z)))
                {
                    ctx.Game(_ => GameActions.OpenDoor());

                    if (!ctx.Sleep(400))
                    {
                        return false;
                    }
                }

                if (TakeStep(ctx, step.Direction, step.X, step.Y))
                {
                    failures = 0;

                    continue;
                }

                // The step may have failed on a door we did not detect - try opening whatever is in
                // reach and repeat once before writing the tile off.
                ctx.Game(_ => GameActions.OpenDoor());

                if (!ctx.Sleep(400))
                {
                    return false;
                }

                if (TakeStep(ctx, step.Direction, step.X, step.Y))
                {
                    failures = 0;

                    continue;
                }

                failures++;
                blocked.Add((step.X, step.Y));

                if (failures == 1 || failures % 5 == 0)
                {
                    ctx.Info($"Blocked at ({step.X}, {step.Y}) - re-planning ({failures}/{MAX_FAILURES})");
                }

                return false;
            }

            return true;
        }

        /// <summary>How long to keep retrying a step that Walk() is rejecting locally (see below).</summary>
        private const int THROTTLE_BUDGET_MS = 400;

        /// <summary>How long to wait for a turn-only request to be reflected in facing direction.</summary>
        private const int TURN_TIMEOUT_MS = 300;

        /// <summary>
        /// Attempts one step and reports whether the player actually reached the expected tile.
        ///
        /// UO's own walk protocol is two-phase whenever the direction changes: a request issued
        /// while not already facing that way only turns the character - PlayerMobile.Walk sets
        /// _serverDirection immediately and returns, without moving a tile - and the *next* request,
        /// now that the character faces the right way, is the one that actually steps. This used to
        /// be handled by inference (retry, and if a poll saw the right facing after ~250ms assume
        /// that was the turn and try again) rather than by knowing it outright, which is the main
        /// thing that made this pathfinder feel like it needed two tries per direction change where
        /// the old standalone client - which sent an explicit separate turn packet before the move
        /// packet - did not. Checking facing first and handling the turn as its own bounded step
        /// removes the guesswork.
        ///
        /// Separately, PlayerMobile.Walk() can also refuse a request without ever sending a packet -
        /// most commonly because the previous step's movement timer (Walker.LastStepRequestTime) has
        /// not elapsed yet, but also a full step queue or a pending resync. That return value used to
        /// be ignored entirely, which meant a purely local, immediate rejection was indistinguishable
        /// from a real step in flight: both ended up polling position for the full step timeout. For
        /// the common pacing case that is nearly a second wasted waiting on a packet that was never
        /// sent. Retrying quickly until Walk() actually accepts the request (see SendWalk) recovers
        /// most of that responsiveness without giving up the shared movement path.
        /// </summary>
        private static bool TakeStep(CommandContext ctx, Direction direction, int expectedX, int expectedY)
        {
            const int POLL_MS = 25;

            if (Position(ctx).dir != direction)
            {
                if (!SendWalk(ctx, direction))
                {
                    return false;
                }

                for (int waited = 0; waited < TURN_TIMEOUT_MS; waited += POLL_MS)
                {
                    if (Position(ctx).dir == direction)
                    {
                        break;
                    }

                    if (!ctx.Sleep(POLL_MS))
                    {
                        return false;
                    }
                }
            }

            if (!SendWalk(ctx, direction))
            {
                return false;
            }

            for (int waited = 0; waited < STEP_TIMEOUT_MS; waited += POLL_MS)
            {
                var now = Position(ctx);

                if (now.x == expectedX && now.y == expectedY)
                {
                    return true;
                }

                if (!ctx.Sleep(POLL_MS))
                {
                    return false;
                }
            }

            var final = Position(ctx);

            return final.x == expectedX && final.y == expectedY;
        }

        /// <summary>
        /// Issues a walk/turn request, retrying at a short interval while Walk() refuses it locally
        /// (see the pacing note on <see cref="TakeStep"/>). Returns false only if the budget below
        /// is spent with no request ever accepted.
        /// </summary>
        private static bool SendWalk(CommandContext ctx, Direction direction)
        {
            const int THROTTLE_RETRY_MS = 15;

            for (int waited = 0; waited < THROTTLE_BUDGET_MS; waited += THROTTLE_RETRY_MS)
            {
                // Run rather than walk - half the time per tile. The client itself drops back to
                // walking when stamina runs out, so asking to run is always safe.
                if (ctx.Game(w => w.Player.Walk(direction, true)))
                {
                    return true;
                }

                if (!ctx.Sleep(THROTTLE_RETRY_MS))
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a door sits at (x, y). Doors are usually dynamic Items rather than statics, so
        /// this checks the world's items the way the client's own door handling does.
        /// </summary>
        private static bool IsDoorAt(World world, int x, int y, int z)
        {
            foreach (var item in world.Items.Values)
            {
                if (item.X != x || item.Y != y || item.IsDestroyed)
                {
                    continue;
                }

                if (item.Z - 15 > z || item.Z + 15 < z)
                {
                    continue;
                }

                if (item.ItemData.IsDoor)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Within the accept distance, and on the goal floor if one was asked for.</summary>
        private static bool Reached(int x, int y, sbyte z, int tx, int ty, int? tz, int acceptDistance) =>
            Chebyshev(x, y, tx, ty) <= acceptDistance && GridPathfinder.OnGoalFloor(z, tz);

        private static (int x, int y, sbyte z, Direction dir) Position(CommandContext ctx) =>
            ctx.Game(w => (w.Player.X, w.Player.Y, w.Player.Z, w.Player.Direction & Direction.Mask));

        private static int Chebyshev(int x1, int y1, int x2, int y2) =>
            Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));
    }
}
