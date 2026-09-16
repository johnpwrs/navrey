// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;
using ClassicUO.Network;
using System.Text.RegularExpressions;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Turns incoming server traffic into the CLI's running commentary - the stream of lines you
    /// watch while playing, and the lines the uo-* skills grep for.
    ///
    /// Two sources, deliberately:
    ///
    ///  - Already-parsed managers, where they exist. Speech comes from
    ///    World.MessageManager.MessageReceived rather than from packets 0x1C/0xAE, because the
    ///    manager has already resolved the speaker, hue and message type for us.
    ///
    ///  - Wrapped packet handlers for everything else. PacketHandlers.Handler.Get/Add lets us
    ///    capture the installed delegate and reinstall one that calls through and then narrates, so
    ///    the 7,000-line PacketHandlers.cs needs no edits and plugins still see every packet. The
    ///    wrapper never consumes the reader it narrates from - it takes its own copy first.
    ///
    /// These lines are NOT attributed to any command. A packet may be a consequence of something
    /// the CLI just did, or of the player clicking in the game window, or of nothing either of them
    /// did; pretending otherwise would be a lie, so narration is emitted unbracketed as it arrives.
    /// </summary>
    internal static class Narrator
    {
        private static bool _installed;
        private static World _world;
        private static bool _announcedWorld;

        public static void Install(World world)
        {
            if (_installed || world == null)
            {
                return;
            }

            _installed = true;
            _world = world;

            world.MessageManager.MessageReceived += OnMessage;

            WrapHandlers();
        }

        public static void Uninstall()
        {
            if (!_installed)
            {
                return;
            }

            if (_world != null)
            {
                _world.MessageManager.MessageReceived -= OnMessage;
            }

            _installed = false;
            _world = null;
        }

        /// <summary>Called once per frame so world-entry can be announced when it happens.</summary>
        public static void Update(World world)
        {
            if (world == null)
            {
                return;
            }

            if (!_announcedWorld && world.InGame && world.Player != null)
            {
                _announcedWorld = true;

                // Keep this exact line - the uo-* skills wait on it to know login finished.
                Output.Raw("*** Entered the world! ***");
                Output.Raw($"Playing {world.Player.Name} at " +
                           $"({world.Player.X}, {world.Player.Y}, {world.Player.Z}) on map {world.MapIndex}");
            }
            else if (_announcedWorld && !world.InGame)
            {
                _announcedWorld = false;
            }
        }

        private static void OnMessage(object sender, MessageEventArgs e)
        {
            string speaker = string.IsNullOrEmpty(e.Name) ? "?" : e.Name;
            string text = e.Text ?? string.Empty;

            if (text.Length == 0)
            {
                return;
            }

            switch (e.Type)
            {
                case MessageType.System:
                    Output.Raw($"[SYSTEM] {text}");
                    break;

                case MessageType.Yell:
                    Output.Raw($"[YELL] {speaker}: {text}");
                    break;

                case MessageType.Whisper:
                    Output.Raw($"[whisper] {speaker}: {text}");
                    break;

                case MessageType.Emote:
                    Output.Raw($"* {speaker} {text}");
                    break;

                case MessageType.Label:
                    Output.Raw($"[LABEL] {speaker}: {text}");
                    break;

                case MessageType.Regular:
                    // The type byte is not enough on its own here. Shards send a lot of their own
                    // announcements as Regular rather than System - MOTD, welcome text, "you are
                    // frozen and cannot move" - with the speaker left empty or literally "System".
                    // Labelling those [SAY] would make the label useless for its one job, which is
                    // "somebody said this". Named speaker or it is not speech.
                    if (string.IsNullOrEmpty(e.Name) || e.Name == "System")
                    {
                        Output.Raw($"[SYSTEM] {text}");
                    }
                    else
                    {
                        Output.Raw($"[SAY] {speaker}: {text}");
                    }

                    break;

                case MessageType.Guild:
                    Output.Raw($"[GUILD] {speaker}: {text}");
                    break;

                case MessageType.Alliance:
                    Output.Raw($"[ALLIANCE] {speaker}: {text}");
                    break;

                case MessageType.Party:
                    Output.Raw($"[PARTY] {speaker}: {text}");
                    break;

                case MessageType.Spell:
                    Output.Raw($"[SPELL] {speaker}: {text}");
                    break;

                default:
                    // Everything carries its type now, including the ones this switch does not
                    // name. Ordinary speech used to fall through here bare, as `<speaker>: <text>`
                    // - a shape indistinguishable from `Swing: ...`, `Damage: ...`, an object
                    // label, or another script's own log line, so every reader had to re-derive
                    // "was this speech?" by exclusion. The packet knew all along: MessageType is
                    // a byte on the incoming message. Printing the raw number for the remainder
                    // means a new message kind shows up as `[MSG:13]` rather than silently
                    // impersonating conversation.
                    Output.Raw($"[MSG:{(byte)e.Type}] {speaker}: {text}");
                    break;
            }
        }

        private static void WrapHandlers()
        {
            // 0xB0 / 0xDD - a server gump opened (plain and compressed layouts). Previously the
            // only way to see one was to poll `gumps`, which just names the gump's type - useless
            // for anything the player actually has to read and answer, like a moongate destination
            // picker. Both packets start the same way (sender serial, then gump serial), and by
            // the time this narration runs the real handler has already built the full control
            // tree (Wrap calls it first), so there is no need to parse the layout ourselves - just
            // find the Gump it produced and read back what it already parsed.
            void NarrateGump(World world, ReadOnlySpan<byte> data)
            {
                var p = new StackDataReader(data);
                p.Seek(3);

                uint sender = p.ReadUInt32BE();
                uint gumpId = p.ReadUInt32BE();

                Gump gump = null;

                foreach (var g in UIManager.Gumps)
                {
                    if (!g.IsDisposed && g.LocalSerial == sender && g.ServerSerial == gumpId)
                    {
                        gump = g;
                    }
                }

                if (gump == null)
                {
                    return;
                }

                Output.Raw($"[GUMP] opened local 0x{sender:X8} server 0x{gumpId:X8} {gump.GetType().Name} - use 'gumpresponse <button>'");

                foreach (string line in DumpGumpText(gump))
                {
                    Output.Raw($"[GUMP]   {line}");
                }
            }

            Wrap(0xB0, NarrateGump);
            Wrap(0xDD, NarrateGump);

            // 0x6F - secure trade: window opened/closed, gold offered, accept status changed.
            // Surfaced here for the same reason gumps are - a trade offer from another player is
            // otherwise invisible to the CLI until something thinks to poll 'trades' for it.
            Wrap(0x6F, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(3);

                byte type = p.ReadUInt8();
                uint serial = p.ReadUInt32BE();

                // A close (type 1) has already been disposed by the real handler by the time this
                // runs, so there is nothing left to look up - report the bare fact instead.
                if (type == 1)
                {
                    Output.Raw($"[TRADE] closed (0x{serial:X8})");

                    return;
                }

                var trade = UIManager.GetTradingGump(serial);

                if (trade == null)
                {
                    return;
                }

                string status = $"my box 0x{trade.ID1:X8} (gold {trade.Gold}, plat {trade.Platinum}, " +
                    $"{(trade.ImAccepting ? "accepted" : "not accepted")}) - their box 0x{trade.ID2:X8} " +
                    $"(gold {trade.HisGold}, plat {trade.HisPlatinum}, " +
                    $"{(trade.HeIsAccepting ? "accepted" : "not accepted")})";

                switch (type)
                {
                    case 0:
                        Output.Raw($"[TRADE] {trade.Name} opened a trade (local 0x{trade.LocalSerial:X8}) - " +
                                    "use 'trades' to inspect, 'accepttrade' to accept");
                        break;

                    case 2:
                        Output.Raw($"[TRADE] accept status changed - {status}");
                        break;

                    case 3:
                    case 4:
                        Output.Raw($"[TRADE] offer changed - {status}");
                        break;
                }
            });

            // 0x24 - a container gump was opened
            // 0x93 / 0xD4 - a book opened (old fixed-size and new variable-size headers). The
            // gump draws pages the client fetches one at a time as they are turned, so a reader
            // outside the window would only ever see page 1. Ask for every page here instead;
            // they arrive as 0x66 packets and are narrated below. Reading a book is the only way
            // to learn what a quest wants, and the book gump is client-side - `gumps` never lists it.
            void NarrateBook(World world, ReadOnlySpan<byte> data)
            {
                bool oldPacket = data[0] == 0x93;
                var p = new StackDataReader(data);
                p.Seek(oldPacket ? 1 : 3);

                uint serial = p.ReadUInt32BE();
                p.Skip(2);   // editable flags
                ushort pages = p.ReadUInt16BE();
                string title = oldPacket ? p.ReadUTF8(60, true) : p.ReadUTF8(p.ReadUInt16BE(), true);
                string author = oldPacket ? p.ReadUTF8(30, true) : p.ReadUTF8(p.ReadUInt16BE(), true);

                Output.Raw($"[BOOK] 0x{serial:X8} \"{title.Trim()}\" by {author.Trim()} - {pages} page(s)");

                for (ushort page = 1; page <= pages; page++)
                {
                    NetClient.Socket.Send_BookPageDataRequest(serial, page);
                }
            }

            Wrap(0x93, NarrateBook);
            Wrap(0xD4, NarrateBook);

            // 0x66 - book page contents, any number of pages per packet.
            Wrap(0x66, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(3);

                uint serial = p.ReadUInt32BE();
                ushort pageCnt = p.ReadUInt16BE();

                for (int i = 0; i < pageCnt; i++)
                {
                    ushort pageNum = p.ReadUInt16BE();
                    ushort lineCnt = p.ReadUInt16BE();
                    var lines = new List<string>(lineCnt);

                    for (int line = 0; line < lineCnt; line++)
                    {
                        lines.Add(ModernBookGump.IsNewBook ? p.ReadUTF8(true) : p.ReadASCII());
                    }

                    while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
                    {
                        lines.RemoveAt(lines.Count - 1);
                    }

                    Output.Raw($"[BOOK] 0x{serial:X8} page {pageNum}:" + (lines.Count == 0 ? $" (blank; lines={lineCnt} packet={data.Length}b pages={pageCnt})" : string.Empty));

                    foreach (string text in lines)
                    {
                        Output.Raw($"[BOOK]   {text}");
                    }
                }
            });

            // 0x9A / 0xC2 - the server is asking a question and waiting for typed text (a rune's
            // description, a pet's name). Nothing else says one is open, and `say` cannot answer
            // it; `prompt <text>` can.
            void NarratePrompt(World world, ReadOnlySpan<byte> data)
            {
                Output.Raw("[PROMPT] the server is waiting for text - answer with 'prompt <text>' or 'promptcancel'");
            }

            Wrap(0x9A, NarratePrompt);
            Wrap(0xC2, NarratePrompt);

            Wrap(0x24, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(1);

                uint serial = p.ReadUInt32BE();
                ushort graphic = p.ReadUInt16BE();

                Output.Raw($"[CONTAINER] Opened 0x{serial:X8} (gump 0x{graphic:X4})");
            });

            // 0x25 - a single item was added to a container
            Wrap(0x25, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(1);

                uint serial = p.ReadUInt32BE();
                ushort graphic = p.ReadUInt16BE();
                p.ReadUInt8();
                ushort amount = Math.Max((ushort)1, p.ReadUInt16BE());
                p.ReadUInt16BE();
                p.ReadUInt16BE();

                Output.Raw($"[CONTAINER] +{amount} 0x{graphic:X4} (0x{serial:X8})");
            });

            // 0x3C - bulk container contents
            Wrap(0x3C, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(3);

                ushort count = p.ReadUInt16BE();

                Output.Raw($"[CONTAINER] Contents: {count} item(s)");
            });

            // 0x74 - vendor buy list. Recorded as well as announced: the client keeps stock only
            // inside the shop gump's private controls, so this is the CLI's only view of it.
            //
            // The packet itself carries only a container serial plus, per item, a price and a
            // (possibly empty/cliloc) name - the serial/graphic/amount of each item come from the
            // container contents the server already sent earlier and the real handler (which Wrap
            // calls before this narration runs) has already stamped Price/Name onto those tracked
            // items. So rather than re-deriving the wire layout here, just read the container
            // serial back out and walk what the real handler already populated.
            Wrap(0x74, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(3);

                uint containerSerial = p.ReadUInt32BE();
                Item container = world.Items.Get(containerSerial);

                var entries = new List<Capabilities.VendorStock.Entry>();
                uint vendor = container?.Container ?? 0;

                for (LinkedObject o = container?.Items; o != null; o = o.Next)
                {
                    Item it = (Item)o;

                    // Livestock is sold as *mobiles*, not items: an animal trainer's stock comes
                    // back with serials in the mobile range and a body graphic where an item
                    // graphic would be. Naming those through the item tiledata is what turned
                    // Creighton's horses into "Stone Arch" and "Marble Arch" - so resolve a
                    // mobile-serial entry through the body-graphic table instead.
                    string name = it.Name;
                    if (SerialHelper.IsMobile(it.Serial))
                    {
                        name = Capabilities.MobNames.Get(it.Graphic);
                    }

                    entries.Add(new Capabilities.VendorStock.Entry(
                        it.Serial, it.Graphic, it.Amount, (ushort)it.Price, name));
                }

                Capabilities.VendorStock.Replace(vendor, entries);

                Output.Raw($"[SHOP] Buy list from 0x{vendor:X8}: {entries.Count} entries - use 'shop' to list");
            });

            // 0x9E - vendor sell list: what this vendor will buy *from us*. Unlike 0x74 the wire
            // format carries everything inline (serial, graphic, hue, amount, price, name) rather
            // than leaning on container contents the server sent earlier, so it is parsed directly.
            //
            // Without this the CLI had no view of selling at all: the real handler builds a
            // ShopGump, which is fine for the window but private to the UI, so a headless caller
            // saw only silence - the vendor stops saying "you have nothing I would be interested
            // in" and nothing else happens. Observed live before this existed.
            Wrap(0x9E, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(3);

                uint vendor = p.ReadUInt32BE();
                ushort count = p.ReadUInt16BE();

                var entries = new List<Capabilities.VendorStock.Entry>();

                for (int i = 0; i < count; i++)
                {
                    uint serial = p.ReadUInt32BE();
                    ushort graphic = p.ReadUInt16BE();
                    p.ReadUInt16BE();                       // hue - not useful to the CLI
                    ushort amount = p.ReadUInt16BE();
                    ushort price = p.ReadUInt16BE();
                    string name = p.ReadASCII(p.ReadUInt16BE());

                    if (int.TryParse(name, out int cliloc))
                    {
                        name = Client.Game.UO.FileManager.Clilocs.GetString(cliloc);
                    }

                    entries.Add(new Capabilities.VendorStock.Entry(
                        serial, graphic, amount, price, name));
                }

                Capabilities.VendorStock.ReplaceSell(vendor, entries);

                Output.Raw($"[SHOP] Sell list from 0x{vendor:X8}: {entries.Count} entries - use 'selllist' to list");
            });

            // 0x2C - you died
            Wrap(0x2C, (World world, ReadOnlySpan<byte> data) =>
            {
                Output.Raw("*** You have died - you are a ghost ***");
            });

            // 0x72 - war mode changed
            Wrap(0x72, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(1);

                Output.Raw($"War mode: {(p.ReadBool() ? "ON" : "off")}");
            });

            // 0x6C - the server is asking for a target
            Wrap(0x6C, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(1);

                byte targetType = p.ReadUInt8();
                uint cursorId = p.ReadUInt32BE();

                Output.Raw($"[TARGET] Server wants a target (cursor 0x{cursorId:X8}, " +
                           $"{(targetType == 0 ? "object" : "location")}) - use 'target <serial|self>'");
            });

            // 0xAA - attack accepted / cleared
            Wrap(0xAA, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(1);

                uint serial = p.ReadUInt32BE();

                Output.Raw(serial == 0
                    ? "Attack cleared"
                    : $"Attack confirmed -> 0x{serial:X8} ({NameOf(world, serial)})");
            });

            // 0x2F - a swing happened. Layout is: cmd, 0x00, attacker, defender.
            //
            // The second byte is a constant in the protocol, not a hit flag. Reading it as one is
            // what this used to do, and since it is always zero every swing narrated as "MISS" -
            // including four in a row that dealt damage and took a target from 25 HP to 3. That
            // produced advice built on a fiction ("an unbroken MISS run means no arrows or no line
            // of sight") for a run that is simply what every fight looks like.
            //
            // The packet does not carry an outcome at all. Whether a swing connected is in the
            // 0x0B damage packet below, which is why nothing here guesses.
            Wrap(0x2F, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(2);

                uint attacker = p.ReadUInt32BE();
                uint defender = p.ReadUInt32BE();

                Output.Raw($"Swing: {NameOf(world, attacker)} -> {NameOf(world, defender)}");
            });

            // 0x21 - the server refused a step and resynced us
            Wrap(0x21, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(1);

                byte seq = p.ReadUInt8();
                ushort x = p.ReadUInt16BE();
                ushort y = p.ReadUInt16BE();

                Output.Raw($"Walk denied (seq {seq}) - resync to ({x}, {y})");
            });

            // 0x0B - damage taken or dealt
            Wrap(0x0B, (World world, ReadOnlySpan<byte> data) =>
            {
                var p = new StackDataReader(data);
                p.Seek(1);

                uint serial = p.ReadUInt32BE();
                ushort amount = p.ReadUInt16BE();

                Output.Raw($"Damage: {NameOf(world, serial)} takes {amount}");
            });
        }

        private static string NameOf(World world, uint serial)
        {
            if (world == null || serial == 0)
            {
                return "?";
            }

            Entity entity = world.Get(serial);

            if (entity == null)
            {
                return $"0x{serial:X8}";
            }

            if (!string.IsNullOrEmpty(entity.Name))
            {
                return entity.Name;
            }

            if (entity is Mobile mobile)
            {
                string known = Capabilities.MobNames.Get(mobile.Graphic);

                if (!string.IsNullOrEmpty(known))
                {
                    return known;
                }
            }

            return $"0x{serial:X8}";
        }

        /// <summary>
        /// Walks a gump's control tree for the parts a CLI session actually needs to answer it:
        /// readable text (labels, HTML blocks) and every button's id. Anything else server gumps
        /// carry - backgrounds, checkboxes, radio groups, text entry fields - isn't rendered here;
        /// 'gumpresponse' only supports plain button replies today.
        /// </summary>
        internal static IEnumerable<string> DumpGumpText(Control control)
        {
            string last = null;

            foreach (string line in WalkGumpText(control))
            {
                // Gump art commonly draws a label as a stack of near-identical siblings - several
                // dark offset copies plus one lighter one on top - to fake a drop-shadow/outline
                // without a real font effect. That reliably makes the *same* line of text repeat
                // several times in a row here, which is noise for a text dump even though it is
                // exactly what makes the label readable in the rendered gump. Only consecutive
                // duplicates are dropped, so a genuinely repeated destination name elsewhere in a
                // long list still shows up on its own line.
                if (line == last)
                {
                    continue;
                }

                last = line;

                yield return line;
            }
        }

        private static IEnumerable<string> WalkGumpText(Control control)
        {
            foreach (Control child in control.Children)
            {
                switch (child)
                {
                    // Buttons carry no public caption text of their own - gump layouts render a
                    // button's label as a separate, positionally-adjacent Label/HtmlControl, which
                    // the walk below already surfaces.
                    case Button button:
                        yield return $"[button {button.ButtonID}]";
                        break;

                    // A radio/checkbox is a *switch*: it is not pressed, it is selected, and the
                    // selection travels with whichever button is pressed afterwards (the gump
                    // response packet carries a list of checked switch ids). A destination list is
                    // the typical shape - pick a city (switch), then press GO (button). Its label
                    // sits beside it in the layout just as a button's does. RadioButton derives from
                    // Checkbox, so it must be matched first.
                    case RadioButton radio:
                        yield return $"[radio {radio.LocalSerial}{(radio.IsChecked ? " selected" : "")}]";
                        break;

                    case Checkbox checkbox:
                        yield return $"[checkbox {checkbox.LocalSerial}{(checkbox.IsChecked ? " checked" : "")}]";
                        break;

                    // Gump HTML text carries its own markup (<BASEFONT COLOR=...>, <BIG>, etc.)
                    // for rendering - stripped here since only the words matter for a text dump,
                    // and stripping color also lets the drop-shadow dedup above collapse a label's
                    // outline copy and its differently-coloured "real" copy into one line instead
                    // of two, since they differ only in the tag that gets removed.
                    case HtmlControl html when !string.IsNullOrWhiteSpace(html.Text):
                        yield return StripHtml(html.Text);
                        break;

                    case Label label when !string.IsNullOrWhiteSpace(label.Text):
                        yield return label.Text.Trim();
                        break;

                    // A text entry is filled, not pressed: its id and current text travel with
                    // whichever button is pressed afterwards, via `gumpresponse ... text:<id>=<value>`.
                    // A search form is the typical shape - type the item name, then press SEARCH.
                    case StbTextBox box:
                        yield return $"[textentry {box.LocalSerial}{(string.IsNullOrEmpty(box.Text) ? "" : $" = \"{box.Text}\"")}]";
                        break;
                }

                foreach (string line in WalkGumpText(child))
                {
                    yield return line;
                }
            }
        }

        private static readonly Regex HtmlTag = new("<[^>]*>", RegexOptions.Compiled);

        private static string StripHtml(string text) => HtmlTag.Replace(text, "").Trim();

        /// <summary>
        /// Installs <paramref name="narrate"/> alongside the existing handler for
        /// <paramref name="id"/>. The original runs first and gets an untouched reader; narration
        /// then parses its own copy of the bytes, so nothing about the client's own handling changes.
        /// </summary>
        private static void Wrap(byte id, Action<World, ReadOnlySpan<byte>> narrate)
        {
            var original = PacketHandlers.Handler.Get(id);

            PacketHandlers.Handler.Add(id, (World world, ref StackDataReader p) =>
            {
                var copy = p.Buffer.ToArray();

                original?.Invoke(world, ref p);

                try
                {
                    narrate(world, copy);
                }
                catch
                {
                    // Narration must never be able to break packet handling.
                }
            });
        }
    }
}
