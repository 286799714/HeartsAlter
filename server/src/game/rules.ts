/**
 * Pure rules for the Hearts Alter card game.
 *
 * The room adapter should own transport concerns (sessions, timers and
 * Colyseus schemas) and use these functions as its server-authoritative
 * source of truth.  Card ids intentionally match the supplied image assets:
 * `Club2`, `Diamond10`, `HeartQ`, `SpadeA`, etc.
 */

export const SUITS = ["clubs", "diamonds", "hearts", "spades"] as const;
export type Suit = (typeof SUITS)[number];

export const RANKS = [
  "2",
  "3",
  "4",
  "5",
  "6",
  "7",
  "8",
  "9",
  "10",
  "J",
  "Q",
  "K",
  "A",
] as const;
export type Rank = (typeof RANKS)[number];

export const PLAYER_COUNT = 4;
export const CARD_COUNT = SUITS.length * RANKS.length;
export const CARDS_PER_PLAYER = CARD_COUNT / PLAYER_COUNT;
/** Deal totals depend on how many hearts are played after the six-point Q♠. */
export const MIN_TOTAL_POINTS = 19;
export const MAX_TOTAL_POINTS = 32;

const SUIT_ASSET_PREFIX: Record<Suit, string> = {
  clubs: "Club",
  diamonds: "Diamond",
  hearts: "Heart",
  spades: "Spade",
};

const RANK_VALUE: Record<Rank, number> = {
  "2": 2,
  "3": 3,
  "4": 4,
  "5": 5,
  "6": 6,
  "7": 7,
  "8": 8,
  "9": 9,
  "10": 10,
  J: 11,
  Q: 12,
  K: 13,
  A: 14,
};

/** A standard playing card (jokers are deliberately not representable). */
export interface Card {
  readonly id: string;
  readonly suit: Suit;
  readonly rank: Rank;
}

/** A card played by a player into a trick. */
export interface PlayedCard<P = string> {
  readonly playerId: P;
  readonly card: Card;
}

/** A completed trick, used by {@link scoreTricks}. */
export interface ScoredTrick<P = string> {
  readonly winnerId: P;
  readonly plays: readonly PlayedCard<P>[];
}

export type RandomSource = () => number;

/** Construct one card using the same id as its supplied image asset. */
export function createCard(suit: Suit, rank: Rank): Card {
  return {
    id: `${SUIT_ASSET_PREFIX[suit]}${rank}`,
    suit,
    rank,
  };
}

/** Create a fresh, ordered 52-card deck. No jokers are included. */
export function createDeck(): Card[] {
  const deck: Card[] = [];
  for (const suit of SUITS) {
    for (const rank of RANKS) {
      deck.push(createCard(suit, rank));
    }
  }
  return deck;
}

/** Convert an asset-compatible id back into a card, or return undefined. */
export function cardFromId(id: string): Card | undefined {
  if (typeof id !== "string") {
    return undefined;
  }
  for (const suit of SUITS) {
    const prefix = SUIT_ASSET_PREFIX[suit];
    if (!id.startsWith(prefix)) {
      continue;
    }
    const rank = id.slice(prefix.length) as Rank;
    if ((RANKS as readonly string[]).includes(rank)) {
      return createCard(suit, rank);
    }
  }
  return undefined;
}

/** True when two card values denote the same physical card. */
export function sameCard(a: Card, b: Card): boolean {
  return a.id === b.id;
}

/** Numeric rank ordering used to resolve a trick. */
export function rankValue(card: Card): number {
  const value = RANK_VALUE[card.rank];
  if (value === undefined) {
    throw new RangeError(`unknown card rank: ${String(card.rank)}`);
  }
  return value;
}

/** Fisher–Yates shuffle. The input array is never mutated. */
export function shuffleDeck<T>(cards: readonly T[], random: RandomSource = Math.random): T[] {
  const result = [...cards];
  for (let index = result.length - 1; index > 0; index -= 1) {
    const sample = random();
    if (!Number.isFinite(sample) || sample < 0 || sample >= 1) {
      throw new RangeError("random source must return a number in [0, 1)");
    }
    const swapIndex = Math.floor(sample * (index + 1));
    [result[index], result[swapIndex]] = [result[swapIndex], result[index]];
  }
  return result;
}

