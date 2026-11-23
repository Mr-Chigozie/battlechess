// Online battles on the board, as the web game's (js/game3d.js with js/online3d.js): the server referees and
// keeps the move list; this board replays it. Your moves are sent as you make them, your rival's arrive and play
// out one at a time once the board is idle, and anything the board can't decide (resignation, time, an agreed
// draw, a battle called off) comes from the server. Leaving keeps the battle open to resume from the War Room.
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ChessWars.Online;
using ChessWars.Rules;
using UnityEngine;

namespace ChessWars.Game
{
    using Reply = Dictionary<string, object>;

    public partial class BattleController
    {
        sealed class OnlineGame
        {
            public string Id, Opponent, ClockType;
            public char Mine;
            public long OpponentId;
            public List<Reply> Moves = new List<Reply>();
            public Reply View;
            public float ReceivedAt;
            public int SeenOffer = -1;
            public double LastTauntAt;
            public bool ResultShown;
            public string Taunt;
        }

        OnlineGame online;
        Server server;
        OnlineUi onlineUi;
        int plyPlayed;   // moves played on this board (yours and your rival's)

        void InitOnline()
        {
            server = Server.Create();
            onlineUi = OnlineUi.Create(server);
            onlineUi.Closed += () => menu.Show(0);
            onlineUi.JoinRequested += JoinOnline;
            menu.OnlinePressed += () => { menu.Hide(); onlineUi.Show(); };
            server.GameUpdate += OnGameView;
            server.Reconnected += () => { if (online != null) Rejoin(); };
            server.GameStarted += id =>
            {
                Sfx.Play("drums");
                // In the menus: straight to it. Mid-battle: just say so.
                if (online == null && (onlineUi.Open || menu.Open))
                {
                    onlineUi.ShowToast("Your challenge was accepted. To battle!");
                    JoinOnline(id);
                }
                else if (online == null || online.Id != id) onlineUi.ShowToast("A new battle is waiting for you in the War Room.");
            };
            server.ChallengeIncoming += c =>
            {
                Sfx.Play("drums");
                if (!onlineUi.RoomOpen || onlineUi.CardOpen)
                {
                    onlineUi.ShowToast($"{c.Str("fromName")} challenges you! Open the War Room to answer.");
                    return;
                }
                long id = c.Long("id");
                string army = Armies.All.TryGetValue(c.Str("fromArmy") ?? "", out var a) ? a.Name : "Their army";
                onlineUi.ShowCard("Challenge", $"{c.Str("fromName")} challenges you!",
                    new[] { $"The {army} want to battle you, and they lead as Ivory.", c.Str("clock") == "daily" ? "Daily · a move a day" : "Live · 10 min + 5 s" },
                    ("Accept", () =>
                    {
                        onlineUi.HideCard();
                        server.Emit("challenge:answer", new Reply { ["id"] = id, ["action"] = "accept" }, r =>
                        {
                            if (r.Has("error")) onlineUi.ShowToast(r.Str("message"));
                            else if (r.Str("gameId") is string game) JoinOnline(game);
                        });
                    }, true),
                    ("Decline", () => { onlineUi.HideCard(); server.Emit("challenge:answer", new Reply { ["id"] = id, ["action"] = "decline" }); }, false),
                    ("Later", onlineUi.HideCard, false));
            };
            ui.TauntPressed += TauntCard;
            // Invite links open the app (see Editor/AndroidLinks.cs): when it starts, or while it's running.
            Application.deepLinkActivated += OpenLink;
        }

        void OpenLink(string url)
        {
            if (string.IsNullOrEmpty(url) || Server.InviteCode(url) == null) return;
            if (online != null && !gameOver)
            {
                onlineUi.ShowToast("Finish this battle first, then open the invite again (or paste it in the War Room).");
                return;
            }
            if (online != null) LeaveOnline();
            menu.Hide();
            ui.Hidden = true;
            onlineUi.OpenLink(url);
        }

