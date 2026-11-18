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
        static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        public string Army { get; private set; }
        public char Type { get; private set; }
        public char Color { get; private set; }
        public int Row, Col;
        public bool HasBehead { get; private set; }
        public bool Dying { get; private set; }             // falling: no badge, no longer a target
        public float Height { get; private set; } = 1.2f;   // top of the model above the board
        public string Style => Armies.Style(Army, Type);

        /// <summary>Middle of the body, where blows land and sparks fly.</summary>
        public Vector3 Center => transform.position + Vector3.up * Height * 0.5f;
        /// <summary>Roughly where a gun barrel or staff points from.</summary>
        public Vector3 Tip => transform.position + Vector3.up * Height * 0.62f + transform.forward * 0.3f;

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
            float top = 0f;
            foreach (var r in GetComponentsInChildren<Renderer>()) top = Mathf.Max(top, r.bounds.max.y);
            if (top > 0.3f) Height = top - transform.position.y;
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

        public void Play(string state, float fade = 0.15f, float from = 0f)
        {
            if (animator == null || !HasState(state)) return;
            animator.CrossFadeInFixedTime(state, fade, 0, from);
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

        // Walk to a point; knights leap in an arc. `seconds` overrides the pace; `accelerate` eases in (a charge).
        public IEnumerator Travel(Vector3 to, float arc = 0f, float seconds = 0f, bool accelerate = false, bool walk = true, bool turn = true)
        {
            if (turn) yield return TurnTowards(to);
            var from = transform.position;
            if (seconds <= 0) seconds = arc > 0 ? 0.55f + Vector3.Distance(from, to) * 0.08f : 0.25f + Vector3.Distance(from, to) * 0.38f;
            if (walk) Play("Walk");
            for (float t = 0; t < 1; t += Time.deltaTime / seconds)
            {
                float k = accelerate ? t * t : t;
                transform.position = Vector3.Lerp(from, to, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * arc;
                yield return null;
            }
            transform.position = to;
            if (walk) Play("Idle");
        }

        // Swing so the blow lands `seconds` from now (starting the clip part-way through if that's sooner than its wind-up).
        public IEnumerator StrikeIn(float seconds)
        {
            if (seconds >= AttackHit)
            {
                yield return new WaitForSeconds(seconds - AttackHit);
                Play("Attack", 0.08f);
            }
            else Play("Attack", 0.05f, AttackHit - seconds);
        }

        /// <summary>Shift along the ground (a lunge, a recoil, a stagger) over `seconds`.</summary>
        public IEnumerator Nudge(Vector3 dir, float distance, float seconds, bool easeIn = false)
        {
            var from = transform.position;
            for (float t = 0; t < 1; t += Time.deltaTime / seconds)
            {
                float k = easeIn ? t * t : 1 - (1 - t) * (1 - t);
                transform.position = from + dir * distance * k;
                yield return null;
            }
            transform.position = from + dir * distance;
        }

        /// <summary>Lights up white for a moment on the hit (its own renderers only, so its comrades don't flash).</summary>
        public IEnumerator FlashWhite()
        {
            var renderers = GetComponentsInChildren<Renderer>();
            var block = new MaterialPropertyBlock();
            var white = UnityEngine.Color.white * 1.4f;
            for (float t = 0; ; t += Time.deltaTime / 0.3f)
            {
                float k = Mathf.Clamp01(t);
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] == null || !mats[i].HasProperty(EmissionColor)) continue;
                        r.GetPropertyBlock(block, i);
                        block.SetColor(EmissionColor, UnityEngine.Color.Lerp(white, mats[i].GetColor(EmissionColor), k));
                        r.SetPropertyBlock(block, i);
                    }
                }
                if (k >= 1) yield break;
                yield return null;
            }
        }

        // Plays the Death clip (or Behead), with the army's flourish on top, then sinks away; as the web game's
        // modelDeath (js/stage3d.js). Asgardians rise into light instead of sinking.
        public IEnumerator Die(bool behead, int combo)
        {
            var fx = Fx.I;
            var army = Armies.All[Army];
            Dying = true;
            behead &= HasBehead;
            Play(behead ? "Behead" : "Death", 0.08f);
            var spot = Center;
            if (behead) fx.Burst(transform.position + Vector3.up * Height * 0.78f, army.Blood, 24, 1.9f, 0.9f, 0.8f);
            switch (Army)
            {
                case "undead":
                    // A green soul slips away.
                    fx.Glow(spot, army.Magic, 0.35f, 1.6f, new Vector3(0, 0.9f, 0));
                    for (int i = 0; i < 3; i++) fx.Ember(spot + Vector3.up * i * 0.15f, army.Magic, 1.2f);
                    break;
                case "machines":
                    // Sparks, sparks, then the whole thing goes up.
                    for (int i = 0; i < 3; i++)
                    {
                        fx.Burst(Center + new Vector3((Random.value - 0.5f) * 0.3f, (Random.value - 0.3f) * 0.3f, 0), Armies.Hex(0xffd27a), 10, 1.6f, 0.7f, 0.3f);
                        yield return new WaitForSeconds(0.11f);
                    }
                    fx.Shockwave(transform.position, Armies.Hex(0xffa23a));
                    fx.Burst(Center, Armies.Hex(0xff7a2a), 30, 3f, 1.2f, 0.7f);
                    Fx.Shake(0.25f + combo * 0.05f);
                    break;
                case "samurai":
                    // The cut hangs in the air; petals scatter.
                    fx.Cut(spot + Vector3.up * 0.05f, transform.eulerAngles.y);
                    fx.Burst(spot, Armies.Hex(0xffb7c5), 26, 1.8f, 1f, 1.1f);
                    break;
            }
            yield return new WaitForSeconds(0.9f);
            fx.Dust(transform.position + Vector3.up * 0.1f, 12);
            Fx.Shake(0.08f);
            if (Army == "asgardians")
            {
                var from = transform.position;
                // Run on Fx: this unit is gone before the beam fades.
                fx.StartCoroutine(fx.Beam(from + Vector3.up * 8f, from + Vector3.up * 0.05f, army.Magic, 1f));
                var scale = transform.localScale;
                for (float t = 0; t < 1; t += Time.deltaTime / 0.7f)
                {
                    float k = t * t;
                    transform.position = from + Vector3.up * 0.6f * k;
                    transform.localScale = scale * Mathf.Max(0.05f, 1 - 0.95f * k);
                    yield return null;
                }
                fx.Burst(Center, UnityEngine.Color.white, 24, 2.4f, 1f, 0.6f);
                foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = false;
                yield return new WaitForSeconds(0.35f);
                Destroy(gameObject);
                yield break;
            }
            yield return new WaitForSeconds(0.25f);
            fx.Dust(transform.position + Vector3.up * 0.1f, 8);
            var start = transform.position;
            for (float t = 0; t < 1; t += Time.deltaTime / 0.6f)
            {
                transform.position = start + Vector3.down * 0.9f * t * t;
                yield return null;
            }
            Destroy(gameObject);
        }
    }
}
