// Import rules for the generated characters in Assets/Characters (written by art/blender/build_units.py).
// Tuned for low-end Android: compressed meshes and animation, no CPU-readable mesh copies, no extra UVs.
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace ChessWars.EditorTools
{
    public class CharacterImport : AssetPostprocessor
    {
        static bool IsCharacter(string path) => path.Replace('\\', '/').StartsWith("Assets/Characters/");

        // Idle and Walk loop; attacks, shots and deaths play once.
        static bool Loops(string clipName) => clipName.EndsWith("Idle") || clipName.EndsWith("Walk");

        static bool IsProp(string path) => path.Replace('\\', '/').StartsWith("Assets/Resources/Props/");

        void OnPreprocessModel()
        {
            if (IsProp(assetPath))
            {
                // Scenery: static meshes, no skeleton or animation, no CPU copy.
                var prop = (ModelImporter)assetImporter;
                prop.importAnimation = false;
                prop.animationType = ModelImporterAnimationType.None;
                prop.importCameras = false;
                prop.importLights = false;
                prop.isReadable = false;
                prop.meshCompression = ModelImporterMeshCompression.Medium;
                prop.importTangents = ModelImporterTangents.None;
                prop.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
                return;
            }
            if (!IsCharacter(assetPath)) return;
            var importer = (ModelImporter)assetImporter;
            importer.globalScale = 1f;
            importer.useFileScale = true;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importBlendShapes = false;
            importer.importVisibility = false;
            importer.isReadable = false;
            importer.meshCompression = ModelImporterMeshCompression.Medium;
            importer.generateSecondaryUV = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.None;   // no normal maps; the tangents carry outline normals (below)
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = true;
            importer.animationCompression = ModelImporterAnimationCompression.Optimal;
            importer.resampleCurves = true;
        }

        void OnPreprocessAnimation()
        {
            if (!IsCharacter(assetPath)) return;
            var importer = (ModelImporter)assetImporter;
            var clips = importer.defaultClipAnimations;
            foreach (var clip in clips)
            {
                // Blender names takes "<Armature>|<Action>"; keep just the action (Idle, Walk, Attack...).
                clip.name = clip.name.Split('|').Last();
                clip.loopTime = Loops(clip.name);
                clip.loopPose = false;
            }
            importer.clipAnimations = clips;
        }

        // After URP's own material setup (order 2), so ours wins: every character and prop part gets the toon
        // shader with its Blender colour and glow.
        public override int GetPostprocessOrder() => 10;

        void OnPreprocessMaterialDescription(MaterialDescription description, Material material, AnimationClip[] animations)
        {
            if (!IsCharacter(assetPath) && !IsProp(assetPath)) return;
            var toon = Shader.Find("ChessWars/Toon");
            if (toon == null) return;
            var color = Color.white;
            // Blender writes linear values; SetColor expects sRGB, so convert back or every colour comes out too dark.
            if (description.TryGetProperty("DiffuseColor", out Vector4 diffuse)) color = new Color(diffuse.x, diffuse.y, diffuse.z, 1f).gamma;
            var emission = Color.black;
            if (description.TryGetProperty("EmissiveColor", out Vector4 emissive))
            {
                float factor = description.TryGetProperty("EmissiveFactor", out float f) ? f : 1f;
                emission = new Color(emissive.x, emissive.y, emissive.z).gamma * factor;
            }
            material.shader = toon;
            material.SetColor("_BaseColor", color);
            material.SetColor("_EmissionColor", emission);
            // Glowing parts (eyes, runes, fire) skip the shading so they stay bright; props get a slightly thinner ink.
            if (emission.maxColorComponent > 0.05f) material.SetFloat("_RimStrength", 0f);
            if (IsProp(assetPath)) material.SetFloat("_OutlineWidth", 0.02f);
        }

        // Smoothed normals in the tangents for the outline pass: faceted models keep hard-edged lighting, while the
        // ink outline pushes out along averaged normals and doesn't crack at the corners. GPU skinning rotates
        // tangents with the bones, so outlines follow the animation.
        void OnPostprocessModel(GameObject root)
        {
            if (!IsCharacter(assetPath) && !IsProp(assetPath)) return;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true)) BakeOutlineNormals(filter.sharedMesh);
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) BakeOutlineNormals(skin.sharedMesh);
        }

        static void BakeOutlineNormals(Mesh mesh)
        {
            if (mesh == null) return;
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            if (normals.Length != vertices.Length) return;
            var sums = new System.Collections.Generic.Dictionary<Vector3Int, Vector3>();
            Vector3Int Key(Vector3 v) => new Vector3Int(Mathf.RoundToInt(v.x * 1e4f), Mathf.RoundToInt(v.y * 1e4f), Mathf.RoundToInt(v.z * 1e4f));
            for (int i = 0; i < vertices.Length; i++)
            {
                var k = Key(vertices[i]);
                sums[k] = (sums.TryGetValue(k, out var s) ? s : Vector3.zero) + normals[i];
            }
            var tangents = new Vector4[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var smooth = sums[Key(vertices[i])].normalized;
                tangents[i] = new Vector4(smooth.x, smooth.y, smooth.z, 1f);
            }
            mesh.tangents = tangents;
        }
    }
}
