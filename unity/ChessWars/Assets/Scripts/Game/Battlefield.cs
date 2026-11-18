// The world around the board: a stone platform, braziers, the ground, props, sky, fog, sun and weather,
// dressed per army (Themes.cs). Purely visual. Scales down on the Low quality tier for cheap phones.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChessWars.Game
{
    public class Battlefield : MonoBehaviour
    {
        public Material skybox;      // ChessWars/SkyGradient, coloured per theme
        public Material lit;         // ChessWars/Toon template with stone/grass detail, copied per surface colour
        public Material particle;    // URP Particles/Unlit, soft additive dots
        public Light sun;

        Transform root;

        static int Tier => QualitySettings.GetQualityLevel();   // 0 Low, 1 Medium, 2 High

        // A copy of the toon template in one colour; `grain` is the size of the stone/grass detail (0 = none).
        Material Colored(Color c, float grain = 0.6f, float strength = 0.35f)
        {
            var m = new Material(lit);
            m.SetColor("_BaseColor", c);
            m.SetFloat("_DetailScale", grain);
            m.SetFloat("_DetailStrength", grain > 0 ? strength : 0f);
            return m;
        }

        public void Build(Theme theme, int seed = 7)
        {
            if (root != null) DestroyImmediate(root.gameObject);
            root = new GameObject("World").transform;
            root.SetParent(transform, false);
            Atmosphere(theme);
            Platform(theme);
            Ground(theme);
            Scatter(theme, seed);
            if (Application.isPlaying) Weather(theme);
        }

        void Atmosphere(Theme theme)
        {
            // Gradient sky (ChessWars/SkyGradient): sky above, a bright horizon band, the fog colour below, a sun glow.
            skybox.SetColor("_Top", theme.Sky * theme.Exposure);
            skybox.SetColor("_Horizon", theme.Horizon);
            skybox.SetColor("_Bottom", theme.Fog);
            skybox.SetVector("_SunDir", -sun.transform.forward);
            skybox.SetColor("_SunColor", theme.Sun);
            RenderSettings.skybox = skybox;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Color.Lerp(theme.Sky, Color.white, 0.65f);
            RenderSettings.ambientEquatorColor = Color.Lerp(theme.Horizon, Color.gray, 0.3f);
            RenderSettings.ambientGroundColor = Color.Lerp(theme.Ground, Color.black, 0.4f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = theme.Fog;
            RenderSettings.fogStartDistance = 18f;
            RenderSettings.fogEndDistance = 48f;
            sun.color = theme.Sun;
            sun.intensity = theme.SunIntensity;
            sun.shadows = Tier == 0 ? LightShadows.None : Tier == 1 ? LightShadows.Hard : LightShadows.Soft;
        }

        GameObject Block(string name, Vector3 pos, Vector3 size, Material mat, bool shadows = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(root, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            DestroyImmediate(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            go.isStatic = true;
            return go;
        }

        // Two stone steps under the board, a coloured trim, a corner pillar and a lit brazier at each corner.
        void Platform(Theme theme)
        {
            var stone = Colored(theme.Platform);
            var darker = Colored(Color.Lerp(theme.Platform, Color.black, 0.25f));
            var trim = Colored(theme.Trim, 1.2f, 0.25f);
            Block("Step upper", new Vector3(0, -0.32f, 0), new Vector3(9.7f, 0.5f, 9.7f), stone);
            Block("Step lower", new Vector3(0, -0.72f, 0), new Vector3(11.2f, 0.4f, 11.2f), darker);
            Block("Trim", new Vector3(0, -0.07f, 0), new Vector3(9.9f, 0.08f, 9.9f), trim);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    var corner = new Vector3(sx * 5.15f, 0, sz * 5.15f);
                    Block("Pillar", corner + new Vector3(0, -0.2f, 0), new Vector3(0.7f, 0.9f, 0.7f), darker);
                    var brazier = Prop("Brazier", corner + new Vector3(0, 0.25f, 0), 0, 1.1f);
                    if (brazier != null && Tier > 0 && Application.isPlaying) Embers(brazier.transform);
                }
        }

        void Ground(Theme theme)
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ground.name = "Ground";
            ground.transform.SetParent(root, false);
            ground.transform.localPosition = new Vector3(0, -0.95f, 0);
            ground.transform.localScale = new Vector3(110f, 0.05f, 110f);
            DestroyImmediate(ground.GetComponent<Collider>());
            ground.GetComponent<Renderer>().sharedMaterial = Colored(theme.Ground, 0.12f, 0.45f);
            ground.isStatic = true;
        }

        GameObject Prop(string name, Vector3 pos, float yaw, float scale)
        {
            var prefab = Resources.Load<GameObject>("Props/" + name);
            if (prefab == null) return null;
            var go = Instantiate(prefab, root);
            go.name = name;
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0, yaw, 0);
            go.transform.localScale = Vector3.one * scale;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = Tier == 2 ? ShadowCastingMode.On : ShadowCastingMode.Off;
            go.isStatic = true;
            return go;
        }

        // A ring of scenery outside the platform. Tall things stay further out so they never hide the board.
        void Scatter(Theme theme, int seed)
        {
            var rng = new System.Random(seed);
            int count = Tier == 0 ? 22 : Tier == 1 ? 38 : 56;
            for (int i = 0; i < count; i++)
            {
                string name = theme.Props[rng.Next(theme.Props.Length)];
                bool tall = name.StartsWith("Tree") || name == "Column" || name == "Pylon" || name == "Banner";
                float angle = (float)(rng.NextDouble() * Mathf.PI * 2);
                float radius = (tall ? 9.5f : 7f) + (float)rng.NextDouble() * (tall ? 14f : 9f);
                var pos = new Vector3(Mathf.Cos(angle) * radius, -0.92f, Mathf.Sin(angle) * radius);
                if (name == "FloatingRock") pos.y = 1.5f + (float)rng.NextDouble() * 3f;
                Prop(name, pos, (float)rng.NextDouble() * 360f, 0.85f + (float)rng.NextDouble() * 0.6f);
            }
            if (Application.isPlaying) StaticBatchingUtility.Combine(root.gameObject);
        }

        ParticleSystem Particles(string name, Transform parent, int max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.maxParticles = max;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = particle;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        void Embers(Transform brazier)
        {
            var ps = Particles("Embers", brazier, 24);
            ps.transform.localPosition = new Vector3(0, 0.8f, 0);
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.09f);
            main.startColor = new ParticleSystem.MinMaxGradient(Armies.Hex(0xffb347), Armies.Hex(0xff5a1a));
            main.gravityModifier = -0.15f;
            var emission = ps.emission;
            emission.rateOverTime = 10f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 18f;
            shape.radius = 0.12f;
            shape.rotation = new Vector3(-90, 0, 0);
            ps.Play();
        }

        // Falling or drifting particles over the whole field.
        void Weather(Theme theme)
        {
            if (theme.Weather == Game.Weather.None) return;
            int max = Tier == 0 ? 60 : Tier == 1 ? 140 : 280;
            var ps = Particles("Weather", root, max);
            ps.transform.localPosition = new Vector3(0, 9f, 0);
            var main = ps.main;
            var emission = ps.emission;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(26f, 1f, 26f);
            main.startLifetime = 9f;
            emission.rateOverTime = max / 9f;
            var noise = ps.noise;
            noise.enabled = Tier > 0;
            noise.strength = 0.4f;
            noise.frequency = 0.3f;
            switch (theme.Weather)
            {
                case Game.Weather.Snow:
                    Set(main, 0.06f, 0.14f, Color.white, 0.12f); break;
                case Game.Weather.Ash:
                    Set(main, 0.04f, 0.09f, Armies.Hex(0x9aa89a), 0.05f); break;
                case Game.Weather.Pollen:
                    Set(main, 0.03f, 0.06f, Armies.Hex(0xfff4b0), 0.02f); break;
                case Game.Weather.Petals:
                    Set(main, 0.06f, 0.11f, Armies.Hex(0xf6b8c8), 0.06f); break;
                case Game.Weather.Dust:
                    Set(main, 0.05f, 0.12f, Armies.Hex(0xe6cf9c), 0.02f); main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.2f); shape.rotation = new Vector3(0, 0, 80); break;
                case Game.Weather.Stars:
                    Set(main, 0.04f, 0.1f, Armies.Hex(0xc8e8ff), -0.01f); break;
                case Game.Weather.Motes:
                    Set(main, 0.03f, 0.07f, Armies.Hex(0xff8a5a), -0.03f); break;
                case Game.Weather.Smoke:
                    Set(main, 0.25f, 0.6f, new Color(0.7f, 0.7f, 0.68f, 0.18f), 0.01f); break;
            }
            ps.Play();
        }

        static void Set(ParticleSystem.MainModule main, float minSize, float maxSize, Color color, float gravity)
        {
            main.startSize = new ParticleSystem.MinMaxCurve(minSize, maxSize);
            main.startColor = color;
            main.gravityModifier = gravity;
            main.startSpeed = 0.2f;
        }
    }
}
