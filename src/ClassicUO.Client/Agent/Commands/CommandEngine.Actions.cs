// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;

namespace ClassicUO.Agent
{
    /// <summary>
    /// The commands that map onto ClassicUO's own verbs, plus plain reads of what the client
    /// already holds. Almost every one is a single call into GameActions, a manager or the World -
    /// the point of sharing the engine is that the CLI issues exactly the action the UI would,
    /// through the same code path, so the two can never diverge. Anything with logic of our own
    /// layered on top belongs in CommandEngine.Extras.cs.
    /// </summary>
    internal sealed partial class CommandEngine
    {
        private void RegisterActions()
        {
            RegisterSpeech();
            RegisterInteraction();
            RegisterInspection();
            RegisterTrade();
            RegisterGumps();
        }

        private void RegisterCore()
        {
            Register("help", "help", "List all commands", Help, "?", "h");

            Register("pos", "pos", "Player X, Y, Z, direction and map", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                ctx.Print(ctx.Game(w =>
                {
                    var p = w.Player;

                    return $"Position: ({p.X}, {p.Y}, {p.Z}) facing {Directions.Name(p.Direction)} on map {w.MapIndex}";
                }));
            }, "position");

            Register("status", "status", "HP, mana, stamina, stats and gold", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                foreach (var line in ctx.Game(w =>
                {
                    var p = w.Player;

                    return new[]
                    {
                        $"Name:    {p.Name}",
                        $"HP:      {p.Hits}/{p.HitsMax}",
                        $"Mana:    {p.Mana}/{p.ManaMax}",
                        $"Stamina: {p.Stamina}/{p.StaminaMax}",
                        $"STR/DEX/INT: {p.Strength}/{p.Dexterity}/{p.Intelligence}",
                        $"Gold:    {p.Gold}   Weight: {p.Weight}/{p.WeightMax}",
                        $"War mode: {(p.InWarMode ? "ON" : "off")}"
                    };
                }))
                {
                    ctx.Print(line);
                }
            }, "stat");

            Register("world", "world", "Login state, map and entity counts", ctx =>
            {
                foreach (var line in ctx.Game(w => new[]
                {
                    $"InGame:  {w.InGame}",
                    $"Map:     {w.MapIndex}",
                    $"Mobiles: {w.Mobiles.Count}",
                    $"Items:   {w.Items.Count}"
                }))
                {
                    ctx.Print(line);
                }
            }, "info");

            // Deliberately does not duplicate `pos`/`status` output - the uo-* skills grep for
            // those exact line shapes. This exists so the state file is discoverable from `help`
            // and readable without knowing where it lives.
            Register("state", "state", "Path and contents of the continuous state file", ctx =>
            {
                ctx.Print($"State file: {StateFile.Path}");

                try
                {
                    ctx.Print(File.ReadAllText(StateFile.Path).TrimEnd());
                }
                catch (Exception ex)
                {
                    ctx.Fail($"cannot read {StateFile.Path}: {ex.Message}");
                }
            }, "statefile");

