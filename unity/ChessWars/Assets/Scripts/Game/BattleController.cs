// A skirmish against the computer: tap a unit, tap a square. The rules are ChessWars.Rules (the C# port of
// js/engine.js); everything here is presentation: markers, walking, and the kill sequence, which follows the web
// game's playCapture (js/stage3d.js): the camera swoops in, the victim braces, the attacker strikes in its own
// style, hit-stop and sparks on impact, the victim dies its army's way, the victor steps in, the camera returns.
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChessWars.Rules;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

namespace ChessWars.Game
{
    public class BattleController : MonoBehaviour
    {
        public BoardView board;
        public CameraRig cameraRig;
        public Battlefield battlefield;
        public string whiteArmy = "knights", blackArmy = "undead";
        public char aiColor = 'b';
        [Range(1, 3)] public int aiDepth = 2;
        public bool blood;          // beheadings on blade kills, as the web game's Blood toggle
        public bool cinematics = true;   // the close-up kill camera, as the web game's Cinematics toggle

        sealed class Combo
        {
            public string Title, Tags;
            public int Level;
        }

        Position pos;
        readonly UnitView[,] units = new UnitView[8, 8];
        readonly Dictionary<string, int> seen = new Dictionary<string, int>();
        readonly Dictionary<char, int> streak = new Dictionary<char, int> { ['w'] = 0, ['b'] = 0 };
        Transform unitRoot;
        Vector2Int? selected;
        List<Move> targets = new List<Move>();
        Move lastMove;
        bool lastWasCapture, anyCapture;
        bool busy, aiThinking, gameOver;
        float lastEmptyTap = -1f, lastFury;
        string furyNews, furyNewsText;
        Fx fx;
        KillHud hud;
        KillCam killCam;
        BattleHud ui;
        readonly List<UnitView> roster = new List<UnitView>();        // every unit alive, for the badges
        readonly List<char?> captures = new List<char?>();            // per move: the type taken, if any
        MainMenu menu;
        bool attract;                                                  // the title-screen backdrop
        Theme builtTheme;

        static readonly string[] Ranks = { "", "Recruit", "Veteran", "Warlord" };
        static readonly (float min, string name)[] FuryNames = { (0.75f, "Inferno"), (0.45f, "Burning"), (0.2f, "Clash"), (0f, "Calm") };

        static bool LoadSetting(string key, bool fallback) => PlayerPrefs.GetInt("cw." + key, fallback ? 1 : 0) == 1;

        void Start()
        {
            Application.targetFrameRate = 60;
            // Glow and colour grading cost fill rate: off on the Low tier for cheap phones.
            var camData = Camera.main.GetComponent<UniversalAdditionalCameraData>();
            if (camData != null) camData.renderPostProcessing = QualitySettings.GetQualityLevel() > 0;
            // Ink outlines cost a second pass per model: off on the Low tier.
            Shader.SetGlobalFloat("_CW_OutlineScale", QualitySettings.GetQualityLevel() > 0 ? 1f : 0f);
            fx = Fx.Create(battlefield.particle, transform);
            hud = KillHud.Create();
            killCam = KillCam.Create(cameraRig);
            ui = BattleHud.Create(hud);
            ui.Units = () => roster;
            cinematics = LoadSetting("cinematics", true);
            blood = LoadSetting("blood", false);
            ui.SetSetting("cinematics", cinematics);
            ui.SetSetting("blood", blood);
            ui.SetSetting("badges", LoadSetting("badges", true));
            ui.SettingChanged += (key, on) =>
            {
                PlayerPrefs.SetInt("cw." + key, on ? 1 : 0);
                if (key == "cinematics") cinematics = on;
                if (key == "blood") blood = on;
            };
            ui.ViewPressed += () => cameraRig.viewColor = cameraRig.viewColor == 'w' ? 'b' : 'w';
            ui.NewGamePressed += NewGame;
            ui.MainMenuPressed += ShowTitle;
            menu = MainMenu.Create();
            menu.Battle += (mine, theirs, side, rank) =>
            {
                // Ivory moves first; the computer takes the other side.
                Begin(side == 'w' ? mine : theirs, side == 'w' ? theirs : mine, side == 'w' ? 'b' : 'w', rank, false);
            };
            ShowTitle();
        }

