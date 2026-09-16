// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicUO.Agent.Capabilities.Poi;

namespace ClassicUO.Agent.Capabilities
{
    /// <summary>
    /// Point-of-interest lookups: "where is the nearest bank", "find the <city> blacksmith".
    ///
    /// This is our own data, not something ClassicUO knows about - the shard's landmarks come
    /// from the grid-indexed *.json in the directory settings.json's "poi_directory" names, and
    /// nothing ships with the client (the uo-poi-data skill builds it). Unlike the old headless
    /// client, which needed an explicit `loadpoi`, the index loads once on first use.
    /// </summary>
    internal static class PoiCommands
    {
        private static PoiIndex _index;
        private static bool _attempted;
        private static string _loadMessage;

        public static PoiIndex Index
        {
            get
            {
                EnsureLoaded();

                return _index;
            }
        }

        public static string LoadMessage
        {
            get
            {
                EnsureLoaded();

                return _loadMessage;
            }
        }

        public static IReadOnlyList<string> Categories =>
            Index?.Categories ?? (IReadOnlyList<string>)Array.Empty<string>();

        /// <summary>Forces a reload, e.g. after editing the data files.</summary>
        public static void Reload()
        {
            _attempted = false;
            _index = null;

            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_attempted)
            {
                return;
            }

            _attempted = true;

            string dir = FindDataDirectory();

            if (dir == null)
            {
                string configured = Settings.GlobalSettings.PoiDirectory;
                _loadMessage = string.IsNullOrWhiteSpace(configured)
                    ? "No POI data: set \"poi_directory\" in settings.json (see the uo-poi-data skill)"
                    : $"No POI data: \"poi_directory\" in settings.json does not exist: {configured}";

                return;
            }

            _index = PoiIndex.LoadDirectory(dir, out _loadMessage);
        }

        /// <summary>
        /// The one place POI data comes from: "poi_directory" in settings.json. There is
        /// deliberately no fallback to data beside the executable or in the source tree - two
        /// possible sources meant a lookup could silently answer from the wrong shard's map.
        /// </summary>
        private static string FindDataDirectory()
        {
            string configured = Settings.GlobalSettings.PoiDirectory;

            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

            return Directory.Exists(configured) ? configured : null;
        }
    }
}
