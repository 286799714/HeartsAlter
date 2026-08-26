import { schema, t, type SchemaType } from "@colyseus/schema";

/** A card currently visible in the centre trick. The owning hand stays private. */
export const TrickCard = schema({
  playerId: t.string(),
  cardId: t.string(),
});
export type TrickCard = SchemaType<typeof TrickCard>;

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
});
export type MyRoomState = SchemaType<typeof MyRoomState>;