        // The title screen: the player's last matchup stands ready on its battlefield while the camera circles.
        void ShowTitle()
        {
            Begin(menu.Army, menu.Enemy, 'b', 2, true);
            menu.Show(0);
        }

        /// <summary>Set up a battle. The world is the computer's army's homeland, as in the web game's story mode.
        /// `attract` is the title-screen backdrop: no turns, no HUD, the camera circling.</summary>
        void Begin(string white, string black, char computer, int depth, bool attract)
        {
            whiteArmy = white;
            blackArmy = black;
            aiColor = computer;
            aiDepth = depth;
            this.attract = attract;
            var theme = Themes.For(computer == 'b' ? black : white);
            if (theme != builtTheme)
            {
                battlefield.Build(theme);
                builtTheme = theme;
            }
            board.Build(theme, computer == 'b' ? 'w' : 'b');
            ui.Hidden = attract;
            cameraRig.Spin = attract ? 5f : 0f;
            cameraRig.ResetView();
            NewGame();
        }

        void Track(UnitView unit)
        {
            roster.RemoveAll(u => u == null);
            roster.Add(unit);
        }

        // ---------------------------------------------------------------- HUD

        void UpdateTurn(string over = null)
        {
            bool check = !gameOver && Rules.Rules.IsInCheck(pos, pos.Turn);
            string text = over
                ?? (aiThinking ? $"{SideName(pos.Turn)} is plotting…" : $"{SideName(pos.Turn)} to move{(check ? " — check!" : "")}");
            ui.SetTurn(text, aiThinking, check);
        }

        // Battle fury, as the web game's tension(): climbs with material lost, recent bloodshed, checks and streaks.
        void UpdateFury()
        {
            float lost = 0;
            int recent = 0;
            for (int i = 0; i < captures.Count; i++)
                if (captures[i] is char t)
                {
                    lost += Armies.Value[t];
                    if (i >= captures.Count - 6) recent++;
                }
            float check = Rules.Rules.IsInCheck(pos, pos.Turn) ? 0.2f : 0f;
            float fury = Mathf.Min(1f, lost / 40f + recent * 0.07f + check + Mathf.Max(streak['w'], streak['b']) * 0.06f);
            ui.SetFury(fury, FuryNames.First(f => fury >= f.min).name);
            // Announced once the move's own narration has had its moment.
            if (fury >= 0.75f && lastFury < 0.75f) { furyNews = "Inferno"; furyNewsText = "The whole field is ablaze. Only one army will walk out of this fire."; }
            else if (fury >= 0.45f && lastFury < 0.45f) { furyNews = "The board burns"; furyNewsText = "Fire spreads across the battlefield. This is war."; }
            lastFury = fury;
        }

        public void NewGame()
        {
            StopAllCoroutines();
            Time.timeScale = 1f;
            busy = aiThinking = gameOver = false;
            if (killCam.Active || hud.Cinematic) { StartCoroutine(killCam.Exit()); hud.SetCinematic(false); }
            if (unitRoot != null) Destroy(unitRoot.gameObject);
            unitRoot = new GameObject("Units").transform;
            pos = Rules.Rules.InitialPosition();
            seen.Clear();
            seen[Rules.Rules.PositionKey(pos)] = 1;
            streak['w'] = streak['b'] = 0;
            lastMove = null;
            lastWasCapture = anyCapture = false;
            roster.Clear();
            captures.Clear();
            lastFury = 0;
            furyNews = null;
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                {
                    units[row, col] = pos.Board[row, col] is Piece p
                        ? UnitView.Spawn(p.Color == 'w' ? whiteArmy : blackArmy, p.Type, p.Color, row, col, unitRoot)
                        : null;
                    if (units[row, col] != null) roster.Add(units[row, col]);
                }
            cameraRig.viewColor = aiColor == 'w' ? 'b' : 'w';
            cameraRig.Fit();
            selected = null;
            targets.Clear();
            board.ClearMarkers();
            ui.SetTitle("Skirmish", $"{ArmyName('w')} vs {ArmyName('b')} · {Ranks[aiDepth]}");
            ui.Narrate("The opening", $"{SideName('w')} faces {SideName('b')} on {Themes.For(aiColor == 'b' ? blackArmy : whiteArmy).Name}. Tap a unit to give orders. Drag to look around, pinch to zoom.");
            UpdateTurn();
            UpdateFury();
            if (!attract && pos.Turn == aiColor) StartCoroutine(AiTurn());
        }

