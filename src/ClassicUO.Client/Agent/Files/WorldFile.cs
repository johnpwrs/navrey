// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers;
using System.Linq;
using System.Text.Json;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Publishes what is around the character - nearby mobiles and ground items - to a JSON file,
    /// the same way <see cref="StateFile"/> publishes the character itself.
    ///
    /// These are the three reads that appear in every automation loop (`mobiles`, `items`, and the
    /// backpack half of `inv`), and as commands each costs a round trip through the command file,
    /// the serial worker and the log, then has to be recovered from padded English by anchoring on
    /// trailing `d=` tokens because item names contain spaces, parentheses and quotes. As a file
    /// they cost a single read of a few kilobytes and no parsing at all.
    ///
    /// Filters mirror the `mobiles` and `items` commands - same range default, same ordering, same
    /// Movable test - so the file and the commands can never disagree about what is nearby. The one
    /// difference is deliberate: ground items are limited to movable ones, making this the `items`
    /// command's [take] subset rather than all of it. Names come from <see cref="Describe"/> for
    /// the same reason.
    ///
    /// A mobile that leaves the client's view disappears from this file rather than going stale.
    /// The file states what the client currently knows and nothing more; continuity for the one
    /// entity that matters lives in the state file's `lastAttack` / `lastCursorTarget`, which report
    /// `exists: false` instead of vanishing.
    /// </summary>
    internal static class WorldFile
    {
        public const string DEFAULT_WORLD_FILE = "/tmp/cuoworld.json";

        /// <summary>Matches the `mobiles` and `items` commands' own default.</summary>
        private const int RANGE_TILES = 18;

        private static readonly object _lock = new();
        private static readonly ArrayBufferWriter<byte> _buffer = new(8192);

        private static string _configuredPath;
        private static JsonFileWriter _writer;

        public static string Path => _writer?.Path ?? _configuredPath ?? DEFAULT_WORLD_FILE;

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
                _writer ??= new JsonFileWriter(_configuredPath ?? DEFAULT_WORLD_FILE, "world file");
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

        /// <summary>Called once per frame from <see cref="AgentHost.PumpGameThread"/>.</summary>
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
                writer.Fail($"world snapshot failed: {ex.Message}");
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
                    json.WriteBoolean("inGame", true);
                    json.WriteNumber("range", RANGE_TILES);

                    WriteMobiles(json, world);
                    WriteItems(json, world);
                    WriteContainers(json, world);
                }
                else
                {
                    json.WriteBoolean("inGame", false);
                }

                json.WriteEndObject();
            }

            writer.Publish(_buffer.WrittenSpan);
        }

        private static void WriteMobiles(Utf8JsonWriter json, World world)
        {
            json.WriteStartArray("mobiles");

            foreach (var mobile in world.Mobiles.Values
                         .Where(m => m.Serial != world.Player.Serial && m.Distance <= RANGE_TILES)
                         .OrderBy(m => m.Distance))
            {
                json.WriteStartObject();

                json.WriteString("serial", $"0x{mobile.Serial:X8}");
                json.WriteString("name", Describe.MobileName(mobile));
                json.WriteNumber("graphic", mobile.Graphic);

                // The body graphic as a word. `name` is whatever the *server* sent, and not every
                // mobile of a type is named after its type - one may carry a personal name, with
                // the type visible only in the graphic. So a name-only filter cannot see that
                // "Gruuk" is an orc, while this can: it is the same table `knownMonster` uses,
                // resolved forward here so nothing downstream needs its own copy of it.
                //
                // Null, not a placeholder, when the table has no entry - see MobNames.TryGet.
                string species = Capabilities.MobNames.TryGet(mobile.Graphic);

                if (species == null)
                {
                    json.WriteNull("species");
                }
                else
                {
                    json.WriteString("species", species);
                }

                json.WriteNumber("x", mobile.X);
                json.WriteNumber("y", mobile.Y);
                json.WriteNumber("z", mobile.Z);
                json.WriteNumber("distance", mobile.Distance);
                json.WriteString("notoriety", mobile.NotorietyFlag.ToString());

                // Notoriety alone can't separate animals from monsters that will fight: both read
                // Gray while passive, and a monster (a shade, among others) can stay Gray right up
                // until the moment it commits to attacking - by then it may already be adjacent.
                // The graphic id is known the instant the mobile is seen, well before any of that,
                // so it is the only signal available in advance rather than after the fact.
                json.WriteBoolean("knownMonster", Capabilities.MobNames.IsAggressive(mobile.Graphic));

                // As with the state file's target blocks, another mobile's HP is only what the
                // server has sent for it - a placeholder until something asks. `hp <serial>` primes
                // it; after that it tracks.
                json.WriteNumber("hits", mobile.Hits);
                json.WriteNumber("maxHits", mobile.HitsMax);

                json.WriteBoolean("isDead", mobile.IsDead);
                json.WriteBoolean("isHuman", mobile.IsHuman);
                json.WriteBoolean("inWarMode", mobile.InWarMode);

                // Which creature is *ours*. `followers` in the state file is only a count, so with
                // nothing here a bought horse is indistinguishable from the strays milling around
                // the stable that sold it. The server sets the rename flag for creatures under our
                // control and nothing else, which makes it the one owner signal on the wire.
                json.WriteBoolean("isPet", mobile.IsRenamable);

                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        /// <summary>
        /// Whether an item is real loot rather than map dressing.
        ///
        /// Ruined walls, flagstones, bulletin boards and barrels live in the same World.Items
        /// collection as anything you could pick up, and in a town they outnumber it heavily - the
        /// unfiltered list here was 125 entries and 18KB, almost all of it scenery no script will
        /// ever act on.
        ///
        /// The per-instance Movable bit is the right test, not a tiledata guess: the same chair
        /// graphic is takeable loose and refused when locked down as decor, and only the server's
        /// own flag (set from the object-info packet) knows which this one is. It is the same test
        /// the `items` command uses to print [take] versus [fixed], so this file is exactly that
        /// command's [take] subset.
        /// </summary>
        private static bool IsMovable(Item item) => (item.Flags & Flags.Movable) != 0;

        private static void WriteItems(Utf8JsonWriter json, World world)
        {
            json.WriteStartArray("items");

            foreach (var item in world.Items.Values
                         .Where(i => i.OnGround && i.Distance <= RANGE_TILES && IsMovable(i))
                         .OrderBy(i => i.Distance))
            {
                json.WriteStartObject();

                json.WriteString("serial", $"0x{item.Serial:X8}");
                json.WriteString("name", Describe.ItemName(item));
                json.WriteNumber("graphic", item.Graphic);
                json.WriteNumber("amount", item.Amount);
                json.WriteNumber("hue", item.Hue);
                json.WriteNumber("x", item.X);
                json.WriteNumber("y", item.Y);
                json.WriteNumber("z", item.Z);
                json.WriteNumber("distance", item.Distance);

                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        /// <summary>
        /// Whether an item can be opened, regardless of whether it can be carried.
        ///
        /// A corpse (graphic 0x2006) is a container by this same client-side tiledata flag, so it
        /// needs no special-casing to appear here - it just isn't Movable (see IsMovable above),
        /// which is exactly why it never appears in the `items` list a script would otherwise
        /// have to search. This is what makes finding a kill's corpse a JSON read instead of
        /// scraping the `items` command's text for a `[fixed] ... corpse ...` row.
        /// </summary>
        private static bool IsContainer(Item item) => item.ItemData.IsContainer;

        private static void WriteContainers(Utf8JsonWriter json, World world)
        {
            json.WriteStartArray("containers");

            foreach (var item in world.Items.Values
                         .Where(i => i.OnGround && i.Distance <= RANGE_TILES && IsContainer(i))
                         .OrderBy(i => i.Distance))
            {
                json.WriteStartObject();

                json.WriteString("serial", $"0x{item.Serial:X8}");
                json.WriteString("name", Describe.ItemName(item));
                json.WriteNumber("graphic", item.Graphic);
                json.WriteNumber("x", item.X);
                json.WriteNumber("y", item.Y);
                json.WriteNumber("z", item.Z);
                json.WriteNumber("distance", item.Distance);
                json.WriteBoolean("isCorpse", item.Graphic == 0x2006);
                json.WriteBoolean("isContainer", true);   // trivially true by list membership -
                                                           // written anyway so the field means the
                                                           // same thing here as it does on an item
                                                           // found via the `container` command

                json.WriteEndObject();
            }

            json.WriteEndArray();
        }
    }
}
