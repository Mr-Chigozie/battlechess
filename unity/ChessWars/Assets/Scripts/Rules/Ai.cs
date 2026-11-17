// Computer opponent: a port of the negamax search in js/engine.js (alpha-beta, MVV-LVA ordering and a
// capture-only quiescence search), so the Unity game's AI plays like the web game's.
using System;
using System.Collections.Generic;
using System.Linq;

namespace ChessWars.Rules
{
    public static class Ai
    {
        public const int MateScore = 100000;

        static readonly Dictionary<char, int> PieceValues = new Dictionary<char, int>
        {
            ['p'] = 100, ['n'] = 320, ['b'] = 330, ['r'] = 500, ['q'] = 900, ['k'] = 0
        };

        // Piece-square tables from Ivory's point of view, row 0 = rank 8. Onyx reads them mirrored.
        static readonly Dictionary<char, int[,]> Pst = new Dictionary<char, int[,]>
        {
            ['p'] = new[,] { { 0, 0, 0, 0, 0, 0, 0, 0 }, { 50, 50, 50, 50, 50, 50, 50, 50 }, { 10, 10, 20, 30, 30, 20, 10, 10 }, { 5, 5, 10, 25, 25, 10, 5, 5 }, { 0, 0, 0, 20, 20, 0, 0, 0 }, { 5, -5, -10, 0, 0, -10, -5, 5 }, { 5, 10, 10, -20, -20, 10, 10, 5 }, { 0, 0, 0, 0, 0, 0, 0, 0 } },
            ['n'] = new[,] { { -50, -40, -30, -30, -30, -30, -40, -50 }, { -40, -20, 0, 0, 0, 0, -20, -40 }, { -30, 0, 10, 15, 15, 10, 0, -30 }, { -30, 5, 15, 20, 20, 15, 5, -30 }, { -30, 0, 15, 20, 20, 15, 0, -30 }, { -30, 5, 10, 15, 15, 10, 5, -30 }, { -40, -20, 0, 5, 5, 0, -20, -40 }, { -50, -40, -30, -30, -30, -30, -40, -50 } },
            ['b'] = new[,] { { -20, -10, -10, -10, -10, -10, -10, -20 }, { -10, 0, 0, 0, 0, 0, 0, -10 }, { -10, 0, 5, 10, 10, 5, 0, -10 }, { -10, 5, 5, 10, 10, 5, 5, -10 }, { -10, 0, 10, 10, 10, 10, 0, -10 }, { -10, 10, 10, 10, 10, 10, 10, -10 }, { -10, 5, 0, 0, 0, 0, 5, -10 }, { -20, -10, -10, -10, -10, -10, -10, -20 } },
            ['r'] = new[,] { { 0, 0, 0, 0, 0, 0, 0, 0 }, { 5, 10, 10, 10, 10, 10, 10, 5 }, { -5, 0, 0, 0, 0, 0, 0, -5 }, { -5, 0, 0, 0, 0, 0, 0, -5 }, { -5, 0, 0, 0, 0, 0, 0, -5 }, { -5, 0, 0, 0, 0, 0, 0, -5 }, { -5, 0, 0, 0, 0, 0, 0, -5 }, { 0, 0, 0, 5, 5, 0, 0, 0 } },
            ['q'] = new[,] { { -20, -10, -10, -5, -5, -10, -10, -20 }, { -10, 0, 0, 0, 0, 0, 0, -10 }, { -10, 0, 5, 5, 5, 5, 0, -10 }, { -5, 0, 5, 5, 5, 5, 0, -5 }, { 0, 0, 5, 5, 5, 5, 0, -5 }, { -10, 5, 5, 5, 5, 5, 0, -10 }, { -10, 0, 5, 0, 0, 0, 0, -10 }, { -20, -10, -10, -5, -5, -10, -10, -20 } },
            ['k'] = new[,] { { -30, -40, -40, -50, -50, -40, -40, -30 }, { -30, -40, -40, -50, -50, -40, -40, -30 }, { -30, -40, -40, -50, -50, -40, -40, -30 }, { -30, -40, -40, -50, -50, -40, -40, -30 }, { -20, -30, -30, -40, -40, -30, -30, -20 }, { -10, -20, -20, -20, -20, -20, -20, -10 }, { 20, 20, 0, 0, 0, 0, 20, 20 }, { 20, 30, 10, 0, 0, 10, 30, 20 } }
        };