        string ArmyName(char color) => Armies.All[color == 'w' ? whiteArmy : blackArmy].Name;
        // Mirror matches name the sides by colour, as the web game does.
        string SideName(char color) => whiteArmy == blackArmy ? (color == 'w' ? "Ivory" : "Onyx") : ArmyName(color);
        static string UnitName(UnitView u) => Armies.All[u.Army].Units[u.Type];

        IEnumerable<UnitView> AllUnits()
        {
            foreach (var u in units) if (u != null) yield return u;
        }

        void Update()
        {
            if (attract || menu.Open) return;
            // The phone's back button opens (or closes) the battle menu.
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame && !killCam.Active) ui.ToggleMenu();
            // Act on release, and only for a tap: a press that turned into a drag was moving the camera.
            var pointer = Pointer.current;
            if (pointer == null || !pointer.press.wasReleasedThisFrame || cameraRig.PressWasDrag || killCam.Active) return;
            if (ui.Blocks(pointer.position.ReadValue())) return;   // a HUD button, or the menu
            var square = PickSquare(pointer.position.ReadValue());
            if (square == null)
            {
                if (Time.unscaledTime - lastEmptyTap < 0.35f) cameraRig.ResetView();   // double-tap off the board
                lastEmptyTap = Time.unscaledTime;
                return;
            }
            if (busy || pos.Turn == aiColor) return;
            OnSquare(square.Value.y, square.Value.x);
        }

        // A tap on a unit's body picks its square; otherwise the board square under the finger.
        Vector2Int? PickSquare(Vector2 screen)
        {
            var cam = Camera.main;
            if (Physics.Raycast(cam.ScreenPointToRay(screen), out var hit, 100f) && hit.collider.GetComponentInParent<UnitView>() is UnitView unit)
                return new Vector2Int(unit.Col, unit.Row);
            return BoardView.SquareAt(cam, screen);
        }

        void OnSquare(int row, int col)
        {
            var move = targets.FirstOrDefault(m => m.ToRow == row && m.ToCol == col);
            if (move != null)
            {
                StartCoroutine(Play(move));
                return;
            }
            if (pos.Board[row, col] is Piece p && p.Color == pos.Turn)
            {
                selected = new Vector2Int(col, row);
                targets = Rules.Rules.LegalMoves(pos, row, col);
                string unit = Armies.All[p.Color == 'w' ? whiteArmy : blackArmy].Units[p.Type];
                ui.Narrate($"{SideName(p.Color)} {unit}", targets.Count > 0
                    ? $"Moves like a {Armies.Pieces[p.Type].ToLowerInvariant()}. {targets.Count} order{(targets.Count == 1 ? "" : "s")} available."
                    : $"The {unit} has nowhere to go.");
            }
            else
            {
                selected = null;
                targets.Clear();
            }
            ShowMarkers(null);
        }

        void ShowMarkers(Move last)
        {
            board.ClearMarkers();
            if (last != null)
            {
                board.Mark(last.FromRow, last.FromCol, board.lastMoveMarker, 0.98f, 0.002f);
                board.Mark(last.ToRow, last.ToCol, board.lastMoveMarker, 0.98f, 0.002f);
            }
            if (selected is Vector2Int s) board.Mark(s.y, s.x, board.selectMarker, 0.9f);
            foreach (var m in targets)
            {
                bool capture = pos.Board[m.ToRow, m.ToCol] != null || m.EnPassant;
                board.Mark(m.ToRow, m.ToCol, capture ? board.captureMarker : board.moveMarker, capture ? 0.9f : 0.3f);
            }
        }

