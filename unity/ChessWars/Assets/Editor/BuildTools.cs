// Scene setup, preview renders and the Android build, all runnable from the command line:
//   -executeMethod ChessWars.EditorTools.BuildTools.CreateScene    (writes Assets/Scenes/Battle.unity)
//   -executeMethod ChessWars.EditorTools.BuildTools.Preview        (renders Builds/preview-*.png)
//   -buildTarget Android -executeMethod ChessWars.EditorTools.BuildTools.BuildAndroid   (Builds/ChessWars.apk)
using System.IO;
using ChessWars.Game;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ChessWars.EditorTools
{
    public static class BuildTools
    {
        const string ScenePath = "Assets/Scenes/Battle.unity";
        const string MaterialFolder = "Assets/Materials";

        static Material Lit(string name, uint hex, float smoothness = 0.2f)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", Armies.Hex(hex));
            mat.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // An unlit see-through colour for the square markers.
        static Material Marker(string name, uint hex, float alpha)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            var c = Armies.Hex(hex);
            c.a = alpha;
            mat.SetColor("_BaseColor", c);
            mat.SetFloat("_Surface", 1);
            mat.SetFloat("_Blend", 0);
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // A soft round dot for particles (weather, embers), so they aren't drawn as squares.
        static Texture2D SoftDot()
        {
            string path = $"{MaterialFolder}/SoftDot.png";
            if (!File.Exists(path))
            {
                var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x, y), new Vector2(31.5f, 31.5f)) / 32f;
                        float a = Mathf.Clamp01(1f - d);
                        tex.SetPixel(x, y, new Color(1, 1, 1, a * a));
                    }
                File.WriteAllBytes(path, tex.EncodeToPNG());
                AssetDatabase.ImportAsset(path);
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Material ParticleMaterial()
        {
            string path = $"{MaterialFolder}/Particle.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetTexture("_BaseMap", SoftDot());
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Surface", 1);
            mat.SetFloat("_Blend", 0);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material SkyMaterial()
        {
            string path = $"{MaterialFolder}/Sky.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Skybox/Procedural"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetFloat("_SunSize", 0.03f);
            return mat;
        }

        // Glow on magic, fire and eyes; gentle grading. Turned off at run time on the Low tier.
        static VolumeProfile PostProfile()
        {
            string path = "Assets/Settings/Rendering/BattlePost.asset";
            AssetDatabase.DeleteAsset(path);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);
            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(0.95f);
            bloom.intensity.Override(0.9f);
            bloom.scatter.Override(0.6f);
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.Neutral);
            var color = profile.Add<ColorAdjustments>(true);
            color.contrast.Override(12f);
            color.saturation.Override(14f);
            color.postExposure.Override(0.1f);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.24f);
            vignette.smoothness.Override(0.45f);
            foreach (var component in profile.components) AssetDatabase.AddObjectToAsset(component, profile);
            EditorUtility.SetDirty(profile);
            return profile;
        }

        [MenuItem("Chess Wars/Create battle scene")]
        public static void CreateScene()
        {
            Directory.CreateDirectory(MaterialFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 42f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 120f;
            cam.clearFlags = CameraClearFlags.Skybox;
            var rig = camGo.AddComponent<CameraRig>();
            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.None;   // MSAA from the quality tier instead

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = Armies.Hex(0xfff1dc);
            sun.intensity = 1.6f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(52f, -30f, 0f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Armies.Hex(0xd8e2ff);
            RenderSettings.ambientEquatorColor = Armies.Hex(0x9a9a8e);
            RenderSettings.ambientGroundColor = Armies.Hex(0x4a4036);
            RenderSettings.fog = false;

            var boardGo = new GameObject("Board");
            var board = boardGo.AddComponent<BoardView>();
            board.lightSquare = Lit("SquareLight", 0xd7dcae);
            board.darkSquare = Lit("SquareDark", 0x5f7d3b);
            board.frame = Lit("BoardFrame", 0x4a3324, 0.35f);
            board.selectMarker = Marker("MarkSelected", 0xf0c060, 0.9f);
            board.moveMarker = Marker("MarkMove", 0xf6e3a8, 0.75f);
            board.captureMarker = Marker("MarkCapture", 0xe0503c, 0.7f);
            board.lastMoveMarker = Marker("MarkLastMove", 0xe9c264, 0.3f);

            var volume = new GameObject("Post").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = PostProfile();

            var world = new GameObject("Battlefield").AddComponent<Battlefield>();
            world.skybox = SkyMaterial();
            world.lit = Lit("WorldLit", 0xffffff);
            world.particle = ParticleMaterial();
            world.sun = sun;

            var battle = new GameObject("Battle").AddComponent<BattleController>();
            battle.board = board;
            battle.cameraRig = rig;
            battle.battlefield = world;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[BuildTools] scene saved: {ScenePath}");
        }

        // Renders the opening position to PNGs (phone portrait and landscape) without entering play mode.
        public static void Preview()
        {
            EditorSceneManager.OpenScene(ScenePath);
            var battle = Object.FindFirstObjectByType<BattleController>();
            var theme = Themes.For(battle.blackArmy);
            battle.battlefield.Build(theme);
            battle.board.Build(theme);
            var unitRoot = new GameObject("Units").transform;
            var pos = Rules.Rules.InitialPosition();
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                    if (pos.Board[row, col] is Rules.Piece p)
                        UnitView.Spawn(p.Color == 'w' ? battle.whiteArmy : battle.blackArmy, p.Type, p.Color, row, col, unitRoot);
            Directory.CreateDirectory("Builds");
            ShaderUtil.allowAsyncCompilation = false;   // otherwise the first frame draws with placeholder shaders
            // The very first frame renders before lighting and materials are fully set up; throw it away.
            Render(battle.cameraRig, 64, 64, "Builds/warmup.png");
            File.Delete("Builds/warmup.png");
            Render(battle.cameraRig, 720, 1560, "Builds/preview-phone.png");
            Render(battle.cameraRig, 1600, 900, "Builds/preview-wide.png");
            // A bright world too, to check the daylight look.
            var meadow = Themes.For("knights");
            battle.battlefield.Build(meadow);
            battle.board.Build(meadow);
            Render(battle.cameraRig, 64, 64, "Builds/warmup.png");
            File.Delete("Builds/warmup.png");
            Render(battle.cameraRig, 1600, 900, "Builds/preview-meadow.png");
            Debug.Log("[BuildTools] previews written to Builds/");
        }

        static void Render(CameraRig rig, int width, int height, string file)
        {
            var cam = rig.GetComponent<Camera>();
            var rt = new RenderTexture(width, height, 24) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.aspect = (float)width / height;
            rig.Fit();
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(file, tex.EncodeToPNG());
            RenderTexture.active = null;
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }

        public static void BuildAndroid()
        {
            Directory.CreateDirectory("Builds");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/ChessWars.apk",
                target = BuildTarget.Android,
                options = BuildOptions.None
            });
            var s = report.summary;
            Debug.Log($"[BuildTools] Android build {s.result}: {s.outputPath} ({s.totalSize / (1024f * 1024f):0.0} MB) in {s.totalTime}, {s.totalErrors} errors");
            if (s.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }
    }
}
