// Turns every character FBX in Assets/Characters into a ready-to-spawn prefab in Assets/Resources/Characters:
// the model at board scale, an Animator using a shared controller (with this model's clips swapped in) and a
// collider for taps. Run after the characters change:
//   Unity.exe -batchmode -quit -projectPath unity/ChessWars -executeMethod ChessWars.EditorTools.CharacterPrefabs.Build
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ChessWars.EditorTools
{
    public static class CharacterPrefabs
    {
        const string Source = "Assets/Characters";
        const string Output = "Assets/Resources/Characters";
        const string AnimFolder = "Assets/Animation";
        const float BoardScale = 0.8f;   // the models are built for 1.2-unit squares; the board's are 1.0

        static readonly string[] Fighter = { "Idle", "Walk", "Attack", "Shoot", "Death", "Behead" };
        static readonly string[] Plain = { "Idle", "Walk", "Attack", "Shoot", "Death" };

        [MenuItem("Chess Wars/Build character prefabs")]
        public static void Build()
        {
            Directory.CreateDirectory(Output);
            Directory.CreateDirectory(AnimFolder);
            var fbxPaths = Directory.GetFiles(Source, "*.fbx").Select(p => p.Replace('\\', '/')).OrderBy(p => p).ToArray();
            // The shared controllers take their placeholder clips from a model that has them all.
            var fighterBase = BaseController($"{AnimFolder}/Fighter.controller", Fighter, ClipsOf($"{Source}/Knights_Pawn.fbx"));
            var plainBase = BaseController($"{AnimFolder}/Plain.controller", Plain, ClipsOf($"{Source}/Knights_Rook.fbx"));
            int built = 0;
            foreach (var path in fbxPaths)
            {
                var clips = ClipsOf(path);
                bool fighter = clips.Any(c => c.name == "Behead");
                var controller = Override(Path.GetFileNameWithoutExtension(path), fighter ? fighterBase : plainBase, clips);
                MakePrefab(path, controller);
                built++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[CharacterPrefabs] built {built} prefabs in {Output}");
        }

        static AnimationClip[] ClipsOf(string fbx) =>
            AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).ToArray();

        static AnimatorController BaseController(string path, string[] states, AnimationClip[] clips)
        {
            AssetDatabase.DeleteAsset(path);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var machine = controller.layers[0].stateMachine;
            AnimatorState idle = null;
            foreach (var name in states)
            {
                var state = machine.AddState(name);
                state.motion = clips.First(c => c.name == name);
                if (name == "Idle") idle = state;
            }
            machine.defaultState = idle;
            // Attacks and shots settle back into idle on their own; deaths hold their last frame.
            foreach (var child in machine.states.Where(s => s.state.name == "Attack" || s.state.name == "Shoot"))
            {
                var back = child.state.AddTransition(idle);
                back.hasExitTime = true;
                back.exitTime = 1f;
                back.duration = 0.15f;
                back.hasFixedDuration = true;
            }
            return controller;
        }

        static AnimatorOverrideController Override(string name, AnimatorController baseController, AnimationClip[] clips)
        {
            string path = $"{AnimFolder}/{name}.overrideController";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(path);
            if (controller == null)
            {
                controller = new AnimatorOverrideController(baseController);
                AssetDatabase.CreateAsset(controller, path);
            }
            controller.runtimeAnimatorController = baseController;
            var overrides = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<AnimationClip, AnimationClip>>();
            controller.GetOverrides(overrides);
            for (int i = 0; i < overrides.Count; i++)
            {
                var mine = clips.FirstOrDefault(c => c.name == overrides[i].Key.name);
                overrides[i] = new System.Collections.Generic.KeyValuePair<AnimationClip, AnimationClip>(overrides[i].Key, mine);
            }
            controller.ApplyOverrides(overrides);
            EditorUtility.SetDirty(controller);
            return controller;
        }

        static void MakePrefab(string fbxPath, AnimatorOverrideController controller)
        {
            string name = Path.GetFileNameWithoutExtension(fbxPath);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var root = new GameObject(name);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            instance.transform.localScale = Vector3.one * BoardScale;
            var animator = instance.GetComponent<Animator>() ?? instance.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;   // off-screen units stop animating
            var bounds = new Bounds(Vector3.up, Vector3.one);
            foreach (var skin in instance.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                skin.quality = SkinQuality.Bone1;          // every vertex follows exactly one bone
                skin.updateWhenOffscreen = false;
                skin.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                skin.receiveShadows = true;
                skin.skinnedMotionVectors = false;
                // The bind-pose bounds don't cover falls and leaps; grow them so units aren't culled mid-animation.
                var local = skin.localBounds;
                local.Expand(new Vector3(1.5f, 1f, 1.5f));
                skin.localBounds = local;
                bounds.Encapsulate(skin.bounds);
            }
            // A capsule for taps, sized to the model (wider for mounts and towers).
            var capsule = root.AddComponent<CapsuleCollider>();
            float height = Mathf.Clamp(bounds.max.y, 1f, 2.2f);
            capsule.height = height;
            capsule.radius = 0.36f;
            capsule.center = new Vector3(0, height / 2f, 0);
            PrefabUtility.SaveAsPrefabAsset(root, $"{Output}/{name}.prefab");
            Object.DestroyImmediate(root);
        }
    }
}