        IEnumerator Play(Move move, char promotion = 'q')
        {
            busy = true;
            selected = null;
            targets.Clear();
            board.ClearMarkers();
            var attacker = units[move.FromRow, move.FromCol];
            int victimRow = move.EnPassant ? move.FromRow : move.ToRow;
            var victim = units[victimRow, move.ToCol];
            var result = Rules.Rules.ApplyMove(pos, move, promotion);
            units[move.FromRow, move.FromCol] = null;
            units[victimRow, move.ToCol] = null;
            var dest = Armies.SquareToWorld(move.ToRow, move.ToCol);
            bool check = Rules.Rules.IsInCheck(pos, pos.Turn);
            captures.Add(victim != null ? victim.Type : (char?)null);

            if (victim != null)
            {
                var combo = ComboFor(attacker, victim, victimRow, move.ToCol, check);
                yield return Capture(attacker, victim, dest, combo);
            }
            else
            {
                streak[attacker.Color] = 0;
                yield return attacker.Travel(dest, attacker.Type == 'n' ? 1.1f : 0f);
                attacker.FaceHome();
            }
            attacker.Row = move.ToRow;
            attacker.Col = move.ToCol;
            units[move.ToRow, move.ToCol] = attacker;
            lastMove = move;
            lastWasCapture = victim != null;

            if (move.Castle != Castle.None)
            {
                int rookFrom = move.Castle == Castle.King ? 7 : 0, rookTo = move.Castle == Castle.King ? 5 : 3;
                var rook = units[move.FromRow, rookFrom];
                units[move.FromRow, rookFrom] = null;
                yield return rook.Travel(Armies.SquareToWorld(move.FromRow, rookTo), 0.8f);
                rook.Col = rookTo;
                units[move.FromRow, rookTo] = rook;
                rook.FaceHome();
            }
            bool promoted = result.MovingPiece.Type == 'p' && (move.ToRow == 0 || move.ToRow == 7);
            if (promoted) yield return Promote(attacker, promotion);

            string key = Rules.Rules.PositionKey(pos);
            seen[key] = seen.TryGetValue(key, out int n) ? n + 1 : 1;
            ShowMarkers(move);
            busy = false;
            UpdateFury();
            if (CheckGameOver(key)) yield break;

            // The narrator reports the move (captures were already told as they happened), as the web game does.
            var army = Armies.All[attacker.Army];
            string to = Rules.Rules.Algebraic(move.ToRow, move.ToCol);
            if (check) ui.Narrate("Check", $"The {SideName(pos.Turn)} {KingName(pos.Turn)} is under attack!");
            else if (promoted) ui.Narrate("A new champion", $"The {army.Units['p']} rises as a {army.Units[promotion]} on {to}.");
            else if (move.Castle != Castle.None) ui.Narrate("The line holds", $"The {army.Units['k']} retreats behind the {army.Units['r']}.");
            else if (victim == null) ui.Narrate("Orders carried out", $"{SideName(attacker.Color)} {army.Units[attacker.Type]} advances to {to}.");
            if (furyNews != null)
            {
                StartCoroutine(NarrateLater(2.5f, furyNews, furyNewsText));
                furyNews = null;
            }
            if (pos.Turn == aiColor) yield return AiTurn();
        }

        string KingName(char color) => Armies.All[color == 'w' ? whiteArmy : blackArmy].Units['k'];

        IEnumerator NarrateLater(float seconds, string label, string text)
        {
            yield return new WaitForSeconds(seconds);
            ui.Narrate(label, text);
        }

        // ---------------------------------------------------------------- the kill sequence

