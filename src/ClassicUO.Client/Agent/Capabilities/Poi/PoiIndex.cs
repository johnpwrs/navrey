// Points-of-interest lookups (see poi/*.csv and the conversion that produces poi/*.json).
// Entirely optional data: the directory and files may not exist, in which case callers
// should treat a null index as "no POI data available" rather than an error.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;


namespace ClassicUO.Agent.Capabilities.Poi
{
    internal class PoiEntry
    {
        public int X;
        public int Y;
        public int Z;
        public string Name = "";
        public string Category = "";
        public string Color = "";
        public bool Major;

        /// <summary>Facet (world.MapIndex) the point is on; -1 means "any facet" for data without one.</summary>
        public int Map = -1;

        public bool OnMap(int? map) => map == null || Map < 0 || Map == map.Value;

        public string MapName => PoiIndex.MapName(Map);

        public double DistanceTo(int x, int y)
        {
            double dx = X - x, dy = Y - y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    internal class PoiIndex
    {
        private static readonly string[] MapNames = { "Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "Ter Mur" };

        public static string MapName(int map) =>
            map >= 0 && map < MapNames.Length ? MapNames[map] : map < 0 ? "any facet" : $"map {map}";

        public List<PoiEntry> Points { get; } = new();
        public IReadOnlyList<string> Categories { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Loads every *.json file in <paramref name="dir"/> (shaped like the poi/*.json grid
        /// files: { cells: { "cx,cy": [ {x,y,z,name,category,color,major?}, ... ] } }) and
        /// flattens them into one list. Returns null (with a human-readable reason in
        /// <paramref name="message"/>) if the directory/files don't exist or nothing could be
        /// loaded — this is expected/optional, not an error condition for callers to surface loudly.
        /// </summary>
        public static PoiIndex? LoadDirectory(string dir, out string message)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                message = $"POI directory not found (optional, skipping): {dir}";
                return null;
            }

            var jsonFiles = Directory.GetFiles(dir, "*.json");
            if (jsonFiles.Length == 0)
            {
                message = $"No .json POI files found in: {dir}";
                return null;
            }

            var index = new PoiIndex();
            int filesLoaded = 0;
            foreach (var file in jsonFiles)
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    using var doc = JsonDocument.Parse(stream);
                    if (!doc.RootElement.TryGetProperty("cells", out var cellsElem)) continue;

                    foreach (var cellProp in cellsElem.EnumerateObject())
                    {
                        foreach (var pointElem in cellProp.Value.EnumerateArray())
                        {
                            index.Points.Add(new PoiEntry
                            {
                                X = pointElem.GetProperty("x").GetInt32(),
                                Y = pointElem.GetProperty("y").GetInt32(),
                                Z = pointElem.TryGetProperty("z", out var zEl) ? zEl.GetInt32() : 0,
                                Name = pointElem.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "",
                                Category = pointElem.TryGetProperty("category", out var cEl) ? cEl.GetString() ?? "" : "",
                                Color = pointElem.TryGetProperty("color", out var colEl) ? colEl.GetString() ?? "" : "",
                                Major = pointElem.TryGetProperty("major", out var mEl) && mEl.ValueKind == JsonValueKind.True,
                                Map = pointElem.TryGetProperty("map", out var mapEl) && mapEl.ValueKind == JsonValueKind.Number ? mapEl.GetInt32() : -1,
                            });
                        }
                    }
                    filesLoaded++;
                }
                catch (Exception ex)
                {
                    Output.Warn($"[POI] Failed to parse {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            if (index.Points.Count == 0)
            {
                message = $"POI directory found but no points loaded from {jsonFiles.Length} file(s) in: {dir}";
                return null;
            }

            index.Categories = index.Points
                .Select(p => p.Category)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            message = $"Loaded {index.Points.Count} POIs ({index.Categories.Count} categories) from {filesLoaded} file(s) in {dir}";
            return index;
        }

        /// <summary>
        /// Nearest point in the given category (case-insensitive) to (x,y), or null if none.
        /// When <paramref name="map"/> is given, only points on that facet (or facet-less points)
        /// are considered - a Trammel character asking for a bank must not be sent to Ilshenar.
        /// </summary>
        public PoiEntry? FindNearest(int x, int y, string category, int? map = null)
        {
            PoiEntry? best = null;
            double bestDist = double.MaxValue;
            foreach (var p in Points)
            {
                if (!p.OnMap(map)) continue;
                if (!string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase)) continue;
                double d = p.DistanceTo(x, y);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "in", "of", "the", "a", "an", "at", "near", "to", "for", "on"
        };

        /// <summary>
        /// Free-text search over POI names, e.g. "blacksmith in moonglow" or "bank in britain".
        /// Splits the query into words (dropping short/stop words), does a plain case-insensitive
        /// substring check per word against each point's Name, and returns the point matching the
        /// most query words. Ties are broken first by being on <paramref name="map"/> (so "britain
        /// bank" from Trammel is the Trammel one, not the Felucca twin), then by proximity to
        /// (originX, originY) when a position is given, otherwise by shorter/more specific name.
        /// With <paramref name="strictMap"/> other facets are excluded outright instead of merely
        /// losing ties. Null if nothing matches.
        /// </summary>
        public PoiEntry? FindByName(string query, out int matchedWordCount, out int totalQueryWordCount, int? originX = null, int? originY = null, int? map = null, bool strictMap = false)
        {
            var words = (query ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !StopWords.Contains(w))
                .Select(w => w.ToLowerInvariant())
                .Distinct()
                .ToList();

            totalQueryWordCount = words.Count;
            matchedWordCount = 0;
            if (words.Count == 0) return null;

            PoiEntry? best = null;
            int bestScore = 0;
            foreach (var p in Points)
            {
                if (strictMap && !p.OnMap(map)) continue;
                string nameLower = p.Name.ToLowerInvariant();
                int score = 0;
                foreach (var w in words) if (nameLower.Contains(w)) score++;
                if (score == 0) continue;

                bool better;
                if (score > bestScore)
                {
                    better = true;
                }
                else if (score == bestScore && best != null)
                {
                    bool pHere = p.OnMap(map), bestHere = best.OnMap(map);
                    if (pHere != bestHere)
                    {
                        better = pHere;
                    }
                    else
                    {
                        better = originX.HasValue && originY.HasValue
                            ? p.DistanceTo(originX.Value, originY.Value) < best.DistanceTo(originX.Value, originY.Value)
                            : p.Name.Length < best.Name.Length;
                    }
                }
                else
                {
                    better = false;
                }

                if (better) { bestScore = score; best = p; }
            }

            matchedWordCount = bestScore;
            return best;
        }
    }
}
