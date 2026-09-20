import { schema, t, type SchemaType } from "@colyseus/schema";

/** A card currently visible in the centre trick. The owning hand stays private. */
export const TrickCard = schema({
  playerId: t.string(),
  cardId: t.string(),
});
export type TrickCard = SchemaType<typeof TrickCard>;

/** A completed trick checkpoint in the public round progress journal. */
export const ResolvedTrick = schema({
  winnerId: t.string(),
  points: t.uint8(),
  /** Sequence of the fourth play that completed this trick. */
  playSequence: t.uint8(),
});
export type ResolvedTrick = SchemaType<typeof ResolvedTrick>;

/** Public scoreboard entry for one seat. */
export const Player = schema({
  name: t.string().default("玩家"),
  seat: t.uint8().default(0),
  chips: t.int32().default(1000),
  stake: t.int32().default(0),
  score: t.uint16().default(0),
  handCount: t.uint8().default(0),
  tricksWon: t.uint8().default(0),
  payout: t.int32().default(0),
  connected: t.boolean().default(true),
  isTreating: t.boolean().default(false),
  /** The lobby owner is always ready and cannot toggle this flag off. */
  ready: t.boolean().default(false),
  /** Synthetic seats are public so the ready-room can render their badge. */
  isBot: t.boolean().default(false),
  /** Exactly one seat is the room owner. */
  isHost: t.boolean().default(false),
  /** Signals emitted by the local table/animation controller. */
  tableReady: t.boolean().default(false),
  dealReady: t.boolean().default(false),
  nextRoundReady: t.boolean().default(false),
  /** Stable public save identity; never the device key. Empty for legacy guests/bots. */
  profileId: t.string().default(""),
  avatarId: t.uint8().default(1),
});
export type Player = SchemaType<typeof Player>;

/**
 * Public state for the authoritative Hearts room.
 *
 * Hands are intentionally absent. Each client receives its own hand through a
 * private `hand` message, while this state contains only information that every
 * seat is allowed to see.
 */
export const MyRoomState = schema({
  phase: t.string().default("waiting"),
  players: t.map(Player),
  playerOrder: t.array("string"),
  currentTurn: t.string().default(""),
  turnDeadline: t.float64().default(0),
  turnDuration: t.uint16().default(15000),
  turnCount: t.uint16().default(0),
  trickNumber: t.uint8().default(0),
  leadSuit: t.string().default(""),
  heartsBroken: t.boolean().default(false),
  trick: t.array(TrickCard),
  lastTrick: t.array(TrickCard),
  lastTrickWinner: t.string().default(""),
  lastTrickPoints: t.uint8().default(0),
  pot: t.int32().default(0),
  ante: t.int32().default(100),
  roundNumber: t.uint16().default(0),
  message: t.string().default("等待四名玩家加入"),
  /** Session id of the room owner; the owner is always ready. */
  hostId: t.string().default(""),
  /** A lobby-created room waits for an explicit host start command. */
  lobbyManaged: t.boolean().default(false),
  /** Deadline for a phase-level handshake (table/deal), in server ms. */
  phaseDeadline: t.float64().default(0),
  /** Room rules remain fixed from the start handshake through settlement. */
  heartsBreakingEnabled: t.boolean().default(false),
  mustDiscardPointsWhenVoid: t.boolean().default(false),
  /**
   * Append-only public progress for the active round. Clients use these
   * journals to fill message gaps after a delayed patch or reconnection.
   */
  playHistory: t.array(TrickCard),
  trickHistory: t.array(ResolvedTrick),
});
export type MyRoomState = SchemaType<typeof MyRoomState>;

/** Public listing row shown by the lobby. */
export const LobbyRoomInfo = schema({
  roomId: t.string().default(""),
  name: t.string().default("房间"),
  phase: t.string().default("waiting"),
  playerCount: t.uint8().default(0),
  maxPlayers: t.uint8().default(4),
  readyCount: t.uint8().default(0),
  bots: t.boolean().default(false),
  hostName: t.string().default(""),
});
export type LobbyRoomInfo = SchemaType<typeof LobbyRoomInfo>;

/** State for the singleton lobby room. */
export const LobbyState = schema({
  rooms: t.map(LobbyRoomInfo),
  message: t.string().default("正在加载房间…"),
});
export type LobbyState = SchemaType<typeof LobbyState>;
