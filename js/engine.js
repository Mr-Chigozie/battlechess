// Chess Wars rules engine and computer opponent. Shared by the 2D board (index.html) and the 3D battle (battle3d.html).
// Board rows run 0 (rank 8, Onyx home) to 7 (rank 1, Ivory home); colours are "w" (Ivory) and "b" (Onyx).
"use strict";

const FILES = "abcdefgh";
const ROLE_NAMES = { k: "king", q: "queen", r: "rook", b: "bishop", n: "knight", p: "pawn" };

function emptyBoard() {
  return Array.from({ length: 8 }, () => Array(8).fill(null));
}

function initialPosition() {
  const board = emptyBoard();
  const backRank = ["r", "n", "b", "q", "k", "b", "n", "r"];
  for (let col = 0; col < 8; col += 1) {
    board[0][col] = { type: backRank[col], color: "b", moved: false };
    board[1][col] = { type: "p", color: "b", moved: false };
    board[6][col] = { type: "p", color: "w", moved: false };
    board[7][col] = { type: backRank[col], color: "w", moved: false };
  }
  return { board, turn: "w", enPassant: null, halfmove: 0, fullmove: 1 };
}

function clonePosition(pos) {
  return {
    board: pos.board.map(row => row.map(piece => piece ? { ...piece } : null)),
    turn: pos.turn,
    enPassant: pos.enPassant ? { ...pos.enPassant } : null,
    halfmove: pos.halfmove,
    fullmove: pos.fullmove
  };
}

function inside(row, col) {
  return row >= 0 && row < 8 && col >= 0 && col < 8;
}

function pieceAt(pos, row, col) {
  return inside(row, col) ? pos.board[row][col] : null;
}

function findKing(pos, color) {
  for (let row = 0; row < 8; row += 1) {
    for (let col = 0; col < 8; col += 1) {
      const piece = pos.board[row][col];
      if (piece && piece.color === color && piece.type === "k") return { row, col };
    }
  }
  return null;
}

function isSquareAttacked(pos, row, col, byColor) {
  const pawnRow = row + (byColor === "w" ? 1 : -1);
  for (const pawnCol of [col - 1, col + 1]) {
    const pawn = pieceAt(pos, pawnRow, pawnCol);
    if (pawn && pawn.color === byColor && pawn.type === "p") return true;
  }
  const knightSteps = [[-2, -1], [-2, 1], [-1, -2], [-1, 2], [1, -2], [1, 2], [2, -1], [2, 1]];
  for (const [dr, dc] of knightSteps) {
    const piece = pieceAt(pos, row + dr, col + dc);
    if (piece && piece.color === byColor && piece.type === "n") return true;
  }
  for (let dr = -1; dr <= 1; dr += 1) {
    for (let dc = -1; dc <= 1; dc += 1) {
      if (dr === 0 && dc === 0) continue;
      const piece = pieceAt(pos, row + dr, col + dc);
      if (piece && piece.color === byColor && piece.type === "k") return true;
    }
  }
  const rays = [
    { directions: [[-1, 0], [1, 0], [0, -1], [0, 1]], types: ["r", "q"] },
    { directions: [[-1, -1], [-1, 1], [1, -1], [1, 1]], types: ["b", "q"] }
  ];
  for (const ray of rays) {
    for (const [dr, dc] of ray.directions) {
      let nextRow = row + dr;
      let nextCol = col + dc;
      while (inside(nextRow, nextCol)) {
        const piece = pieceAt(pos, nextRow, nextCol);
        if (piece) {
          if (piece.color === byColor && ray.types.includes(piece.type)) return true;
          break;
        }
        nextRow += dr;
        nextCol += dc;
      }
    }
  }
  return false;
}

function isInCheck(pos, color) {
  const king = findKing(pos, color);
  return king ? isSquareAttacked(pos, king.row, king.col, color === "w" ? "b" : "w") : false;
}

