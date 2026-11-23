// The in-battle screen, styled after the web game's HUD (battle3d.html): title and matchup at the top left,
// View and Menu buttons at the top right, the turn banner with the battle-fury meter under them, the narrator's
// box at the bottom, and a chess badge floating over every unit. Built in code (UGUI), inside the safe area.
// It fades out while a kill plays, as the web HUD does.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ChessWars.Game
{
    using static Ui;

    public class BattleHud : MonoBehaviour
    {
        public Func<IEnumerable<UnitView>> Units;   // every unit standing on the board
        public bool ShowBadges = true;
        public event Action ViewPressed, NewGamePressed, MainMenuPressed;
        public bool Hidden;          // the title screen is up: no HUD at all
        public bool MenuOpen => menuOpen;
        public event Action<string, bool> SettingChanged;

        RectTransform safe, badgeLayer, menu;
        CanvasGroup group, narration;
        Text title, sub, banner, furyLabel, narrLabel, narrText;
        Image bannerFrame;
        readonly List<RectTransform> blockers = new List<RectTransform>();
        readonly List<Image> badges = new List<Image>();
        readonly Dictionary<string, Text> toggleMarks = new Dictionary<string, Text>();
        bool thinking, menuOpen;
        float narratedAt = -100f, fury, furyShown;
        KillHud kill;

        public static BattleHud Create(KillHud kill)
        {
            var go = Ui.Canvas("Battle HUD", 5);
            var hud = go.AddComponent<BattleHud>();
            hud.kill = kill;
            hud.Build(go.transform);
            return hud;
        }

        // Every HUD button also blocks taps from reaching the board underneath.
        RectTransform Button(string name, Transform parent, Vector2 min, Vector2 max, string caption, Action onClick, bool primary = false)
        {
            var r = Ui.Button(name, parent, min, max, caption, onClick, primary);
            blockers.Add(r);
            return r;
        }

        // ---------------------------------------------------------------- layout

        void Build(Transform root)
        {
            group = gameObject.AddComponent<CanvasGroup>();
            badgeLayer = Node("Badges", root, Vector2.zero, Vector2.one);
            safe = SafeArea(root);

            // Top shade, so text reads over any sky.
            var shade = Node("Top shade", safe, new Vector2(0, 0.82f), new Vector2(1, 1), Vector2.zero, new Vector2(0, 200));
            var shadeImg = shade.gameObject.AddComponent<RawImage>();
            shadeImg.texture = Gradient(1, 32, (x, y) => new Color(0, 0, 0, 0.93f * Mathf.Sqrt(y)));
            shadeImg.raycastTarget = false;

            // Title and matchup, top left.
            title = Label(Node("Title", safe, new Vector2(0, 1), new Vector2(0.62f, 1), new Vector2(28, -86), new Vector2(0, -22)), Serif, 40, Ink, TextAnchor.MiddleLeft);
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            sub = Label(Node("Sub", safe, new Vector2(0, 1), new Vector2(1, 1), new Vector2(28, -128), new Vector2(-300, -86)), Sans, 22, Armies.Hex(0xc9c5b6), TextAnchor.MiddleLeft);
            sub.resizeTextForBestFit = true;
            sub.resizeTextMinSize = 14;
            sub.resizeTextMaxSize = 22;

            // View and Menu, top right.
            Button("Menu", safe, new Vector2(1, 1), new Vector2(1, 1), "Menu", ToggleMenu).SetOffsets(new Vector2(-150, -116), new Vector2(-24, -24));
            Button("View", safe, new Vector2(1, 1), new Vector2(1, 1), "View", () => ViewPressed?.Invoke()).SetOffsets(new Vector2(-286, -116), new Vector2(-162, -24));

            // Turn banner and fury meter, centred under them.
            var bannerBox = Node("Turn banner", safe, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-330, -222), new Vector2(330, -152));
            bannerFrame = Fill(bannerBox, new Color(Gold.r, Gold.g, Gold.b, 0.44f));
            Fill(Node("Fill", bannerBox, Vector2.zero, Vector2.one, new Vector2(2, 2), new Vector2(-2, -2)), Panel);
            banner = Label(Node("Text", bannerBox, Vector2.zero, Vector2.one, new Vector2(16, 0), new Vector2(-16, 0)), Sans, 28, GoldLight, TextAnchor.MiddleCenter);
            banner.resizeTextForBestFit = true;
            banner.resizeTextMinSize = 16;
            banner.resizeTextMaxSize = 28;

            var track = Node("Fury", safe, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-170, -246), new Vector2(170, -236));
            Fill(track, new Color(1, 1, 1, 0.1f));
            var fill = Node("Fill", track, Vector2.zero, new Vector2(0, 1));
            var fillImg = fill.gameObject.AddComponent<RawImage>();
            fillImg.texture = Gradient(64, 1, (x, y) => x < 0.5f ? Color.Lerp(Gold, Armies.Hex(0xff7a2a), x * 2) : Color.Lerp(Armies.Hex(0xff7a2a), Armies.Hex(0xff2a10), x * 2 - 1));
            fillImg.raycastTarget = false;
            furyBar = fill;
            furyLabel = Label(Node("Fury label", safe, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-170, -286), new Vector2(170, -250)), Sans, 20, Muted, TextAnchor.MiddleCenter);

            // The narrator, bottom: a gold rule on the left, a small caps label and an italic line.
            var narr = Node("Narration", safe, new Vector2(0, 0), new Vector2(1, 0), new Vector2(24, 24), new Vector2(-24, 190));
            narration = narr.gameObject.AddComponent<CanvasGroup>();
            narration.blocksRaycasts = false;
            Fill(narr, Panel);
            Fill(Node("Rule", narr, Vector2.zero, new Vector2(0, 1), Vector2.zero, new Vector2(4, 0)), Gold);
            narrLabel = Label(Node("Label", narr, new Vector2(0, 1), Vector2.one, new Vector2(30, -56), new Vector2(-30, -14)), Sans, 20, Gold, TextAnchor.MiddleCenter);
            narrText = Label(Node("Text", narr, Vector2.zero, Vector2.one, new Vector2(30, 12), new Vector2(-30, -56)), Serif, 32, Ink, TextAnchor.MiddleCenter, FontStyle.Italic);
            narrText.resizeTextForBestFit = true;   // long lines shrink to fit instead of being cut off
            narrText.resizeTextMinSize = 20;
            narrText.resizeTextMaxSize = 32;

            BuildMenu();
        }

        RectTransform furyBar;

        void BuildMenu()
        {
            menu = Node("Menu panel", safe, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-520, -1024), new Vector2(-24, -132));
            Fill(menu, ButtonLine, true);
            Fill(Node("Fill", menu, Vector2.zero, Vector2.one, new Vector2(2, 2), new Vector2(-2, -2)), new Color(0.07f, 0.08f, 0.065f, 0.96f));
            var heading = Label(Node("Heading", menu, new Vector2(0, 1), Vector2.one, new Vector2(28, -84), new Vector2(-28, -20)), Serif, 36, GoldLight, TextAnchor.MiddleLeft);
            heading.text = "Battle settings";
            float y = -100;
            foreach (var (key, caption) in new[] { ("cinematics", "Cinematic kills"), ("badges", "Piece icons"), ("blood", "Blood"), ("sound", "Sound"), ("music", "Music") })
            {
                var row = Button(key, menu, new Vector2(0, 1), new Vector2(1, 1), "", () => Toggle(key));
                row.SetOffsets(new Vector2(24, y - 92), new Vector2(-24, y));
                var text = row.Find("Text").GetComponent<Text>();
                text.alignment = TextAnchor.MiddleLeft;
                ((RectTransform)text.transform).offsetMin = new Vector2(28, 0);
                text.text = caption;
                var mark = Label(Node("State", row, new Vector2(1, 0), Vector2.one, new Vector2(-120, 0), new Vector2(-28, 0)), Sans, 28, Gold, TextAnchor.MiddleRight);
                toggleMarks[key] = mark;
                y -= 108;
            }
            Button("New game", menu, new Vector2(0, 0), new Vector2(1, 0), "New game", () => { ToggleMenu(); NewGamePressed?.Invoke(); }, true)
                .SetOffsets(new Vector2(24, 132), new Vector2(-24, 224));
            Button("Main menu", menu, new Vector2(0, 0), new Vector2(1, 0), "Main menu", () => { ToggleMenu(); MainMenuPressed?.Invoke(); })
                .SetOffsets(new Vector2(24, 24), new Vector2(-24, 116));
            menu.gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------- what the game tells it

        public void SetTitle(string heading, string matchup)
        {
            title.text = heading;
            sub.text = matchup.ToUpperInvariant();
        }

        public void SetTurn(string text, bool aiThinking, bool check)
        {
            banner.text = text.ToUpperInvariant();
            thinking = aiThinking;
            banner.color = check ? Armies.Hex(0xffb3a8) : GoldLight;
            bannerFrame.color = check ? Danger : new Color(Gold.r, Gold.g, Gold.b, 0.44f);
        }

        public void SetFury(float value, string label)
        {
            fury = Mathf.Clamp01(value);
            furyLabel.text = label.ToUpperInvariant();
            furyLabel.color = fury >= 0.45f ? Armies.Hex(0xff9a5a) : Muted;
        }

        public void Narrate(string label, string text)
        {
            narrLabel.text = label.ToUpperInvariant();
            narrText.text = text;
            narratedAt = Time.unscaledTime;
        }

        public void SetSetting(string key, bool on)
        {
            if (toggleMarks.TryGetValue(key, out var mark)) mark.text = on ? "ON" : "OFF";
            if (key == "badges") ShowBadges = on;
        }

        void Toggle(string key)
        {
            bool on = toggleMarks[key].text != "ON";
            SetSetting(key, on);
            SettingChanged?.Invoke(key, on);
        }

        public void ToggleMenu()
        {
            menuOpen = !menuOpen;
            menu.gameObject.SetActive(menuOpen);
        }

        /// <summary>True when a screen point is on a button or the open menu, so the board ignores the tap.</summary>
        public bool Blocks(Vector2 screen)
        {
            if (group.alpha < 0.5f) return false;
            if (menuOpen && RectTransformUtility.RectangleContainsScreenPoint(menu, screen)) return true;
            foreach (var r in blockers)
                if (r.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(r, screen)) return true;
            // A tap anywhere else closes the menu.
            if (menuOpen) { ToggleMenu(); return true; }
            return false;
        }

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            bool cine = Hidden || (kill != null && kill.Cinematic);
            group.alpha = Mathf.MoveTowards(group.alpha, cine ? 0f : 1f, dt / 0.25f);
            group.blocksRaycasts = !cine;
            if (Hidden && menuOpen) ToggleMenu();
            banner.canvasRenderer.SetAlpha(thinking ? 0.45f + 0.55f * (0.5f + 0.5f * Mathf.Cos(Time.unscaledTime * Mathf.PI * 2)) : 1f);
            furyShown = Mathf.MoveTowards(furyShown, fury, dt / 0.6f);
            furyBar.anchorMax = new Vector2(furyShown, 1);
            float age = Time.unscaledTime - narratedAt;
            narration.alpha = narratedAt < 0 ? 0 : age < 5f ? 1f : Mathf.MoveTowards(narration.alpha, 0.45f, dt / 0.4f);
        }

        void LateUpdate()
        {
            // A badge over each unit's head, sized to the unit's distance so it sits like a marker in the world.
            var cam = Camera.main;
            int used = 0;
            bool show = ShowBadges && group.alpha > 0.01f && Units != null && cam != null;
            if (show)
            {
                var canvasRect = (RectTransform)transform;
                float toCanvas = canvasRect.rect.height / Screen.height;
                foreach (var unit in Units())
                {
                    if (unit == null || unit.Dying) continue;
                    var top = unit.transform.position + Vector3.up * (unit.Height + 0.18f);
                    var p = cam.WorldToScreenPoint(top);
                    if (p.z <= 0) continue;
                    var q = cam.WorldToScreenPoint(top + cam.transform.up * 0.32f);
                    float size = Mathf.Abs(q.y - p.y) * toCanvas;
                    if (used == badges.Count)
                    {
                        var r = Node("Badge", badgeLayer, Vector2.zero, Vector2.zero);
                        var img = r.gameObject.AddComponent<Image>();
                        img.raycastTarget = false;
                        img.preserveAspect = true;
                        badges.Add(img);
                    }
                    var badge = badges[used++];
                    badge.sprite = BadgeArt.Get(unit.Color, unit.Type);
                    var rt = badge.rectTransform;
                    rt.anchoredPosition = new Vector2(p.x, p.y) * toCanvas;
                    rt.sizeDelta = new Vector2(size, size);
                    badge.color = new Color(1, 1, 1, group.alpha);
                    badge.enabled = true;
                }
            }
            for (int i = used; i < badges.Count; i++) badges[i].enabled = false;
        }
    }
}
