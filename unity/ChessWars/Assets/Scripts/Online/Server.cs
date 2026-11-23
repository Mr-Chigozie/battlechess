// The Chess Wars server (server/ in this repo, live on Render), as the web game's js/online3d.js talks to it:
// accounts over HTTPS (sign in, enlist, guest, upgrade, delete), then a live Socket.IO line for the War Room,
// challenges, invite links and battles. The server referees every move and runs the clocks.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ChessWars.Online
{
    using Reply = Dictionary<string, object>;

    public sealed class User
    {
        public long Id;
        public string Name, Army;
        public bool Guest;
        public int Wins, Losses, Draws;

        public static User From(Reply o) => o == null ? null : new User
        {
            Id = o.Long("id"), Name = o.Str("name", ""), Army = o.Str("army", "knights"), Guest = o.Bool("guest"),
            Wins = o.Int("wins"), Losses = o.Int("losses"), Draws = o.Int("draws"),
        };

        public string Record => $"{Wins}W · {Losses}L · {Draws}D";
    }

    public sealed class Server : MonoBehaviour
    {
        /// <summary>The live server. Development builds can be pointed elsewhere for testing:
        /// adb shell am start -n com.chesswars.game/com.unity3d.player.UnityPlayerGameActivity -e server http://10.0.2.2:8090</summary>
        public static string Address { get; private set; } = "https://chesswars-server.onrender.com";
        const string TokenKey = "cw.token";

        public static Server I { get; private set; }

        public User Me { get; private set; }
        public Reply Lobby { get; private set; }
        public bool SignedIn => Me != null;
        public bool Connected => socket != null && socket.IsConnected;

        /// <summary>The account or the War Room changed (sign in/out, a lobby update).</summary>
        public event Action Changed;
        public event Action<Reply> GameUpdate;               // a battle's latest state
        public event Action<string> GameStarted;             // a battle we're in has begun (a challenge was accepted)
        public event Action<Reply> ChallengeIncoming;
        public event Action<string> Toast;                   // something to tell the player in passing
        public event Action Reconnected;                     // the line came back: pick up the battle on screen

        string token;
        SocketIo socket;
        bool everConnected;

        public static Server Create()
        {
            var go = new GameObject("Server");
            DontDestroyOnLoad(go);
            I = go.AddComponent<Server>();
            return I;
        }

        /// <summary>A string extra from the Intent that launched the app (development hooks), or null.</summary>
        public static string IntentExtra(string key)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                return intent.Call<string>("getStringExtra", key);
            }
            catch (Exception) { return null; }
#else
            return null;
#endif
        }

        void Start()
        {
            if (Debug.isDebugBuild && IntentExtra("server") is string other && other.StartsWith("http")) Address = other.TrimEnd('/');
            token = PlayerPrefs.GetString(TokenKey, "");
            if (string.IsNullOrEmpty(token)) return;
            // Signed in from last time: check the token is still good, then go live.
            Http("GET", "/api/me", null, (r, err, status) =>
            {
                if (err == null) { Me = User.From(r.Obj("user")); Connect(); Changed?.Invoke(); }
                else if (status == 401) SignOutLocal(null);
            });
        }

        void Update() => socket?.Pump();

        void OnDestroy()
        {
            socket?.Dispose();
            if (I == this) I = null;
        }

        // ---------------------------------------------------------------- HTTPS

        /// <summary>A JSON request to the server. `done` gets (reply, null, status) or (reply, error message, status).</summary>
        public void Http(string method, string path, Reply body, Action<Reply, string, long> done) => StartCoroutine(HttpRoutine(method, path, body, done));

        IEnumerator HttpRoutine(string method, string path, Reply body, Action<Reply, string, long> done)
        {
            using var req = new UnityWebRequest(Address + path, method);
            if (body != null) req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(Json.Write(body)));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(token)) req.SetRequestHeader("Authorization", "Bearer " + token);
            req.timeout = 70;   // a free server asleep takes up to a minute to wake
            yield return req.SendWebRequest();
            Reply reply = null;
            try { reply = Json.Parse(req.downloadHandler.text) as Reply; } catch (Exception) { }
            reply ??= new Reply();
            if (req.result == UnityWebRequest.Result.ConnectionError || req.result == UnityWebRequest.Result.DataProcessingError)
                done(reply, "Can't reach the server. Check your connection.", 0);
            else if (req.responseCode >= 400)
                done(reply, reply.Str("message", "Something went wrong. Try again."), req.responseCode);
            else done(reply, null, req.responseCode);
        }

        // ---------------------------------------------------------------- accounts

        /// <summary>mode: login (email, password), register (name, email, password, army, code), guest (name, army).</summary>
        public void Authenticate(string mode, Reply fields, Action<string, string> done)
        {
            Http("POST", "/api/auth/" + mode, fields, (r, err, status) =>
            {
                if (err != null) { done(err, r.Str("error")); return; }
                token = r.Str("token");
                PlayerPrefs.SetString(TokenKey, token);
                PlayerPrefs.Save();
                Me = User.From(r.Obj("user"));
                Connect();
                Changed?.Invoke();
                done(null, null);
            });
        }

        /// <summary>A guest keeps their record by adding an email and password.</summary>
        public void Upgrade(string name, string email, string password, Action<string> done) =>
            Http("POST", "/api/auth/upgrade", new Reply { ["name"] = name, ["email"] = email, ["password"] = password }, (r, err, _) =>
            {
                if (err == null) { Me = User.From(r.Obj("user")); Changed?.Invoke(); }
                done(err);
            });

        public void Forgot(string email, Action<string, bool> done) =>
            Http("POST", "/api/auth/forgot", new Reply { ["email"] = email }, (r, err, _) => done(err ?? r.Str("message", "Check your email."), err == null));

        public void ChangeArmy(string army, Action<string> done) =>
            Http("PATCH", "/api/me", new Reply { ["army"] = army }, (r, err, _) =>
            {
                if (err == null) { Me = User.From(r.Obj("user")); Changed?.Invoke(); Emit("lobby:get", null, OnLobbyReply); }
                done(err);
            });

        public void SignOut()
        {
            if (!string.IsNullOrEmpty(token)) Http("POST", "/api/auth/logout", null, (_, __, ___) => { });
            SignOutLocal(null);
        }

        public void DeleteAccount(string password, Action<string> done) =>
            Http("DELETE", "/api/me", new Reply { ["password"] = password ?? "" }, (r, err, _) =>
            {
                if (err == null) SignOutLocal(null);
                done(err);
            });

        void SignOutLocal(string message)
        {
            socket?.Dispose();
            socket = null;
            token = null;
            Me = null;
            Lobby = null;
            PlayerPrefs.DeleteKey(TokenKey);
            PlayerPrefs.Save();
            Changed?.Invoke();
            if (message != null) Toast?.Invoke(message);
        }

        // ---------------------------------------------------------------- the live line

        void Connect()
        {
            socket?.Dispose();
            everConnected = false;
            socket = new SocketIo(Address, token);
            socket.Connected += () =>
            {
                Emit("lobby:get", null, OnLobbyReply);
                if (everConnected) Reconnected?.Invoke();
                everConnected = true;
                Changed?.Invoke();
            };
            socket.Disconnected += () => Changed?.Invoke();
            socket.Refused += reason =>
            {
                if (reason == "unauthorized") SignOutLocal("Please sign in again.");
            };
            socket.Event += OnEvent;
        }

        void OnLobbyReply(Reply r)
        {
            if (r.Has("error")) return;
            Lobby = r;
            if (r.Obj("me") is Reply me) Me = User.From(me);
            Changed?.Invoke();
        }

        void OnEvent(string name, Reply data)
        {
            switch (name)
            {
                case "lobby": OnLobbyReply(data); break;
                case "game": GameUpdate?.Invoke(data); break;
                case "game:started": GameStarted?.Invoke(data.Str("gameId")); break;
                case "challenge:incoming": ChallengeIncoming?.Invoke(data); break;
                case "challenge:declined": Toast?.Invoke($"{data.Str("byName", "They")} declined your challenge."); break;
            }
        }

        /// <summary>Sends an event over the live line; `reply` gets { ok, ... } or { error, message }.</summary>
        public void Emit(string name, Reply payload, Action<Reply> reply = null)
        {
            if (socket == null)
            {
                reply?.Invoke(SocketIo.Error("offline", "Sign in to play online."));
                return;
            }
            socket.Emit(name, payload, reply);
        }

        public void RefreshLobby() => Emit("lobby:get", null, OnLobbyReply);

        /// <summary>The invite code in a code or a pasted link (…/i/CODE or …?invite=CODE), or null.</summary>
        public static string InviteCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            int at = text.LastIndexOf("/i/", StringComparison.OrdinalIgnoreCase);
            if (at >= 0) text = text.Substring(at + 3);
            at = text.IndexOf("invite=", StringComparison.OrdinalIgnoreCase);
            if (at >= 0) text = text.Substring(at + 7);
            var code = new StringBuilder();
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) code.Append(char.ToUpperInvariant(c));
                else if (code.Length > 0) break;
            }
            return code.Length >= 4 && code.Length <= 16 ? code.ToString() : null;
        }
    }
}
