// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Context menus - the right-click menu a real client shows on a mobile or item.
    ///
    /// Worth having because a whole class of server interactions lives *only* here and has no
    /// double-click or speech equivalent. The one that forced the issue: UOAlive's new-player
    /// armor quest hands over a ticket only once the ticket is flagged a quest item, and the only
    /// way to flag it is the item's own "Toggle Quest Item" context entry. With no way to send
    /// that, the quest simply cannot be completed from the CLI.
    ///
    /// Two commands, deliberately split, because the server decides the entries:
    ///   contextmenu &lt;serial&gt;          asks for the menu and prints the entries
    ///   contextpick &lt;serial&gt; &lt;index&gt;  chooses one by the index the listing showed
    ///
    /// Never guess an index. Entry indices are assigned by the server per object and differ
    /// between an item, a mobile and the same item in another state, so the listing is not
    /// decoration - it is the only source of a correct index. This mirrors the standing rule for
    /// 'gumps'/'gumpresponse', and for the same reason: pressing an unread button does something,
    /// just not the thing you wanted.
    ///
    /// Reading the entries back is done off the gump's own controls rather than the packet. The
    /// client builds one HitBox per entry carrying the server's index in Tag, followed by the
    /// Label holding that entry's text, so walking the children recovers both halves without
    /// touching ClassicUO proper - the same "observe rather than instrument" stance Narrator takes.
    /// </summary>
    internal sealed partial class CommandEngine
    {
        /// <summary>How long to wait for the server to answer with a menu before giving up.</summary>
        private const int CONTEXT_MENU_WAIT_MS = 2000;

        private const int CONTEXT_MENU_POLL_MS = 100;

        private void RegisterContextMenus()
        {
            Register("contextmenu", "contextmenu <serial>",
                     "Ask for a mobile's or item's right-click context menu and list its entries",
                     ctx =>
            {
                uint serial = CommandContext.ParseSerial(ctx.Arg(0));

                if (serial == 0)
                {
                    ctx.Warn("usage: contextmenu <serial>   (a mobile or item serial, as 'inv'/'mobiles' print them)");

                    return;
                }

                // Any menu still on screen is from a previous request; drop it so the entries we
                // list cannot be last request's answer.
                ctx.Game(_ => ClosePopupMenus());

                // Deliberately the raw packet rather than GameActions' helper: that one is gated
                // behind the profile's HoldShiftForContext setting and would silently send nothing
                // when it is on, which from the CLI is indistinguishable from "no menu exists".
                ctx.Game(_ => NetClient.Socket.Send_RequestPopupMenu(serial));

                for (int waited = 0; waited < CONTEXT_MENU_WAIT_MS; waited += CONTEXT_MENU_POLL_MS)
                {
                    if (!ctx.Sleep(CONTEXT_MENU_POLL_MS))
                    {
                        return;
                    }

                    List<string> lines = ctx.Game(_ => DescribePopupMenus());

                    if (lines.Count > 0)
                    {
                        foreach (string line in lines)
                        {
                            ctx.Print(line);
                        }

                        ctx.Print($"  use 'contextpick 0x{serial:X8} <index>' to choose one");

                        return;
                    }
                }

                // Not an error: plenty of objects legitimately have no context menu, and the
                // server simply says nothing rather than sending an empty one.
                ctx.Print($"No context menu arrived for 0x{serial:X8} within {CONTEXT_MENU_WAIT_MS}ms " +
                          "- it may have none, or be out of range");
            }, "context", "cm");

            Register("contextpick", "contextpick <serial> <index>",
                     "Choose an entry from a context menu, by the index 'contextmenu' listed",
                     ctx =>
            {
                uint serial = CommandContext.ParseSerial(ctx.Arg(0));
                string raw = ctx.Arg(1);

                if (serial == 0 || !ushort.TryParse(raw, out ushort index))
                {
                    ctx.Warn("usage: contextpick <serial> <index>   (run 'contextmenu <serial>' first; " +
                             "indices are per-object and must be read, never guessed)");

                    return;
                }

                ctx.Game(_ =>
                {
                    GameActions.ResponsePopupMenu(serial, index);

                    // The server closes its menu on selection; the client's copy is ours to tidy.
                    ClosePopupMenus();
                });

                ctx.Print($"Picked context entry {index} on 0x{serial:X8}");
            }, "contextselect", "cp");
        }

        /// <summary>Game thread only. One line per entry of every open context menu.</summary>
        private static List<string> DescribePopupMenus()
        {
            var lines = new List<string>();

            foreach (var gump in UIManager.Gumps.OfType<PopupMenuGump>().Where(g => !g.IsDisposed))
            {
                lines.Add("[CONTEXT] menu:");

                foreach (var (index, text) in ReadEntries(gump))
                {
                    lines.Add($"  [entry {index}] {text}");
                }
            }

            return lines;
        }

        /// <summary>
        /// Recovers (index, text) per entry from the gump's controls.
        ///
        /// The client adds a HitBox whose Tag is the server's entry index, then the Label with that
        /// entry's text, in that order, once per entry - so pairing each tagged HitBox with the
        /// next Label that follows it reconstructs the menu. An entry whose Label is missing still
        /// reports its index, because an index with no caption is far more useful than dropping the
        /// entry entirely.
        /// </summary>
        private static List<(ushort Index, string Text)> ReadEntries(PopupMenuGump gump)
        {
            var entries = new List<(ushort, string)>();
            bool pending = false;
            ushort pendingIndex = 0;

            foreach (var child in gump.Children)
            {
                if (child is HitBox box && box.Tag is ushort index)
                {
                    if (pending)
                    {
                        entries.Add((pendingIndex, "(no caption)"));
                    }

                    pending = true;
                    pendingIndex = index;

                    continue;
                }

                if (pending && child is Label label)
                {
                    entries.Add((pendingIndex, string.IsNullOrWhiteSpace(label.Text) ? "(no caption)" : label.Text));
                    pending = false;
                }
            }

            if (pending)
            {
                entries.Add((pendingIndex, "(no caption)"));
            }

            return entries;
        }

        /// <summary>Game thread only.</summary>
        private static void ClosePopupMenus()
        {
            foreach (var gump in UIManager.Gumps.OfType<PopupMenuGump>().Where(g => !g.IsDisposed).ToList())
            {
                gump.Dispose();
            }
        }
    }
}
