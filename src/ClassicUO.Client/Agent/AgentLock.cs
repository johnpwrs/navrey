// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using System.IO;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Ensures only one client at a time owns the command and log files.
    ///
    /// Two clients sharing them is not a small problem. They fight over one character, and because
    /// each truncates the log at startup while the other keeps writing at its old offset, the log
    /// fills with NUL padding and every reader - navrey, tail, an agent - sees garbage. Both
    /// failures look like a mystery rather than "you started two clients", so refuse up front.
    /// </summary>
    internal sealed class AgentLock : IDisposable
    {
        private readonly string _path;
        private bool _held;

        private AgentLock(string path)
        {
            _path = path;
        }

        /// <summary>
        /// Claims ownership of <paramref name="commandFile"/>. Returns null when another live
        /// client already holds it, with the reason in <paramref name="heldBy"/>.
        /// </summary>
        public static AgentLock TryAcquire(string commandFile, out string heldBy)
        {
            heldBy = null;

            string path = commandFile + ".pid";

            try
            {
                if (File.Exists(path) &&
                    int.TryParse(File.ReadAllText(path).Trim(), out int existing) &&
                    existing != Environment.ProcessId &&
                    IsAlive(existing))
                {
                    heldBy = $"process {existing}";

                    return null;
                }

                File.WriteAllText(path, Environment.ProcessId.ToString());
            }
            catch (Exception ex)
            {
                // A lock we cannot write is not worth failing startup over - warn and carry on.
                heldBy = null;

                Utility.Logging.Log.Warn($"agent: could not write {path}: {ex.Message}");

                return new AgentLock(path);
            }

            return new AgentLock(path) { _held = true };
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);

                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                // No such process - a stale lock from a client that was killed.
                return false;
            }
            catch
            {
                // Cannot tell; assume it is alive rather than trampling a running client.
                return true;
            }
        }

        public void Dispose()
        {
            if (!_held)
            {
                return;
            }

            _held = false;

            try
            {
                if (File.Exists(_path) &&
                    int.TryParse(File.ReadAllText(_path).Trim(), out int owner) &&
                    owner == Environment.ProcessId)
                {
                    File.Delete(_path);
                }
            }
            catch
            {
                // Best effort; a stale lock is detected by liveness anyway.
            }
        }
    }
}
