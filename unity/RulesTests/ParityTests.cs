using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ChessWars.Rules;
using Xunit;

namespace ChessWars.Tests
{
    // Every case in parity.json was recorded from js/engine.js; the C# port must reproduce it exactly.
    public class ParityTests
    {
        public class Case
        {
            public string name { get; set; }
            public string fen { get; set; }
            public int depth { get; set; }
            public long nodes { get; set; }
            public Dictionary<string, long> divide { get; set; }
            public Dictionary<string, string> notation { get; set; }
            public Dictionary<string, string> keys { get; set; }
            public string key { get; set; }
            public bool inCheck { get; set; }
            public int evaluation { get; set; }
            public bool insufficient { get; set; }
            public Dictionary<string, string> ai { get; set; }
        }

        static readonly List<Case> Cases =
            JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "parity.json")));

        public static IEnumerable<object[]> Names => Cases.Select(c => new object[] { c.name });
        static Case Get(string name) => Cases.Single(c => c.name == name);

        static long Perft(Position pos, int depth)
        {
            if (depth == 0) return 1;
            long nodes = 0;
            foreach (var move in Rules.Rules.AllLegalMoves(pos, pos.Turn))
            {
                var next = pos.Clone();
                Rules.Rules.ApplyMove(next, move);
                nodes += Perft(next, depth - 1);
            }
            return nodes;
        }

        [Theory, MemberData(nameof(Names))]
        public void SameLegalMovesAndMoveCountsToDepth(string name)
        {
            var c = Get(name);
            var pos = Rules.Rules.FromFen(c.fen);
            var divide = new Dictionary<string, long>();
            foreach (var move in Rules.Rules.AllLegalMoves(pos, pos.Turn))
            {
                var next = pos.Clone();
                Rules.Rules.ApplyMove(next, move);
                divide[move.ToString()] = Perft(next, c.depth - 1);
            }
            Assert.Equal(c.divide.OrderBy(kv => kv.Key), divide.OrderBy(kv => kv.Key));
            Assert.Equal(c.nodes, divide.Values.Sum());
        }

        [Theory, MemberData(nameof(Names))]
        public void SameNotationAndRepetitionKeys(string name)
        {
            var c = Get(name);
            var pos = Rules.Rules.FromFen(c.fen);
            Assert.Equal(c.key, Rules.Rules.PositionKey(pos));
            Assert.Equal(c.inCheck, Rules.Rules.IsInCheck(pos, pos.Turn));
            Assert.Equal(c.insufficient, Rules.Rules.HasInsufficientMaterial(pos));
            foreach (var move in Rules.Rules.AllLegalMoves(pos, pos.Turn))
            {
                var next = pos.Clone();
                var result = Rules.Rules.ApplyMove(next, move);
                char? promo = result.MovingPiece.Type == 'p' && (move.ToRow == 0 || move.ToRow == 7) ? 'q' : (char?)null;
                Assert.Equal(c.notation[move.ToString()], Rules.Rules.MoveNotation(pos, move, result.MovingPiece, result.Captured, promo, ""));
                Assert.Equal(c.keys[move.ToString()], Rules.Rules.PositionKey(next));
            }
        }

        [Theory, MemberData(nameof(Names))]
        public void SameEvaluationAndAiChoices(string name)
        {
            var c = Get(name);
            var pos = Rules.Rules.FromFen(c.fen);
            Assert.Equal(c.evaluation, Ai.Evaluate(pos));
            foreach (var (depth, expected) in c.ai)
                Assert.Equal(expected, Ai.ChooseMove(pos.Clone(), int.Parse(depth), null, () => 0.5).ToString());
        }
    }
}