        void JoinOnline(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            server.Emit("game:join", new Reply { ["id"] = id }, r =>
            {
                if (r.Has("error")) { onlineUi.ShowToast(r.Str("message")); return; }
                StartOnline(r.Obj("game"));
            });
        }

        // Back after a dropped line, or the boards disagreed: take the server's battle as it stands.
        void Rejoin()
        {
            var id = online?.Id;
            if (id == null) return;
            server.Emit("game:join", new Reply { ["id"] = id }, r =>
            {
                if (r.Has("error") || online == null || online.Id != id) return;
                var view = r.Obj("game");
                var moves = view.List("moves").Cast<Reply>().ToList();
                // Only rebuild the board if it's out of step; otherwise just take the news.
                if (!busy && !SameHistory(moves)) Replay(moves);
                OnGameView(view);
            });
        }

        readonly List<Move> playedMoves = new List<Move>();

        bool SameHistory(List<Reply> moves)
        {
            if (moves.Count < playedMoves.Count) return false;
            for (int i = 0; i < playedMoves.Count; i++)
            {
                var m = moves[i];
                var p = playedMoves[i];
                if (m.Int("fromRow") != p.FromRow || m.Int("fromCol") != p.FromCol || m.Int("toRow") != p.ToRow || m.Int("toCol") != p.ToCol) return false;
            }
            return true;
        }

        void StartOnline(Reply view)
        {
            if (view == null || server.Me == null) return;
            char mine = view.Long("white") == server.Me.Id ? 'w' : 'b';
            online = new OnlineGame
            {
                Id = view.Str("id"), Mine = mine,
                Opponent = mine == 'w' ? view.Str("blackName", "Rival") : view.Str("whiteName", "Rival"),
                OpponentId = mine == 'w' ? view.Long("black") : view.Long("white"),
                ClockType = view.Obj("clock").Str("type", "live"),
                LastTauntAt = view.Obj("taunt").Num("at"),   // old battle cries aren't news
            };
            onlineUi.Hide();
            onlineUi.HideCard();
            menu.Hide();
            string white = Known(view.Str("whiteArmy")), black = Known(view.Str("blackArmy"));
            // The rival takes the "computer's" seat: their homeland is the battlefield, the board faces you, and
            // the board only takes your taps on your turn. (No computer moves: see `online` in AiTurn's callers.)
            Begin(white, black, Rules.Rules.Opponent(mine), aiDepth, false);
            ui.SetOnline(true);
            var moves = view.List("moves").Cast<Reply>().ToList();
            Replay(moves);
            ui.SetTitle("Online battle", $"{view.Str("whiteName")} vs {view.Str("blackName")} · {(online.ClockType == "daily" ? "Daily" : "Live")}");
            ui.Narrate("Online battle", $"You command the {Armies.All[mine == 'w' ? white : black].Name} as {(mine == 'w' ? "Ivory, moving first" : "Onyx")}, against {online.Opponent}.{(moves.Count > 0 ? " The battle resumes." : "")}");
            Sfx.Play("drums");
            OnGameView(view);
        }

        static string Known(string army) => army != null && Armies.All.ContainsKey(army) ? army : "knights";

        // Sets the board to the position after these moves, without playing them out.
        void Replay(List<Reply> moves)
        {
            var p = Rules.Rules.InitialPosition();
            var counts = new Dictionary<string, int> { [Rules.Rules.PositionKey(p)] = 1 };
            var taken = new List<char?>();
            var played = new List<Move>();
            foreach (var m in moves)
            {
                var legal = Match(p, m);
                if (legal == null) { Debug.LogWarning("Online: a stored move doesn't fit the board"); break; }
                var result = Rules.Rules.ApplyMove(p, legal, Promotion(m));
                taken.Add(result.Captured is Piece c ? c.Type : (char?)null);
                string key = Rules.Rules.PositionKey(p);
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
                played.Add(legal);
            }
            Setup(p);
            foreach (var kv in counts) seen[kv.Key] = kv.Value;
            captures.AddRange(taken);
            anyCapture = taken.Any(t => t != null);
            playedMoves.AddRange(played);
            plyPlayed = played.Count;
            lastMove = played.LastOrDefault();
            ShowMarkers(lastMove);
            UpdateFury();
            if (!CheckGameOver(Rules.Rules.PositionKey(p))) UpdateTurn();
        }

