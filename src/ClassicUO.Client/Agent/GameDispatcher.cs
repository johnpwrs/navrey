// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Marshals work from the CLI worker thread onto the game thread.
    ///
    /// This is the one rule the whole agent layer is built around: <b>only the game thread may touch
    /// NetClient, World or Player</b>. NetClient.Send locks its send stream for the enqueue but
    /// encrypts beforehand using streaming, order-dependent cipher state that is not under that
    /// lock, so a send from any other thread can silently corrupt the connection. Reading world
    /// state off-thread is equally unsafe while packet handlers mutate it mid-frame.
    ///
    /// So commands run on a worker thread and reach the game through <see cref="Invoke"/>. That
    /// keeps the ported navigation logic written as straight-line procedural code (it can Sleep and
    /// poll freely without stalling the game loop) while every actual game touch happens in a
    /// single-threaded window at the top of GameController.Update.
    /// </summary>
    internal sealed class GameDispatcher
    {
        private readonly ConcurrentQueue<(Action run, string label)> _queue = new();
        private volatile bool _shutdown;

        /// <summary>How long <see cref="Invoke"/> waits before assuming the game thread is gone.</summary>
        private const int INVOKE_TIMEOUT_MS = 15000;

        /// <summary>
        /// The slowest action the last <see cref="Pump"/> ran, for the `[PERF]` frame line. Every
        /// action here runs with the frame loop stopped, so this is where a stall has a name.
        /// </summary>
        public double LastMaxActionMs { get; private set; }

        public string LastMaxActionLabel { get; private set; }

        /// <summary>
        /// Drains every pending action. Called once per frame from the game thread.
        /// Drains the whole queue rather than one item per frame, so a command that marshals several
        /// operations in a row is not spread across several frames.
        /// </summary>
        public void Pump()
        {
            LastMaxActionMs = 0;
            LastMaxActionLabel = null;

            while (_queue.TryDequeue(out var entry))
            {
                long started = Stopwatch.GetTimestamp();

                try
                {
                    entry.run();
                }
                catch (Exception ex)
                {
                    // An exception here would otherwise kill the game loop for a CLI typo.
                    Output.Error($"agent action failed: {ex.Message}");
                }

                double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                if (ms > LastMaxActionMs)
                {
                    LastMaxActionMs = ms;
                    LastMaxActionLabel = entry.label;
                }
            }
        }

        /// <summary>Queues work on the game thread without waiting for it.</summary>
        public void Post(Action action, string label = null)
        {
            if (_shutdown)
            {
                return;
            }

            _queue.Enqueue((action, label));
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the game thread and blocks the caller until it has
        /// completed. Exceptions are rethrown on the calling thread.
        /// </summary>
        public void Invoke(Action action, string label = null)
        {
            Invoke<object>(() =>
            {
                action();
                return null;
            }, label);
        }

        /// <summary>
        /// Runs <paramref name="func"/> on the game thread and returns its result.
        /// Never call this from the game thread - it would deadlock.
        /// <paramref name="label"/> is what the `[PERF]` line calls it if it is the slow one.
        /// </summary>
        public T Invoke<T>(Func<T> func, string label = null)
        {
            if (_shutdown)
            {
                throw new InvalidOperationException("agent dispatcher is shutting down");
            }

            if (Thread.CurrentThread == CUOEnviroment.GameThread)
            {
                // Already where we want to be; running inline avoids a guaranteed deadlock.
                return func();
            }

            using var done = new ManualResetEventSlim(false);

            T result = default;
            Exception error = null;

            _queue.Enqueue((() =>
            {
                try
                {
                    result = func();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            }, label));

            if (!done.Wait(INVOKE_TIMEOUT_MS))
            {
                throw new TimeoutException("game thread did not respond - is the client still running?");
            }

            if (error != null)
            {
                throw error;
            }

            return result;
        }

        public void Shutdown()
        {
            _shutdown = true;

            while (_queue.TryDequeue(out _))
            {
            }
        }
    }
}
