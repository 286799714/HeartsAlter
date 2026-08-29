import assert from "node:assert/strict";

import {
  CARD_COUNT,
  PLAYER_COUNT,
  RANKS,
  SUITS,
  TOTAL_POINTS,
  type Card,
  type PlayedCard,
  cardPoints,
  createDeck,
  dealHands,
  determineTrickWinner,
  getLegalCards,
  isLegalPlay,
  scoreCards,
  scoreTricks,
  settlePot,
  shuffleDeck,
} from "../src/game/rules.js";

const deck = createDeck();

function card(id: string): Card {
  const found = deck.find((candidate) => candidate.id === id);
  assert.ok(found, `expected ${id} in standard deck`);
  return found;
}

describe("Hearts rules", () => {
  it("creates exactly the 52 non-joker cards with asset-compatible ids", () => {
    assert.equal(CARD_COUNT, 52);
    assert.equal(TOTAL_POINTS, 19);
    assert.equal(PLAYER_COUNT, 4);
    assert.equal(SUITS.length, 4);
    assert.equal(RANKS.length, 13);
    assert.equal(deck.length, CARD_COUNT);
    assert.equal(new Set(deck.map((item) => item.id)).size, CARD_COUNT);
    assert.ok(deck.every((item) => !item.id.toUpperCase().includes("JOKER")));
    assert.deepEqual(deck[0], { id: "Club2", suit: "clubs", rank: "2" });
    assert.deepEqual(deck.at(-1), { id: "SpadeA", suit: "spades", rank: "A" });
  });

  it("shuffles without mutating the source and deals four equal hands", () => {
    const original = createDeck();
    const shuffled = shuffleDeck(original, () => 0.37);
    assert.equal(shuffled.length, CARD_COUNT);
    assert.deepEqual(original, createDeck());
    assert.deepEqual(
      new Set(shuffled.map((item) => item.id)),
      new Set(original.map((item) => item.id)),
    );

    const hands = dealHands(shuffled, PLAYER_COUNT);
    assert.equal(hands.length, PLAYER_COUNT);
    assert.ok(hands.every((hand) => hand.length === 13));
    assert.equal(new Set(hands.flat().map((item) => item.id)).size, CARD_COUNT);
  });

  it("rejects malformed decks and impossible player counts", () => {
    assert.throws(() => dealHands(deck.slice(0, 51), PLAYER_COUNT), /52|divisible/i);
    assert.throws(() => dealHands(deck, 3), /player|divisible/i);
    assert.throws(() => dealHands([...deck, deck[0]], PLAYER_COUNT), /duplicate|52/i);
  });

  it("forces the two of clubs on the first lead", () => {
    const hand = [card("Club2"), card("ClubA"), card("Heart3"), card("SpadeQ")];
    const legal = getLegalCards(hand, [], { firstTrick: true, heartsBroken: false });
    assert.deepEqual(legal.map((item) => item.id), ["Club2"]);
    assert.equal(isLegalPlay(hand, card("ClubA"), [], { firstTrick: true }), false);
    assert.equal(isLegalPlay(hand, card("Club2"), [], { firstTrick: true }), true);
  });

  it("requires following the lead suit when possible", () => {
    const hand = [card("Club3"), card("Heart4"), card("Spade5")];
    const trick = [card("Club10")];
    assert.deepEqual(
      getLegalCards(hand, trick, { firstTrick: false }).map((item) => item.id),
      ["Club3"],
    );
    assert.equal(isLegalPlay(hand, card("Heart4"), trick), false);
  });

  it("does not lead hearts before they are broken when another suit is held", () => {
    const hand = [card("Heart2"), card("Diamond3")];
    assert.deepEqual(
      getLegalCards(hand, [], { firstTrick: false, heartsBroken: false }).map((item) => item.id),
      ["Diamond3"],
    );
    assert.deepEqual(
      getLegalCards(hand, [], { firstTrick: false, heartsBroken: true }).map((item) => item.id),
      ["Heart2", "Diamond3"],
    );
  });

  it("allows a heart discard when void in the lead suit and keeps Q♠ independent", () => {
    const hand = [card("Heart2"), card("SpadeQ"), card("Diamond3")];
    const trick = [card("ClubA")];
    const legal = getLegalCards(hand, trick, {
      firstTrick: false,
      heartsBroken: false,
    });
    assert.deepEqual(legal.map((item) => item.id), ["Heart2", "SpadeQ", "Diamond3"]);
    assert.equal(isLegalPlay(hand, card("Heart2"), trick, { heartsBroken: false }), true);
    assert.equal(isLegalPlay(hand, card("SpadeQ"), trick, { heartsBroken: false }), true);
    assert.deepEqual(
      getLegalCards(hand, [], { firstTrick: false, heartsBroken: false }).map((item) => item.id),
      ["SpadeQ", "Diamond3"],
    );
  });

  it("keeps point cards out of the first trick when a safe discard exists", () => {
    const hand = [card("Club3"), card("Heart2"), card("SpadeQ")];
    const legal = getLegalCards(hand, [card("Diamond10")], {
      firstTrick: true,
      heartsBroken: false,
    });
    assert.deepEqual(legal.map((item) => item.id), ["Club3"]);
    assert.equal(isLegalPlay(hand, card("Heart2"), [card("Diamond10")], { firstTrick: true }), false);
    assert.equal(isLegalPlay(hand, card("SpadeQ"), [card("Diamond10")], { firstTrick: true }), false);
  });

  it("allows a point discard on the first trick when no safe card exists", () => {
    const hand = [card("Heart2"), card("SpadeQ")];
    const trick = [card("Diamond10")];
    assert.deepEqual(
      getLegalCards(hand, trick, { firstTrick: true, heartsBroken: false }).map((item) => item.id),
      ["Heart2", "SpadeQ"],
    );
  });

  it("selects the highest card of the lead suit and ignores off-suit cards", () => {
    const plays: PlayedCard<string>[] = [
      { playerId: "north", card: card("Club2") },
      { playerId: "east", card: card("ClubQ") },
      { playerId: "south", card: card("HeartA") },
      { playerId: "west", card: card("Club10") },
    ];
    assert.equal(determineTrickWinner(plays), "east");
  });

  it("scores each heart as one and the queen of spades as six", () => {
    assert.equal(cardPoints(card("HeartA")), 1);
    assert.equal(cardPoints(card("SpadeQ")), 6);
    assert.equal(cardPoints(card("ClubA")), 0);
    assert.equal(scoreCards([card("HeartA"), card("Heart2"), card("SpadeQ")]), 8);

    const tricks = [
      {
        winnerId: "a",
        plays: [
          { playerId: "a", card: card("HeartA") },
          { playerId: "b", card: card("Club2") },
        ],
      },
      {
        winnerId: "b",
        plays: [
          { playerId: "b", card: card("SpadeQ") },
          { playerId: "a", card: card("Diamond2") },
        ],
      },
    ];
    const scores = scoreTricks(tricks);
    assert.equal(scores.get("a"), 1);
    assert.equal(scores.get("b"), 6);
  });

  it("allocates an integer pot by non-highest score weights", () => {
    const result = settlePot(
      [
        { playerId: "a", score: 10 },
        { playerId: "b", score: 5 },
        { playerId: "c", score: 2 },
        { playerId: "d", score: 0 },
      ],
      400,
    );
    assert.equal(result.highestScore, 10);
    assert.deepEqual(result.highestPlayerIds, ["a"]);
    assert.equal(result.payouts.get("a"), 0);
    assert.equal(result.payouts.get("b"), 286);
    assert.equal(result.payouts.get("c"), 114);
    assert.equal(result.payouts.get("d"), 0);
    assert.equal(result.distributed + result.unallocatedPot, 400);
  });

  it("excludes all tied highest scorers and splits tied weights deterministically", () => {
    const tied = settlePot(
      [
        { playerId: "a", score: 10 },
        { playerId: "b", score: 10 },
        { playerId: "c", score: 5 },
        { playerId: "d", score: 5 },
      ],
      401,
    );
    assert.deepEqual(tied.highestPlayerIds, ["a", "b"]);
    assert.equal(tied.payouts.get("a"), 0);
    assert.equal(tied.payouts.get("b"), 0);
    assert.equal(tied.payouts.get("c"), 201);
    assert.equal(tied.payouts.get("d"), 200);
    assert.equal(tied.distributed + tied.unallocatedPot, 401);
  });

  it("uses equal fallback for zero weights and for an all-highest tie", () => {
    const zeroWeight = settlePot(
      [
        { playerId: "a", score: 14 },
        { playerId: "b", score: 0 },
        { playerId: "c", score: 0 },
        { playerId: "d", score: 0 },
      ],
      400,
    );
    assert.equal(zeroWeight.payouts.get("a"), 0);
    assert.equal(zeroWeight.payouts.get("b"), 134);
    assert.equal(zeroWeight.payouts.get("c"), 133);
    assert.equal(zeroWeight.payouts.get("d"), 133);
    assert.equal(zeroWeight.unallocatedPot, 0);

    const allTied = settlePot(
      [
        { playerId: "a", score: 3 },
        { playerId: "b", score: 3 },
        { playerId: "c", score: 3 },
        { playerId: "d", score: 3 },
      ],
      401,
    );
    assert.deepEqual(allTied.payouts, new Map([
      ["a", 101],
      ["b", 100],
      ["c", 100],
      ["d", 100],
    ]));
    assert.equal(allTied.distributed + allTied.unallocatedPot, 401);
  });

  it("rejects invalid scores, stakes, and duplicate player ids", () => {
    assert.throws(() => settlePot([{ playerId: "a", score: -1 }], 10), /score/i);
    assert.throws(() => settlePot([{ playerId: "a", score: 1 }], -1), /pot/i);
    assert.throws(() => settlePot([
      { playerId: "a", score: 1 },
      { playerId: "a", score: 2 },
    ], 10), /duplicate/i);
  });
});