        IEnumerator Capture(UnitView attacker, UnitView victim, Vector3 dest, Combo combo)
        {
            var A = attacker.transform.position;
            var V = victim.transform.position;
            var dir = V - A;
            dir.y = 0;
            dir.Normalize();
            string style = attacker.Style;
            var color = Armies.All[attacker.Army].Magic;
            bool behead = blood && style == "strike" && victim.HasBehead;
            bool cine = cinematics;
            string who = $"{SideName(attacker.Color)} {UnitName(attacker)}  vs  {SideName(victim.Color)} {UnitName(victim)}";
            string line = $"The {UnitName(attacker)} {Armies.KillVerb(style)} the {UnitName(victim)}!";
            int level = combo?.Level ?? 0;

            if (cine)
            {
                hud.SetCinematic(true);
                hud.ShowCaption(who, "");
                yield return killCam.Enter(A, V, Mathf.Max(attacker.Height, victim.Height), behead, AllUnits());
            }
            StartCoroutine(victim.TurnTowards(A, 0.3f));
            yield return attacker.TurnTowards(V, 0.3f);

            Coroutine finishing = null;   // anything still running after impact (a beam) that must end first
            switch (style)
            {
                case "strike":
                    yield return attacker.Travel(V - dir * 0.62f);
                    StartCoroutine(attacker.StrikeIn(0.46f));
                    yield return new WaitForSeconds(0.34f);
                    yield return attacker.Nudge(dir, 0.14f, 0.12f, true);
                    break;
                case "thrust":
                    yield return attacker.Travel(V - dir * 0.72f);
                    StartCoroutine(attacker.StrikeIn(0.52f));
                    yield return new WaitForSeconds(0.2f);
                    yield return attacker.Nudge(dir, -0.12f, 0.22f);
                    yield return attacker.Nudge(dir, 0.34f, 0.1f, true);
                    break;
                case "charge":
                {
                    // Back up a step, then charge home.
                    yield return attacker.Nudge(dir, -0.25f, 0.35f);
                    float run = 0.2f + Vector3.Distance(A, V) * 0.16f;
                    StartCoroutine(attacker.StrikeIn(run));
                    yield return attacker.Travel(V - dir * 0.45f, seconds: run, accelerate: true, walk: false, turn: false);
                    break;
                }
                case "slam":
                    // Leap high and come down on top of the victim.
                    StartCoroutine(attacker.StrikeIn(1.0f));
                    yield return new WaitForSeconds(0.3f);
                    yield return attacker.Travel(V, arc: 1.7f, seconds: 0.7f, walk: false, turn: false);
                    break;
                case "shoot":
                {
                    bool heavy = Armies.HeavyGun(attacker.Army, attacker.Type);
                    attacker.Play("Shoot");
                    // The sniper has to get down on his belly first.
                    yield return new WaitForSeconds(0.2f + (attacker.Army == "marines" && attacker.Type == 'b' ? 0.6f : 0f));
                    int shots = heavy ? 1 : 4;
                    for (int i = 0; i < shots; i++)
                    {
                        var tip = attacker.Tip;
                        fx.Burst(tip, Armies.Hex(0xffd27a), 8, 1.4f, 0.7f, 0.18f);
                        var flight = StartCoroutine(fx.Projectile(tip, victim.Center, heavy ? Armies.Hex(0xffa23a) : color, !heavy));
                        StartCoroutine(attacker.Nudge(dir, heavy ? -0.15f : -0.03f, 0.06f));
                        if (i == shots - 1) yield return flight;
                        else yield return new WaitForSeconds(0.1f);
                    }
                    break;
                }
                case "spell":
                    attacker.Play("Shoot");
                    yield return new WaitForSeconds(0.35f);
                    for (int i = 0; i < 3; i++)
                    {
                        fx.Burst(attacker.Tip, color, 8, 0.8f, 0.8f, 0.3f);
                        yield return new WaitForSeconds(0.12f);
                    }
                    yield return fx.Projectile(attacker.Tip, victim.Center, color, false);
                    break;
                case "beam":
                    attacker.Play("Shoot");
                    for (int i = 0; i < 3; i++)
                    {
                        fx.Burst(attacker.Tip, color, 8, 0.8f, 0.8f, 0.3f);
                        yield return new WaitForSeconds(0.12f);
                    }
                    finishing = StartCoroutine(fx.Beam(attacker.Tip, victim.Center, color, 0.9f));
                    yield return new WaitForSeconds(0.35f);
                    break;
                case "lightning":
                    attacker.Play("Shoot");
                    yield return new WaitForSeconds(0.4f);
                    hud.Flash(0.5f);
                    StartCoroutine(fx.Lightning(attacker.Tip, victim.Center, color, 4));
                    yield return fx.Lightning(V + Vector3.up * 7f, victim.Center, color, 5);
                    break;
            }

            // Impact.
            float holdUntil = 0f;
            var hit = victim.Center;
            fx.Hitstop(0.09f + level * 0.03f, level > 0 ? 0.28f : 1f, level > 0 ? 0.6f + level * 0.25f : 0f);
            if (level > 0)
            {
                fx.Burst(hit, Color.white, 20 + level * 10, 4f, 1.2f, 0.8f);
                fx.Shockwave(victim.transform.position, color);
            }
            fx.Burst(hit, color, 30, 3.2f);
            fx.Burst(hit, Color.white, 10, 2.2f, 0.8f, 0.4f);
            Fx.Shake((style == "slam" || style == "lightning" ? 0.4f : 0.18f) + level * 0.08f);
            if (style == "spell" || style == "beam" || style == "lightning" || style == "slam" || level > 1) hud.Flash(0.3f);
            if (style == "slam") fx.Shockwave(victim.transform.position, color);
            StartCoroutine(victim.FlashWhite());
            if (killCam.Active) killCam.PunchIn(behead ? 0.04f : 0.12f + level * 0.03f);
            hud.ShowCaption(who, line);
            ui.Narrate(combo != null ? combo.Title : "Battle", line);
            if (combo != null) hud.ShowCombo(combo.Title, combo.Tags, combo.Level);
            if (blood) fx.Burst(hit, Armies.All[victim.Army].Blood, 16, 2.4f, 0.9f, 0.6f);
            // Reel from the blow, then fall.
            yield return victim.Nudge(dir, 0.16f + (style == "slam" || style == "charge" ? 0.12f : 0f), 0.16f);
            var dying = StartCoroutine(victim.Die(behead, level));

            // Let the death play out before the victor steps in.
            holdUntil = Time.time + (cine ? 0.9f : 0.35f);
            yield return dying;
            if (finishing != null) yield return finishing;
            while (Time.time < holdUntil) yield return null;
            if (Vector3.Distance(attacker.transform.position, dest) > 0.05f)
                yield return attacker.Travel(dest, seconds: 0f);
            attacker.transform.position = dest;
            yield return attacker.TurnTowards(dest + (attacker.Color == 'w' ? Vector3.back : Vector3.forward), 0.22f);
            attacker.FaceHome();
            attacker.Play("Idle");
            if (cine)
            {
                hud.SetCinematic(false);
                yield return killCam.Exit();
            }
            else hud.HideCaption();
        }

