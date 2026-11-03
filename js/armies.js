"use strict";

// Each army maps the six chess roles to its own icon and unit name; the rules never change.
// `look` drives the procedural 3D models in battle3d.html (`blood` is what spills on a kill); `story` drives the campaign.
const ARMIES = {
  marines: {
    name: "Marines", mark: "⚓",
    pieces: { k: "🎖️", q: "⭐", r: "🛡️", b: "🚁", n: "🪂", p: "🪖" },
    units: { k: "General", q: "Colonel", r: "Armor Squad", b: "Gunship", n: "Paratrooper", p: "Rifleman" },
    taunts: ["Semper fi. Let's see what you've got.", "You can call it a retreat. We call it a tactical adjustment.", "Eyes up. The next move is ours."],
    look: {
      armor: 0x5f6b45, cloth: 0x4a5236, accent: 0xc9a227, skin: 0xc68e6a, head: "combat",
      weapon: "rifle", mount: "parachute", rook: "tank", bishop: "heli", queen: "champion", king: "champion",
      magic: 0xffa23a, blood: 0x7e0b0b, sky: 0x9fb1b8, ground: 0x6d6a55, fog: 0xb8b9a8
    },
    story: {
      title: "Operation Iron Tide",
      intro: "The Marines hit the beach of the Board at dawn: landing craft, smoke and steel. They hold every square they take, and they hold it under fire.",
      victory: "Their General salutes as the last rifle falls silent. \"Hell of a fight. We'll be ready next time.\""
    }
  },
  undead: {
    name: "Army of the Dead", mark: "☠",
    pieces: { k: "💀", q: "👻", r: "⚰️", b: "🕯️", n: "🐺", p: "🦴" },
    units: { k: "Bone King", q: "Wraith Queen", r: "Crypt", b: "Necromancer", n: "Dire Wolf", p: "Skeleton" },
    taunts: ["You cannot outlast what is already dead.", "The grave has room for one more king.", "We have all the time in the world."],
    look: {
      armor: 0x8a8f7a, cloth: 0x2f3a2c, accent: 0x7dff9a, skin: 0xe8e2cc, head: "skull",
      weapon: "scythe", mount: "wolf", rook: "crypt", bishop: "caster", queen: "wraith", king: "champion",
      magic: 0x63ff8a, blood: 0x4fa83a, sky: 0x1b2420, ground: 0x2c302a, fog: 0x2a3a30
    },
    story: {
      title: "The Hollow March",
      intro: "Fog rolls over the board and the dead rise rank by rank. The Bone King has waited a thousand years for a living opponent.",
      victory: "The Bone King's crown rolls across the stones. \"Death is patient,\" the wind whispers. \"So am I.\""
    }
  },
  knights: {
    name: "Order of Knights", mark: "✠",
    pieces: { k: "🤴", q: "👸", r: "🏰", b: "⛪", n: "🐎", p: "🗡️" },
    units: { k: "King", q: "Queen", r: "Keep", b: "Templar", n: "Paladin", p: "Squire" },
    taunts: ["A noble move. Now answer mine.", "The crown is guarded. Are you sure?", "Courage is admirable. Checkmate is better."],
    look: {
      armor: 0xa9b6c4, cloth: 0x7a1f24, accent: 0xd8342c, skin: 0xe0b394, head: "helm",
      weapon: "sword", mount: "horse", rook: "tower", bishop: "caster", queen: "champion", king: "champion",
      magic: 0xffe08a, blood: 0x7e0b0b, sky: 0x8ec5e8, ground: 0x5d7d3c, fog: 0xbcd8e8
    },
    story: {
      title: "The Tournament of Crowns",
      intro: "Banners snap in the wind. The Order of Knights rides out with lances lowered, sworn to defend a crown that has never fallen.",
      victory: "The old King lays down his sword. \"Well fought. The Order bows to you.\""
    }
  },
  asgardians: {
    name: "Asgardians", mark: "ᚨ",
    pieces: { k: "👁️", q: "⚡", r: "🌈", b: "🐍", n: "🐎", p: "⚔️" },
    units: { k: "All-Father", q: "Thunder God", r: "Bifrost Gate", b: "Trickster", n: "Valkyrie", p: "Einherjar" },
    taunts: ["The Nine Realms have seen better generals than you.", "Thunder answers every insult.", "Ragnarök comes early for your king."],
    look: {
      armor: 0xd9c27a, cloth: 0x2b4a8a, accent: 0x7fd4ff, skin: 0xf0c8a0, head: "winged",
      weapon: "hammer", mount: "pegasus", rook: "gate", bishop: "caster", queen: "champion", king: "champion",
      magic: 0x7fd4ff, blood: 0xd4a640, sky: 0x2a3566, ground: 0x4d4a63, fog: 0x5a5f9a
    },
    story: {
      title: "Twilight of the Gods",
      intro: "The Bifrost cracks open over the board. Gods do not ask permission to wage war, and the Thunder God is already swinging.",
      victory: "The All-Father closes his one eye. \"The sagas will sing of this game. Not kindly, perhaps, but loudly.\""
    }
  },
  vikings: {
    name: "Vikings", mark: "ᛟ",
    pieces: { k: "🪓", q: "🛡️", r: "🛶", b: "🔮", n: "🐻", p: "🏹" },
    units: { k: "Jarl", q: "Shieldmaiden", r: "Longship", b: "Seer", n: "Berserker", p: "Raider" },
    taunts: ["The ravens are already circling.", "A fine move. Shame it will not save you.", "Valhalla is watching this board."],
    look: {
      armor: 0x8c8f93, cloth: 0x6b4a2e, accent: 0xd9a441, skin: 0xe6b08f, head: "horned",
      weapon: "axe", mount: "bear", rook: "longship", bishop: "caster", queen: "champion", king: "champion",
      magic: 0xa6e3ff, blood: 0x7e0b0b, sky: 0x8796a3, ground: 0x6f7468, fog: 0xa9b4bb
    },
    story: {
      title: "The Northern Raid",
      intro: "Longships slide out of the sleet. The Jarl has heard that your king wears gold, and the Vikings have come to collect it.",
      victory: "The Jarl laughs and spits into the snow. \"A worthy foe! Tonight we drink to your name.\""
    }
  },
  samurai: {
    name: "Samurai Clans", mark: "⛩",
    pieces: { k: "🏯", q: "🐉", r: "⛩️", b: "🥷", n: "🏇", p: "🎌" },
    units: { k: "Shogun", q: "Dragon General", r: "Torii Gate", b: "Shinobi", n: "Mounted Samurai", p: "Ashigaru" },
    taunts: ["The blade is drawn only once.", "Patience is the sharpest sword.", "Your castle is paper. Our resolve is steel."],
    look: {
      armor: 0x2d2a2e, cloth: 0xa3232b, accent: 0xe8c547, skin: 0xe8c09c, head: "kabuto",
      weapon: "katana", mount: "horse", rook: "torii", bishop: "caster", queen: "champion", king: "champion",
      magic: 0xff4d5e, blood: 0x7e0b0b, sky: 0xe8b7a8, ground: 0x6e5a4c, fog: 0xf0c9bd
    },
    story: {
      title: "Blossoms and Steel",
      intro: "Cherry petals fall on silent ranks. The Shogun's army does not shout. It waits, and when it strikes, it strikes once.",
      victory: "The Shogun sheathes his blade and bows low. \"There is honour in defeat by a master.\""
    }
  },
  spartans: {
    name: "Spartans", mark: "Λ",
    pieces: { k: "🏛️", q: "🦁", r: "🧱", b: "🏺", n: "🐴", p: "🔱" },
    units: { k: "Basileus", q: "Lion of Sparta", r: "Phalanx Wall", b: "Oracle", n: "Horseman", p: "Hoplite" },
    taunts: ["Come and take them.", "Return with your shield, or on it.", "Our walls are made of men."],
    look: {
      armor: 0xc08a3e, cloth: 0x9b1c1c, accent: 0xd6332c, skin: 0xc98e64, head: "crested",
      weapon: "spear", mount: "horse", rook: "wall", bishop: "caster", queen: "champion", king: "champion",
      magic: 0xffb347, blood: 0x7e0b0b, sky: 0xe9cf9b, ground: 0x9a7b4f, fog: 0xead6ad
    },
    story: {
      title: "The Narrow Pass",
      intro: "Bronze shields lock together across the pass. Three hundred stand where three thousand should, and not one of them will step back.",
      victory: "The Basileus plants his spear in the dust. \"Tell them in Sparta: we fell facing forward.\""
    }
  },
  machines: {
    name: "Machine Legion", mark: "⚙",
    pieces: { k: "🧠", q: "🛸", r: "🦾", b: "📡", n: "🕷️", p: "🤖" },
    units: { k: "Core Mind", q: "Mothership", r: "Heavy Frame", b: "Uplink", n: "Spider Mech", p: "Drone" },
    taunts: ["Probability of your victory: 0.03%.", "Your move has been predicted and logged.", "Resistance is computationally inefficient."],
    look: {
      armor: 0x9aa3ad, cloth: 0x30363d, accent: 0xff3b3b, skin: 0x9aa3ad, head: "visor",
      weapon: "blaster", mount: "spider", rook: "mech", bishop: "dish", queen: "ufo", king: "champion",
      magic: 0xff3b3b, blood: 0x101215, sky: 0x10151c, ground: 0x23292f, fog: 0x1b2633
    },
    story: {
      title: "Protocol Zero",
      intro: "The board hums. Red optics flicker on in perfect rows. The Machine Legion has run this game ten million times, and it has never lost.",
      victory: "The Core Mind flickers. \"ERROR. Outcome not in model. Recalculating… recalculating…\""
    }
  }
};
