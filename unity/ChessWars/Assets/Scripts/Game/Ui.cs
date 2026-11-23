// Shared pieces for the code-built screens (BattleHud, MainMenu): the web game's palette and fonts, a canvas
// set up for phones, and small builders for rectangles, panels, labels and web-style buttons.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace ChessWars.Game
{
    public static class Ui
    {
        // The web palette (battle3d.html :root).
        public static readonly Color Ink = Armies.Hex(0xe8e4d6), Muted = Armies.Hex(0x9a9c90), Gold = Armies.Hex(0xd6ad66),
            GoldLight = Armies.Hex(0xf0d39a), Danger = Armies.Hex(0xd4564a), Panel = new Color(0.08f, 0.09f, 0.075f, 0.9f),
            ButtonFill = new Color(0.137f, 0.149f, 0.118f, 0.85f), ButtonLine = Armies.Hex(0x4a4b3f),
            PrimaryLine = Armies.Hex(0xa07f45), PrimaryFill = Armies.Hex(0x7f6436);

        static Font serif, sans;

        /// <summary>A serif face for titles and narration (the web uses Georgia), from the phone's own fonts.</summary>
        public static Font Serif
        {
            get
            {
                if (serif != null) return serif;
                var installed = new HashSet<string>(Font.GetOSInstalledFontNames());
                foreach (var name in new[] { "Noto Serif", "NotoSerif", "Georgia", "Droid Serif", "Times New Roman" })
                    if (installed.Contains(name)) return serif = Font.CreateDynamicFontFromOSFont(name, 32);
                return serif = Sans;
            }
        }

        public static Font Sans => sans != null ? sans : sans = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        /// <summary>A screen-space canvas scaled for a 1080 x 1920 phone layout, with an event system for its buttons.</summary>
        public static GameObject Canvas(string name, int order)
        {
            var go = new GameObject(name);
            var canvas = go.AddComponent<UnityEngine.Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            if (EventSystem.current == null && UnityEngine.Object.FindFirstObjectByType<EventSystem>() == null)
            {
                var events = new GameObject("Event System");
                events.AddComponent<EventSystem>();
                events.AddComponent<InputSystemUIInputModule>();
            }
            return go;
        }

        public static RectTransform Node(string name, Transform parent, Vector2 min, Vector2 max, Vector2 offsetMin = default, Vector2 offsetMax = default)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform;
            r.anchorMin = min;
            r.anchorMax = max;
            r.offsetMin = offsetMin;
            r.offsetMax = offsetMax;
            return r;
        }

        public static Image Fill(RectTransform r, Color color, bool raycast = false)
        {
            var img = r.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        public static Text Label(RectTransform r, Font font, int size, Color color, TextAnchor align, FontStyle style = FontStyle.Normal)
        {
            var t = r.gameObject.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.fontStyle = style;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }

        /// <summary>A web-style button: dark fill, thin olive border, light text (gold-brown when primary).</summary>
        public static RectTransform Button(string name, Transform parent, Vector2 min, Vector2 max, string caption, Action onClick, bool primary = false)
        {
            var r = Node(name, parent, min, max);
            Fill(r, primary ? PrimaryLine : ButtonLine, true);
            Fill(Node("Fill", r, Vector2.zero, Vector2.one, new Vector2(2, 2), new Vector2(-2, -2)), primary ? PrimaryFill : ButtonFill);
            var text = Label(Node("Text", r, Vector2.zero, Vector2.one), Sans, 30, primary ? Armies.Hex(0xfff4d6) : Armies.Hex(0xe0dccd), TextAnchor.MiddleCenter);
            text.text = caption;
            var button = r.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.onClick.AddListener(() => { Sfx.Play("click"); onClick(); });
            return r;
        }

        /// <summary>Recolours a button made by Button(): selected ones get the gold border, disabled ones dim.</summary>
        public static void Style(RectTransform button, bool selected, bool enabled = true)
        {
            button.GetComponent<Image>().color = selected ? Gold : ButtonLine;
            button.Find("Fill").GetComponent<Image>().color = selected ? new Color(0.25f, 0.21f, 0.13f, 0.95f) : ButtonFill;
            var text = button.Find("Text").GetComponent<Text>();
            var c = selected ? GoldLight : Armies.Hex(0xe0dccd);
            c.a = enabled ? 1f : 0.4f;
            text.color = c;
            button.GetComponent<UnityEngine.UI.Button>().interactable = enabled;
        }

        public static Texture2D Gradient(int w, int h, Func<float, float, Color> at)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    tex.SetPixel(x, y, at(w > 1 ? x / (w - 1f) : 0, h > 1 ? y / (h - 1f) : 0));
            tex.Apply();
            return tex;
        }

        /// <summary>Fits a rect to the screen's safe area (clear of notches and system bars).</summary>
        public static RectTransform SafeArea(Transform root)
        {
            var area = Screen.safeArea;
            return Node("Safe area", root,
                new Vector2(area.xMin / Screen.width, area.yMin / Screen.height),
                new Vector2(area.xMax / Screen.width, area.yMax / Screen.height));
        }

        public static RectTransform SetOffsets(this RectTransform r, Vector2 min, Vector2 max)
        {
            r.offsetMin = min;
            r.offsetMax = max;
            return r;
        }
    }
}