function pseudoMoves(pos, row, col) {
  const piece = pieceAt(pos, row, col);
  if (!piece) return [];
  const moves = [];
  const addIfAvailable = (toRow, toCol) => {
    if (!inside(toRow, toCol)) return false;
    const target = pieceAt(pos, toRow, toCol);
    if (!target) {
      moves.push({ fromRow: row, fromCol: col, toRow, toCol });
      return true;
    }
    if (target.color !== piece.color && target.type !== "k") {
      moves.push({ fromRow: row, fromCol: col, toRow, toCol });
    }
    return false;
  };

  if (piece.type === "p") {
    const direction = piece.color === "w" ? -1 : 1;
    const startRow = piece.color === "w" ? 6 : 1;
    if (inside(row + direction, col) && !pieceAt(pos, row + direction, col)) {
      moves.push({ fromRow: row, fromCol: col, toRow: row + direction, toCol: col });
      if (row === startRow && !pieceAt(pos, row + 2 * direction, col)) {
        moves.push({ fromRow: row, fromCol: col, toRow: row + 2 * direction, toCol: col, doublePawn: true });
      }
    }
    for (const dc of [-1, 1]) {
      const targetRow = row + direction;
      const targetCol = col + dc;
      if (!inside(targetRow, targetCol)) continue;
      const target = pieceAt(pos, targetRow, targetCol);
      if (target && target.color !== piece.color && target.type !== "k") {
        moves.push({ fromRow: row, fromCol: col, toRow: targetRow, toCol: targetCol });
      } else if (pos.enPassant && pos.enPassant.row === targetRow && pos.enPassant.col === targetCol) {
        moves.push({ fromRow: row, fromCol: col, toRow: targetRow, toCol: targetCol, enPassant: true });
      }
    }
  } else if (piece.type === "n") {
    for (const [dr, dc] of [[-2, -1], [-2, 1], [-1, -2], [-1, 2], [1, -2], [1, 2], [2, -1], [2, 1]]) {
      addIfAvailable(row + dr, col + dc);
    }
  } else if (piece.type === "k") {
    for (let dr = -1; dr <= 1; dr += 1) {
      for (let dc = -1; dc <= 1; dc += 1) {
        if (dr !== 0 || dc !== 0) addIfAvailable(row + dr, col + dc);
      }
    }
    const homeRow = piece.color === "w" ? 7 : 0;
    const enemy = piece.color === "w" ? "b" : "w";
    if (row === homeRow && col === 4 && !piece.moved && !isInCheck(pos, piece.color)) {
      const kingRook = pieceAt(pos, homeRow, 7);
      if (kingRook && kingRook.type === "r" && kingRook.color === piece.color && !kingRook.moved &&
          !pieceAt(pos, homeRow, 5) && !pieceAt(pos, homeRow, 6) &&
          !isSquareAttacked(pos, homeRow, 5, enemy) && !isSquareAttacked(pos, homeRow, 6, enemy)) {
        moves.push({ fromRow: row, fromCol: col, toRow: homeRow, toCol: 6, castle: "king" });
      }
      const queenRook = pieceAt(pos, homeRow, 0);
      if (queenRook && queenRook.type === "r" && queenRook.color === piece.color && !queenRook.moved &&
          !pieceAt(pos, homeRow, 1) && !pieceAt(pos, homeRow, 2) && !pieceAt(pos, homeRow, 3) &&
          !isSquareAttacked(pos, homeRow, 3, enemy) && !isSquareAttacked(pos, homeRow, 2, enemy)) {
        moves.push({ fromRow: row, fromCol: col, toRow: homeRow, toCol: 2, castle: "queen" });
      }
    }
  } else {
    const directions = [];
    if (piece.type === "r" || piece.type === "q") directions.push([-1, 0], [1, 0], [0, -1], [0, 1]);
    if (piece.type === "b" || piece.type === "q") directions.push([-1, -1], [-1, 1], [1, -1], [1, 1]);
    for (const [dr, dc] of directions) {
      let nextRow = row + dr;
      let nextCol = col + dc;
      while (inside(nextRow, nextCol)) {
        if (!addIfAvailable(nextRow, nextCol)) break;
        nextRow += dr;
        nextCol += dc;
      }
    }
  }
  return moves;
}

