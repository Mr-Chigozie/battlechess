"use strict";

// The server runs the very same rules file as the game page (js/engine.js), so both always agree
// on what is legal and how a battle ends. The browser scripts define globals; we load them into a sandbox.
const fs = require("fs");
const vm = require("vm");
const config = require("./config");

function load(file, names) {
  const context = vm.createContext({});
  return vm.runInContext(`${fs.readFileSync(file, "utf8")}\n;({ ${names.join(", ")} });`, context, { filename: file });
}

const E = load(config.enginePath, ["initialPosition", "legalMoves", "allLegalMoves", "applyMove", "clonePosition", "isInCheck",
  "positionKey", "hasInsufficientMaterial", "moveNotation"]);
const { ARMIES } = load(config.armiesPath, ["ARMIES"]);

const PROMOTIONS = new Set(["q", "r", "b", "n"]);
const isSquare = n => Number.isInteger(n) && n >= 0 && n < 8;

// Rebuilds a battle from its move list.
function replay(moves) {
  const state = { pos: E.initialPosition(), counts: new Map(), ply: 0 };
  state.counts.set(E.positionKey(state.pos), 1);
  for (const m of moves) {
    const out = play(state, m);
    if (out.error) throw new Error(`stored move ${state.ply} is illegal`);
  }
  return state;
}

// Plays one move on a replayed state. Returns { notation, end } or { error }.
// `end` is null, or { result: "w" | "b" | "draw", reason } when the move ends the battle.
function play(state, m) {
  if (!m || ![m.fromRow, m.fromCol, m.toRow, m.toCol].every(isSquare)) return { error: "bad-move" };
  const { pos } = state;
  const legal = E.legalMoves(pos, m.fromRow, m.fromCol).find(x => x.toRow === m.toRow && x.toCol === m.toCol);
  if (!legal) return { error: "illegal" };
  const piece = pos.board[m.fromRow][m.fromCol];
  const promoting = piece.type === "p" && (m.toRow === 0 || m.toRow === 7);
  const promotion = promoting ? (PROMOTIONS.has(m.promotion) ? m.promotion : "q") : null;
  const before = E.clonePosition(pos);
  const { captured } = E.applyMove(pos, legal, promotion || "q");
  state.ply += 1;
  const key = E.positionKey(pos);
  state.counts.set(key, (state.counts.get(key) || 0) + 1);
  const check = E.isInCheck(pos, pos.turn);
  const replies = E.allLegalMoves(pos, pos.turn);
  const notation = E.moveNotation(before, legal, piece, captured, promotion, check ? (replies.length ? "+" : "#") : "");
  let end = null;
  if (!replies.length) end = check ? { result: piece.color, reason: "checkmate" } : { result: "draw", reason: "stalemate" };
  else if (state.counts.get(key) >= 3) end = { result: "draw", reason: "repetition" };
  else if (pos.halfmove >= 100) end = { result: "draw", reason: "fifty-move" };
  else if (E.hasInsufficientMaterial(pos)) end = { result: "draw", reason: "insufficient" };
  return { move: { fromRow: m.fromRow, fromCol: m.fromCol, toRow: m.toRow, toCol: m.toCol, promotion }, notation, end };
}

const isArmy = id => Object.prototype.hasOwnProperty.call(ARMIES, id);
const isTaunt = (army, text) => isArmy(army) && ARMIES[army].taunts.includes(text);

module.exports = { replay, play, isArmy, isTaunt, ARMIES };