function isStandardCard(card: Card): boolean {
  if (!card || typeof card.id !== "string") {
    return false;
  }
  if (!(SUITS as readonly unknown[]).includes(card.suit)) {
    return false;
  }
  if (!(RANKS as readonly unknown[]).includes(card.rank)) {
    return false;
  }
  return card.id === createCard(card.suit, card.rank).id;
}

function assertValidDeck(deck: readonly Card[]): void {
  const ids = new Set<string>();
  for (const card of deck) {
    if (!isStandardCard(card)) {
      throw new RangeError(`deck contains an invalid card: ${String(card?.id)}`);
    }
    if (ids.has(card.id)) {
      throw new RangeError(`deck contains a duplicate card: ${card.id}`);
    }
    ids.add(card.id);
  }
}

/**
 * Deal a deck in round-robin order. The caller controls shuffle order; this
 * makes deterministic tests and replay straightforward.
 */
export function dealHands(
  deck: readonly Card[] = createDeck(),
  playerCount: number = PLAYER_COUNT,
): Card[][] {
  if (!Number.isInteger(playerCount) || playerCount < 1) {
    throw new RangeError("player count must be a positive integer");
  }
  // Validate card identity before checking divisibility so callers get a
  // useful duplicate/invalid-card error for malformed decks of any size.
  assertValidDeck(deck);
  if (deck.length === 0 || deck.length % playerCount !== 0) {
    throw new RangeError("deck size must be positive and divisible by player count");
  }

  const hands = Array.from({ length: playerCount }, () => [] as Card[]);
  deck.forEach((card, index) => hands[index % playerCount].push(card));
  return hands;
}

/** Shuffle a fresh standard deck and deal four equal hands. */
export function dealShuffledHands(random: RandomSource = Math.random): Card[][] {
  return dealHands(shuffleDeck(createDeck(), random), PLAYER_COUNT);
}

/** Alias that makes the four-player intent explicit at call sites. */
export const dealFourHands = dealShuffledHands;

export interface LegalPlayOptions {
  /** Whether a heart has already been played in an earlier trick. */
  readonly heartsBroken?: boolean;
  /** Whether this is the first trick of the deal. */
  readonly firstTrick?: boolean;
  /** Restrict first-trick point cards when leading or following suit (default true). */
  readonly enforceFirstTrickPoints?: boolean;
}

export function isQueenOfSpades(card: Card): boolean {
  return card.suit === "spades" && card.rank === "Q";
}

/** Whether a card contributes penalty points. */
export function isPointCard(card: Card): boolean {
  return card.suit === "hearts" || isQueenOfSpades(card);
}

/** Points carried by a card at the moment it is played. */
export function cardPoints(card: Card, queenOfSpadesPlayed = false): number {
  if (card.suit === "hearts") {
    return queenOfSpadesPlayed ? 2 : 1;
  }
  return isQueenOfSpades(card) ? 6 : 0;
}

/** Score cards in play order, including a Q♠ played earlier in the deal. */
export function scoreCards(cards: readonly Card[], queenOfSpadesPlayed = false): number {
  let points = 0;
  for (const card of cards) {
    points += cardPoints(card, queenOfSpadesPlayed);
    queenOfSpadesPlayed ||= isQueenOfSpades(card);
  }
  return points;
}

/**
 * Return the cards that may legally be played from a hand.
 *
 * `trick` contains cards already played in the current trick (in play order),
 * and therefore its first card determines the lead suit. The function keeps
 * hand order, which gives the room a deterministic timeout choice.
 */
