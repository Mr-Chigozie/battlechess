// A skirmish against the computer: tap a unit, tap a square. The rules are ChessWars.Rules (the C# port of
// js/engine.js); everything here is presentation: markers, walking, attacks and deaths.
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
        public bool blood;   // beheadings on blade kills, as the web game's Blood toggle

        Position pos;
        readonly UnitView[,] units = new UnitView[8, 8];
        readonly Dictionary<string, int> seen = new Dictionary<string, int>();
        Transform unitRoot;
        Vector2Int? selected;
        List<Move> targets = new List<Move>();
        bool busy;
        string status = "";
        float lastEmptyTap = -1f;

        void Start()
        {
            Application.targetFrameRate = 60;
            // The battle is fought in the opponent's world (as in the web game's story mode).
            var theme = Themes.For(aiColor == 'b' ? blackArmy : whiteArmy);
            battlefield.Build(theme);
            board.Build(theme);
            // Glow and colour grading cost fill rate: off on the Low tier for cheap phones.
            var camData = cameraRig.GetComponent<UniversalAdditionalCameraData>();
            if (camData != null) camData.renderPostProcessing = QualitySettings.GetQualityLevel() > 0;
            NewGame();
        }

        public void NewGame()
        {
            StopAllCoroutines();
            busy = false;
            if (unitRoot != null) Destroy(unitRoot.gameObject);
            unitRoot = new GameObject("Units").transform;
            pos = Rules.Rules.InitialPosition();
            seen.Clear();
            seen[Rules.Rules.PositionKey(pos)] = 1;
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

        void Update()
        {
            // Act on release, and only for a tap: a press that turned into a drag was moving the camera.
            var pointer = Pointer.current;
            if (pointer == null || !pointer.press.wasReleasedThisFrame || cameraRig.PressWasDrag) return;
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
            var cam = cameraRig.GetComponent<Camera>();
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

        // Ranged units shoot from where they stand; everyone else walks up and strikes.
        static bool Ranged(UnitView unit) =>
            unit.Type == 'b' || unit.Type == 'r' || unit.Army == "marines" || (unit.Army == "machines" && unit.Type != 'n');

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

            if (victim != null)
            {
                bool behead = blood && !Ranged(attacker) && victim.HasBehead;
                if (Ranged(attacker))
                {
                    yield return attacker.TurnTowards(victim.transform.position, 0.25f);
                    attacker.Play("Shoot");
                    yield return new WaitForSeconds(attacker.Army == "marines" && attacker.Type == 'b' ? 1.1f : 0.6f);
                }
                else
                {
                    var stop = victim.transform.position + (attacker.transform.position - victim.transform.position).normalized * 0.62f;
                    yield return attacker.Travel(stop);
                    yield return attacker.TurnTowards(victim.transform.position, 0.12f);
                    StartCoroutine(attacker.StrikeIn(0f));
                    yield return new WaitForSeconds(UnitView.AttackHit);
                }
                StartCoroutine(victim.TurnTowards(attacker.transform.position, 0.1f));
                StartCoroutine(victim.Die(behead));
                yield return new WaitForSeconds(1.0f);
            }
            yield return attacker.Travel(dest, attacker.Type == 'n' && victim == null ? 1.1f : 0f);
            attacker.Row = move.ToRow;
            attacker.Col = move.ToCol;
            units[move.ToRow, move.ToCol] = attacker;
            yield return attacker.TurnTowards(dest + (attacker.Color == 'w' ? Vector3.back : Vector3.forward), 0.2f);
            attacker.FaceHome();

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
            {
                Destroy(attacker.gameObject);
                units[move.ToRow, move.ToCol] = UnitView.Spawn(attacker.Army, promotion, attacker.Color, move.ToRow, move.ToCol, unitRoot);
            }

            string key = Rules.Rules.PositionKey(pos);
            seen[key] = seen.TryGetValue(key, out int n) ? n + 1 : 1;
            ShowMarkers(move);
            busy = false;
            if (CheckGameOver(key)) yield break;
            if (pos.Turn == aiColor) yield return AiTurn();
        }

        bool CheckGameOver(string key)
        {
            bool inCheck = Rules.Rules.IsInCheck(pos, pos.Turn);
            if (Rules.Rules.AllLegalMoves(pos, pos.Turn).Count == 0)
            {
                status = inCheck ? $"Checkmate! {ArmyName(Rules.Rules.Opponent(pos.Turn))} wins." : "Stalemate. A draw.";
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

        // A plain status line and restart button until the real interface is built.
        void OnGUI()
        {
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