        static Move Match(Position p, Reply m) =>
            Rules.Rules.LegalMoves(p, m.Int("fromRow"), m.Int("fromCol")).FirstOrDefault(x => x.ToRow == m.Int("toRow") && x.ToCol == m.Int("toCol"));

        static char Promotion(Reply m) => m.Str("promotion") is string s && s.Length > 0 ? s[0] : 'q';

        // Your move goes to the server as you make it (the board plays it out meanwhile).
        void SendMove(Move move, char promotion, int ply)
        {
            var piece = pos.Board[move.FromRow, move.FromCol];
            bool promoting = piece is Piece pc && pc.Type == 'p' && (move.ToRow == 0 || move.ToRow == 7);
            var payload = new Reply
            {
                ["id"] = online.Id,
                ["ply"] = ply,
                ["move"] = new Reply
                {
                    ["fromRow"] = move.FromRow, ["fromCol"] = move.FromCol, ["toRow"] = move.ToRow, ["toCol"] = move.ToCol,
                    ["promotion"] = promoting ? promotion.ToString() : null,
                },
            };
            string id = online.Id;
            server.Emit("game:move", payload, r =>
            {
                if (!r.Has("error") || online == null || online.Id != id) return;
                onlineUi.ShowToast(r.Str("error") == "finished" ? r.Str("message") : $"{r.Str("message")} Resyncing the board…");
                StartCoroutine(ResyncWhenIdle());
            });
        }

        IEnumerator ResyncWhenIdle()
        {
            while (busy && !gameOver) yield return null;
            playedMoves.Clear();   // forces a rebuild from the server's list
            Rejoin();
        }

        // Your rival's next move, once the board is free.
        void PumpRemote()
        {
            if (online != null && online.Taunt != null && !busy)
            {
                ui.Narrate($"{online.Opponent} says", $"“{online.Taunt}”");
                Sfx.Play("select");
                online.Taunt = null;
            }
            if (online == null || busy || gameOver || plyPlayed >= online.Moves.Count) return;
            var m = online.Moves[plyPlayed];
            var legal = Match(pos, m);
            if (legal == null)
            {
                onlineUi.ShowToast("The boards disagree. Resyncing…");
                playedMoves.Clear();
                Rejoin();
                return;
            }
            StartCoroutine(Play(legal, Promotion(m), true));
        }

        void OnGameView(Reply view)
        {
            if (online == null || view == null || view.Str("id") != online.Id) return;
            online.View = view;
            online.ReceivedAt = Time.unscaledTime;
            online.Moves = view.List("moves").Cast<Reply>().ToList();
            PumpRemote();

            // A battle cry from across the board.
            var taunt = view.Obj("taunt");
            if (taunt != null && taunt.Long("by") != server.Me?.Id && taunt.Num("at") > online.LastTauntAt)
            {
                online.LastTauntAt = taunt.Num("at");
                online.Taunt = taunt.Str("text");   // shown once the board is still, so a move's news doesn't bury it
            }

            // A draw offered.
            string offer = view.Str("drawOffer");
            if (view.Str("status") == "active" && offer != null && offer[0] != online.Mine && online.SeenOffer != online.Moves.Count)
            {
                online.SeenOffer = online.Moves.Count;
                ui.Narrate("Parley", $"{online.Opponent} offers a draw.");
                string id = online.Id;
                onlineUi.ShowCard("Draw offer", $"{online.Opponent} offers a draw", new[] { "Accept and the battle ends level. Decline and fight on." },
                    ("Accept draw", () => { onlineUi.HideCard(); server.Emit("game:draw", new Reply { ["id"] = id, ["action"] = "accept" }, ToastError); }, true),
                    ("Decline", () => { onlineUi.HideCard(); server.Emit("game:draw", new Reply { ["id"] = id, ["action"] = "decline" }, ToastError); }, false));
            }

            // Endings that aren't on the board. (Checkmate and drawn positions are found by playing the last move.)
            string reason = view.Str("reason");
            if (view.Str("status") == "finished" && (reason == "resign" || reason == "timeout" || reason == "agreement" || reason == "aborted") && !gameOver)
            {
                gameOver = busy = true;
                Sfx.InBattle = false;
                string result = view.Str("result");
                bool won = result != null && result.Length == 1 && result[0] == online.Mine;
                var (label, text) = reason switch
                {
                    "resign" => ("Resignation", won ? $"{online.Opponent} has resigned." : "You laid down your arms."),
                    "timeout" => ("Out of time", won ? $"{online.Opponent} ran out of time." : "Your time ran out."),
                    "agreement" => ("Parley", "Both commanders agree to a draw."),
                    _ => ("Called off", "The opening move never came, so the battle was called off. No result is recorded."),
                };
                ui.Narrate(label, text);
                UpdateTurn(reason == "aborted" ? "Called off" : result == "draw" ? "Draw" : won ? "Victory" : "Defeat");
                if (reason != "agreement" && reason != "aborted") Sfx.Play(won ? "fanfare" : "defeat");
                StartCoroutine(OnlineResult(reason == "aborted" ? "aborted" : result == "draw" ? "draw" : won ? "win" : "loss", reason, 1.2f));
            }
        }

