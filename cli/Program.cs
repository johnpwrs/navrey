// SPDX-License-Identifier: BSD-2-Clause
//
// navrey - the command-line front end for the ClassicUO agent daemon.
//
// This process holds no game state and opens no connection of its own. The game client (started
// with -agent, or -headless for no window) owns the single socket and the single world; this tool
// only appends command lines to the command file and streams the log file back. That is what lets
// the CLI and the game window drive the same character with no possibility of divergence.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Navrey.Cli
{
    internal static class Program
    {
        private const string DEFAULT_COMMAND_FILE = "/tmp/cuocmd";
        private const string DEFAULT_LOG_FILE = "/tmp/cuolog";

        private static string _commandFile = DEFAULT_COMMAND_FILE;
        private static string _logFile = DEFAULT_LOG_FILE;
        private static int _timeoutSeconds = 30;

        private static int Main(string[] args)
        {
            var rest = ParseOptions(args);

            if (!File.Exists(_logFile))
            {
                Console.Error.WriteLine(
                    $"No agent log at {_logFile}. Start the client with -agent (or -headless) first.");

                return 2;
            }

            return rest.Count > 0
                ? RunOnce(string.Join(" ", rest))
                : RunInteractive();
        }

        private static List<string> ParseOptions(string[] args)
        {
            var rest = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--cmdfile" when i + 1 < args.Length:
                        _commandFile = args[++i];
                        break;

                    case "--logfile" when i + 1 < args.Length:
                        _logFile = args[++i];
                        break;

                    case "--timeout" when i + 1 < args.Length:
                        int.TryParse(args[++i], out _timeoutSeconds);
                        break;

                    case "-h":
                    case "--help":
                        PrintUsage();
                        Environment.Exit(0);
                        break;

                    default:
                        rest.Add(args[i]);
                        break;
                }
            }

            return rest;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  navrey                       interactive prompt");
            Console.WriteLine("  navrey <command> [args...]   run one command and print its output");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine($"  --cmdfile <path>   command file (default {DEFAULT_COMMAND_FILE})");
            Console.WriteLine($"  --logfile <path>   log file     (default {DEFAULT_LOG_FILE})");
            Console.WriteLine("  --timeout <secs>   one-shot wait for completion (default 30)");
        }

        /// <summary>
        /// Sends one command and streams output until its handler reports completion.
        ///
        /// Completion means the [CMD-END] marker for this exact command line. That marker says the
        /// handler returned - it does not promise the server has finished reacting, because packets
        /// arrive asynchronously and cannot be attributed to a command. Any packet narration that
        /// happens to arrive in the meantime is printed too, which is usually what you want to see.
        /// </summary>
        private static int RunOnce(string command)
        {
            var tail = new LogTail(_logFile);

            Send(command);

            string endMarker = $"[CMD-END] {command} ";
            DateTime deadline = DateTime.UtcNow.AddSeconds(_timeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                foreach (string line in tail.ReadNew())
                {
                    // Lines carry a "[HH:mm:ss.fff] " timestamp prefix ahead of the actual
                    // content, so match markers by position-within-line rather than at index 0.
                    int markerIndex = line.IndexOf(endMarker, StringComparison.Ordinal);

                    if (markerIndex >= 0)
                    {
                        string outcome = line.Substring(markerIndex + endMarker.Length);

                        return outcome.StartsWith("ok", StringComparison.Ordinal) ? 0 : 1;
                    }

                    // Skip the echo of our own command; the caller already knows what they asked.
                    if (!line.EndsWith($"[CMD] {command}", StringComparison.Ordinal))
                    {
                        Console.WriteLine(line);
                    }
                }

                Thread.Sleep(60);
            }

            Console.Error.WriteLine($"timed out after {_timeoutSeconds}s waiting for '{command}'");

            return 3;
        }

        private const string PROMPT = "navrey> ";

        /// <summary>
        /// The interactive prompt. Runs happily alongside the game window - it is a separate
        /// process talking to the same client, so you can type here and play there at once.
        ///
        /// The prompt has to share the terminal with a live stream of game events, which arrive
        /// whenever the server feels like it, including mid-keystroke. So input is read a key at a
        /// time rather than with ReadLine: that way the printer thread can rub out the prompt,
        /// print what arrived, and redraw the prompt with whatever you had half-typed still intact.
        /// </summary>
        private static int RunInteractive()
        {
            var tail = new LogTail(_logFile);
            bool running = true;

            // Guards the terminal: only one of the printer thread and the input loop may be
            // drawing at a time, or the redraw races with the echo of a keystroke.
            var console = new object();
            var editor = new LineEditor();

            bool interactive = !Console.IsInputRedirected;

            var printer = new Thread(() =>
            {
                while (running)
                {
                    var lines = tail.ReadNew();

                    if (lines.Count > 0)
                    {
                        lock (console)
                        {
                            if (interactive)
                            {
                                ErasePrompt(editor);
                            }

                            foreach (string line in lines)
                            {
                                Console.WriteLine(line);
                            }

                            if (interactive)
                            {
                                DrawPrompt(editor);
                            }
                        }
                    }

                    Thread.Sleep(60);
                }
            })
            {
                IsBackground = true,
                Name = "navrey-tail"
            };

            printer.Start();

            Console.WriteLine($"navrey - commands to {_commandFile}, output from {_logFile}");
            Console.WriteLine("Type 'help' for commands, 'exit' to leave (the game client keeps running).");
            Console.WriteLine();

            if (!interactive)
            {
                // Piped input: no line editing to do, just read and send.
                string? piped;

                while ((piped = Console.ReadLine()) != null)
                {
                    piped = piped.Trim();

                    if (piped.Length == 0)
                    {
                        continue;
                    }

                    if (piped is "exit" or "quit")
                    {
                        break;
                    }

                    Send(piped);
                    Thread.Sleep(400);
                }

                running = false;

                return 0;
            }

            lock (console)
            {
                DrawPrompt(editor);
            }

            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                lock (console)
                {
                    if (key.Key == ConsoleKey.Enter)
                    {
                        string line = editor.Take();

                        Console.WriteLine();

                        if (line.Length == 0)
                        {
                            DrawPrompt(editor);

                            continue;
                        }

                        if (line is "exit" or "quit")
                        {
                            break;
                        }

                        Send(line);
                        DrawPrompt(editor);

                        continue;
                    }

                    if (!editor.Handle(key))
                    {
                        // Ctrl+C / Ctrl+D at an empty prompt.
                        Console.WriteLine();

                        break;
                    }

                    RedrawInput(editor);
                }
            }

            running = false;

            return 0;
        }

        private static void DrawPrompt(LineEditor editor)
        {
            Console.Write(PROMPT + editor.Text);
        }

        private static void ErasePrompt(LineEditor editor)
        {
            int width = PROMPT.Length + editor.Text.Length;

            Console.Write('\r');
            Console.Write(new string(' ', Math.Max(width, 1)));
            Console.Write('\r');
        }

        private static void RedrawInput(LineEditor editor)
        {
            Console.Write('\r');
            Console.Write(PROMPT + editor.Text);

            // Rub out the tail of a line that just got shorter (backspace).
            if (editor.LastWidth > editor.Text.Length)
            {
                Console.Write(new string(' ', editor.LastWidth - editor.Text.Length));
                Console.Write('\r');
                Console.Write(PROMPT + editor.Text);
            }

            editor.Commit();
        }

        /// <summary>
        /// A deliberately small line editor: printable characters, backspace, and command history
        /// on the up/down arrows. Enough to type comfortably while output streams past.
        /// </summary>
        private sealed class LineEditor
        {
            private readonly List<string> _history = new();
            private string _text = string.Empty;
            private int _historyIndex = -1;

            public string Text => _text;

            public int LastWidth { get; private set; }

            public void Commit() => LastWidth = _text.Length;

            public string Take()
            {
                string line = _text.Trim();

                _text = string.Empty;
                LastWidth = 0;
                _historyIndex = -1;

                if (line.Length > 0 && (_history.Count == 0 || _history[^1] != line))
                {
                    _history.Add(line);
                }

                return line;
            }

            /// <summary>Returns false when the user asked to quit.</summary>
            public bool Handle(ConsoleKeyInfo key)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Backspace:
                        if (_text.Length > 0)
                        {
                            _text = _text.Substring(0, _text.Length - 1);
                        }

                        return true;

                    case ConsoleKey.Escape:
                        _text = string.Empty;

                        return true;

                    case ConsoleKey.UpArrow:
                        if (_history.Count > 0)
                        {
                            _historyIndex = _historyIndex < 0
                                ? _history.Count - 1
                                : Math.Max(0, _historyIndex - 1);

                            _text = _history[_historyIndex];
                        }

                        return true;

                    case ConsoleKey.DownArrow:
                        if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
                        {
                            _historyIndex++;
                            _text = _history[_historyIndex];
                        }
                        else
                        {
                            _historyIndex = -1;
                            _text = string.Empty;
                        }

                        return true;
                }

                if (key.Modifiers.HasFlag(ConsoleModifiers.Control) &&
                    key.Key is ConsoleKey.C or ConsoleKey.D)
                {
                    return false;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    _text += key.KeyChar;
                }

                return true;
            }
        }

        private static void Send(string command)
        {
            using var stream = new FileStream(_commandFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);

            writer.WriteLine(command);
        }

        /// <summary>
        /// Follows the log file from wherever it currently ends, tolerating truncation (which is
        /// what a client restart looks like from here).
        /// </summary>
        private sealed class LogTail
        {
            private readonly string _path;
            private long _position;

            public LogTail(string path)
            {
                _path = path;
                _position = new FileInfo(path).Length;
            }

            public List<string> ReadNew()
            {
                var lines = new List<string>();

                try
                {
                    var info = new FileInfo(_path);

                    if (!info.Exists)
                    {
                        _position = 0;

                        return lines;
                    }

                    if (info.Length < _position)
                    {
                        _position = 0;
                    }

                    if (info.Length == _position)
                    {
                        return lines;
                    }

                    using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(_position, SeekOrigin.Begin);

                    using var reader = new StreamReader(fs, Encoding.UTF8);

                    string? line;

                    while ((line = reader.ReadLine()) != null)
                    {
                        // Two clients writing one log file leaves NUL padding behind (one truncates
                        // while the other keeps writing at its old offset). Strip it rather than
                        // rendering a screenful of control characters.
                        if (line.IndexOf('\0') >= 0)
                        {
                            line = line.Replace("\0", string.Empty);

                            if (line.Length == 0)
                            {
                                continue;
                            }
                        }

                        lines.Add(line);
                    }

                    _position = fs.Position;
                }
                catch (IOException)
                {
                    // The client is mid-write; try again on the next poll.
                }

                return lines;
            }
        }
    }
}
