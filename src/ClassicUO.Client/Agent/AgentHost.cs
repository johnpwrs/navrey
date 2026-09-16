// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Owns the agent layer: the dispatcher, the command worker thread and the file daemon.
    ///
    /// Threading model:
    ///   game thread   - the FNA loop; sole owner of NetClient and World. Calls PumpGameThread().
    ///   command worker- executes commands serially, exactly as the old daemon loop did. May block.
    ///   daemon reader - tails the command file and queues lines for the worker.
    /// </summary>
    internal sealed class AgentHost : IDisposable
    {
        public static AgentHost Instance { get; private set; }

        private readonly GameDispatcher _dispatcher = new();
        private readonly CommandEngine _engine = new();
        private readonly BlockingCollection<string> _pending = new(new ConcurrentQueue<string>());

        private Daemon _daemon;
        private AgentLock _lock;
        private Thread _worker;
        private volatile bool _running;

        /// <summary>Cancels whatever long-running command is currently executing.</summary>
        private CancellationTokenSource _currentCommand;

        private AgentHost()
        {
        }

        public static void Start()
        {
            if (Instance != null || !CUOEnviroment.Agent)
            {
                return;
            }

            var host = new AgentHost();

            host.Initialize();

            Instance = host;
        }

        private void Initialize()
        {
            string cmdFile = CUOEnviroment.AgentCommandFile ?? Daemon.DEFAULT_COMMAND_FILE;
            string logFile = CUOEnviroment.AgentLogFile ?? Daemon.DEFAULT_LOG_FILE;

            _lock = AgentLock.TryAcquire(cmdFile, out string heldBy);

            if (_lock == null)
            {
                Utility.Logging.Log.Error(
                    $"agent: {cmdFile} is already owned by {heldBy}. Not starting the CLI daemon - " +
                    "two clients would fight over one character and corrupt the log. " +
                    "Stop the other client first, or pass -cmdfile/-logfile to use different files.");

                return;
            }

            _running = true;

            _worker = new Thread(WorkerLoop)
            {
                Name = "CUO_AGENT_WORKER",
                IsBackground = true
            };

            _worker.Start();

            _daemon = new Daemon(cmdFile, logFile, Enqueue);
            _daemon.Start();

            StateFile.Start();
            WorldFile.Start();

            Output.Raw(
                $"=== ClassicUO agent ready - commands: {cmdFile}, log: {logFile}, " +
                $"state: {StateFile.Path}, world: {WorldFile.Path} ===");
        }

        /// <summary>
        /// Shortest gap between two snapshot builds. The game thread does the building, so this is
        /// the knob that decides how much of a frame the agent layer costs.
        /// </summary>
        private const long SNAPSHOT_INTERVAL_MS = 100;

        private long _lastSnapshotMs;

        /// <summary>
        /// Agent time in one frame above which a `[PERF]` line is written. Half a 60fps frame:
        /// anything past this is a visible hitch, and the game's own work still has to fit.
        /// </summary>
        private const double PERF_FRAME_MS = 8;

        /// <summary>At most one `[PERF] frame` line per this interval, so a bad stretch is one line a second, not sixty.</summary>
        private const long PERF_LOG_INTERVAL_MS = 1000;

        private long _lastPerfLogMs;

        /// <summary>
        /// Called once per frame from GameController.Update, on the game thread.
        ///
        /// Every hook here runs with the frame loop stopped, so each is timed and the frame gets a
        /// `[PERF]` line when the agent's share crosses PERF_FRAME_MS. That line is the difference
        /// between "the game feels slow near the bank" and knowing which hook did it - the same
        /// lag was chased by feel for a while before this existed.
        /// </summary>
        public void PumpGameThread()
        {
            var world = ClassicUO.Client.Game?.UO?.World;

            long t0 = Stopwatch.GetTimestamp();

            MapResidency.Update(world);

            long t1 = Stopwatch.GetTimestamp();

            Narrator.Install(world);
            Narrator.Update(world);

            _dispatcher.Pump();

            long t2 = Stopwatch.GetTimestamp();

            // Last, so a command the dispatcher just ran is already reflected in this frame's
            // snapshots rather than the next one's.
            //
            // Throttled rather than run every frame. Building these snapshots is not cheap: the
            // world snapshot scans every tracked item (a town is ~2200) and every mobile, filters
            // and sorts them, and formats a serial string per survivor - all on the game thread,
            // all discarded if nothing changed. At 60fps that was sixty full scans a second
            // feeding readers that poll at 250ms, and the allocation churn showed up as
            // multi-second frame stalls that made `updatedAtMs` look like a dead client.
            //
            // SNAPSHOT_INTERVAL_MS is the floor on how often a change can become visible. Keep it
            // well under the 250ms readers expect, so the published contract ("rewritten whenever
            // anything changes, and at least every 250ms") still holds.
            long nowMs = Environment.TickCount64;
            long t3 = t2, t4 = t2;

            if (nowMs - _lastSnapshotMs >= SNAPSHOT_INTERVAL_MS)
            {
                _lastSnapshotMs = nowMs;

                StateFile.Update(world);
                t3 = Stopwatch.GetTimestamp();

                WorldFile.Update(world);
                t4 = Stopwatch.GetTimestamp();
            }

            double total = Ms(t0, t4);

            if (total < PERF_FRAME_MS || nowMs - _lastPerfLogMs < PERF_LOG_INTERVAL_MS)
            {
                return;
            }

            _lastPerfLogMs = nowMs;

            string slowest = _dispatcher.LastMaxActionLabel == null
                ? string.Empty
                : $" (max {_dispatcher.LastMaxActionLabel} {_dispatcher.LastMaxActionMs:F1}ms)";

            Output.Raw($"[PERF] frame agent={total:F1}ms residency={Ms(t0, t1):F1} " +
                       $"dispatch={Ms(t1, t2):F1}{slowest} state={Ms(t2, t3):F1} world={Ms(t3, t4):F1}");
        }

        private static double Ms(long from, long to) =>
            (to - from) * 1000.0 / Stopwatch.Frequency;

        /// <summary>Queues a command line for execution. Safe from any thread.</summary>
        public void Enqueue(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            line = line.Trim();

            // `stop` must not wait behind the command it is meant to cancel.
            if (line.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("stopgo", StringComparison.OrdinalIgnoreCase))
            {
                var cts = _currentCommand;
                bool cancelledCommand = cts != null && !cts.IsCancellationRequested;
                bool cancelledWalk = Navigator.Active;

                if (cancelledCommand)
                {
                    cts.Cancel();
                }

                // The walk runs on its own thread rather than the worker, so it is not covered by
                // _currentCommand and has to be stopped separately.
                Navigator.Stop();

                Output.Raw("[CMD] stop");

                if (cancelledCommand || cancelledWalk)
                {
                    Output.Info(cancelledWalk && !cancelledCommand ? "Cancelling walk"
                        : cancelledWalk ? "Cancelling current command and walk"
                        : "Cancelling current command");
                }
                else
                {
                    Output.Info("Nothing running");
                }

                Output.Raw("[CMD-END] stop ok");

                return;
            }

            _pending.Add(line);
        }

        private void WorkerLoop()
        {
            foreach (string line in _pending.GetConsumingEnumerable())
            {
                if (!_running)
                {
                    break;
                }

                Execute(line);
            }
        }

        private void Execute(string line)
        {
            // [CMD] ... [CMD-END] brackets only what this handler printed. It says the handler
            // returned - NOT that the server has responded. Packets that arrive as a result show up
            // later, unbracketed, because they cannot honestly be attributed to a command.
            Output.Raw($"[CMD] {line}");

            using var cts = new CancellationTokenSource();

            _currentCommand = cts;

            string[] args = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            var ctx = new CommandContext(line, args, _dispatcher, cts.Token);

            bool ok = _engine.TryExecute(ctx, out string error);

            _currentCommand = null;

            if (ok)
            {
                Output.Raw($"[CMD-END] {line} ok");
            }
            else
            {
                Output.Error(error);
                Output.Raw($"[CMD-END] {line} error: {error}");
            }
        }

        public void Dispose()
        {
            _running = false;

            _pending.CompleteAdding();
            _currentCommand?.Cancel();
            _dispatcher.Shutdown();

            _worker?.Join(1000);

            // Before the daemon, which closes the log Output writes to.
            StateFile.Stop();
            WorldFile.Stop();

            _daemon?.Dispose();
            _lock?.Dispose();

            Instance = null;
        }
    }
}