export function getLegalCards(
  hand: readonly Card[],
  trick: readonly Card[] = [],
  options: LegalPlayOptions = {},
): Card[] {
  if (hand.length === 0) {
    return [];
  }

  const heartsBroken = options.heartsBroken ?? false;
  const firstTrick = options.firstTrick ?? false;
  const enforceFirstTrickPoints = options.enforceFirstTrickPoints ?? true;
  const isLeading = trick.length === 0;

  let legal = [...hand];

  // The room gives the opening turn to the holder of Club2, but that player
  // may lead any card allowed by the ordinary lead and first-trick rules.

  if (!isLeading) {
    const leadSuit = trick[0].suit;
    const followsLead = hand.some((card) => card.suit === leadSuit);
    if (followsLead) {
      legal = legal.filter((card) => card.suit === leadSuit);
    } else {
      // Being void requires discarding a point card when one is held, even
      // on the first trick or before hearts are broken.
      const pointCards = hand.filter(isPointCard);
      return pointCards.length > 0 ? pointCards : legal;
    }
  }

  // Hearts may not be led until broken, unless the hand has no alternative
  // suit. This only constrains the lead; a player must still follow a heart
  // lead when they have one. Q♠ is not a heart and is never affected by this
  // flag (its six points are handled independently by isPointCard).
  if (isLeading && !heartsBroken && hand.some((card) => card.suit !== "hearts")) {
    legal = legal.filter((card) => card.suit !== "hearts");
  }

  // On the first trick, avoid points when leading or following suit if a
  // non-point option exists. The mandatory void discard was handled above.
  if (firstTrick && enforceFirstTrickPoints && legal.some((card) => !isPointCard(card))) {
    legal = legal.filter((card) => !isPointCard(card));
  }

  return legal;
}

/** Test whether one attempted play is legal under the supplied context. */
export function isLegalPlay(
  hand: readonly Card[],
  card: Card,
  trick: readonly Card[] = [],
  options: LegalPlayOptions = {},
): boolean {
  if (!hand.some((candidate) => sameCard(candidate, card))) {
    return false;
  }
  return getLegalCards(hand, trick, options).some((candidate) => sameCard(candidate, card));
}

/** Return the index of the winning play in a trick. */
export function determineTrickWinnerIndex<P>(plays: readonly PlayedCard<P>[]): number {
  if (plays.length === 0) {
    throw new RangeError("cannot resolve an empty trick");
  }

  const seenPlayers = new Set<P>();
  for (const play of plays) {
    if (seenPlayers.has(play.playerId)) {
      throw new RangeError("a player may appear only once in a trick");
    }
    seenPlayers.add(play.playerId);
  }

  const leadSuit = plays[0].card.suit;
  let winnerIndex = 0;
  for (let index = 1; index < plays.length; index += 1) {
    const candidate = plays[index].card;
    const winner = plays[winnerIndex].card;
    if (candidate.suit === leadSuit &&
      (winner.suit !== leadSuit || rankValue(candidate) > rankValue(winner))) {
      winnerIndex = index;
    }
  }
  return winnerIndex;
}

/** Return the player id of the winning play in a trick. */
export function determineTrickWinner<P>(plays: readonly PlayedCard<P>[]): P {
  return plays[determineTrickWinnerIndex(plays)].playerId;
}

/** Score completed tricks in chronological order into a player-id keyed map. */
export function scoreTricks<P>(tricks: readonly ScoredTrick<P>[]): Map<P, number> {
  const scores = new Map<P, number>();
  let queenOfSpadesPlayed = false;
  for (const trick of tricks) {
    const cards = trick.plays.map((play) => play.card);
    const points = scoreCards(cards, queenOfSpadesPlayed);
    queenOfSpadesPlayed ||= cards.some(isQueenOfSpades);
    scores.set(trick.winnerId, (scores.get(trick.winnerId) ?? 0) + points);
  }
  return scores;
}

export type ZeroWeightStrategy = "equal" | "unallocated";

export interface PlayerScore<P = string> {
  readonly playerId: P;
  readonly score: number;
}

export interface SettlementOptions {
  /** What to do when all eligible players have zero points (default equal). */
  readonly zeroWeightStrategy?: ZeroWeightStrategy;
}

export interface SettlementResult<P = string> {
  readonly highestScore: number;
  readonly highestPlayerIds: P[];
  /** Integer chips assigned to every input player, including highest scorers. */
  readonly payouts: Map<P, number>;
  /** Sum of payouts; always `pot - unallocatedPot`. */
  readonly distributed: number;
  /** Explicitly accounted-for chips that were not assigned to a player. */
  readonly unallocatedPot: number;
}

