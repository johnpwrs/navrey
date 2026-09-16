// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;

namespace ClassicUO.Agent.Capabilities
{
    /// <summary>
    /// The last vendor buy list the server sent.
    ///
    /// ClassicUO keeps a vendor's stock only inside the ShopGump's private child controls, which is
    /// fine for a UI but leaves nothing for the CLI to read. Rather than reach into the gump, the
    /// Narrator - which already sees every 0x74 - records the list here as it arrives.
    /// </summary>
    internal static class VendorStock
    {
        public readonly struct Entry
        {
            public Entry(uint serial, ushort graphic, ushort amount, ushort price, string name)
            {
                Serial = serial;
                Graphic = graphic;
                Amount = amount;
                Price = price;
                Name = name;
            }

            public uint Serial { get; }
            public ushort Graphic { get; }
            public ushort Amount { get; }
            public ushort Price { get; }
            public string Name { get; }
        }

        private static readonly object _lock = new();
        private static readonly List<Entry> _items = new();
        private static readonly List<Entry> _sellItems = new();

        public static uint Vendor { get; private set; }

        /// <summary>Vendor whose sell list (0x9E) we last saw - what it will buy *from us*.</summary>
        public static uint SellVendor { get; private set; }

        public static void Replace(uint vendor, IEnumerable<Entry> items)
        {
            lock (_lock)
            {
                Vendor = vendor;
                _items.Clear();
                _items.AddRange(items);
            }
        }

        /// <summary>
        /// The sell list is kept apart from the buy list on purpose: they are different vendors'
        /// answers to different questions, the serials mean different things (stock the vendor
        /// owns vs. items in *our* backpack), and a `sell` that reached for the buy list would
        /// send the vendor its own stock serials back.
        /// </summary>
        public static void ReplaceSell(uint vendor, IEnumerable<Entry> items)
        {
            lock (_lock)
            {
                SellVendor = vendor;
                _sellItems.Clear();
                _sellItems.AddRange(items);
            }
        }

        public static IReadOnlyList<Entry> Items
        {
            get
            {
                lock (_lock)
                {
                    return _items.ToArray();
                }
            }
        }

        public static IReadOnlyList<Entry> SellItems
        {
            get
            {
                lock (_lock)
                {
                    return _sellItems.ToArray();
                }
            }
        }
    }
}
