// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Publishes a JSON document to a file, continuously and safely, for readers outside the
    /// process. Shared by <see cref="StateFile"/> and <see cref="WorldFile"/>.
    ///
    /// Four things this gets right, all of which cost something to discover:
    ///
    ///   Cadence. Callers build a snapshot every frame; only a snapshot that differs from the last
    ///   is written, plus a heartbeat so `updatedAtMs` keeps proving the client is alive. Standing
    ///   still costs four writes a second rather than the frame rate.
    ///
    ///   Change detection excludes the timestamp. It cannot be part of the compared body or every
    ///   frame differs and the cadence collapses, so it is spliced onto the front at write time.
    ///
    ///   The write happens on a background thread. The game loop must never block on the
    ///   filesystem, and a slow disk would otherwise stall the frame.
    ///
    ///   The write lands via a rename. rename(2) is atomic, so a reader running `jq` in a loop sees
    ///   either the whole previous document or the whole new one - never a prefix. Writing in place
    ///   eventually hands a polling reader a truncated object.
    /// </summary>
    internal sealed class JsonFileWriter : IDisposable
    {
        /// <summary>Longest the file may go without a write, so `updatedAtMs` proves liveness.</summary>
        private const int HEARTBEAT_MS = 250;

        /// <summary>Give up after this many consecutive failures rather than log every frame.</summary>
        private const int MAX_CONSECUTIVE_FAILURES = 10;

        /// <summary>
        /// Every live writer, so one signal handler can flush them all.
        ///
        /// `kill` an FNA client and the process dies without reaching GameController.UnloadContent,
        /// so no Dispose runs and the files are left claiming inGame:true forever. Catching
        /// SIGTERM/SIGINT closes that for the ordinary case - it is not a substitute for checking
        /// staleness, since nothing can run on SIGKILL or a crash, which is why `updatedAtMs`
        /// remains the authoritative liveness test for readers.
        /// </summary>
        private static readonly List<JsonFileWriter> _live = new();

        private static readonly object _liveLock = new();
        private static PosixSignalRegistration[] _signals;

        private readonly object _lock = new();
        private readonly ManualResetEventSlim _hasPending = new(false);
        private readonly string _label;

        private Thread _thread;
        private byte[] _pendingBody;
        private long _pendingSnapshotMs;
        private byte[] _lastBody = Array.Empty<byte>();
        private long _lastWriteMs;
        private int _failures;
        private volatile bool _running;

        /// <summary>Writer-thread only: the newest body actually handed over, kept so it can be
        /// re-stamped with a fresh heartbeat while the game thread is quiet or stalled.</summary>
        private byte[] _lastSentBody;
        private long _lastSentSnapshotMs;
        private long _lastDiskWriteMs;
        private volatile bool _dirty;

        public JsonFileWriter(string path, string label)
        {
            Path = path;
            _label = label;
            _running = true;

            _thread = new Thread(WriterLoop)
            {
                Name = "CUO_AGENT_" + label.Replace(" ", "_").ToUpperInvariant(),
                IsBackground = true
            };

            _thread.Start();

            lock (_liveLock)
            {
                _live.Add(this);

                InstallSignalHandlers();
            }
        }

        public string Path { get; }

        public bool Running => _running;

        /// <summary>
        /// Offers a freshly built body. Writes it when it differs from the last one or the heartbeat
        /// is due; otherwise does nothing. The body must be a complete JSON object.
        /// </summary>
        public void Publish(ReadOnlySpan<byte> body)
        {
            if (!_running)
            {
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            bool changed = !body.SequenceEqual(_lastBody);
            bool heartbeat = now - _lastWriteMs >= HEARTBEAT_MS;

            if (!changed && !heartbeat)
            {
                return;
            }

            if (changed)
            {
                _lastBody = body.ToArray();
            }

            _lastWriteMs = now;

            lock (_lock)
            {
                // Overwritten rather than queued: if the writer falls behind, a stale snapshot is
                // worthless and only the newest one is worth the disk. Composing is left to the
                // writer thread so the game thread does no formatting it does not have to.
                _pendingBody = _lastBody;
                _pendingSnapshotMs = now;
                _dirty = true;
            }

            _hasPending.Set();
        }

        /// <summary>
        /// Prepends `updatedAtMs` to an already-serialized body: `{"updatedAtMs":N,` followed by the
        /// body from index 1, skipping its own opening brace. An empty object has no trailing comma
        /// to splice onto, hence the special case.
        /// </summary>
        private static byte[] Compose(ReadOnlySpan<byte> body, long snapshotMs, long heartbeatMs)
        {
            if (body.Length <= 2)
            {
                return Encoding.UTF8.GetBytes(
                    $"{{\"updatedAtMs\":{snapshotMs},\"heartbeatAtMs\":{heartbeatMs}}}\n");
            }

            byte[] head = Encoding.UTF8.GetBytes(
                $"{{\"updatedAtMs\":{snapshotMs},\"heartbeatAtMs\":{heartbeatMs},");
            var payload = new byte[head.Length + body.Length - 1 + 1];

            head.CopyTo(payload, 0);
            body.Slice(1).CopyTo(payload.AsSpan(head.Length));
            payload[^1] = (byte)'\n';

            return payload;
        }

        /// <summary>
        /// Writes pending snapshots, and re-stamps the last one when the game thread goes quiet.
        ///
        /// The re-stamp is the whole reason this waits with a timeout. `Publish` is called from the
        /// game thread, so a game-thread stall - a long frame, a texture upload, a GC pause - used
        /// to freeze `updatedAtMs` too, and every reader treats a frozen timestamp as a dead
        /// client. Measured: a visible game window in a busy town stalled the client past eight
        /// seconds and killed the healer and the speech listener repeatedly while the client was
        /// perfectly alive.
        ///
        /// So the two facts are published separately and must not be conflated:
        ///   updatedAtMs   - when the snapshot was built. Says how fresh the *data* is.
        ///   heartbeatAtMs - when this thread last ran. Says the *process* is alive.
        ///
        /// A re-stamp advances only the heartbeat, never `updatedAtMs`, so a stalled game thread
        /// can never make stale data look fresh.
        /// </summary>
        private void WriterLoop()
        {
            while (_running)
            {
                _hasPending.Wait(HEARTBEAT_MS);

                byte[] body;
                long snapshotMs;

                lock (_lock)
                {
                    if (_pendingBody != null)
                    {
                        _lastSentBody = _pendingBody;
                        _lastSentSnapshotMs = _pendingSnapshotMs;
                        _pendingBody = null;
                    }

                    _hasPending.Reset();

                    body = _lastSentBody;
                    snapshotMs = _lastSentSnapshotMs;
                }

                if (body == null || !_running)
                {
                    continue;
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (now - _lastDiskWriteMs < HEARTBEAT_MS && !_dirty)
                {
                    continue;
                }

                _dirty = false;
                _lastDiskWriteMs = now;

                WriteAtomically(Compose(body, snapshotMs, now));
            }
        }

        private void WriteAtomically(byte[] payload)
        {
            string temp = Path + ".tmp";

            try
            {
                System.IO.File.WriteAllBytes(temp, payload);
                System.IO.File.Move(temp, Path, overwrite: true);

                _failures = 0;
            }
            catch (Exception ex)
            {
                Fail($"{_label} write failed: {ex.Message}");

                try
                {
                    System.IO.File.Delete(temp);
                }
                catch
                {
                    // Nothing useful to do; the next write replaces it anyway.
                }
            }
        }

        public void Fail(string message)
        {
            int count = Interlocked.Increment(ref _failures);

            if (count == 1)
            {
                Output.Error(message);
            }
            else if (count == MAX_CONSECUTIVE_FAILURES)
            {
                Output.Error($"{_label} disabled after {MAX_CONSECUTIVE_FAILURES} failures: {message}");

                _running = false;
                _hasPending.Set();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (!_running)
                {
                    return;
                }

                // Claim the shutdown before writing: a signal handler and the normal teardown can
                // both land here, and the final document must be written exactly once.
                _running = false;
            }

            lock (_liveLock)
            {
                _live.Remove(this);
            }

            // One last document saying the character is gone, so a reader is not left holding a
            // plausible-looking snapshot of a client that has exited.
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                WriteAtomically(Encoding.UTF8.GetBytes(
                    $"{{\"updatedAtMs\":{now},\"heartbeatAtMs\":{now},\"inGame\":false}}\n"));
            }
            catch
            {
                // Shutting down anyway.
            }

            _hasPending.Set();
            _thread?.Join(1000);
            _thread = null;
        }

        /// <summary>Caller must hold <see cref="_liveLock"/>.</summary>
        private static void InstallSignalHandlers()
        {
            if (_signals != null)
            {
                return;
            }

            try
            {
                // Cancel is deliberately left false: the goal is a truthful last write, not to
                // change how the client shuts down.
                _signals = new[]
                {
                    PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => DisposeAll()),
                    PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => DisposeAll()),
                    PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => DisposeAll())
                };
            }
            catch (Exception ex)
            {
                // Not fatal - readers still have updatedAtMs.
                Output.Warn($"could not install signal handlers ({ex.Message})");
            }
        }

        private static void DisposeAll()
        {
            JsonFileWriter[] writers;

            lock (_liveLock)
            {
                writers = _live.ToArray();
            }

            foreach (var writer in writers)
            {
                writer.Dispose();
            }
        }
    }
}
