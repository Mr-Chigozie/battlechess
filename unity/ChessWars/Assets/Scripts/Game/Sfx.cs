// Plays the game's sound: effects (from a pool of voices, panned to where things happen on screen), the music
// (a calm bed plus war drums that rise with the battle's fury), each battlefield's background, and fire under
// a burning battle. The sounds themselves are made by Synth.cs on a worker thread while the title screen loads.
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ChessWars.Game
{
    public sealed class Sfx : MonoBehaviour
    {
        public static Sfx I { get; private set; }

        /// <summary>Effects and the battlefield's background (the battle menu's Sound toggle).</summary>
        public static bool SoundOn = true;
        /// <summary>The music (the battle menu's Music toggle).</summary>
        public static bool MusicOn = true;
        /// <summary>0..1, from the HUD's fury bar: brings in the drums and the fire.</summary>
        public static float Fury;
        /// <summary>False on the title screen and once the game is over: drums out.</summary>
        public static bool InBattle;
        /// <summary>Music sits back under a kill close-up.</summary>
        public static bool Duck;

        // How loud each effect is against the others.
        static readonly Dictionary<string, float> Volume = new Dictionary<string, float>
        {
            ["select"] = 0.3f, ["click"] = 0.3f, ["check"] = 0.45f, ["drums"] = 0.7f,
            ["step"] = 0.18f, ["whoosh"] = 0.4f, ["land"] = 0.45f, ["gallop"] = 0.6f,
            ["swish"] = 0.5f, ["bigswish"] = 0.65f, ["slice"] = 0.6f, ["clang"] = 0.4f, ["stab"] = 0.6f,
            ["claw"] = 0.6f, ["growl"] = 0.6f, ["roar"] = 0.75f,
            ["gun"] = 0.5f, ["tommy"] = 0.42f, ["blaster"] = 0.4f, ["bullethit"] = 0.35f, ["casing"] = 0.2f,
            ["sniper"] = 0.95f, ["bolt"] = 0.4f, ["laser"] = 0.25f,
            ["cannon"] = 0.9f, ["missile"] = 0.55f, ["explode"] = 1f, ["slam"] = 0.9f,
            ["charge"] = 0.45f, ["orb"] = 0.5f, ["zap"] = 0.55f, ["thunder"] = 0.95f, ["shuriken"] = 0.4f, ["thunk"] = 0.45f,
            ["kill"] = 0.85f, ["hit"] = 0.7f, ["fall"] = 0.5f, ["soul"] = 0.5f, ["sparks"] = 0.45f, ["chime"] = 0.45f, ["ascend"] = 0.5f,
            ["cine"] = 0.6f, ["slowmo"] = 0.6f, ["promote"] = 0.6f, ["fanfare"] = 0.6f, ["defeat"] = 0.55f,
        };

        const float MusicLevel = 0.32f, AmbienceLevel = 0.35f, FireLevel = 0.3f;

        readonly Dictionary<string, AudioClip[]> clips = new Dictionary<string, AudioClip[]>();
        readonly Dictionary<string, AudioClip> ambienceClips = new Dictionary<string, AudioClip>();
        AudioSource[] voices;
        int nextVoice;
        AudioSource calm, drums, ambience, fire;
        Task<(Dictionary<string, float[][]> fx, float[] calm, float[] drums, float[] fire)> making;
        Task<float[]> makingAmbience;
        string ambienceWanted, ambienceMaking;
        float drumsLevel;

        public static Sfx Create(Transform parent)
        {
            var go = new GameObject("Sound");
            go.transform.SetParent(parent, false);
            I = go.AddComponent<Sfx>();
            return I;
        }

        void Awake()
        {
            voices = new AudioSource[20];
            for (int i = 0; i < voices.Length; i++) voices[i] = Source();
            calm = Source(true);
            drums = Source(true);
            ambience = Source(true);
            fire = Source(true);
            making = Task.Run(() =>
            {
                var m = Synth.Music();
                return (Synth.Effects(), m.calm, m.drums, Synth.Fire());
            });
        }

        AudioSource Source(bool loop = false)
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = loop;
            s.spatialBlend = 0f;
            s.volume = 0f;
            return s;
        }

        static AudioClip Clip(string name, float[] data, int rate)
        {
            var clip = AudioClip.Create(name, data.Length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>Plays an effect. `at` pans it towards where that happens on screen; `pitch` above 1 is
        /// higher and shorter. Returns the voice (to stop a held sound early), or null.</summary>
        public static AudioSource Play(string name, Vector3? at = null, float volume = 1f, float pitch = 1f, float delay = 0f)
        {
            if (I == null || !SoundOn || !I.clips.TryGetValue(name, out var takes)) return null;
            var s = I.voices[I.nextVoice];
            I.nextVoice = (I.nextVoice + 1) % I.voices.Length;
            s.Stop();
            s.clip = takes[Random.Range(0, takes.Length)];
            s.volume = volume * (Volume.TryGetValue(name, out var v) ? v : 0.5f);
            // A touch of variety, and deeper in slow motion (not in the split-second freezes).
            float ts = Time.timeScale;
            s.pitch = pitch * Random.Range(0.95f, 1.05f) * (ts > 0.1f && ts < 0.9f ? 0.78f : 1f);
            s.panStereo = 0f;
            if (at is Vector3 p && Camera.main != null)
            {
                var vp = Camera.main.WorldToViewportPoint(p);
                if (vp.z > 0) s.panStereo = Mathf.Clamp((vp.x - 0.5f) * 1.1f, -0.6f, 0.6f);
            }
            if (delay > 0) s.PlayDelayed(delay);
            else s.Play();
            return s;
        }

        /// <summary>The battlefield's background, by the army whose homeland it is.</summary>
        public static void Ambience(string army)
        {
            if (I != null) I.ambienceWanted = army;
        }

        void Update()
        {
            if (making != null && making.IsCompleted)
            {
                if (making.Status == TaskStatus.RanToCompletion)
                {
                    var r = making.Result;
                    foreach (var kv in r.fx)
                    {
                        var takes = new AudioClip[kv.Value.Length];
                        for (int i = 0; i < takes.Length; i++) takes[i] = Clip(kv.Key, kv.Value[i], Synth.SfxRate);
                        clips[kv.Key] = takes;
                    }
                    calm.clip = Clip("Music", r.calm, Synth.LoopRate);
                    drums.clip = Clip("Drums", r.drums, Synth.LoopRate);
                    fire.clip = Clip("Fire", r.fire, Synth.LoopRate);
                    // The two music loops start on the same sample so the drums sit on the beat.
                    double start = AudioSettings.dspTime + 0.1;
                    calm.PlayScheduled(start);
                    drums.PlayScheduled(start);
                    fire.Play();
                }
                else Debug.LogException(making.Exception);
                making = null;
            }

            // The battlefield's background, made the first time that field is visited.
            if (ambienceWanted != null && makingAmbience == null)
            {
                if (ambienceClips.TryGetValue(ambienceWanted, out var have))
                {
                    if (ambience.clip != have) { ambience.clip = have; ambience.Play(); }
                    ambienceWanted = null;
                }
                else
                {
                    ambienceMaking = ambienceWanted;
                    string army = ambienceMaking;
                    makingAmbience = Task.Run(() => Synth.Ambience(army));
                }
            }
            if (makingAmbience != null && makingAmbience.IsCompleted)
            {
                if (makingAmbience.Status == TaskStatus.RanToCompletion)
                    ambienceClips[ambienceMaking] = Clip("Ambience " + ambienceMaking, makingAmbience.Result, Synth.LoopRate);
                makingAmbience = null;
            }

            float dt = Time.unscaledDeltaTime;
            float duck = Duck ? 0.45f : 1f;
            float music = MusicOn ? MusicLevel * duck : 0f;
            calm.volume = Mathf.MoveTowards(calm.volume, music * (InBattle ? 0.75f : 1f), dt * 0.5f);
            // The drums come in under any battle and grow with its fury.
            float drumTarget = InBattle ? Mathf.Lerp(0.25f, 1f, Mathf.SmoothStep(0, 1, Fury / 0.8f)) : 0f;
            drumsLevel = Mathf.MoveTowards(drumsLevel, drumTarget, dt * 0.25f);
            drums.volume = music * drumsLevel;
            ambience.volume = Mathf.MoveTowards(ambience.volume, SoundOn ? AmbienceLevel : 0f, dt * 0.4f);
            float burning = InBattle && SoundOn ? FireLevel * Mathf.SmoothStep(0, 1, (Fury - 0.35f) / 0.4f) : 0f;
            fire.volume = Mathf.MoveTowards(fire.volume, burning, dt * 0.3f);
        }

        void OnDestroy()
        {
            if (I == this) I = null;
        }
    }
}
