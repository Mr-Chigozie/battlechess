// Keeps a screen's content above the ad banner: raises the bottom of its safe-area rect by the part of the
// banner that reaches into it (Ads.BannerPixels), and lets it back down when the banner hides.
using UnityEngine;

namespace ChessWars.Game
{
    public class BannerInset : MonoBehaviour
    {
        RectTransform rect;
        Canvas canvas;
        float applied = -1f;

        void Awake()
        {
            rect = (RectTransform)transform;
            canvas = GetComponentInParent<Canvas>().rootCanvas;
        }

        void LateUpdate()
        {
            float px = Mathf.Max(0f, Ads.BannerPixels - Screen.safeArea.yMin);
            float inset = px / Mathf.Max(0.01f, canvas.scaleFactor);
            if (Mathf.Abs(inset - applied) < 0.5f) return;
            applied = inset;
            rect.offsetMin = new Vector2(rect.offsetMin.x, inset);
        }
    }
}
