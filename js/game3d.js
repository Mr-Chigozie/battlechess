"use strict";

// Game flow for the 3D battle: turns, AI, HUD, story campaign and skirmish setup.
(() => {
  const $ = id => document.getElementById(id);
  const STORY_KEY = "chesswars.story.v1";
  const SETTINGS_KEY = "chesswars.settings.v1";
  const CHAPTER_ORDER = ["marines", "vikings", "spartans", "samurai", "knights", "undead", "asgardians", "machines"];
  const CHAPTER_DEPTH = [1, 1, 2, 2, 2, 3, 3];
  const RANKS = { 1: "Recruit", 2: "Veteran", 3: "Warlord" };
  const VALUE = { p: 1, n: 3, b: 3, r: 5, q: 9, k: 0 };
  const STREAK_NAMES = [null, null, "Double Kill", "Triple Kill", "Rampage", "Massacre", "Unstoppable", "Godlike"];
  const FURY = [[0.75, "Inferno"], [0.45, "Burning"], [0.2, "Clash"], [0, "Calm"]];

  let position = initialPosition();
  let history = [];
  let moveLog = [];
  let positionCounts = new Map();
  let armies = { w: "knights", b: "undead" };
  let aiColor = null;
  let aiDepth = 2;
  let gameOver = false;
  let busy = false;
  let aiThinking = false;
  let token = 0;
  let selected = null;
  let targets = [];
  let lastMove = null;
  let paused = false;
  let streak = { w: 0, b: 0 }; // consecutive turns on which each side captured
  let lastFury = 0;
  let furyNews = null;
  let online = null; // { gameId, myColor, opponent, game, recorded } during an online battle
  let remoteMoves = [];
  let lastTauntAt = 0;
  let queuedOrder = null; // a move given while the previous one was still animating
  let comboTimer = null;
  let battle = null; // { mode: "story" | "skirmish", chapter, enemy, title, sub, setup }

  // ---------------------------------------------------------------- storage
  const load = (key, fallback) => {
    try { return JSON.parse(localStorage.getItem(key)) ?? fallback; } catch (error) { return fallback; }
  };
  const save = (key, value) => {
    try { localStorage.setItem(key, JSON.stringify(value)); } catch (error) { /* progress just won't persist */ }
  };
  let story = load(STORY_KEY, null);
  const settings = { cinematics: true, sound: true, blood: true, ...load(SETTINGS_KEY, {}) };

  // ---------------------------------------------------------------- naming
  const army = color => ARMIES[armies[color]];
  const sideName = color => armies.w === armies.b ? (color === "w" ? "Ivory" : "Onyx") : army(color).name;
  const unitName = (color, type) => army(color).units[type];
  const pick = list => list[Math.floor(Math.random() * list.length)];

  // ---------------------------------------------------------------- screens
  function showScreen(id) {
    for (const screen of document.querySelectorAll(".screen")) screen.classList.toggle("open", screen.id === id);
    $("unit-tip").classList.remove("show");
    $("hud").classList.toggle("open", !id);
  }

  function showCard({ eyebrow = "", title, paragraphs = [], quote = null, buttons }) {
    $("card-eyebrow").textContent = eyebrow;
    $("card-title").textContent = title;
    const body = $("card-body");
    body.replaceChildren();
    for (const text of paragraphs) {
      const p = document.createElement("p");
      p.textContent = text;
      body.appendChild(p);
    }
    if (quote) {
      const p = document.createElement("p");
      p.className = "quote";
      p.textContent = quote;
      body.appendChild(p);
    }
    const row = $("card-buttons");
    row.replaceChildren();
    for (const { label, action, primary } of buttons) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `btn${primary ? " primary" : ""}`;
      button.textContent = label;
      button.addEventListener("click", action);
      row.appendChild(button);
    }
    showScreen("card-screen");
  }

  // On phones the narration fades back after a few seconds so it doesn't sit on the board (see .dim).
  let narrTimer = null;
  function narrate(label, text) {
    $("narr-label").textContent = label;
    $("narr-text").textContent = text;
    const box = $("narr-text").parentElement;
    box.classList.remove("dim");
    clearTimeout(narrTimer);
    narrTimer = setTimeout(() => box.classList.add("dim"), 5000);
  }

  // ---------------------------------------------------------------- HUD
  function updateHud() {
    if (battle) {
      $("hud-title").textContent = battle.title;
      $("hud-sub").textContent = battle.sub;
    }
    const banner = $("turn-banner");
    const inCheck = !gameOver && isInCheck(position, position.turn);
    const waiting = online && position.turn !== online.myColor;
    banner.textContent = gameOver ? gameOver
      : aiThinking ? `${sideName(position.turn)} is plotting…`
      : online ? (waiting ? `${online.opponent} is thinking…` : `Your move${inCheck ? " — check!" : ""}`)
      : `${sideName(position.turn)} to move${inCheck ? " — check!" : ""}`;
    banner.classList.toggle("thinking", aiThinking || Boolean(waiting && !gameOver));
    banner.classList.toggle("check", inCheck);
    $("btn-undo").disabled = busy || aiThinking || !history.length || Boolean(online);

    for (const color of ["w", "b"]) {
      $(`cap-${color}-label`).textContent = `${sideName(color)} losses`;
      $(`cap-${color}`).textContent = history.filter(h => h.captured && h.captured.color === color)
        .map(h => army(color).pieces[h.captured.type]).join(" ") || "—";
    }
    if (typeof Fort !== "undefined") {
      const share = Fort.share();
      const hex = color => `#${new THREE.Color(army(color).look.magic).getHexString()}`;
      Object.assign($("terr-w").style, { width: `${share.w}%`, background: hex("w") });
      Object.assign($("terr-b").style, { width: `${share.b}%`, background: hex("b"), marginLeft: "auto" });
      $("terr-label").textContent = `${sideName("w")} ${share.w}% · ${sideName("b")} ${share.b}%`;
    }
    const log = $("move-log");
    log.replaceChildren();
    for (let i = 0; i < moveLog.length; i += 2) {
      const row = document.createElement("div");
      row.className = "row";
      for (const text of [`${i / 2 + 1}.`, moveLog[i], moveLog[i + 1] || ""]) {
        const cell = document.createElement("span");
        cell.textContent = text;
        if (text.endsWith(".")) cell.className = "n";
        row.appendChild(cell);
      }
      log.appendChild(row);
    }
    log.scrollTop = log.scrollHeight;

    const king = findKing(position, position.turn);
    Stage.setMarkers({ selected, targets, lastMove, check: inCheck ? king : null });
    updateFury();
  }

  // ---------------------------------------------------------------- combos & battle fury
  // Fury climbs with material lost, recent bloodshed, checks and kill streaks; it drives the fire on the board.
  function tension() {
    const lost = history.reduce((sum, h) => sum + (h.captured ? VALUE[h.captured.type] : 0), 0);
    const recent = history.slice(-6).filter(h => h.captured).length;
    const check = isInCheck(position, position.turn) ? 0.2 : 0;
    return Math.min(1, lost / 40 + recent * 0.07 + check + Math.max(streak.w, streak.b) * 0.06);
  }

  function updateFury() {
    const fury = battle ? tension() : 0;
    War.setTension(fury);
    $("fury-fill").style.width = `${Math.round(fury * 100)}%`;
    const label = $("fury-label");
    label.textContent = FURY.find(([min]) => fury >= min)[1];
    label.classList.toggle("hot", fury >= 0.45);
    // Announced once the current move finishes, so the kill narration doesn't bury it.
    if (fury >= 0.75 && lastFury < 0.75) {
      FX.sfx("drums");
      furyNews = ["Inferno", "The whole field is ablaze. Only one army will walk out of this fire."];
    } else if (fury >= 0.45 && lastFury < 0.45) {
      FX.sfx("drums");
      furyNews = ["The board burns", "Fire spreads across the battlefield. This is war."];
    }
    lastFury = fury;
  }

  // Works out the combo for a capture. Must run before the move is pushed onto history.
  function comboFor(mover, victim, victimSq, check) {
    streak[mover.color] += 1;
    const tags = [];
    // Ordered by prestige: the first tag becomes the headline when there's no streak.
    if (!history.some(h => h.captured)) tags.push("First Blood");
    if (VALUE[victim.type] - VALUE[mover.type] >= 4) tags.push("Giant Slayer");
    const prev = history[history.length - 1];
    if (prev?.captured && lastMove && lastMove.toRow === victimSq.row && lastMove.toCol === victimSq.col) tags.push("Revenge");
    if (check) tags.push("With check");
    const count = streak[mover.color];
    const title = count >= 2 ? STREAK_NAMES[Math.min(count, STREAK_NAMES.length - 1)] : tags.shift() || null;
    if (!title) return null;
    return { title, tags, level: count >= 2 ? count - 1 : 1, count };
  }

  function showCombo(combo) {
    const el = $("combo");
    $("combo-title").textContent = combo.title;
    $("combo-tags").textContent = [combo.count >= 2 ? `${combo.count} kills in a row` : null, ...combo.tags].filter(Boolean).join("  ·  ");
    el.className = `combo show level-${Math.min(combo.level, 4)}`;
    FX.sfx("combo", combo.level);
    clearTimeout(comboTimer);
    comboTimer = setTimeout(() => el.classList.remove("show"), 2300);
  }

  // ---------------------------------------------------------------- battle lifecycle
  function startBattle(setup) {
    const { white, black, ai, depth, theme, title, sub, mode, chapter = null, enemy = null, moves = [] } = setup;
    token += 1;
    queuedOrder = null;
    online = setup.online ? { ...setup.online } : null;
    if (!online) $("clocks").hidden = true;
    remoteMoves = moves;
    lastTauntAt = Date.now();
    paused = false;
    armies = { w: white, b: black };
    aiColor = ai;
    aiDepth = depth;
    battle = { mode, chapter, enemy, title, sub, setup };
    position = initialPosition();
    history = [];
    moveLog = [];
    positionCounts = new Map([[positionKey(position), 1]]);
    gameOver = false;
    busy = false;
    aiThinking = false;
    selected = null;
    targets = [];
    lastMove = null;
    streak = { w: 0, b: 0 };
    lastFury = 0;
    Stage.setTheme(theme);
    // Resuming an online battle: replay the moves so far without animation.
    for (const m of moves) {
      const legal = matchMove(m);
      if (!legal) break;
      record(legal, m.promotion || "q");
      lastMove = legal;
    }
    Stage.syncAll(position, armies);
    Stage.setView(online ? online.myColor : aiColor === "w" ? "b" : "w", false);
    battle.sub += ` · ${Terrain.name}`;
    if (online) {
      narrate("Online battle", `You command ${sideName(online.myColor)} as ${online.myColor === "w" ? "Ivory, moving first" : "Onyx"}, against ${online.opponent}.${moves.length ? " The battle resumes." : ""}`);
    } else {
      narrate("The opening", `${sideName("w")} faces ${sideName("b")} on ${Terrain.name}. ${Stage.touch ? "Tap a unit to give orders, hold it to read it. Pinch or twist with two fingers to look around." : "Click one of your units to give orders. Hover any unit to read it. Drag to look around."}`);
    }
    showScreen(null);
    updateHud();
    maybeAi();
  }

  // While the previous move is still animating, the next player can already pick a unit and give
  // an order; it runs the moment the board is free (quick taps on phones would otherwise be lost).
  function onSquare(clicked, ground = null) {
    if (gameOver || aiThinking || position.turn === aiColor) return;
    if (online && position.turn !== online.myColor) return;
    const at = s => s && targets.find(m => m.toRow === s.row && m.toCol === s.col);
    // A unit standing in front of a highlighted square doesn't swallow the order meant for that square.
    const sq = selected && !at(clicked) && at(ground) ? ground : clicked;
    const move = at(sq);
    if (selected && move) {
      const piece = position.board[selected.row][selected.col];
      const order = () => {
        if (piece.type === "p" && (move.toRow === 0 || move.toRow === 7)) askPromotion(piece.color).then(type => execute(move, type));
        else execute(move);
      };
      if (busy) {
        queuedOrder = order;
        FX.sfx("select");
        narrate("Orders given", `The ${unitName(piece.color, piece.type)} moves out as soon as the field is clear.`);
      } else {
        order();
      }
      return;
    }
    const piece = position.board[sq.row][sq.col];
    if (piece && piece.color === position.turn) {
      selected = sq;
      targets = legalMoves(position, sq.row, sq.col);
      FX.sfx("select");
      const unit = unitName(piece.color, piece.type);
      narrate(`${sideName(piece.color)} ${unit}`, targets.length
        ? `Moves like a ${ROLE_NAMES[piece.type]}. ${targets.length} order${targets.length === 1 ? "" : "s"} available.`
        : `The ${unit} has nowhere to go.`);
    } else {
      selected = null;
      targets = [];
    }
    updateHud();
  }

  function askPromotion(color) {
    return new Promise(resolve => {
      $("promo-text").textContent = `Your ${unitName(color, "p")} reached the far line. Choose what it becomes.`;
      const row = $("promo-buttons");
      row.replaceChildren();
      for (const type of ["q", "r", "b", "n"]) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = `btn${type === "q" ? " primary" : ""}`;
        button.textContent = `${army(color).pieces[type]} ${unitName(color, type)}`;
        button.title = ROLE_NAMES[type];
        button.addEventListener("click", () => { showScreen(null); resolve(type); });
        row.appendChild(button);
      }
      showScreen("promo-screen");
    });
  }

  // Applies a move to the game state (history, notation, streaks) without any animation.
  function record(move, promotion) {
    const snapshot = clonePosition(position);
    const mover = snapshot.board[move.fromRow][move.fromCol];
    const victimSq = move.enPassant ? { row: move.fromRow, col: move.toCol }
      : snapshot.board[move.toRow][move.toCol] ? { row: move.toRow, col: move.toCol } : null;
    const victim = victimSq ? snapshot.board[victimSq.row][victimSq.col] : null;
    const promoted = mover.type === "p" && (move.toRow === 0 || move.toRow === 7);

    const savedStreak = { ...streak };
    const result = applyMove(position, move, promotion);
    const check = isInCheck(position, position.turn);
    const combo = victim ? comboFor(mover, victim, victimSq, check) : null;
    if (!victim) streak[mover.color] = 0;
    history.push({ position: snapshot, captured: result.captured, moveLogLength: moveLog.length, lastMove, streak: savedStreak });
    const key = positionKey(position);
    positionCounts.set(key, (positionCounts.get(key) || 0) + 1);
    const replies = allLegalMoves(position, position.turn);
    moveLog.push(moveNotation(snapshot, move, mover, result.captured, promoted ? promotion : null, check ? (replies.length ? "+" : "#") : ""));
    return { mover, victimSq, victim, promoted, check, combo, replies, key };
  }

  // Find the engine's legal move matching a plain {fromRow, fromCol, toRow, toCol} (e.g. from the server).
  const matchMove = m => legalMoves(position, m.fromRow, m.fromCol).find(x => x.toRow === m.toRow && x.toCol === m.toCol);

  async function execute(move, promotion = "q", remote = false) {
    const myToken = token;
    busy = true;
    selected = null;
    targets = [];
    const { mover, victimSq, victim, promoted, check, combo, replies, key } = record(move, promotion);
    if (online && !remote) {
      Online.sendMove(online.gameId, moveLog.length - 1, {
        fromRow: move.fromRow, fromCol: move.fromCol, toRow: move.toRow, toCol: move.toCol, promotion: promoted ? promotion : null
      });
    }
    updateHud();

    const style = Models.attackStyle(armies[mover.color], mover.type);
    const killGround = victim ? Terrain.at(victimSq.row, victimSq.col) : null;
    const caption = victim ? {
      who: `${sideName(mover.color)} ${unitName(mover.color, mover.type)}  vs  ${sideName(victim.color)} ${unitName(victim.color, victim.type)}`,
      text: `The ${unitName(mover.color, mover.type)} ${Stage.killVerb(style)} the ${unitName(victim.color, victim.type)}${killGround ? ` ${killGround.label}` : ""}!`,
      onImpact: combo ? () => showCombo(combo) : null
    } : null;
    if (victim) narrate(combo ? combo.title : "Battle", caption.text);
    try {
      await Stage.playMove({
        move, victimSq, caption, combo: combo ? combo.level : 0,
        promote: promoted ? () => Models.buildUnit(armies[mover.color], promotion, mover.color) : null
      });
    } catch (error) {
      console.error(error);
      Stage.syncAll(position, armies);
    }
    if (myToken !== token) return;
    lastMove = move;

    const to = `${FILES[move.toCol]}${8 - move.toRow}`;
    const enemyKing = `${sideName(position.turn)} ${unitName(position.turn, "k")}`;
    if (!replies.length && check) {
      gameOver = `${sideName(mover.color)} wins`;
      narrate("Checkmate", `The ${enemyKing} is trapped. ${sideName(mover.color)} claims the board.`);
      updateHud();
      await Stage.playCheckmate(findKing(position, position.turn));
      return finish(mover.color, "checkmate");
    }
    let draw = null;
    if (!replies.length) draw = "Stalemate. Neither side can make a legal move.";
    else if (positionCounts.get(key) >= 3) draw = "The same position has come up three times. The battle is a draw.";
    else if (position.halfmove >= 100) draw = "Fifty moves with no capture or pawn move. The battle is a draw.";
    else if (hasInsufficientMaterial(position)) draw = "Neither side has enough left to force checkmate.";
    if (draw) {
      gameOver = "Draw";
      narrate("Draw", draw);
      updateHud();
      return finish(null, draw);
    }

    if (check) {
      FX.sfx("check");
      narrate("Check", `The ${enemyKing} is under attack!`);
    } else if (promoted) {
      narrate("A new champion", `The ${unitName(mover.color, "p")} rises as a ${unitName(mover.color, promotion)} on ${to}.`);
    } else if (move.castle) {
      narrate("The line holds", `The ${unitName(mover.color, "k")} retreats behind the ${unitName(mover.color, "r")}.`);
    } else if (!victim) {
      const ground = Terrain.at(move.toRow, move.toCol);
      narrate("Orders carried out", `${sideName(mover.color)} ${unitName(mover.color, mover.type)} advances to ${to}${ground ? `, ${ground.label}` : ""}.`);
    }
    if (furyNews) {
      narrate(...furyNews);
      furyNews = null;
    }
    busy = false;
    updateHud();
    if (queuedOrder) {
      const order = queuedOrder;
      queuedOrder = null;
      order();
      return;
    }
    maybeAi();
    pumpRemote();
  }

  // ---------------------------------------------------------------- online play
  // Plays the opponent's next move from the server, one at a time, once the board is idle.
  function pumpRemote() {
    if (!online || busy || gameOver) return;
    const next = remoteMoves[moveLog.length];
    if (!next) return;
    const legal = matchMove(next);
    if (!legal) {
      Online.toast("The boards disagree. Resyncing…");
      Online.joinGame(online.gameId);
      return;
    }
    execute(legal, next.promotion || "q", true);
  }

  function onGame(doc) {
    if (!online || doc.id !== online.gameId) return;
    online.game = doc;
    remoteMoves = doc.moves;
    pumpRemote();
    if (doc.taunt && doc.taunt.by !== Online.uid && doc.taunt.at > lastTauntAt) {
      lastTauntAt = doc.taunt.at;
      narrate(`${online.opponent} says`, `“${doc.taunt.text}”`);
      FX.sfx("select");
    }
    // Draw offers from the rival.
    if (doc.status === "active" && doc.drawOffer && doc.drawOffer !== online.myColor && online.seenOffer !== doc.moves.length) {
      online.seenOffer = doc.moves.length;
      narrate("Parley", `${online.opponent} offers a draw.`);
      showCard({
        eyebrow: "Draw offer",
        title: `${online.opponent} offers a draw`,
        paragraphs: ["Accept and the battle ends level. Decline and fight on."],
        buttons: [
          { label: "Decline", action: () => { showScreen(null); Online.draw(online.gameId, "decline"); } },
          { label: "Accept draw", primary: true, action: () => { showScreen(null); Online.draw(online.gameId, "accept"); } }
        ]
      });
    }
    // Endings the server decides that aren't on the board: resignation, time, agreement, a battle called off.
    // (Checkmate and the drawn positions are found by replaying the final move here.)
    const offBoard = { resign: true, timeout: true, agreement: true, aborted: true };
    if (doc.status === "finished" && offBoard[doc.reason] && !gameOver) {
      gameOver = doc.reason === "aborted" ? "Called off" : doc.result === "draw" ? "Draw" : `${sideName(doc.result)} wins`;
      const mine = doc.result === online.myColor;
      const news = {
        resign: ["Resignation", mine ? `${online.opponent} has resigned.` : "You laid down your arms."],
        timeout: ["Out of time", mine ? `${online.opponent} ran out of time.` : "Your time ran out."],
        agreement: ["Parley", "Both commanders agree to a draw."],
        aborted: ["Called off", "The opening move never came, so the battle was called off. No result is recorded."]
      }[doc.reason];
      narrate(...news);
      finish(doc.result === "draw" || doc.reason === "aborted" ? null : doc.result, doc.reason);
    }
  }

  function leaveOnline() {
    token += 1;
    Online.leaveGame();
    online = null;
    battle = null;
    Online.open();
  }

  function onlineResult(outcome, reason) {
    const won = outcome === "win";
    const why = {
      resign: won ? `${online.opponent} resigned.` : "You resigned.",
      checkmate: won ? `You checkmated ${online.opponent}.` : `${online.opponent} checkmated you.`,
      timeout: won ? `${online.opponent} ran out of time.` : "Your time ran out.",
      agreement: "You agreed a draw.",
      aborted: "The first move never came, so the battle was called off."
    }[reason] || reason;
    const recorded = reason === "aborted" ? "Nothing goes on either record." : "The result has been added to your record.";
    const guest = Online.guest && reason !== "aborted";
    showCard({
      eyebrow: "Online battle",
      title: reason === "aborted" ? "Called off" : won ? "Victory" : outcome === "loss" ? "Defeat" : "Draw",
      paragraphs: [why, guest ? "You're playing as a guest. Create an account to keep this result and challenge anyone, from any device." : recorded],
      buttons: [
        { label: "View the board", action: () => showScreen(null) },
        guest
          ? { label: "Create account", primary: true, action: () => { leaveBattle(); Online.openUpgrade(); } }
          : { label: "War room", primary: true, action: leaveOnline }
      ]
    });
  }

  // Leave the battle screen without opening the War Room (used before the guest sign-up form).
  function leaveBattle() {
    token += 1;
    Online.leaveGame();
    online = null;
    battle = null;
  }

  function maybeAi() {
    if (paused || gameOver || busy || aiThinking || position.turn !== aiColor) return;
    aiThinking = true;
    updateHud();
    const myToken = token;
    // Let the banner paint before the search blocks the main thread.
    setTimeout(() => {
      if (myToken !== token) return;
      const move = chooseAiMove(position, aiDepth, positionCounts);
      aiThinking = false;
      if (move) execute(move, "q");
      else updateHud();
    }, 380);
  }

  function undo() {
    if (busy || aiThinking || !history.length || online) return;
    queuedOrder = null;
    const step = () => {
      const prev = history.pop();
      const key = positionKey(position);
      const count = positionCounts.get(key) || 0;
      if (count <= 1) positionCounts.delete(key); else positionCounts.set(key, count - 1);
      position = prev.position;
      moveLog.length = prev.moveLogLength;
      lastMove = prev.lastMove;
      streak = prev.streak;
    };
    step();
    if (position.turn === aiColor && history.length) step();
    gameOver = false;
    selected = null;
    targets = [];
    Stage.syncAll(position, armies);
    narrate("Regroup", "The last order is recalled.");
    updateHud();
    maybeAi();
  }

  function finish(winner, reason) {
    busy = false;
    queuedOrder = null;
    updateHud();
    if (battle.mode === "online") {
      // The server has already recorded the result; this only tells the player.
      const outcome = winner === null ? "draw" : winner === online.myColor ? "win" : "loss";
      FX.sfx(outcome === "win" ? "fanfare" : "defeat");
      setTimeout(() => { if (online) onlineResult(outcome, reason || "draw"); }, 1800);
      return;
    }
    const human = aiColor ? (aiColor === "w" ? "b" : "w") : null;
    const won = winner && (!human || winner === human);
    FX.sfx(winner && won ? "fanfare" : "defeat");
    setTimeout(() => {
      if (battle.mode === "story") storyResult(winner === human, winner === null, reason);
      else skirmishResult(winner, reason);
    }, 1800);
  }

  function skirmishResult(winner, reason) {
    showCard({
      eyebrow: winner ? "Checkmate" : "Draw",
      title: winner ? `${sideName(winner)} win the field` : "Neither army yields",
      paragraphs: [winner ? `The ${sideName(winner === "w" ? "b" : "w")} ${unitName(winner === "w" ? "b" : "w", "k")} has fallen.` : reason],
      buttons: [
        { label: "View the board", action: () => showScreen(null) },
        { label: "Main menu", action: () => showScreen("menu-screen") },
        { label: "Rematch", primary: true, action: () => startBattle(battle.setup) }
      ]
    });
  }

  // ---------------------------------------------------------------- story campaign
  const enemiesFor = id => CHAPTER_ORDER.filter(enemy => enemy !== id);

  function storyResult(won, drawn, reason) {
    const enemy = ARMIES[battle.enemy];
    if (won) {
      if (!story.done.includes(battle.enemy)) story.done.push(battle.enemy);
      save(STORY_KEY, story);
      const enemies = enemiesFor(story.army);
      const next = enemies[battle.chapter + 1];
      if (enemies.every(e => story.done.includes(e))) return finale();
      showCard({
        eyebrow: `Chapter ${battle.chapter + 1} · Victory`,
        title: `${enemy.story.title}: won`,
        paragraphs: [`The ${enemy.name} are broken.`],
        quote: enemy.story.victory,
        buttons: [
          { label: "Campaign", action: openCampaign },
          ...(next ? [{ label: "Next chapter", primary: true, action: () => chapterIntro(battle.chapter + 1) }] : [])
        ]
      });
    } else {
      showCard({
        eyebrow: `Chapter ${battle.chapter + 1} · ${drawn ? "Stalemate" : "Defeat"}`,
        title: drawn ? "The field is held by no one" : `Your ${ARMIES[story.army].units.k} has fallen`,
        paragraphs: [drawn ? reason : `The ${enemy.name} hold the board for now. The board lets you try again.`],
        buttons: [
          { label: "Campaign", action: openCampaign },
          { label: "Try again", primary: true, action: () => beginChapter(battle.chapter) }
        ]
      });
    }
  }

  function finale() {
    const mine = ARMIES[story.army];
    showCard({
      eyebrow: "Epilogue",
      title: "The board lets you go",
      paragraphs: [
        `Seven armies have fallen. The squares go quiet, and the carved stones slowly let go of the ${mine.name}.`,
        `Your ${mine.units.k} takes one last look at the board, then leads the army home through a gate of light. The others will be pulled back one day, and the game will start again.`
      ],
      quote: "You have mastered the board. Replay any chapter, or start over with a new army.",
      buttons: [
        { label: "Campaign", action: openCampaign },
        { label: "Main menu", primary: true, action: () => showScreen("menu-screen") }
      ]
    });
  }

  function openArmySelect() {
    const grid = $("army-grid");
    grid.replaceChildren();
    for (const [id, info] of Object.entries(ARMIES)) {
      const card = document.createElement("button");
      card.type = "button";
      card.className = "army-card";
      const icons = document.createElement("div");
      icons.className = "icons";
      icons.textContent = `${info.pieces.k}${info.pieces.n}${info.pieces.p}`;
      const name = document.createElement("div");
      name.className = "name";
      name.textContent = info.name;
      const units = document.createElement("div");
      units.className = "units";
      units.textContent = ["k", "q", "r", "b", "n", "p"].map(t => info.units[t]).join(" · ");
      card.append(icons, name, units);
      card.addEventListener("click", () => {
        story = { army: id, done: [] };
        save(STORY_KEY, story);
        showCard({
          eyebrow: "Prologue",
          title: "The board wakes",
          paragraphs: [
            `The ${info.name} wake up on cold stone. There is no sky they know, only a vast board of light and dark squares and seven armies dragged here from other ages.`,
            "Voices in the stone repeat the old law: move as the pieces move. Take what you can. Protect your king. Only the last army standing goes home."
          ],
          quote: `Your ${info.units.k} draws a weapon. "Then we fight."`,
          buttons: [{ label: "To the campaign", primary: true, action: openCampaign }]
        });
      });
      grid.appendChild(card);
    }
    showScreen("army-screen");
  }

  function openCampaign() {
    if (!story) return openArmySelect();
    const mine = ARMIES[story.army];
    $("campaign-army").textContent = `${mine.mark} ${mine.name} · ${story.done.length} of 7 battles won`;
    const list = $("chapters");
    list.replaceChildren();
    enemiesFor(story.army).forEach((id, index) => {
      const enemy = ARMIES[id];
      const done = story.done.includes(id);
      const unlocked = index === 0 || story.done.includes(enemiesFor(story.army)[index - 1]) || done;
      const row = document.createElement("div");
      row.className = `chapter${done ? " done" : ""}${unlocked ? "" : " locked"}`;
      const num = document.createElement("div");
      num.className = "num";
      num.textContent = done ? "✓" : String(index + 1);
      const text = document.createElement("div");
      const title = document.createElement("div");
      title.className = "ch-title";
      title.textContent = unlocked ? enemy.story.title : "Sealed";
      const sub = document.createElement("div");
      sub.className = "ch-sub";
      sub.textContent = unlocked ? `vs ${enemy.name} · ${RANKS[CHAPTER_DEPTH[index]]}` : "Win the previous chapter to unlock";
      text.append(title, sub);
      const button = document.createElement("button");
      button.type = "button";
      button.className = `btn small${done ? "" : " primary"}`;
      button.textContent = done ? "Replay" : "Fight";
      button.disabled = !unlocked;
      button.addEventListener("click", () => chapterIntro(index));
      row.append(num, text, button);
      list.appendChild(row);
    });
    showScreen("campaign-screen");
  }

  function chapterIntro(index) {
    const id = enemiesFor(story.army)[index];
    const enemy = ARMIES[id];
    showCard({
      eyebrow: `Chapter ${index + 1} of 7 · ${enemy.name}`,
      title: enemy.story.title,
      paragraphs: [enemy.story.intro, `Battlefield: ${Terrain.nameFor(id)}.`],
      quote: `${enemy.units.k}: “${pick(enemy.taunts)}”`,
      buttons: [
        { label: "Campaign", action: openCampaign },
        { label: "To battle", primary: true, action: () => beginChapter(index) }
      ]
    });
  }

  function beginChapter(index) {
    const id = enemiesFor(story.army)[index];
    startBattle({
      white: story.army, black: id, ai: "b", depth: CHAPTER_DEPTH[index], theme: id, mode: "story", chapter: index, enemy: id,
      title: `Chapter ${index + 1}: ${ARMIES[id].story.title}`,
      sub: `${ARMIES[story.army].name} vs ${ARMIES[id].name} · ${RANKS[CHAPTER_DEPTH[index]]}`
    });
  }

  // ---------------------------------------------------------------- skirmish
  function openSkirmish() {
    for (const [id, initial] of [["sk-white", "knights"], ["sk-black", "undead"]]) {
      const select = $(id);
      if (!select.options.length) {
        for (const [key, info] of Object.entries(ARMIES)) select.add(new Option(`${info.mark} ${info.name}`, key));
        select.value = initial;
      }
    }
    showScreen("skirmish-screen");
  }

  function startSkirmish() {
    const white = $("sk-white").value;
    const black = $("sk-black").value;
    const opponent = $("sk-opponent").value;
    const depth = Number($("sk-depth").value);
    startBattle({
      white, black, ai: opponent === "human" ? null : opponent, depth, theme: black, mode: "skirmish",
      title: "Skirmish",
      sub: `${ARMIES[white].name} vs ${ARMIES[black].name}${opponent === "human" ? " · two players" : ` · ${RANKS[depth]}`}`
    });
  }

  // ---------------------------------------------------------------- menu & wiring
  function openPause() {
    if (online) return openOnlineMenu();
    // A running animation finishes normally; only a queued AI search is cancelled and re-queued on resume.
    paused = true;
    if (aiThinking) {
      token += 1;
      aiThinking = false;
    }
    const inStory = battle?.mode === "story";
    showCard({
      eyebrow: "Paused",
      title: battle?.title || "Chess Wars",
      paragraphs: ["The armies hold their positions."],
      buttons: [
        { label: "Main menu", action: () => { token += 1; paused = false; battle = null; refreshMenu(); showScreen("menu-screen"); } },
        ...(inStory ? [{ label: "Restart chapter", action: () => { paused = false; beginChapter(battle.chapter); } }] : []),
        { label: "Resume", primary: true, action: () => { paused = false; showScreen(null); updateHud(); maybeAi(); } }
      ]
    });
  }

  // Online battles can't be paused: the opponent's clock keeps running.
  function openOnlineMenu() {
    showCard({
      eyebrow: "Online battle",
      title: `vs ${online.opponent}`,
      paragraphs: [gameOver ? "This battle is over." : "Leaving keeps the battle open. Resume it any time from the War Room."],
      buttons: [
        { label: "War room", action: leaveOnline },
        { label: "Report player", action: () => {
          const rivalId = online.myColor === "w" ? online.game.black : online.game.white;
          Online.reportUser(rivalId, online.opponent, online.gameId, () => showScreen(null));
        } },
        ...(gameOver ? [] : [{ label: "Offer draw", action: () => { showScreen(null); Online.draw(online.gameId, "offer"); } }]),
        ...(gameOver ? [] : [{ label: "Resign", action: () => showCard({
          eyebrow: "Resign",
          title: "Lay down your arms?",
          paragraphs: [`${online.opponent} wins and the loss goes on your record.`],
          buttons: [
            { label: "Keep fighting", action: () => showScreen(null) },
            { label: "Resign", primary: true, action: () => { showScreen(null); Online.resign(online.gameId, online.myColor).catch(() => Online.toast("Couldn't resign. Check your connection.")); } }
          ]
        }) }]),
        { label: "Back to battle", primary: true, action: () => showScreen(null) }
      ]
    });
  }

  function refreshMenu() {
    $("menu-continue").hidden = !story;
    if (story) $("menu-continue").textContent = `Continue campaign (${ARMIES[story.army].name}, ${story.done.length}/7)`;
  }

  function setToggle(id, on) {
    $(id).setAttribute("aria-pressed", String(on));
  }

  $("menu-story").addEventListener("click", () => (story ? showCard({
    eyebrow: "Story campaign",
    title: "Start a new campaign?",
    paragraphs: [`You already have a campaign with the ${ARMIES[story.army].name} (${story.done.length}/7). A new one starts over with a fresh army.`],
    buttons: [
      { label: "Continue existing", action: openCampaign },
      { label: "New campaign", primary: true, action: openArmySelect }
    ]
  }) : openArmySelect()));
  $("menu-continue").addEventListener("click", openCampaign);
  $("menu-skirmish").addEventListener("click", openSkirmish);
  $("sk-start").addEventListener("click", startSkirmish);
  $("campaign-reset").addEventListener("click", openArmySelect);
  for (const back of document.querySelectorAll("[data-back]")) back.addEventListener("click", () => { refreshMenu(); showScreen("menu-screen"); });
  $("btn-undo").addEventListener("click", undo);
  $("btn-view").addEventListener("click", () => Stage.toggleView());

  // Phone HUD: the ⋯ menu holds the less-used buttons; the field report opens as a bottom sheet.
  const setMore = open => {
    $("hud").classList.toggle("more-open", open);
    $("btn-more").setAttribute("aria-expanded", String(open));
  };
  $("btn-more").addEventListener("click", event => {
    event.stopPropagation();
    setMore(!$("hud").classList.contains("more-open"));
  });
  $("hud-extras").addEventListener("click", () => setMore(false));
  document.addEventListener("pointerdown", event => {
    if (!event.target.closest(".hud-buttons")) setMore(false);
  });
  $("btn-report").addEventListener("click", () => $("hud").classList.toggle("report-open"));
  $("sheet-close").addEventListener("click", () => $("hud").classList.remove("report-open"));
  $("btn-menu").addEventListener("click", openPause);
  $("btn-taunt").addEventListener("click", () => {
    if (gameOver) return;
    if (online) {
      // Online, your taunt crosses the board to your opponent.
      const text = pick(army(online.myColor).taunts);
      Online.sendTaunt(online.gameId, text);
      narrate("You taunt your rival", `“${text}”`);
      return;
    }
    narrate("A message across the field", `${sideName(position.turn)}: “${pick(army(position.turn).taunts)}”`);
  });
  $("btn-cine").addEventListener("click", () => {
    settings.cinematics = !settings.cinematics;
    Stage.cinematics = settings.cinematics;
    setToggle("btn-cine", settings.cinematics);
    save(SETTINGS_KEY, settings);
  });
  $("btn-blood").addEventListener("click", () => {
    settings.blood = !settings.blood;
    Stage.blood = settings.blood;
    setToggle("btn-blood", settings.blood);
    save(SETTINGS_KEY, settings);
  });
  $("btn-sound").addEventListener("click", () => {
    settings.sound = !settings.sound;
    FX.muted = !settings.sound;
    setToggle("btn-sound", settings.sound);
    save(SETTINGS_KEY, settings);
  });
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && selected) { selected = null; targets = []; updateHud(); }
  });

  Online.init({
    showScreen, showCard, onGame,
    // The challenger's opponent picks the battlefield: you fight on your rival's home ground.
    startGame: (game, myColor) => startBattle({
      white: game.whiteArmy, black: game.blackArmy, ai: null, depth: 0,
      theme: myColor === "w" ? game.blackArmy : game.whiteArmy,
      title: "Online battle", sub: `${game.whiteName} vs ${game.blackName}`, mode: "online",
      online: { gameId: game.id, myColor, opponent: myColor === "w" ? game.blackName : game.whiteName, game },
      moves: game.moves
    }),
    resync: () => { if (online) Online.joinGame(online.gameId); },
    inGame: () => Boolean(online) && !gameOver,
    touch: () => Stage.touch,
    currentGameId: () => (online && !gameOver ? online.gameId : null)
  });
  $("menu-online").addEventListener("click", Online.open);

  // ---------------------------------------------------------------- unit tooltip
  const ROLE_VALUE = { p: 1, n: 3, b: 3, r: 5, q: 9 };
  const ROLE_LABEL = { k: "King", q: "Queen", r: "Rook", b: "Bishop", n: "Knight", p: "Pawn" };
  let tipKey = "";

  function unitTip(sq, x, y) {
    const tip = $("unit-tip");
    const piece = sq && position.board[sq.row][sq.col];
    if (!piece) {
      tip.classList.remove("show");
      tipKey = "";
      return;
    }
    // Rebuild the card only when the unit or the position changed; otherwise just follow the pointer.
    const key = `${sq.row}${sq.col}|${history.length}`;
    if (key !== tipKey) {
      tipKey = key;
      const info = army(piece.color);
      const enemy = piece.color === "w" ? "b" : "w";
      const side = piece.color === "w" ? "Ivory" : "Onyx";
      const lines = [];
      const worth = ROLE_VALUE[piece.type] ? ` · worth ${ROLE_VALUE[piece.type]}` : "";
      lines.push(`${ROLE_LABEL[piece.type]} · ${algebraic(sq.row, sq.col)}${worth}`);
      const ground = Terrain.at(sq.row, sq.col);
      if (ground) lines.push(`Standing ${ground.label}`);
      const threatened = isSquareAttacked(position, sq.row, sq.col, enemy);
      const guarded = piece.type !== "k" && isSquareAttacked(position, sq.row, sq.col, piece.color);
      if (piece.type === "k" && threatened) lines.push(`<span class="warn">In check!</span>`);
      else if (threatened) lines.push(`<span class="warn">Under threat</span>${guarded ? " · guarded" : " · unguarded"}`);
      else if (guarded) lines.push(`<span class="good">Guarded</span>`);
      const mine = piece.color === position.turn && piece.color !== aiColor && (!online || piece.color === online.myColor);
      if (mine && !gameOver) {
        const count = legalMoves(position, sq.row, sq.col).length;
        lines.push(count ? `${count} move${count === 1 ? "" : "s"} available` : "No moves available");
      }
      tip.style.setProperty("--tip-accent", `#${info.look.accent.toString(16).padStart(6, "0")}`);
      tip.innerHTML = `
        <div class="tip-head">
          <span class="tip-icon">${info.pieces[piece.type]}</span>
          <div><div class="tip-name">${info.units[piece.type]}</div><div class="tip-army">${info.name} · ${side}</div></div>
        </div>
        <ul class="tip-lines">${lines.map(line => `<li>${line}</li>`).join("")}</ul>`;
    }
    tip.classList.add("show");
    // Beside the mouse; above the finger on touch screens so the hand doesn't cover it.
    const w = tip.offsetWidth, h = tip.offsetHeight, pad = 12;
    let left = Stage.touch ? x - w / 2 : x + 16;
    let top = Stage.touch ? y - h - 28 : y + 18;
    if (!Stage.touch && left + w > innerWidth - pad) left = x - w - 16;
    if (top + h > innerHeight - pad) top = y - h - 12;
    left = Math.max(pad, Math.min(left, innerWidth - w - pad));
    top = Math.max(pad, top);
    tip.style.left = `${left}px`;
    tip.style.top = `${top}px`;
  }

  Stage.init($("stage"), onSquare, unitTip);
  Stage.cinematics = settings.cinematics;
  Stage.blood = settings.blood;
  FX.muted = !settings.sound;
  setToggle("btn-cine", settings.cinematics);
  setToggle("btn-blood", settings.blood);
  setToggle("btn-sound", settings.sound);
  Stage.setTheme("knights");
  Stage.syncAll(position, armies);
  refreshMenu();
  $("loading").remove();
  showScreen("menu-screen");
})();
