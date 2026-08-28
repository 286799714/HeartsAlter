import { Room, Client, CloseCode, Delayed } from "colyseus";
import {
  cardFromId,
  dealShuffledHands,
  determineTrickWinner,
  getLegalCards,
  rankValue,
  scoreCards,
  settlePot,
  type Card,
  type PlayedCard,
} from "../game/rules.js";
import { MyRoomState, Player, TrickCard } from "./schema/MyRoomState.js";

/** How long a connected or disconnected seat has to act before the server plays for it. */
export const TURN_DURATION = 15_000;
/** How quickly a synthetic demo seat answers after it receives the turn. */
export const BOT_TURN_DURATION = 600;
export const DEFAULT_ANTE = 100;
export const DEFAULT_STARTING_CHIPS = 1_000;
export const MAX_PLAYERS = 4;
/**
 * A join may resolve on a client before its message handlers are installed.
 * Re-sending the private hand on the next room tick closes that small race;
 * `request_hand` remains available for explicit refreshes and reconnects.
 */
export const HAND_RESEND_DELAY = 100;
/**
 * Chip fields in the public schema are signed int32 values and the pot is the
 * sum of all four stakes.  Capping a per-seat balance/ante at this value keeps
 * both each field and the aggregate pot representable on the wire.
 */
export const MAX_CHIPS_PER_PLAYER = Math.floor(2_147_483_647 / MAX_PLAYERS);

type PlayMessage = { cardId?: unknown } | string | undefined;

/**
 * Colyseus adapter for the server-authoritative Hearts Alter round.
 *
 * The room deliberately keeps hands outside the synchronized schema. Every
 * player receives only their own hand through a private message, while all
 * public trick and scoreboard data remains in `MyRoomState`.
 */
export class MyRoom extends Room<{ state: MyRoomState }> {
  maxClients = MAX_PLAYERS;
  state = new MyRoomState();

  private readonly hands = new Map<string, Card[]>();
  private readonly handResendTimers = new Set<Delayed>();
  private readonly botPlayerIds = new Set<string>();
  private turnTimeout?: Delayed;
  private ante = DEFAULT_ANTE;
  private startingChips = DEFAULT_STARTING_CHIPS;
  private botsEnabled = false;

  messages = {
    /** A player submits the id of one card from their private hand. */
    play: (client: Client, message: PlayMessage) => {
      const cardId = this.readCardId(message);
      if (!cardId) {
        this.sendError(client, "请选择一张牌");
        return;
      }
      this.playCard(client.sessionId, cardId, client);
    },

    /** Re-send the private hand after a reconnect or an explicit refresh. */
    request_hand: (client: Client) => {
      this.sendHand(client.sessionId);
    },

    /** Start another round with the same four seats after settlement. */
    restart: (client: Client) => {
      if (this.state.phase !== "finished") {
        this.sendError(client, "本局尚未结束");
        return;
      }
      if (!this.state.players.has(client.sessionId)) {
        this.sendError(client, "你不在本局座位中");
        return;
      }
      const cannotAnte = this.state.playerOrder.find((playerId) => {
        const player = this.state.players.get(playerId);
        return player !== undefined && player.chips < this.ante;
      });
      if (cannotAnte) {
        this.sendError(client, "有玩家筹码不足，无法重新押注");
        return;
      }
      this.startRound();
    },
  };

  onCreate(options: Record<string, unknown> = {}) {
    const safeOptions = options && typeof options === "object" ? options : {};
    this.ante = this.readChipAmount(safeOptions.ante, DEFAULT_ANTE);
    // A caller may omit startingChips or provide less than the ante.  Raise it
    // to the ante in that case so a full room can never become permanently
    // locked in the waiting phase with an impossible buy-in.
    this.startingChips = Math.max(
      this.readChipAmount(safeOptions.startingChips, DEFAULT_STARTING_CHIPS),
      this.ante,
    );
    this.botsEnabled = safeOptions.bots === true;
    // A demo room has one real client and three synthetic seats.  Limiting
    // matchmaking to one connection makes the room's intent explicit and
    // prevents a second real client from racing the synthetic seats.
    this.maxClients = this.botsEnabled ? 1 : MAX_PLAYERS;
    this.state.ante = this.ante;
    this.state.message = this.botsEnabled
      ? "等待一名玩家加入（机器人演示）"
      : "等待四名玩家加入";
  }

