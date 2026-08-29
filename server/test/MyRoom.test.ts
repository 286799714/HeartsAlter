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

  const completePassing = async (room: any, clients: any[]) => {
    const hands = new Map<string, string[]>();
    for (const client of clients) {
      const handMessage = client.waitForMessage("hand");
      client.send("request_hand");
      const payload = await handMessage;
      hands.set(client.sessionId, [...payload.cards]);
    }
    for (const client of clients) {
      client.send("pass_cards", { cardIds: hands.get(client.sessionId)!.slice(0, 3) });
    }
    const deadline = Date.now() + 2_000;
    while (room.state.phase !== "playing" && Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
    assert.equal(room.state.phase, "playing");
    return hands;
  };

  it("waits for four seats, charges one equal ante, and deals 13 cards per seat", async () => {
    const room = await colyseus.createRoom<MyRoomState>("hearts", {});
    const clients = [];
    for (let index = 0; index < MAX_PLAYERS; index += 1) {
      clients.push(await colyseus.connectTo(room, { name: `玩家${index + 1}` }));
    }

    assert.equal(room.state.phase, "passing");
    assert.equal(room.state.players.size, MAX_PLAYERS);
    assert.equal(room.state.pot, 400);
    assert.equal(room.state.ante, 100);
    assert.equal(room.state.playerOrder.length, MAX_PLAYERS);
    for (const player of room.state.players.values()) {
      assert.equal(player.handCount, 13);
      assert.equal(player.stake, 100);
      assert.equal(player.chips, 900);
    }
    await completePassing(room, clients);

    // The first turn is always owned by the seat holding the two of clubs;
    // this also proves the room did not invent a client-controlled turn.
    assert.ok(room.state.playerOrder.includes(room.state.currentTurn));
    assert.ok(clients.some((client) => client.sessionId === room.state.currentTurn));
  });

  it("broadcasts passing choices and privately delivers them to the successor", async () => {
    const room = await colyseus.createRoom<MyRoomState>("hearts", {});
    const clients: any[] = [];
    for (let index = 0; index < MAX_PLAYERS; index += 1) {
      clients.push(await colyseus.connectTo(room));
    }
    const hands = new Map<string, string[]>();
    for (const client of clients) {
      const handMessage = client.waitForMessage("hand");
      client.send("request_hand");
      hands.set(client.sessionId, [...(await handMessage).cards]);
    }

    const firstSelection = hands.get(clients[0].sessionId)!.slice(0, 3);
    const privatePass = clients[1].waitForMessage("passing_received");
    clients[0].send("pass_cards", { cardIds: firstSelection });
    const privatePayload = await privatePass;
    assert.equal(privatePayload.fromPlayerId, clients[0].sessionId);
    assert.deepEqual(privatePayload.cardIds, firstSelection);
    assert.equal(room.state.phase, "passing");

    for (let index = 1; index < clients.length; index += 1) {
      clients[index].send("pass_cards", {
        cardIds: hands.get(clients[index].sessionId)!.slice(0, 3),
      });
    }
    const deadline = Date.now() + 2_000;
    while (room.state.phase !== "playing" && Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
    assert.equal(room.state.phase, "playing");
    assert.ok([...room.state.players.values()].every((player) => player.handCount === 13));
  });

  it("fills a bot demo room from one client and lets a bot take a turn", async () => {
    const room = await colyseus.createRoom<MyRoomState>("hearts", { bots: true });
    const client = await colyseus.connectTo(room, { name: "演示玩家" });

    assert.equal(room.maxClients, 1);
    assert.equal(room.state.phase, "passing");
    assert.equal(room.state.players.size, MAX_PLAYERS);
    assert.equal(room.state.playerOrder.length, MAX_PLAYERS);
    assert.equal(room.state.pot, DEFAULT_ANTE * MAX_PLAYERS);

    const botIds = [...room.state.players.entries()]
      .filter(([, player]) => player.name.startsWith("机器人"))
      .map(([playerId]) => playerId);
    assert.equal(botIds.length, MAX_PLAYERS - 1);
    assert.ok(botIds.every((playerId) => playerId.startsWith("bot-")));
    assert.ok([...room.state.players.values()].every(
      (player) => player.handCount === 13 && player.stake === DEFAULT_ANTE,
    ));

    // Install the play listener before requesting the hand: a bot may already
    // own the opening turn, and its short timer should not race the assertion.
    const firstPlayMessage = client.waitForMessage("card_played", 5_000);
    await completePassing(room, [client]);
    const handMessage = client.waitForMessage("hand");
    client.send("request_hand");
    const handPayload = await handMessage;
    assert.equal(handPayload.cards.length, 13);

    // If the human seat owns Club2, play it once so the following seat (always
    // a synthetic seat in this room) can demonstrate the automatic turn.
    if (room.state.currentTurn === client.sessionId) {
      assert.ok(handPayload.cards.includes("Club2"));
      client.send("play", { cardId: "Club2" });
    }

    const firstPlay = await firstPlayMessage;
    let botPlay = firstPlay;
    if (firstPlay.playerId === client.sessionId) {
      botPlay = await client.waitForMessage("card_played", 5_000);
    }
    assert.ok(botIds.includes(botPlay.playerId));
    assert.equal(botPlay.roundNumber, room.state.roundNumber);
    assert.ok(typeof botPlay.cardId === "string" && botPlay.cardId.length > 0);
  });

  it("tags private hand and public play messages with the active round", async () => {
    const room = await colyseus.createRoom<MyRoomState>("hearts", {});
    const clients: any[] = [];
    for (let index = 0; index < MAX_PLAYERS; index += 1) {
      clients.push(await colyseus.connectTo(room));
    }

    const expectedRound = room.state.roundNumber;
    await completePassing(room, clients);
    const handMessage = clients[0].waitForMessage("hand");
    clients[0].send("request_hand");
    const handPayload = await handMessage;
    assert.equal(handPayload.roundNumber, expectedRound);
    assert.equal(handPayload.cards.length, 13);

    const starter = clients.find((client) => client.sessionId === room.state.currentTurn);
    assert.ok(starter);
    const playMessage = clients[0].waitForMessage("card_played");
    starter!.send("play", { cardId: "Club2" });
    const playPayload = await playMessage;
    assert.equal(playPayload.roundNumber, expectedRound);
    assert.equal(playPayload.playerId, starter!.sessionId);
    assert.equal(playPayload.cardId, "Club2");
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
    assert.equal(raisedBuyIn.state.phase, "passing");
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
    await completePassing(room, clients);
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
    const clients: any[] = [];
    for (let index = 0; index < MAX_PLAYERS; index += 1) {
      clients.push(await colyseus.connectTo(room));
    }
    await completePassing(room, clients);
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
    await completePassing(room, clients);
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

    const finishedRound = room.state.roundNumber;
    const roundStartedMessage = clients[0].waitForMessage("round_started");
    clients[0].send("restart");
    const roundStartedPayload = await roundStartedMessage;
    assert.equal(roundStartedPayload.roundNumber, finishedRound + 1);
    assert.equal(room.state.roundNumber, finishedRound + 1);
    assert.equal(room.state.phase, "passing");
    assert.equal(room.state.pot, 400);
    assert.ok([...room.state.players.values()].every((player) => player.handCount === 13));
    await completePassing(room, clients);

    const restartedHandMessage = clients[0].waitForMessage("hand");
    clients[0].send("request_hand");
    const restartedHandPayload = await restartedHandMessage;
    assert.equal(restartedHandPayload.roundNumber, finishedRound + 1);
    assert.equal(restartedHandPayload.cards.length, 13);
  });
});
