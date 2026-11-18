// The battlefields, one per army: the same worlds as the web game (js/terrain3d.js, js/armies.js), with the
// props from Assets/Environment/Props (built by art/blender/build_props.py) scattered around the board.
using System.Collections.Generic;
using UnityEngine;

namespace ChessWars.Game
{
    public enum Weather { None, Pollen, Ash, Snow, Petals, Dust, Stars, Motes, Smoke }

    public sealed class Theme
    {
        public string Name;
        public Color Light, Dark, Sky, Horizon, Ground, Fog, Platform, Trim;
        public Weather Weather;
        public string[] Props;          // prop names, picked at random (repeat a name to weight it)
        public float SunIntensity = 1.6f;
        public Color Sun = Armies.Hex(0xfff1dc);
        public float Exposure = 1.1f;
    }

    public static class Themes
    {
        static Color H(uint c) => Armies.Hex(c);

        public static readonly Dictionary<string, Theme> All = new Dictionary<string, Theme>
        {
            ["knights"] = new Theme { Name = "the Green Meadow", Light = H(0xd7dcae), Dark = H(0x5f7d3b), Sky = H(0x8ec5e8), Horizon = H(0xdfeedd), Ground = H(0x5d7d3c), Fog = H(0xbcd8e8), Platform = H(0x8a8274), Trim = H(0x5a3e28), Weather = Weather.Pollen,
                Props = new[] { "TreeRound", "TreeRound", "TreeRound", "RockGrey", "Banner" } },
            ["undead"] = new Theme { Name = "the Barrow Fields", Light = H(0xb4b2a2), Dark = H(0x454a3f), Sky = H(0x1b2420), Horizon = H(0x3a4a3e), Ground = H(0x2c302a), Fog = H(0x2a3a30), Platform = H(0x4a4c46), Trim = H(0x2a2c28), Weather = Weather.Ash, SunIntensity = 1.1f, Sun = H(0xb8d8c8), Exposure = 0.9f,
                Props = new[] { "Tombstone", "Tombstone", "TombstoneCross", "TreeDead", "RockDark" } },
            ["vikings"] = new Theme { Name = "the Frozen Fjord", Light = H(0xe8eef2), Dark = H(0x7d93a3), Sky = H(0x8796a3), Horizon = H(0xd8e2e8), Ground = H(0xdde6ea), Fog = H(0xa9b4bb), Platform = H(0x6e7a84), Trim = H(0x4e3420), Weather = Weather.Snow,
                Props = new[] { "TreePineSnow", "TreePineSnow", "TreePine", "RockSnow" } },
            ["samurai"] = new Theme { Name = "the Blossom Garden", Light = H(0xe6d6b8), Dark = H(0x7a6a52), Sky = H(0xe8b7a8), Horizon = H(0xf6dcd0), Ground = H(0x6e8a4c), Fog = H(0xf0c9bd), Platform = H(0x8a8a84), Trim = H(0x7a1e1e), Weather = Weather.Petals,
                Props = new[] { "TreeCherry", "TreeCherry", "Lantern", "RockGrey" } },
            ["spartans"] = new Theme { Name = "the Hot Gates", Light = H(0xe2c995), Dark = H(0x9a7448), Sky = H(0xe9cf9b), Horizon = H(0xf6e6c4), Ground = H(0xb89a68), Fog = H(0xead6ad), Platform = H(0xd8d2c4), Trim = H(0x8a6a3a), Weather = Weather.Dust, SunIntensity = 1.9f,
                Props = new[] { "RockSand", "RockSand", "Column", "Column" } },
            ["asgardians"] = new Theme { Name = "the Bifrost Realm", Light = H(0xc9c6e8), Dark = H(0x3f3b6b), Sky = H(0x2a3566), Horizon = H(0x7a6ab0), Ground = H(0x4d4a63), Fog = H(0x5a5f9a), Platform = H(0x8c8aa8), Trim = H(0xe0b84a), Weather = Weather.Stars, SunIntensity = 1.2f, Sun = H(0xdde4ff),
                Props = new[] { "FloatingRock", "FloatingRock", "RockDark" } },
            ["machines"] = new Theme { Name = "the Foundry", Light = H(0x9aa3ad), Dark = H(0x2d333a), Sky = H(0x10151c), Horizon = H(0x3a2a2a), Ground = H(0x23292f), Fog = H(0x1b2633), Platform = H(0x3a3f46), Trim = H(0xe8b21e), Weather = Weather.Motes, SunIntensity = 1.0f, Sun = H(0xffd8c0), Exposure = 0.85f,
                Props = new[] { "Pylon", "Pylon", "RockDark", "Hedgehog" } },
            ["marines"] = new Theme { Name = "the Beachhead", Light = H(0xe0cf9e), Dark = H(0x8a7a55), Sky = H(0x9fb1b8), Horizon = H(0xd8dccf), Ground = H(0xc9b07a), Fog = H(0xb8b9a8), Platform = H(0x8a8678), Trim = H(0x4a5236), Weather = Weather.Smoke,
                Props = new[] { "Sandbags", "Sandbags", "Hedgehog", "RockSand" } },
            ["mafia"] = new Theme { Name = "the Back Alleys", Light = H(0xcfc6b4), Dark = H(0x4a4038), Sky = H(0x2a2430), Horizon = H(0x6a4a3a), Ground = H(0x3a3530), Fog = H(0x3a3038), Platform = H(0x5a281e), Trim = H(0xc9a24a), Weather = Weather.Smoke, SunIntensity = 1.0f, Sun = H(0xffd8a8), Exposure = 0.9f,
                Props = new[] { "Lantern", "RockDark", "Sandbags" } },
        };

        public static Theme For(string army) => All.TryGetValue(army, out var t) ? t : All["knights"];
    }
}
