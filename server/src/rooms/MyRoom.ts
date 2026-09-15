import { Room, Client, CloseCode, Delayed } from "colyseus";
import {
  cardFromId,
  dealShuffledHands,
  determineTrickWinner,
  getLegalCards,
  scoreCards,
  settlePot,
  shuffleDeck,
  type Card,
  type PlayedCard,
} from "../game/rules.js";
import { MyRoomState, Player, TrickCard } from "./schema/MyRoomState.js";
import { AVATAR_COUNT, getPlayerProfileStore, readDeviceId } from "../persistence/PlayerProfileStore.js";

/** How long a connected or disconnected seat has to act before the server plays for it. */
export const TURN_DURATION = 15_000;
/** Maximum wait for a table/deal/next-round handshake. */
export const PHASE_READY_DURATION = 30_000;
/** How quickly a synthetic demo seat answers after it receives the turn. */
export const BOT_TURN_DURATION = 600;
/** Extra pause after a trick is collected before a bot starts the next one. */
export const BOT_TRICK_DELAY = 1_000;
/** Grace period after the pass animation before a bot's opening turn starts. */
export const PASSING_BOT_DELAY = 1_200;
/** Maximum wait for all four players to choose their three passing cards. */
export const PASSING_DURATION = 30_000;
export const PASS_CARD_COUNT = 3;
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

type PlayMessage = {
  cardId?: unknown;
  cardIndex?: unknown;
  suit?: unknown;
  rank?: unknown;
} | string | undefined;

type PassMessage = {
  cardIds?: unknown;
  cards?: unknown;
  cardIndexes?: unknown;
} | string | undefined;

/**
 * Colyseus adapter for the server-authoritative Hearts Alter round.
 *
 * The room deliberately keeps hands outside the synchronized schema. Every
 * player receives only their own hand through a private message, while all
 * public trick and scoreboard data remains in `MyRoomState`.
 */
export interface MyRoomMetadata {
  displayName: string;
  phase: string;
  playerCount: number;
  maxPlayers: number;
  readyCount: number;
  bots: boolean;
  hostName: string;
}

export class MyRoom extends Room<{ state: MyRoomState; metadata: MyRoomMetadata }> {
  maxClients = MAX_PLAYERS;
  state = new MyRoomState();

  private readonly hands = new Map<string, Card[]>();
  private readonly handResendTimers = new Set<Delayed>();
  private readonly botPlayerIds = new Set<string>();
  private readonly profileSeats = new Map<string, string>();
  private readonly departedPlayers = new Set<string>();
  private turnTimeout?: Delayed;
  private ante = DEFAULT_ANTE;
  private startingChips = DEFAULT_STARTING_CHIPS;
  private botsEnabled = false;
  private displayName = "房间";
  private lobbyManaged = false;
  private starting = false;
  private phaseTimeout?: Delayed;
  private passingTimeout?: Delayed;
  private readonly passingSelections = new Map<string, Card[]>();

