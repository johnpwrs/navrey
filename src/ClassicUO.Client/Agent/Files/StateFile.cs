// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Publishes the player's state to a JSON file, continuously, so callers can read it instead of
    /// asking for it.
    ///
    /// Every fact here is already resident in <see cref="World.Player"/> - the packet handlers keep
    /// it current. The only thing missing was a way for another process to see it: `navrey pos`
    /// costs a round trip through /tmp/cuocmd, the 200ms daemon poll, the game thread and
    /// /tmp/cuolog, occupies the single serialized command slot (so it queues behind a running
    /// `goto`), and hands back English that the caller has to re-parse. Reading a file costs none
    /// of that.
    ///
    /// Cadence is on-change plus a heartbeat: the snapshot is rebuilt every frame, but only written
    /// when it differs from the last one, or when HEARTBEAT_MS has passed. Standing still is ~4
    /// writes/sec rather than 60, and the file is still never more than a frame behind a change.
    ///
    /// The write itself happens on a background thread and lands via a rename, for two different
    /// reasons: the game loop must never block on the filesystem, and a reader running `jq` in a
    /// loop must never catch a half-written document.
    /// </summary>
    internal static class StateFile
    {
        public const string DEFAULT_STATE_FILE = "/tmp/cuostate.json";

        private static readonly object _lock = new();

        /// <summary>Reused across frames; the snapshot is built and discarded every frame.</summary>
        private static readonly ArrayBufferWriter<byte> _buffer = new(4096);

        /// <summary>Set from -statefile before <see cref="Start"/>.</summary>
        private static string _configuredPath;

        private static JsonFileWriter _writer;

        public static string Path => _writer?.Path ?? _configuredPath ?? DEFAULT_STATE_FILE;

        /// <summary>Records the path chosen by -statefile. Safe before Start.</summary>
        public static void Configure(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _configuredPath = path;
            }
        }

        public static void Start()
        {
            lock (_lock)
            {
                _writer ??= new JsonFileWriter(_configuredPath ?? DEFAULT_STATE_FILE, "state file");
            }
        }

        public static void Stop()
        {
            JsonFileWriter writer;

            lock (_lock)
            {
                writer = _writer;
                _writer = null;
            }

            writer?.Dispose();
        }

        /// <summary>
        /// Called once per frame from <see cref="AgentHost.PumpGameThread"/>, on the game thread.
        ///
        /// Note this runs before GameController.Update parses this frame's packets, so the snapshot
        /// reflects the end of the previous frame - one iteration, ~1-2ms, behind. That is far
        /// inside the noise of anything reading the file.
        /// </summary>
        public static void Update(World world)
        {
            var writer = _writer;

            if (writer == null || !writer.Running)
            {
                return;
            }

            try
            {
                Build(world, writer);
            }
            catch (Exception ex)
            {
                // A snapshot is never worth breaking the game loop for. Narrator.Wrap takes the
                // same stance for the same reason.
                writer.Fail($"state snapshot failed: {ex.Message}");
            }
        }

        private static void Build(World world, JsonFileWriter writer)
        {
            _buffer.Clear();

            using (var json = new Utf8JsonWriter(_buffer))
            {
                json.WriteStartObject();

                if (world?.InGame == true && world.Player != null)
                {
                    WritePlayer(json, world);
                }
                else
                {
                    // Deliberately still a document: a consumer must be able to tell "client up, at
                    // the login screen" from "no client at all", and the latter looks like a missing
                    // file or a stale updatedAtMs.
                    json.WriteBoolean("inGame", false);
                }

                json.WriteEndObject();
            }

            writer.Publish(_buffer.WrittenSpan);
        }

        private static void WritePlayer(Utf8JsonWriter json, World world)
        {
            PlayerMobile p = world.Player;

            json.WriteBoolean("inGame", true);

            json.WriteString("charID", Hex(p.Serial));
            json.WriteString("charName", p.Name ?? string.Empty);

            // Empty until a single-click or OPL reply has come back; reported as-is rather than
            // synthesized from the name.
            json.WriteString("title", p.Title ?? string.Empty);
            json.WriteString("sex", p.IsFemale ? "female" : "male");
            json.WriteString("race", RaceName(p));

            json.WriteNumber("charPosX", p.X);
            json.WriteNumber("charPosY", p.Y);
            json.WriteNumber("charPosZ", p.Z);
            json.WriteNumber("map", world.MapIndex);

            // Direction.ToString() is unusable - the diagonals are named Right/Down/Left/Up and Up
            // shares its value with Mask, so north-west prints as "Mask".
            json.WriteString("charDir", Directions.Name(p.Direction));
            json.WriteNumber("graphic", p.Graphic);
            json.WriteNumber("hue", p.Hue);

            json.WriteBoolean("charGhost", p.IsDead);
            json.WriteBoolean("isHidden", p.IsHidden);
            json.WriteBoolean("isPoisoned", p.IsPoisoned);
            json.WriteBoolean("isParalyzed", p.IsParalyzed);
            json.WriteBoolean("isFlying", p.IsFlying);
            json.WriteBoolean("isMounted", p.IsMounted);
            WriteMount(json, p);
            json.WriteBoolean("isDrivingBoat", p.IsDrivingBoat);
            json.WriteBoolean("isRunning", p.IsRunning);
            json.WriteBoolean("isRenamable", p.IsRenamable);
            json.WriteBoolean("isHuman", p.IsHuman);
            json.WriteBoolean("isGargoyle", p.IsGargoyle);
            json.WriteBoolean("isYellowHits", p.IsYellowHits);
            json.WriteBoolean("ignoreCharacters", p.IgnoreCharacters);
            json.WriteBoolean("warMode", p.InWarMode);
            json.WriteNumber("flagsRaw", (byte)p.Flags);
            json.WriteString("notoriety", p.NotorietyFlag.ToString());
            json.WriteString("speedMode", p.SpeedMode.ToString());

            json.WriteNumber("hits", p.Hits);
            json.WriteNumber("maxHits", p.HitsMax);

            // Entity.HitsPercentage is only maintained via UpdateHits() for *other* mobiles; for the
            // player Hits/HitsMax are exact, so derive it rather than reporting a stale byte.
            json.WriteNumber("hitsPercentage", p.HitsMax > 0 ? p.Hits * 100 / p.HitsMax : 0);

            json.WriteNumber("stamina", p.Stamina);
            json.WriteNumber("maxStam", p.StaminaMax);
            json.WriteNumber("mana", p.Mana);
            json.WriteNumber("maxMana", p.ManaMax);

            json.WriteNumber("str", p.Strength);
            json.WriteString("strLock", p.StrLock.ToString());
            json.WriteNumber("strengthIncrease", p.StrengthIncrease);
            json.WriteNumber("dex", p.Dexterity);
            json.WriteString("dexLock", p.DexLock.ToString());
            json.WriteNumber("dexterityIncrease", p.DexterityIncrease);
            json.WriteNumber("int", p.Intelligence);
            json.WriteString("intLock", p.IntLock.ToString());
            json.WriteNumber("intelligenceIncrease", p.IntelligenceIncrease);
            json.WriteNumber("statsCap", p.StatsCap);

            json.WriteNumber("luck", p.Luck);
            json.WriteNumber("weight", p.Weight);
            json.WriteNumber("maxWeight", p.WeightMax);
            json.WriteNumber("minDmg", p.DamageMin);
            json.WriteNumber("maxDmg", p.DamageMax);
            json.WriteNumber("damageIncrease", p.DamageIncrease);
            json.WriteNumber("gold", p.Gold);
            json.WriteNumber("tithingPoints", p.TithingPoints);
            json.WriteNumber("followers", p.Followers);
            json.WriteNumber("maxFollowers", p.FollowersMax);

            // There is no Backpack property; FindItemByLayer is the idiom the rest of the client
            // uses. Null until the server has sent the equipment.
            Item backpack = p.FindItemByLayer(Layer.Backpack);

            if (backpack != null)
            {
                json.WriteString("backpackID", Hex(backpack.Serial));
            }
            else
            {
                json.WriteNull("backpackID");
            }

            json.WriteNumber("hitChanceIncrease", p.HitChanceIncrease);
            json.WriteNumber("defenseChanceIncrease", p.DefenseChanceIncrease);
            json.WriteNumber("maxDefenseChanceIncrease", p.MaxDefenseChanceIncrease);
            json.WriteNumber("swingSpeedIncrease", p.SwingSpeedIncrease);
            json.WriteNumber("spellDamageIncrease", p.SpellDamageIncrease);
            json.WriteNumber("fasterCasting", p.FasterCasting);
            json.WriteNumber("fasterCastRecovery", p.FasterCastRecovery);
            json.WriteNumber("lowerManaCost", p.LowerManaCost);
            json.WriteNumber("lowerReagentCost", p.LowerReagentCost);
            json.WriteNumber("enhancePotions", p.EnhancePotions);
            json.WriteNumber("reflectPhysicalDamage", p.ReflectPhysicalDamage);

            json.WriteNumber("hitPointsRegeneration", p.HitPointsRegeneration);
            json.WriteNumber("hitPointsIncrease", p.HitPointsIncrease);
            json.WriteNumber("maxHitPointsIncrease", p.MaxHitPointsIncrease);
            json.WriteNumber("manaRegeneration", p.ManaRegeneration);
            json.WriteNumber("manaIncrease", p.ManaIncrease);
            json.WriteNumber("maxManaIncrease", p.MaxManaIncrease);
            json.WriteNumber("staminaRegeneration", p.StaminaRegeneration);
            json.WriteNumber("staminaIncrease", p.StaminaIncrease);
            json.WriteNumber("maxStaminaIncrease", p.MaxStaminaIncrease);

            // Note the client's own spelling: the base fields are ...Resistance, the caps are
            // ...Resistence. Kept as declared so the JSON matches what the source says.
            json.WriteNumber("physicalResistance", p.PhysicalResistance);
            json.WriteNumber("maxPhysicResistence", p.MaxPhysicResistence);
            json.WriteNumber("fireResistance", p.FireResistance);
            json.WriteNumber("maxFireResistence", p.MaxFireResistence);
            json.WriteNumber("coldResistance", p.ColdResistance);
            json.WriteNumber("maxColdResistence", p.MaxColdResistence);
            json.WriteNumber("poisonResistance", p.PoisonResistance);
            json.WriteNumber("maxPoisonResistence", p.MaxPoisonResistence);
            json.WriteNumber("energyResistance", p.EnergyResistance);
            json.WriteNumber("maxEnergyResistence", p.MaxEnergyResistence);

            json.WriteNumber("deathScreenTimer", p.DeathScreenTimer);

            WriteEquipped(json, p);
            WriteBackpack(json, backpack);

            WriteCursorTarget(json, world);
            WriteLastAttack(json, world);
            WriteNav(json);
        }

        /// <summary>
        /// What we are riding, by name - `"mount": "Horse"` rather than `item 0x3EA2`.
        ///
        /// Mounting deletes the animal's *mobile* and replaces it with an item on the Mount layer
        /// carrying a fresh item serial, so `isPet` in the world file has nothing to match while
        /// riding and the ridden animal's own serial is simply not on the wire. The name is
        /// recoverable though: `Mounts` maps the mount-item graphic to the body graphic it stands
        /// for (0x3EA2 -> 0x00CC), and that is the same body-graphic table `MobNames` is keyed by.
        ///
        /// Omitted entirely when not mounted, so `.mount` is null rather than an empty string.
        /// </summary>
        private static void WriteMount(Utf8JsonWriter json, PlayerMobile p)
        {
            Item mount = p.FindItemByLayer(Layer.Mount);

            if (mount == null || !p.IsMounted)
            {
                return;
            }

            string name = Mounts.TryGet(mount.Graphic, out var info)
                ? Capabilities.MobNames.Get(info.Graphic)
                : $"Unknown(0x{mount.Graphic:X4})";

            json.WriteString("mount", name);
            json.WriteString("mountItem", $"0x{mount.Serial:X8}");
        }

        /// <summary>Everything worn, keyed by layer - the paperdoll as data.</summary>
        private static void WriteEquipped(Utf8JsonWriter json, PlayerMobile p)
        {
            json.WriteStartArray("equipped");

            for (LinkedObject i = p.Items; i != null; i = i.Next)
            {
                if (i is Item item)
                {
                    WriteItem(json, item, layer: item.Layer.ToString());
                }
            }

            json.WriteEndArray();
        }

        /// <summary>
        /// The backpack's immediate contents.
        ///
        /// Empty until the backpack has been double-clicked open once this session: containers are
        /// never populated for free, the server only sends contents on request. That is a real trap
        /// - an empty array here means "not opened yet" at least as often as it means "nothing in
        /// there" - so `backpackOpened` states which it is rather than leaving callers to guess.
        ///
        /// One level deep only. A container inside the backpack appears as an item; its own contents
        /// need their own open, and belong to whatever asked for them.
        /// </summary>
        private static void WriteBackpack(Utf8JsonWriter json, Item backpack)
        {
            bool opened = backpack != null && backpack.Items != null;

            json.WriteBoolean("backpackOpened", opened);
            json.WriteStartArray("backpack");

            if (backpack != null)
            {
                for (LinkedObject i = backpack.Items; i != null; i = i.Next)
                {
                    if (i is Item item)
                    {
                        WriteItem(json, item, layer: null);
                    }
                }
            }

            json.WriteEndArray();
        }

        /// <summary>
        /// One item as data rather than as a display string. Amount and hue are their own fields
        /// here; the text commands append them to the name, which is exactly the parsing this is
        /// meant to replace.
        /// </summary>
        private static void WriteItem(Utf8JsonWriter json, Item item, string layer)
        {
            json.WriteStartObject();

            json.WriteString("serial", Hex(item.Serial));
            json.WriteString("name", Describe.ItemName(item));
            json.WriteNumber("graphic", item.Graphic);
            json.WriteNumber("amount", item.Amount);
            json.WriteNumber("hue", item.Hue);

            if (layer != null)
            {
                json.WriteString("layer", layer);
            }

            // The tooltip lines (weight, durability, damage, resists...). Present only when the
            // shard sends them and they have arrived; absent otherwise, so a reader can tell "no
            // such data on this shard" from "an item with no properties". See ItemProps.
            var props = ItemProps.Lines(ClassicUO.Client.Game.UO.World, item.Serial);

            if (props.Count > 1)
            {
                json.WriteStartArray("props");

                // Line 0 is the name, already carried by "name".
                for (int i = 1; i < props.Count; i++)
                {
                    json.WriteStringValue(props[i]);
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        /// <summary>
        /// The client's last cursor target (TargetManager.LastTargetInfo): the last thing a spell or
        /// skill cursor was pointed at. Attacking does not set it, so it is `none` in a session that
        /// only attacks; `lastAttack` covers that side.
        ///
        /// An entity's position is resolved live from the world rather than read back from the
        /// record, both because LastTargetInfo.SetEntity deliberately stores X = Y = 0xFFFF for an
        /// entity, and because a mobile has almost certainly moved since it was targeted.
        /// </summary>
        private static void WriteCursorTarget(Utf8JsonWriter json, World world)
        {
            var last = world.TargetManager?.LastTargetInfo;

            json.WriteStartObject("lastCursorTarget");

            if (last == null || IsUntargeted(last))
            {
                json.WriteString("kind", "none");
                json.WriteEndObject();

                return;
            }

            if (last.IsEntity)
            {
                json.WriteString("kind", "entity");
                json.WriteString("serial", Hex(last.Serial));

                WriteEntityPosition(json, world.Get(last.Serial));
            }
            else
            {
                json.WriteString("kind", last.IsStatic ? "static" : "land");
                json.WriteNull("serial");
                json.WriteNull("name");
                json.WriteNull("species");
                json.WriteNumber("graphic", last.Graphic);
                json.WriteNumber("x", last.X);
                json.WriteNumber("y", last.Y);
                json.WriteNumber("z", last.Z);
                json.WriteBoolean("exists", true);
            }

            json.WriteEndObject();
        }

        /// <summary>
        /// Distinguishes "nothing has ever been targeted" from a real land target.
        ///
        /// There are two such states, because LastTargetInfo starts life all-zero and only reaches
        /// its 0xFFFF sentinel once Clear() has run. Neither is a land target: untouched reads as
        /// graphic 0 at (0, 0), which IsLand happily reports as the north-west corner of the map -
        /// a tile no character can stand on, and not somewhere the player targeted.
        /// </summary>
        private static bool IsUntargeted(LastTargetInfo last)
        {
            if (last.IsEntity)
            {
                return false;
            }

            bool cleared = last.Graphic == 0xFFFF && last.X == 0xFFFF;
            bool untouched = last.Graphic == 0 && last.X == 0 && last.Y == 0;

            return cleared || untouched;
        }

        /// <summary>The mobile the player last attacked (TargetManager.LastAttack).</summary>
        private static void WriteLastAttack(Utf8JsonWriter json, World world)
        {
            uint serial = world.TargetManager?.LastAttack ?? 0;

            json.WriteStartObject("lastAttack");

            if (!SerialHelper.IsValid(serial))
            {
                json.WriteString("kind", "none");
                json.WriteEndObject();

                return;
            }

            json.WriteString("kind", "entity");
            json.WriteString("serial", Hex(serial));

            Entity target = world.Get(serial);

            WriteEntityPosition(json, target);
            WriteEntityHits(json, target);

            json.WriteEndObject();
        }

        /// <summary>
        /// The current or most recent walk. `goto` is fire and forget - it returns as soon as the
        /// walk starts - so this is where the outcome actually lands.
        ///
        /// Position cannot substitute for it. "still walking", "arrived" and "gave up, no path" all
        /// look identical from coordinates alone: a number that may or may not be changing. Only
        /// `status` separates them, and only `active` says whether waiting longer could still help.
        /// </summary>
        private static void WriteNav(Utf8JsonWriter json)
        {
            json.WriteStartObject("nav");

            json.WriteBoolean("active", Navigator.Active);
            json.WriteString("status", Navigator.Status.ToString().ToLowerInvariant());

            if (Navigator.Status == Navigator.NavStatus.Idle)
            {
                json.WriteNull("target");
                json.WriteNull("reason");
                json.WriteEndObject();

                return;
            }

            json.WriteStartArray("target");
            json.WriteNumberValue(Navigator.TargetX);
            json.WriteNumberValue(Navigator.TargetY);
            json.WriteEndArray();

            if (Navigator.Reason == null)
            {
                json.WriteNull("reason");
            }
            else
            {
                json.WriteString("reason", Navigator.Reason);
            }

            json.WriteEndObject();
        }

        private static void WriteEntityHits(Utf8JsonWriter json, Entity entity)
        {
            if (entity == null)
            {
                json.WriteNull("hits");
                json.WriteNull("maxHits");

                return;
            }

            json.WriteNumber("hits", entity.Hits);
            json.WriteNumber("maxHits", entity.HitsMax);
        }

        /// <summary>
        /// Position and name for a resolved entity, or nulls with exists:false when it has left the
        /// client's view - which is honest, and distinguishable from never having had a target.
        /// </summary>
        private static void WriteEntityPosition(Utf8JsonWriter json, Entity entity)
        {
            if (entity == null)
            {
                json.WriteNull("name");
                json.WriteNull("species");
                json.WriteNull("graphic");
                json.WriteNull("x");
                json.WriteNull("y");
                json.WriteNull("z");
                json.WriteBoolean("exists", false);

                return;
            }

            json.WriteString("name", entity.Name ?? string.Empty);

            // Same pairing as the world file's mobiles: `name` is whatever the server sent,
            // `species` is the body graphic as a word. A target carrying a personal name says
            // nothing about what it is, and this is the only field that does.
            //
            // Mobiles only. MobNames is keyed by *body* graphic, so running an item's graphic
            // through it returns an unrelated creature - the same class of mistake as reading an
            // item name out of tiledata for a mobile, which is where "Stone Arch" horses came from
            // (Narrator.cs). An item target has no species, and null says so.
            string species = entity is Mobile ? Capabilities.MobNames.TryGet(entity.Graphic) : null;

            if (species == null)
            {
                json.WriteNull("species");
            }
            else
            {
                json.WriteString("species", species);
            }

            json.WriteNumber("graphic", entity.Graphic);
            json.WriteNumber("x", entity.X);
            json.WriteNumber("y", entity.Y);
            json.WriteNumber("z", entity.Z);
            json.WriteBoolean("exists", true);
        }

        /// <summary>
        /// Race is only assigned by the 0x11 status handler for packet type 5 and up (ML), so on a
        /// pre-ML shard it reads 0 - not a valid RaceType. Fall back to the graphic-range checks
        /// rather than emitting a meaningless "0".
        /// </summary>
        private static string RaceName(PlayerMobile p)
        {
            if (Enum.IsDefined(typeof(RaceType), p.Race))
            {
                return p.Race.ToString();
            }

            return p.IsGargoyle ? "GARGOYLE" : "HUMAN";
        }

        private static string Hex(uint serial) => $"0x{serial:X8}";
    }
}