function applyMove(pos, move, promotion = "q") {
  const piece = pos.board[move.fromRow][move.fromCol];
  const captured = move.enPassant
    ? pos.board[move.fromRow][move.toCol]
    : pos.board[move.toRow][move.toCol];
  pos.board[move.fromRow][move.fromCol] = null;
  if (move.enPassant) pos.board[move.fromRow][move.toCol] = null;
  if (move.castle) {
    const rookFrom = move.castle === "king" ? 7 : 0;
    const rookTo = move.castle === "king" ? 5 : 3;
    const rook = pos.board[move.fromRow][rookFrom];
    pos.board[move.fromRow][rookFrom] = null;
    pos.board[move.fromRow][rookTo] = { ...rook, moved: true };
  }
  const movedPiece = { ...piece, moved: true };
  if (piece.type === "p" && (move.toRow === 0 || move.toRow === 7)) movedPiece.type = promotion;
  pos.board[move.toRow][move.toCol] = movedPiece;
  pos.enPassant = move.doublePawn
    ? { row: (move.fromRow + move.toRow) / 2, col: move.fromCol }
    : null;
  pos.halfmove = piece.type === "p" || captured ? 0 : pos.halfmove + 1;
  if (piece.color === "b") pos.fullmove += 1;
  pos.turn = piece.color === "w" ? "b" : "w";
  return { captured, movingPiece: piece };
}

function legalMoves(pos, row, col) {
  const piece = pieceAt(pos, row, col);
  if (!piece || piece.color !== pos.turn) return [];
  return pseudoMoves(pos, row, col).filter(move => {
    const test = clonePosition(pos);
    applyMove(test, move);
    return !isInCheck(test, piece.color);
  });
}

function allLegalMoves(pos, color) {
  const savedTurn = pos.turn;
  pos.turn = color;
  const moves = [];
  for (let row = 0; row < 8; row += 1) {
    for (let col = 0; col < 8; col += 1) {
      const piece = pieceAt(pos, row, col);
      if (piece && piece.color === color) moves.push(...legalMoves(pos, row, col));
    }
  }
  pos.turn = savedTurn;
  return moves;
}

function castlingRights(pos) {
  let rights = "";
  for (const [color, homeRow] of [["w", 7], ["b", 0]]) {
    const king = pos.board[homeRow][4];
    if (!king || king.type !== "k" || king.color !== color || king.moved) continue;
    for (const [rookCol, flag] of [[7, "k"], [0, "q"]]) {
      const rook = pos.board[homeRow][rookCol];
      if (rook && rook.type === "r" && rook.color === color && !rook.moved) {
        rights += color === "w" ? flag.toUpperCase() : flag;
      }
    }
  }
  return rights || "-";
}

// Repetition compares placement, side to move, castling rights and a usable en passant square (FIDE 9.2).
function positionKey(pos) {
  const pieces = pos.board.map(row => row.map(piece => piece ? `${piece.color}${piece.type}` : ".").join("")).join("/");
  let ep = "-";
  if (pos.enPassant) {
    const pawnRow = pos.enPassant.row + (pos.turn === "w" ? 1 : -1);
    const canTake = [pos.enPassant.col - 1, pos.enPassant.col + 1].some(col => {
      const pawn = pieceAt(pos, pawnRow, col);
      return pawn && pawn.type === "p" && pawn.color === pos.turn &&
        legalMoves(pos, pawnRow, col).some(move => move.enPassant);
    });
    if (canTake) ep = `${pos.enPassant.row}${pos.enPassant.col}`;
  }
  return `${pieces} ${pos.turn} ${castlingRights(pos)} ${ep}`;
}

function hasInsufficientMaterial(pos) {
  const remaining = [];
  for (const row of pos.board) {
    for (const piece of row) {
      if (piece && piece.type !== "k") remaining.push(piece);
    }
  }
  if (remaining.length === 0) return true;
  if (remaining.some(piece => piece.type === "p" || piece.type === "r" || piece.type === "q")) return false;
  if (remaining.length === 1) return true;
  return remaining.every(piece => piece.type === "b") &&
    new Set(pos.board.flatMap((row, r) => row.map((piece, c) =>
      piece && piece.type === "b" ? (r + c) % 2 : null
    ).filter(square => square !== null))).size === 1;
}

