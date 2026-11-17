// One-shot project configuration for the Android build, run from the command line:
//   Unity.exe -batchmode -quit -projectPath unity/ChessWars -executeMethod ChessWars.EditorTools.ProjectSetup.Run
// Safe to run again: it rewrites the same settings and render pipeline assets.
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ChessWars.EditorTools
{
    public static class ProjectSetup
    {
        const string RenderingFolder = "Assets/Settings/Rendering";

        // Quality tiers from cheap phones up. Low drops shadows and renders at 75% resolution.
        struct Tier
        {
            public string Name;
            public float RenderScale;
            public int Msaa;
            public bool Shadows;
            public bool SoftShadows;
            public int ShadowResolution;
            public float ShadowDistance;
        }

        static readonly Tier[] Tiers =
        {
            new Tier { Name = "Low", RenderScale = 0.75f, Msaa = 1, Shadows = false, SoftShadows = false, ShadowResolution = 512, ShadowDistance = 0 },
            new Tier { Name = "Medium", RenderScale = 0.9f, Msaa = 2, Shadows = true, SoftShadows = false, ShadowResolution = 1024, ShadowDistance = 20 },
            new Tier { Name = "High", RenderScale = 1f, Msaa = 4, Shadows = true, SoftShadows = true, ShadowResolution = 2048, ShadowDistance = 25 },
        };

        public static void Run()
        {
            Directory.CreateDirectory(RenderingFolder);
            var assets = new UniversalRenderPipelineAsset[Tiers.Length];
            for (int i = 0; i < Tiers.Length; i++) assets[i] = PipelineFor(Tiers[i]);
            ConfigureQuality(assets);
            GraphicsSettings.defaultRenderPipeline = assets[Tiers.Length - 1];
            ConfigurePlayer();
            AssetDatabase.SaveAssets();
            Debug.Log($"[ProjectSetup] done: {Tiers.Length} quality tiers, Android player settings applied.");
        }

        static UniversalRenderPipelineAsset PipelineFor(Tier tier)
        {
            string rendererPath = $"{RenderingFolder}/Renderer.asset";
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, rendererPath);
            }
            string path = $"{RenderingFolder}/URP-{tier.Name}.asset";
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            if (asset == null)
            {
                asset = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(asset, path);
            }
            var so = new SerializedObject(asset);
            Set(so, "m_RenderScale", tier.RenderScale);
            Set(so, "m_MSAA", tier.Msaa);
            Set(so, "m_SupportsHDR", false);                 // cheaper framebuffers; the look doesn't rely on HDR
            Set(so, "m_MainLightShadowsSupported", tier.Shadows);
            Set(so, "m_MainLightShadowmapResolution", tier.ShadowResolution);
            Set(so, "m_ShadowDistance", tier.ShadowDistance);
            Set(so, "m_ShadowCascadeCount", 1);
            Set(so, "m_SoftShadowsSupported", tier.SoftShadows);
            Set(so, "m_AdditionalLightsRenderingMode", 0);   // sun and sky only: no extra per-pixel lights
            Set(so, "m_RequireDepthTexture", false);
            Set(so, "m_RequireOpaqueTexture", false);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            return asset;
        }

        static void Set(SerializedObject so, string field, float value) { var p = so.FindProperty(field); if (p != null) p.floatValue = value; else Missing(field); }
        static void Set(SerializedObject so, string field, int value) { var p = so.FindProperty(field); if (p != null) p.intValue = value; else Missing(field); }
        static void Set(SerializedObject so, string field, bool value) { var p = so.FindProperty(field); if (p != null) p.boolValue = value; else Missing(field); }
        static void Missing(string field) => Debug.LogWarning($"[ProjectSetup] URP asset has no field {field}");

        // Exactly three quality levels, Low / Medium / High, each with its own pipeline asset. Android starts on Medium;
        // the game steps down at run time if a phone can't keep up.
        static void ConfigureQuality(UniversalRenderPipelineAsset[] assets)
        {
            var qualityAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0];
            var so = new SerializedObject(qualityAsset);
            var levels = so.FindProperty("m_QualitySettings");
            while (levels.arraySize > Tiers.Length) levels.DeleteArrayElementAtIndex(levels.arraySize - 1);
            while (levels.arraySize < Tiers.Length) levels.InsertArrayElementAtIndex(levels.arraySize - 1);
            for (int i = 0; i < Tiers.Length; i++)
            {
                var level = levels.GetArrayElementAtIndex(i);
                level.FindPropertyRelative("name").stringValue = Tiers[i].Name;
                level.FindPropertyRelative("customRenderPipeline").objectReferenceValue = assets[i];
                level.FindPropertyRelative("vSyncCount").intValue = 0;
                level.FindPropertyRelative("antiAliasing").intValue = 0;   // MSAA lives in the URP asset
                level.FindPropertyRelative("globalTextureMipmapLimit").intValue = i == 0 ? 1 : 0;
                level.FindPropertyRelative("anisotropicTextures").intValue = 0;
                level.FindPropertyRelative("skinWeights").intValue = i == 0 ? 2 : 4;  // bones per vertex (ours use 1)
            }
            so.FindProperty("m_CurrentQuality").intValue = 1;
            var perPlatform = so.FindProperty("m_PerPlatformDefaultQuality");
            for (int i = 0; i < perPlatform.arraySize; i++)
            {
                var entry = perPlatform.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative("second").intValue = entry.FindPropertyRelative("first").stringValue == "Android" ? 1 : 2;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Chess Wars";
            PlayerSettings.productName = "Chess Wars";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;

            var android = NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(android, "com.chesswars.game");
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.Medium);
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;  // Android 7.1+, the lowest Unity 6.3 supports
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            // 32-bit ARM too: many cheap phones still run 32-bit Android. x86_64 runs natively on PC emulators
            // (ARM code crashes in their translation layer) and Chromebooks; Play sends each phone only its own one.
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARMv7 | AndroidArchitecture.ARM64 | AndroidArchitecture.X86_64;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.gpuSkinning = true;
            PlayerSettings.SetMobileMTRendering(android, true);

            // Touch input through the new Input System only.
            var playerAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0];
            var so = new SerializedObject(playerAsset);
            var handler = so.FindProperty("activeInputHandler");
            if (handler != null) { handler.intValue = 1; so.ApplyModifiedPropertiesWithoutUndo(); }
        }
    }
}