        // The web game's combos: kill streaks, plus First Blood, Giant Slayer, Revenge and With check.
        Combo ComboFor(UnitView mover, UnitView victim, int victimRow, int victimCol, bool check)
        {
            streak[mover.Color] += 1;
            var tags = new List<string>();
            if (!anyCapture) tags.Add("First Blood");
            anyCapture = true;
            if (Armies.Value[victim.Type] - Armies.Value[mover.Type] >= 4) tags.Add("Giant Slayer");
            if (lastWasCapture && lastMove != null && lastMove.ToRow == victimRow && lastMove.ToCol == victimCol) tags.Add("Revenge");
            if (check) tags.Add("With check");
            int count = streak[mover.Color];
            string title;
            if (count >= 2) title = Armies.StreakNames[Mathf.Min(count, Armies.StreakNames.Length - 1)];
            else if (tags.Count > 0) { title = tags[0]; tags.RemoveAt(0); }
            else return null;
            if (count >= 2) tags.Insert(0, $"{count} kills in a row");
            return new Combo { Title = title, Tags = string.Join("  ·  ", tags), Level = count >= 2 ? count - 1 : 1 };
        }

        // A beam from the sky, a burst, and the promoted unit grows in its place.
        IEnumerator Promote(UnitView pawn, char promotion)
        {
            var color = Armies.All[pawn.Army].Magic;
            var at = pawn.transform.position;
            var ray = StartCoroutine(fx.Beam(at + Vector3.up * 8f, at + Vector3.up * 0.1f, color, 1.1f));
            yield return new WaitForSeconds(0.45f);
            fx.Burst(pawn.Center, color, 40, 2.5f);
            hud.Flash(0.35f);
            Destroy(pawn.gameObject);
            var queen = UnitView.Spawn(pawn.Army, promotion, pawn.Color, pawn.Row, pawn.Col, unitRoot);
            units[pawn.Row, pawn.Col] = queen;
            Track(queen);
            var scale = queen.transform.localScale;
            for (float t = 0; t < 1; t += Time.deltaTime / 0.5f)
            {
                // Ease out with a little overshoot.
                float k = 1 + 2.7f * Mathf.Pow(t - 1, 3) + 1.7f * Mathf.Pow(t - 1, 2);
                queen.transform.localScale = scale * Mathf.Max(0.01f, k);
                yield return null;
            }
            queen.transform.localScale = scale;
            yield return ray;
        }

