// Records what js/engine.js says about a set of positions, as the fixture the C# port is tested against.
// Run from the repo root:  node unity/RulesTests/make-parity.js
"use strict";
const fs = require("fs");
const path = require("path");
const vm = require("vm");

vm.runInThisContext(fs.readFileSync(path.join(__dirname, "..", "..", "js", "engine.js"), "utf8"));

// Same FEN reading as Rules.FromFen in C#: castling flags decide the kings' and rooks' `moved` state.
function fromFen(fen) {
  const [placement, turn = "w", castling = "-", ep = "-", half = "0", full = "1"] = fen.trim().split(" ");
  const board = emptyBoard();
  placement.split("/").forEach((rank, row) => {
    let col = 0;
    for (const ch of rank) {
      if (/\d/.test(ch)) { col += +ch; continue; }
      board[row][col++] = { type: ch.toLowerCase(), color: ch === ch.toUpperCase() ? "w" : "b", moved: true };
    }
  });
  const unmoved = (row, col) => { if (board[row][col]) board[row][col].moved = false; };
  if (castling.includes("K")) { unmoved(7, 4); unmoved(7, 7); }
  if (castling.includes("Q")) { unmoved(7, 4); unmoved(7, 0); }
  if (castling.includes("k")) { unmoved(0, 4); unmoved(0, 7); }
  if (castling.includes("q")) { unmoved(0, 4); unmoved(0, 0); }
  for (let col = 0; col < 8; col += 1) {
    if (board[6][col]?.type === "p" && board[6][col].color === "w") board[6][col].moved = false;
    if (board[1][col]?.type === "p" && board[1][col].color === "b") board[1][col].moved = false;
  }
  const enPassant = ep === "-" ? null : { row: 8 - +ep[1], col: ep.charCodeAt(0) - 97 };
  return { board, turn, enPassant, halfmove: +half, fullmove: +full };
}

const moveName = m => algebraic(m.fromRow, m.fromCol) + algebraic(m.toRow, m.toCol);

function perft(pos, depth) {
  if (depth === 0) return 1;
  let nodes = 0;
  for (const move of allLegalMoves(pos, pos.turn)) {
    const next = clonePosition(pos);
    applyMove(next, move);
    nodes += perft(next, depth - 1);
  }
  return nodes;
}

const CASES = [
  { name: "start", fen: "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", depth: 4 },
  { name: "kiwipete", fen: "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", depth: 3 },
  { name: "endgame", fen: "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", depth: 3 },
  { name: "promotions", fen: "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", depth: 3 },
  { name: "tricky", fen: "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", depth: 3 },
  { name: "enpassant", fen: "rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3", depth: 3 }
];

Math.random = () => 0.5;  // no noise, so the AI's choice is deterministic
const out = CASES.map(({ name, fen, depth }) => {
  const pos = fromFen(fen);
  const moves = allLegalMoves(pos, pos.turn);
  const divide = {};
  const notation = {};
  const keys = {};
  for (const move of moves) {
    const next = clonePosition(pos);
    const { captured, movingPiece } = applyMove(next, move);
    const promo = movingPiece.type === "p" && (move.toRow === 0 || move.toRow === 7) ? "q" : null;
    divide[moveName(move)] = perft(next, depth - 1);
    notation[moveName(move)] = moveNotation(pos, move, movingPiece, captured, promo, "");
    keys[moveName(move)] = positionKey(next);
  }
  const ai = {};
  for (const d of [1, 2, 3]) ai[d] = moveName(chooseAiMove(clonePosition(pos), d, new Map()));
  return {
    name, fen, depth,
    nodes: Object.values(divide).reduce((a, b) => a + b, 0),
    divide, notation, keys,
    key: positionKey(pos), inCheck: isInCheck(pos, pos.turn), evaluation: evaluate(pos),
    insufficient: hasInsufficientMaterial(pos), ai
  };
});
fs.writeFileSync(path.join(__dirname, "parity.json"), JSON.stringify(out, null, 1));
for (const c of out) console.log(c.name.padEnd(11), "nodes", String(c.nodes).padStart(7), " ai", JSON.stringify(c.ai));