  onJoin(client: Client, options: Record<string, unknown> = {}) {
    if (this.state.phase !== "waiting") {
      throw new Error("本房间已经开始一局牌");
    }

    const player = new Player();
    const safeOptions = options && typeof options === "object" ? options : {};
    player.name = this.readName(safeOptions.name, this.state.players.size + 1);
    player.seat = this.state.players.size;
    player.chips = this.startingChips;
    player.connected = true;
    player.isTreating = false;
    this.state.players.set(client.sessionId, player);
    this.state.playerOrder.push(client.sessionId);
    this.hands.set(client.sessionId, []);
    this.sendHand(client.sessionId);
    this.scheduleHandResend(client.sessionId);

    this.state.message = `${this.state.players.size}/${MAX_PLAYERS} 名玩家已就位`;
    this.broadcast("player_joined", {
      playerId: client.sessionId,
      seat: player.seat,
      name: player.name,
    });

    if (this.botsEnabled && this.state.players.size === 1) {
      this.addDemoBots();
      this.state.message = "演示模式：1 名玩家与 3 个机器人已就位";
    }

    if (this.state.players.size === MAX_PLAYERS) {
      // A full room is deliberately locked before dealing, so a fifth client
      // can never observe a partially started round.
      void this.lock();
      this.startRound();
    }
  }

  onLeave(client: Client, code: CloseCode) {
    const player = this.state.players.get(client.sessionId);
    if (!player) {
      return;
    }

    // Before a round starts, a vacant seat can be filled by another player.
    // Once cards are dealt, retain the seat and let the timeout play for it so
    // the other three players can always finish the round.
    if (this.state.phase === "waiting") {
      this.state.players.delete(client.sessionId);
      this.hands.delete(client.sessionId);
      const orderIndex = this.state.playerOrder.indexOf(client.sessionId);
      if (orderIndex >= 0) {
        this.state.playerOrder.splice(orderIndex, 1);
      }
      this.state.playerOrder.forEach((playerId, seat) => {
        const remaining = this.state.players.get(playerId);
        if (remaining) {
          remaining.seat = seat;
        }
      });
      this.state.message = this.botsEnabled
        ? "等待一名玩家加入（机器人演示）"
        : `${this.state.players.size}/${MAX_PLAYERS} 名玩家已就位`;
      this.broadcast("player_left", { playerId: client.sessionId });
      return;
    }

    player.connected = false;
    this.state.message = `${player.name} 暂时离线，服务器将代为出牌`;
    this.broadcast("player_disconnected", { playerId: client.sessionId, code });
    if (this.state.phase === "playing" && this.state.currentTurn === client.sessionId) {
      this.autoPlayCurrentTurn();
    }
  }

  onDrop(client: Client, code: CloseCode) {
    const player = this.state.players.get(client.sessionId);
    if (player) {
      player.connected = false;
    }
    // Holding the seat gives the SDK time to reconnect with the same session.
    // If it does not, onLeave keeps the seat and the round continues by bot.
    this.allowReconnection(client, 30).catch(() => {});
    if (this.state.phase === "playing" && this.state.currentTurn === client.sessionId) {
      this.autoPlayCurrentTurn();
    }
    this.broadcast("player_disconnected", { playerId: client.sessionId, code });
  }

  onReconnect(client: Client) {
    const player = this.state.players.get(client.sessionId);
    if (player) {
      player.connected = true;
      this.state.message = `${player.name} 已重新连接`;
      this.sendHand(client.sessionId);
    }
  }

  onDispose() {
    this.turnTimeout?.clear();
    for (const timer of this.handResendTimers) {
      timer.clear();
    }
    this.handResendTimers.clear();
    this.hands.clear();
    this.botPlayerIds.clear();
  }

