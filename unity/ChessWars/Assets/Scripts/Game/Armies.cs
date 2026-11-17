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

        public static readonly Dictionary<char, string> Pieces = new Dictionary<char, string>
        {
            ['p'] = "Pawn", ['n'] = "Knight", ['b'] = "Bishop", ['r'] = "Rook", ['q'] = "Queen", ['k'] = "King"
        };

        public static string PrefabName(string army, char type) => $"{All[army].ModelPrefix}_{Pieces[type]}";

        // Square (row, col) sits at world (col - 3.5, 0, row - 3.5), as in the web game: Ivory's home rows are at +z.
        public static Vector3 SquareToWorld(int row, int col) => new Vector3(col - 3.5f, 0f, row - 3.5f);
    }
}
