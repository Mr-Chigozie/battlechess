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
using Unity.Cinemachine;

namespace ChessWars.EditorTools
{
    public static class BuildTools
    {
        const string ScenePath = "Assets/Scenes/Battle.unity";
        const string MaterialFolder = "Assets/Materials";

        // A toon material (ChessWars/Toon) with the stone/grass grain; `grain` sets its size, 0 for none.
        static Material Lit(string name, uint hex, float grain = 0f, float strength = 0.3f, float outline = 0f)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var toon = Shader.Find("ChessWars/Toon");
            if (mat == null)
            {
                mat = new Material(toon);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = toon;
            mat.SetColor("_BaseColor", Armies.Hex(hex));
            mat.SetTexture("_DetailTex", DetailTexture());
            mat.SetFloat("_DetailScale", grain);
            mat.SetFloat("_DetailStrength", grain > 0 ? strength : 0f);
            mat.SetFloat("_OutlineWidth", outline);   // the board and world need no ink lines
            mat.SetFloat("_RimStrength", 0.12f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // A tileable mottled grain (a few octaves of smooth value noise) for stone, wood and grass.
        static Texture2D DetailTexture()
        {
            string path = $"{MaterialFolder}/Grain.png";
            if (!File.Exists(path))
            {
                const int size = 256;
                var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
                var rnd = new System.Random(3);
                float[,] Lattice(int cells)
                {
                    var g = new float[cells, cells];
                    for (int y = 0; y < cells; y++) for (int x = 0; x < cells; x++) g[x, y] = (float)rnd.NextDouble();
                    return g;
                }
                float Sample(float[,] g, int cells, float u, float v)
                {
                    float fx = u * cells, fy = v * cells;
                    int x0 = Mathf.FloorToInt(fx) % cells, y0 = Mathf.FloorToInt(fy) % cells;
                    int x1 = (x0 + 1) % cells, y1 = (y0 + 1) % cells;
                    float tx = Mathf.SmoothStep(0, 1, fx - Mathf.Floor(fx)), ty = Mathf.SmoothStep(0, 1, fy - Mathf.Floor(fy));
                    return Mathf.Lerp(Mathf.Lerp(g[x0, y0], g[x1, y0], tx), Mathf.Lerp(g[x0, y1], g[x1, y1], tx), ty);
                }
                var octaves = new[] { (4, 0.5f), (8, 0.25f), (16, 0.15f), (32, 0.1f) };
                var grids = new float[octaves.Length][,];
                for (int i = 0; i < octaves.Length; i++) grids[i] = Lattice(octaves[i].Item1);
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float u = (float)x / size, v = (float)y / size, n = 0;
                        for (int i = 0; i < octaves.Length; i++) n += Sample(grids[i], octaves[i].Item1, u, v) * octaves[i].Item2;
                        tex.SetPixel(x, y, new Color(n, n, n));
                    }
                File.WriteAllBytes(path, tex.EncodeToPNG());
                AssetDatabase.ImportAsset(path);
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
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
                mat = new Material(Shader.Find("ChessWars/SkyGradient"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = Shader.Find("ChessWars/SkyGradient");
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

        // Re-runs the import rules (toon materials, outline normals) on every character and prop.
        [MenuItem("Chess Wars/Reimport characters and props")]
        public static void ReimportArt()
        {
            foreach (var folder in new[] { "Assets/Characters", "Assets/Resources/Props" })
                AssetDatabase.ImportAsset(folder, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
            Debug.Log("[BuildTools] characters and props reimported");
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
            camGo.AddComponent<CinemachineBrain>();
            // The board view is a Cinemachine camera placed by CameraRig; kill-sequence cameras will blend in and out.
            var boardCam = new GameObject("Board Camera");
            var vcam = boardCam.AddComponent<CinemachineCamera>();
            var lens = LensSettings.Default;
            lens.FieldOfView = 42f;
            lens.NearClipPlane = 0.1f;
            lens.FarClipPlane = 120f;
            vcam.Lens = lens;
            var rig = boardCam.AddComponent<CameraRig>();
            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.None;   // MSAA from the quality tier instead

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = Armies.Hex(0xfff1dc);
            sun.intensity = 1.6f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.LookRotation(-new Vector3(6f, 12f, 5f));   // as the web game: from the player's side, so their own army is front-lit
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Armies.Hex(0xd8e2ff);
            RenderSettings.ambientEquatorColor = Armies.Hex(0x9a9a8e);
            RenderSettings.ambientGroundColor = Armies.Hex(0x4a4036);
            RenderSettings.fog = false;

            var boardGo = new GameObject("Board");
            var board = boardGo.AddComponent<BoardView>();
            board.lightSquare = Lit("SquareLight", 0xd7dcae, 1.1f, 0.22f);
            board.darkSquare = Lit("SquareDark", 0x5f7d3b, 1.1f, 0.22f);
            board.frame = Lit("BoardFrame", 0x4a3324, 1.4f, 0.3f);
            board.selectMarker = Marker("MarkSelected", 0xf0c060, 0.9f);
            board.moveMarker = Marker("MarkMove", 0xf6e3a8, 0.75f);
            board.captureMarker = Marker("MarkCapture", 0xe0503c, 0.7f);
            board.lastMoveMarker = Marker("MarkLastMove", 0xe9c264, 0.3f);

            var volume = new GameObject("Post").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = PostProfile();

            var world = new GameObject("Battlefield").AddComponent<Battlefield>();
            world.skybox = SkyMaterial();
            world.lit = Lit("WorldLit", 0xffffff, 0.6f, 0.35f);
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
            var cam = Camera.main;
            var rt = new RenderTexture(width, height, 24) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.aspect = (float)width / height;
            rig.Fit();
            // Outside play mode no Cinemachine brain runs, so put the real camera where the board camera is.
            cam.transform.SetPositionAndRotation(rig.transform.position, rig.transform.rotation);
            Shader.SetGlobalFloat("_CW_OutlineScale", 1f);
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

        // Every synthesised sound (Synth.cs) as WAV files, to listen to outside the game: Builds/Sounds, or the folder
        // in the CW_SOUND_DIR environment variable. -executeMethod ChessWars.EditorTools.BuildTools.ExportSounds
        [MenuItem("Chess Wars/Export sounds")]
        public static void ExportSounds()
        {
            string folder = System.Environment.GetEnvironmentVariable("CW_SOUND_DIR");
            if (string.IsNullOrEmpty(folder)) folder = "Builds/Sounds";
            Directory.CreateDirectory(folder);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var effects = Synth.Effects();
            var (calm, drums) = Synth.Music();
            var fire = Synth.Fire();
            Debug.Log($"Sounds made in {watch.ElapsedMilliseconds} ms");
            foreach (var kv in effects)
                for (int i = 0; i < kv.Value.Length; i++)
                    Wav($"{folder}/{kv.Key}{(kv.Value.Length > 1 ? "-" + (i + 1) : "")}.wav", kv.Value[i], Synth.SfxRate);
            Wav($"{folder}/music-calm.wav", calm, Synth.LoopRate);
            Wav($"{folder}/music-drums.wav", drums, Synth.LoopRate);
            var both = new float[calm.Length];
            for (int i = 0; i < both.Length; i++) both[i] = calm[i] * 0.6f + drums[i] * 0.6f;
            Wav($"{folder}/music-battle.wav", both, Synth.LoopRate);
            Wav($"{folder}/fire.wav", fire, Synth.LoopRate);
            foreach (var army in Themes.All.Keys) Wav($"{folder}/ambience-{army}.wav", Synth.Ambience(army), Synth.LoopRate);
            Debug.Log($"Sounds written to {Path.GetFullPath(folder)}");
        }

        static void Wav(string path, float[] data, int rate)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + data.Length * 2);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(data.Length * 2);
            foreach (var x in data) w.Write((short)Mathf.Clamp(Mathf.RoundToInt(x * 32767f), -32768, 32767));
        }

        // A portrait of each army's King for the army cards: three-quarter view, transparent background, written to
        // Assets/Resources/Portraits/<army id>.png. -executeMethod ChessWars.EditorTools.BuildTools.Portraits
        [MenuItem("Chess Wars/Render army portraits")]
        public static void Portraits()
        {
            const string folder = "Assets/Resources/Portraits";
            const int size = 384;
            EditorSceneManager.OpenScene(ScenePath);
            Directory.CreateDirectory(folder);
            ShaderUtil.allowAsyncCompilation = false;
            Shader.SetGlobalFloat("_CW_OutlineScale", 1f);
            var cam = Camera.main;
            cam.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing = false;   // post would flatten the alpha
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.fieldOfView = 30f;
            var root = new GameObject("Portrait").transform;
            bool warmed = false;
            foreach (var id in Armies.All.Keys)
            {
                var unit = UnitView.Spawn(id, 'k', 'w', 0, 0, root);
                unit.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0, 24f, 0));   // the models face +Z
                float h = unit.Height;
                var look = new Vector3(0, h * 0.56f, 0);
                cam.transform.position = look + new Vector3(0.35f, 0.18f, 1f).normalized * h * 1.85f;
                cam.transform.LookAt(look);
                var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                cam.targetTexture = rt;
                cam.aspect = 1f;
                if (!warmed) { cam.Render(); warmed = true; }   // the first frame renders before materials are ready
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply();
                string path = $"{folder}/{id}.png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                RenderTexture.active = null;
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
                Object.DestroyImmediate(unit.gameObject);
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Default;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.maxTextureSize = 512;
                importer.SaveAndReimport();
            }
            Object.DestroyImmediate(root.gameObject);
            Debug.Log($"[BuildTools] portraits written to {folder}");
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