        public static int PieceValue(char type) => PieceValues[type];

        public static int Evaluate(Position pos)
        {
            int score = 0;
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                {
                    if (!(pos.Board[row, col] is Piece p)) continue;
                    int value = PieceValues[p.Type] + Pst[p.Type][p.Color == 'w' ? row : 7 - row, col];
                    score += p.Color == 'w' ? value : -value;
                }
            return pos.Turn == 'w' ? score : -score;
        }

        static Piece? CapturedBy(Position pos, Move move) =>
            move.EnPassant ? pos.Board[move.FromRow, move.ToCol] : pos.Board[move.ToRow, move.ToCol];

        // Captures first (most valuable victim, least valuable attacker), then promotions. Stable, like the JS sort.
        static List<Move> OrderMoves(Position pos, IEnumerable<Move> moves) =>
            moves.OrderByDescending(move =>
            {
                var victim = CapturedBy(pos, move);
                var attacker = pos.Board[move.FromRow, move.FromCol].Value;
                int value = victim is Piece v ? 10 * PieceValues[v.Type] - PieceValues[attacker.Type] + 10000 : 0;
                if (attacker.Type == 'p' && (move.ToRow == 0 || move.ToRow == 7)) value += 9000;
                return value;
            }).ToList();

        static double Quiesce(Position pos, double alpha, double beta, int depthLeft)
        {
            double standPat = Evaluate(pos);
            if (standPat >= beta || depthLeft == 0) return standPat;
            if (standPat > alpha) alpha = standPat;
            var captures = OrderMoves(pos, Rules.AllLegalMoves(pos, pos.Turn).Where(m => CapturedBy(pos, m) != null));
            foreach (var move in captures)
            {
                var next = pos.Clone();
                Rules.ApplyMove(next, move);
                double score = -Quiesce(next, -beta, -alpha, depthLeft - 1);
                if (score >= beta) return score;
                if (score > alpha) alpha = score;
            }
            return alpha;
        }

        static double Negamax(Position pos, int depth, double alpha, double beta, int ply)
        {
            var moves = Rules.AllLegalMoves(pos, pos.Turn);
            if (moves.Count == 0) return Rules.IsInCheck(pos, pos.Turn) ? -MateScore + ply : 0;
            if (pos.Halfmove >= 100 || Rules.HasInsufficientMaterial(pos)) return 0;
            if (depth == 0) return Quiesce(pos, alpha, beta, 4);
            double best = double.NegativeInfinity;
            foreach (var move in OrderMoves(pos, moves))
            {
                var next = pos.Clone();
                Rules.ApplyMove(next, move);
                double score = -Negamax(next, depth - 1, -beta, -alpha, ply + 1);
                if (score > best) best = score;
                if (score > alpha) alpha = score;
                if (alpha >= beta) break;
            }
            return best;
        }

        // `counts` maps PositionKey -> times seen this game, so the AI can avoid drifting into repetition draws.
        // `random` returns [0, 1); pass a seeded one for repeatable games.
        public static Move ChooseMove(Position pos, int depth, IReadOnlyDictionary<string, int> counts = null, Func<double> random = null)
        {
            random ??= new Random().NextDouble;
            // Recruits are deliberately sloppy: a little noise makes them beatable.
            int noise = depth == 1 ? 60 : depth == 2 ? 12 : 0;
            Move best = null;
            double bestScore = double.NegativeInfinity;
            double alpha = double.NegativeInfinity;
            foreach (var move in OrderMoves(pos, Rules.AllLegalMoves(pos, pos.Turn)))
            {
                var next = pos.Clone();
                Rules.ApplyMove(next, move);
                int repeats = counts != null && counts.TryGetValue(Rules.PositionKey(next), out int n) ? n : 0;
                double score = -Negamax(next, depth - 1, double.NegativeInfinity, -alpha, 1) + (random() - 0.5) * noise;
                if (repeats >= 2 && score > 0) score = 0;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = move;
                }
                if (score > alpha && noise == 0) alpha = score;
            }
            return best;
        }
    }
}