  messages = {
    /** Toggle the ready flag. The host and bots are always ready. */
    ready: (client: Client, message: { ready?: unknown } | undefined) => {
      const player = this.state.players.get(client.sessionId);
      if (!player || this.state.phase !== "waiting" || player.isHost || player.isBot) {
        return;
      }
      player.ready = message?.ready !== false;
      this.state.message = player.ready ? `${player.name} 已准备` : `${player.name} 取消准备`;
      this.publishRoomMetadata();
    },
    toggle_ready: (client: Client, message: { ready?: unknown } | undefined) => {
      this.messages.ready(client, message);
    },

    /** The owner adds one synthetic seat to an empty slot. */
    add_bot: (client: Client) => {
      if (this.state.phase !== "waiting" || client.sessionId !== this.state.hostId) {
        this.sendError(client, "只有房主可以添加机器人");
        return;
      }
      if (this.state.players.size >= MAX_PLAYERS) {
        this.sendError(client, "房间没有空席位");
        return;
      }
      this.addBot();
      this.state.message = "已添加机器人（机器人自动准备）";
      this.publishRoomMetadata();
    },
    add_robot: (client: Client) => {
      this.messages.add_bot(client);
    },

    /** The owner can dissolve the room from the settlement screen. */
    disband_room: (client: Client) => {
      if (client.sessionId !== this.state.hostId) {
        this.sendError(client, "只有房主可以解散房间");
        return;
      }
      void this.disconnect().catch(() => {});
    },

    /** Start only after every occupied seat is ready. */
    start_game: (client: Client) => {
      if (this.state.phase !== "waiting" || client.sessionId !== this.state.hostId) {
        this.sendError(client, "只有房主可以开始游戏");
        return;
      }
      if (this.state.players.size !== MAX_PLAYERS) {
        this.sendError(client, `需要 ${MAX_PLAYERS} 个席位才能开始`);
        return;
      }
      if ([...this.state.players.values()].some((player) => !player.isBot && !player.connected)) {
        this.sendError(client, "请等待所有玩家连接后再开始");
        return;
      }
      if ([...this.state.players.values()].some((player) => !player.ready)) {
        this.sendError(client, "仍有玩家未准备");
        return;
      }
      if (this.starting) return;
      this.starting = true;
      void this.lock().then(() => {
        this.starting = false;
        if (this.lobbyManaged) this.enterTableReady();
        else this.startRound();
      }).catch(() => {
        this.starting = false;
        this.sendError(client, "开始游戏失败，请稍后重试");
      });
    },
    start: (client: Client) => {
      this.messages.start_game(client);
    },

    /** The table scene has finished constructing its four seat widgets. */
    table_ready: (client: Client) => {
      const player = this.state.players.get(client.sessionId);
      if (!player || this.state.phase !== "table_ready") return;
      player.tableReady = true;
      if (this.allRealPlayersHave((candidate) => candidate.tableReady)) {
        this.startManagedDeal();
      }
    },

    /** The local deal animation has completed. */
    deal_ready: (client: Client) => {
      const player = this.state.players.get(client.sessionId);
      if (!player || this.state.phase !== "dealing") return;
      player.dealReady = true;
      if (this.allRealPlayersHave((candidate) => candidate.dealReady)) {
        this.beginPlayingAfterDeal();
      }
    },

    /** Submit exactly three cards to pass to the next seat. */
    pass_cards: (client: Client, message: PassMessage) => {
      this.submitPassingCards(client, message);
    },
    // Keep a short alias for clients built against an earlier protocol draft.
    pass: (client: Client, message: PassMessage) => {
      this.submitPassingCards(client, message);
    },
    pass_selected: (client: Client, message: PassMessage) => {
      this.submitPassingCards(client, message);
    },

    /** Every player explicitly agrees to proceed to another round. */
    next_round: (client: Client) => {
      const player = this.state.players.get(client.sessionId);
      if (!player || this.state.phase !== "finished") return;
      if ([...this.state.players.values()].some((candidate) => !candidate.isBot && !candidate.connected)) {
        this.sendError(client, "有玩家已离开，请返回大厅重新组局");
        return;
      }
      player.nextRoundReady = true;
      if (this.allRealPlayersHave((candidate) => candidate.nextRoundReady)) {
        this.enterTableReady();
      }
    },

    /** A player submits the id of one card from their private hand. */
    play: (client: Client, message: PlayMessage) => {
      const cardId = this.readCardId(message);
      if (!cardId) {
        this.sendError(client, "请选择一张牌");
        return;
      }
      if (!this.isCardDescriptorConsistent(message, cardId)) {
        this.sendError(client, "牌面信息与牌 ID 不一致");
        return;
      }
      this.playCard(client.sessionId, cardId, client, this.readCardIndex(message));
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
      if (this.lobbyManaged) {
        this.messages.next_round(client);
        return;
      }
      if ([...this.state.players.values()].some((player) => !player.isBot && !player.connected)) {
        this.sendError(client, "有玩家已离开，请重新组局");
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
    this.lobbyManaged = safeOptions.lobbyManaged === true;
    this.state.lobbyManaged = this.lobbyManaged;
    this.displayName = this.readRoomName(safeOptions.roomName);
    // A demo room has one real client and three synthetic seats.  Limiting
    // matchmaking to one connection makes the room's intent explicit and
    // prevents a second real client from racing the synthetic seats.
    this.maxClients = this.botsEnabled ? 1 : MAX_PLAYERS;
    this.state.ante = this.ante;
    this.state.message = this.botsEnabled
      ? "等待一名玩家加入（机器人演示）"
      : "等待四名玩家加入";
    this.publishRoomMetadata();
  }

  onJoin(client: Client, options: Record<string, unknown> = {}) {
    if (this.state.phase !== "waiting") {
      throw new Error("本房间已经开始一局牌");
    }

    const player = new Player();
    const safeOptions = options && typeof options === "object" ? options : {};
    // Legacy direct rooms may still use ephemeral guests. Lobby seats always
    // load server-owned data, ignoring any submitted avatar/chips/profile id.
    const profile = this.lobbyManaged || safeOptions.deviceId !== undefined
      ? getPlayerProfileStore().getOrCreate(readDeviceId(safeOptions.deviceId), safeOptions.name)
      : undefined;
    if (profile) {
      getPlayerProfileStore().claimSeat(profile.playerId, this.profileOwner(client.sessionId));
      this.profileSeats.set(client.sessionId, profile.playerId);
      player.profileId = profile.playerId;
      player.avatarId = profile.avatarId;
    }
    player.name = profile?.name ?? this.readName(safeOptions.name, this.state.players.size + 1);
    player.seat = this.state.players.size;
    player.chips = profile?.chips ?? this.startingChips;
    player.connected = true;
    player.isTreating = false;
    player.ready = this.state.players.size === 0;
    player.isHost = this.state.players.size === 0;
    player.isBot = false;
    if (player.isHost) {
      this.state.hostId = client.sessionId;
    }
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

    this.publishRoomMetadata();
    if (!this.lobbyManaged && this.state.players.size === MAX_PLAYERS) {
      // A full room is deliberately locked before dealing, so a fifth client
      // can never observe a partially started round.
      void this.lock();
      this.startRound();
    }
  }

  onLeave(client: Client, code: CloseCode) {
    const player = this.state.players.get(client.sessionId);
    if (!player) {
      this.releaseProfileSeat(client.sessionId);
      return;
    }
    this.departedPlayers.add(client.sessionId);
    if (this.state.phase === "waiting" || this.state.phase === "finished") {
      this.releaseProfileSeat(client.sessionId);
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
      if (client.sessionId === this.state.hostId) {
        const nextHostId = this.state.playerOrder.find((playerId) => !this.isBotPlayer(playerId));
        this.state.hostId = nextHostId ?? this.state.playerOrder[0] ?? "";
        this.state.playerOrder.forEach((playerId) => {
          const remaining = this.state.players.get(playerId);
          if (remaining) {
            remaining.isHost = playerId === this.state.hostId;
            if (remaining.isHost) remaining.ready = true;
          }
        });
      }
      this.broadcast("player_left", { playerId: client.sessionId });
      this.publishRoomMetadata();
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
    for (const sessionId of this.profileSeats.keys()) this.releaseProfileSeat(sessionId);
    this.departedPlayers.clear();
    this.turnTimeout?.clear();
    this.phaseTimeout?.clear();
    this.passingTimeout?.clear();
    for (const timer of this.handResendTimers) {
      timer.clear();
    }
    this.handResendTimers.clear();
    this.hands.clear();
    this.passingSelections.clear();
    this.botPlayerIds.clear();
  }

  private startRound(): boolean {
    if (this.state.players.size !== MAX_PLAYERS) {
      return false;
    }
    if (this.lobbyManaged && [...this.state.players.values()].some((player) => !player.ready)) {
      this.state.message = "仍有玩家未准备，无法开始游戏";
      void this.unlock().catch(() => {});
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

    return this.dealRound(false);
  }

  /** Enter the table handshake after the host starts a lobby-managed room. */
  private enterTableReady() {
    this.phaseTimeout?.clear();
    this.turnTimeout?.clear();
    this.passingTimeout?.clear();
    this.hands.clear();
    this.passingSelections.clear();
    this.state.phase = "table_ready";
    this.state.phaseDeadline = this.clock.currentTime + PHASE_READY_DURATION;
    this.state.currentTurn = "";
    this.state.turnDeadline = 0;
    this.state.trick.clear();
    this.state.lastTrick.clear();
    this.state.lastTrickWinner = "";
    this.state.lastTrickPoints = 0;
    this.state.leadSuit = "";
    this.state.heartsBroken = false;
    this.state.trickNumber = 0;
    this.state.pot = 0;
    for (const [playerId, player] of this.state.players.entries()) {
      // The previous round is already settled. Its stake is only retained for
      // the settlement UI and must not be refunded by a new handshake timeout.
      player.stake = 0;
      player.tableReady = this.isBotPlayer(playerId);
      player.dealReady = false;
      player.nextRoundReady = false;
    }
    this.state.message = "牌桌初始化中，等待所有玩家就绪";
    this.publishRoomMetadata();
    this.armPhaseTimeout("table_ready");
  }

  private startManagedDeal(): boolean {
    return this.dealRound(true);
  }

  /** Deal once the table handshake has completed. */
  private dealRound(waitForDealReady: boolean): boolean {
    if (this.state.players.size !== MAX_PLAYERS) return false;
    const cannotAnte = this.state.playerOrder.find((playerId) => {
      const player = this.state.players.get(playerId);
      return player !== undefined && player.chips < this.ante;
    });
    if (cannotAnte) {
      this.returnToWaiting("有玩家筹码不足，无法开始新一局");
      return false;
    }
    this.turnTimeout?.clear();
    this.phaseTimeout?.clear();
    this.passingTimeout?.clear();
    this.passingSelections.clear();
    this.hands.clear();
    this.state.roundNumber += 1;
    this.state.phase = waitForDealReady ? "dealing" : "passing";
    this.state.currentTurn = "";
    this.state.turnDeadline = 0;
    this.state.phaseDeadline = waitForDealReady
      ? this.clock.currentTime + PHASE_READY_DURATION
      : 0;
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
      player.tableReady = false;
      player.dealReady = !waitForDealReady || this.isBotPlayer(playerId);
      player.nextRoundReady = false;
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

    this.state.message = this.botsEnabled
      ? "演示模式：已发牌，请选择三张牌传给下家"
      : "已发牌，请选择三张牌传给下家";
    this.publishRoomMetadata();
    this.broadcast("round_started", {
      roundNumber: this.state.roundNumber,
      ante: this.ante,
      pot: this.state.pot,
      cards: 52,
    });
    if (waitForDealReady) {
      this.broadcast("deal_started", {
        roundNumber: this.state.roundNumber,
        deadline: this.state.phaseDeadline,
      });
      this.armPhaseTimeout("dealing");
    } else {
      this.beginPassingAfterDeal();
    }
    return true;
  }

  private beginPlayingAfterDeal() {
    this.phaseTimeout?.clear();
    this.state.phaseDeadline = 0;
    this.beginPassingAfterDeal();
  }

  /** Enter the post-deal three-card passing stage. */
  private beginPassingAfterDeal() {
    this.turnTimeout?.clear();
    this.phaseTimeout?.clear();
    this.passingTimeout?.clear();
    this.passingSelections.clear();
    this.state.phase = "passing";
    this.state.currentTurn = "";
    this.state.turnDeadline = 0;
    this.state.phaseDeadline = this.clock.currentTime + PASSING_DURATION;
    this.state.message = this.botsEnabled
      ? "演示模式：请选择三张牌传给下家"
      : "请选择三张牌传给下家";
    this.publishRoomMetadata();
    this.broadcast("passing_started", {
      roundNumber: this.state.roundNumber,
      deadline: this.state.phaseDeadline,
      duration: PASSING_DURATION,
      cardCount: PASS_CARD_COUNT,
    });

    // Synthetic seats have no client from which to receive a selection. Make
    // their choice immediately so a demo room never waits thirty seconds for
    // a bot to answer.
    for (const playerId of this.state.playerOrder) {
      if (this.isBotPlayer(playerId)) this.autoSelectPassingCards(playerId);
    }
    this.passingTimeout = this.clock.setTimeout(
      () => this.completeMissingPassingSelections(),
      PASSING_DURATION,
    );
    this.tryResolvePassing();
  }

  private allRealPlayersHave(predicate: (player: Player) => boolean): boolean {
    for (const playerId of this.state.playerOrder) {
      if (this.isBotPlayer(playerId)) continue;
      const player = this.state.players.get(playerId);
      if (!player || !predicate(player)) return false;
    }
    return true;
  }

  private submitPassingCards(client: Client, message: PassMessage) {
    const playerId = client.sessionId;
    if (this.state.phase !== "passing") {
      this.sendError(client, "当前不是传牌阶段");
      return;
    }
    if (!this.state.players.has(playerId)) {
      this.sendError(client, "你不在本局座位中");
      return;
    }
    if (this.passingSelections.has(playerId)) {
      this.sendError(client, "你已经完成传牌选择");
      return;
    }

    const cardIds = this.readPassingCardIds(message);
    if (cardIds.length !== PASS_CARD_COUNT || new Set(cardIds).size !== PASS_CARD_COUNT) {
      this.sendError(client, `请选择 ${PASS_CARD_COUNT} 张不同的牌`);
      return;
    }

    const hand = this.hands.get(playerId) ?? [];
    const selected: Card[] = [];
    for (const cardId of cardIds) {
      const card = cardFromId(cardId);
      if (!card || !hand.some((candidate) => candidate.id === card.id)) {
        this.sendError(client, "只能选择自己手里的牌");
        return;
      }
      selected.push(card);
    }
    this.recordPassingSelection(playerId, selected);
  }

  private recordPassingSelection(playerId: string, selected: Card[]) {
    if (this.state.phase !== "passing" || this.passingSelections.has(playerId)) {
      return;
    }
    const hand = this.hands.get(playerId) ?? [];
    const cardIndexes = selected.map((card) => hand.findIndex((candidate) => candidate.id === card.id));
    if (cardIndexes.some((index) => index < 0)) {
      return;
    }
    this.passingSelections.set(playerId, selected.map((card) => ({ ...card })));
    this.broadcast("passing_selected", {
      roundNumber: this.state.roundNumber,
      playerId,
      cardIds: selected.map((card) => card.id),
      cardIndexes,
      suits: selected.map((card) => card.suit),
      ranks: selected.map((card) => card.rank),
    });

    const recipientId = this.nextSeat(playerId);
    if (!this.isBotPlayer(recipientId)) {
      const recipient = this.clients.find((candidate) => candidate.sessionId === recipientId);
      recipient?.send("passing_received", {
        roundNumber: this.state.roundNumber,
        fromPlayerId: playerId,
        toPlayerId: recipientId,
        cardIds: selected.map((card) => card.id),
        suits: selected.map((card) => card.suit),
        ranks: selected.map((card) => card.rank),
      });
    }
    this.tryResolvePassing();
  }

  private tryResolvePassing() {
    if (this.state.phase !== "passing" || this.passingSelections.size !== MAX_PLAYERS) {
      return;
    }

    this.passingTimeout?.clear();
    const nextHands = new Map<string, Card[]>();
    for (let index = 0; index < this.state.playerOrder.length; index += 1) {
      const playerId = this.state.playerOrder[index];
      const hand = this.hands.get(playerId) ?? [];
      const outgoing = new Set(
        (this.passingSelections.get(playerId) ?? []).map((card) => card.id),
      );
      const previousPlayerId = this.state.playerOrder[
        (index - 1 + this.state.playerOrder.length) % this.state.playerOrder.length
      ];
      const incoming = this.passingSelections.get(previousPlayerId) ?? [];
      nextHands.set(playerId, [
        ...hand.filter((card) => !outgoing.has(card.id)),
        ...incoming.map((card) => ({ ...card })),
      ]);
    }

    for (const [playerId, hand] of nextHands.entries()) {
      this.hands.set(playerId, hand);
      const player = this.state.players.get(playerId);
      if (player) player.handCount = hand.length;
    }

    this.state.phase = "playing";
    this.state.phaseDeadline = 0;
    this.state.message = this.botsEnabled
      ? "演示模式：传牌完成，梅花 2 先出"
      : "传牌完成，梅花 2 先出";
    this.publishRoomMetadata();
    this.broadcast("passing_completed", { roundNumber: this.state.roundNumber });
    const starter = this.findTwoOfClubsOwner() ?? this.state.playerOrder[0] ?? "";
    // Clients need time to finish all twelve pass flights and settle their
    // hand layouts before a synthetic starter begins its short turn timer.
    const openingDelay = this.isBotPlayer(starter) ? PASSING_BOT_DELAY : 0;
    this.setTurn(starter, openingDelay);
  }

  private completeMissingPassingSelections() {
    if (this.state.phase !== "passing") return;
    for (const playerId of this.state.playerOrder) {
      if (!this.passingSelections.has(playerId)) {
        this.autoSelectPassingCards(playerId);
      }
    }
    this.tryResolvePassing();
  }

  private autoSelectPassingCards(playerId: string) {
    if (this.passingSelections.has(playerId)) return;
    const hand = this.hands.get(playerId) ?? [];
    if (hand.length < PASS_CARD_COUNT) return;
    const shuffled = shuffleDeck(hand);
    this.recordPassingSelection(playerId, shuffled.slice(0, PASS_CARD_COUNT));
  }

  private readPassingCardIds(message: PassMessage): string[] {
    if (!message || typeof message !== "object") return [];
    const raw = (message as Record<string, unknown>).cardIds ??
      (message as Record<string, unknown>).cards;
    if (!Array.isArray(raw)) return [];
    return raw.map((entry) => {
      if (typeof entry === "string") return entry;
      if (entry && typeof entry === "object" && typeof (entry as Record<string, unknown>).cardId === "string") {
        return (entry as Record<string, string>).cardId;
      }
      return "";
    }).filter((cardId): cardId is string => cardId.length > 0);
  }

  private armPhaseTimeout(phase: string) {
    this.phaseTimeout?.clear();
    this.phaseTimeout = this.clock.setTimeout(() => {
      if (this.state.phase !== phase) return;
      this.returnToWaiting(`${phase === "dealing" ? "发牌动画" : "牌桌初始化"}超时，已返回准备房间`);
    }, PHASE_READY_DURATION);
  }

  private returnToWaiting(message: string) {
    this.phaseTimeout?.clear();
    this.turnTimeout?.clear();
    this.passingTimeout?.clear();
    this.passingSelections.clear();
    this.hands.clear();
    this.state.phase = "waiting";
    this.state.phaseDeadline = 0;
    this.state.currentTurn = "";
    this.state.turnDeadline = 0;
    this.state.trick.clear();
    this.state.lastTrick.clear();
    this.state.lastTrickWinner = "";
    this.state.lastTrickPoints = 0;
    this.state.leadSuit = "";
    this.state.heartsBroken = false;
    this.state.trickNumber = 0;
    this.state.pot = 0;
    for (const [playerId, player] of this.state.players.entries()) {
      player.tableReady = false;
      player.dealReady = false;
      player.nextRoundReady = false;
      player.ready = player.isHost || player.isBot;
      if (player.stake > 0) player.chips += player.stake;
      player.stake = 0;
      player.score = 0;
      player.tricksWon = 0;
      player.payout = 0;
      player.handCount = 0;
      if (this.isBotPlayer(playerId)) player.connected = true;
    }
    this.state.message = message;
    for (const sessionId of this.departedPlayers) this.releaseProfileSeat(sessionId);
    void this.unlock().catch(() => {});
    this.publishRoomMetadata();
    this.broadcast("room_reset", { reason: message });
  }

  private playCard(playerId: string, cardId: string, client?: Client, submittedIndex?: number): boolean {
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
    // The index is useful for deterministic client animation, but the card id
    // remains the authority because clients may sort their visible hand.
    if (submittedIndex !== undefined &&
      (!Number.isInteger(submittedIndex) || submittedIndex < 0 || submittedIndex >= hand.length)) {
      if (client) this.sendError(client, "出牌位置无效");
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
      cardIndex: handIndex,
      suit: card.suit,
      rank: card.rank,
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
    this.setTurn(winnerId, this.isBotPlayer(winnerId) ? BOT_TRICK_DELAY : 0);
  }

  private finishRound() {
    this.turnTimeout?.clear();

    const scores = this.state.playerOrder.map((playerId) => ({
      playerId,
      score: this.state.players.get(playerId)?.score ?? 0,
    }));
    const settlement = settlePot(scores, this.state.pot);
    try {
      if (this.profileSeats.size > 0) {
        getPlayerProfileStore().saveBalances([...this.profileSeats].map(([sessionId, playerId]) => ({
          playerId,
          owner: this.profileOwner(sessionId),
          chips: this.state.players.get(sessionId)!.chips + (settlement.payouts.get(sessionId) ?? 0),
        })));
      }
    } catch (error) {
      console.error("Failed to persist round settlement", error);
      this.returnToWaiting("存档保存失败，本局已取消并退还底注，请稍后重试");
      return;
    }
    this.state.phase = "finished";
    this.state.currentTurn = "";
    this.state.turnDeadline = 0;
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
      player.nextRoundReady = this.isBotPlayer(playerId);
      payouts[playerId] = payout;
    }
    this.state.message = "本局结束：最高分玩家共同请客";
    for (const sessionId of this.departedPlayers) this.releaseProfileSeat(sessionId);
    this.publishRoomMetadata();
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
  private setTurn(playerId: string, initialDelay = 0) {
    this.turnTimeout?.clear();
    if (this.state.phase !== "playing" || !playerId) {
      this.state.currentTurn = "";
      this.state.turnDeadline = 0;
      return;
    }
    this.state.currentTurn = playerId;
    this.state.turnCount += 1;
    // Real players receive the 15-second decision budget. Synthetic seats do
    // not have a client to wait for, so they answer their turn immediately
    // using the short server-side bot cadence.
    const turnDuration = this.isBotPlayer(playerId)
      ? BOT_TURN_DURATION : TURN_DURATION;
    this.state.turnDuration = turnDuration;
    const safeDelay = Math.max(0, Math.trunc(initialDelay));
    this.state.turnDeadline = this.clock.currentTime + safeDelay + turnDuration;
    this.turnTimeout = this.clock.setTimeout(
      () => this.autoPlayCurrentTurn(),
      safeDelay + turnDuration,
    );
    this.broadcast("turn_started", {
      playerId,
      deadline: this.state.turnDeadline,
      duration: turnDuration,
      delay: safeDelay,
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
    const selected = legal[Math.floor(Math.random() * legal.length)] ?? legal[0];
    this.broadcast("auto_play", {
      playerId,
      cardId: selected.id,
      cardIndex: hand.indexOf(selected),
      automated: this.isBotPlayer(playerId),
    });
    this.playCard(playerId, selected.id);
  }

  /** Add the three non-network seats used by the single-client demo room. */
  private addDemoBots() {
    while (this.state.players.size < MAX_PLAYERS) {
      this.addBot();
    }
  }

  private addBot() {
    const seat = this.state.players.size;
    const playerId = this.createBotId(seat);
    const player = new Player();
    player.name = `机器人 ${seat + 1}`;
    player.avatarId = seat % AVATAR_COUNT + 1;
    player.seat = seat;
    player.chips = this.startingChips;
    player.connected = true;
    player.isTreating = false;
    player.ready = true;
    player.isBot = true;
    player.isHost = false;
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

  private profileOwner(sessionId: string): string {
    return `${this.roomId}:${sessionId}`;
  }

  private releaseProfileSeat(sessionId: string) {
    const profileId = this.profileSeats.get(sessionId);
    if (!profileId) return;
    getPlayerProfileStore().releaseSeat(profileId, this.profileOwner(sessionId));
    this.profileSeats.delete(sessionId);
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
      const suit = typeof legacy.suit === "string" ? legacy.suit.toLowerCase() : "";
      const rank = legacy.rank;
      if (suit && (typeof rank === "string" || typeof rank === "number")) {
        const suitPrefix = suit.startsWith("club") ? "Club"
          : suit.startsWith("diamond") ? "Diamond"
          : suit.startsWith("heart") ? "Heart"
          : suit.startsWith("spade") ? "Spade" : "";
        const rankText = typeof rank === "number"
          ? ({ 11: "J", 12: "Q", 13: "K", 14: "A" } as Record<number, string>)[Math.trunc(rank)] ?? String(Math.trunc(rank))
          : ({ jack: "J", queen: "Q", king: "K", ace: "A" } as Record<string, string>)[rank.toLowerCase()] ?? rank;
        if (suitPrefix && rankText) return suitPrefix + rankText;
      }
    }
    return undefined;
  }

  private readCardIndex(message: PlayMessage): number | undefined {
    if (!message || typeof message !== "object") return undefined;
    const value = (message as Record<string, unknown>).cardIndex;
    return typeof value === "number" && Number.isInteger(value) ? value : undefined;
  }

  private isCardDescriptorConsistent(message: PlayMessage, cardId: string): boolean {
    if (!message || typeof message !== "object") return true;
    const rawSuit = (message as Record<string, unknown>).suit;
    const rawRank = (message as Record<string, unknown>).rank;
    if (rawSuit === undefined && rawRank === undefined) return true;
    const expected = this.readCardId({ suit: rawSuit, rank: rawRank });
    return expected === cardId;
  }

  private readName(value: unknown, seatNumber: number): string {
    if (typeof value !== "string") {
      return `玩家 ${seatNumber}`;
    }
    const trimmed = value.trim().slice(0, 20);
    return trimmed.length > 0 ? trimmed : `玩家 ${seatNumber}`;
  }

  private readRoomName(value: unknown): string {
    if (typeof value !== "string") return "房间";
    const name = value.trim();
    return name.length > 0 ? name.slice(0, 32) : "房间";
  }

  /** Publish enough public data for the lobby without exposing hands. */
  private publishRoomMetadata() {
    const players = [...this.state.players.values()];
    const host = this.state.players.get(this.state.hostId);
    void this.setMatchmaking({
      metadata: {
        displayName: this.displayName,
        phase: this.state.phase,
        playerCount: players.length,
        maxPlayers: MAX_PLAYERS,
        readyCount: players.filter((player) => player.ready).length,
        bots: players.some((player) => player.isBot),
        hostName: host?.name ?? "",
      },
    }).catch(() => {
      // A room may publish one final state patch while the test/server is
      // shutting down; metadata is best-effort and never affects the match.
    });
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
