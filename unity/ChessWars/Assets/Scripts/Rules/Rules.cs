// Chess Wars rules engine: a line-for-line port of js/engine.js, so the Unity game, the web game and the
// server (server/src/rules.js, which loads engine.js) always agree on what is legal.
// Board rows run 0 (rank 8, Onyx home) to 7 (rank 1, Ivory home); colours are 'w' (Ivory) and 'b' (Onyx);
// piece types are 'k', 'q', 'r', 'b', 'n', 'p' - the same letters the web game and the server use.
// Plain C# 9 with no Unity types, so it also builds and runs under .NET for tests (unity/RulesTests).
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ChessWars.Rules
{
    public readonly struct Piece
    {
        public readonly char Type;
        public readonly char Color;
        public readonly bool Moved;

        public Piece(char type, char color, bool moved = false)
        {
            Type = type;
            Color = color;
            Moved = moved;
        }

        public Piece With(char? type = null, bool? moved = null) => new Piece(type ?? Type, Color, moved ?? Moved);
        public override string ToString() => $"{Color}{Type}";
    }

    public readonly struct Square
    {
        public readonly int Row;
        public readonly int Col;
        public Square(int row, int col) { Row = row; Col = col; }
    }

    public enum Castle { None, King, Queen }

    public sealed class Move
    {
        public int FromRow, FromCol, ToRow, ToCol;
        public bool DoublePawn, EnPassant;
        public Castle Castle;

        public Move(int fromRow, int fromCol, int toRow, int toCol)
        {
            FromRow = fromRow; FromCol = fromCol; ToRow = toRow; ToCol = toCol;
        }

        public override string ToString() => Rules.Algebraic(FromRow, FromCol) + Rules.Algebraic(ToRow, ToCol);
    }

    public sealed class Position
    {
        public Piece?[,] Board = new Piece?[8, 8];
        public char Turn = 'w';
        public Square? EnPassant;
        public int Halfmove;
        public int Fullmove = 1;

        public Position Clone()
        {
            var copy = new Position { Turn = Turn, EnPassant = EnPassant, Halfmove = Halfmove, Fullmove = Fullmove };
            // A plain loop: IL2CPP (Android builds) throws InvalidCastException on Array.Copy of a 2D Nullable array.
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                    copy.Board[row, col] = Board[row, col];
            return copy;
        }
    }

    public readonly struct MoveResult
    {
        public readonly Piece? Captured;
        public readonly Piece MovingPiece;
        public MoveResult(Piece? captured, Piece moving) { Captured = captured; MovingPiece = moving; }
    }

    public static class Rules
    {
        public const string Files = "abcdefgh";
        static readonly int[,] KnightSteps = { { -2, -1 }, { -2, 1 }, { -1, -2 }, { -1, 2 }, { 1, -2 }, { 1, 2 }, { 2, -1 }, { 2, 1 } };
        static readonly int[,] Straight = { { -1, 0 }, { 1, 0 }, { 0, -1 }, { 0, 1 } };
        static readonly int[,] Diagonal = { { -1, -1 }, { -1, 1 }, { 1, -1 }, { 1, 1 } };

        public static char Opponent(char color) => color == 'w' ? 'b' : 'w';

        public static Position InitialPosition()
        {
            var pos = new Position();
            const string backRank = "rnbqkbnr";
            for (int col = 0; col < 8; col++)
            {
                pos.Board[0, col] = new Piece(backRank[col], 'b');
                pos.Board[1, col] = new Piece('p', 'b');
                pos.Board[6, col] = new Piece('p', 'w');
                pos.Board[7, col] = new Piece(backRank[col], 'w');
            }
            return pos;
        }

        public static bool Inside(int row, int col) => row >= 0 && row < 8 && col >= 0 && col < 8;

        public static Piece? PieceAt(Position pos, int row, int col) => Inside(row, col) ? pos.Board[row, col] : null;

        public static Square? FindKing(Position pos, char color)
        {
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                {
                    var piece = pos.Board[row, col];
                    if (piece is Piece p && p.Color == color && p.Type == 'k') return new Square(row, col);
                }
            return null;
        }

        static bool Is(Piece? piece, char color, char type) => piece is Piece p && p.Color == color && p.Type == type;

        public static bool IsSquareAttacked(Position pos, int row, int col, char byColor)
        {
            int pawnRow = row + (byColor == 'w' ? 1 : -1);
            if (Is(PieceAt(pos, pawnRow, col - 1), byColor, 'p') || Is(PieceAt(pos, pawnRow, col + 1), byColor, 'p')) return true;
            for (int i = 0; i < 8; i++)
                if (Is(PieceAt(pos, row + KnightSteps[i, 0], col + KnightSteps[i, 1]), byColor, 'n')) return true;
            for (int dr = -1; dr <= 1; dr++)
                for (int dc = -1; dc <= 1; dc++)
                    if ((dr != 0 || dc != 0) && Is(PieceAt(pos, row + dr, col + dc), byColor, 'k')) return true;
            if (RayHits(pos, row, col, byColor, Straight, 'r')) return true;
            return RayHits(pos, row, col, byColor, Diagonal, 'b');
        }

        // Along each direction, the first piece met attacks the square if it is `slider` or a queen of `byColor`.
        static bool RayHits(Position pos, int row, int col, char byColor, int[,] directions, char slider)
        {
            for (int i = 0; i < 4; i++)
            {
                int r = row + directions[i, 0], c = col + directions[i, 1];
                while (Inside(r, c))
                {
                    if (pos.Board[r, c] is Piece p)
                    {
                        if (p.Color == byColor && (p.Type == slider || p.Type == 'q')) return true;
                        break;
                    }
                    r += directions[i, 0];
                    c += directions[i, 1];
                }
            }
            return false;
        }

        public static bool IsInCheck(Position pos, char color)
        {
            var king = FindKing(pos, color);
            return king is Square k && IsSquareAttacked(pos, k.Row, k.Col, Opponent(color));
        }

        public static List<Move> PseudoMoves(Position pos, int row, int col)
        {
            var moves = new List<Move>();
            if (!(PieceAt(pos, row, col) is Piece piece)) return moves;

            // Adds a step onto an empty square or an enemy (never a king); returns true if the ray may continue.
            bool AddIfAvailable(int toRow, int toCol)
            {
                if (!Inside(toRow, toCol)) return false;
                var target = pos.Board[toRow, toCol];
                if (target == null)
                {
                    moves.Add(new Move(row, col, toRow, toCol));
                    return true;
                }
                if (target.Value.Color != piece.Color && target.Value.Type != 'k') moves.Add(new Move(row, col, toRow, toCol));
                return false;
            }

            if (piece.Type == 'p')
            {
                int direction = piece.Color == 'w' ? -1 : 1;
                int startRow = piece.Color == 'w' ? 6 : 1;
                if (Inside(row + direction, col) && PieceAt(pos, row + direction, col) == null)
                {
                    moves.Add(new Move(row, col, row + direction, col));
                    if (row == startRow && PieceAt(pos, row + 2 * direction, col) == null)
                        moves.Add(new Move(row, col, row + 2 * direction, col) { DoublePawn = true });
                }
                foreach (int dc in new[] { -1, 1 })
                {
                    int targetRow = row + direction, targetCol = col + dc;
                    if (!Inside(targetRow, targetCol)) continue;
                    var target = pos.Board[targetRow, targetCol];
                    if (target is Piece t && t.Color != piece.Color && t.Type != 'k')
                        moves.Add(new Move(row, col, targetRow, targetCol));
                    else if (pos.EnPassant is Square ep && ep.Row == targetRow && ep.Col == targetCol)
                        moves.Add(new Move(row, col, targetRow, targetCol) { EnPassant = true });
                }
            }
            else if (piece.Type == 'n')
            {
                for (int i = 0; i < 8; i++) AddIfAvailable(row + KnightSteps[i, 0], col + KnightSteps[i, 1]);
            }
            else if (piece.Type == 'k')
            {
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                        if (dr != 0 || dc != 0) AddIfAvailable(row + dr, col + dc);
                int homeRow = piece.Color == 'w' ? 7 : 0;
                char enemy = Opponent(piece.Color);
                if (row == homeRow && col == 4 && !piece.Moved && !IsInCheck(pos, piece.Color))
                {
                    if (PieceAt(pos, homeRow, 7) is Piece kr && kr.Type == 'r' && kr.Color == piece.Color && !kr.Moved &&
                        PieceAt(pos, homeRow, 5) == null && PieceAt(pos, homeRow, 6) == null &&
                        !IsSquareAttacked(pos, homeRow, 5, enemy) && !IsSquareAttacked(pos, homeRow, 6, enemy))
                        moves.Add(new Move(row, col, homeRow, 6) { Castle = Castle.King });
                    if (PieceAt(pos, homeRow, 0) is Piece qr && qr.Type == 'r' && qr.Color == piece.Color && !qr.Moved &&
                        PieceAt(pos, homeRow, 1) == null && PieceAt(pos, homeRow, 2) == null && PieceAt(pos, homeRow, 3) == null &&
                        !IsSquareAttacked(pos, homeRow, 3, enemy) && !IsSquareAttacked(pos, homeRow, 2, enemy))
                        moves.Add(new Move(row, col, homeRow, 2) { Castle = Castle.Queen });
                }
            }
            else
            {
                var directions = new List<(int, int)>();
                if (piece.Type == 'r' || piece.Type == 'q') directions.AddRange(new[] { (-1, 0), (1, 0), (0, -1), (0, 1) });
                if (piece.Type == 'b' || piece.Type == 'q') directions.AddRange(new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) });
                foreach (var (dr, dc) in directions)
                {
                    int r = row + dr, c = col + dc;
                    while (Inside(r, c))
                    {
                        if (!AddIfAvailable(r, c)) break;
                        r += dr;
                        c += dc;
                    }
                }
            }
            return moves;
        }

        public static MoveResult ApplyMove(Position pos, Move move, char promotion = 'q')
        {
            var piece = pos.Board[move.FromRow, move.FromCol].Value;
            var captured = move.EnPassant ? pos.Board[move.FromRow, move.ToCol] : pos.Board[move.ToRow, move.ToCol];
            pos.Board[move.FromRow, move.FromCol] = null;
            if (move.EnPassant) pos.Board[move.FromRow, move.ToCol] = null;
            if (move.Castle != Castle.None)
            {
                int rookFrom = move.Castle == Castle.King ? 7 : 0;
                int rookTo = move.Castle == Castle.King ? 5 : 3;
                var rook = pos.Board[move.FromRow, rookFrom].Value;
                pos.Board[move.FromRow, rookFrom] = null;
                pos.Board[move.FromRow, rookTo] = rook.With(moved: true);
            }
            var moved = piece.With(moved: true);
            if (piece.Type == 'p' && (move.ToRow == 0 || move.ToRow == 7)) moved = moved.With(type: promotion);
            pos.Board[move.ToRow, move.ToCol] = moved;
            pos.EnPassant = move.DoublePawn ? new Square((move.FromRow + move.ToRow) / 2, move.FromCol) : (Square?)null;
            pos.Halfmove = piece.Type == 'p' || captured != null ? 0 : pos.Halfmove + 1;
            if (piece.Color == 'b') pos.Fullmove += 1;
            pos.Turn = Opponent(piece.Color);
            return new MoveResult(captured, piece);
        }

        public static List<Move> LegalMoves(Position pos, int row, int col)
        {
            if (!(PieceAt(pos, row, col) is Piece piece) || piece.Color != pos.Turn) return new List<Move>();
            return PseudoMoves(pos, row, col).Where(move =>
            {
                var test = pos.Clone();
                ApplyMove(test, move);
                return !IsInCheck(test, piece.Color);
            }).ToList();
        }

        public static List<Move> AllLegalMoves(Position pos, char color)
        {
            char savedTurn = pos.Turn;
            pos.Turn = color;
            var moves = new List<Move>();
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                    if (pos.Board[row, col] is Piece p && p.Color == color) moves.AddRange(LegalMoves(pos, row, col));
            pos.Turn = savedTurn;
            return moves;
        }

        public static string CastlingRights(Position pos)
        {
            var rights = new StringBuilder();
            foreach (var (color, homeRow) in new[] { ('w', 7), ('b', 0) })
            {
                if (!(pos.Board[homeRow, 4] is Piece king) || king.Type != 'k' || king.Color != color || king.Moved) continue;
                foreach (var (rookCol, flag) in new[] { (7, 'k'), (0, 'q') })
                    if (pos.Board[homeRow, rookCol] is Piece rook && rook.Type == 'r' && rook.Color == color && !rook.Moved)
                        rights.Append(color == 'w' ? char.ToUpperInvariant(flag) : flag);
            }
            return rights.Length > 0 ? rights.ToString() : "-";
        }

        // Repetition compares placement, side to move, castling rights and a usable en passant square (FIDE 9.2).
        public static string PositionKey(Position pos)
        {
            var sb = new StringBuilder();
            for (int row = 0; row < 8; row++)
            {
                if (row > 0) sb.Append('/');
                for (int col = 0; col < 8; col++)
                    if (pos.Board[row, col] is Piece p) sb.Append(p.Color).Append(p.Type); else sb.Append('.');
            }
            string ep = "-";
            if (pos.EnPassant is Square sq)
            {
                int pawnRow = sq.Row + (pos.Turn == 'w' ? 1 : -1);
                bool canTake = new[] { sq.Col - 1, sq.Col + 1 }.Any(col =>
                    PieceAt(pos, pawnRow, col) is Piece pawn && pawn.Type == 'p' && pawn.Color == pos.Turn &&
                    LegalMoves(pos, pawnRow, col).Any(move => move.EnPassant));
                if (canTake) ep = $"{sq.Row}{sq.Col}";
            }
            return $"{sb} {pos.Turn} {CastlingRights(pos)} {ep}";
        }

        public static bool HasInsufficientMaterial(Position pos)
        {
            var remaining = new List<(Piece piece, int square)>();
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                    if (pos.Board[row, col] is Piece p && p.Type != 'k') remaining.Add((p, (row + col) % 2));
            if (remaining.Count == 0) return true;
            if (remaining.Any(e => e.piece.Type == 'p' || e.piece.Type == 'r' || e.piece.Type == 'q')) return false;
            if (remaining.Count == 1) return true;
            return remaining.All(e => e.piece.Type == 'b') && remaining.Select(e => e.square).Distinct().Count() == 1;
        }

        public static string Algebraic(int row, int col) => $"{Files[col]}{8 - row}";

        // `before` is the position prior to the move, used to disambiguate e.g. Nbd2 vs Nfd2.
        public static string MoveNotation(Position before, Move move, Piece piece, Piece? captured, char? promotion, string suffix)
        {
            if (move.Castle != Castle.None) return (move.Castle == Castle.King ? "O-O" : "O-O-O") + suffix;
            string destination = Algebraic(move.ToRow, move.ToCol);
            if (piece.Type == 'p')
                return (captured != null ? Files[move.FromCol] + "x" : "") + destination +
                       (promotion is char pr ? "=" + char.ToUpperInvariant(pr) : "") + suffix;
            string origin = "";
            if (piece.Type != 'k')
            {
                var rivals = AllLegalMoves(before, piece.Color).Where(other =>
                    other.ToRow == move.ToRow && other.ToCol == move.ToCol &&
                    (other.FromRow != move.FromRow || other.FromCol != move.FromCol) &&
                    before.Board[other.FromRow, other.FromCol].Value.Type == piece.Type).ToList();
                if (rivals.Count > 0)
                {
                    if (!rivals.Any(o => o.FromCol == move.FromCol)) origin = Files[move.FromCol].ToString();
                    else if (!rivals.Any(o => o.FromRow == move.FromRow)) origin = (8 - move.FromRow).ToString();
                    else origin = Algebraic(move.FromRow, move.FromCol);
                }
            }
            return $"{char.ToUpperInvariant(piece.Type)}{origin}{(captured != null ? "x" : "")}{destination}{suffix}";
        }

        // Builds a position from FEN (for tests and puzzles). Castling availability sets the kings' and rooks'
        // `Moved` flags, the way this engine tracks castling rights.
        public static Position FromFen(string fen)
        {
            var parts = fen.Trim().Split(' ');
            var pos = new Position();
            var ranks = parts[0].Split('/');
            for (int row = 0; row < 8; row++)
            {
                int col = 0;
                foreach (char ch in ranks[row])
                {
                    if (char.IsDigit(ch)) { col += ch - '0'; continue; }
                    char color = char.IsUpper(ch) ? 'w' : 'b';
                    pos.Board[row, col++] = new Piece(char.ToLowerInvariant(ch), color, moved: true);
                }
            }
            pos.Turn = parts.Length > 1 ? parts[1][0] : 'w';
            string castling = parts.Length > 2 ? parts[2] : "-";
            void Unmoved(int row, int col) { if (pos.Board[row, col] is Piece p) pos.Board[row, col] = p.With(moved: false); }
            if (castling.Contains('K')) { Unmoved(7, 4); Unmoved(7, 7); }
            if (castling.Contains('Q')) { Unmoved(7, 4); Unmoved(7, 0); }
            if (castling.Contains('k')) { Unmoved(0, 4); Unmoved(0, 7); }
            if (castling.Contains('q')) { Unmoved(0, 4); Unmoved(0, 0); }
            // Pawns on their start rank have not moved (it matters only for the double step, which checks the row anyway).
            for (int col = 0; col < 8; col++)
            {
                if (pos.Board[6, col] is Piece wp && wp.Type == 'p' && wp.Color == 'w') pos.Board[6, col] = wp.With(moved: false);
                if (pos.Board[1, col] is Piece bp && bp.Type == 'p' && bp.Color == 'b') pos.Board[1, col] = bp.With(moved: false);
            }
            if (parts.Length > 3 && parts[3] != "-") pos.EnPassant = new Square(8 - (parts[3][1] - '0'), parts[3][0] - 'a');
            if (parts.Length > 4) pos.Halfmove = int.Parse(parts[4]);
            if (parts.Length > 5) pos.Fullmove = int.Parse(parts[5]);
            return pos;
        }
    }
}
