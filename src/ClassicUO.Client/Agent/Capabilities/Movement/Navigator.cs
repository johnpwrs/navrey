// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Threading;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Runs walking off the command worker, so a journey no longer freezes everything else.
    ///
    /// The command worker is a single serial consumer: while a `goto` was executing on it, every
    /// other command sat in the queue until the walk finished. A measured example - `pos` issued one
    /// second into a nine-second walk returned nine seconds later, reporting the destination rather
    /// than where the character was when it was asked. Worse than the latency, nothing could *act*
    /// mid-walk: no bandage while fleeing, no attack on approach, no door opened on the way.
    ///
    /// So movement gets its own thread and the command that started it returns immediately. Walking
    /// is the one thing the character does over a long span while remaining able to do other things,
    /// which is exactly the shape a background task wants.
    ///
    /// Fire and forget: `goto` reports that it *started*, never whether it arrived. The outcome
    /// lands in <see cref="Status"/>, published as the `nav` block of the state file. Position alone
    /// cannot carry it - "still walking", "arrived" and "gave up, no path" all look like a
    /// coordinate that may or may not be changing.
    ///
    /// One walk at a time, last-wins: a second movement command cancels the first rather than
    /// queueing behind it or fighting it for the walk sequence. A character cannot walk two places
    /// at once, and the newer instruction is the one the caller meant.
    /// </summary>
    internal static class Navigator
    {
        public enum NavStatus
        {
            /// <summary>Nothing has been asked for this session.</summary>
            Idle,

            /// <summary>A walk is running right now.</summary>
            Walking,

            /// <summary>Reached the target, or the nearest tile `goto` will settle for.</summary>
            Arrived,

            /// <summary>The planner could not reach the target at all.</summary>
            NoPath,

            /// <summary>Cancelled by `stop`, or superseded by a newer movement command.</summary>
            Stopped,

            /// <summary>The walk threw. <see cref="Reason"/> carries the message.</summary>
            Failed
        }

        private static readonly object _lock = new();

        private static Thread _thread;
        private static CancellationTokenSource _cts;

        public static NavStatus Status { get; private set; } = NavStatus.Idle;

        /// <summary>Where the current or most recent walk was aimed.</summary>
        public static int TargetX { get; private set; }

        public static int TargetY { get; private set; }

        /// <summary>The command that started it, for reporting.</summary>
        public static string Command { get; private set; }

        /// <summary>Why a walk ended other than by arriving; null otherwise.</summary>
        public static string Reason { get; private set; }

        public static bool Active => Status == NavStatus.Walking;

        /// <summary>A new target this close to the active walk's is treated as the same walk.</summary>
        private const int SAME_TARGET_TILES = 2;

        // Planning cost of the current walk, for the [NAV] outcome line. The search runs on the
        // walk's thread against PathGrid copies, so planMs is search time, not frame time; the
        // game thread's share is the chunk copies, counted in `fills`.
        private static int _plans;
        private static long _planNodes, _planMs, _planMaxMs;

        /// <summary>Records one plan's cost against the current walk. Safe from any thread.</summary>
        public static void RecordPlan(Capabilities.GridPathfinder.PlanResult plan)
        {
            lock (_lock)
            {
                _plans++;
                _planNodes += plan.NodesExplored;
                _planMs += plan.ElapsedMs;
                _planMaxMs = Math.Max(_planMaxMs, plan.ElapsedMs);
            }

            // A plan that took longer than one old slice is worth a line of its own, at the time
            // it happened, whether or not the walk ever finishes: it cost no frame, but it says the
            // route was hard, and how hard - and `fills` says how often it had to wait on the
            // game thread for a chunk copy.
            if (plan.ElapsedMs >= Capabilities.GridPathfinder.MAX_PLAN_MS)
            {
                Output.Raw($"[PERF] plan {plan.ElapsedMs}ms nodes={plan.NodesExplored} " +
                           $"fills={plan.Fills} ended={plan.Ended.ToString().ToLowerInvariant()}");
            }
        }

        /// <summary>
        /// Starts <paramref name="walk"/> on the movement thread and returns at once.
        ///
        /// <paramref name="walk"/> is handed a context carrying this navigator's cancellation token,
        /// not the originating command's - the command is over the moment this returns, and its
        /// token is disposed with it.
        /// </summary>
        public static void Start(
            GameDispatcher dispatcher,
            string command,
            int targetX,
            int targetY,
            Func<CommandContext, bool> walk)
        {
            lock (_lock)
            {
                // A re-aim at (almost) the same tile is the same walk. The combat reflexes re-issue
                // `goto` at a target's own tile whenever it shuffles, and each re-issue used to
                // cancel the plan in progress - fine when plans were instant, but a plan that is
                // legitimately spanning frames was being thrown away at 774ms and 1544ms of work
                // for a one-tile change in goal (measured 2026-09-08). The walk already settles
                // for the closest reachable tile, so finishing the old plan lands within reach.
                if (Active && Math.Max(Math.Abs(targetX - TargetX), Math.Abs(targetY - TargetY)) <= SAME_TARGET_TILES)
                {
                    return;
                }

                CancelRunning();

                var cts = new CancellationTokenSource();

                _cts = cts;

                Status = NavStatus.Walking;
                TargetX = targetX;
                TargetY = targetY;
                Command = command;
                Reason = null;

                _plans = 0;
                _planNodes = _planMs = _planMaxMs = 0;

                _thread = new Thread(() => Run(dispatcher, command, walk, cts))
                {
                    Name = "CUO_AGENT_NAV",
                    IsBackground = true
                };

                _thread.Start();
            }
        }

        private static void Run(
            GameDispatcher dispatcher,
            string command,
            Func<CommandContext, bool> walk,
            CancellationTokenSource cts)
        {
            NavStatus outcome;
            string reason = null;

            try
            {
                string[] args = command.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                var ctx = new CommandContext(command, args, dispatcher, cts.Token);

                bool ok = walk(ctx);

                if (cts.IsCancellationRequested)
                {
                    outcome = NavStatus.Stopped;
                    reason = "cancelled";
                }
                else if (ok)
                {
                    outcome = NavStatus.Arrived;
                }
                else
                {
                    outcome = NavStatus.NoPath;
                    reason = ctx.Failure ?? "could not reach the target";
                }
            }
            catch (OperationCanceledException)
            {
                outcome = NavStatus.Stopped;
                reason = "cancelled";
            }
            catch (Exception ex)
            {
                outcome = NavStatus.Failed;
                reason = ex.Message;

                Output.Error($"walk failed: {ex.Message}");
            }

            string planning;

            lock (_lock)
            {
                // A newer walk may have replaced this one while it was unwinding; it owns the status
                // now, and stamping this one's outcome over it would report the wrong thing.
                if (!ReferenceEquals(_cts, cts))
                {
                    return;
                }

                Status = outcome;
                Reason = reason;
                _thread = null;

                planning = $" plans={_plans} nodes={_planNodes} planMs={_planMs} maxPlanMs={_planMaxMs}";
            }

            // Outside the lock: this is the only announcement a fire-and-forget walk makes, and it
            // must not be able to block a caller holding it.
            Output.Raw($"[NAV] {command} -> {outcome.ToString().ToLowerInvariant()}" +
                       (reason == null ? string.Empty : $" ({reason})") + planning);
        }

        /// <summary>Cancels any walk in progress. Safe to call when nothing is running.</summary>
        public static void Stop()
        {
            lock (_lock)
            {
                if (CancelRunning())
                {
                    Status = NavStatus.Stopped;
                    Reason = "cancelled";
                }
            }
        }

        /// <summary>Caller must hold <see cref="_lock"/>. Returns true if something was cancelled.</summary>
        private static bool CancelRunning()
        {
            var cts = _cts;

            if (cts == null)
            {
                return false;
            }

            _cts = null;

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            // Deliberately not joined: the walk thread may be mid-sleep inside a step poll, and the
            // caller is a command handler that must stay responsive. It observes the token and exits
            // on its own; the ReferenceEquals guard in Run keeps its late outcome from being stamped
            // over whatever replaced it.
            return true;
        }
    }
}