  private startRound(): boolean {
    if (this.state.players.size !== MAX_PLAYERS) {
      return false;
    }
    const cannotAnte = this.state.playerOrder.find((playerId) => {
      const player = this.state.players.get(playerId);
      return player !== undefined && player.chips < this.ante;
    });
    if (cannotAnte) {
      this.state.message = "有玩家筹码不足，无法开始新一局";
      return false;
    }

    this.turnTimeout?.clear();
    this.hands.clear();
    this.state.roundNumber += 1;
    this.state.phase = "playing";
    this.state.currentTurn = "";
    this.state.turnDeadline = 0;
    this.state.turnCount = 0;
    this.state.trickNumber = 0;
    this.state.leadSuit = "";
    this.state.heartsBroken = false;
    this.state.trick.clear();
    this.state.lastTrick.clear();
    this.state.lastTrickWinner = "";
    this.state.lastTrickPoints = 0;
    this.state.pot = 0;
    this.state.ante = this.ante;

    for (const playerId of this.state.playerOrder) {
      const player = this.state.players.get(playerId);
      if (!player) {
        continue;
      }
      player.chips -= this.ante;
      player.stake = this.ante;
      player.score = 0;
      player.handCount = 0;
      player.tricksWon = 0;
      player.payout = 0;
      player.isTreating = false;
      this.state.pot += this.ante;
    }

    const hands = dealShuffledHands();
    this.state.playerOrder.forEach((playerId, seat) => {
      const hand = [...(hands[seat] ?? [])];
      this.hands.set(playerId, hand);
      const player = this.state.players.get(playerId);
      if (player) {
        player.handCount = hand.length;
      }
      if (!this.isBotPlayer(playerId)) {
        this.sendHand(playerId);
        this.scheduleHandResend(playerId);
      }
    });

    const starter = this.findTwoOfClubsOwner();
    this.state.message = this.botsEnabled
      ? "演示模式：已发牌，梅花 2 先出"
      : "已发牌，梅花 2 先出";
    this.broadcast("round_started", {
      roundNumber: this.state.roundNumber,
      ante: this.ante,
      pot: this.state.pot,
      cards: 52,
    });
    this.setTurn(starter ?? this.state.playerOrder[0] ?? "");
    return true;
  }

  private playCard(playerId: string, cardId: string, client?: Client): boolean {
    if (this.state.phase !== "playing") {
      if (client) {
        this.sendError(client, "当前没有可出的牌");
      }
      return false;
    }
    if (this.state.currentTurn !== playerId) {
      if (client) {
        this.sendError(client, "还没轮到你");
      }
      return false;
    }

    const hand = this.hands.get(playerId);
    const card = cardFromId(cardId);
    if (!hand || !card) {
      if (client) {
        this.sendError(client, "这张牌不存在");
      }
      return false;
    }
    const handIndex = hand.findIndex((candidate) => candidate.id === card.id);
    if (handIndex < 0) {
      if (client) {
        this.sendError(client, "这张牌不在你的手牌中");
      }
      return false;
    }

    const trickCards = this.state.trick
      .map((entry) => cardFromId(entry.cardId))
      .filter((entry): entry is Card => entry !== undefined);
    const legal = getLegalCards(hand, trickCards, {
      firstTrick: this.state.trickNumber === 0,
      heartsBroken: this.state.heartsBroken,
    });
    if (!legal.some((candidate) => candidate.id === card.id)) {
      if (client) {
        this.sendError(client, "这张牌不符合跟牌或红心规则");
      }
      return false;
    }

    this.turnTimeout?.clear();
    hand.splice(handIndex, 1);
    const player = this.state.players.get(playerId);
    if (player) {
      player.handCount = hand.length;
    }
    if (card.suit === "hearts") {
      this.state.heartsBroken = true;
    }
    if (this.state.trick.length === 0) {
      this.state.leadSuit = card.suit;
    }
    const played = new TrickCard();
    played.playerId = playerId;
    played.cardId = card.id;
    this.state.trick.push(played);
    this.broadcast("card_played", {
      playerId,
      cardId: card.id,
      roundNumber: this.state.roundNumber,
      trickIndex: this.state.trick.length - 1,
    });

    if (this.state.trick.length === MAX_PLAYERS) {
      this.resolveTrick();
    } else {
      this.setTurn(this.nextSeat(playerId));
    }
    return true;
  }