function algebraic(row, col) {
  return `${FILES[col]}${8 - row}`;
}

// `before` is the position prior to the move, used to disambiguate e.g. Nbd2 vs Nfd2.
function moveNotation(before, move, piece, captured, promotion, suffix) {
  if (move.castle) return `${move.castle === "king" ? "O-O" : "O-O-O"}${suffix}`;
  const destination = algebraic(move.toRow, move.toCol);
  if (piece.type === "p") {
    return `${captured ? FILES[move.fromCol] + "x" : ""}${destination}${promotion ? `=${promotion.toUpperCase()}` : ""}${suffix}`;
  }
  let origin = "";
  if (piece.type !== "k") {
    const rivals = allLegalMoves(before, piece.color).filter(other =>
      other.toRow === move.toRow && other.toCol === move.toCol &&
      (other.fromRow !== move.fromRow || other.fromCol !== move.fromCol) &&
      before.board[other.fromRow][other.fromCol].type === piece.type);
    if (rivals.length) {
      if (!rivals.some(other => other.fromCol === move.fromCol)) origin = FILES[move.fromCol];
      else if (!rivals.some(other => other.fromRow === move.fromRow)) origin = String(8 - move.fromRow);
      else origin = algebraic(move.fromRow, move.fromCol);
    }
  }
  return `${piece.type.toUpperCase()}${origin}${captured ? "x" : ""}${destination}${suffix}`;
}


// ---- Computer opponent: negamax with alpha-beta, MVV-LVA ordering and a capture-only quiescence search.
const PIECE_VALUES = { p: 100, n: 320, b: 330, r: 500, q: 900, k: 0 };
// Piece-square tables from Ivory's point of view, row 0 = rank 8. Onyx reads them mirrored.
const PST = {
  p: [[0,0,0,0,0,0,0,0],[50,50,50,50,50,50,50,50],[10,10,20,30,30,20,10,10],[5,5,10,25,25,10,5,5],[0,0,0,20,20,0,0,0],[5,-5,-10,0,0,-10,-5,5],[5,10,10,-20,-20,10,10,5],[0,0,0,0,0,0,0,0]],
  n: [[-50,-40,-30,-30,-30,-30,-40,-50],[-40,-20,0,0,0,0,-20,-40],[-30,0,10,15,15,10,0,-30],[-30,5,15,20,20,15,5,-30],[-30,0,15,20,20,15,0,-30],[-30,5,10,15,15,10,5,-30],[-40,-20,0,5,5,0,-20,-40],[-50,-40,-30,-30,-30,-30,-40,-50]],
  b: [[-20,-10,-10,-10,-10,-10,-10,-20],[-10,0,0,0,0,0,0,-10],[-10,0,5,10,10,5,0,-10],[-10,5,5,10,10,5,5,-10],[-10,0,10,10,10,10,0,-10],[-10,10,10,10,10,10,10,-10],[-10,5,0,0,0,0,5,-10],[-20,-10,-10,-10,-10,-10,-10,-20]],
  r: [[0,0,0,0,0,0,0,0],[5,10,10,10,10,10,10,5],[-5,0,0,0,0,0,0,-5],[-5,0,0,0,0,0,0,-5],[-5,0,0,0,0,0,0,-5],[-5,0,0,0,0,0,0,-5],[-5,0,0,0,0,0,0,-5],[0,0,0,5,5,0,0,0]],
  q: [[-20,-10,-10,-5,-5,-10,-10,-20],[-10,0,0,0,0,0,0,-10],[-10,0,5,5,5,5,0,-10],[-5,0,5,5,5,5,0,-5],[0,0,5,5,5,5,0,-5],[-10,5,5,5,5,5,0,-10],[-10,0,5,0,0,0,0,-10],[-20,-10,-10,-5,-5,-10,-10,-20]],
  k: [[-30,-40,-40,-50,-50,-40,-40,-30],[-30,-40,-40,-50,-50,-40,-40,-30],[-30,-40,-40,-50,-50,-40,-40,-30],[-30,-40,-40,-50,-50,-40,-40,-30],[-20,-30,-30,-40,-40,-30,-30,-20],[-10,-20,-20,-20,-20,-20,-20,-10],[20,20,0,0,0,0,20,20],[20,30,10,0,0,10,30,20]]
};
const MATE_SCORE = 100000;

