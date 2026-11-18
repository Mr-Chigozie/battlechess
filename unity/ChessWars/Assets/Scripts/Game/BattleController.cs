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
        bool busy;
        string status = "";
        float lastEmptyTap = -1f;
        Fx fx;
        KillHud hud;
        KillCam killCam;

        void Start()
        {
            Application.targetFrameRate = 60;
            // The battle is fought in the opponent's world (as in the web game's story mode).
            var theme = Themes.For(aiColor == 'b' ? blackArmy : whiteArmy);
            battlefield.Build(theme);
            board.Build(theme);
            // Glow and colour grading cost fill rate: off on the Low tier for cheap phones.
            var camData = Camera.main.GetComponent<UniversalAdditionalCameraData>();
            if (camData != null) camData.renderPostProcessing = QualitySettings.GetQualityLevel() > 0;
            // Ink outlines cost a second pass per model: off on the Low tier.
            Shader.SetGlobalFloat("_CW_OutlineScale", QualitySettings.GetQualityLevel() > 0 ? 1f : 0f);
            fx = Fx.Create(battlefield.particle, transform);
            hud = KillHud.Create();
            killCam = KillCam.Create(cameraRig);
            NewGame();
        }

        public void NewGame()
        {
            StopAllCoroutines();
            Time.timeScale = 1f;
            busy = false;
            if (killCam.Active || hud.Cinematic) { StartCoroutine(killCam.Exit()); hud.SetCinematic(false); }
            if (unitRoot != null) Destroy(unitRoot.gameObject);
            unitRoot = new GameObject("Units").transform;
            pos = Rules.Rules.InitialPosition();
            seen.Clear();
            seen[Rules.Rules.PositionKey(pos)] = 1;
            streak['w'] = streak['b'] = 0;
            lastMove = null;
            lastWasCapture = anyCapture = false;
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                    units[row, col] = pos.Board[row, col] is Piece p
                        ? UnitView.Spawn(p.Color == 'w' ? whiteArmy : blackArmy, p.Type, p.Color, row, col, unitRoot)
                        : null;
            cameraRig.viewColor = aiColor == 'w' ? 'b' : 'w';
            cameraRig.Fit();
            selected = null;
            targets.Clear();
            board.ClearMarkers();
            status = $"{ArmyName('w')} to move";
            if (pos.Turn == aiColor) StartCoroutine(AiTurn());
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
            // Act on release, and only for a tap: a press that turned into a drag was moving the camera.
            var pointer = Pointer.current;
            if (pointer == null || !pointer.press.wasReleasedThisFrame || cameraRig.PressWasDrag || killCam.Active) return;
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
            if (result.MovingPiece.Type == 'p' && (move.ToRow == 0 || move.ToRow == 7))
                yield return Promote(attacker, promotion);

            string key = Rules.Rules.PositionKey(pos);
            seen[key] = seen.TryGetValue(key, out int n) ? n + 1 : 1;
            ShowMarkers(move);
            busy = false;
            if (CheckGameOver(key)) yield break;
            if (pos.Turn == aiColor) yield return AiTurn();
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
            if (Rules.Rules.AllLegalMoves(pos, pos.Turn).Count == 0)
            {
                status = inCheck ? $"Checkmate! {ArmyName(Rules.Rules.Opponent(pos.Turn))} wins." : "Stalemate. A draw.";
                if (inCheck) StartCoroutine(Checkmate(pos.Turn));
                busy = true;
                return true;
            }
            if (seen[key] >= 3 || pos.Halfmove >= 100 || Rules.Rules.HasInsufficientMaterial(pos))
            {
                status = "A draw.";
                busy = true;
                return true;
            }
            status = $"{ArmyName(pos.Turn)} to move{(inCheck ? " - CHECK!" : "")}";
            return false;
        }

        // The search runs off the main thread so the board keeps animating on slow phones.
        IEnumerator AiTurn()
        {
            busy = true;
            var snapshot = pos.Clone();
            var counts = new Dictionary<string, int>(seen);
            int depth = aiDepth;
            var seed = Random.Range(int.MinValue, int.MaxValue);
            var search = Task.Run(() => Ai.ChooseMove(snapshot, depth, counts, new System.Random(seed).NextDouble));
            yield return new WaitForSeconds(0.4f);
            while (!search.IsCompleted) yield return null;
            busy = false;
            if (search.Result != null) yield return Play(search.Result);
        }

        // A plain status line and restart button until the real interface is built; hidden during a kill.
        void OnGUI()
        {
            if (hud != null && hud.Cinematic) return;
            float scale = Screen.height / 900f;
            var style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(22 * scale), alignment = TextAnchor.MiddleCenter };
            style.normal.textColor = Color.white;
            GUI.Label(new Rect(0, 10 * scale, Screen.width, 40 * scale), status, style);
            var button = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(18 * scale) };
            if (GUI.Button(new Rect(Screen.width - 150 * scale, Screen.height - 60 * scale, 140 * scale, 50 * scale), "New game", button))
                NewGame();
        }
    }
}
