// Import rules for the generated characters in Assets/Characters (written by art/blender/build_units.py).
// Tuned for low-end Android: compressed meshes and animation, no CPU-readable mesh copies, no extra UVs.
using System.Linq;
using UnityEditor;
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
            importer.importTangents = ModelImporterTangents.None;   // no normal maps, so no tangents
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
    }
}
