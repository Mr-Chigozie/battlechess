// Every sound in the game, synthesised in code (as the web game does with Web Audio in js/fx3d.js): no audio
// files, nothing to license, nothing added to the download. Runs off the main thread at startup; Sfx.cs plays
// the results. "Chess Wars/Export sounds" in the editor writes them all out as WAV files to listen to.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChessWars.Game
{
    public enum Wave { Sine, Triangle, Square, Saw }
    public enum Filter { Low, High, Band }

    /// <summary>A mono sample buffer and the few instruments every sound is built from.</summary>
    public sealed class Track
    {
        public readonly int Rate;
        public readonly float[] Data;
        public bool Wrap;   // notes running past the end continue from the start (seamless loops)
        readonly System.Random rng;
        const float Tau = Mathf.PI * 2f;

        public Track(float seconds, int rate, int seed)
        {
            Rate = rate;
            Data = new float[Mathf.Max(1, (int)(seconds * rate))];
            rng = new System.Random(seed);
        }

        public float Rand => (float)rng.NextDouble();
        public float Length => Data.Length / (float)Rate;

        void Add(int i, float v)
        {
            if (Wrap) Data[((i % Data.Length) + Data.Length) % Data.Length] += v;
            else if (i >= 0 && i < Data.Length) Data[i] += v;
        }

        // Percussive: a quick attack, then an exponential fall to silence (as Web Audio's exponentialRamp to 0.001).
        // Held: a linear attack, flat, and a linear release.
        static float Env(float t, float dur, float attack, bool hold, float release)
        {
            attack = Mathf.Max(attack, 0.002f);
            if (t < attack) return t / attack;
            if (hold)
            {
                if (release < 0) release = dur * 0.25f;
                return t > dur - release ? Mathf.Max(0f, (dur - t) / release) : 1f;
            }
            return Mathf.Pow(0.001f, (t - attack) / Mathf.Max(0.001f, dur - attack));
        }

        static float Osc(Wave wave, float p) => wave switch
        {
            Wave.Sine => Mathf.Sin(Tau * p),
            Wave.Triangle => 4f * Mathf.Abs(p - 0.5f) - 1f,
            Wave.Square => p < 0.5f ? 1f : -1f,
            _ => 2f * p - 1f,
        };

        /// <summary>An oscillator from `f0` sliding (exponentially) to `f1`, as the web game's tone().</summary>
        public void Tone(float at, float dur, float f0, float f1 = 0, Wave wave = Wave.Sine, float vol = 1f, float attack = 0.003f,
                         bool hold = false, float release = -1f, float vibrato = 0f, float vibRate = 5f, Func<float, float> amp = null)
        {
            int s0 = (int)(at * Rate), n = (int)(dur * Rate);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float f = f1 > 0 ? f0 * Mathf.Pow(f1 / f0, t / dur) : f0;
                if (vibrato > 0) f *= 1f + vibrato * Mathf.Sin(Tau * vibRate * t);
                phase += f / Rate;
                phase -= Math.Floor(phase);
                float v = Osc(wave, (float)phase) * vol * Env(t, dur, attack, hold, release);
                if (amp != null) v *= amp(at + t);
                Add(s0 + i, v);
            }
        }

        /// <summary>Filtered white noise; the filter's cutoff sweeps from `f0` to `f1` (or follows `cutoff`).</summary>
        public void Noise(float at, float dur, float vol, Filter type, float f0, float f1 = 0, float q = 0.7f, float attack = 0.003f,
                          bool hold = false, float release = -1f, float tremolo = 0f, Func<float, float> amp = null, Func<float, float> cutoff = null)
        {
            int s0 = (int)(at * Rate), n = (int)(dur * Rate);
            float ic1 = 0, ic2 = 0, k = 1f / q;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float fc = cutoff != null ? cutoff(at + t) : f1 > 0 ? f0 * Mathf.Pow(f1 / f0, t / dur) : f0;
                // A state-variable filter (Simper's trapezoidal form): stable at any cutoff.
                float g = Mathf.Tan(Mathf.PI * Mathf.Clamp(fc, 20f, Rate * 0.45f) / Rate);
                float a1 = 1f / (1f + g * (g + k)), a2 = g * a1, a3 = g * a2;
                float x = Rand * 2f - 1f;
                float v3 = x - ic2;
                float v1 = a1 * ic1 + a2 * v3;
                float v2 = ic2 + a2 * ic1 + a3 * v3;
                ic1 = 2f * v1 - ic1;
                ic2 = 2f * v2 - ic2;
                float y = type == Filter.Low ? v2 : type == Filter.High ? x - k * v1 - v2 : k * v1;
                float v = y * vol * Env(t, dur, attack, hold, release);
                if (tremolo > 0) v *= 0.5f + 0.5f * Mathf.Sin(Tau * tremolo * t);
                if (amp != null) v *= amp(at + t);
                Add(s0 + i, v);
            }
        }

        /// <summary>Random pops and ticks: debris, sparks, fire, rain.</summary>
        public void Crackle(float at, float dur, float perSecond, float vol, float decay = 0.004f, Func<float, float> amp = null)
        {
            int s0 = (int)(at * Rate), n = (int)(dur * Rate);
            int len = Mathf.Max(4, (int)(decay * Rate * 3));
            for (int i = 0; i < n; i++)
            {
                if (Rand >= perSecond / Rate) continue;
                float level = vol * (0.25f + 0.75f * Rand * Rand);
                float t0 = (at + i / (float)Rate);
                if (amp != null) level *= amp(t0);
                for (int j = 0; j < len; j++)
                    Add(s0 + i + j, (Rand * 2f - 1f) * level * Mathf.Exp(-j / (decay * Rate)));
            }
        }

        /// <summary>A beast's growl: a rough, wavering, throaty buzz.</summary>
        public void Growl(float at, float dur, float f0, float f1, float vol)
        {
            int s0 = (int)(at * Rate), n = (int)(dur * Rate);
            double phase = 0;
            float am = 0, lp = 0, a = 1f - Mathf.Exp(-Tau * 900f / Rate);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float f = f0 * Mathf.Pow(f1 / f0, t / dur) * (1f + 0.06f * Mathf.Sin(Tau * 5.3f * t) + 0.04f * Mathf.Sin(Tau * 2.1f * t));
                phase += f / Rate;
                phase -= Math.Floor(phase);
                float p = (float)phase;
                am += (Rand * 2f - 1f - am) * 0.006f;   // a ~30 Hz flutter: the rattle in the throat
                float w = (2f * p - 1f) + 0.5f * (2f * ((p * 2.01f) % 1f) - 1f) + (Rand * 2f - 1f) * 0.35f;
                lp += (w * Mathf.Clamp01(0.55f + 2.2f * am) - lp) * a;
                float env = Mathf.Min(1f, t / 0.1f) * (t > dur * 0.6f ? (dur - t) / (dur * 0.4f) : 1f);
                Add(s0 + i, lp * vol * env);
            }
        }

        /// <summary>A steady tone for the whole loop, tuned to a whole number of cycles so it joins up.</summary>
        public void Drone(float f, float vol, Wave wave = Wave.Sine)
        {
            float cycles = Mathf.Max(1, Mathf.Round(f * Length));
            for (int i = 0; i < Data.Length; i++)
            {
                float p = (float)(cycles * (double)i / Data.Length % 1.0);
                Data[i] += Osc(wave, p) * vol;
            }
        }

        /// <summary>Saturates (a gunshot's bark, an explosion's crunch).</summary>
        public void Drive(float amount)
        {
            float norm = 1f / (float)Math.Tanh(amount);
            float peak = Peak();
            if (peak <= 0) return;
            for (int i = 0; i < Data.Length; i++) Data[i] = (float)Math.Tanh(Data[i] / peak * amount) * norm * peak;
        }

        /// <summary>A darkening echo: the crack of a rifle coming back off the hills.</summary>
        public void Echo(float delay, float feedback, float damp = 2500f)
        {
            int d = Mathf.Max(1, (int)(delay * Rate));
            float lp = 0, a = 1f - Mathf.Exp(-Tau * damp / Rate);
            for (int i = d; i < Data.Length; i++)
            {
                lp += (Data[i - d] - lp) * a;
                Data[i] += lp * feedback;
            }
        }

        /// <summary>One-pole low-pass. On a loop it runs round twice so the join is seamless.</summary>
        public void Lowpass(float fc)
        {
            float a = 1f - Mathf.Exp(-Tau * fc / Rate), lp = 0;
            if (Wrap) foreach (var x in Data) lp += (x - lp) * a;
            for (int i = 0; i < Data.Length; i++) Data[i] = lp += (Data[i] - lp) * a;
        }

        public void Mix(Track other, float gain)
        {
            for (int i = 0; i < Data.Length && i < other.Data.Length; i++) Data[i] += other.Data[i] * gain;
        }

        public float Peak()
        {
            float p = 0;
            foreach (var x in Data) p = Mathf.Max(p, Mathf.Abs(x));
            return p;
        }

        public void Normalize(float peak)
        {
            float p = Peak();
            if (p <= 0) return;
            for (int i = 0; i < Data.Length; i++) Data[i] *= peak / p;
            // A few milliseconds' fade at the end so nothing clicks off.
            if (!Wrap)
                for (int i = 0, n = Mathf.Min(Data.Length, Rate / 200); i < n; i++) Data[Data.Length - 1 - i] *= i / (float)n;
        }
    }

    public static class Synth
    {
        public const int SfxRate = 32000, LoopRate = 22050;
        static float Midi(int note) => 440f * Mathf.Pow(2f, (note - 69) / 12f);

        /// <summary>Every sound effect by name, each with a few takes so repeats don't sound copied.</summary>
        public static Dictionary<string, float[][]> Effects()
        {
            var all = new Dictionary<string, float[][]>();
            void S(string name, int takes, float seconds, Action<Track> make)
            {
                var list = new float[takes][];
                for (int v = 0; v < takes; v++)
                {
                    var t = new Track(seconds, SfxRate, name.Length * 7919 + name[0] * 131 + v * 104729);
                    make(t);
                    t.Normalize(0.9f);
                    list[v] = t.Data;
                }
                all[name] = list;
            }

            // ---- interface
            S("select", 1, 0.14f, t => { t.Tone(0, 0.09f, 660, 0, Wave.Triangle, 1f); t.Tone(0.035f, 0.09f, 990, 0, Wave.Sine, 0.45f); });
            S("click", 1, 0.07f, t => { t.Noise(0, 0.025f, 0.5f, Filter.High, 3000); t.Tone(0, 0.05f, 1250, 900, Wave.Sine, 0.7f); });
            S("check", 1, 0.5f, t => { t.Tone(0, 0.18f, 440, 0, Wave.Square, 0.5f); t.Tone(0.15f, 0.3f, 330, 0, Wave.Square, 0.5f); t.Tone(0, 0.45f, 220, 0, Wave.Sine, 0.4f); });
            S("drums", 1, 0.7f, t => { Taiko(t, 0, 1f); Taiko(t, 0.22f, 0.8f); });

            // ---- movement
            S("step", 4, 0.12f, t =>
            {
                t.Noise(0, 0.08f, 1f, Filter.Low, 450 + t.Rand * 300);
                t.Tone(0, 0.07f, 85 + t.Rand * 30, 50, Wave.Sine, 0.6f);
                t.Noise(0.004f, 0.05f, 0.18f, Filter.Band, 2400, 0, 1.5f);
            });
            S("whoosh", 2, 0.42f, t => t.Noise(0, 0.4f, 1f, Filter.Band, 380 + t.Rand * 80, 2400, 1.2f, 0.14f));
            S("land", 2, 0.3f, t => { t.Noise(0, 0.22f, 1f, Filter.Low, 300); t.Tone(0, 0.25f, 75, 40, Wave.Sine, 0.9f); });
            S("gallop", 1, 0.95f, t =>
            {
                foreach (var at in new[] { 0f, 0.11f, 0.3f, 0.41f, 0.6f, 0.71f })
                {
                    t.Noise(at, 0.07f, 0.9f, Filter.Low, 380 + t.Rand * 150);
                    t.Tone(at, 0.08f, 120 + t.Rand * 20, 55, Wave.Sine, 0.8f);
                    t.Noise(at, 0.03f, 0.25f, Filter.Band, 1800, 0, 2f);
                }
            });

            // ---- blades
            S("swish", 4, 0.3f, t => t.Noise(0, 0.26f, 1f, Filter.Band, 650 + t.Rand * 250, 3800 + t.Rand * 600, 2.2f, 0.09f));
            S("bigswish", 2, 0.5f, t =>
            {
                t.Noise(0, 0.48f, 1f, Filter.Band, 320, 2800, 1.6f, 0.22f);
                t.Noise(0, 0.48f, 0.35f, Filter.High, 4500, 0, 0.7f, 0.22f);
            });
            S("slice", 4, 0.45f, t =>
            {
                t.Noise(0, 0.05f, 1f, Filter.High, 2500);                       // the edge
                t.Noise(0, 0.16f, 0.9f, Filter.Low, 650 + t.Rand * 200);       // into the body
                t.Tone(0, 0.12f, 140 + t.Rand * 20, 60, Wave.Sine, 0.6f);
                float ring = 2300 + t.Rand * 500;                               // steel ringing off
                t.Tone(0, 0.4f, ring, 0, Wave.Sine, 0.16f);
                t.Tone(0, 0.32f, ring * 1.51f, 0, Wave.Sine, 0.1f);
                t.Tone(0, 0.25f, ring * 2.09f, 0, Wave.Sine, 0.07f);
                t.Drive(1.6f);
            });
            S("clang", 2, 0.7f, t =>
            {
                float f = 880 + t.Rand * 120;
                t.Tone(0, 0.25f, f, 0, Wave.Square, 0.18f);
                t.Tone(0, 0.5f, f * 1.5f, 0, Wave.Triangle, 0.4f);
                t.Tone(0, 0.65f, f * 2.51f, 0, Wave.Sine, 0.25f);
                t.Tone(0, 0.45f, f * 3.6f, 0, Wave.Sine, 0.15f);
                t.Noise(0, 0.1f, 0.6f, Filter.High, 3000);
            });
            S("stab", 3, 0.32f, t =>
            {
                t.Noise(0, 0.1f, 0.6f, Filter.Band, 1200, 3200, 1.5f, 0.05f);
                t.Noise(0.07f, 0.16f, 1f, Filter.Low, 420);
                t.Tone(0.07f, 0.16f, 200, 70, Wave.Sine, 0.85f);
                t.Noise(0.07f, 0.03f, 0.3f, Filter.High, 2500);
            });

            // ---- beasts
            S("claw", 3, 0.32f, t =>
            {
                for (int i = 0; i < 3; i++)
                    t.Noise(i * 0.032f, 0.1f, 1f - i * 0.15f, Filter.Band, 3400 - t.Rand * 500, 1300, 3f, 0.004f);
                t.Noise(0, 0.16f, 0.5f, Filter.Low, 800);
            });
            S("growl", 2, 0.9f, t => t.Growl(0, 0.85f, 68 + t.Rand * 10, 62, 1f));
            S("roar", 2, 1.2f, t =>
            {
                t.Growl(0, 1.15f, 85, 150, 1f);
                t.Noise(0, 1.15f, 0.45f, Filter.Band, 650, 900, 1.2f, 0.18f, true, 0.5f);
                t.Drive(2f);
            });

            // ---- guns
            S("gun", 4, 0.6f, t =>
            {
                t.Noise(0, 0.025f, 1f, Filter.High, 1500 + t.Rand * 600);
                t.Noise(0, 0.13f, 1f, Filter.Low, 2600, 600);
                t.Tone(0, 0.12f, 150 + t.Rand * 30, 50, Wave.Sine, 0.9f);
                t.Noise(0.02f, 0.5f, 0.22f, Filter.Low, 900);                 // the air ringing after
                t.Drive(3f);
            });
            S("tommy", 4, 0.32f, t =>
            {
                t.Noise(0, 0.02f, 1f, Filter.High, 2000 + t.Rand * 500);
                t.Noise(0, 0.09f, 1f, Filter.Low, 2400, 700);
                t.Tone(0, 0.08f, 170 + t.Rand * 30, 60, Wave.Sine, 0.8f);
                t.Noise(0.015f, 0.28f, 0.18f, Filter.Low, 1000);
                t.Drive(3f);
            });
            S("blaster", 3, 0.26f, t =>
            {
                float f = 2100 + t.Rand * 400;
                t.Tone(0, 0.22f, f, 170, Wave.Saw, 0.45f);
                t.Tone(0, 0.22f, f * 0.5f, 90, Wave.Square, 0.25f);
                t.Noise(0, 0.08f, 0.4f, Filter.Band, 3000, 0, 1.5f);
            });
            S("bullethit", 4, 0.16f, t =>
            {
                t.Noise(0, 0.03f, 0.7f, Filter.High, 3000);
                t.Noise(0, 0.1f, 1f, Filter.Low, 700 + t.Rand * 300);
                t.Tone(0, 0.08f, 160, 70, Wave.Sine, 0.5f);
            });
            S("casing", 3, 0.32f, t =>
            {
                float k = 0.9f + t.Rand * 0.2f;
                foreach (var (at, v) in new[] { (0f, 1f), (0.11f + t.Rand * 0.03f, 0.5f), (0.2f + t.Rand * 0.03f, 0.22f) })
                {
                    t.Tone(at, 0.12f, 4200 * k, 0, Wave.Sine, 0.6f * v);
                    t.Tone(at, 0.09f, 6150 * k, 0, Wave.Sine, 0.4f * v);
                    t.Tone(at, 0.06f, 7900 * k, 0, Wave.Sine, 0.3f * v);
                }
            });
            S("sniper", 1, 2.2f, t =>
            {
                t.Noise(0, 0.02f, 1f, Filter.High, 3000);
                t.Noise(0, 0.22f, 1f, Filter.Low, 3200, 450);
                t.Tone(0, 0.28f, 120, 38, Wave.Sine, 1f);
                t.Drive(4f);
                t.Noise(0.05f, 1.6f, 0.25f, Filter.Low, 300, 0, 0.7f, 0.08f);   // rolling away over the field
                t.Echo(0.23f, 0.42f, 1800);
            });
            S("bolt", 1, 0.5f, t =>
            {
                foreach (var at in new[] { 0f, 0.17f, 0.31f })
                {
                    t.Noise(at, 0.02f, 1f, Filter.High, 2500);
                    t.Tone(at, 0.03f, 1700 + t.Rand * 500, 0, Wave.Square, 0.25f);
                    t.Noise(at, 0.05f, 0.5f, Filter.Band, 900, 0, 2f);
                }
            });
            S("laser", 1, 1.3f, t =>
            {
                t.Tone(0, 0.06f, 2000, 0, Wave.Square, 0.35f, 0.002f, true, 0.01f);
                t.Tone(0.1f, 0.06f, 2000, 0, Wave.Square, 0.35f, 0.002f, true, 0.01f);
                t.Tone(0.12f, 1.15f, 1180, 0, Wave.Sine, 0.22f, 0.05f, true, 0.1f, 0.004f, 7f);
                t.Tone(0.12f, 1.15f, 59, 0, Wave.Saw, 0.06f, 0.05f, true, 0.1f);
            });

            // ---- heavy
            S("cannon", 2, 1.3f, t =>
            {
                t.Noise(0, 0.04f, 1f, Filter.High, 1200);
                t.Noise(0, 1.0f, 1f, Filter.Low, 420, 140);
                t.Tone(0, 0.8f, 68, 28, Wave.Sine, 1f);
                t.Drive(2.2f);
                t.Echo(0.14f, 0.3f, 900);
            });
            S("missile", 2, 0.85f, t =>
            {
                t.Tone(0, 0.1f, 120, 60, Wave.Sine, 0.6f);
                t.Noise(0, 0.8f, 1f, Filter.Band, 800, 3000, 1f, 0.05f);
                t.Noise(0, 0.8f, 0.45f, Filter.High, 5000, 0, 0.7f, 0.05f);
            });
            S("explode", 2, 2.0f, t =>
            {
                t.Noise(0, 0.05f, 1f, Filter.High, 1000);
                t.Noise(0, 1.7f, 1f, Filter.Low, 650, 110);
                t.Tone(0, 1.1f, 55, 24, Wave.Sine, 1f);
                t.Crackle(0.06f, 1.1f, 45, 0.35f, 0.003f, x => Mathf.Max(0, 1 - x));   // debris raining down
                t.Drive(2.5f);
                t.Echo(0.12f, 0.28f, 700);
            });
            S("slam", 2, 1.0f, t =>
            {
                t.Noise(0, 0.8f, 1f, Filter.Low, 320, 120);
                t.Tone(0, 0.6f, 62, 30, Wave.Sine, 1f);
                t.Noise(0, 0.4f, 0.4f, Filter.Band, 600, 200, 1f);
                t.Crackle(0.03f, 0.5f, 30, 0.25f);
                t.Drive(1.8f);
            });

            // ---- magic
            S("charge", 1, 0.85f, t =>
            {
                t.Tone(0, 0.8f, 180, 1000, Wave.Sine, 0.6f, 0.7f);
                t.Tone(0, 0.8f, 90, 500, Wave.Triangle, 0.3f, 0.7f);
                t.Noise(0, 0.8f, 0.4f, Filter.Band, 500, 4200, 3f, 0.7f);
            });
            S("orb", 2, 0.65f, t =>
            {
                t.Tone(0, 0.55f, 300, 1200, Wave.Sine, 0.9f);
                t.Tone(0, 0.55f, 600, 2400, Wave.Triangle, 0.3f);
                t.Noise(0, 0.5f, 0.3f, Filter.Band, 600, 2000, 1.5f, 0.1f);
            });
            S("zap", 1, 1.0f, t =>
            {
                t.Noise(0, 0.95f, 0.8f, Filter.Band, 3000, 0, 1f, 0.02f, true, 0.3f);
                t.Tone(0, 0.95f, 120, 60, Wave.Saw, 0.4f, 0.02f, true, 0.3f, 0, 0, x => 0.6f + 0.4f * Mathf.Sin(x * Mathf.PI * 2 * 60));
                t.Tone(0, 0.95f, 1500, 700, Wave.Sine, 0.3f, 0.02f, true, 0.3f);
            });
            S("thunder", 1, 2.6f, t =>
            {
                t.Noise(0, 0.03f, 1f, Filter.High, 2500);
                t.Crackle(0, 0.25f, 600, 0.8f, 0.002f);
                t.Noise(0.04f, 2.4f, 1f, Filter.Low, 260, 70, 0.7f, 0.12f, false, -1, 0,
                        x => 0.6f + 0.4f * Mathf.Sin(x * 13f) * Mathf.Sin(x * 5.3f));
                t.Drive(2f);
            });
            S("shuriken", 3, 0.3f, t =>
            {
                t.Noise(0, 0.26f, 1f, Filter.Band, 3200 + t.Rand * 600, 0, 4f, 0.03f, false, -1, 38 + t.Rand * 8);
                t.Tone(0, 0.2f, 5200, 4600, Wave.Sine, 0.08f, 0.03f);
            });
            S("thunk", 3, 0.14f, t => { t.Tone(0, 0.05f, 900 + t.Rand * 100, 300, Wave.Triangle, 0.6f); t.Noise(0, 0.07f, 0.8f, Filter.Low, 1500); });

            // ---- the kill and the fall
            S("kill", 3, 0.8f, t =>
            {
                t.Noise(0, 0.05f, 1f, Filter.High, 2000);
                t.Noise(0, 0.5f, 1f, Filter.Low, 700, 200);
                t.Tone(0, 0.5f, 95 + t.Rand * 10, 35, Wave.Sine, 1f);
                t.Drive(1.6f);
                t.Echo(0.09f, 0.2f, 1200);
            });
            S("hit", 2, 0.4f, t => { t.Noise(0, 0.3f, 1f, Filter.Low, 900); t.Tone(0, 0.35f, 110, 40, Wave.Sine, 0.9f); });
            S("fall", 3, 0.45f, t =>
            {
                t.Noise(0, 0.3f, 1f, Filter.Low, 240 + t.Rand * 60);
                t.Tone(0, 0.3f, 72, 40, Wave.Sine, 0.9f);
                t.Noise(0.03f, 0.15f, 0.25f, Filter.High, 4000);   // kit rattling
            });
            S("soul", 1, 1.6f, t =>
            {
                t.Tone(0, 1.5f, 700, 200, Wave.Sine, 0.5f, 0.1f, false, -1, 0.02f, 6f);
                t.Tone(0, 1.5f, 1050, 300, Wave.Sine, 0.25f, 0.1f, false, -1, 0.02f, 6.5f);
                t.Noise(0, 1.5f, 0.5f, Filter.Band, 900, 380, 4f, 0.3f);
            });
            S("sparks", 2, 0.5f, t =>
            {
                t.Crackle(0, 0.4f, 260, 1f, 0.0015f);
                t.Tone(0, 0.3f, 1800, 500, Wave.Square, 0.12f);
            });
            S("chime", 1, 2.2f, t =>
            {
                float f = 1046;
                t.Tone(0, 2.1f, f, 0, Wave.Sine, 0.6f);
                t.Tone(0, 1.4f, f * 2.76f, 0, Wave.Sine, 0.25f);
                t.Tone(0, 0.8f, f * 5.4f, 0, Wave.Sine, 0.12f);
                t.Tone(0, 0.5f, f * 8.93f, 0, Wave.Sine, 0.06f);
                t.Noise(0, 0.2f, 0.12f, Filter.High, 5000);
            });
            S("ascend", 1, 1.5f, t =>
            {
                foreach (var f in new[] { 523f, 659f, 784f })
                    t.Tone(0, 1.4f, f, f * 2, Wave.Sine, 0.35f, 0.3f, true, 0.5f, 0.006f, 6f);
                t.Noise(0, 1.4f, 0.25f, Filter.High, 6000, 0, 0.7f, 0.5f, true, 0.5f);
            });

            // ---- the show
            S("cine", 1, 1.2f, t =>
            {
                Taiko(t, 0, 1f);
                t.Noise(0, 1.1f, 0.25f, Filter.Low, 500, 150, 0.7f, 0.05f);
            });
            S("slowmo", 1, 1.4f, t =>
            {
                t.Tone(0, 1.3f, 130, 32, Wave.Sine, 1f, 0.04f);
                t.Noise(0, 1.3f, 0.6f, Filter.Low, 1400, 140, 0.7f, 0.08f);
                t.Noise(0, 0.5f, 0.3f, Filter.Band, 2500, 400, 2f, 0.02f);
            });
            for (int level = 1; level <= 5; level++)
            {
                int notes = Mathf.Min(6, level + 2);
                S("combo" + level, 1, 0.5f + notes * 0.075f, t =>
                {
                    Taiko(t, 0, 0.8f);
                    var scale = new[] { 392f, 494f, 587f, 784f, 988f, 1175f };
                    for (int i = 0; i < notes; i++) Brass(t, i * 0.07f, 0.3f, scale[i], 0.6f);
                });
            }
            S("promote", 1, 1.7f, t =>
            {
                t.Noise(0, 1.0f, 0.4f, Filter.Band, 400, 3000, 1.2f, 0.6f);
                foreach (var (f, at) in new[] { (523f, 0.45f), (659f, 0.52f), (784f, 0.59f), (1046f, 0.66f) })
                {
                    t.Tone(at, 1.0f, f, 0, Wave.Sine, 0.4f);
                    t.Tone(at, 0.6f, f * 2.76f, 0, Wave.Sine, 0.08f);
                }
            });
            S("fanfare", 1, 1.6f, t =>
            {
                Taiko(t, 0, 1f);
                var notes = new[] { 523f, 659f, 784f, 1046f };
                for (int i = 0; i < notes.Length; i++) Brass(t, i * 0.16f, i == 3 ? 0.9f : 0.45f, notes[i], 0.7f);
            });
            S("defeat", 1, 1.6f, t =>
            {
                var notes = new[] { 392f, 330f, 262f, 196f };
                for (int i = 0; i < notes.Length; i++) t.Tone(i * 0.22f, 0.6f, notes[i], 0, Wave.Sine, 0.6f);
                t.Tone(0.66f, 0.9f, 98, 0, Wave.Triangle, 0.3f);
            });
            return all;
        }

        // A big war drum: a falling boom and the skin's slap.
        static void Taiko(Track t, float at, float vol)
        {
            t.Tone(at, 0.65f, 72, 44, Wave.Sine, vol);
            t.Noise(at, 0.14f, 0.5f * vol, Filter.Low, 420);
            t.Noise(at, 0.02f, 0.3f * vol, Filter.High, 1500);
        }

        // A horn-ish note: a triangle body with a little saw edge.
        static void Brass(Track t, float at, float dur, float f, float vol)
        {
            t.Tone(at, dur, f, 0, Wave.Triangle, vol, 0.02f, true, dur * 0.5f, 0.004f, 5.5f);
            t.Tone(at, dur, f, 0, Wave.Saw, vol * 0.18f, 0.02f, true, dur * 0.5f, 0.004f, 5.5f);
        }

        // ------------------------------------------------------------------ music

        const float Bpm = 84f;
        const int Bars = 8;
        static float Beat => 60f / Bpm;
        // Dm | Bb | Gm | A, two bars each (MIDI notes).
        static readonly int[][] Chords = { new[] { 50, 53, 57 }, new[] { 46, 50, 53 }, new[] { 43, 46, 50 }, new[] { 45, 49, 52 } };

        /// <summary>The music, as two loops of the same length: a brooding bed that plays always, and the war drums
        /// and pulse that come in as the battle's fury rises.</summary>
        public static (float[] calm, float[] drums) Music()
        {
            float length = Bars * 4 * Beat;
            var calm = new Track(length, LoopRate, 11) { Wrap = true };
            calm.Drone(Midi(38), 0.12f);
            calm.Drone(Midi(50), 0.04f, Wave.Triangle);
            var pad = new Track(length, LoopRate, 12) { Wrap = true };
            for (int c = 0; c < 4; c++)
            {
                float at = c * 8 * Beat;
                foreach (var note in Chords[c])
                    foreach (var detune in new[] { 0.996f, 1.004f })
                        pad.Tone(at, 8 * Beat + 1.6f, Midi(note) * detune, 0, Wave.Saw, 0.1f, 1.4f, true, 1.8f, 0.003f, 4.5f);
                // A bell over each chord.
                calm.Tone(at, 3f, Midi(Chords[c][0] + 24), 0, Wave.Sine, 0.09f);
                calm.Tone(at, 1.6f, Midi(Chords[c][0] + 24) * 2.76f, 0, Wave.Sine, 0.025f);
                calm.Tone(at + 4.5f * Beat, 2.5f, Midi(Chords[c][2] + 24), 0, Wave.Sine, 0.06f);
            }
            pad.Lowpass(650);
            pad.Lowpass(900);
            calm.Mix(pad, 1f);
            calm.Normalize(0.8f);

            var drums = new Track(length, LoopRate, 13) { Wrap = true };
            var pulse = new Track(length, LoopRate, 14) { Wrap = true };
            for (int bar = 0; bar < Bars; bar++)
            {
                float b0 = bar * 4 * Beat;
                Taiko(drums, b0, 1f);
                Taiko(drums, b0 + 2 * Beat, 0.75f);
                foreach (var beat in new[] { 1.5f, 3f })
                    Tom(drums, b0 + beat * Beat, 0.55f, 120);
                if (bar % 4 == 3)
                    for (int i = 0; i < 4; i++) Tom(drums, b0 + (3f + i * 0.25f) * Beat, 0.45f + i * 0.1f, 110 + i * 25);
                else Tom(drums, b0 + 3.5f * Beat, 0.4f, 100);
                for (int e = 0; e < 8; e++)
                {
                    float at = b0 + e * 0.5f * Beat;
                    float v = e % 2 == 0 ? 0.16f : 0.1f;
                    drums.Noise(at, 0.035f, v, Filter.Band, 2600, 0, 2f);
                    // The low string pulse on every eighth, following the chord's root.
                    pulse.Tone(at, 0.5f * Beat, Midi(Chords[bar / 2][0] - 12), 0, Wave.Saw, e % 2 == 0 ? 0.3f : 0.2f, 0.005f, true, 0.06f);
                }
            }
            pulse.Lowpass(420);
            pulse.Lowpass(600);
            drums.Mix(pulse, 1.4f);
            drums.Normalize(0.8f);
            return (calm.Data, drums.Data);
        }

        static void Tom(Track t, float at, float vol, float f)
        {
            t.Tone(at, 0.32f, f, f * 0.65f, Wave.Sine, vol);
            t.Noise(at, 0.07f, 0.3f * vol, Filter.Low, 900);
        }

        // ------------------------------------------------------------------ the field around you

        /// <summary>Fire crackling: rises under the battle as the fury burns.</summary>
        public static float[] Fire()
        {
            var t = new Track(8f, LoopRate, 21) { Wrap = true };
            t.Noise(0, 9f, 0.35f, Filter.Low, 260, 0, 0.7f, 1f, true, 1f, 0, Gust(8f, 0.7f));
            t.Crackle(0, 8f, 70, 0.7f, 0.0025f);
            t.Crackle(0, 8f, 12, 1f, 0.006f);
            t.Normalize(0.8f);
            return t.Data;
        }

        // A slow swell that joins up at the loop (whole cycles only).
        static Func<float, float> Gust(float length, float floor, int seed = 0) =>
            x => floor + (1 - floor) * (0.5f + 0.3f * Mathf.Sin(x / length * Mathf.PI * 2 * 2 + seed) + 0.2f * Mathf.Sin(x / length * Mathf.PI * 2 * 5 + 1 + seed));

        /// <summary>Each battlefield's own background: wind and birdsong in the meadow, a howl on the fjord, the
        /// surf at the beachhead, rain in the alleys, the foundry's hum.</summary>
        public static float[] Ambience(string army)
        {
            const float L = 12f;
            var t = new Track(L, LoopRate, army.Length * 31 + 7) { Wrap = true };
            // Every loop is wind of some kind: one long noise that overlaps its own start by 1 s, cross-faded.
            void Wind(float vol, float fc, float floor = 0.5f, Filter type = Filter.Low, float q = 0.7f) =>
                t.Noise(0, L + 1f, vol, type, fc, 0, q, 1f, true, 1f, 0, Gust(L, floor));
            switch (army)
            {
                case "knights":   // a meadow: soft breeze, birds
                    Wind(0.5f, 500);
                    for (int i = 0; i < 7; i++)
                    {
                        float at = t.Rand * L, f = 2600 + t.Rand * 1400;
                        int chirps = 2 + (int)(t.Rand * 4);
                        for (int c = 0; c < chirps; c++)
                            t.Tone(at + c * 0.09f, 0.07f, f, f * (1.25f + t.Rand * 0.3f), Wave.Sine, 0.12f, 0.01f);
                    }
                    break;
                case "undead":    // a low, cold moan over the graves
                    Wind(0.6f, 300, 0.4f);
                    t.Noise(0, L + 1f, 0.35f, Filter.Band, 380, 0, 9f, 1f, true, 1f, 0, Gust(L, 0.2f, 2),
                            x => 330 + 70 * Mathf.Sin(x / L * Mathf.PI * 2 * 3));
                    break;
                case "vikings":   // a howling gale
                    Wind(0.5f, 700, 0.35f);
                    t.Noise(0, L + 1f, 0.5f, Filter.Band, 600, 0, 7f, 1f, true, 1f, 0, Gust(L, 0.25f, 1),
                            x => 620 + 180 * Mathf.Sin(x / L * Mathf.PI * 2 * 2) + 60 * Mathf.Sin(x / L * Mathf.PI * 2 * 7));
                    break;
                case "samurai":   // a light breeze and wind chimes
                    Wind(0.4f, 600);
                    var pent = new[] { 76, 79, 81, 84, 86, 88 };
                    for (int i = 0; i < 9; i++)
                    {
                        float at = t.Rand * L, f = Midi(pent[(int)(t.Rand * pent.Length)]);
                        t.Tone(at, 2.2f, f, 0, Wave.Sine, 0.12f);
                        t.Tone(at, 1.2f, f * 2.76f, 0, Wave.Sine, 0.03f);
                    }
                    break;
                case "spartans":  // a dry, hot wind
                    Wind(0.45f, 1200, 0.45f, Filter.Band, 0.8f);
                    Wind(0.3f, 350);
                    break;
                case "asgardians":   // the shimmer of the bridge
                    Wind(0.3f, 450);
                    foreach (var (n, v) in new[] { (69, 0.05f), (76, 0.04f), (81, 0.03f), (88, 0.015f) })
                    {
                        t.Drone(Midi(n), v);
                        t.Drone(Midi(n) * 1.003f, v);
                    }
                    break;
                case "machines":  // the foundry: a deep hum, steam, distant clanks
                    t.Drone(50, 0.12f, Wave.Triangle);
                    t.Drone(100, 0.05f);
                    t.Drone(150, 0.025f);
                    Wind(0.25f, 300);
                    for (int i = 0; i < 5; i++)
                    {
                        float at = t.Rand * L, f = 300 + t.Rand * 500;
                        t.Tone(at, 0.6f, f, 0, Wave.Triangle, 0.1f);
                        t.Tone(at, 0.4f, f * 2.6f, 0, Wave.Sine, 0.05f);
                        t.Noise(at, 0.05f, 0.12f, Filter.Low, 900);
                    }
                    t.Noise(t.Rand * L, 2f, 0.12f, Filter.High, 4500, 0, 0.7f, 0.2f, true, 1f);
                    break;
                case "marines":   // surf rolling in on the beach, guns far off
                    Wind(0.25f, 500);
                    t.Noise(0, L + 1f, 0.8f, Filter.Low, 900, 0, 0.7f, 1f, true, 1f, 0,
                            x => Mathf.Pow(0.5f + 0.5f * Mathf.Sin(x / L * Mathf.PI * 2 * 2 - 1.5f), 2f) * 0.9f + 0.1f,
                            x => 500 + 900 * (0.5f + 0.5f * Mathf.Sin(x / L * Mathf.PI * 2 * 2 - 1.5f)));
                    for (int i = 0; i < 2; i++)
                    {
                        float at = 1 + i * 6 + t.Rand * 3;
                        t.Noise(at, 1.4f, 0.2f, Filter.Low, 160);
                        t.Tone(at, 1f, 50, 30, Wave.Sine, 0.15f);
                    }
                    break;
                case "mafia":     // rain on the alley
                    Wind(0.35f, 2500, 0.8f, Filter.Band, 0.5f);
                    t.Crackle(0, L, 220, 0.18f, 0.0012f);
                    Wind(0.25f, 400, 0.6f);
                    break;
                default:
                    Wind(0.5f, 500);
                    break;
            }
            t.Normalize(0.8f);
            return t.Data;
        }
    }
}
