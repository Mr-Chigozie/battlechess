// One unit on the board: a character prefab (Assets/Resources/Characters) driven through its Animator.
// The Animator's states are Idle, Walk, Attack, Shoot, Death and Behead (see Assets/Editor/CharacterPrefabs.cs).
using System.Collections;
using UnityEngine;

namespace ChessWars.Game
{
    public class UnitView : MonoBehaviour
    {
        public const float AttackHit = 14f / 30f;   // seconds into the Attack clip when the blow lands
        static readonly Color Ivory = Armies.Hex(0xefe6d2), Onyx = Armies.Hex(0x2a2522);
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        public string Army { get; private set; }
        public char Type { get; private set; }
        public char Color { get; private set; }
        public int Row, Col;
        public bool HasBehead { get; private set; }

        Animator animator;

        public static UnitView Spawn(string army, char type, char color, int row, int col, Transform parent)
        {
            var prefab = Resources.Load<GameObject>("Characters/" + Armies.PrefabName(army, type));
            var go = prefab != null ? Instantiate(prefab, parent) : GameObject.CreatePrimitive(PrimitiveType.Capsule);
            if (prefab == null) go.transform.SetParent(parent, false);  // a stand-in if a model is missing
            go.name = $"{Armies.PrefabName(army, type)} {Rules.Rules.Algebraic(row, col)}";
            var unit = go.AddComponent<UnitView>();
            unit.Init(army, type, color, row, col);
            return unit;
        }

        void Init(string army, char type, char color, int row, int col)
        {
            Army = army; Type = type; Color = color; Row = row; Col = col;
            animator = GetComponentInChildren<Animator>();
            if (animator != null)
            {
                HasBehead = HasState("Behead");
                // Start each unit's idle at a different moment so the ranks don't breathe in unison.
                animator.Play("Idle", 0, Random.value);
            }
            transform.position = Armies.SquareToWorld(row, col);
            FaceHome();
            TintBase(color == 'w' ? Ivory : Onyx);
        }

        bool HasState(string state)
        {
            int hash = Animator.StringToHash(state);
            return animator.HasState(0, hash);
        }

        // Both sides share one model; the base ("Team_*" material) takes the side's colour.
        void TintBase(UnityEngine.Color tint)
        {
            var block = new MaterialPropertyBlock();
            foreach (var renderer in GetComponentsInChildren<Renderer>())
            {
                var mats = renderer.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || !mats[i].name.StartsWith("Team_")) continue;
                    renderer.GetPropertyBlock(block, i);
                    block.SetColor(BaseColor, tint);
                    renderer.SetPropertyBlock(block, i);
                }
            }
        }

        // The models face +Z; Ivory sits at +z and faces the enemy at -z.
        public void FaceHome() => transform.rotation = Quaternion.Euler(0, Color == 'w' ? 180 : 0, 0);

        public void Play(string state, float fade = 0.15f)
        {
            if (animator == null || !HasState(state)) return;
            animator.CrossFadeInFixedTime(state, fade);
        }

        public IEnumerator TurnTowards(Vector3 target, float seconds = 0.2f)
        {
            var dir = target - transform.position;
            dir.y = 0;
            if (dir.sqrMagnitude < 1e-4f) yield break;
            var from = transform.rotation;
            var to = Quaternion.LookRotation(dir);
            for (float t = 0; t < 1; t += Time.deltaTime / seconds)
            {
                transform.rotation = Quaternion.Slerp(from, to, t);
                yield return null;
            }
            transform.rotation = to;
        }

        // Walk to a point; knights leap in an arc.
        public IEnumerator Travel(Vector3 to, float arc = 0f)
        {
            yield return TurnTowards(to);
            var from = transform.position;
            float seconds = arc > 0 ? 0.55f + Vector3.Distance(from, to) * 0.08f : 0.25f + Vector3.Distance(from, to) * 0.38f;
            Play("Walk");
            for (float t = 0; t < 1; t += Time.deltaTime / seconds)
            {
                transform.position = Vector3.Lerp(from, to, t) + Vector3.up * Mathf.Sin(t * Mathf.PI) * arc;
                yield return null;
            }
            transform.position = to;
            Play("Idle");
        }

        // Swing so the blow lands `seconds` from now.
        public IEnumerator StrikeIn(float seconds)
        {
            if (seconds > AttackHit) yield return new WaitForSeconds(seconds - AttackHit);
            Play("Attack", 0.08f);
        }

        // Fall (or lose the head), lie a moment, then sink into the board and disappear.
        public IEnumerator Die(bool behead)
        {
            Play(behead && HasBehead ? "Behead" : "Death", 0.08f);
            yield return new WaitForSeconds(1.15f);
            var from = transform.position;
            for (float t = 0; t < 1; t += Time.deltaTime / 0.6f)
            {
                transform.position = from + Vector3.down * 0.9f * t * t;
                yield return null;
            }
            Destroy(gameObject);
        }
    }
}