            Register("walk", "walk <n|s|e|w|ne|nw|se|sw> [run]", "Walk one tile", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                if (!Directions.TryParse(ctx.Arg(0), out Direction dir))
                {
                    ctx.Warn($"unknown direction '{ctx.Arg(0)}'");

                    return;
                }

                bool run = string.Equals(ctx.Arg(1), "run", StringComparison.OrdinalIgnoreCase);

                // A running `goto` would otherwise keep stepping toward its own destination while
                // this one-tile request loses the race for the pending-step ring and comes back
                // "refused" - technically safe, since Walk() guards the sequence either way, but a
                // confusing answer to someone steering by hand. Movement is last-wins across the
                // board: a second goto cancels the first, and so does a manual step.
                if (Navigator.Active)
                {
                    Navigator.Stop();
                    ctx.Print("Cancelled the walk in progress");

                    // Cancelling stops the walk thread issuing new steps, but a step it already
                    // sent is still outstanding, and Walk() refuses while the pending ring is not
                    // empty. Without this drain the cancel appears to work and the step you asked
                    // for is still rejected. Bounded: the walker also clears on its own timeout.
                    for (int waited = 0; waited < 900; waited += 50)
                    {
                        if (ctx.Game(w => w.Player.Walker.StepsCount == 0 &&
                                          w.Player.Walker.UnacceptedPacketsCount == 0))
                        {
                            break;
                        }

                        if (!ctx.Sleep(50))
                        {
                            return;
                        }
                    }
                }

                // Walk() is the ONLY movement path. It owns the walk sequence byte, the five-slot
                // pending-step ring and the step throttle in WalkerManager, all of which the server
                // validates. Sending 0x02 directly would desync the moment the UI also walked.
                var before = ctx.Game(w => (w.Player.X, w.Player.Y));

                bool accepted = ctx.Game(w => w.Player.Walk(dir, run));

                if (!accepted)
                {
                    // Walk() refuses while a step is still in flight, while the throttle has not
                    // expired, or when the client's own CanWalk says the tile is blocked.
                    ctx.Print(ctx.Game(w =>
                    {
                        var walker = w.Player.Walker;

                        return $"Walk refused (steps={walker.StepsCount} unacked={walker.UnacceptedPacketsCount} " +
                               $"failed={walker.WalkingFailed} throttleIn={(long)walker.LastStepRequestTime - Time.Ticks}ms)";
                    }));

                    return;
                }

                // Walk() returning true does not mean the tile changed: the first step in a new
                // direction is a turn, and pure turns can be coalesced away entirely. Poll the
                // actual position instead of trusting the return value or a fixed delay.
                var after = WaitForStep(ctx, before.X, before.Y);

                if (after.x == before.X && after.y == before.Y)
                {
                    ctx.Print($"Turned to face {Directions.Name(after.dir)} at ({after.x}, {after.y}, {after.z})");
                }
                else
                {
                    ctx.Print($"Moved to ({after.x}, {after.y}, {after.z}) facing {Directions.Name(after.dir)}");
                }
            }, "w");

            Register("show", "show", "Show the game window", ctx =>
            {
                ctx.Dispatcher.Invoke(HeadlessWindow.Show);
                ctx.Print("Game window shown");
            });

            Register("hide", "hide", "Hide the game window", ctx =>
            {
                ctx.Dispatcher.Invoke(HeadlessWindow.Hide);
                ctx.Print("Game window hidden");
            });

            Register("verbose", "verbose", "Show trace-level output", ctx =>
            {
                Output.MinLevel = AgentLogLevel.Trace;
                ctx.Print("Verbose output enabled");
            });

            Register("quiet", "quiet", "Show warnings and errors only", ctx =>
            {
                Output.MinLevel = AgentLogLevel.Warn;
                ctx.Print("Quiet output enabled");
            }, "q");
        }

        /// <summary>
        /// Polls the player's tile until it leaves (<paramref name="fromX"/>, <paramref name="fromY"/>)
        /// or the step window elapses. A step takes one movement interval plus the server round
        /// trip, and a turn never moves at all, so a timeout here is a normal outcome.
        /// </summary>
        private static (int x, int y, sbyte z, Direction dir) WaitForStep(CommandContext ctx, int fromX, int fromY)
        {
            const int TIMEOUT_MS = 1200;
            const int POLL_MS = 40;

            for (int waited = 0; waited < TIMEOUT_MS; waited += POLL_MS)
            {
                var now = ctx.Game(w => (w.Player.X, w.Player.Y, w.Player.Z, w.Player.Direction));

                if (now.X != fromX || now.Y != fromY)
                {
                    return (now.X, now.Y, now.Z, now.Direction);
                }

                if (!ctx.Sleep(POLL_MS))
                {
                    break;
                }
            }

            var final = ctx.Game(w => (w.Player.X, w.Player.Y, w.Player.Z, w.Player.Direction));

            return (final.X, final.Y, final.Z, final.Direction);
        }

        private void RegisterSpeech()
        {
            void Speak(CommandContext ctx, MessageType type, string label)
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                string text = string.Join(" ", ctx.Args.Skip(1));

                if (string.IsNullOrWhiteSpace(text))
                {
                    ctx.Warn($"usage: {label} <text>");

                    return;
                }

                // GameActions.Say routes through Send_*SpeechRequest, which already matches the
                // text against speech.mul and sends the encoded keyword form when it matches -
                // that is what makes "bank" and "<name> buy" trigger server scripts.
                ctx.Game(w => GameActions.Say(text, type: type));
                ctx.Print($"Said ({label}): {text}");
            }

            // Party is the odd one out: guild and alliance ride the same speech request as
            // ordinary talk with a different MessageType, but a party message is its own packet
            // (Send_PartyMessage), so it cannot go through Speak. Serial 0 addresses the whole
            // party rather than one member. Mirrors what the client's own chat box does for the
            // "/" (party), "\" (guild) and "|" (alliance) prefixes - those are parsed by the chat
            // UI, so they do nothing when passed to `say`, which is why these exist.
            void SpeakParty(CommandContext ctx)
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                string text = string.Join(" ", ctx.Args.Skip(1));

                if (string.IsNullOrWhiteSpace(text))
                {
                    ctx.Warn("usage: party <text>");

                    return;
                }

                ctx.Game(w => GameActions.SayParty(text));
                ctx.Print($"Said (party): {text}");
            }

            Register("say", "say <text>", "Speak", ctx => Speak(ctx, MessageType.Regular, "say"), "s");
            Register("yell", "yell <text>", "Yell", ctx => Speak(ctx, MessageType.Yell, "yell"), "y");
            Register("emote", "emote <text>", "Emote", ctx => Speak(ctx, MessageType.Emote, "emote"), "em");
            Register("whisper", "whisper <text>", "Whisper", ctx => Speak(ctx, MessageType.Whisper, "whisper"), "wh");
            Register("guild", "guild <text>", "Guild chat", ctx => Speak(ctx, MessageType.Guild, "guild"), "g");
            Register("alliance", "alliance <text>", "Alliance chat", ctx => Speak(ctx, MessageType.Alliance, "alliance"), "al");
            Register("party", "party <text>", "Party chat", SpeakParty, "p");
        }

        private void RegisterInteraction()
        {
            Register("click", "click <serial>", "Single-click (request name)", ctx =>
            {
                if (!ctx.RequireInGame() || !TrySerial(ctx, 0, out uint serial))
                {
                    return;
                }

                ctx.Game(w => GameActions.SingleClick(w, serial));
                ctx.Print($"Clicked 0x{serial:X8}");
            });

            Register("use", "use <serial>", "Double-click (open, use, equip)", ctx =>
            {
                if (!ctx.RequireInGame() || !TrySerial(ctx, 0, out uint serial))
                {
                    return;
                }

                ctx.Game(w => GameActions.DoubleClick(w, serial));
                ctx.Print($"Used 0x{serial:X8}");
            }, "dclick");

            // Some server actions ask a *question* rather than open a gump: marking a rune's
            // description, naming a pet, setting a house sign. The server sends a prompt packet
            // (0x9A / 0xC2) and the real client routes whatever you type next into a prompt
            // response instead of speech - so `say` can never answer one. This is that reply.
            Register("prompt", "prompt <text>", "Answer the server's open text prompt (rune description, pet name, ...)", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                string text = string.Join(" ", ctx.Args.Skip(1));

                if (string.IsNullOrEmpty(text))
                {
                    ctx.Warn("usage: prompt <text>   (or 'promptcancel' to decline)");

                    return;
                }

                string result = ctx.Game(w =>
                {
                    if (w.MessageManager.PromptData.Prompt == ConsolePrompt.None)
                    {
                        return "no prompt is open - something has to ask first (e.g. 'use <marked rune>')";
                    }

                    w.MessageManager.SendServerPromptResponse(text);

                    return null;
                });

                if (result != null)
                {
                    ctx.Warn(result);

                    return;
                }

                ctx.Print($"Answered the prompt: {text}");
            });

            Register("promptcancel", "promptcancel", "Decline the server's open text prompt", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                ctx.Game(w => w.MessageManager.CancelServerPrompt());
                ctx.Print("Prompt cancelled");
            });

            // A book is a client-side gump: `gumps` never lists it and nothing outside the window
            // can see its pages. Opening it makes the Narrator ask for every page, so the whole
            // text lands in the log as [BOOK] lines - which is all `read` adds over `use`.
            Register("read", "read <serial>", "Open a book and narrate every page as [BOOK] lines", ctx =>
            {
                if (!ctx.RequireInGame() || !TrySerial(ctx, 0, out uint serial))
                {
                    return;
                }

                ctx.Game(w => GameActions.DoubleClick(w, serial));
                ctx.Print($"Reading 0x{serial:X8} - pages follow as [BOOK] lines");
            });

            Register("attack", "attack <serial>", "Attack a mobile", ctx =>
            {
                if (!ctx.RequireInGame() || !TrySerial(ctx, 0, out uint serial))
                {
                    return;
                }

                string refusal = ctx.Game(w =>
                {
                    var mobile = w.Mobiles.Get(serial);

                    if (mobile == null)
                    {
                        return $"no mobile 0x{serial:X8} in view";
                    }

                    // Attacking a blue (Innocent) is a criminal act that flags you grey; the old
                    // CLI refused it and there is no reason to make that easier from a script.
                    if (mobile.NotorietyFlag == NotorietyFlag.Innocent)
                    {
                        return $"{DescribeMobile(w, mobile)} is Innocent (blue) - refusing";
                    }

                    GameActions.Attack(w, serial);

                    return null;
                });

                if (refusal != null)
                {
                    ctx.Warn(refusal);

                    return;
                }

                ctx.Print($"Attacking 0x{serial:X8}");
            }, "atk", "a");

            // `use <horse>` is how a mount is ridden, but `use` on a *mobile* means "attack" while
            // war mode is on - same command, opposite action, and the reply is "Used 0x..." either
            // way. Measured live: the mount silently failed, `isMounted` stayed false, and the
            // attack attempt left a target cursor hanging that the next targeted command would have
            // answered. This exists so callers stop having to remember the three-step dance.
            Register("war", "war", "Toggle war mode", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                bool nowInWar = ctx.Game(w =>
                {
                    GameActions.ToggleWarMode(w.Player);

                    return !w.Player.InWarMode;
                });

                ctx.Print($"War mode requested: {(nowInWar ? "ON" : "off")}");
            }, "warmode");

            Register("opendoor", "opendoor", "Open the door in front of you", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                ctx.Game(_ => GameActions.OpenDoor());
                ctx.Print("Open-door sent");
            });

            Register("backpack", "backpack", "Open the backpack", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                ctx.Print(ctx.Game(w => GameActions.OpenBackpack(w)) ? "Backpack opened" : "No backpack");
            });

            Register("target", "target <serial|self>", "Answer a target cursor", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                string arg = ctx.Arg(0);

                if (string.IsNullOrWhiteSpace(arg))
                {
                    ctx.Warn("usage: target <serial|self>");

                    return;
                }

                string result = ctx.Game(w =>
                {
                    if (!w.TargetManager.IsTargeting)
                    {
                        return "nothing is asking for a target";
                    }

                    uint serial = string.Equals(arg, "self", StringComparison.OrdinalIgnoreCase)
                        ? w.Player.Serial
                        : CommandContext.ParseSerial(arg);

                    if (serial == 0)
                    {
                        return $"could not parse serial '{arg}'";
                    }

                    w.TargetManager.Target(serial);

                    return null;
                });

                ctx.Print(result ?? $"Targeted {arg}");
            });

            Register("canceltarget", "canceltarget", "Cancel a pending target cursor", ctx =>
            {
                ctx.Game(w => w.TargetManager.CancelTarget());
                ctx.Print("Target cancelled");
            });

            Register("get", "get <serial> [amount]", "Move an item into your backpack", ctx =>
            {
                if (!ctx.RequireInGame() || !TrySerial(ctx, 0, out uint serial))
                {
                    return;
                }

                ushort amount = ctx.ArgCount >= 2 && ushort.TryParse(ctx.Arg(1), out ushort a) ? a : (ushort)0;

                string error = ctx.Game(w =>
                {
                    var item = w.Items.Get(serial);

                    if (item == null)
                    {
                        return $"no item 0x{serial:X8} in view";
                    }

                    // GrabItem does the pick-up/drop pair and picks the backpack for us.
                    GameActions.GrabItem(w, serial, amount == 0 ? item.Amount : amount);

                    return null;
                });

                ctx.Print(error ?? $"Grabbed 0x{serial:X8}");

                if (error != null)
                {
                    ctx.Warn(error);
                }
            }, "pickup", "grab");

            Register("equip", "equip <serial>", "Equip an item from your backpack", ctx =>
            {
                if (!ctx.RequireInGame() || !TrySerial(ctx, 0, out uint serial))
                {
                    return;
                }

                // Equip() lifts the item and lets the client pick the layer from its tiledata,
                // which is what the paperdoll does on a drag - no hand-specified layer hex needed.
                string error = ctx.Game(w =>
                {
                    var item = w.Items.Get(serial);

                    if (item == null)
                    {
                        return $"no item 0x{serial:X8} in view";
                    }

                    GameActions.PickUp(w, serial, 0, 0, 1);

                    return null;
                });

                if (error != null)
                {
                    ctx.Warn(error);

                    return;
                }

                ctx.Sleep(200);
                ctx.Game(w => GameActions.Equip(w));
                ctx.Print($"Equipped 0x{serial:X8}");
            }, "wear");

            Register("drop", "drop <item> <container> [amount]", "Move an item into a container", ctx =>
            {
                if (!ctx.RequireInGame() || !TrySerial(ctx, 0, out uint item) || !TrySerial(ctx, 1, out uint container))
                {
                    return;
                }

                ushort amount = ctx.ArgCount >= 3 && ushort.TryParse(ctx.Arg(2), out ushort a) ? a : (ushort)0;

                ctx.Game(w => GameActions.PickUp(w, item, 0, 0, amount == 0 ? 1 : amount));
                ctx.Sleep(200);
                ctx.Game(_ => GameActions.DropItem(item, 0xFFFF, 0xFFFF, 0, container));
                ctx.Print($"Dropped 0x{item:X8} into 0x{container:X8}");
            }, "moveitem");

            Register("dropground", "dropground <item> [amount] [<x> <y> [z]]", "Drop an item on the ground - at your feet, or on a given tile", ctx =>
            {
                if (!ctx.RequireInGame() || !TrySerial(ctx, 0, out uint item))
                {
                    return;
                }

                ushort amount = ctx.ArgCount >= 2 && ushort.TryParse(ctx.Arg(1), out ushort a) ? a : (ushort)0;

                var (x, y, z) = ctx.Game(w => (w.Player.X, w.Player.Y, w.Player.Z));

                // An explicit tile, as the game window has by definition - a drag there drops
                // wherever the cursor is, which is rarely the tile the player stands on. Own-tile
                // drops are refused on some ground (and the refusal is silent: the item simply
                // reappears in the pack), so being able to aim one tile over is the difference
                // between shedding weight and being stuck with it.
                int coordArg = ctx.ArgCount >= 2 && ushort.TryParse(ctx.Arg(1), out _) ? 2 : 1;

                if (ctx.ArgCount >= coordArg + 2
                    && ushort.TryParse(ctx.Arg(coordArg), out ushort tx)
                    && ushort.TryParse(ctx.Arg(coordArg + 1), out ushort ty))
                {
                    x = (ushort)tx;
                    y = (ushort)ty;

                    if (ctx.ArgCount >= coordArg + 3 && sbyte.TryParse(ctx.Arg(coordArg + 2), out sbyte tz))
                    {
                        z = tz;
                    }
                }

                ctx.Game(w => GameActions.PickUp(w, item, 0, 0, amount == 0 ? 1 : amount));
                ctx.Sleep(200);
                // Container 0 (invalid) plus real world coordinates is how a ground drop differs
                // from the container-drop path above - that one hardcodes x/y since the server
                // places it into a slot itself, but the ground has no slot to auto-pick.
                //
                // Do not "fix" this to 0xFFFFFFFF to match GameSceneInputHandler: tried
                // 2026-09-13 and it changed nothing, because the bounce being chased was never
                // the container id. Ground drops work fine with 0 (a 22-piece bone dump went
                // through minutes earlier in the same session); what stops them is carrying
                // at or over the weight cap, which makes the server refuse *every* item move -
                // ground, container and equip alike - and silently hand the item back.
                ctx.Game(_ => GameActions.DropItem(item, x, y, z, 0));
                ctx.Print($"Dropped 0x{item:X8} on the ground at ({x}, {y}, {z})");
            });

            Register("skills", "skills", "List skills", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                ctx.Game(w => NetClient.Socket.Send_SkillsRequest(w.Player.Serial));

                // The reply is a packet, so there is nothing to await deterministically; give the
                // server a beat, then print whatever the client currently holds.
                ctx.Sleep(700);

                foreach (string line in ctx.Game(w =>
                         {
                             var lines = new List<string>();

                             foreach (var skill in w.Player.Skills.Where(s => s.Base > 0 || s.Value > 0)
                                          .OrderBy(s => s.Name, StringComparer.Ordinal))
                             {
                                 string lockState = skill.Lock switch
                                 {
                                     Lock.Up => "up",
                                     Lock.Down => "dn",
                                     _ => "locked"
                                 };

                                 lines.Add($"[SKILL] {skill.Name,-16} {skill.Value,6:F1} (base {skill.Base,5:F1}) {lockState}");
                             }

                             if (lines.Count == 0)
                             {
                                 lines.Add("No skills known yet");
                             }

                             return lines;
                         }))
                {
                    ctx.Print(line);
                }
            }, "sk");

            // Invoke a skill the way the skills gump's blue button does. Skills that need a
            // target (Arms Lore, Anatomy, Animal Lore, Taming, ...) open a cursor afterwards;
            // answer it with `target <serial>`. Passive skills cannot be invoked and the server
            // simply ignores the request.
            Register("useskill", "useskill <name|index>", "Use a skill by name (e.g. 'arms lore') or index; answer any target cursor with `target`", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                if (ctx.ArgCount < 1)
                {
                    ctx.Warn("Usage: useskill <name|index> - `skills` lists the names");
                    return;
                }

                string query = string.Join(" ", ctx.Args.Skip(1)).Trim();

                (int index, string name, string error) = ctx.Game<(int, string, string)>(w =>
                {
                    if (int.TryParse(query, out int idx))
                    {
                        var byIndex = w.Player.Skills.FirstOrDefault(s => s.Index == idx);
                        return byIndex != null ? (byIndex.Index, byIndex.Name, null) : (-1, null, $"No skill with index {idx}");
                    }

                    var exact = w.Player.Skills.FirstOrDefault(s => string.Equals(s.Name, query, StringComparison.OrdinalIgnoreCase));
                    if (exact != null)
                    {
                        return (exact.Index, exact.Name, null);
                    }

                    var prefix = w.Player.Skills.Where(s => s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (prefix.Count == 1)
                    {
                        return (prefix[0].Index, prefix[0].Name, null);
                    }

                    if (prefix.Count > 1)
                    {
                        return (-1, null, $"'{query}' matches {string.Join(", ", prefix.Select(s => s.Name))} - be more specific");
                    }

                    return (-1, null, $"No skill named '{query}' - `skills` lists them");
                });

                if (error != null)
                {
                    ctx.Warn(error);
                    return;
                }

                ctx.Game(_ => GameActions.UseSkill(index));
                ctx.Print($"Used skill {name} (index {index}) - if it needs a target, answer the cursor with `target <serial>`");
            }, "skill");

            // Cast by name across every spell school the client knows (magery 1-64, necromancy
            // 101+, chivalry 201+, bushido 401+, ninjitsu 501+, spellweaving 601+, mysticism
            // 678+, mastery 701+), or by that full index. The server decides whether it works -
            // spellbook, reagents, mana, skill - and says so in the log. A targeted spell opens a
            // cursor afterwards, answered with `target <serial|self>`.
            Register("cast", "cast <spell name|index>", "Cast a spell by name (e.g. 'cast heal') or full index; answer any target cursor with `target`", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                if (ctx.ArgCount < 1)
                {
                    ctx.Warn("Usage: cast <spell name|index>");
                    return;
                }

                string query = string.Join(" ", ctx.Args.Skip(1)).Trim();

                SpellDefinition spell = null;
                string error = null;

                if (int.TryParse(query, out int idx))
                {
                    spell = SpellDefinition.FullIndexGetSpell(idx);
                    if (spell == null || spell.ID == 0)
                    {
                        error = $"No spell with index {idx}";
                    }
                }
                else
                {
                    var all = new List<(int full, SpellDefinition def)>();
                    for (int i = 1; i < 800; i++)
                    {
                        var d = SpellDefinition.FullIndexGetSpell(i);
                        if (d != null && d.ID != 0 && !string.IsNullOrEmpty(d.Name) && d.ID == i)
                        {
                            all.Add((i, d));
                        }
                    }

                    var exact = all.Where(e => string.Equals(e.def.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
                    var hits = exact.Count > 0 ? exact
                             : all.Where(e => e.def.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();

                    if (hits.Count == 1)
                    {
                        spell = hits[0].def;
                    }
                    else if (hits.Count > 1)
                    {
                        error = $"'{query}' matches {string.Join(", ", hits.Select(h => $"{h.def.Name} ({h.full})"))} - be more specific";
                    }
                    else
                    {
                        error = $"No spell named '{query}'";
                    }
                }

                if (error != null)
                {
                    ctx.Warn(error);
                    return;
                }

                int full = spell.ID;
                ctx.Game(_ => GameActions.CastSpell(full));

                string needs = spell.TargetType == TargetType.Neutral ? "no target" : "a target cursor follows - answer it with `target <serial|self>`";
                ctx.Print($"Casting {spell.Name} (index {full}, \"{spell.PowerWords}\", {spell.ManaCost} mana) - {needs}");
            });

            Register("hp", "hp <serial>", "Request a mobile's status bar", ctx =>
            {
                if (!ctx.RequireInGame() || !TrySerial(ctx, 0, out uint serial))
                {
                    return;
                }

                ctx.Game(w => GameActions.RequestMobileStatus(w, serial, true));
                ctx.Print($"Status requested for 0x{serial:X8}");
            });

            Register("unequip", "unequip <serial>", "Move an equipped item to the backpack", ctx =>
            {
                if (!ctx.RequireInGame() || !TrySerial(ctx, 0, out uint serial))
                {
                    return;
                }

                uint backpack = ctx.Game(w => w.Player.FindItemByLayer(Layer.Backpack)?.Serial ?? 0);

                if (backpack == 0)
                {
                    ctx.Warn("no backpack");

                    return;
                }

                ctx.Game(w => GameActions.PickUp(w, serial, 0, 0, 1));
                ctx.Sleep(200);
                ctx.Game(_ => GameActions.DropItem(serial, 0xFFFF, 0xFFFF, 0, backpack));
                ctx.Print($"Unequipped 0x{serial:X8} into the backpack");
            }, "topack");
        }

        private void RegisterTrade()
        {
            Register("trades", "trades", "List open secure-trade windows", ctx =>
            {
                foreach (string line in ctx.Game(_ =>
                         {
                             var lines = new List<string>();

                             foreach (var trade in UIManager.Gumps.OfType<TradingGump>().Where(t => !t.IsDisposed))
                             {
                                 lines.Add(
                                     $"[TRADE] local 0x{trade.LocalSerial:X8} with {trade.Name} - " +
                                     $"my box 0x{trade.ID1:X8} (gold {trade.Gold}, plat {trade.Platinum}, " +
                                     $"{(trade.ImAccepting ? "accepted" : "not accepted")}) - " +
                                     $"their box 0x{trade.ID2:X8} (gold {trade.HisGold}, plat {trade.HisPlatinum}, " +
                                     $"{(trade.HeIsAccepting ? "accepted" : "not accepted")})");
                                 lines.Add("  use 'container <box serial>' to see items on either side");
                             }

                             if (lines.Count == 0)
                             {
                                 lines.Add("No secure-trade windows open");
                             }

                             return lines;
                         }))
                {
                    ctx.Print(line);
                }
            });

            // Toggles this side's accept checkbox - the server only completes the trade once both
            // sides have it checked, same as clicking the checkbox in the gump would.
            Register("accepttrade", "accepttrade [local serial]", "Check (or re-check) your accept box on an open trade", ctx =>
            {
                string error = ctx.Game(_ =>
                {
                    var trade = FindTrade(ctx);

                    if (trade == null)
                    {
                        return "no secure-trade window open";
                    }

                    GameActions.AcceptTrade(trade.ID1, true);

                    return null;
                });

                if (error != null)
                {
                    ctx.Warn(error);

                    return;
                }

                ctx.Print("Accept sent");
            });

            Register("canceltrade", "canceltrade [local serial]", "Cancel an open trade", ctx =>
            {
                string error = ctx.Game(_ =>
                {
                    var trade = FindTrade(ctx);

                    if (trade == null)
                    {
                        return "no secure-trade window open";
                    }

                    GameActions.CancelTrade(trade.ID1);

                    return null;
                });

                if (error != null)
                {
                    ctx.Warn(error);

                    return;
                }

                ctx.Print("Cancel sent");
            });
        }

        /// <summary>
        /// The trade window matching an optional local-serial argument, or the only/most recent one
        /// open when no argument is given - mirrors how 'gumpresponse' picks a target gump.
        /// </summary>
        private TradingGump FindTrade(CommandContext ctx)
        {
            var open = UIManager.Gumps.OfType<TradingGump>().Where(t => !t.IsDisposed);

            if (ctx.ArgCount >= 1 && TrySerial(ctx, 0, out uint serial))
            {
                return open.FirstOrDefault(t => t.LocalSerial == serial);
            }

            return open.FirstOrDefault();
        }

        /// <summary>
        /// Parses a variadic "&lt;serial&gt; &lt;amount&gt; [&lt;serial&gt; &lt;amount&gt; ...]" argument list into the
        /// item array both Send_BuyRequest and Send_SellRequest take. Both packets carry a whole
        /// cart, and the server closes the vendor's list once it has answered one - so batching is
        /// what keeps a multi-item purchase to a single round trip.
        /// </summary>
        /// <summary>Server dialogs: list them, press their buttons, close paperdolls.</summary>
        private void RegisterGumps()
        {
            Register("gumpresponse", "gumpresponse <button> [switch ...] [text:<id>=<value> ...] [gump:<serial>]", "Press a button on the open gump, with any radio/checkbox switch ids selected and any text entries filled", ctx =>
            {
                if (!int.TryParse(ctx.Arg(0), out int button))
                {
                    ctx.Warn("usage: gumpresponse <button> [switch ...] [gump:<serial>]   (switch ids are the [radio N]/[checkbox N] markers in 'gumps'; gump: picks one when several are open)");

                    return;
                }

                // Every further argument is a switch id to send as selected - exactly what the UI
                // sends when those radios/checkboxes are ticked and the button is clicked - except
                // gump:<serial>, which names the gump when more than one is open.
                var switches = new List<uint>();
                var entries = new List<Tuple<ushort, string>>();
                uint which = 0;

                for (int i = 1; ; i++)
                {
                    string arg = ctx.Arg(i);

                    if (arg == null)
                    {
                        break;
                    }

                    if (arg.StartsWith("gump:", StringComparison.OrdinalIgnoreCase))
                    {
                        which = CommandContext.ParseSerial(arg.Substring(5));

                        if (which == 0)
                        {
                            ctx.Warn($"gump: needs a hex serial from 'gumps', got '{arg}'");

                            return;
                        }

                        continue;
                    }

                    // text:<id>=<value> fills a text entry ([textentry N] in 'gumps'). Underscores
                    // in the value become spaces, since arguments split on whitespace.
                    if (arg.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = arg.IndexOf('=');

                        if (eq < 6 || !ushort.TryParse(arg.Substring(5, eq - 5), out ushort entryId))
                        {
                            ctx.Warn($"text entry must look like text:<id>=<value>, got '{arg}'");

                            return;
                        }

                        entries.Add(Tuple.Create(entryId, arg.Substring(eq + 1).Replace('_', ' ')));

                        continue;
                    }

                    if (!uint.TryParse(arg, out uint sw))
                    {
                        ctx.Warn($"switch id must be a number, got '{arg}'");

                        return;
                    }

                    switches.Add(sw);
                }

                string error = ctx.Game(_ =>
                {
                    var open = UIManager.Gumps.OfType<Gump>().Where(g => g.ServerSerial != 0 && !g.IsDisposed).ToList();

                    if (open.Count == 0)
                    {
                        return "no server gump open";
                    }

                    Gump gump;

                    if (which != 0)
                    {
                        gump = open.FirstOrDefault(g => g.ServerSerial == which || g.LocalSerial == which);

                        if (gump == null)
                        {
                            return $"no open server gump with serial 0x{which:X8} - see 'gumps'";
                        }
                    }
                    else if (open.Count == 1)
                    {
                        gump = open[0];
                    }
                    else
                    {
                        // Refuse rather than guess. Measured 2026-09-03: with the shard's global chat
                        // history gump open beside a moongate menu, the guess landed a moongate
                        // button on the chat gump and the server dropped the connection.
                        return $"{open.Count} server gumps are open - say which with gump:<serial>: " +
                               string.Join(", ", open.Select(g => $"0x{g.ServerSerial:X8} ({Narrator.DumpGumpText(g).FirstOrDefault() ?? g.GetType().Name})"));
                    }

                    GameActions.ReplyGump(gump.LocalSerial, gump.ServerSerial, button,
                                          switches.Count > 0 ? switches.ToArray() : null,
                                          entries.Count > 0 ? entries.ToArray() : null);
                    gump.Dispose();

                    return null;
                });

                if (error != null)
                {
                    ctx.Warn(error);

                    return;
                }

                ctx.Print($"Gump button {button} pressed"
                          + (switches.Count > 0 ? $" with switch(es) {string.Join(",", switches)}" : string.Empty)
                          + (entries.Count > 0 ? $" with text {string.Join(", ", entries.Select(e => $"{e.Item1}=\"{e.Item2}\""))}" : string.Empty));
            }, "gumpbtn");


            Register("gumps", "gumps", "List open server gumps", ctx =>
            {
                foreach (string line in ctx.Game(_ =>
                         {
                             var lines = new List<string>();

                             foreach (var gump in UIManager.Gumps.OfType<Gump>()
                                          .Where(g => g.ServerSerial != 0 && !g.IsDisposed))
                             {
                                 lines.Add($"[GUMP] local 0x{gump.LocalSerial:X8} server 0x{gump.ServerSerial:X8} {gump.GetType().Name}");

                                 foreach (string text in Narrator.DumpGumpText(gump))
                                 {
                                     lines.Add($"  {text}");
                                 }
                             }

                             if (lines.Count == 0)
                             {
                                 lines.Add("No server gumps open");
                             }

                             return lines;
                         }))
                {
                    ctx.Print(line);
                }
            });

            // Paperdolls are client-side gumps: `gumps` never lists them and `gumpresponse` has
            // nothing to press, so inspecting a crowd with `use <serial>` leaves one window per
            // person stacked on the screen with no way to clear them from the CLI. This is that way.

            Register("closepaperdolls", "closepaperdolls [serial]",
                     "Close open paperdoll gumps - all of them, or just one mobile's", ctx =>
            {
                uint only = CommandContext.ParseSerial(ctx.Arg(0));

                int closed = ctx.Game(_ =>
                {
                    var doomed = UIManager.Gumps.OfType<PaperDollGump>()
                                          .Where(g => !g.IsDisposed && (only == 0 || g.LocalSerial == only))
                                          .ToList();

                    foreach (var gump in doomed)
                    {
                        gump.Dispose();
                    }

                    return doomed.Count;
                });

                ctx.Print(closed == 0 ? "No paperdolls open" : $"Closed {closed} paperdoll(s)");
            }, "closepaperdoll", "closedolls");

        }

        private void RegisterInspection()
        {
            Register("mobiles", "mobiles [range]", "Nearby mobiles by distance", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                int range = ctx.ArgCount >= 1 && int.TryParse(ctx.Arg(0), out int r) ? r : 18;

                foreach (string line in ctx.Game(w =>
                         {
                             var lines = new List<string>();

                             foreach (var mobile in w.Mobiles.Values
                                          .Where(m => m.Serial != w.Player.Serial && m.Distance <= range)
                                          .OrderBy(m => m.Distance))
                             {
                                 lines.Add($"  0x{mobile.Serial:X8} {DescribeMobile(w, mobile),-28} " +
                                           $"d={mobile.Distance,-3} ({mobile.X}, {mobile.Y}, {mobile.Z}) " +
                                           $"{mobile.NotorietyFlag} hp={mobile.Hits}/{mobile.HitsMax}");
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
            }, "mobs", "m");

            Register("gear", "gear <serial|self>", "Equipped items of any mobile in view", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                string arg = ctx.Arg(0);
                bool self = arg == null || arg.Equals("self", StringComparison.OrdinalIgnoreCase);
                uint serial = 0;

                if (!self && !TrySerial(ctx, 0, out serial))
                {
                    return;
                }

                foreach (string line in ctx.Game(w =>
                         {
                             var lines = new List<string>();
                             Mobile mobile = self ? w.Player : w.Mobiles.Get(serial);

                             if (mobile == null)
                             {
                                 lines.Add($"no mobile 0x{serial:X8} in view");

                                 return lines;
                             }

                             // Mobile.Title is the paperdoll's fame/karma line ("the Glorious Lord",
                             // "the Scoundrel"). It arrives only in the 0x88 paperdoll packet, which
                             // the server sends in reply to a double-click - so it stays empty until
                             // `use <serial>` has been sent for that mobile at least once this
                             // session. Nothing else on the wire carries it: the single-click label
                             // is a different, shorter string (name + NPC job / guild tag).
                             string title = string.IsNullOrWhiteSpace(mobile.Title)
                                 ? " title=? (send 'use <serial>' first)"
                                 : $" \"{mobile.Title.Trim()}\"";

                             lines.Add($"[GEAR] 0x{mobile.Serial:X8} {DescribeMobile(w, mobile)}{title} " +
                                       $"d={mobile.Distance} {mobile.NotorietyFlag}:");

                             bool any = false;

                             foreach (var item in Children(mobile))
                             {
                                 any = true;
                                 lines.Add($"  [{item.Layer,-12}] 0x{item.Serial:X8} {DescribeItem(item)}");
                             }

                             if (!any)
                             {
                                 // Equipment arrives with the mobile's own packets when it comes into
                                 // view, so an empty list means nothing is worn - not that it is
                                 // unknown. A container's *contents* are the thing that needs opening.
                                 lines.Add("  (nothing equipped)");
                             }

                             return lines;
                         }))
                {
                    ctx.Print(line);
                }
            }, "equipment");

            Register("nearest", "nearest", "Closest mobile", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                ctx.Print(ctx.Game(w =>
                {
                    var mobile = w.Mobiles.Values
                        .Where(m => m.Serial != w.Player.Serial)
                        .OrderBy(m => m.Distance)
                        .FirstOrDefault();

                    return mobile == null
                        ? "No mobiles in view"
                        : $"Nearest: 0x{mobile.Serial:X8} {DescribeMobile(w, mobile)} " +
                          $"d={mobile.Distance} ({mobile.X}, {mobile.Y}, {mobile.Z}) {mobile.NotorietyFlag}";
                }));
            }, "nearestmob");

            Register("items", "items [range]", "Nearby ground items", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                int range = ctx.ArgCount >= 1 && int.TryParse(ctx.Arg(0), out int r) ? r : 18;

                foreach (string line in ctx.Game(w =>
                         {
                             var lines = new List<string>();

                             foreach (var item in w.Items.Values
                                          .Where(i => i.OnGround && i.Distance <= range)
                                          .OrderBy(i => i.Distance))
                             {
                                 string tag = IsPickupable(item) ? "[take] " : "[fixed]";

                                 lines.Add($"  {tag} 0x{item.Serial:X8} {DescribeItem(item),-34} " +
                                           $"d={item.Distance,-3} ({item.X}, {item.Y}, {item.Z})");
                             }

                             if (lines.Count == 0)
                             {
                                 lines.Add($"No ground items within {range} tiles");
                             }

                             return lines;
                         }))
                {
                    ctx.Print(line);
                }
            }, "i");

            Register("inv", "inv", "Equipped items and backpack contents", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                foreach (string line in ctx.Game(w =>
                         {
                             var lines = new List<string> { "Equipped:" };

                             foreach (var item in Children(w.Player))
                             {
                                 lines.Add($"  [{item.Layer,-12}] 0x{item.Serial:X8} {DescribeItem(item)}");
                             }

                             var backpack = w.Player.FindItemByLayer(Layer.Backpack);

                             if (backpack == null)
                             {
                                 lines.Add("No backpack");

                                 return lines;
                             }

                             lines.Add($"Backpack (0x{backpack.Serial:X8}):");

                             bool any = false;

                             foreach (var item in Children(backpack))
                             {
                                 any = true;
                                 lines.Add($"  0x{item.Serial:X8} {DescribeItem(item)}");
                             }

                             if (!any)
                             {
                                 lines.Add("  (empty, or not opened yet - try 'backpack')");
                             }

                             return lines;
                         }))
                {
                    ctx.Print(line);
                }
            }, "inventory");

            Register("props", "props <serial|self|layer>", "An item's property list (tooltip): weight, durability, damage, resists", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                string arg = ctx.Arg(0);

                if (string.IsNullOrWhiteSpace(arg))
                {
                    ctx.Warn("usage: props <serial>  |  props <layer>  (OneHanded, TwoHanded, Torso...)  |  props self");

                    return;
                }

                uint serial = CommandContext.ParseSerial(arg);
                bool self = arg.Equals("self", StringComparison.OrdinalIgnoreCase);
                Layer layer = Layer.Invalid;

                if (serial == 0 && !self && !Enum.TryParse(arg, true, out layer))
                {
                    ctx.Warn($"'{arg}' is not a serial, a layer name or 'self'");

                    return;
                }

                // The list is a cached server reply. First sight of an item queues the request; the
                // answer lands a round trip later, so give it a few short waits before concluding.
                const int TRIES = 8, WAIT_MS = 120;
                List<string> lines = null;

                for (int attempt = 0; attempt < TRIES; attempt++)
                {
                    lines = ctx.Game(w =>
                    {
                        uint target = serial;

                        if (self)
                        {
                            target = w.Player.Serial;
                        }
                        else if (target == 0)
                        {
                            var worn = w.Player.FindItemByLayer(layer);

                            if (worn == null)
                            {
                                return new List<string> { $"nothing equipped on layer {layer}" };
                            }

                            target = worn.Serial;
                        }

                        var entity = SerialHelper.IsMobile(target) ? (Entity)w.Mobiles.Get(target) : w.Items.Get(target);

                        if (entity == null)
                        {
                            return new List<string> { $"no item or mobile 0x{target:X8} known to the client" };
                        }

                        // Ask first: an item nobody has hovered has nothing cached yet, and this
                        // command is the one caller that wants the request sent.
                        ItemProps.Request(w, target);

                        var props = ItemProps.Lines(w, target);
                        var result = new List<string>();

                        if (props.Count == 0)
                        {
                            if (!ItemProps.Supported(w))
                            {
                                result.Add($"[PROPS] 0x{target:X8} {(entity is Item it ? DescribeItem(it) : DescribeMobile(w, (Mobile)entity))}: this shard sends no property lists (pre-AOS protocol)");
                            }

                            return result;   // empty = not yet, try again
                        }

                        result.Add($"[PROPS] 0x{target:X8} {props[0]}");

                        for (int i = 1; i < props.Count; i++)
                        {
                            result.Add($"  {props[i]}");
                        }

                        if (props.Count == 1)
                        {
                            result.Add("  (no properties beyond the name)");
                        }

                        return result;
                    });

                    if (lines.Count > 0)
                    {
                        break;
                    }

                    System.Threading.Thread.Sleep(WAIT_MS);
                }

                if (lines == null || lines.Count == 0)
                {
                    ctx.Warn($"no property list for {arg} after {TRIES * WAIT_MS} ms - the server has not answered (out of range, or it sends none for this item)");

                    return;
                }

                foreach (string line in lines)
                {
                    ctx.Print(line);
                }
            }, "tooltip");

            Register("container", "container <serial>", "List a container's contents", ctx =>
            {
                if (!ctx.RequireInGame() || !TrySerial(ctx, 0, out uint serial))
                {
                    return;
                }

                foreach (string line in ctx.Game(w =>
                         {
                             var lines = new List<string>();
                             var container = w.Items.Get(serial);

                             if (container == null)
                             {
                                 lines.Add($"no container 0x{serial:X8} in view");

                                 return lines;
                             }

                             lines.Add($"[CONTAINER] 0x{serial:X8} {DescribeItem(container)}:");

                             bool any = false;

                             foreach (var item in Children(container))
                             {
                                 any = true;
                                 string tag = item.ItemData.IsContainer ? "[container]" : "[item]     ";
                                 lines.Add($"  {tag} 0x{item.Serial:X8} {DescribeItem(item)}");
                             }

                             if (!any)
                             {
                                 lines.Add("  (empty, or not opened yet - try 'use <serial>')");
                             }

                             return lines;
                         }))
                {
                    ctx.Print(line);
                }
            }, "contents");

            Register("dead", "dead", "Whether you are a ghost", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                ctx.Print(ctx.Game(w => w.Player.IsDead ? "You are DEAD (ghost)" : "You are alive"));
            });
        }

        private static bool TrySerial(CommandContext ctx, int index, out uint serial)
        {
            serial = CommandContext.ParseSerial(ctx.Arg(index));

            if (serial != 0)
            {
                return true;
            }

            ctx.Warn($"expected a hex serial, got '{ctx.Arg(index) ?? "nothing"}'");

            return false;
        }

        /// <summary>
        /// Walks an entity's contained items. ClassicUO stores them as an intrusive linked list
        /// (Entity.Items is the first child, chained by Next), not as a collection.
        /// </summary>
        private static IEnumerable<Item> Children(Entity entity)
        {
            for (LinkedObject i = entity?.Items; i != null; i = i.Next)
            {
                if (i is Item item && !item.IsDestroyed)
                {
                    yield return item;
                }
            }
        }

        private static string DescribeMobile(World world, Mobile mobile) => Describe.MobileName(mobile);

        /// <summary>
        /// Best available name for an item. The server only sends names on request (single-click on
        /// this era's protocol, OPL on later ones), so fall back to the English name baked into
        /// tiledata.mul rather than showing a bare graphic id.
        /// </summary>
        /// <summary>
        /// Whether an item can actually be picked up, as opposed to being map dressing that only
        /// looks like an object on a ground scan - ruined walls, flagstones, fireplaces, and the
        /// like live in the same World.Items collection as real loot, and nothing else here tells
        /// them apart.
        ///
        /// This used to guess from tiledata's Weight (255 = "no weight", the convention static
        /// map art is normally given) - close, but wrong for a house/room decoration placed on top
        /// of an otherwise-ordinary graphic: the tiledata Weight for a chair is the same whether
        /// that particular chair is sitting loose or locked down as fixed decor, so the guess said
        /// "take" for one that the server then refused. The Movable bit in the item's own Flags is
        /// the real, per-instance signal - the server sets it directly (Entity.Flags, from the
        /// object-info packet), which is exactly the placement-specific fact a shared tiledata
        /// entry can never carry.
        /// </summary>
        private static bool IsPickupable(Item item) => (item.Flags & Flags.Movable) != 0;

        private static string DescribeItem(Item item)
        {
            // Name resolution is shared with the JSON writers so the two can never disagree about
            // what an item is called; only the display suffixes below are text-command specific.
            string name = Describe.ItemName(item);

            string suffix = item.Amount > 1 ? $" x{item.Amount}" : "";

            if (item.Hue != 0)
            {
                string hueName = HueName(item.Hue);

                suffix += hueName == null
                    ? $" (hue 0x{item.Hue:X4})"
                    : $" (hue 0x{item.Hue:X4} \"{hueName}\")";
            }

            return name + suffix;
        }

        /// <summary>
        /// The human-readable name hues.mul embeds for a hue index, if any. Most of the 20-byte
        /// name slots in the file are blank - only some ranges (mostly the dye-tub-selectable
        /// ones) were ever given one by the original data - so a miss here is normal, not a bug.
        /// </summary>
        private static unsafe string HueName(ushort hue)
        {
            if (hue == 0)
            {
                return null;
            }

            var hues = ClassicUO.Client.Game.UO.FileManager?.Hues;

            if (hues?.HuesRange == null)
            {
                return null;
            }

            int index = hue - 1;
            int group = index >> 3;
            int entry = index % 8;

            if (group < 0 || group >= hues.HuesRange.Length)
            {
                return null;
            }

            HuesBlock block = hues.HuesRange[group].Entries[entry];

            string name = Encoding.ASCII.GetString(block.Name, 20).TrimEnd('\0', ' ');

            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

    }
}