        void ToastError(Reply r)
        {
            if (r.Has("error")) onlineUi.ShowToast(r.Str("message"));
        }

        // The board found the end (checkmate or a drawn position).
        void OnlineBoardEnd()
        {
            bool mate = Rules.Rules.AllLegalMoves(pos, pos.Turn).Count == 0 && Rules.Rules.IsInCheck(pos, pos.Turn);
            string outcome = mate ? (Rules.Rules.Opponent(pos.Turn) == online.Mine ? "win" : "loss") : "draw";
            StartCoroutine(OnlineResult(outcome, mate ? "checkmate" : "draw", 2.2f));
        }

        IEnumerator OnlineResult(string outcome, string reason, float delay)
        {
            var game = online;
            if (game == null || game.ResultShown) yield break;
            game.ResultShown = true;
            yield return new WaitForSecondsRealtime(delay);
            if (online != game) yield break;
            string rival = game.Opponent;
            string title = reason == "aborted" ? "Called off" : outcome == "win" ? "Victory" : outcome == "loss" ? "Defeat" : "Draw";
            string line = reason switch
            {
                "resign" => outcome == "win" ? $"{rival} resigned." : "You resigned.",
                "checkmate" => outcome == "win" ? $"You checkmated {rival}." : $"{rival} checkmated you.",
                "timeout" => outcome == "win" ? $"{rival} ran out of time." : "Your time ran out.",
                "agreement" => "You agreed a draw.",
                "aborted" => "No result is recorded.",
                _ => "Neither side could win it.",
            };
            var buttons = new List<(string, System.Action, bool)>();
            bool guest = server.Me != null && server.Me.Guest && reason != "aborted";
            if (guest) buttons.Add(("Keep your record (create account)", () => { onlineUi.HideCard(); LeaveOnline(); onlineUi.ShowUpgrade(); }, true));
            buttons.Add(("War room", () => { onlineUi.HideCard(); LeaveOnline(); }, !guest));
            buttons.Add(("Look at the board", onlineUi.HideCard, false));
            onlineUi.ShowCard("Online battle", title, new[] { line, reason == "aborted" ? "" : "The result is on your record in the War Room." }, buttons.ToArray());
        }

        /// <summary>Back to the War Room. The battle stays open on the server to resume later.</summary>
        void LeaveOnline()
        {
            if (online != null) server.Emit("game:leave", null);
            online = null;
            playedMoves.Clear();
            ui.SetOnline(false);
            onlineUi.HideCard();
            Begin(menu.Army, menu.Enemy, 'b', 2, true);
            onlineUi.Show();
            server.RefreshLobby();
        }