  private resolveTrick() {
    const plays: PlayedCard<string>[] = this.state.trick
      .map((entry) => {
        const card = cardFromId(entry.cardId);
        if (!card) {
          throw new Error(`状态中存在未知牌 ${entry.cardId}`);
        }
        return { playerId: entry.playerId, card };
      });
    const winnerId = determineTrickWinner(plays);
    const points = scoreCards(plays.map((play) => play.card));
    const winner = this.state.players.get(winnerId);
    if (winner) {
      winner.score += points;
      winner.tricksWon += 1;
    }

    this.state.lastTrick.clear();
    for (const play of plays) {
      const entry = new TrickCard();
      entry.playerId = play.playerId;
      entry.cardId = play.card.id;
      this.state.lastTrick.push(entry);
    }
    this.state.lastTrickWinner = winnerId;
    this.state.lastTrickPoints = points;
    this.state.trick.clear();
    this.state.leadSuit = "";
    this.state.trickNumber += 1;
    this.broadcast("trick_resolved", {
      winnerId,
      points,
      trickNumber: this.state.trickNumber,
      cards: plays.map((play) => ({ playerId: play.playerId, cardId: play.card.id })),
    });

    if (this.state.trickNumber >= 13) {
      this.finishRound();
      return;
    }
    this.setTurn(winnerId);
  }

  private finishRound() {
    this.turnTimeout?.clear();
    this.state.phase = "finished";
    this.state.currentTurn = "";
    this.state.turnDeadline = 0;

    const scores = this.state.playerOrder.map((playerId) => ({
      playerId,
      score: this.state.players.get(playerId)?.score ?? 0,
    }));
    const settlement = settlePot(scores, this.state.pot);
    const highest = new Set(settlement.highestPlayerIds);
    const payouts: Record<string, number> = {};
    for (const playerId of this.state.playerOrder) {
      const player = this.state.players.get(playerId);
      if (!player) {
        continue;
      }
      const payout = settlement.payouts.get(playerId) ?? 0;
      player.payout = payout;
      player.chips += payout;
      player.isTreating = highest.has(playerId);
      payouts[playerId] = payout;
    }
    this.state.message = "本局结束：最高分玩家共同请客";
    this.broadcast("round_finished", {
      scores: scores.map((entry) => ({ playerId: entry.playerId, score: entry.score })),
      payouts,
      highestPlayerIds: settlement.highestPlayerIds,
      pot: this.state.pot,
      distributed: settlement.distributed,
      unallocatedPot: settlement.unallocatedPot,
    });
  }

  /** Hand the turn to a seat and arm a server-side deadline. */
  private setTurn(playerId: string) {
    this.turnTimeout?.clear();
    if (this.state.phase !== "playing" || !playerId) {
      this.state.currentTurn = "";
      this.state.turnDeadline = 0;
      return;
    }
    this.state.currentTurn = playerId;
    this.state.turnCount += 1;
    const turnDuration = this.isBotPlayer(playerId) ? BOT_TURN_DURATION : TURN_DURATION;
    this.state.turnDeadline = this.clock.currentTime + turnDuration;
    this.turnTimeout = this.clock.setTimeout(() => this.autoPlayCurrentTurn(), turnDuration);
    this.broadcast("turn_started", {
      playerId,
      deadline: this.state.turnDeadline,
      trickNumber: this.state.trickNumber,
      automated: this.isBotPlayer(playerId),
    });
  }

  /** Choose the lowest legal card when a client misses its deadline. */
  private autoPlayCurrentTurn() {
    if (this.state.phase !== "playing" || !this.state.currentTurn) {
      return;
    }
    const playerId = this.state.currentTurn;
    const hand = this.hands.get(playerId) ?? [];
    const trickCards = this.state.trick
      .map((entry) => cardFromId(entry.cardId))
      .filter((entry): entry is Card => entry !== undefined);
    const legal = getLegalCards(hand, trickCards, {
      firstTrick: this.state.trickNumber === 0,
      heartsBroken: this.state.heartsBroken,
    });
    if (legal.length === 0) {
      // This should be unreachable for a valid 52-card deal. Recovering by
      // ending the round is safer than leaving every other seat blocked.
      this.finishRound();
      return;
    }
    const selected = [...legal].sort((left, right) => {
      const rankDifference = rankValue(left) - rankValue(right);
      return rankDifference !== 0 ? rankDifference : left.id.localeCompare(right.id);
    })[0];
    this.broadcast("auto_play", {
      playerId,
      cardId: selected.id,
      automated: this.isBotPlayer(playerId),
    });
    this.playCard(playerId, selected.id);
  }

