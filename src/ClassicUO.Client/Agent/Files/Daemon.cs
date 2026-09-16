// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Threading;

namespace ClassicUO.Agent
{
    /// <summary>
    /// File-based command intake, unchanged in protocol from the old headless client so the
    /// existing uo-* skills keep working: append a line to the command file, read the log file.
    ///
    /// Lines are handed to the command worker rather than executed here, so a long-running command
    /// (goto, travel) no longer stalls command intake the way it did when the daemon loop executed
    /// commands inline.
    /// </summary>
    internal sealed class Daemon : IDisposable
    {
        public const string DEFAULT_COMMAND_FILE = "/tmp/cuocmd";
        public const string DEFAULT_LOG_FILE = "/tmp/cuolog";

        /// <summary>
        /// How often the command file is checked for new lines. This is the dominant cost of every
        /// command: the wait averages half the interval, so at 200ms a round trip measured ~100ms
        /// against a game-thread hop of only 1-2ms - roughly 95% of the latency was spent here.
        ///
        /// 5ms brings that to ~2.5ms, at which point the transport stops being the bottleneck and a
        /// FIFO or socket would buy nothing measurable. The cost is 200 FileInfo stats a second on a
        /// background thread, which does not register against the game loop's own ~1ms iteration.
        /// </summary>
        private const int POLL_MS = 5;

        private readonly string _commandFile;
        private readonly Action<string> _onCommand;
        private Thread _thread;
        private volatile bool _running;

        public Daemon(string commandFile, string logFile, Action<string> onCommand)
        {
            _commandFile = commandFile;
            _onCommand = onCommand;

            File.WriteAllText(_commandFile, string.Empty);
            Output.OpenLogFile(logFile);
        }

        public void Start()
        {
            _running = true;

            _thread = new Thread(Loop)
            {
                Name = "CUO_AGENT_DAEMON",
                IsBackground = true
            };

            _thread.Start();
        }

        private void Loop()
        {
            long filePos = 0;

            while (_running)
            {
                Thread.Sleep(POLL_MS);

                try
                {
                    var info = new FileInfo(_commandFile);

                    if (!info.Exists)
                    {
                        filePos = 0;
                        continue;
                    }

                    long newLen = info.Length;

                    // The file was truncated or replaced - start over rather than seeking past
                    // the end and reading nothing forever.
                    if (newLen < filePos)
                    {
                        filePos = 0;
                    }

                    if (newLen <= filePos)
                    {
                        continue;
                    }

                    using var fs = new FileStream(_commandFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(filePos, SeekOrigin.Begin);

                    using var sr = new StreamReader(fs);

                    string line;

                    while ((line = sr.ReadLine()) != null)
                    {
                        filePos = fs.Position;

                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            _onCommand(line.Trim());
                        }
                    }
                }
                catch (Exception ex)
                {
                    Output.Error($"daemon read failed: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            _thread?.Join(1000);
            Output.Close();
        }
    }
}
