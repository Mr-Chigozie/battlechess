// The close-up for a kill, as the web game's enterCinematic/chooseShot (js/stage3d.js): a second Cinemachine
// camera that the brain blends to from the board view and back. The shot is the spot around the duel where the
// fewest bystanders block the view, off the attacker's shoulder on phones and side-on on wide screens.
using System.Collections;
using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;

namespace ChessWars.Game
{
    public class KillCam : MonoBehaviour
    {
        const float BaseFov = 42f;

        CinemachineCamera vcam;
        CinemachineBrain brain;
        CameraRig rig;
        Vector3 look;
        float punch;          // 0..1 punch-in amount, eased back out
        float punchStart = -10f, punchSize;

        public bool Active { get; private set; }

        public static KillCam Create(CameraRig boardRig)
        {
            var go = new GameObject("Kill Camera");
            var cam = go.AddComponent<KillCam>();
            cam.rig = boardRig;
            cam.vcam = go.AddComponent<CinemachineCamera>();
            var lens = LensSettings.Default;
            lens.FieldOfView = BaseFov;
            lens.NearClipPlane = 0.1f;
            lens.FarClipPlane = 120f;
            cam.vcam.Lens = lens;
            go.AddComponent<CameraShake>();
            if (boardRig.GetComponent<CameraShake>() == null) boardRig.gameObject.AddComponent<CameraShake>();
            cam.brain = Camera.main.GetComponent<CinemachineBrain>();
            // The swoop keeps its pace through hit-stop and slow motion.
            if (cam.brain != null) cam.brain.IgnoreTimeScale = true;
            go.SetActive(false);
            return cam;
        }

        void SetBlend(float seconds)
        {
            if (brain != null) brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.EaseInOut, seconds);
        }

        /// <summary>Swoop in on attacker A and victim V. `tall` is the taller fighter's height.</summary>
        public IEnumerator Enter(Vector3 A, Vector3 V, float tall, bool behead, IEnumerable<UnitView> bystanders)
        {
            var mid = (A + V) * 0.5f;
            tall = Mathf.Max(1.2f, tall);
            look = new Vector3(mid.x, Mathf.Max(0.55f, tall * 0.46f) + (behead ? 0.2f : 0f), mid.z);
            var shot = ChooseShot(A, V, mid, tall, behead, bystanders);
            transform.SetPositionAndRotation(shot, Quaternion.LookRotation(look - shot));
            GetComponent<CameraShake>().Focus = look;
            rig.enabled = false;
            SetBlend(0.85f);
            Active = true;
            gameObject.SetActive(true);
            yield return new WaitForSecondsRealtime(0.85f);
        }

        public IEnumerator Exit()
        {
            SetBlend(0.8f);
            gameObject.SetActive(false);
            yield return new WaitForSecondsRealtime(0.8f);
            Active = false;
            rig.enabled = true;
        }

        /// <summary>A quick zoom in on the hit and back out.</summary>
        public void PunchIn(float amount)
        {
            punchSize = amount;
            punchStart = Time.unscaledTime;
        }

        void LateUpdate()
        {
            float t = Mathf.Clamp01((Time.unscaledTime - punchStart) / 0.45f);
            punch = punchSize * (1 - (1 - (1 - t) * (1 - t)));
            var lens = vcam.Lens;
            lens.FieldOfView = BaseFov * (1 - punch);
            vcam.Lens = lens;
        }

        Vector3 ChooseShot(Vector3 A, Vector3 V, Vector3 mid, float tall, bool behead, IEnumerable<UnitView> units)
        {
            var dir = V - A;
            dir.y = 0;
            dir.Normalize();
            var bystanders = new List<Vector3>();
            foreach (var u in units)
                if (u != null) bystanders.Add(new Vector3(u.transform.position.x, u.Height * 0.55f, u.transform.position.z));
            var subjects = new[] { new Vector3(A.x, tall * 0.42f, A.z), new Vector3(V.x, tall * 0.42f, V.z) };
            // Portrait screens are narrow: film from behind the attacker at an angle (the duel runs up the screen).
            var cam = Camera.main;
            bool portrait = cam.aspect < 0.9f;
            float reach = (portrait ? 0.9f : 1f) * (tall / 1.2f) * (behead ? 1.25f : 1f);
            float lift = (tall - 1.2f) * 0.7f + (behead ? 0.35f : 0f);
            float span = Vector3.Distance(A, V);
            float distance = (portrait ? Mathf.Max(3.2f, span * 0.5f + 3f) : Mathf.Max(2.8f, span * 0.75f + 2f)) * reach;
            var heights = portrait ? new[] { 3.2f + lift, 4.0f + lift } : new[] { 1.3f + lift, 2.1f + lift };
            float girth = 0.42f * Mathf.Max(1f, tall / 1.2f);
            var current = cam.transform.position - mid;
            current.y = 0;
            current.Normalize();

            int Blocked(Vector3 from, Vector3 to)
            {
                var seg = to - from;
                float len = seg.magnitude;
                seg /= len;
                int n = 0;
                foreach (var p in bystanders)
                {
                    float along = Vector3.Dot(p - from, seg);
                    if (along < 0 || along > len - 0.35f) continue;
                    if (Vector3.Distance(from + seg * along, p) < girth) n++;
                }
                return n;
            }

            var best = mid + Vector3.up * heights[0];
            float bestScore = float.MaxValue;
            for (int i = 0; i < 16; i++)
            {
                float angle = i / 16f * Mathf.PI * 2;
                var flat = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                foreach (var height in heights)
                {
                    var pos = mid + flat * distance;
                    pos.y = height;
                    int occluders = 0;
                    foreach (var s in subjects) occluders += Blocked(pos, s);
                    // Portrait: about 45 degrees off the attacker's shoulder, so the attacker doesn't hide the victim.
                    float framing = portrait ? Mathf.Abs(Vector3.Dot(flat, dir) + 0.7f) * 3 : Mathf.Abs(Vector3.Dot(flat, dir)) * 3;
                    int crowded = 0;
                    foreach (var b in bystanders)
                        if (new Vector2(b.x - pos.x, b.z - pos.z).magnitude < 0.75f) crowded++;
                    float score = (occluders + crowded) * 10 + framing + (1 - Vector3.Dot(flat, current)) * 0.8f + (height == heights[1] ? 1.5f : 0);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = pos;
                    }
                }
            }
            return best;
        }
    }
}
