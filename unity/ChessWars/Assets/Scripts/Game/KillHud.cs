// The kill sequence's screen layer, as the web game's (battle3d.html): letterbox bars, the caption ("who vs who",
// then the kill line), the combo banner (First Blood, Double Kill...) and the white flash. Built in code.
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace ChessWars.Game
{
    public class KillHud : MonoBehaviour
    {
        static readonly Color Gold = Armies.Hex(0xd6ad66), Parchment = Armies.Hex(0xf3e4bf);
        static readonly Color ComboWarm = Armies.Hex(0xffe2a0), ComboHot = Armies.Hex(0xffb0a0);

        RectTransform top, bottom;
        CanvasGroup caption, combo;
        Text who, line, comboTitle, comboTags;
        Image flash;
        Outline comboOutline;
        float bars, barsTarget;
        float captionAlpha, captionTarget;
        float comboShownAt = -10f, flashAlpha;

        public bool Cinematic { get; private set; }

        public static KillHud Create()
        {
            var go = new GameObject("Kill HUD");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            var hud = go.AddComponent<KillHud>();
            hud.Build(go.transform);
            return hud;
        }

        static RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform;
            r.anchorMin = min;
            r.anchorMax = max;
            r.offsetMin = r.offsetMax = Vector2.zero;
            return r;
        }

        static Text Label(string name, Transform parent, Vector2 min, Vector2 max, int size, FontStyle style, Color color)
        {
            var text = Rect(name, parent, min, max).gameObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            var shadow = text.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.85f);
            shadow.effectDistance = new Vector2(0, -3);
            return text;
        }

        void Build(Transform root)
        {
            flash = Rect("Flash", root, Vector2.zero, Vector2.one).gameObject.AddComponent<Image>();
            flash.color = new Color(1, 1, 1, 0);
            flash.raycastTarget = false;

            top = Rect("Letterbox top", root, new Vector2(0, 1), Vector2.one);
            bottom = Rect("Letterbox bottom", root, Vector2.zero, new Vector2(1, 0));
            foreach (var bar in new[] { top, bottom })
            {
                var img = bar.gameObject.AddComponent<Image>();
                img.color = Color.black;
                img.raycastTarget = false;
            }

            var cap = Rect("Caption", root, new Vector2(0.04f, 0.02f), new Vector2(0.96f, 0.1f));
            caption = cap.gameObject.AddComponent<CanvasGroup>();
            caption.alpha = 0;
            caption.blocksRaycasts = false;
            who = Label("Who", cap, new Vector2(0, 0.62f), Vector2.one, 26, FontStyle.Bold, Gold);
            line = Label("Line", cap, Vector2.zero, new Vector2(1, 0.62f), 42, FontStyle.Italic, Parchment);

            var banner = Rect("Combo", root, new Vector2(0.02f, 0.66f), new Vector2(0.98f, 0.8f));
            combo = banner.gameObject.AddComponent<CanvasGroup>();
            combo.alpha = 0;
            combo.blocksRaycasts = false;
            comboTitle = Label("Title", banner, new Vector2(0, 0.3f), Vector2.one, 104, FontStyle.Bold, ComboWarm);
            comboTitle.resizeTextForBestFit = true;
            comboTitle.resizeTextMinSize = 40;
            comboTitle.resizeTextMaxSize = 104;
            comboTitle.horizontalOverflow = HorizontalWrapMode.Overflow;
            comboOutline = comboTitle.gameObject.AddComponent<Outline>();
            comboOutline.effectColor = Armies.Hex(0x6b1a08);
            comboOutline.effectDistance = new Vector2(3, -3);
            comboTags = Label("Tags", banner, Vector2.zero, new Vector2(1, 0.3f), 26, FontStyle.Bold, Color.white);
        }

        public void SetCinematic(bool on)
        {
            Cinematic = on;
            barsTarget = on ? (Screen.width < Screen.height * 0.9f ? 0.08f : 0.11f) : 0f;
            if (!on) HideCaption();
        }

        public void ShowCaption(string whoText, string lineText)
        {
            who.text = whoText.ToUpperInvariant();
            line.text = lineText;
            captionTarget = 1;
        }

        public void HideCaption() => captionTarget = 0;

        public void ShowCombo(string title, string tags, int level)
        {
            comboTitle.text = title.ToUpperInvariant();
            comboTags.text = tags.ToUpperInvariant();
            comboTitle.color = level >= 3 ? ComboHot : ComboWarm;
            comboShownAt = Time.unscaledTime;
        }

        public void Flash(float strength) => flashAlpha = Mathf.Max(flashAlpha, strength);

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            bars = Mathf.MoveTowards(bars, barsTarget, dt / 0.38f * 0.11f);
            top.anchorMin = new Vector2(0, 1 - bars);
            bottom.anchorMax = new Vector2(1, bars);

            captionAlpha = Mathf.MoveTowards(captionAlpha, captionTarget, dt / 0.3f);
            caption.alpha = captionAlpha;
            ((RectTransform)caption.transform).anchoredPosition = new Vector2(0, (captionAlpha - 1) * 12f);

            // The combo pops in (overshooting scale), holds, then fades: 2.3 s in all.
            float age = Time.unscaledTime - comboShownAt;
            if (age < 2.5f)
            {
                float pop = Mathf.Clamp01(age / 0.26f);
                float overshoot = 1 + Mathf.Sin(pop * Mathf.PI) * 0.18f;
                combo.transform.localScale = Vector3.one * Mathf.Lerp(0.6f, 1f, pop) * overshoot;
                combo.alpha = age < 0.2f ? age / 0.2f : age > 2.3f ? 1 - (age - 2.3f) / 0.2f : 1;
            }
            else combo.alpha = 0;

            flashAlpha = Mathf.MoveTowards(flashAlpha, 0, dt / 0.45f * 0.5f);
            flash.color = new Color(1, 1, 1, flashAlpha);
        }
    }
}
