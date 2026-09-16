using System.Collections.Generic;

namespace ClassicUO.Agent.Capabilities
{
    internal static class MobNames
    {
        // UO body graphic ID → mob/NPC name (classic pre-AoS / T2A / Renaissance era)
        private static readonly Dictionary<int, string> _names = new()
        {
            {1,"Ogre"},{2,"Sea Serpent"},{3,"Zombie"},{4,"Gargoyle"},{5,"Eagle"},
            {6,"Bird"},{7,"Orc"},{8,"Corpser"},{9,"Daemon"},{10,"Daemon"},
            {11,"Dread Spider"},{12,"Dragon"},{13,"Air Elemental"},{14,"Earth Elemental"},
            {15,"Fire Elemental"},{16,"Water Elemental"},{17,"Orc"},{18,"Ettin"},
            {20,"Frost Spider"},{21,"Giant Serpent"},{22,"Gazer"},{23,"Dire Wolf"},
            {24,"Lich"},{25,"Grey Wolf"},{26,"Shade"},{27,"Grey Wolf"},{28,"Giant Spider"},
            {29,"Gorilla"},{30,"Harpy"},{31,"Headless One"},{34,"White Wolf"},
            {35,"Lizardman"},{36,"Lizardman"},{37,"White Wolf"},{39,"Mongbat"},
            {40,"Balron"},{42,"Ratman"},{43,"Ice Fiend"},{46,"Ancient Wyrm"},
            {47,"Reaper"},{48,"Scorpion"},{50,"Skeleton"},{51,"Slime"},{52,"Snake"},
            {53,"Troll"},{54,"Troll"},{55,"Frost Troll"},{56,"Skeleton"},
            {57,"Bone Knight"},{58,"Wisp"},{59,"Dragon"},{61,"Cold Drake"},
            {62,"Wyvern"},{63,"Cougar"},{64,"Snow Leopard"},{65,"Snow Leopard"},
            {66,"Swamp Tentacle"},{67,"Stone Gargoyle"},{70,"Terathan Warrior"},
            {71,"Terathan Drone"},{72,"Terathan Matriarch"},{73,"Stone Harpy"},
            {74,"Imp"},{75,"Cyclops"},{76,"Titan"},{77,"Kraken"},{78,"Ancient Lich"},
            {79,"Lich Lord"},{80,"Giant Toad"},{81,"Bull Frog"},{83,"Ogre Lord"},
            {85,"Ophidian Mage"},{86,"Ophidian Warrior"},{87,"Ophidian Matriarch"},
            {88,"Mountain Goat"},{89,"Ice Serpent"},{90,"Lava Serpent"},
            {92,"Silver Serpent"},{94,"Frost Ooze"},{95,"Turkey"},{98,"Hell Hound"},
            {99,"Dark Wolf"},{101,"Centaur"},{103,"Serpentine Dragon"},
            {104,"Skeletal Dragon"},{106,"Shadow Wyrm"},{116,"Nightmare"},
            {122,"Unicorn"},{124,"Evil Mage"},{125,"Evil Mage Lord"},{127,"Hell Cat"},
            {128,"Pixie"},{130,"Fire Gargoyle"},{131,"Efreet"},{132,"Kirin"},
            {135,"Arctic Ogre Lord"},{138,"Orcish Lord"},{140,"Orcish Mage"},
            {142,"Ratman Archer"},{143,"Ratman Mage"},{144,"Sea Horse"},
            {146,"Harrower"},{147,"Skeletal Knight"},{148,"Bone Magi"},
            {149,"Succubus"},{150,"Sea Serpent"},{151,"Dolphin"},
            {152,"Terathan Avenger"},{153,"Ghoul"},{154,"Mummy"},
            {155,"Rotting Corpse"},{157,"Giant Black Widow"},{164,"Energy Vortex"},
            {165,"Wisp"},{167,"Brown Bear"},{181,"Orc Scout"},{182,"Orc Bomber"},
            {183,"Savage"},{184,"Savage"},{185,"Savage Rider"},{186,"Savage Shaman"},
            {187,"Ridgeback"},{188,"Savage Ridgeback"},{189,"Orc Brute"},
            {200,"Horse"},{201,"Cat"},{202,"Alligator"},{203,"Pig"},{204,"Horse"},
            {205,"Rabbit"},{206,"Lava Lizard"},{207,"Sheep"},{208,"Chicken"},
            {209,"Goat"},{210,"Desert Ostard"},{211,"Black Bear"},{212,"Grizzly Bear"},
            {213,"Polar Bear"},{214,"Panther"},{215,"Giant Rat"},{216,"Cow"},
            {217,"Dog"},{218,"Frenzied Ostard"},{219,"Forest Ostard"},{220,"Llama"},
            {221,"Walrus"},{225,"Timber Wolf"},{226,"Horse"},{228,"Horse"},
            {231,"Cow"},{232,"Bull"},{233,"Bull"},{234,"Great Hart"},{237,"Hind"},
            {238,"Rat"},{290,"Boar"},{291,"Pack Horse"},{292,"Pack Llama"},
            {400,"Human (Male)"},{401,"Human (Female)"},{402,"Ghost (Male)"},
            {403,"Ghost (Female)"},
        };

        public static string Get(int graphicId)
            => _names.TryGetValue(graphicId, out var n) ? n : $"Unknown({graphicId:X4})";

        /// <summary>
        /// The species name for a body graphic, or null when the table does not know it.
        ///
        /// Same lookup as <see cref="Get"/>, minus its "Unknown(XXXX)" placeholder. That
        /// placeholder is fine for a human-readable line but wrong for the world file's `species`,
        /// which the Python avoid/include filter substring-matches against: "Unknown(019A)" is not
        /// a name and matching it would be matching file syntax. Null instead lets a consumer tell
        /// "not in the table" from a real species and fall back to the server-sent name.
        /// </summary>
        public static string TryGet(int graphicId)
            => _names.TryGetValue(graphicId, out var n) ? n : null;

        public static bool IsAggressive(int graphicId) => graphicId switch
        {
            // Monsters that attack on sight
            1 or 3 or 4 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15
            or 16 or 17 or 18 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27
            or 28 or 30 or 31 or 35 or 36 or 39 or 40 or 42 or 43 or 47 or 48
            or 50 or 51 or 52 or 53 or 54 or 55 or 56 or 57 or 59 or 61 or 62
            or 67 or 70 or 71 or 72 or 73 or 74 or 75 or 76 or 77 or 78 or 79
            or 80 or 81 or 83 or 85 or 86 or 87 or 89 or 90 or 98 or 99 or 124
            or 125 or 130 or 131 or 135 or 138 or 140 or 142 or 143 or 146
            or 147 or 148 or 149 or 150 or 153 or 154 or 155 or 157 => true,
            _ => false
        };
    }
}
