// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;

namespace ClassicUO.Agent
{
    internal enum AgentLogLevel
    {
        Trace,
        Debug,
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// The single writer for everything the CLI emits: command results, packet narration and
    /// errors. Line shapes are kept identical to the old headless client because the uo-* skills
    /// grep for them.
    ///
    /// Output is deliberately free-form text with no request ids. Packets arrive asynchronously and
    /// most are not responses to anything the CLI sent, so tagging them with a command id would be
    /// a fiction. The only framing is the [CMD] / [CMD-END] pair, which brackets the output a
    /// command handler produced itself.
    /// </summary>
    internal static class Output
    {
        private static readonly object _lock = new();
        private static StreamWriter _file;
        private static bool _echoToConsole = true;

        public static AgentLogLevel MinLevel { get; set; } = AgentLogLevel.Info;

        public static void OpenLogFile(string path)
        {
            lock (_lock)
            {
                _file?.Dispose();
                _file = new StreamWriter(path, append: false) { AutoFlush = true };
            }
        }

        public static void SetConsoleEcho(bool enabled) => _echoToConsole = enabled;

        public static void Trace(string msg) => Write(AgentLogLevel.Trace, msg);
        public static void Debug(string msg) => Write(AgentLogLevel.Debug, msg);
        public static void Info(string msg) => Write(AgentLogLevel.Info, msg);
        public static void Warn(string msg) => Write(AgentLogLevel.Warn, msg);
        public static void Error(string msg) => Write(AgentLogLevel.Error, msg);

        private static void Write(AgentLogLevel level, string msg)
        {
            if (level < MinLevel)
            {
                return;
            }

            Raw($"[{level.ToString().ToUpperInvariant(),-5}] {msg}");
        }

        /// <summary>Writes a line verbatim (aside from the timestamp prefix), with no level prefix.</summary>
        public static void Raw(string line)
        {
            string stamped = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";

            lock (_lock)
            {
                if (_echoToConsole)
                {
                    Console.WriteLine(stamped);
                }

                // Background Python scripts also append their own log lines to this same file
                // (see uo.log() in cli/uo/script.py) so they're tailable the same way client
                // narration is. This stream's own position is cached in-process and does not
                // notice an external append growing the file, so without seeking to the true end
                // first, this write would land at the stale position and silently clobber
                // whatever the other process just wrote - observed live before this fix.
                if (_file != null)
                {
                    _file.BaseStream.Seek(0, SeekOrigin.End);
                    _file.WriteLine(stamped);
                }
            }
        }

        public static void Close()
        {
            lock (_lock)
            {
                _file?.Dispose();
                _file = null;
            }
        }
    }
}
