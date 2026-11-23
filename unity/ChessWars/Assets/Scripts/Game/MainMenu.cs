// The front end, over the live battlefield: the title screen (as the web game's: "Every capture is a battle",
// CHESS WARS, the lede), then a two-step skirmish setup: choose your army, then choose your enemy, your side and
// the computer's rank. Built in code on the shared Ui pieces. Story is shown as coming soon; Online opens the War Room (OnlineUi).
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ChessWars.Game
{
    using static Ui;

    public class MainMenu : MonoBehaviour
    {
        /// <summary>Start a skirmish: (player's army, enemy army, player's colour, computer depth 1-3).</summary>
        public event Action<string, string, char, int> Battle;
        /// <summary>The Online battle button.</summary>
        public event Action OnlinePressed;

        static readonly string[] Order = { "knights", "undead", "vikings", "samurai", "spartans", "asgardians", "marines", "machines", "mafia" };
        static readonly string[] RankNames = { "", "Recruit", "Veteran", "Warlord" };

        CanvasGroup group;
        RectTransform title, pick;
        Text pickEyebrow, pickTitle;
        RectTransform options, next, toBattle;
        readonly Dictionary<string, RectTransform> cards = new Dictionary<string, RectTransform>();
        readonly Dictionary<char, RectTransform> sides = new Dictionary<char, RectTransform>();
        readonly Dictionary<int, RectTransform> ranks = new Dictionary<int, RectTransform>();
        int step;                 // 0 title, 1 your army, 2 your enemy
        string army, enemy;
        char side;
        int depth;

        public bool Open => group.alpha > 0.01f && group.blocksRaycasts;

        public static MainMenu Create()
        {
            var go = Ui.Canvas("Main menu", 20);
            var menu = go.AddComponent<MainMenu>();
            menu.army = PlayerPrefs.GetString("cw.army", "knights");
            menu.enemy = PlayerPrefs.GetString("cw.enemy", "undead");
            menu.side = PlayerPrefs.GetString("cw.side", "w")[0];
            menu.depth = Mathf.Clamp(PlayerPrefs.GetInt("cw.depth", 2), 1, 3);
            if (!Armies.All.ContainsKey(menu.army)) menu.army = "knights";
            if (!Armies.All.ContainsKey(menu.enemy)) menu.enemy = "undead";
            menu.Build(go.transform);
            return menu;
        }

        public string Army => army;
        public string Enemy => enemy;

        // ---------------------------------------------------------------- layout

        void Build(Transform root)
        {
            group = gameObject.AddComponent<CanvasGroup>();
            var safe = SafeArea(root);
            BuildTitle(safe);
            BuildPick(safe);
            Show(0);
        }

        void BuildTitle(RectTransform safe)
        {
            title = Node("Title screen", safe, Vector2.zero, Vector2.one);
            // Darken the lower half so the words read over any battlefield.
            var shade = Node("Shade", title, Vector2.zero, Vector2.one, new Vector2(-200, -200), new Vector2(200, 200));
            var img = shade.gameObject.AddComponent<RawImage>();
            // Deep behind the buttons and the title, clearing towards the top so the battlefield shows.
            img.texture = Gradient(1, 64, (x, y) => new Color(0.03f, 0.035f, 0.03f, y < 0.62f ? Mathf.Lerp(0.985f, 0.9f, y / 0.62f) : Mathf.Lerp(0.9f, 0.1f, (y - 0.62f) / 0.38f)));
            img.raycastTarget = false;

            Label(Node("Eyebrow", title, new Vector2(0, 0.6f), new Vector2(1, 0.6f), new Vector2(40, 0), new Vector2(-40, 44)), Sans, 26, Gold, TextAnchor.MiddleCenter).text = "EVERY  CAPTURE  IS  A  BATTLE";
            var heading = Label(Node("Heading", title, new Vector2(0, 0.48f), new Vector2(1, 0.6f), new Vector2(20, 0), new Vector2(-20, 0)), Serif, 130, Ink, TextAnchor.MiddleCenter);
            heading.text = "CHESS WARS";
            heading.resizeTextForBestFit = true;
            heading.resizeTextMinSize = 60;
            heading.resizeTextMaxSize = 130;
            var shadow = heading.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.8f);
            shadow.effectDistance = new Vector2(0, -5);
            Label(Node("Lede", title, new Vector2(0, 0.38f), new Vector2(1, 0.48f), new Vector2(80, 0), new Vector2(-80, 0)), Serif, 34, Armies.Hex(0xcfcab8), TextAnchor.UpperCenter, FontStyle.Italic)
                .text = "The rules of chess, unchanged. The pieces, alive. Lead an army across the board and watch every capture play out.";

            float y = 0.3f;
            Button("Skirmish", title, new Vector2(0.5f, y), new Vector2(0.5f, y), "Skirmish", () => Show(1), true).SetOffsets(new Vector2(-320, -60), new Vector2(320, 60));
            y -= 0.075f;
            var story = Button("Story", title, new Vector2(0.5f, y), new Vector2(0.5f, y), "Story campaign  ·  coming soon", () => { });
            story.SetOffsets(new Vector2(-320, -60), new Vector2(320, 60));
            Style(story, false, false);
            y -= 0.075f;
            var online = Button("Online", title, new Vector2(0.5f, y), new Vector2(0.5f, y), "Online battle", () => OnlinePressed?.Invoke());
            online.SetOffsets(new Vector2(-320, -60), new Vector2(320, 60));
        }

        void BuildPick(RectTransform safe)
        {
            pick = Node("Army screen", safe, Vector2.zero, Vector2.one);
            Fill(Node("Backdrop", pick, Vector2.zero, Vector2.one, new Vector2(-200, -200), new Vector2(200, 200)), new Color(0.04f, 0.045f, 0.04f, 0.975f), true);
            pickEyebrow = Label(Node("Eyebrow", pick, new Vector2(0, 1), Vector2.one, new Vector2(40, -80), new Vector2(-40, -36)), Sans, 24, Gold, TextAnchor.MiddleCenter);
            pickTitle = Label(Node("Heading", pick, new Vector2(0, 1), Vector2.one, new Vector2(40, -170), new Vector2(-40, -80)), Serif, 64, Ink, TextAnchor.MiddleCenter);

            // Nine army cards, three by three.
            const float top = -190, cardH = 330, gap = 18;
            for (int i = 0; i < Order.Length; i++)
            {
                string id = Order[i];
                int row = i / 3, col = i % 3;
                float x0 = col / 3f, x1 = (col + 1) / 3f;
                var card = Button(id, pick, new Vector2(x0, 1), new Vector2(x1, 1), "", () => Choose(id));
                card.SetOffsets(new Vector2(col == 0 ? 28 : gap / 2, top - (row + 1) * cardH - row * gap), new Vector2(col == 2 ? -28 : -gap / 2, top - row * cardH - row * gap));
                Card(card, id);
                cards[id] = card;
            }

            // Step 2 extras: which side you lead and how hard the computer plays.
            options = Node("Options", pick, new Vector2(0, 1), Vector2.one, new Vector2(28, top - 3 * cardH - 2 * gap - 250), new Vector2(-28, top - 3 * cardH - 2 * gap - 20));
            Label(Node("Side label", options, new Vector2(0, 1), new Vector2(0.3f, 1), new Vector2(0, -100), Vector2.zero), Sans, 24, Muted, TextAnchor.MiddleLeft).text = "YOU LEAD";
            foreach (var (c, caption, x0, x1) in new[] { ('w', "Ivory · moves first", 0.3f, 0.65f), ('b', "Onyx", 0.66f, 1f) })
            {
                var b = Button("Side " + c, options, new Vector2(x0, 1), new Vector2(x1, 1), caption, () => { side = c; Refresh(); });
                b.SetOffsets(new Vector2(0, -100), new Vector2(0, -8));
                sides[c] = b;
            }
            Label(Node("Rank label", options, new Vector2(0, 1), new Vector2(0.3f, 1), new Vector2(0, -212), new Vector2(0, -112)), Sans, 24, Muted, TextAnchor.MiddleLeft).text = "COMPUTER";
            for (int r = 1; r <= 3; r++)
            {
                int rank = r;
                float x0 = 0.3f + (r - 1) * 0.2367f, x1 = x0 + 0.2267f;
                var b = Button("Rank " + r, options, new Vector2(x0, 1), new Vector2(x1, 1), RankNames[r], () => { depth = rank; Refresh(); });
                b.SetOffsets(new Vector2(0, -212), new Vector2(0, -120));
                ranks[r] = b;
            }

            Button("Back", pick, Vector2.zero, new Vector2(0.38f, 0), "Back", Back).SetOffsets(new Vector2(28, 28), new Vector2(-10, 140));
            next = Button("Next", pick, new Vector2(0.38f, 0), new Vector2(1, 0), "Next: choose your enemy", () => Show(2), true);
            next.SetOffsets(new Vector2(10, 28), new Vector2(-28, 140));
            toBattle = Button("To battle", pick, new Vector2(0.38f, 0), new Vector2(1, 0), "To battle", StartBattle, true);
            toBattle.SetOffsets(new Vector2(10, 28), new Vector2(-28, 140));
        }

        // A card: the army's King, its name, and its signature units, over a wash of the army's colour.
        static void Card(RectTransform card, string id)
        {
            var army = Armies.All[id];
            var wash = Node("Wash", card, Vector2.zero, Vector2.one, new Vector2(2, 2), new Vector2(-2, -2));
            var washImg = wash.gameObject.AddComponent<RawImage>();
            var tint = army.Magic;
            washImg.texture = Gradient(1, 32, (x, y) => new Color(tint.r, tint.g, tint.b, 0.05f + 0.22f * y));
            washImg.raycastTarget = false;
            var portrait = Resources.Load<Texture2D>("Portraits/" + id);
            if (portrait != null)
            {
                // The fitter sizes to its parent, so the picture gets its own area above the name.
                var area = Node("Portrait area", card, new Vector2(0, 0.33f), Vector2.one, new Vector2(6, 0), new Vector2(-6, -6));
                var pic = Node("Portrait", area, Vector2.zero, Vector2.one).gameObject.AddComponent<RawImage>();
                pic.texture = portrait;
                pic.raycastTarget = false;
                var fitter = pic.gameObject.AddComponent<AspectRatioFitter>();
                fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fitter.aspectRatio = 1f;
            }
            var name = card.Find("Text").GetComponent<Text>();
            name.transform.SetAsLastSibling();
            var nr = (RectTransform)name.transform;
            nr.anchorMin = new Vector2(0, 0.19f);
            nr.anchorMax = new Vector2(1, 0.33f);
            nr.offsetMin = new Vector2(8, 0);
            nr.offsetMax = new Vector2(-8, 0);
            name.font = Serif;
            name.fontSize = 30;
            name.resizeTextForBestFit = true;
            name.resizeTextMinSize = 18;
            name.resizeTextMaxSize = 30;
            name.text = army.Name;
            var units = Label(Node("Units", card, Vector2.zero, new Vector2(1, 0.19f), new Vector2(8, 8), new Vector2(-8, 0)), Sans, 18, Muted, TextAnchor.MiddleCenter);
            units.resizeTextForBestFit = true;
            units.resizeTextMinSize = 12;
            units.resizeTextMaxSize = 18;
            units.text = $"{army.Units['q']} · {army.Units['n']} · {army.Units['b']}";
        }

        // ---------------------------------------------------------------- behaviour

        public void Show(int to)
        {
            step = to;
            group.alpha = 1;
            group.blocksRaycasts = true;
            title.gameObject.SetActive(step == 0);
            pick.gameObject.SetActive(step > 0);
            options.gameObject.SetActive(step == 2);
            next.gameObject.SetActive(step == 1);
            toBattle.gameObject.SetActive(step == 2);
            pickEyebrow.text = step == 1 ? "SKIRMISH  ·  STEP 1 OF 2" : "SKIRMISH  ·  STEP 2 OF 2";
            pickTitle.text = step == 1 ? "Choose your army" : "Choose your enemy";
            Refresh();
        }

        public void Hide()
        {
            group.alpha = 0;
            group.blocksRaycasts = false;
        }

        void Choose(string id)
        {
            if (step == 1) army = id;
            else enemy = id;
            Refresh();
        }

        void Refresh()
        {
            string chosen = step == 2 ? enemy : army;
            foreach (var kv in cards) Style(kv.Value, kv.Key == chosen);
            // Keep the card labels in their own colours (Style() recolours the button text).
            foreach (var kv in cards) kv.Value.Find("Text").GetComponent<Text>().color = kv.Key == chosen ? GoldLight : Ink;
            foreach (var kv in sides) Style(kv.Value, kv.Key == side);
            foreach (var kv in ranks) Style(kv.Value, kv.Key == depth);
        }

        void Back()
        {
            if (step > 0) Show(step - 1);
        }

        void StartBattle()
        {
            PlayerPrefs.SetString("cw.army", army);
            PlayerPrefs.SetString("cw.enemy", enemy);
            PlayerPrefs.SetString("cw.side", side.ToString());
            PlayerPrefs.SetInt("cw.depth", depth);
            PlayerPrefs.Save();
            Hide();
            Battle?.Invoke(army, enemy, side, depth);
        }

        void Update()
        {
            // The phone's back button (Escape to the Input System) steps back through the screens.
            if (Open && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Back();
        }
    }
}
