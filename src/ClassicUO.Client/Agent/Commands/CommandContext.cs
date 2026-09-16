// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Globalization;
using System.Threading;
using ClassicUO.Game;

namespace ClassicUO.Agent
{
    /// <summary>
    /// What a command handler is given: its arguments, the dispatcher it must use to reach the
    /// game, an output sink, and a cancellation token so long-running commands answer `stop`.
    /// </summary>
    internal sealed class CommandContext
    {
        public CommandContext(string raw, string[] args, GameDispatcher dispatcher, CancellationToken cancel)
        {
            Raw = raw;
            Args = args;
            Dispatcher = dispatcher;
            Cancel = cancel;
        }

        /// <summary>The full command line as typed.</summary>
        public string Raw { get; }

        /// <summary>Whitespace-split tokens; Args[0] is the command name.</summary>
        public string[] Args { get; }

        public GameDispatcher Dispatcher { get; }

        public CancellationToken Cancel { get; }

        public int ArgCount => Args.Length - 1;

        /// <summary>
        /// Set when a handler completed but did not achieve what was asked - a goto that could not
        /// reach its target, for instance. Distinct from an exception: the command ran fine, the
        /// outcome was just negative, and callers (navrey's exit code, agent tools) need to see that.
        /// </summary>
        public string Failure { get; private set; }

        public void Fail(string reason)
        {
            Failure ??= reason;

            Warn(reason);
        }

        /// <summary>
        /// Clears a recorded failure. Used by multi-attempt commands (gonear widening its radius)
        /// where an early attempt failing is expected and not the command's outcome.
        /// </summary>
        public void ClearFailure() => Failure = null;

        public string Arg(int index) => index + 1 < Args.Length ? Args[index + 1] : null;

        public void Print(string line) => Output.Raw(line);

        public void Info(string line) => Output.Info(line);

        public void Warn(string line) => Output.Warn(line);

        /// <summary>
        /// Runs <paramref name="func"/> on the game thread and returns its result.
        /// <paramref name="label"/> names it in the `[PERF]` line if it turns out to be the slow
        /// thing in a frame; unlabelled work shows as the command's name.
        /// </summary>
        public T Game<T>(Func<World, T> func, string label = null) =>
            Dispatcher.Invoke(() => func(ClassicUO.Client.Game.UO.World), label ?? Args[0]);

        /// <summary>Runs <paramref name="action"/> on the game thread, waiting for it to finish.</summary>
        public void Game(Action<World> action, string label = null) =>
            Dispatcher.Invoke(() => action(ClassicUO.Client.Game.UO.World), label ?? Args[0]);

        /// <summary>True once the player is in the world; most commands need this.</summary>
        public bool RequireInGame()
        {
            if (Game(w => w.InGame && w.Player != null))
            {
                return true;
            }

            Warn("not in game yet");

            return false;
        }

        /// <summary>Parses a hex serial, tolerating a 0x prefix. Returns 0 on failure.</summary>
        public static uint ParseSerial(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(2);
            }

            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint serial)
                ? serial
                : 0;
        }

        /// <summary>Sleeps in short slices so cancellation is responsive.</summary>
        public bool Sleep(int milliseconds)
        {
            const int SLICE = 50;

            for (int waited = 0; waited < milliseconds; waited += SLICE)
            {
                if (Cancel.IsCancellationRequested)
                {
                    return false;
                }

                Thread.Sleep(Math.Min(SLICE, milliseconds - waited));
            }

            return !Cancel.IsCancellationRequested;
        }
    }
}
