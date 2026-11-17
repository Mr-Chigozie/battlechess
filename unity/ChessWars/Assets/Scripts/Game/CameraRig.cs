// A cinematic orbit around the board, framed for the screen's shape, from the player's side.
// Drag with one finger to circle and tilt, pinch (or the mouse wheel) to zoom, double-tap empty space to reset.
// A press that moves more than a few pixels is a drag, never a tap, so moving the camera never moves a unit.
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChessWars.Game
{
    [RequireComponent(typeof(Camera))]
    public class CameraRig : MonoBehaviour
    {
        public char viewColor = 'w';
        public bool PressWasDrag { get; private set; }

        const float DragThreshold = 14f;            // pixels before a press counts as a drag
        const float MinPitch = 0.42f, MaxPitch = 1.35f;

        Camera cam;
        float yaw, pitch, zoom = 1f;                 // yaw and pitch offsets from the framed view, zoom multiplier
        Vector2 pressStart, last;
        float pinchStart, zoomAtPinch;
        bool pressing, pinching;

        void Awake() => cam = GetComponent<Camera>();

        void LateUpdate()
        {
            Gestures();
            Place();
        }

        public void ResetView()
        {
            yaw = 0f;
            pitch = 0f;
            zoom = 1f;
        }

        // The default angle: lower than a top-down chess view so the units stand tall, steeper on narrow phones
        // so the whole board still fits.
        float BaseElevation(float aspect) => aspect < 0.8f ? 0.98f : aspect < 1.25f ? 0.82f : 0.7f;

        public void Fit() => Place();

        void Place()
        {
            cam = cam != null ? cam : GetComponent<Camera>();
            float aspect = cam.aspect;
            bool portrait = aspect < 0.8f;
            float elevation = Mathf.Clamp(BaseElevation(aspect) + pitch, MinPitch, MaxPitch);
            float halfV = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float halfH = Mathf.Atan(Mathf.Tan(halfV) * aspect);
            float fitWidth = 4.75f / Mathf.Tan(halfH);
            float fitDepth = (4.6f * Mathf.Sin(elevation) + 1.6f) / Mathf.Tan(halfV);
            float distance = Mathf.Max(fitWidth, fitDepth, 9f) * zoom;
            float heading = (viewColor == 'w' ? 0f : 180f) + yaw;
            var flat = Quaternion.Euler(0, heading, 0) * Vector3.forward;   // from the board toward the camera
            var target = flat * (portrait ? 0.6f : 0.3f) + Vector3.up * 0.4f;
            transform.position = target + (flat * Mathf.Cos(elevation) + Vector3.up * Mathf.Sin(elevation)) * distance;
            transform.LookAt(target);
        }

        void Gestures()
        {
            var touch = Touchscreen.current;
            int touching = 0;
            if (touch != null)
                foreach (var t in touch.touches) if (t.press.isPressed) touching++;

            // Two fingers: pinch to zoom.
            if (touching >= 2)
            {
                var a = touch.touches[0].position.ReadValue();
                var b = touch.touches[1].position.ReadValue();
                float span = Vector2.Distance(a, b);
                if (!pinching) { pinching = true; pinchStart = span; zoomAtPinch = zoom; }
                else if (pinchStart > 1f) zoom = Mathf.Clamp(zoomAtPinch * pinchStart / span, 0.55f, 1.35f);
                PressWasDrag = true;
                return;
            }
            pinching = false;

            var mouse = Mouse.current;
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f) zoom = Mathf.Clamp(zoom * (scroll > 0 ? 0.92f : 1.08f), 0.55f, 1.35f);
            }

            // One finger (or the mouse): drag to orbit and tilt.
            var pointer = Pointer.current;
            if (pointer == null) return;
            var pos = pointer.position.ReadValue();
            if (pointer.press.wasPressedThisFrame)
            {
                pressing = true;
                PressWasDrag = false;
                pressStart = last = pos;
            }
            if (pressing && pointer.press.isPressed)
            {
                if (!PressWasDrag && Vector2.Distance(pos, pressStart) > DragThreshold * (Screen.dpi > 0 ? Screen.dpi / 160f : 1f)) PressWasDrag = true;
                if (PressWasDrag)
                {
                    var delta = pos - last;
                    float scale = 180f / Mathf.Max(Screen.width, 1);
                    yaw += delta.x * scale;
                    pitch = Mathf.Clamp(pitch - delta.y * 0.004f, MinPitch - BaseElevation(cam.aspect), MaxPitch - BaseElevation(cam.aspect));
                }
                last = pos;
            }
            if (pointer.press.wasReleasedThisFrame) pressing = false;
        }
    }
}
