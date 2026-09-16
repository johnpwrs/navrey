// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClassicUO.Game;

namespace ClassicUO.Agent
{
    /// <summary>
    /// An item's property list - weight, durability, damage, resists, the "special stats" - as
    /// plain lines.
    ///
    /// This is the AOS-era tooltip (the 0xD6 "mega cliloc" reply), which ClassicUO already keeps
    /// in <c>World.OPL</c> for the hover tooltip; we only read that cache. Two shard eras behave
    /// differently and both are supported by the same code:
    ///
    ///  - A modern shard (UO Alive) announces tooltips in its feature flags and pushes a revision
    ///    (0xDC) with every item, so the cache fills on its own and lines are available at once.
    ///  - A pre-AOS shard (Renaissance) never sends 0xDC and never answers 0xD6, so the cache stays
    ///    empty and every lookup yields nothing. That is the honest answer for that era: single-click
    ///    names are all the protocol carries, and <see cref="Describe.ItemName"/> covers those.
    ///
    /// Lines come back stripped of the tooltip markup the client adds for colouring.
    /// </summary>
    internal static class ItemProps
    {
        private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

        /// <summary>
        /// Asks the server for a serial's properties if none are cached. Game thread only.
        ///
        /// This is <c>OPL.Contains</c>'s side effect, given its own name: a command that wants an
        /// answer calls it and asks <see cref="Lines"/> again a little later. Kept out of
        /// <see cref="Lines"/> on purpose - the state snapshot calls that ten times a second for
        /// every backpack and equipped item, and while it went through Contains it re-queued a
        /// request for every item the server had not answered, every 100ms, for as long as the
        /// client ran. A snapshot reads; it must not send.
        /// </summary>
        public static void Request(World world, uint serial) => world?.OPL.Contains(serial);

        /// <summary>
        /// Property lines for a serial, name first, or an empty list when none are cached.
        /// Game thread only. Reads the cache and nothing else - see <see cref="Request"/>.
        /// </summary>
        public static List<string> Lines(World world, uint serial)
        {
            var lines = new List<string>();

            if (world == null || !world.OPL.TryGetNameAndData(serial, out string name, out string data))
            {
                return lines;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                lines.Add(Clean(name));
            }

            if (!string.IsNullOrEmpty(data))
            {
                foreach (string raw in data.Split('\n'))
                {
                    string line = Clean(raw);

                    if (line.Length > 0)
                    {
                        lines.Add(line);
                    }
                }
            }

            return lines;
        }

        /// <summary>True when the server has said it sends tooltips at all.</summary>
        public static bool Supported(World world) => world?.ClientFeatures.TooltipsEnabled == true;

        private static string Clean(string s) => Tags.Replace(s, string.Empty).Trim();
    }
}