  /** Add the three non-network seats used by the single-client demo room. */
  private addDemoBots() {
    while (this.state.players.size < MAX_PLAYERS) {
      const seat = this.state.players.size;
      const playerId = this.createBotId(seat);
      const player = new Player();
      player.name = `机器人 ${seat + 1}`;
      player.seat = seat;
      player.chips = this.startingChips;
      player.connected = true;
      player.isTreating = false;
      this.state.players.set(playerId, player);
      this.state.playerOrder.push(playerId);
      this.hands.set(playerId, []);
      this.botPlayerIds.add(playerId);
      this.broadcast("player_joined", {
        playerId,
        seat,
        name: player.name,
        automated: true,
      });
    }
  }

  private createBotId(seat: number): string {
    const baseId = `bot-${seat + 1}`;
    let playerId = baseId;
    let suffix = 2;
    while (this.state.players.has(playerId)) {
      playerId = `${baseId}-${suffix}`;
      suffix += 1;
    }
    return playerId;
  }

  private isBotPlayer(playerId: string): boolean {
    return this.botPlayerIds.has(playerId);
  }

  private nextSeat(playerId: string): string {
    const index = this.state.playerOrder.indexOf(playerId);
    if (index < 0 || this.state.playerOrder.length === 0) {
      return this.state.playerOrder[0] ?? "";
    }
    return this.state.playerOrder[(index + 1) % this.state.playerOrder.length] ?? "";
  }

  private findTwoOfClubsOwner(): string | undefined {
    for (const [playerId, hand] of this.hands.entries()) {
      if (hand.some((card) => card.id === "Club2")) {
        return playerId;
      }
    }
    return undefined;
  }

  private sendHand(playerId: string) {
    if (this.isBotPlayer(playerId)) {
      return;
    }
    const client = this.clients.find((candidate) => candidate.sessionId === playerId);
    if (!client) {
      return;
    }
    client.send("hand", {
      roundNumber: this.state.roundNumber,
      cards: (this.hands.get(playerId) ?? []).map((card) => card.id),
    });
  }

  private scheduleHandResend(playerId: string) {
    if (this.isBotPlayer(playerId)) {
      return;
    }
    const timer = this.clock.setTimeout(() => {
      this.handResendTimers.delete(timer);
      // The seat may have left before the deferred delivery. `sendHand`
      // safely no-ops when there is no active client for the session.
      this.sendHand(playerId);
    }, HAND_RESEND_DELAY);
    this.handResendTimers.add(timer);
  }

  private sendError(client: Client, reason: string) {
    client.send("invalid_play", { reason });
  }

  private readCardId(message: PlayMessage): string | undefined {
    if (typeof message === "string") {
      return message;
    }
    if (message && typeof message === "object") {
      if (typeof message.cardId === "string") {
        return message.cardId;
      }
      const legacy = message as Record<string, unknown>;
      if (typeof legacy.card === "string") {
        return legacy.card;
      }
      if (typeof legacy.id === "string") {
        return legacy.id;
      }
    }
    return undefined;
  }

  private readName(value: unknown, seatNumber: number): string {
    if (typeof value !== "string") {
      return `玩家 ${seatNumber}`;
    }
    const trimmed = value.trim().slice(0, 20);
    return trimmed.length > 0 ? trimmed : `玩家 ${seatNumber}`;
  }

  private readChipAmount(value: unknown, fallback: number): number {
    // Room options arrive over the matchmaking API and are untrusted.  Only
    // accept primitive numeric/string values in the range that keeps all
    // int32-backed schema fields and the four-player pot safe.  In particular,
    // avoid Number(Symbol(...)) / hostile valueOf() exceptions escaping
    // onCreate and wedging room creation.
    let parsed: number;
    if (typeof value === "number") {
      parsed = value;
    } else if (typeof value === "string" && value.trim().length > 0) {
      parsed = Number(value);
    } else {
      return fallback;
    }
    return Number.isSafeInteger(parsed) && parsed > 0 && parsed <= MAX_CHIPS_PER_PLAYER
      ? parsed
      : fallback;
  }
}