function evaluate(pos) {
  let score = 0;
  for (let row = 0; row < 8; row += 1) {
    for (let col = 0; col < 8; col += 1) {
      const piece = pos.board[row][col];
      if (!piece) continue;
      const value = PIECE_VALUES[piece.type] + PST[piece.type][piece.color === "w" ? row : 7 - row][col];
      score += piece.color === "w" ? value : -value;
    }
  }
  return pos.turn === "w" ? score : -score;
}

function capturedBy(pos, move) {
  return move.enPassant ? pos.board[move.fromRow][move.toCol] : pos.board[move.toRow][move.toCol];
}

function orderMoves(pos, moves) {
  const score = move => {
    const victim = capturedBy(pos, move);
    const attacker = pos.board[move.fromRow][move.fromCol];
    let value = victim ? 10 * PIECE_VALUES[victim.type] - PIECE_VALUES[attacker.type] + 10000 : 0;
    if (attacker.type === "p" && (move.toRow === 0 || move.toRow === 7)) value += 9000;
    return value;
  };
  return moves.map(move => ({ move, value: score(move) }))
    .sort((a, b) => b.value - a.value)
    .map(entry => entry.move);
}

function quiesce(pos, alpha, beta, depthLeft) {
  const standPat = evaluate(pos);
  if (standPat >= beta || depthLeft === 0) return standPat;
  if (standPat > alpha) alpha = standPat;
  const captures = orderMoves(pos, allLegalMoves(pos, pos.turn).filter(move => capturedBy(pos, move)));
  for (const move of captures) {
    const next = clonePosition(pos);
    applyMove(next, move);
    const score = -quiesce(next, -beta, -alpha, depthLeft - 1);
    if (score >= beta) return score;
    if (score > alpha) alpha = score;
  }
  return alpha;
}

function negamax(pos, depth, alpha, beta, ply) {
  const moves = allLegalMoves(pos, pos.turn);
  if (!moves.length) return isInCheck(pos, pos.turn) ? -MATE_SCORE + ply : 0;
  if (pos.halfmove >= 100 || hasInsufficientMaterial(pos)) return 0;
  if (depth === 0) return quiesce(pos, alpha, beta, 4);
  let best = -Infinity;
  for (const move of orderMoves(pos, moves)) {
    const next = clonePosition(pos);
    applyMove(next, move);
    const score = -negamax(next, depth - 1, -beta, -alpha, ply + 1);
    if (score > best) best = score;
    if (score > alpha) alpha = score;
    if (alpha >= beta) break;
  }
  return best;
}

// `counts` maps positionKey -> times seen this game, so the AI can avoid drifting into repetition draws.
function chooseAiMove(pos, depth, counts = new Map()) {
  // Recruits are deliberately sloppy: a little noise makes them beatable.
  const noise = depth === 1 ? 60 : depth === 2 ? 12 : 0;
  let best = null;
  let bestScore = -Infinity;
  let alpha = -Infinity;
  for (const move of orderMoves(pos, allLegalMoves(pos, pos.turn))) {
    const next = clonePosition(pos);
    applyMove(next, move);
    const repeats = counts.get(positionKey(next)) || 0;
    let score = -negamax(next, depth - 1, -Infinity, -alpha, 1) + (Math.random() - 0.5) * noise;
    if (repeats >= 2 && score > 0) score = 0;
    if (score > bestScore) {
      bestScore = score;
      best = move;
    }
    if (score > alpha && !noise) alpha = score;
  }
  return best;
}
