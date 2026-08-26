import assert from "node:assert/strict";
import { ColyseusTestServer, boot } from "@colyseus/testing";

import appConfig from "../src/app.config.js";
import {
  DEFAULT_ANTE,
  DEFAULT_STARTING_CHIPS,
  MAX_CHIPS_PER_PLAYER,
  MAX_PLAYERS,
} from "../src/rooms/MyRoom.js";
import { MyRoomState } from "../src/rooms/schema/MyRoomState.js";
import {
  cardFromId,
  getLegalCards,
} from "../src/game/rules.js";

describe("authoritative Hearts room", () => {
  let colyseus: ColyseusTestServer<typeof appConfig>;

  before(async () => { colyseus = await boot(appConfig); });
  after(async () => { await colyseus.shutdown(); });

  beforeEach(async () => {
    await colyseus.cleanup();
  });

  it("waits for four seats, charges one equal ante, and deals 13 cards per seat", async () => {
    const room = await colyseus.createRoom<MyRoomState>("hearts", {});
    const clients = [];
    for (let index = 0; index < MAX_PLAYERS; index += 1) {
      clients.push(await colyseus.connectTo(room, { name: `玩家${index + 1}` }));
    }

    assert.equal(room.state.phase, "playing");
    assert.equal(room.state.players.size, MAX_PLAYERS);
    assert.equal(room.state.pot, 400);
    assert.equal(room.state.ante, 100);
    assert.equal(room.state.playerOrder.length, MAX_PLAYERS);
    for (const player of room.state.players.values()) {
      assert.equal(player.handCount, 13);
      assert.equal(player.stake, 100);
      assert.equal(player.chips, 900);
    }

    // The first turn is always owned by the seat holding the two of clubs;
    // this also proves the room did not invent a client-controlled turn.
    assert.ok(room.state.playerOrder.includes(room.state.currentTurn));
    assert.ok(clients.some((client) => client.sessionId === room.state.currentTurn));
  });

  it("normalizes untrusted chip options to schema-safe values", async () => {
    const oversized = await colyseus.createRoom<MyRoomState>("hearts", {
      ante: Number.MAX_SAFE_INTEGER,
      startingChips: Number.MAX_SAFE_INTEGER,
    });
    for (let index = 0; index < MAX_PLAYERS; index += 1) {
      await colyseus.connectTo(oversized);
    }
    assert.equal(oversized.state.ante, DEFAULT_ANTE);
    assert.equal(oversized.state.pot, DEFAULT_ANTE * MAX_PLAYERS);
    assert.ok([...oversized.state.players.values()].every(
      (player) => player.chips === DEFAULT_STARTING_CHIPS - DEFAULT_ANTE,
    ));

    const raisedBuyIn = await colyseus.createRoom<MyRoomState>("hearts", {
      ante: 2_000,
      startingChips: 100,
    });
    for (let index = 0; index < MAX_PLAYERS; index += 1) {
      await colyseus.connectTo(raisedBuyIn);
    }
    assert.equal(raisedBuyIn.state.phase, "playing");
    assert.equal(raisedBuyIn.state.ante, 2_000);
    assert.equal(raisedBuyIn.state.pot, 8_000);
    assert.ok([...raisedBuyIn.state.players.values()].every((player) => player.chips === 0));

    const maxSafe = await colyseus.createRoom<MyRoomState>("hearts", {
      ante: MAX_CHIPS_PER_PLAYER,
      startingChips: MAX_CHIPS_PER_PLAYER,
    });
    for (let index = 0; index < MAX_PLAYERS; index += 1) {
      await colyseus.connectTo(maxSafe);
    }
    assert.equal(maxSafe.state.pot, MAX_CHIPS_PER_PLAYER * MAX_PLAYERS);
    assert.ok(maxSafe.state.pot <= 2_147_483_647);
  });

  it("rejects an out-of-turn play and accepts only the starter's two of clubs", async () => {
    const room = await colyseus.createRoom<MyRoomState>("hearts", {});
    const clients = [];
    for (let index = 0; index < MAX_PLAYERS; index += 1) {
      clients.push(await colyseus.connectTo(room));
    }
    const starter = clients.find((client) => client.sessionId === room.state.currentTurn);
    assert.ok(starter);
    const other = clients.find((client) => client.sessionId !== room.state.currentTurn);
    assert.ok(other);

    other!.send("play", { cardId: "Club2" });
    await room.waitForMessage("play");
    assert.equal(room.state.trick.length, 0);

    starter!.send("play", { cardId: "Club2" });
    await room.waitForMessage("play");
    assert.equal(room.state.trick.length, 1);
    assert.equal(room.state.trick[0].cardId, "Club2");
    assert.notEqual(room.state.currentTurn, starter!.sessionId);
  });

  it("keeps public state free of private hand contents", async () => {
    const room = await colyseus.createRoom<MyRoomState>("hearts", {});
    for (let index = 0; index < MAX_PLAYERS; index += 1) {
      await colyseus.connectTo(room);
    }
    const serialized = JSON.stringify(room.state.toJSON());
    assert.equal(serialized.includes("Club2"), false);
    assert.equal(serialized.includes("HeartA"), false);
    assert.equal(serialized.includes("hand"), true); // handCount remains public
  });

  it("can finish all thirteen tricks through the public play seam", async () => {
    const room = await colyseus.createRoom<MyRoomState>("hearts", {});
    const clients: any[] = [];
    for (let index = 0; index < MAX_PLAYERS; index += 1) {
      clients.push(await colyseus.connectTo(room));
    }
    const byId = new Map<string, any>(clients.map((client) => [client.sessionId, client]));
    const hands = new Map<string, string[]>();

    const requestHand = (client: any): Promise<string[]> => new Promise((resolve, reject) => {
      let timer: ReturnType<typeof setTimeout> | undefined;
      const off = client.onMessage("hand", (payload: { cards?: string[] }) => {
        if (!Array.isArray(payload?.cards)) return;
        if (timer) clearTimeout(timer);
        off();
        resolve([...payload.cards]);
      });
      timer = setTimeout(() => {
        off();
        reject(new Error(`hand message timed out for ${client.sessionId}`));
      }, 2_000);
      client.send("request_hand");
    });
    for (const client of clients) {
      hands.set(client.sessionId, await requestHand(client));
    }

    const waitUntil = async (predicate: () => boolean) => {
      const deadline = Date.now() + 2_000;
      while (!predicate() && Date.now() < deadline) {
        await new Promise((resolve) => setTimeout(resolve, 5));
      }
      assert.ok(predicate(), "room did not advance after play");
    };

    for (let playNumber = 0; playNumber < 52; playNumber += 1) {
      const playerId = room.state.currentTurn;
      const hand = hands.get(playerId);
      assert.ok(hand, "current turn must have a private hand");
      const trick = room.state.trick
        .map((entry) => cardFromId(entry.cardId))
        .filter((card): card is NonNullable<typeof card> => card !== undefined);
      const legal = getLegalCards(
        hand!.map((id) => cardFromId(id)!).filter(Boolean),
        trick,
        {
          firstTrick: room.state.trickNumber === 0,
          heartsBroken: room.state.heartsBroken,
        },
      );
      assert.ok(legal.length > 0, `no legal card for ${playerId}`);
      const cardId = legal[0].id;
      const beforeCount = room.state.players.get(playerId)!.handCount;
      hand!.splice(hand!.indexOf(cardId), 1);
      byId.get(playerId).send("play", { cardId });
      await waitUntil(() => room.state.phase === "finished" || room.state.players.get(playerId)!.handCount < beforeCount);
    }

    assert.equal(room.state.phase, "finished");
    assert.equal(room.state.trickNumber, 13);
    assert.ok([...room.state.players.values()].every((player) => player.handCount === 0));
    assert.equal([...room.state.players.values()].reduce((sum, player) => sum + player.score, 0), 19);
    assert.equal([...room.state.players.values()].reduce((sum, player) => sum + player.payout, 0), 400);
    assert.equal([...room.state.players.values()].filter((player) => player.isTreating).length >= 1, true);
  });
});
