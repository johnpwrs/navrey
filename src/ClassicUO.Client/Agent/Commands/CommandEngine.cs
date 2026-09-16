// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassicUO.Agent
{
    /// <summary>
    /// The CLI command table. One entry per command; handlers run on the worker thread and reach
    /// the game only through <see cref="CommandContext.Game{T}"/>.
    /// </summary>
    internal sealed partial class CommandEngine
    {
        private sealed class Entry
        {
            public string Name;
            public string[] Aliases = Array.Empty<string>();
            public string Usage;
            public string Help;
            public Action<CommandContext> Run;
        }

        private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Entry> _all = new();

        public CommandEngine()
        {
            RegisterCore();
            RegisterPoi();
            RegisterActions();
            RegisterExtras();
        }

        private void Register(string name, string usage, string help, Action<CommandContext> run, params string[] aliases)
        {
            var entry = new Entry
            {
                Name = name,
                Aliases = aliases ?? Array.Empty<string>(),
                Usage = usage,
                Help = help,
                Run = run
            };

            _all.Add(entry);
            _byName[name] = entry;

            foreach (var alias in entry.Aliases)
            {
                _byName[alias] = entry;
            }
        }

        public bool TryExecute(CommandContext ctx, out string error)
        {
            error = null;

            if (ctx.Args.Length == 0)
            {
                return true;
            }

            if (!_byName.TryGetValue(ctx.Args[0], out var entry))
            {
                error = $"unknown command '{ctx.Args[0]}' - try 'help'";

                return false;
            }

            try
            {
                entry.Run(ctx);

                error = ctx.Failure;

                return error == null;
            }
            catch (OperationCanceledException)
            {
                error = "cancelled";

                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;

                return false;
            }
        }

        private void Help(CommandContext ctx)
        {
            ctx.Print("Commands:");

            foreach (var entry in _all.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                string aliases = entry.Aliases.Length > 0 ? $"  (aka {string.Join(", ", entry.Aliases)})" : string.Empty;

                ctx.Print($"  {entry.Usage,-42} {entry.Help}{aliases}");
            }
        }

    }
}
