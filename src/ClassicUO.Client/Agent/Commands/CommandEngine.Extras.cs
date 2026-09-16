// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassicUO.Agent.Capabilities;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Commands with logic of our own on top of the client: route planning and guarded walks,
    /// POI lookups, captured vendor stock, line of sight, mounting with its war-mode handling,
    /// and the map diagnostics. The rule for what lives here versus Actions: if the handler is a
    /// straight call into a ClassicUO verb, it is an action; if it builds on a Capability or adds
    /// its own state machine, it is an extra.
    /// </summary>
    internal sealed partial class CommandEngine
    {
        private void RegisterExtras()
        {
            RegisterNavigation();
            RegisterVendor();
            RegisterSight();
            RegisterDiagnostics();
            RegisterMounts();
            RegisterContextMenus();
        }

        private void RegisterPoi()
        {
            Register("poi", "poi", "POI data status and categories", ctx =>
            {
                ctx.Print(Capabilities.PoiCommands.LoadMessage ?? "no POI data");

                var categories = Capabilities.PoiCommands.Categories;

                if (categories.Count > 0)
                {
                    ctx.Print($"Categories: {string.Join(", ", categories)}");
                }
            }, "loadpoi");

            Register("closest", "closest <category>", "Nearest POI in a category", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                var index = Capabilities.PoiCommands.Index;

                if (index == null)
                {
                    ctx.Warn(Capabilities.PoiCommands.LoadMessage ?? "no POI data loaded");

                    return;
                }

                string category = ctx.Arg(0);

                if (string.IsNullOrWhiteSpace(category))
                {
                    ctx.Warn($"usage: closest <category>   ({string.Join(", ", Capabilities.PoiCommands.Categories)})");

                    return;
                }

                var (px, py, map) = ctx.Game(w => (w.Player.X, w.Player.Y, w.MapIndex));
                var hit = index.FindNearest(px, py, category, map);

                if (hit == null)
                {
                    ctx.Warn($"no POI in category '{category}' on {Capabilities.Poi.PoiIndex.MapName(map)}");

                    return;
                }

                ctx.Print($"[POI] {hit.Name} ({hit.Category}) at ({hit.X}, {hit.Y}, {hit.Z}) {hit.MapName} - {hit.DistanceTo(px, py):F0} tiles away");
            }, "nearestpoi");

            Register("findpoi", "findpoi <text>", "Search POIs by name", ctx =>
            {
                var index = Capabilities.PoiCommands.Index;

                if (index == null)
                {
                    ctx.Warn(Capabilities.PoiCommands.LoadMessage ?? "no POI data loaded");

                    return;
                }

                string query = string.Join(" ", ctx.Args.Skip(1));

                if (string.IsNullOrWhiteSpace(query))
                {
                    ctx.Warn("usage: findpoi <text>");

                    return;
                }

                var (inGame, px, py, map) = ctx.Game(w => (w.InGame, w.Player.X, w.Player.Y, w.MapIndex));
                // Every facet is searched; the current one only wins ties. A name that exists
                // elsewhere is still the answer - it just comes with a note that it is not walkable.
                var hit = inGame
                    ? index.FindByName(query, out int matched, out int total, px, py, map)
                    : index.FindByName(query, out matched, out total);

                if (hit == null)
                {
                    ctx.Warn($"no POI matching '{query}'");

                    return;
                }

                string where = inGame
                    ? hit.OnMap(map)
                        ? $" - {hit.DistanceTo(px, py):F0} tiles away"
                        : $" - on another facet (you are on {Capabilities.Poi.PoiIndex.MapName(map)}), not walkable from here"
                    : string.Empty;

                ctx.Print($"[POI] {hit.Name} ({hit.Category}) at ({hit.X}, {hit.Y}, {hit.Z}) {hit.MapName}{where}  [matched {matched}/{total} words]");
            }, "fp");
        }

        private static bool TryCart(CommandContext ctx, string verb, out Tuple<uint, ushort>[] items)
        {
            items = null;

            if (ctx.ArgCount < 2 || ctx.ArgCount % 2 != 0)
            {
                ctx.Warn($"usage: {verb} <serial> <amount> [<serial> <amount> ...]");

                return false;
            }

            var cart = new Tuple<uint, ushort>[ctx.ArgCount / 2];

            for (int i = 0; i < cart.Length; ++i)
            {
                if (!TrySerial(ctx, i * 2, out uint serial))
                {
                    return false;
                }

                if (!ushort.TryParse(ctx.Arg(i * 2 + 1), out ushort amount) || amount == 0)
                {
                    ctx.Warn($"expected a non-zero amount after 0x{serial:X8}, got '{ctx.Arg(i * 2 + 1) ?? "nothing"}'");

                    return false;
                }

                cart[i] = new Tuple<uint, ushort>(serial, amount);
            }

            items = cart;

            return true;
        }

        private static string DescribeCart(Tuple<uint, ushort>[] items) =>
            string.Join(", ", items.Select(e => $"{e.Item2} x 0x{e.Item1:X8}"));

        private void RegisterNavigation()
        {
            // "avoid:1425,1544" or "avoid:1425,1544,12" - tiles the planner must route around,
            // on top of the ones the server refuses. The radius defaults to
            // Navigation.DEFAULT_AVOID_RADIUS.
            //
            // This exists because "walk somewhere else and approach from there" does not work when
            // the obstruction sits in the only corridor: every detour still has to come back
            // through it, so the route is identical and the character walks into the same creature
            // again. Measured - a shade parked in Britain's north-west gate closed that approach
            // for a whole session, and four different intermediate waypoints all produced a route
            // straight back through its tile. An obstacle the planner knows about is the only
            // deterministic way around one.
            HashSet<(int x, int y)> ParseAvoid(CommandContext ctx)
            {
                var tiles = new HashSet<(int x, int y)>();

                foreach (string arg in ctx.Args)
                {
                    if (!arg.StartsWith("avoid:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] parts = arg.Substring("avoid:".Length).Split(',');

                    if (parts.Length < 2
                        || !int.TryParse(parts[0], out int ax)
                        || !int.TryParse(parts[1], out int ay))
                    {
                        ctx.Warn($"ignoring '{arg}' - expected avoid:<x>,<y>[,<radius>]");

                        continue;
                    }

                    int radius = Capabilities.Navigation.DEFAULT_AVOID_RADIUS;

                    if (parts.Length >= 3 && !int.TryParse(parts[2], out radius))
                    {
                        radius = Capabilities.Navigation.DEFAULT_AVOID_RADIUS;
                    }

                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        for (int dy = -radius; dy <= radius; dy++)
                        {
                            tiles.Add((ax + dx, ay + dy));
                        }
                    }

                    ctx.Print($"Avoiding ({ax}, {ay}) and {radius} tiles around it");
                }

                return tiles.Count == 0 ? null : tiles;
            }

            bool TryTarget(CommandContext ctx, out int x, out int y, out int? z)
            {
                x = y = 0;
                z = null;

                // "goto 1425 1690" or "goto bank" - a POI category or name is usually what you
                // actually mean, and typing coordinates by hand is how mistakes happen.
                // "goto 1438 1687 21" names a floor as well: the roof, not the street under it.
                if (ctx.ArgCount >= 2 && int.TryParse(ctx.Arg(0), out x) && int.TryParse(ctx.Arg(1), out y))
                {
                    if (ctx.ArgCount >= 3 && int.TryParse(ctx.Arg(2), out int zArg))
                    {
                        z = zArg;
                    }

                    return true;
                }

                string query = string.Join(" ", ctx.Args.Skip(1).Where(
                    a => !a.StartsWith("avoid:", StringComparison.OrdinalIgnoreCase)));

                if (string.IsNullOrWhiteSpace(query))
                {
                    ctx.Warn($"usage: {ctx.Args[0]} <x> <y>   |   {ctx.Args[0]} <poi name or category>");

                    return false;
                }

                var index = Capabilities.PoiCommands.Index;

                if (index == null)
                {
                    ctx.Warn($"'{query}' is not a coordinate pair and no POI data is loaded");

                    return false;
                }

                var (px, py, map) = ctx.Game(w => (w.Player.X, w.Player.Y, w.MapIndex));

                // A walk cannot cross facets, so only this facet's points are candidates. But a
                // name that matches better somewhere else means the caller asked for *that* place
                // (say "luna bank" from Trammel): refuse with the facet, rather than quietly walking
                // to whichever local name shares a word with it.
                int hereMatched = 0;
                var hit = index.FindNearest(px, py, query, map);

                if (hit != null)
                {
                    hereMatched = int.MaxValue;   // an exact category is never outranked by a name elsewhere
                }
                else
                {
                    hit = index.FindByName(query, out hereMatched, out _, px, py, map, strictMap: true);
                }

                var anywhere = index.FindByName(query, out int anyMatched, out _, px, py, map);

                if (anywhere != null && !anywhere.OnMap(map) && anyMatched > hereMatched)
                {
                    ctx.Warn($"'{query}' is {anywhere.Name} on {anywhere.MapName} at ({anywhere.X}, {anywhere.Y}); you are on {Capabilities.Poi.PoiIndex.MapName(map)} - cross facets by moongate first");

                    return false;
                }

                if (hit == null)
                {
                    ctx.Warn($"no POI matching '{query}'");

                    return false;
                }

                x = hit.X;
                y = hit.Y;

                ctx.Print($"[POI] {hit.Name} ({hit.Category}) at ({hit.X}, {hit.Y})");

                return true;
            }

            // Hands a walk to Navigator and returns immediately.
            //
            // Movement is fire and forget: this reports that the walk *started*, never whether it
            // arrived, because it does not wait to find out. That is the point - the command worker
            // is serial, so a blocking walk used to stall every other command for its whole
            // duration, and nothing could bandage, attack or open a door mid-journey.
            //
            // Read the outcome from the `nav` block of the state file, or watch for `[NAV]`.
            void StartWalk(CommandContext ctx,
                           Func<CommandContext, int, int, int?, HashSet<(int x, int y)>, bool> walk)
            {
                if (!ctx.RequireInGame() || !TryTarget(ctx, out int x, out int y, out int? z))
                {
                    return;
                }

                var avoid = ParseAvoid(ctx);

                bool replacing = Navigator.Active;
                int previousX = Navigator.TargetX, previousY = Navigator.TargetY;

                Navigator.Start(ctx.Dispatcher, ctx.Raw, x, y, c => walk(c, x, y, z, avoid));

                if (replacing && Navigator.TargetX == previousX && Navigator.TargetY == previousY && (previousX != x || previousY != y))
                {
                    ctx.Print($"Already walking to ({previousX}, {previousY}) - close enough, keeping that walk");

                    return;
                }

                if (replacing)
                {
                    ctx.Print("Cancelled the previous walk - one at a time, newest wins");
                }

                string where = z == null ? $"({x}, {y})" : $"({x}, {y}, z {z})";
                ctx.Print($"Walking to {where} - not waiting; watch .nav in {StateFile.Path}");
            }

            // goto settles for the closest reachable tile rather than failing outright: a target is
            // very often something you cannot literally stand on - a POI marking a building, a
            // counter, a tile someone else is on - and refusing to move at all is never the useful
            // answer. `gotoexact` is there for when the precise tile really matters.
            Register("goto", "goto <x> <y> [z] | goto <poi> [avoid:<x>,<y>[,<r>]]",
                     "Start walking to a coordinate (optionally on a given floor) or POI", ctx =>
            {
                StartWalk(ctx, (c, x, y, z, avoid) => Capabilities.Navigation.GoToNear(c, x, y, avoid: avoid, tz: z));
            }, "go", "gonear");

            Register("gotoexact", "gotoexact <x> <y> [z] [avoid:<x>,<y>[,<r>]]",
                     "Start walking to exactly this tile", ctx =>
            {
                StartWalk(ctx, (c, x, y, z, avoid) => Capabilities.Navigation.GoTo(c, x, y, avoid: avoid, tz: z));
            });

            Register("travel", "travel <x> <y> [z] | travel <poi> [avoid:<x>,<y>[,<r>]]",
                     "Start long-distance travel in hops", ctx =>
            {
                StartWalk(ctx, (c, x, y, z, avoid) => Capabilities.Navigation.Travel(c, x, y, avoid, z));
            });

            Register("stopgo", "stop", "Cancel the current walk or long-running command", ctx =>
            {
                // Handled ahead of the queue in AgentHost; this entry exists so `help` lists it.
                ctx.Print("Nothing running");
            });
        }

        /// <summary>Riding: a double-click with war-mode handling built around it.</summary>
        /// <summary>War mode is cleared by a server reply, so it needs settling time.</summary>
        private const int WAR_MODE_SETTLE_MS = 50;
        private const int WAR_MODE_SETTLE_TRIES = 20;

        private void RegisterMounts()
        {
            Register("mount", "mount [<serial>]", "Ride a mount, clearing war mode first", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                uint serial = 0;

                if (ctx.ArgCount >= 1 && !TrySerial(ctx, 0, out serial))
                {
                    return;
                }

                string report = ctx.Game(w =>
                {
                    if (w.Player.IsMounted)
                    {
                        return "already mounted - `dismount` first";
                    }

                    // No serial given: ride our own animal. IsRenamable is the server's own "this
                    // creature is yours" flag, so this cannot pick up a stray at a stable that is
                    // standing closer than the horse we actually bought.
                    if (!SerialHelper.IsValid(serial))
                    {
                        Mobile best = null;

                        foreach (Mobile m in w.Mobiles.Values)
                        {
                            if (m.IsRenamable && m.Serial != w.Player.Serial
                                && (best == null || m.Distance < best.Distance))
                            {
                                best = m;
                            }
                        }

                        if (best == null)
                        {
                            return "no pet of ours in view - pass a serial";
                        }

                        serial = best.Serial;
                    }

                    if (w.Player.InWarMode)
                    {
                        GameActions.RequestWarMode(w.Player, false);
                    }

                    return null;
                });

                if (report != null)
                {
                    ctx.Warn(report);

                    return;
                }

                // Waiting here is the whole point. Requesting war mode off and double-clicking in
                // the same frame does not work: the flag is cleared by a *server reply*, so the
                // click arrives while the server still has us flagged and lands as an attack on the
                // horse. Observed exactly that - war mode read `false` afterwards and `isMounted`
                // was still false, because the two crossed on the wire.
                for (int i = 0; i < WAR_MODE_SETTLE_TRIES && ctx.Game(w => w.Player.InWarMode); i++)
                {
                    if (!ctx.Sleep(WAR_MODE_SETTLE_MS))
                    {
                        return;
                    }
                }

                if (ctx.Game(w => w.Player.InWarMode))
                {
                    ctx.Warn("war mode did not clear - refusing to click, it would attack the mount");

                    return;
                }

                ctx.Game(w =>
                {
                    // A cursor left open by an earlier attack attempt would otherwise swallow the
                    // next targeted command.
                    w.TargetManager.CancelTarget();
                    GameActions.DoubleClick(w, serial);
                });

                ctx.Print($"Mounting 0x{serial:X8} - confirm with .isMounted, not this line");
            }, "ride");

            // Dismounting is double-clicking *yourself*, not the animal - double-clicking the mount
            // again does nothing. Easy to get wrong, so it gets its own verb.

            Register("dismount", "dismount", "Get off the mount", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                string report = ctx.Game(w =>
                {
                    if (!w.Player.IsMounted)
                    {
                        return "not mounted";
                    }

                    GameActions.DoubleClick(w, w.Player.Serial);

                    return null;
                });

                if (report != null)
                {
                    ctx.Warn(report);

                    return;
                }

                ctx.Print("Dismounting - confirm with .isMounted, not this line");
            }, "unmount");

        }

        private void RegisterVendor()
        {
            Register("shop", "shop", "List the open vendor's stock", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                var stock = Capabilities.VendorStock.Items;

                if (stock.Count == 0)
                {
                    ctx.Print("No shop list received - say \"<vendor name> buy\" next to a vendor first");

                    return;
                }

                ctx.Print($"[SHOP] vendor 0x{Capabilities.VendorStock.Vendor:X8}:");

                foreach (var entry in stock)
                {
                    // Livestock and goods arrive in one list and read identically once named, so
                    // say which is which: a mobile-range serial is an animal that will follow you
                    // out of the stable, not something that lands in the backpack.
                    string kind = SerialHelper.IsMobile(entry.Serial) ? " [live]" : "";

                    ctx.Print($"  0x{entry.Serial:X8} {entry.Price,7}gp  x{entry.Amount,-4} {entry.Name}{kind}");
                }
            });

            // The 0x3B buy packet carries a *list* of items - a real client fills a cart and sends
            // one packet - and the server closes the vendor's buy list once it has been answered.
            // So buying N different things as N single-item requests needs the list reopened N-1
            // times, and every reopen mints fresh serials for every stock line. Taking the whole
            // cart here makes a multi-item purchase one packet and one round trip instead.
            Register("buy", "buy <serial> <amount> [<serial> <amount> ...]", "Buy from the open vendor", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                if (!TryCart(ctx, "buy", out var items))
                {
                    return;
                }

                uint vendor = Capabilities.VendorStock.Vendor;

                if (vendor == 0)
                {
                    ctx.Warn("no shop list received - say \"<vendor name> buy\" next to a vendor first");

                    return;
                }

                ctx.Game(_ => NetClient.Socket.Send_BuyRequest(vendor, items));

                ctx.Print($"Buy request sent: {DescribeCart(items)}");
            });

            Register("selllist", "selllist", "List what the open vendor will buy from us", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                var stock = Capabilities.VendorStock.SellItems;

                if (stock.Count == 0)
                {
                    ctx.Print("No sell list received - say \"<vendor name> sell\" next to a vendor first");

                    return;
                }

                ctx.Print($"[SHOP] sell to vendor 0x{Capabilities.VendorStock.SellVendor:X8}:");

                foreach (var entry in stock)
                {
                    // Livestock and goods arrive in one list and read identically once named, so
                    // say which is which: a mobile-range serial is an animal that will follow you
                    // out of the stable, not something that lands in the backpack.
                    string kind = SerialHelper.IsMobile(entry.Serial) ? " [live]" : "";

                    ctx.Print($"  0x{entry.Serial:X8} {entry.Price,7}gp  x{entry.Amount,-4} {entry.Name}{kind}");
                }
            });

            // Like buy, the 0x9F sell packet carries a list, so a whole backpack's worth of loot
            // goes in one request rather than one round trip per stack.
            Register("sell", "sell <serial> <amount> [<serial> <amount> ...]", "Sell an item to the open vendor", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                if (!TryCart(ctx, "sell", out var items))
                {
                    return;
                }

                uint vendor = Capabilities.VendorStock.SellVendor;

                if (vendor == 0)
                {
                    ctx.Warn("no sell list received - say \"<vendor name> sell\" next to a vendor first");

                    return;
                }

                ctx.Game(_ => NetClient.Socket.Send_SellRequest(vendor, items));

                ctx.Print($"Sell request sent: {DescribeCart(items)}");
            });

        }

        private void RegisterSight()
        {
            Register("los", "los [range]", "Line of sight to each nearby mobile", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                int range = ctx.ArgCount >= 1 && int.TryParse(ctx.Arg(0), out int r) ? r : 18;

                foreach (string line in ctx.Game(w =>
                         {
                             var lines = new List<string>();
                             var p = w.Player;

                             foreach (var mobile in w.Mobiles.Values
                                          .Where(m => m.Serial != p.Serial && m.Distance <= range)
                                          .OrderBy(m => m.Distance))
                             {
                                 bool visible = LineOfSight.HasLineOfSight(w, p.X, p.Y, p.Z, mobile.X, mobile.Y, mobile.Z);

                                 lines.Add($"  {(visible ? "YES" : "no ")} 0x{mobile.Serial:X8} " +
                                           $"{DescribeMobile(w, mobile),-28} d={mobile.Distance}");
                             }

                             if (lines.Count == 0)
                             {
                                 lines.Add($"No mobiles within {range} tiles");
                             }

                             return lines;
                         }))
                {
                    ctx.Print(line);
                }
            });

            Register("attacknearest", "attacknearest", "Attack the nearest hostile with line of sight", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                string result = ctx.Game(w =>
                {
                    var p = w.Player;

                    // Only things it is legal and sane to attack - never a blue Innocent - and only
                    // when we can actually see them, or we stand there swinging at a wall.
                    var target = w.Mobiles.Values
                        .Where(m => m.Serial != p.Serial)
                        .Where(m => m.NotorietyFlag is NotorietyFlag.Criminal
                            or NotorietyFlag.Enemy
                            or NotorietyFlag.Murderer
                            or NotorietyFlag.Gray)
                        .Where(m => LineOfSight.HasLineOfSight(w, p.X, p.Y, p.Z, m.X, m.Y, m.Z))
                        .OrderBy(m => m.Distance)
                        .FirstOrDefault();

                    if (target == null)
                    {
                        return "no hostile mobile in view with line of sight";
                    }

                    GameActions.Attack(w, target.Serial);

                    return $"OK:Attacking 0x{target.Serial:X8} {DescribeMobile(w, target)} at distance {target.Distance}";
                });

                if (result.StartsWith("OK:", StringComparison.Ordinal))
                {
                    ctx.Print(result.Substring(3));
                }
                else
                {
                    ctx.Warn(result);
                }
            }, "an");
        }

        private void RegisterDiagnostics()
        {
            Register("canwalk", "canwalk [<x> <y>]", "Whether a tile is walkable, and why not", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                foreach (var line in ctx.Game(w =>
                {
                    var results = new List<string>();
                    var player = w.Player;

                    int tx = ctx.ArgCount >= 2 && int.TryParse(ctx.Arg(0), out int px) ? px : player.X;
                    int ty = ctx.ArgCount >= 2 && int.TryParse(ctx.Arg(1), out int py) ? py : player.Y;

                    var chunk = w.Map.GetChunk(tx, ty, false);

                    results.Add($"Tile ({tx}, {ty})  player at ({player.X}, {player.Y}, {player.Z})");
                    results.Add($"  chunk resident: {(chunk != null ? "yes" : "NO - nothing here can be walked on")}");

                    var head = w.Map.GetTile(tx, ty, false);

                    if (head == null)
                    {
                        results.Add("  no tile objects loaded");

                        return results;
                    }

                    var obj = head;

                    while (obj.TPrevious != null)
                    {
                        obj = obj.TPrevious;
                    }

                    for (; obj != null; obj = obj.TNext)
                    {
                        string kind = obj switch
                        {
                            Land => "land",
                            Static => "static",
                            Item => "item",
                            Mobile => "mobile",
                            Multi => "multi",
                            _ => obj.GetType().Name.ToLowerInvariant()
                        };

                        results.Add($"  {kind,-7} 0x{obj.Graphic:X4} z={obj.Z}");
                    }

                    // Ask the client's own collision check, in each direction, exactly as Walk does.
                    foreach (var d in Directions.Clockwise)
                    {
                        var dir = d;
                        int nx = player.X;
                        int ny = player.Y;
                        sbyte nz = player.Z;

                        bool ok = player.Pathfinder.CanWalk(ref dir, ref nx, ref ny, ref nz);

                        results.Add($"  walk {Directions.Name(d),-9} -> {(ok ? $"ok ({nx}, {ny}, {nz})" : "blocked")}");
                    }

                    return results;
                }))
                {
                    ctx.Print(line);
                }
            });


            Register("tiles", "tiles <x> <y>", "Everything on a tile, with flags", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                if (!int.TryParse(ctx.Arg(0), out int tx) || !int.TryParse(ctx.Arg(1), out int ty))
                {
                    ctx.Warn("usage: tiles <x> <y>");

                    return;
                }

                foreach (string line in ctx.Game(w =>
                         {
                             var lines = new List<string> { $"Tile ({tx}, {ty}):" };
                             var head = w.Map.GetTile(tx, ty, true);

                             if (head == null)
                             {
                                 lines.Add("  nothing loaded here");

                                 return lines;
                             }

                             var obj = head;

                             while (obj.TPrevious != null)
                             {
                                 obj = obj.TPrevious;
                             }

                             for (; obj != null; obj = obj.TNext)
                             {
                                 switch (obj)
                                 {
                                     case Land land:
                                         lines.Add($"  land   0x{land.Graphic:X4} z={land.Z,-4} {land.TileData.Flags}");
                                         break;

                                     case Static st:
                                         lines.Add($"  static 0x{st.Graphic:X4} z={st.Z,-4} h={st.ItemData.Height,-3} {st.ItemData.Flags}");
                                         break;

                                     case Item item:
                                         lines.Add($"  item   0x{item.Graphic:X4} z={item.Z,-4} h={item.ItemData.Height,-3} " +
                                                   $"serial=0x{item.Serial:X8} {item.ItemData.Flags}");
                                         break;

                                     case Mobile mobile:
                                         lines.Add($"  mobile 0x{mobile.Graphic:X4} z={mobile.Z,-4} serial=0x{mobile.Serial:X8}");
                                         break;

                                     case Multi multi:
                                         lines.Add($"  multi  0x{multi.Graphic:X4} z={multi.Z,-4} {multi.ItemData.Flags}");
                                         break;
                                 }
                             }

                             return lines;
                         }))
                {
                    ctx.Print(line);
                }
            });

            Register("path", "path <x> <y> [z] [avoid:<x>,<y>[,<r>]]",
                     "Plan a route without walking it", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                if (!int.TryParse(ctx.Arg(0), out int tx) || !int.TryParse(ctx.Arg(1), out int ty))
                {
                    ctx.Warn("usage: path <x> <y> [z] [avoid:<x>,<y>[,<r>]]");

                    return;
                }

                int? tz = int.TryParse(ctx.Arg(2), out int zArg) ? zArg : null;

                // Same obstacle syntax `goto` takes, so a detour can be *checked* before it is
                // walked. That is the whole reason this command exists, and picking waypoints by
                // eye instead is what produced four routes in a row straight back through the
                // creature they were meant to avoid.
                var avoidTiles = new HashSet<(int x, int y)>();

                foreach (string arg in ctx.Args)
                {
                    if (!arg.StartsWith("avoid:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] parts = arg.Substring("avoid:".Length).Split(',');

                    if (parts.Length < 2
                        || !int.TryParse(parts[0], out int ax)
                        || !int.TryParse(parts[1], out int ay))
                    {
                        ctx.Warn($"ignoring '{arg}' - expected avoid:<x>,<y>[,<radius>]");

                        continue;
                    }

                    int radius = Capabilities.Navigation.DEFAULT_AVOID_RADIUS;

                    if (parts.Length >= 3 && !int.TryParse(parts[2], out radius))
                    {
                        radius = Capabilities.Navigation.DEFAULT_AVOID_RADIUS;
                    }

                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        for (int dy = -radius; dy <= radius; dy++)
                        {
                            avoidTiles.Add((ax + dx, ay + dy));
                        }
                    }
                }

                var planner = ctx.Game(w =>
                {
                    MapResidency.SetFocus(w, tx, ty);

                    return new GridPathfinder.Planner(w, tx, ty, tz, 0, avoidTiles.Count == 0 ? null : avoidTiles);
                });

                while (true)
                {
                    var step = planner.Step(GridPathfinder.MAX_PLAN_MS, ctx.Cancel);

                    if (step == GridPathfinder.StepResult.Done)
                    {
                        break;
                    }

                    if (step == GridPathfinder.StepResult.NeedChunks)
                    {
                        ctx.Game(w => planner.FillPending(w), "PathGrid.Fill");
                    }
                }

                foreach (string line in ctx.Game(w =>
                         {
                             var plan = planner.Result;

                             string cost = $"{plan.NodesExplored} nodes explored in {plan.ElapsedMs}ms " +
                                           $"({plan.Fills} chunk fill(s) on the game thread), ended={plan.Ended.ToString().ToLowerInvariant()}";

                             var path = plan.Path;

                             if (path == null)
                             {
                                 return new List<string> { $"No route toward ({tx}, {ty}) - {cost}" };
                             }

                             int rx = plan.ReachedX, ry = plan.ReachedY;
                             sbyte rz = plan.ReachedZ;
                             bool reached = rx == tx && ry == ty && GridPathfinder.OnGoalFloor(rz, tz);

                             var lines = new List<string>
                             {
                                 $"{path.Count} steps, ending at ({rx}, {ry}, z {rz})" +
                                 (reached ? string.Empty : "  (closest reachable)") +
                                 $" - {cost}"
                             };

                             lines.Add("  " + string.Join(" ", path.Select(s => Directions.Name(s.Direction))));

                             return lines;
                         }))
                {
                    ctx.Print(line);
                }

                ctx.Game(_ => MapResidency.ClearFocus());
            });

            Register("allitems", "allitems", "Every tracked item, container state included", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                foreach (string line in ctx.Game(w =>
                         {
                             var lines = new List<string>();

                             foreach (var item in w.Items.Values.OrderBy(i => i.Serial))
                             {
                                 lines.Add($"  0x{item.Serial:X8} {DescribeItem(item),-34} " +
                                           $"({item.X}, {item.Y}, {item.Z}) " +
                                           $"{(item.OnGround ? "ground" : $"in 0x{item.Container:X8}")} " +
                                           $"layer={item.Layer}");
                             }

                             if (lines.Count == 0)
                             {
                                 lines.Add("No items tracked");
                             }

                             return lines;
                         }))
                {
                    ctx.Print(line);
                }
            });

            Register("debuglos", "debuglos <x> <y>", "Tile-by-tile sight line to a point", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                if (!int.TryParse(ctx.Arg(0), out int tx) || !int.TryParse(ctx.Arg(1), out int ty))
                {
                    ctx.Warn("usage: debuglos <x> <y>");

                    return;
                }

                foreach (string line in ctx.Game(w =>
                         {
                             var lines = new List<string>();
                             var p = w.Player;

                             int steps = Math.Max(Math.Abs(tx - p.X), Math.Abs(ty - p.Y));

                             if (steps == 0)
                             {
                                 lines.Add("same tile");

                                 return lines;
                             }

                             for (int i = 1; i < steps; i++)
                             {
                                 lines.Add(LineOfSight.Describe(w, p.X, p.Y, p.Z, tx, ty, p.Z, i, steps));
                             }

                             lines.Add(LineOfSight.HasLineOfSight(w, p.X, p.Y, p.Z, tx, ty, p.Z)
                                 ? "Line of sight: YES"
                                 : "Line of sight: NO");

                             return lines;
                         }))
                {
                    ctx.Print(line);
                }
            });
        }

    }
}
