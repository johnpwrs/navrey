// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Best-available names for items and mobiles.
    ///
    /// Shared deliberately: the text commands and the JSON files must never disagree about what a
    /// thing is called. The state file exists so callers stop parsing `inv` output, which only works
    /// if both describe the same item identically.
    ///
    /// The server sends names only on request - single-click on this era's protocol, OPL on later
    /// ones - so an item usually has no name until something asks. Hence the fallback chain, ending
    /// at the English string baked into tiledata.mul rather than a bare graphic id.
    /// </summary>
    internal static class Describe
    {
        /// <summary>Name only, with no amount or hue suffix - the JSON carries those as fields.</summary>
        public static string ItemName(Item item)
        {
            if (item == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(item.Name) && !IsStatusTagOnly(item.Name))
            {
                return item.Name;
            }

            if (ClassicUO.Client.Game.UO.World.OPL.TryGetNameAndData(item.Serial, out string opl, out _) &&
                !string.IsNullOrEmpty(opl))
            {
                return opl;
            }

            return TileDataName(item.Graphic) ?? item.Name ?? $"item 0x{item.Graphic:X4}";
        }

        /// <summary>
        /// Some shards send a status tag like "(newbied)" as its own single-click text line,
        /// separate from the item's real name. The client latches its first such line onto
        /// <see cref="Item.Name"/> and never revisits it, so a bracketed tag there is a sign the
        /// real name never landed - worth falling through to OPL/tiledata for, rather than
        /// reporting the tag as if it were the item.
        /// </summary>
        private static bool IsStatusTagOnly(string s) =>
            s.Length > 1 && s[0] == '(' && s[s.Length - 1] == ')';

        public static string MobileName(Mobile mobile)
        {
            if (mobile == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(mobile.Name))
            {
                return mobile.Name;
            }

            string known = Capabilities.MobNames.Get(mobile.Graphic);

            return string.IsNullOrEmpty(known) ? $"body 0x{mobile.Graphic:X4}" : known;
        }

        /// <summary>
        /// Removes tiledata's pluralisation markers, which are file format, not a name.
        ///
        /// tiledata.mul writes them as a %-delimited group giving the suffix to add when there is
        /// more than one - "clean bandage%s%", "board%s". Leaving them in leaks raw file syntax into
        /// every listing and into the JSON, where the amount is its own field anyway. The singular
        /// is what a reader wants and what a name match should hit.
        /// </summary>
        private static string StripPlural(string name)
        {
            int marker = name.IndexOf('%');

            if (marker < 0)
            {
                return name;
            }

            var sb = new System.Text.StringBuilder(name.Length);
            bool inside = false;

            foreach (char c in name)
            {
                if (c == '%')
                {
                    inside = !inside;

                    continue;
                }

                if (!inside)
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Trim();
        }

        public static string TileDataName(ushort graphic)
        {
            var tileData = ClassicUO.Client.Game.UO.FileManager?.TileData;

            if (tileData == null || graphic >= tileData.StaticData.Length)
            {
                return null;
            }

            string name = tileData.StaticData[graphic].Name;

            return string.IsNullOrWhiteSpace(name) ? null : StripPlural(name.Trim());
        }
    }
}
