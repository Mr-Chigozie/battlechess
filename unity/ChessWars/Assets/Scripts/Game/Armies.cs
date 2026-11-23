// The armies as the game knows them: the same ids as js/armies.js (and the server), with the file prefix of
// their character models in Assets/Resources/Characters (built by Assets/Editor/CharacterPrefabs.cs).
using System.Collections.Generic;
using UnityEngine;

namespace ChessWars.Game
{
    public static class Armies
    {
        public sealed class Army
        {
            public string Id, Name, ModelPrefix;
            public Dictionary<char, string> Units;
            public Color Magic, Blood;
        }

        static Army Make(string id, string name, string prefix, string k, string q, string r, string b, string n, string p, uint magic, uint blood) => new Army
        {
            Id = id, Name = name, ModelPrefix = prefix,
            Units = new Dictionary<char, string> { ['k'] = k, ['q'] = q, ['r'] = r, ['b'] = b, ['n'] = n, ['p'] = p },
            Magic = Hex(magic), Blood = Hex(blood)
        };

        public static Color Hex(uint rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

        public static readonly Dictionary<string, Army> All = new Dictionary<string, Army>
        {
            ["knights"] = Make("knights", "Order of Knights", "Knights", "King", "Queen", "Keep", "Templar", "Paladin", "Squire", 0xffe08a, 0x7e0b0b),
            ["undead"] = Make("undead", "Army of the Dead", "Undead", "Bone King", "Wraith Queen", "Crypt", "Necromancer", "Dire Wolf", "Skeleton", 0x63ff8a, 0x4fa83a),
            ["marines"] = Make("marines", "Marines", "Marines", "Commander", "Heavy Gunner", "Bunker", "Sniper", "Jeep", "Rifleman", 0xffa23a, 0x7e0b0b),
            ["vikings"] = Make("vikings", "Vikings", "Vikings", "Jarl", "Shieldmaiden", "Longship", "Seer", "War Bear", "Raider", 0xa6e3ff, 0x7e0b0b),
            ["samurai"] = Make("samurai", "Samurai Clans", "Samurai", "Daimyo", "Onna-musha", "Pagoda", "Ninja", "Samurai Cavalry", "Ashigaru", 0xff4d5e, 0x7e0b0b),
            ["spartans"] = Make("spartans", "Spartans", "Spartans", "Warlord", "Champion", "Watchtower", "Oracle", "War Lion", "Hoplite", 0xffb347, 0x7e0b0b),
            ["asgardians"] = Make("asgardians", "Asgardians", "Asgard", "All-Father", "Valkyrie", "Bifrost Gate", "Thunderer", "Sleipnir", "Einherjar", 0x7fd4ff, 0xd4a640),
            ["machines"] = Make("machines", "Machine Legion", "Machines", "Overlord", "Annihilator", "Turret", "Seeker", "Mech Hound", "Drone", 0xff3b3b, 0x101215),
            ["mafia"] = Make("mafia", "The Mafia", "Mafia", "The Boss", "Underboss", "Speakeasy", "Consigliere", "Getaway Car", "Soldier", 0xc9a24a, 0x7e0b0b),
        };

        /// <summary>Each army's battle cries for online play: the same lines as js/armies.js (the server only accepts these).</summary>
        public static readonly Dictionary<string, string[]> Taunts = new Dictionary<string, string[]>
        {
            ["marines"] = new[] { "Semper fi. Let's see what you've got.", "You can call it a retreat. We call it a tactical adjustment.", "Eyes up. The next move is ours." },
            ["undead"] = new[] { "You cannot outlast what is already dead.", "The grave has room for one more king.", "We have all the time in the world." },
            ["knights"] = new[] { "A noble move. Now answer mine.", "The crown is guarded. Are you sure?", "Courage is admirable. Checkmate is better." },
            ["asgardians"] = new[] { "The Nine Realms have seen better generals than you.", "Thunder answers every insult.", "Ragnarök comes early for your king." },
            ["vikings"] = new[] { "The ravens are already circling.", "A fine move. Shame it will not save you.", "Valhalla is watching this board." },
            ["samurai"] = new[] { "The blade is drawn only once.", "Patience is the sharpest sword.", "Your castle is paper. Our resolve is steel." },
            ["spartans"] = new[] { "Come and take them.", "Return with your shield, or on it.", "Our walls are made of men." },
            ["machines"] = new[] { "Probability of your victory: 0.03%.", "Your move has been predicted and logged.", "Resistance is computationally inefficient." },
        };

        /// <summary>Armies the server knows (The Mafia is offline-only for now).</summary>
        public static bool Online(string id) => Taunts.ContainsKey(id);

        public static readonly Dictionary<char, string> Pieces = new Dictionary<char, string>
        {
            ['p'] = "Pawn", ['n'] = "Knight", ['b'] = "Bishop", ['r'] = "Rook", ['q'] = "Queen", ['k'] = "King"
        };

        public static string PrefabName(string army, char type) => $"{All[army].ModelPrefix}_{Pieces[type]}";

        // How each unit kills, as the web game's Models.attackStyle (js/models3d.js) works it out for the models:
        // strike, thrust, charge, slam, shoot, spell, beam or lightning. The Mafia exists only here so far.
        static readonly Dictionary<string, string> StyleTable = new Dictionary<string, string>
        {
            ["knights"] = "k:strike q:strike r:slam b:spell n:charge p:strike",
            ["undead"] = "k:strike q:spell r:slam b:spell n:charge p:strike",
            ["marines"] = "k:strike q:shoot r:shoot b:shoot n:charge p:shoot",
            ["vikings"] = "k:strike q:strike r:slam b:spell n:charge p:strike",
            ["samurai"] = "k:strike q:strike r:shoot b:spell n:charge p:strike",
            ["spartans"] = "k:thrust q:thrust r:slam b:spell n:charge p:thrust",
            ["asgardians"] = "k:thrust q:strike r:beam b:lightning n:charge p:strike",
            ["machines"] = "k:shoot q:shoot r:shoot b:shoot n:charge p:shoot",
            ["mafia"] = "k:strike q:shoot r:shoot b:shoot n:charge p:strike",
        };

        public static string Style(string army, char type)
        {
            foreach (var entry in StyleTable[army].Split(' '))
                if (entry[0] == type) return entry.Substring(2);
            return "strike";
        }

        // Heavy guns fire one big round instead of a burst: the Marines' tank and the Speakeasy's barrel catapult.
        public static bool HeavyGun(string army, char type) => type == 'r' && (army == "marines" || army == "mafia");

        static readonly Dictionary<string, string[]> Verbs = new Dictionary<string, string[]>
        {
            ["strike"] = new[] { "cuts down", "cleaves through", "strikes down", "hews down" },
            ["thrust"] = new[] { "runs through", "impales", "skewers" },
            ["charge"] = new[] { "tramples", "rides down", "smashes through" },
            ["slam"] = new[] { "crushes", "flattens", "pulverises" },
            ["shoot"] = new[] { "guns down", "blasts apart", "riddles" },
            ["spell"] = new[] { "unmakes", "incinerates", "withers" },
            ["beam"] = new[] { "disintegrates", "vaporises", "burns away" },
            ["lightning"] = new[] { "smites", "thunderstrikes", "reduces to cinders" },
        };

        public static string KillVerb(string style)
        {
            var verbs = Verbs.TryGetValue(style, out var v) ? v : Verbs["strike"];
            return verbs[Random.Range(0, verbs.Length)];
        }

        public static readonly Dictionary<char, int> Value = new Dictionary<char, int> { ['p'] = 1, ['n'] = 3, ['b'] = 3, ['r'] = 5, ['q'] = 9, ['k'] = 0 };
        public static readonly string[] StreakNames = { null, null, "Double Kill", "Triple Kill", "Rampage", "Massacre", "Unstoppable", "Godlike" };

        // Square (row, col) sits at world (3.5 - col, 0, row - 3.5); Ivory's home rows are at +z, as in the web game.
        // Unity's axes are left-handed where three.js's are right-handed, so x runs the other way here: without the
        // flip the whole board would be a mirror image (a-file on Ivory's right, the light square in the wrong corner).
        public static Vector3 SquareToWorld(int row, int col) => new Vector3(3.5f - col, 0f, row - 3.5f);
    }
}
