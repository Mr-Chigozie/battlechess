// Battle effects, ported from the web game's js/fx3d.js: spark bursts, glows, dust, shockwave rings, tracers and
// orbs, beams, lightning, camera shake, hit-stop and slow motion. Purely visual.
// Particles come from three shared systems (sparks fall, glows float, dust billows) fed with Emit(), so a kill
// costs no allocations beyond a few short-lived line renderers.
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChessWars.Game
{
    public class Fx : MonoBehaviour
    {
        public static Fx I { get; private set; }

        /// <summary>Current camera shake in world units; CameraShake adds it to whichever camera is live.</summary>
        public static float ShakeAmp;

        ParticleSystem sparks, glows, dust;
        Material glowMat, lineMat;
        Coroutine timeRoutine;

        public static Fx Create(Material particle, Transform parent)
        {
            var go = new GameObject("Fx");
            go.transform.SetParent(parent, false);
            I = go.AddComponent<Fx>();
            I.Setup(particle);
            return I;
        }

        void Setup(Material particle)
        {
            // The scene's particle material blends by alpha (dust); glows and lines add light instead.
            glowMat = new Material(particle) { name = "Glow" };
            glowMat.SetFloat("_Blend", 2f);
            glowMat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            glowMat.SetFloat("_DstBlend", (float)BlendMode.One);
            lineMat = new Material(glowMat) { name = "GlowLine" };
            lineMat.SetTexture("_BaseMap", null);
            sparks = System("Sparks", glowMat, 0.4f, 1f, 0.2f);
            glows = System("Glows", glowMat, 0f, 1f, 1.6f);
            dust = System("Dust", particle, 0f, 1f, 2.2f);
        }

        ParticleSystem System(string name, Material material, float gravity, float sizeFrom, float sizeTo)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.maxParticles = 1500;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = gravity;
            main.playOnAwake = false;
            var emission = ps.emission;
            emission.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;
            var fade = ps.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                             new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.8f, 0.6f), new GradientAlphaKey(0, 1) });
            fade.color = gradient;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, sizeFrom, 1, sizeTo));
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            ps.Play();
            return ps;
        }

        static void Emit(ParticleSystem ps, Vector3 pos, Vector3 vel, float size, float life, Color color)
        {
            var p = new ParticleSystem.EmitParams
            {
                position = pos, velocity = vel, startSize = size, startLifetime = life, startColor = color, applyShapeToPosition = false
            };
            ps.Emit(p, 1);
        }

        // ---------------------------------------------------------------- particles

        /// <summary>Sparks flung out from a point (fx3d burst): `power` is speed, `size` scales the sparks.</summary>
        public void Burst(Vector3 pos, Color color, int count = 24, float power = 3f, float size = 1f, float life = 0.7f)
        {
            for (int i = 0; i < count; i++)
            {
                var dir = new Vector3(Random.value - 0.5f, Random.value * 0.8f, Random.value - 0.5f).normalized;
                Emit(sparks, pos, dir * power * (0.4f + Random.value), 0.09f * size * (0.6f + Random.value), life * (0.6f + Random.value * 0.6f), color);
            }
        }

        /// <summary>A soft glowing ball that drifts and fades (souls, muzzle glows, orb trails).</summary>
        public void Glow(Vector3 pos, Color color, float size, float life, Vector3 velocity = default) =>
            Emit(glows, pos, velocity, size, life, color);

        public void Ember(Vector3 pos, Color color, float rise = 1f) =>
            Emit(glows, pos, new Vector3((Random.value - 0.5f) * 0.4f, (0.5f + Random.value * 0.9f) * rise, (Random.value - 0.5f) * 0.4f),
                 0.05f + Random.value * 0.06f, 1.6f + Random.value * 1.6f, color);

        /// <summary>Puffs of dust kicked up around a point on the ground.</summary>
        public void Dust(Vector3 pos, int count = 14)
        {
            var color = new Color(0.73f, 0.68f, 0.58f, 0.5f);
            for (int i = 0; i < count; i++)
            {
                float a = Random.value * Mathf.PI * 2;
                var at = pos + new Vector3((Random.value - 0.5f) * 0.4f, 0.05f, (Random.value - 0.5f) * 0.4f);
                Emit(dust, at, new Vector3(Mathf.Cos(a), 0.25f + Random.value * 0.4f, Mathf.Sin(a)) * 0.9f, 0.14f + Random.value * 0.1f, 1.1f, color);
            }
        }

        // ---------------------------------------------------------------- lines

        LineRenderer Line(string name, Color color, float width, int points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = lineMat;
            line.positionCount = points;
            line.widthMultiplier = width;
            line.startColor = line.endColor = color;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.numCapVertices = 2;
            return line;
        }

        /// <summary>A glowing ring that races out across the ground.</summary>
        public void Shockwave(Vector3 pos, Color color) => StartCoroutine(ShockwaveRoutine(pos, color));

        IEnumerator ShockwaveRoutine(Vector3 pos, Color color)
        {
            const int n = 40;
            var line = Line("Shockwave", color, 0.08f, n);
            line.loop = true;
            for (float t = 0; t < 1; t += Time.deltaTime / 0.55f)
            {
                float radius = Mathf.Lerp(0.1f, 1.4f, 1 - (1 - t) * (1 - t));
                for (int i = 0; i < n; i++)
                {
                    float a = i * Mathf.PI * 2 / n;
                    line.SetPosition(i, new Vector3(pos.x + Mathf.Cos(a) * radius, 0.12f, pos.z + Mathf.Sin(a) * radius));
                }
                line.widthMultiplier = Mathf.Lerp(0.1f, 0.02f, t);
                var c = color;
                c.a = 1 - t;
                line.startColor = line.endColor = c;
                yield return null;
            }
            Destroy(line.gameObject);
        }

        /// <summary>A tracer round (fast, straight) or a magic orb (slower, arcing, trailing glow) flying at a target.</summary>
        public IEnumerator Projectile(Vector3 from, Vector3 to, Color color, bool tracer)
        {
            float dist = Vector3.Distance(from, to);
            float seconds = tracer ? dist / 22f : dist / 6f;
            LineRenderer streak = tracer ? Line("Tracer", Color.Lerp(color, Color.white, 0.5f), 0.035f, 2) : null;
            var dir = (to - from).normalized;
            for (float t = 0; t < 1; t += Time.deltaTime / seconds)
            {
                var p = Vector3.Lerp(from, to, t);
                if (tracer)
                {
                    streak.SetPosition(0, p - dir * 0.32f);
                    streak.SetPosition(1, p);
                }
                else
                {
                    p.y += Mathf.Sin(t * Mathf.PI) * 0.35f;
                    Glow(p, Color.white, 0.12f, 0.08f);
                    Glow(p, color, 0.26f, 0.3f);
                }
                yield return null;
            }
            if (streak != null) Destroy(streak.gameObject);
        }

        /// <summary>A white-cored beam of light between two points that swells and fades.</summary>
        public IEnumerator Beam(Vector3 from, Vector3 to, Color color, float seconds = 0.7f)
        {
            var halo = Line("Beam", color, 0.2f, 2);
            var core = Line("Beam core", Color.white, 0.07f, 2);
            for (float t = 0; t < 1; t += Time.deltaTime / seconds)
            {
                float grow = Mathf.Min(1f, t * 5f);
                float width = Mathf.Sin(Mathf.Min(1f, t * 1.2f) * Mathf.PI) * (0.9f + Random.value * 0.2f);
                var end = Vector3.Lerp(from, to, grow);
                foreach (var l in new[] { halo, core })
                {
                    l.SetPosition(0, from);
                    l.SetPosition(1, end);
                }
                halo.widthMultiplier = 0.2f * width;
                core.widthMultiplier = 0.07f * width;
                yield return null;
            }
            Destroy(halo.gameObject);
            Destroy(core.gameObject);
        }

        /// <summary>Jagged bolts flickering between two points, alternating white and the army's colour.</summary>
        public IEnumerator Lightning(Vector3 from, Vector3 to, Color color, int strikes = 4)
        {
            var bolt = Line("Lightning", color, 0.05f, 11);
            for (int s = 0; s < strikes; s++)
            {
                for (int i = 0; i <= 10; i++)
                {
                    var p = Vector3.Lerp(from, to, i / 10f);
                    if (i > 0 && i < 10) p += new Vector3(Random.value - 0.5f, Random.value - 0.5f, Random.value - 0.5f) * 0.35f;
                    bolt.SetPosition(i, p);
                }
                bolt.startColor = bolt.endColor = s % 2 == 1 ? Color.white : color;
                yield return new WaitForSeconds(0.07f);
            }
            Destroy(bolt.gameObject);
        }

        /// <summary>A bright slash mark hanging in the air for a moment (the Samurai's cut).</summary>
        public void Cut(Vector3 center, float yaw)
        {
            StartCoroutine(CutRoutine(center, yaw));
        }

        IEnumerator CutRoutine(Vector3 center, float yaw)
        {
            var line = Line("Cut", Color.white, 0.03f, 2);
            var along = Quaternion.Euler(0, yaw, 0) * Quaternion.Euler(0, 0, 34f) * Vector3.right;
            for (float t = 0; t < 1; t += Time.deltaTime / 0.5f)
            {
                float half = 0.375f * (1 + 0.3f * t);
                line.SetPosition(0, center - along * half);
                line.SetPosition(1, center + along * half);
                var c = Color.white;
                c.a = t < 0.2f ? 1 : 1 - (t - 0.2f) / 0.8f;
                line.startColor = line.endColor = c;
                yield return null;
            }
            Destroy(line.gameObject);
        }

        // ---------------------------------------------------------------- camera and time

        public static void Shake(float amount) => ShakeAmp = Mathf.Max(ShakeAmp, amount);

        void Update()
        {
            ShakeAmp = Mathf.Max(0f, ShakeAmp - Time.unscaledDeltaTime * 2.2f);
        }

        /// <summary>Everything holds still for a split second on contact; combos then drop into slow motion.</summary>
        public void Hitstop(float realSeconds, float slowFactor = 1f, float slowSeconds = 0f)
        {
            if (timeRoutine != null) StopCoroutine(timeRoutine);
            timeRoutine = StartCoroutine(TimeRoutine(realSeconds, slowFactor, slowSeconds));
        }

        IEnumerator TimeRoutine(float stop, float slowFactor, float slowSeconds)
        {
            Time.timeScale = 0.02f;
            yield return new WaitForSecondsRealtime(stop);
            if (slowSeconds > 0)
            {
                Time.timeScale = slowFactor;
                yield return new WaitForSecondsRealtime(slowSeconds);
            }
            Time.timeScale = 1f;
            timeRoutine = null;
        }

        void OnDestroy()
        {
            Time.timeScale = 1f;
            if (I == this) I = null;
        }
    }
}
