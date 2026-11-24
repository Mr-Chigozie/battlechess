// The online screens, as the web game's (js/online3d.js): sign in / enlist / play as a guest, then the War Room:
// your commander, challenges, invite links to share, your battles, and every other commander. Also the pop-up
// cards (a challenge arrives, a draw is offered, the battle's result) and passing toasts, which the battle uses too.
using System;
using System.Collections.Generic;
using System.Linq;
using ChessWars.Online;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ChessWars.Game
{
    using static Ui;
    using Reply = Dictionary<string, object>;

    public class OnlineUi : MonoBehaviour
    {
        const string GameSite = "https://mr-chigozie.github.io/battlechess/";

        /// <summary>Back to the main menu.</summary>
        public event Action Closed;
        /// <summary>Open this battle (by id) on the board.</summary>
        public event Action<string> JoinRequested;

        Server server;
        CanvasGroup group;
        RectTransform auth, room, cardLayer, cardPanel, toast;
        Text toastText;
        float toastUntil;

        public bool Open => group.blocksRaycasts;
        public bool RoomOpen => Open && room.gameObject.activeSelf;
        public bool CardOpen => cardLayer.gameObject.activeSelf;

        public static OnlineUi Create(Server server)
        {
            var go = Ui.Canvas("Online", 22);
            var ui = go.AddComponent<OnlineUi>();
            ui.server = server;
            ui.Build(go.transform);
            return ui;
        }

        void Build(Transform root)
        {
            group = gameObject.AddComponent<CanvasGroup>();
            var safe = SafeArea(root);
            safe.gameObject.AddComponent<BannerInset>();
            BuildAuth(safe);
            BuildRoom(safe);
            // Cards and toasts sit on their own canvas, above the battle HUD and the kill camera's bars, and stay
            // usable mid-battle while these screens are hidden.
            var top = Ui.Canvas("Online cards", 30).transform;
            var topSafe = SafeArea(top);
            BuildCard(topSafe);
            BuildToast(topSafe);
            Hide();
            server.Changed += () =>
            {
                // Signed in (just now, or last time's sign-in confirmed): on to the War Room.
                if (Open && auth.gameObject.activeSelf && server.SignedIn && (mode == "guest" || mode == "login" || mode == "register")) ShowRoom();
                else if (RoomOpen) RenderRoom();
                TryPendingInvite();
            };
            server.Toast += ShowToast;
        }

        // ---------------------------------------------------------------- showing

        /// <summary>The War Room if signed in, otherwise the sign-in screen.</summary>
        public void Show()
        {
            group.alpha = 1;
            group.blocksRaycasts = true;
            if (server.SignedIn) ShowRoom();
            else ShowAuth("guest");
        }

        string pendingInvite;   // from a link the app was opened with, waiting for the player to be signed in

        /// <summary>An invite link the app was opened with (https://…/i/CODE or chesswars://invite/CODE): sign in
        /// first if needed (a guest name is enough), then the challenge card.</summary>
        public void OpenLink(string url)
        {
            var code = Server.InviteCode(url);
            if (code == null) return;
            pendingInvite = code;
            Show();
            TryPendingInvite();
        }

        void TryPendingInvite()
        {
            if (pendingInvite == null || !server.SignedIn || !RoomOpen) return;
            var code = pendingInvite;
            pendingInvite = null;
            OpenInvite(code);
        }

        public void Hide()
        {
            group.alpha = 0;
            group.blocksRaycasts = false;
            auth.gameObject.SetActive(false);
            room.gameObject.SetActive(false);
        }

        void Update()
        {
            if (toast.gameObject.activeSelf && Time.unscaledTime > toastUntil) toast.gameObject.SetActive(false);
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                if (CardOpen) { if (cardBack != null) cardBack(); else HideCard(); }
                else if (auth.gameObject.activeSelf) AuthBack();
                else if (RoomOpen) Close();
            }
        }

        void Close()
        {
            Hide();
            Closed?.Invoke();
        }

        // ---------------------------------------------------------------- small builders

        static RectTransform Panel(string name, Transform parent)
        {
            var r = Node(name, parent, Vector2.zero, Vector2.one);
            Fill(Node("Backdrop", r, Vector2.zero, Vector2.one, new Vector2(-200, -200), new Vector2(200, 200)), new Color(0.04f, 0.045f, 0.04f, 0.975f), true);
            return r;
        }

        static InputField Field(RectTransform parent, string name, string placeholder, InputField.ContentType type, int limit)
        {
            var r = Node(name, parent, new Vector2(0, 1), new Vector2(1, 1));
            var border = Fill(r, ButtonLine, true);
            Fill(Node("Fill", r, Vector2.zero, Vector2.one, new Vector2(2, 2), new Vector2(-2, -2)), new Color(0.075f, 0.082f, 0.063f, 0.98f));
            var text = Label(Node("Text", r, Vector2.zero, Vector2.one, new Vector2(26, 0), new Vector2(-26, 0)), Sans, 34, Ink, TextAnchor.MiddleLeft);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            var hint = Label(Node("Hint", r, Vector2.zero, Vector2.one, new Vector2(26, 0), new Vector2(-26, 0)), Sans, 30, Muted, TextAnchor.MiddleLeft, FontStyle.Italic);
            hint.text = placeholder;
            var input = r.gameObject.AddComponent<InputField>();
            input.textComponent = text;
            input.placeholder = hint;
            input.targetGraphic = border;
            input.contentType = type;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = limit;
            input.caretColor = GoldLight;
            input.selectionColor = new Color(Gold.r, Gold.g, Gold.b, 0.4f);
            return input;
        }

        static Text Caption(RectTransform parent, string name, string text, int size, Color color, TextAnchor align = TextAnchor.MiddleLeft, FontStyle style = FontStyle.Normal, Font font = null)
        {
            var t = Label(Node(name, parent, new Vector2(0, 1), new Vector2(1, 1)), font ?? Sans, size, color, align, style);
            t.text = text;
            return t;
        }

        // Places a rect as a full-width band `height` tall, `top` pixels below the top of its parent.
        static RectTransform Band(Component c, float top, float height, float left = 40, float right = 40)
        {
            var r = (RectTransform)c.transform;
            r.anchorMin = new Vector2(0, 1);
            r.anchorMax = new Vector2(1, 1);
            r.offsetMin = new Vector2(left, -top - height);
            r.offsetMax = new Vector2(-right, -top);
            return r;
        }

        static string ArmyName(string id) => Armies.All.TryGetValue(id ?? "", out var a) ? a.Name : "Unknown army";
        static string ClockName(string type) => type == "daily" ? "Daily · a move a day" : "Live · 10 min + 5 s";

        // ---------------------------------------------------------------- sign in, enlist, guest

        string mode = "guest";
        RectTransform authBody;
        Text authEyebrow, authTitle, authNote, authError;
        InputField fName, fEmail, fPass, fCode;
        RectTransform armyRow, submit, forgot, terms, tabs;
        Text armyLabel;
        readonly Dictionary<string, RectTransform> tabButtons = new Dictionary<string, RectTransform>();
        string authArmy = "knights";
        bool needCode, busy;
        static readonly string[] OnlineArmies = { "knights", "undead", "vikings", "samurai", "spartans", "asgardians", "marines", "machines" };

        static readonly Dictionary<string, (string title, string submit)> Modes = new Dictionary<string, (string, string)>
        {
            ["guest"] = ("Take the field", "Play now"),
            ["login"] = ("Report for duty", "Sign in"),
            ["register"] = ("Enlist as a commander", "Create account"),
            ["upgrade"] = ("Keep your record", "Create account"),
            ["delete"] = ("Delete your commander", "Delete forever"),
        };

        void BuildAuth(RectTransform safe)
        {
            auth = Panel("Sign in screen", safe);
            authEyebrow = Caption(auth, "Eyebrow", "ONLINE  BATTLE", 24, Gold, TextAnchor.MiddleCenter);
            Band(authEyebrow, 50, 40);
            authTitle = Caption(auth, "Heading", "", 60, Ink, TextAnchor.MiddleCenter, FontStyle.Normal, Serif);
            Band(authTitle, 96, 90);
            tabs = Node("Tabs", auth, new Vector2(0, 1), new Vector2(1, 1));
            Band(tabs, 210, 96);
            var tabList = new[] { ("guest", "Guest"), ("login", "Sign in"), ("register", "Enlist") };
            for (int i = 0; i < tabList.Length; i++)
            {
                var (key, caption) = tabList[i];
                var b = Button("Tab " + key, tabs, new Vector2(i / 3f, 0), new Vector2((i + 1) / 3f, 1), caption, () => SetMode(key));
                b.SetOffsets(new Vector2(i == 0 ? 0 : 6, 0), new Vector2(i == 2 ? 0 : -6, 0));
                tabButtons[key] = b;
            }
            authBody = Node("Form", auth, Vector2.zero, Vector2.one);
            authNote = Caption(authBody, "Note", "", 28, Armies.Hex(0xcfcab8), TextAnchor.UpperLeft, FontStyle.Italic, Serif);
            authNote.horizontalOverflow = HorizontalWrapMode.Wrap;
            fName = Field(authBody, "Name", "Commander name", InputField.ContentType.Standard, 20);
            fEmail = Field(authBody, "Email", "Email", InputField.ContentType.EmailAddress, 190);
            fPass = Field(authBody, "Password", "Password", InputField.ContentType.Password, 200);
            fCode = Field(authBody, "Code", "Beta invite code", InputField.ContentType.Standard, 40);
            armyRow = Node("Army", authBody, new Vector2(0, 1), new Vector2(1, 1));
            Button("Prev", armyRow, Vector2.zero, new Vector2(0, 1), "‹", () => CycleArmy(-1)).SetOffsets(Vector2.zero, new Vector2(110, 0));
            Button("Next", armyRow, new Vector2(1, 0), Vector2.one, "›", () => CycleArmy(1)).SetOffsets(new Vector2(-110, 0), Vector2.zero);
            armyLabel = Label(Node("Label", armyRow, Vector2.zero, Vector2.one, new Vector2(120, 0), new Vector2(-120, 0)), Serif, 34, GoldLight, TextAnchor.MiddleCenter);
            authError = Caption(authBody, "Error", "", 28, Armies.Hex(0xff9a8a), TextAnchor.UpperCenter);
            submit = Button("Submit", authBody, new Vector2(0, 1), new Vector2(1, 1), "", Submit, true);
            forgot = Button("Forgot", authBody, new Vector2(0, 1), new Vector2(1, 1), "Forgot your password?", Forgot);
            terms = Node("Terms", authBody, new Vector2(0, 1), new Vector2(1, 1));
            Label(Node("Text", terms, new Vector2(0, 0.5f), Vector2.one), Sans, 24, Muted, TextAnchor.MiddleCenter).text = "By playing online you agree to the Terms and the Privacy Policy.";
            Button("Terms", terms, Vector2.zero, new Vector2(0.5f, 0.5f), "Terms", () => Application.OpenURL(GameSite + "terms.html")).SetOffsets(new Vector2(60, 0), new Vector2(-8, -6));
            Button("Privacy", terms, new Vector2(0.5f, 0), new Vector2(1, 0.5f), "Privacy", () => Application.OpenURL(GameSite + "privacy.html")).SetOffsets(new Vector2(8, 0), new Vector2(-60, -6));
            Button("Back", auth, Vector2.zero, new Vector2(0.4f, 0), "Back", AuthBack).SetOffsets(new Vector2(40, 32), new Vector2(-10, 140));
        }

        void ShowAuth(string m)
        {
            group.alpha = 1;
            group.blocksRaycasts = true;
            room.gameObject.SetActive(false);
            auth.gameObject.SetActive(true);
            authArmy = server.Me?.Army ?? PlayerPrefs.GetString("cw.army", "knights");
            if (!Armies.Online(authArmy)) authArmy = "knights";
            SetMode(m);
        }

        /// <summary>A guest keeps their record: the account form.</summary>
        public void ShowUpgrade() => ShowAuth("upgrade");

        void AuthBack()
        {
            if ((mode == "upgrade" || mode == "delete") && server.SignedIn) ShowRoom();
            else Close();
        }

        void SetMode(string m)
        {
            mode = m;
            bool account = m == "upgrade" || m == "delete";
            tabs.gameObject.SetActive(!account);
            foreach (var kv in tabButtons) Style(kv.Value, kv.Key == m);
            authTitle.text = Modes[m].title;
            submit.Find("Text").GetComponent<Text>().text = Modes[m].submit;
            authError.text = "";
            authError.color = Armies.Hex(0xff9a8a);
            var show = new HashSet<string>();
            switch (m)
            {
                case "guest": show.UnionWith(new[] { "name", "army" }); break;
                case "login": show.UnionWith(new[] { "email", "pass", "forgot" }); break;
                case "register": show.UnionWith(new[] { "name", "email", "pass", "army" }); if (needCode) show.Add("code"); break;
                case "upgrade": show.UnionWith(new[] { "name", "email", "pass" }); break;
                case "delete": if (server.Me != null && !server.Me.Guest) show.Add("pass"); break;
            }
            if (m == "guest" || m == "register" || m == "upgrade") show.Add("terms");
            if (m == "upgrade" && server.Me != null) fName.text = server.Me.Name;
            authNote.text = m switch
            {
                "guest" when pendingInvite != null => "A friend has challenged you! Pick a commander name and an army to answer as a guest, or sign in.",
                "guest" => "Pick a commander name and an army and you're in: no email needed. You can keep your record with an account later.",
                "register" => "An account keeps your record and lets you sign in on any phone or on the web.",
                "upgrade" => "Add an email and password and your wins and losses stay with you.",
                "delete" => "This wipes your name, email and record for good. Finished battles stay in your rivals' history under an anonymous name.",
                _ => "",
            };
            fPass.placeholder.GetComponent<Text>().text = m == "login" || m == "delete" ? "Password" : "Password (8+ characters)";
            // Lay the visible parts out top to bottom.
            float y = account ? 210 : 330;
            if (authNote.text.Length > 0) { Band(authNote, y, 110); y += 124; }
            authNote.gameObject.SetActive(authNote.text.Length > 0);
            foreach (var (key, part, h) in new (string, Component, float)[] { ("name", fName, 100), ("email", fEmail, 100), ("pass", fPass, 100), ("code", fCode, 100), ("army", armyRow, 100) })
            {
                part.gameObject.SetActive(show.Contains(key));
                if (!show.Contains(key)) continue;
                Band(part, y, h);
                y += h + 20;
            }
            armyLabel.text = ArmyName(authArmy);
            Band(authError, y, 80);
            y += 90;
            Band(submit, y, 120);
            if (m == "delete") submit.GetComponent<Image>().color = Danger;
            else submit.GetComponent<Image>().color = PrimaryLine;
            y += 140;
            forgot.gameObject.SetActive(show.Contains("forgot"));
            if (show.Contains("forgot")) { Band(forgot, y, 90, 200, 200); y += 110; }
            terms.gameObject.SetActive(show.Contains("terms"));
            if (show.Contains("terms")) Band(terms, y, 150, 40, 40);
        }

        void CycleArmy(int step)
        {
            int i = Array.IndexOf(OnlineArmies, authArmy);
            authArmy = OnlineArmies[(i + step + OnlineArmies.Length) % OnlineArmies.Length];
            armyLabel.text = ArmyName(authArmy);
        }

        void Submit()
        {
            if (busy) return;
            string name = fName.text.Trim(), email = fEmail.text.Trim(), pass = fPass.text;
            authError.color = Armies.Hex(0xff9a8a);
            if (fName.gameObject.activeSelf && (name.Length < 2 || name.Length > 20)) { authError.text = "Pick a commander name of 2 to 20 characters."; return; }
            if (fEmail.gameObject.activeSelf && email.Length == 0) { authError.text = "Enter your email."; return; }
            if (fPass.gameObject.activeSelf && pass.Length == 0) { authError.text = "Enter your password."; return; }
            busy = true;
            authError.color = Muted;
            authError.text = "Contacting the server… (it can take up to a minute to wake)";
            void Done(string err)
            {
                busy = false;
                fPass.text = "";
                authError.color = Armies.Hex(0xff9a8a);
                authError.text = err ?? "";
            }
            switch (mode)
            {
                case "upgrade":
                    server.Upgrade(name, email, pass, err =>
                    {
                        Done(err);
                        if (err == null) { ShowToast($"Account created. Welcome to the ranks, {server.Me.Name}."); ShowRoom(); }
                    });
                    break;
                case "delete":
                    server.DeleteAccount(pass, err =>
                    {
                        Done(err);
                        if (err == null) { ShowToast("Your commander has been deleted."); Close(); }
                    });
                    break;
                default:
                    var fields = new Reply();
                    if (mode != "login") { fields["name"] = name; fields["army"] = authArmy; }
                    if (mode != "guest") { fields["email"] = email; fields["password"] = pass; }
                    if (mode == "register") fields["code"] = fCode.text.Trim();
                    server.Authenticate(mode, fields, (err, code) =>
                    {
                        Done(err);
                        if (code == "beta" && !needCode) { needCode = true; SetMode(mode); authError.text = err; }
                        if (err == null) { ShowToast($"Welcome, {server.Me.Name}."); ShowRoom(); }
                    });
                    break;
            }
        }

        void Forgot()
        {
            string email = fEmail.text.Trim();
            if (email.Length == 0) { authError.text = "Type your email above first, then tap \"Forgot your password?\" again."; return; }
            server.Forgot(email, (text, ok) =>
            {
                authError.color = ok ? Armies.Hex(0x8fd99f) : Armies.Hex(0xff9a8a);
                authError.text = text;
            });
        }

        // ---------------------------------------------------------------- the War Room

        RectTransform listContent;
        Text roomName, roomRecord, roomStatus, codeHint;
        InputField codeField;
        readonly Dictionary<string, RectTransform> clockButtons = new Dictionary<string, RectTransform>();
        RectTransform onlineOnlyButton;
        string clock = "live";
        bool onlineOnly;

        void BuildRoom(RectTransform safe)
        {
            room = Panel("War room", safe);
            Band(Caption(room, "Eyebrow", "WAR  ROOM", 24, Gold, TextAnchor.MiddleCenter), 40, 40);
            roomName = Caption(room, "Name", "", 56, Ink, TextAnchor.MiddleLeft, FontStyle.Normal, Serif);
            Band(roomName, 90, 80, 40, 300);
            roomRecord = Caption(room, "Record", "", 26, Muted);
            Band(roomRecord, 170, 40, 40, 300);
            roomStatus = Caption(room, "Status", "", 22, Muted);
            Band(roomStatus, 210, 34, 40, 300);
            var account = Button("Account", room, new Vector2(1, 1), new Vector2(1, 1), "Account", AccountCard);
            account.SetOffsets(new Vector2(-280, -200), new Vector2(-40, -100));

            // Your army, and the clock for new challenges and invites.
            var army = Node("Army", room, new Vector2(0, 1), new Vector2(1, 1));
            Band(army, 262, 92);
            Button("Prev", army, Vector2.zero, new Vector2(0, 1), "‹", () => ChangeArmy(-1)).SetOffsets(Vector2.zero, new Vector2(100, 0));
            Button("Next", army, new Vector2(1, 0), Vector2.one, "›", () => ChangeArmy(1)).SetOffsets(new Vector2(-100, 0), Vector2.zero);
            Label(Node("Label", army, Vector2.zero, Vector2.one, new Vector2(110, 0), new Vector2(-110, 0)), Serif, 32, GoldLight, TextAnchor.MiddleCenter).name = "Army label";
            var clocks = Node("Clocks", room, new Vector2(0, 1), new Vector2(1, 1));
            Band(clocks, 368, 84);
            foreach (var (key, caption, x0, x1) in new[] { ("live", "Live · 10 min + 5 s", 0f, 0.5f), ("daily", "Daily · a move a day", 0.5f, 1f) })
            {
                var b = Button("Clock " + key, clocks, new Vector2(x0, 0), new Vector2(x1, 1), caption, () => { clock = key; RenderRoom(); });
                b.SetOffsets(new Vector2(x0 == 0 ? 0 : 6, 0), new Vector2(x1 == 1 ? 0 : -6, 0));
                clockButtons[key] = b;
            }

            // Bring a friend: share a link, or paste one you were sent.
            var invite = Button("Invite", room, new Vector2(0, 1), new Vector2(1, 1), "Invite a friend (WhatsApp, SMS…)", CreateInvite, true);
            Band(invite, 466, 104);
            codeField = Field(room, "Code", "Got a link or code? Paste it here", InputField.ContentType.Standard, 300);
            Band(codeField, 584, 96, 40, 260);
            var join = Button("Join", room, new Vector2(1, 1), new Vector2(1, 1), "Join", () => OpenInvite(codeField.text));
            join.SetOffsets(new Vector2(-244, -680), new Vector2(-40, -584));
            codeHint = Caption(room, "Code hint", "", 22, Muted, TextAnchor.MiddleLeft);
            Band(codeHint, 684, 30);

            // Everything else scrolls: challenges, open links, battles, commanders.
            var view = Node("List", room, Vector2.zero, Vector2.one, new Vector2(40, 170), new Vector2(-40, -724));
            Fill(view, new Color(0, 0, 0, 0.001f), true);
            view.gameObject.AddComponent<RectMask2D>();
            listContent = Node("Content", view, new Vector2(0, 1), new Vector2(1, 1));
            listContent.pivot = new Vector2(0.5f, 1);
            var layout = listContent.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10;
            layout.childControlHeight = layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.padding = new RectOffset(0, 0, 4, 24);
            listContent.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.content = listContent;
            scroll.viewport = view;
            scroll.horizontal = false;
            scroll.scrollSensitivity = 30;

            Button("Back", room, Vector2.zero, new Vector2(0.4f, 0), "Main menu", Close).SetOffsets(new Vector2(40, 32), new Vector2(-10, 140));
            onlineOnlyButton = Button("Online only", room, new Vector2(0.4f, 0), new Vector2(1, 0), "", () => { onlineOnly = !onlineOnly; RenderRoom(); });
            onlineOnlyButton.SetOffsets(new Vector2(10, 32), new Vector2(-40, 140));
        }

        void ShowRoom()
        {
            group.alpha = 1;
            group.blocksRaycasts = true;
            auth.gameObject.SetActive(false);
            room.gameObject.SetActive(true);
            server.RefreshLobby();
            RenderRoom();
            TryPendingInvite();
        }

        void ChangeArmy(int step)
        {
            if (server.Me == null) return;
            int i = Array.IndexOf(OnlineArmies, server.Me.Army);
            var next = OnlineArmies[(Math.Max(0, i) + step + OnlineArmies.Length) % OnlineArmies.Length];
            room.Find("Army/Army label").GetComponent<Text>().text = ArmyName(next);
            server.ChangeArmy(next, err => { if (err != null) ShowToast(err); RenderRoom(); });
        }

        void RenderRoom()
        {
            var me = server.Me;
            if (me == null) { if (RoomOpen) ShowAuth("guest"); return; }
            roomName.text = me.Name;
            roomRecord.text = $"{(me.Guest ? "Guest · " : "")}{me.Wins} wins · {me.Losses} losses · {me.Draws} draws";
            roomStatus.text = server.Connected ? "<color=#8fd99f>●</color> Connected" : "<color=#d4564a>●</color> Connecting to the server…";
            room.Find("Army/Army label").GetComponent<Text>().text = ArmyName(me.Army);
            foreach (var kv in clockButtons) Style(kv.Value, kv.Key == clock);

            foreach (Transform child in listContent) Destroy(child.gameObject);
            var lobby = server.Lobby;
            if (lobby == null)
            {
                Empty(server.Connected ? "Loading the War Room…" : "Waking the server… this can take up to a minute.");
                SetOnlineOnly(0);
                return;
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            Section("CHALLENGES");
            int before = listContent.childCount;
            foreach (Reply c in lobby.List("incoming"))
            {
                long id = c.Long("id");
                Row(true, $"{c.Str("fromName")} challenges you", $"{ArmyName(c.Str("fromArmy"))} as Ivory · {ClockName(c.Str("clock"))}",
                    ("Accept", () => Answer(id, "accept"), true), ("Decline", () => Answer(id, "decline"), false));
            }
            foreach (Reply c in lobby.List("outgoing"))
            {
                long id = c.Long("id");
                Row(false, $"Waiting for {c.Str("toName")}…", $"Challenge sent · {ClockName(c.Str("clock"))}", ("Cancel", () => Answer(id, "cancel"), false));
            }
            foreach (Reply inv in lobby.List("invites"))
            {
                string url = inv.Str("url"), code = inv.Str("code");
                Row(false, $"Invite link  {code}", $"{ClockName(inv.Str("clock"))} · waiting for a friend",
                    ("Share", () => Share(url), false), ("✕", () => server.Emit("invite:cancel", new Reply { ["code"] = code }, _ => server.RefreshLobby()), false));
            }
            if (listContent.childCount == before) Empty("No challenges yet. Challenge a commander below, or invite a friend with a link.");

            Section("YOUR BATTLES");
            before = listContent.childCount;
            var games = lobby.List("games").Cast<Reply>().OrderByDescending(g => g.Num("updatedAt")).ToList();
            foreach (var g in games.Where(g => g.Str("status") == "active"))
            {
                string id = g.Str("id");
                bool myTurn = g.Bool("myTurn");
                Row(myTurn, $"vs {g.Str("rival")}", $"{(myTurn ? "Your move" : "Their move")} · move {g.Int("ply") / 2 + 1} · {(g.Str("clock") == "daily" ? "daily" : "live")}",
                    ("Resume", () => JoinRequested?.Invoke(id), myTurn));
            }
            foreach (var g in games.Where(g => g.Str("status") == "finished"))
            {
                string reason = g.Str("reason"), result = g.Str("result");
                string outcome = reason == "aborted" ? "Called off" : result == "draw" ? "Draw" : result == g.Str("myColor") ? "Victory" : "Defeat";
                Row(false, $"{outcome} vs {g.Str("rival")}", ReasonText(reason));
            }
            if (listContent.childCount == before) Empty("No battles yet.");

            var users = lobby.List("users").Cast<Reply>().ToList();
            int onlineNow = users.Count(u => u.Bool("online"));
            SetOnlineOnly(onlineNow);
            Section($"COMMANDERS  ·  {onlineNow} ONLINE");
            before = listContent.childCount;
            var outgoing = new HashSet<long>(lobby.List("outgoing").Cast<Reply>().Select(c => c.Long("toId")));
            foreach (var u in users.Where(u => !onlineOnly || u.Bool("online"))
                                   .OrderByDescending(u => u.Bool("online")).ThenBy(u => u.Bool("playing")).ThenByDescending(u => u.Num("lastSeen")))
            {
                var target = u;
                bool sent = outgoing.Contains(u.Long("id"));
                var row = Row(u.Bool("online"), $"{u.Str("name")}{(u.Bool("guest") ? " (guest)" : "")}",
                    $"{ArmyName(u.Str("army"))} · {u.Int("wins")}W {u.Int("losses")}L {u.Int("draws")}D · {SeenText(u, now)}",
                    (sent ? "Sent" : "Challenge", () => Challenge(target), !sent && u.Bool("online")), ("⋯", () => UserCard(target), false));
                if (sent) Style((RectTransform)row.Find("Buttons/Challenge"), false, false);
            }
            if (listContent.childCount == before)
                Empty(onlineOnly ? "Nobody else is online right now. Invite a friend with a link!" : "No other commanders yet. Invite a friend with a link!");
        }

        void SetOnlineOnly(int count)
        {
            onlineOnlyButton.Find("Text").GetComponent<Text>().text = onlineOnly ? "Show everyone" : $"Online now ({count})";
            Style(onlineOnlyButton, onlineOnly);
        }

        static string ReasonText(string reason) => reason switch
        {
            "checkmate" => "by checkmate", "resign" => "by resignation", "timeout" => "on time", "agreement" => "by agreement",
            "stalemate" => "stalemate", "repetition" => "by repetition", "fifty-move" => "fifty-move rule", "insufficient" => "not enough pieces",
            "aborted" => "the first move never came", _ => reason ?? "",
        };

        static string SeenText(Reply u, long now)
        {
            if (u.Bool("playing")) return "in battle";
            if (u.Bool("online")) return "online";
            long minutes = (long)Math.Round((now - u.Num("lastSeen")) / 60000.0);
            if (minutes < 60) return $"seen {Math.Max(1, minutes)} min ago";
            long hours = (long)Math.Round(minutes / 60.0);
            return hours < 48 ? $"seen {hours} h ago" : $"seen {Math.Round(hours / 24.0)} days ago";
        }

        void Section(string title)
        {
            var r = Node(title, listContent, Vector2.zero, Vector2.one);
            r.gameObject.AddComponent<LayoutElement>().preferredHeight = 70;
            Label(Node("Text", r, Vector2.zero, Vector2.one, new Vector2(4, 0), Vector2.zero), Sans, 24, Gold, TextAnchor.LowerLeft).text = title;
        }

        void Empty(string text)
        {
            var r = Node("Empty", listContent, Vector2.zero, Vector2.one);
            r.gameObject.AddComponent<LayoutElement>().preferredHeight = 90;
            var t = Label(Node("Text", r, Vector2.zero, Vector2.one, new Vector2(4, 0), Vector2.zero), Serif, 28, Muted, TextAnchor.MiddleLeft, FontStyle.Italic);
            t.text = text;
        }

        // A list row: a status dot, a name and a line of detail, and its buttons on the right.
        RectTransform Row(bool lit, string name, string meta, params (string label, Action action, bool primary)[] buttons)
        {
            var r = Node("Row", listContent, Vector2.zero, Vector2.one);
            r.gameObject.AddComponent<LayoutElement>().preferredHeight = 130;
            Fill(r, new Color(0.11f, 0.12f, 0.1f, 0.9f));
            Fill(Node("Dot", r, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(22, -9), new Vector2(40, 9)), lit ? Armies.Hex(0x8fd99f) : Armies.Hex(0x4a4b3f));
            float right = 20 + buttons.Sum(b => (b.label.Length <= 2 ? 90 : 200) + 12);
            var who = Label(Node("Name", r, new Vector2(0, 0.5f), new Vector2(1, 1), new Vector2(60, 0), new Vector2(-right, -10)), Serif, 32, Ink, TextAnchor.LowerLeft);
            who.text = name;
            who.horizontalOverflow = HorizontalWrapMode.Overflow;
            var detail = Label(Node("Meta", r, Vector2.zero, new Vector2(1, 0.5f), new Vector2(60, 10), new Vector2(-right, 0)), Sans, 22, Muted, TextAnchor.UpperLeft);
            detail.text = meta;
            detail.resizeTextForBestFit = true;
            detail.resizeTextMinSize = 16;
            detail.resizeTextMaxSize = 22;
            var row = Node("Buttons", r, Vector2.zero, Vector2.one);
            float x = -20;
            foreach (var (label, action, primary) in buttons.Reverse())
            {
                float w = label.Length <= 2 ? 90 : 200;
                var b = Button(label == "Sent" ? "Challenge" : label, row, new Vector2(1, 0.5f), new Vector2(1, 0.5f), label, action, primary);
                b.SetOffsets(new Vector2(x - w, -42), new Vector2(x, 42));
                b.Find("Text").GetComponent<Text>().fontSize = 26;
                x -= w + 12;
            }
            return r;
        }

        // ---------------------------------------------------------------- War Room actions

        void Answer(long id, string action) =>
            server.Emit("challenge:answer", new Reply { ["id"] = id, ["action"] = action }, r =>
            {
                if (r.Has("error")) { ShowToast(r.Str("message")); return; }
                if (r.Str("gameId") is string game) JoinRequested?.Invoke(game);
            });

        void Challenge(Reply u) =>
            server.Emit("challenge:send", new Reply { ["to"] = u.Long("id"), ["clock"] = clock }, r =>
                ShowToast(r.Has("error") ? r.Str("message") : $"Challenge sent to {u.Str("name")}."));

        void CreateInvite() =>
            server.Emit("invite:create", new Reply { ["clock"] = clock }, r =>
            {
                if (r.Has("error")) { ShowToast(r.Str("message")); return; }
                Share(r.Str("url"));
            });

        // The phone's share sheet (WhatsApp, SMS…), and the link on the clipboard either way.
        void Share(string url)
        {
            string text = $"{server.Me?.Name ?? "A commander"} challenges you to a battle in Chess Wars! Tap to play: {url}";
            GUIUtility.systemCopyBuffer = url;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var intentClass = new AndroidJavaClass("android.content.Intent");
                using var intent = new AndroidJavaObject("android.content.Intent");
                intent.Call<AndroidJavaObject>("setAction", intentClass.GetStatic<string>("ACTION_SEND")).Dispose();
                intent.Call<AndroidJavaObject>("setType", "text/plain").Dispose();
                intent.Call<AndroidJavaObject>("putExtra", intentClass.GetStatic<string>("EXTRA_TEXT"), text).Dispose();
                using var chooser = intentClass.CallStatic<AndroidJavaObject>("createChooser", intent, "Send the challenge");
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                activity.Call("startActivity", chooser);
                return;
            }
            catch (Exception e) { Debug.LogWarning("Share failed: " + e.Message); }
#endif
            ShowToast("Invite link copied. Paste it to a friend!");
        }

        /// <summary>A friend's invite (a code or a pasted link): who's asking, then into the battle.</summary>
        public void OpenInvite(string text)
        {
            var code = Server.InviteCode(text);
            if (code == null) { codeHint.text = "That doesn't look like an invite link or code."; return; }
            codeHint.text = "Looking up the invite…";
            server.Http("GET", "/api/invites/" + code, null, (r, err, status) =>
            {
                codeHint.text = "";
                if (err != null) { ShowToast(status == 404 ? "That invite doesn't exist. Check you copied all of it." : err); return; }
                var inv = r.Obj("invite");
                if (!inv.Bool("open")) { ShowToast("That invite has already been used or has expired. Ask your friend for a new link."); return; }
                if (server.Me != null && inv.Long("fromId") == server.Me.Id) { ShowToast("That's your own invite link. Send it to a friend!"); return; }
                ShowCard("You've been challenged", $"{inv.Str("fromName")} challenges you",
                    new[] { $"They lead the {ArmyName(inv.Str("fromArmy"))} as Ivory and move first. You command the {ArmyName(server.Me?.Army)} as Onyx.", ClockName(inv.Str("clock")) },
                    ("Not now", HideCard, false),
                    ("To battle!", () =>
                    {
                        HideCard();
                        server.Emit("invite:accept", new Reply { ["code"] = code }, a =>
                        {
                            if (a.Has("error")) { ShowToast(a.Str("message")); return; }
                            codeField.text = "";
                            JoinRequested?.Invoke(a.Str("gameId"));
                        });
                    }, true));
            });
        }

        void AccountCard()
        {
            var me = server.Me;
            if (me == null) return;
            var buttons = new List<(string, Action, bool)>();
            if (me.Guest) buttons.Add(("Keep your record (create account)", () => { HideCard(); ShowAuth("upgrade"); }, true));
            buttons.Add(("Sign out", () =>
            {
                HideCard();
                if (me.Guest)
                    ShowCard("Sign out", "Leave this guest commander?", new[] { "Guests can't sign back in. Create an account first to keep your record." },
                        ("Stay", HideCard, true), ("Sign out", () => { HideCard(); server.SignOut(); Close(); }, false));
                else { server.SignOut(); Close(); }
            }, false));
            buttons.Add(("Delete my commander", () => { HideCard(); if (me.Guest) ConfirmGuestDelete(); else ShowAuth("delete"); }, false));
            buttons.Add(("Back", HideCard, false));
            ShowCard("Account", me.Name, new[] { $"{(me.Guest ? "Guest commander" : "Commander")} · {ArmyName(me.Army)}", $"{me.Wins} wins · {me.Losses} losses · {me.Draws} draws" }, buttons.ToArray());
        }

        void ConfirmGuestDelete() =>
            ShowCard("Delete", "Delete this commander?", new[] { "This wipes your name and record for good." },
                ("Keep it", HideCard, true),
                ("Delete forever", () => server.DeleteAccount(null, err =>
                {
                    HideCard();
                    ShowToast(err ?? "Your commander has been deleted.");
                    if (err == null) Close();
                }), false));

        void UserCard(Reply u)
        {
            string name = u.Str("name");
            long id = u.Long("id");
            ShowCard("Commander", name, new[] { $"{ArmyName(u.Str("army"))} · {u.Int("wins")}W {u.Int("losses")}L {u.Int("draws")}D" },
                ("Challenge", () => { HideCard(); Challenge(u); }, true),
                ("Report", () => Report(id, name, null, HideCard), false),
                ("Block", () => server.Emit("user:block", new Reply { ["id"] = id }, r =>
                {
                    HideCard();
                    ShowToast(r.Has("error") ? r.Str("message") : $"{name} is blocked. They can't see or challenge you.");
                }), false),
                ("Back", HideCard, false));
        }

        /// <summary>Report a player (from the War Room or a battle).</summary>
        public void Report(long id, string name, string gameId, Action back)
        {
            Action Send(string reason) => () => server.Emit("user:report", new Reply { ["id"] = id, ["gameId"] = gameId, ["reason"] = reason }, r =>
            {
                ShowToast(r.Has("error") ? r.Str("message") : "Thanks. The report has been sent to the moderators.");
                back();
            });
            ShowCard("Report", $"Report {name}", new[] { "What's the problem?" },
                ("Offensive name", Send("Offensive name"), false),
                ("Stalling or abandoning", Send("Stalling or abandoning battles"), false),
                ("Something else", Send("Other"), false),
                ("Cancel", back, true));
        }

        // ---------------------------------------------------------------- cards and toasts

        Action cardBack;

        void BuildCard(RectTransform safe)
        {
            cardLayer = Node("Card layer", safe, Vector2.zero, Vector2.one);
            Fill(Node("Dim", cardLayer, Vector2.zero, Vector2.one, new Vector2(-200, -200), new Vector2(200, 200)), new Color(0, 0, 0, 0.7f), true);
            cardPanel = Node("Card", cardLayer, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            cardPanel.sizeDelta = new Vector2(940, 600);
            Fill(cardPanel, Gold, true);
            Fill(Node("Fill", cardPanel, Vector2.zero, Vector2.one, new Vector2(3, 3), new Vector2(-3, -3)), new Color(0.07f, 0.08f, 0.065f, 0.99f));
            var layout = cardPanel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(48, 48, 44, 44);
            layout.spacing = 16;
            layout.childControlHeight = layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            cardPanel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            // The fill and border aren't laid out.
            foreach (Transform t in cardPanel) t.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            cardLayer.gameObject.SetActive(false);
        }

        /// <summary>A pop-up card: a small heading, a title, a few lines, and a column of buttons.</summary>
        public void ShowCard(string eyebrow, string title, string[] lines, params (string label, Action action, bool primary)[] buttons)
        {
            // Clear the last card (everything but the panel's fill). Detach first: Destroy waits for the frame's end.
            for (int i = cardPanel.childCount - 1; i >= 1; i--)
            {
                var old = cardPanel.GetChild(i).gameObject;
                old.transform.SetParent(null, false);
                Destroy(old);
            }
            Text Line(string text, Font font, int size, Color color, FontStyle style = FontStyle.Normal)
            {
                var t = Label(Node("Line", cardPanel, Vector2.zero, Vector2.one), font, size, color, TextAnchor.MiddleCenter, style);
                t.verticalOverflow = VerticalWrapMode.Overflow;
                t.text = text;
                return t;
            }
            Line(eyebrow.ToUpperInvariant(), Sans, 24, Gold);
            Line(title, Serif, 50, Ink);
            foreach (var l in lines) Line(l, Serif, 30, Armies.Hex(0xcfcab8), FontStyle.Italic);
            var spacer = Node("Spacer", cardPanel, Vector2.zero, Vector2.one);
            spacer.gameObject.AddComponent<LayoutElement>().preferredHeight = 8;
            cardBack = null;
            foreach (var (label, action, primary) in buttons)
            {
                var b = Button(label, cardPanel, Vector2.zero, Vector2.one, label, action, primary);
                b.gameObject.AddComponent<LayoutElement>().preferredHeight = 104;
                if (label == "Back" || label == "Cancel" || label == "Not now" || label == "Later" || label == "Keep fighting" || label == "Stay")
                    cardBack = action;
            }
            cardLayer.gameObject.SetActive(true);
            cardLayer.SetAsLastSibling();
        }

        public void HideCard()
        {
            if (cardLayer.gameObject.activeSelf) cardClosedAt = Time.unscaledTime;
            cardLayer.gameObject.SetActive(false);
        }

        float cardClosedAt = -10f;
        /// <summary>A card is up, or was just tapped away (that same tap mustn't also land on the board).</summary>
        public bool CardBlocking => CardOpen || Time.unscaledTime - cardClosedAt < 0.3f;

        void BuildToast(RectTransform safe)
        {
            toast = Node("Toast", safe, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-480, 180), new Vector2(480, 300));
            Fill(toast, new Color(0.1f, 0.11f, 0.09f, 0.96f));
            Fill(Node("Rule", toast, Vector2.zero, new Vector2(0, 1), Vector2.zero, new Vector2(5, 0)), Gold);
            toastText = Label(Node("Text", toast, Vector2.zero, Vector2.one, new Vector2(30, 8), new Vector2(-24, -8)), Sans, 28, Ink, TextAnchor.MiddleLeft);
            toastText.resizeTextForBestFit = true;
            toastText.resizeTextMinSize = 18;
            toastText.resizeTextMaxSize = 28;
            toast.gameObject.SetActive(false);
        }

        public void ShowToast(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            toastText.text = text;
            toast.gameObject.SetActive(true);
            toast.SetAsLastSibling();
            toastUntil = Time.unscaledTime + 3.6f;
        }
    }
}