        // Draw, resign or report: the menu's options in an online battle (it can't be paused or restarted).
        void OnlineOptions()
        {
            if (online == null) return;
            string id = online.Id, rival = online.Opponent;
            long rivalId = online.OpponentId;
            var buttons = new List<(string, System.Action, bool)>();
            if (!gameOver)
            {
                buttons.Add(("Offer a draw", () =>
                {
                    onlineUi.HideCard();
                    server.Emit("game:draw", new Reply { ["id"] = id, ["action"] = "offer" }, r =>
                        onlineUi.ShowToast(r.Has("error") ? r.Str("message") : "Draw offered. Waiting for your rival's answer."));
                }, false));
                buttons.Add(("Resign", () => onlineUi.ShowCard("Resign", "Lay down your arms?", new[] { $"{rival} wins and the loss goes on your record." },
                    ("Keep fighting", onlineUi.HideCard, true),
                    ("Resign", () => { onlineUi.HideCard(); server.Emit("game:resign", new Reply { ["id"] = id }, ToastError); }, false)), false));
            }
            buttons.Add(("Report player", () => onlineUi.Report(rivalId, rival, id, onlineUi.HideCard), false));
            buttons.Add(("Back to battle", onlineUi.HideCard, true));
            onlineUi.ShowCard("Online battle", $"vs {rival}",
                new[] { gameOver ? "This battle is over." : "Leaving keeps the battle open. Resume it any time from the War Room." }, buttons.ToArray());
        }

        void TauntCard()
        {
            if (online == null || gameOver) return;
            var army = online.Mine == 'w' ? whiteArmy : blackArmy;
            if (!Armies.Taunts.TryGetValue(army, out var cries)) return;
            string id = online.Id;
            var buttons = cries.Select(text => (text, (System.Action)(() =>
            {
                onlineUi.HideCard();
                server.Emit("game:taunt", new Reply { ["id"] = id, ["text"] = text }, r =>
                {
                    if (r.Has("error")) onlineUi.ShowToast(r.Str("message"));
                    else if (r.Bool("ok")) ui.Narrate("You say", $"“{text}”");
                    else onlineUi.ShowToast("Give it a moment before the next battle cry.");
                });
            }), false)).ToList();
            buttons.Add(("Cancel", onlineUi.HideCard, true));
            onlineUi.ShowCard("Battle cry", "Shout across the board", new string[0], buttons.ToArray());
        }

        // Both clocks, counting down between the server's updates.
        void UpdateClocks()
        {
            var view = online?.View;
            var c = view?.Obj("clock");
            if (c == null) return;
            bool active = view.Str("status") == "active";
            double elapsed = active ? (Time.unscaledTime - online.ReceivedAt) * 1000.0 : 0;
            bool running = c.Bool("running");
            char turn = (c.Str("turn") ?? "w")[0];
            string type = c.Str("type", "live");
            double Left(char color) => (color == 'w' ? c.Num("whiteMs") : c.Num("blackMs")) - (running && turn == color ? elapsed : 0);
            double? firstMove = active && !running && c.Has("deadline") ? c.Num("deadline") - (c.Num("serverNow") + elapsed) : (double?)null;
            string Part(char color, string who)
            {
                bool toMove = active && turn == color;
                double ms = toMove && firstMove.HasValue ? firstMove.Value : Left(color);
                string text = toMove && firstMove.HasValue ? $"{who}: first move {ClockText(ms, "live")}" : $"{who} {ClockText(ms, type)}";
                bool low = toMove && ms < (type == "daily" ? 3600000 : 30000);
                return toMove ? $"<color=#{(low ? "ff8a7a" : "f0d39a")}><b>{text}</b></color>" : $"<color=#9a9c90>{text}</color>";
            }
            ui.SetClocks($"{Part(online.Mine, "You")}     {Part(Rules.Rules.Opponent(online.Mine), online.Opponent)}");
        }

        static string ClockText(double ms, string type)
        {
            double t = System.Math.Max(0, ms);
            if (type == "daily" && t >= 3600000) return $"{(int)(t / 3600000)}h {(int)(t % 3600000 / 60000)}m";
            int s = (int)System.Math.Ceiling(t / 1000);
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