function assertNonNegativeSafeInteger(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new RangeError(`${label} must be a non-negative safe integer`);
  }
}

function distributeEqual<P>(
  entries: readonly PlayerScore<P>[],
  pot: number,
  payouts: Map<P, number>,
): number {
  if (entries.length === 0) {
    return 0;
  }
  const count = BigInt(entries.length);
  const whole = BigInt(pot) / count;
  const remainder = BigInt(pot) % count;
  entries.forEach((entry, index) => {
    payouts.set(entry.playerId, Number(whole + (BigInt(index) < remainder ? 1n : 0n)));
  });
  return pot;
}

/**
 * Allocate a pot by score, excluding all players tied for the highest score.
 *
 * Shares are integer chips. We use exact BigInt numerators and the
 * largest-remainder method, breaking equal remainders by input/seat order.
 * If every eligible score is zero, the default policy is a deterministic
 * equal split among eligible players. If every player is tied for highest,
 * the same equal fallback includes all players so the pot remains conserved.
 */
export function settlePot<P>(
  players: readonly PlayerScore<P>[],
  pot: number,
  options: SettlementOptions = {},
): SettlementResult<P> {
  assertNonNegativeSafeInteger(pot, "pot");

  const ids = new Set<P>();
  for (const player of players) {
    if (ids.has(player.playerId)) {
      throw new RangeError("duplicate player id in score list");
    }
    ids.add(player.playerId);
    assertNonNegativeSafeInteger(player.score, "score");
  }

  const payouts = new Map<P, number>();
  for (const player of players) {
    payouts.set(player.playerId, 0);
  }

  if (players.length === 0) {
    return {
      highestScore: 0,
      highestPlayerIds: [],
      payouts,
      distributed: 0,
      unallocatedPot: pot,
    };
  }

  const highestScore = players.reduce((max, player) => Math.max(max, player.score), 0);
  const highestPlayerIds = players
    .filter((player) => player.score === highestScore)
    .map((player) => player.playerId);
  const eligible = players.filter((player) => player.score < highestScore);

  // A complete tie (or a one-player room) has no non-highest recipient. Use
  // the same explicit zero-weight fallback over everyone to conserve chips.
  const recipients = eligible.length > 0 ? eligible : players;
  const totalWeight = eligible.reduce((sum, player) => sum + BigInt(player.score), 0n);

  if (eligible.length === 0 || totalWeight === 0n) {
    if (eligible.length > 0 && options.zeroWeightStrategy === "unallocated") {
      return {
        highestScore,
        highestPlayerIds,
        payouts,
        distributed: 0,
        unallocatedPot: pot,
      };
    }
    const distributed = distributeEqual(recipients, pot, payouts);
    return {
      highestScore,
      highestPlayerIds,
      payouts,
      distributed,
      unallocatedPot: pot - distributed,
    };
  }

  const denominator = totalWeight;
  const allocations = eligible.map((player, index) => {
    const numerator = BigInt(pot) * BigInt(player.score);
    return {
      player,
      index,
      whole: numerator / denominator,
      remainder: numerator % denominator,
    };
  });

  let distributedBig = 0n;
  for (const allocation of allocations) {
    payouts.set(allocation.player.playerId, Number(allocation.whole));
    distributedBig += allocation.whole;
  }

  // The number of leftover chips is smaller than the recipient count, so it
  // is safe to convert to a regular number for the bounded loop.
  const leftover = BigInt(pot) - distributedBig;
  allocations.sort((left, right) => {
    if (left.remainder === right.remainder) {
      return left.index - right.index;
    }
    return left.remainder > right.remainder ? -1 : 1;
  });
  for (let index = 0; index < Number(leftover); index += 1) {
    const allocation = allocations[index];
    payouts.set(allocation.player.playerId, (payouts.get(allocation.player.playerId) ?? 0) + 1);
  }

  return {
    highestScore,
    highestPlayerIds,
    payouts,
    distributed: pot,
    unallocatedPot: 0,
  };
}