        // The beaten king falls.
        IEnumerator Checkmate(char loser)
        {
            var king = AllUnits().FirstOrDefault(u => u.Type == 'k' && u.Color == loser);
            if (king == null) yield break;
            Fx.Shake(0.25f);
            king.Play("Death", 0.1f);
            yield return new WaitForSeconds(1.2f);
            fx.Dust(king.transform.position + Vector3.up * 0.1f, 14);
            hud.Flash(0.4f);
        }

        bool CheckGameOver(string key)
        {
            bool inCheck = Rules.Rules.IsInCheck(pos, pos.Turn);
            string enemyKing = $"{SideName(pos.Turn)} {KingName(pos.Turn)}";
            if (Rules.Rules.AllLegalMoves(pos, pos.Turn).Count == 0)
            {
                gameOver = busy = true;
                if (inCheck)
                {
                    var winner = Rules.Rules.Opponent(pos.Turn);
                    ui.Narrate("Checkmate", $"The {enemyKing} is trapped. {SideName(winner)} claims the board.");
                    UpdateTurn($"{SideName(winner)} wins");
                    StartCoroutine(Checkmate(pos.Turn));
                }
                else
                {
                    ui.Narrate("Draw", "Stalemate. Neither side can make a legal move.");
                    UpdateTurn("Draw");
                }
                return true;
            }
            string draw = seen[key] >= 3 ? "The same position has come up three times. The battle is a draw."
                : pos.Halfmove >= 100 ? "Fifty moves with no capture or pawn move. The battle is a draw."
                : Rules.Rules.HasInsufficientMaterial(pos) ? "Neither side has enough left to force checkmate." : null;
            if (draw != null)
            {
                gameOver = busy = true;
                ui.Narrate("Draw", draw);
                UpdateTurn("Draw");
                return true;
            }
            UpdateTurn();
            return false;
        }

        // The search runs off the main thread so the board keeps animating on slow phones.
        IEnumerator AiTurn()
        {
            busy = aiThinking = true;
            UpdateTurn();
            var snapshot = pos.Clone();
            var counts = new Dictionary<string, int>(seen);
            int depth = aiDepth;
            var seed = Random.Range(int.MinValue, int.MaxValue);
            var search = Task.Run(() => Ai.ChooseMove(snapshot, depth, counts, new System.Random(seed).NextDouble));
            yield return new WaitForSeconds(0.4f);
            while (!search.IsCompleted) yield return null;
            busy = aiThinking = false;
            UpdateTurn();
            if (search.Result != null) yield return Play(search.Result);
        }
    }
}
