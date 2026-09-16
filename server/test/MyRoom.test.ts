import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { mock } from "node:test";
import { ColyseusTestServer, boot } from "@colyseus/testing";

import appConfig from "../src/app.config.js";
import {
  DEFAULT_ANTE,
  DEFAULT_STARTING_CHIPS,
  MAX_CHIPS_PER_PLAYER,
  MAX_PLAYERS,
  PASSING_BOT_DELAY,
} from "../src/rooms/MyRoom.js";
import { LobbyState, MyRoomState } from "../src/rooms/schema/MyRoomState.js";
import { closePlayerProfileStore, getPlayerProfileStore } from "../src/persistence/PlayerProfileStore.js";
import {
  cardFromId,
  getLegalCards,
} from "../src/game/rules.js";

describe("authoritative Hearts room", () => {
  let colyseus: ColyseusTestServer<typeof appConfig>;
  let saveDirectory: string;
  const previousDatabasePath = process.env.PLAYER_DB_PATH;

  before(async () => {
    saveDirectory = mkdtempSync(join(tmpdir(), "hearts-room-tests-"));
    process.env.PLAYER_DB_PATH = join(saveDirectory, "players.sqlite");
    colyseus = await boot(appConfig);
  });
  after(async () => {
    await colyseus.shutdown();
    if (previousDatabasePath === undefined) delete process.env.PLAYER_DB_PATH;
    else process.env.PLAYER_DB_PATH = previousDatabasePath;
    rmSync(saveDirectory, { recursive: true, force: true });
  });

  beforeEach(async () => {
    await colyseus.cleanup();
  });
  afterEach(() => mock.restoreAll());

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

  const requestProfile = async (client: any) => {
    const message = client.waitForMessage("player_profile");
    client.send("request_profile");
    return message;
  };

  const consumeReservation = async (lobby: any, type: string, payload: any) => {
    const received = lobby.waitForMessage("room_joined");
    lobby.send(type, payload);
    const reservation = await received;
    const client = await colyseus.sdk.consumeSeatReservation<MyRoomState>(reservation);
    await client.waitForInitialState();
    return { client, room: colyseus.getRoomById<MyRoomState>(reservation.roomId) };
  };

  it("requires a valid device key before joining the lobby", async () => {
    for (const deviceId of [undefined, "", " ", 42, "x".repeat(129)]) {
      await assert.rejects(colyseus.sdk.joinOrCreate("lobby", { deviceId }), /设备标识无效/);
    }
  });

  it("privately syncs stable device saves and carries their public identity through lobby reservations", async () => {
    const lobbyA = await colyseus.sdk.joinOrCreate("lobby", { deviceId: "lobby-device-a", name: "Alice" });
    const profileA = await requestProfile(lobbyA);
    const lobbyB = await colyseus.sdk.joinOrCreate("lobby", { deviceId: "lobby-device-b", name: "Bob" });
    const profileB = await requestProfile(lobbyB);
    const again = await colyseus.sdk.joinOrCreate("lobby", { deviceId: "lobby-device-a", name: "Overwrite" });
    assert.deepEqual(await requestProfile(again), profileA);
    assert.notEqual(profileA.playerId, profileB.playerId);
    assert.deepEqual(Object.keys(profileA).sort(), ["avatarId", "chips", "name", "playerId"]);
    assert.ok(profileA.avatarId >= 1 && profileA.avatarId <= 4);
    assert.equal(profileA.chips, 1000);
    assert.equal(profileA.name, "Alice");
    const publicLobby = JSON.stringify(colyseus.getRoomById<LobbyState>(lobbyA.roomId).state.toJSON());
    assert.equal(publicLobby.includes("lobby-device-a"), false);
    assert.equal(publicLobby.includes(profileA.playerId), false);

    const { client: host, room } = await consumeReservation(lobbyA, "create_room", {
      name: "Profile test", playerName: "Forged", avatarId: 99, chips: 99999, deviceId: "lobby-device-b",
    });
    const { client: guest } = await consumeReservation(lobbyB, "join_room", {
      roomId: room.roomId, playerName: "Forged", avatarId: 99, chips: 99999,
    });
    for (const [client, profile] of [[host, profileA], [guest, profileB]] as const) {
      const player = room.state.players.get(client.sessionId)!;
      assert.equal(player.profileId, profile.playerId);
      assert.equal(player.name, profile.name);
      assert.equal(player.avatarId, profile.avatarId);
      assert.equal(player.chips, profile.chips);
    }
    assert.equal(JSON.stringify(room.state.toJSON()).includes("lobby-device-"), false);
    await room.waitForNextPatch();
    assert.equal(host.state.players.get(guest.sessionId).avatarId, profileB.avatarId);
  });

  it("rejects a second active seat and releases the device after leaving a waiting room", async () => {
    const room = await colyseus.createRoom<MyRoomState>("hearts", { lobbyManaged: true });
    const client = await colyseus.connectTo(room, { deviceId: "exclusive-device" });
    await assert.rejects(colyseus.connectTo(room, { deviceId: "exclusive-device" }), /已在游戏房间/);
    const other = await colyseus.createRoom<MyRoomState>("hearts", { lobbyManaged: true });
    await assert.rejects(colyseus.connectTo(other, { deviceId: "exclusive-device" }), /已在游戏房间/);
    const profileId = room.state.players.get(client.sessionId)!.profileId;
    await client.leave();
    const fresh = await colyseus.createRoom<MyRoomState>("hearts", { lobbyManaged: true });
    const rejoined = await colyseus.connectTo(fresh, { deviceId: "exclusive-device", chips: 99999 });
    assert.equal(fresh.state.players.get(rejoined.sessionId)!.profileId, profileId);
    assert.equal(fresh.state.players.get(rejoined.sessionId)!.chips, 1000);
  });

  it("keeps unfinished stakes out of saved balances and gives bots bundled avatars without saves", async () => {
    const room = await colyseus.createRoom<MyRoomState>("hearts", { bots: true });
    const client = await colyseus.connectTo(room, { deviceId: "aborted-device" });
    assert.equal(room.state.players.get(client.sessionId)!.chips, 900);
    const saved = getPlayerProfileStore().getByDevice("aborted-device");
    assert.equal(saved.chips, 1000);
    for (const player of room.state.players.values()) {
      assert.ok(player.avatarId >= 1 && player.avatarId <= 4);
      if (player.isBot) assert.equal(player.profileId, "");
    }
    await colyseus.cleanup();
    closePlayerProfileStore();
    assert.deepEqual(getPlayerProfileStore().getByDevice("aborted-device"), saved);
  });

  it("refunds a failed deal handshake without changing the save", async () => {
    const room = await colyseus.createRoom<MyRoomState>("hearts", { lobbyManaged: true, bots: true });
    const client = await colyseus.connectTo(room, { deviceId: "handshake-device" });
    client.send("start_game");
    await room.waitForMessage("start_game");
    client.send("table_ready");
    await room.waitForMessage("table_ready");
    assert.equal(room.state.phase, "dealing");
    assert.equal(room.state.players.get(client.sessionId)!.chips, 900);
    for (const timer of [...room.clock.delayed]) timer.tick(31_000);
    assert.equal(room.state.phase, "waiting");
    assert.equal(room.state.players.get(client.sessionId)!.chips, 1000);
    assert.equal(getPlayerProfileStore().getByDevice("handshake-device").chips, 1000);
  });

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
    while (String(room.state.phase) !== "playing" && Date.now() < deadline) {
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
    const turnStartedMessage = client.waitForMessage("turn_started", 5_000);
    await completePassing(room, [client]);
    const turnStarted = await turnStartedMessage;
    if (botIds.includes(turnStarted.playerId)) {
      assert.equal(turnStarted.delay, PASSING_BOT_DELAY);
    }
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

  it("gives the Club2 holder the first turn but accepts a different opening card", async () => {
    mock.method(Math, "random", () => 0.37);
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

    const handMessage = starter!.waitForMessage("hand");
    starter!.send("request_hand");
    const { cards } = await handMessage;
    assert.ok(cards.includes("Club2"), "the starter must hold Club2 after passing");
    const opening = getLegalCards(cards.map((id: string) => cardFromId(id)!), [], { firstTrick: true })
      .find((card) => card.suit !== "clubs");
    assert.ok(opening, "the fixture should offer a different opening suit");

    other!.send("play", { cardId: "Club2" });
    await room.waitForMessage("play");
    assert.equal(room.state.trick.length, 0);

    starter!.send("play", { cardId: opening.id });
    await room.waitForMessage("play");
    assert.equal(room.state.trick.length, 1);
    assert.equal(room.state.trick[0].cardId, opening.id);
    assert.equal(room.state.leadSuit, opening.suit);
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

  it("enforces point discards, scores every trick, and resets scoring on the next round", async () => {
    mock.method(Math, "random", () => 0.37);
    const room = await colyseus.createRoom<MyRoomState>("hearts", {});
    const clients: any[] = [];
    for (let index = 0; index < MAX_PLAYERS; index += 1) {
      clients.push(await colyseus.connectTo(room, { deviceId: `settlement-device-${index}` }));
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
    const waitUntil = async (predicate: () => boolean) => {
      const deadline = Date.now() + 2_000;
      while (!predicate() && Date.now() < deadline) {
        await new Promise((resolve) => setTimeout(resolve, 5));
      }
      assert.ok(predicate(), "room did not advance after play");
    };

    const playRound = async () => {
      for (const client of clients) {
        hands.set(client.sessionId, await requestHand(client));
      }
      assert.ok([...room.state.players.values()].every((player) => player.score === 0));
      const expectedScores = new Map(clients.map((client) => [client.sessionId, 0]));
      let queenPlayed = false;
      let heartsBeforeQueen = 0;
      let heartsAfterQueen = 0;
      let trickPoints = 0;
      let rejectedVoidDiscard = false;

      for (let playNumber = 0; playNumber < 52; playNumber += 1) {
        const playerId = room.state.currentTurn;
        const hand = hands.get(playerId);
        assert.ok(hand, "current turn must have a private hand");
        const trick = room.state.trick
          .map((entry) => cardFromId(entry.cardId))
          .filter((card): card is NonNullable<typeof card> => card !== undefined);
        const handCards = hand!.map((id) => cardFromId(id)!);
        if (!rejectedVoidDiscard && trick.length > 0 &&
          !handCards.some((card) => card.suit === trick[0].suit) &&
          handCards.some((card) => card.suit === "hearts" || card.id === "SpadeQ")) {
          const nonPointCard = handCards.find((card) => card.suit !== "hearts" && card.id !== "SpadeQ");
          if (nonPointCard) {
            const beforeCount = room.state.players.get(playerId)!.handCount;
            const invalidMessage = byId.get(playerId).waitForMessage("invalid_play");
            byId.get(playerId).send("play", { cardId: nonPointCard.id });
            const invalid = await invalidMessage;
            assert.equal(invalid.reason, "缺门时必须优先出红桃或黑桃 Q");
            assert.equal(room.state.players.get(playerId)!.handCount, beforeCount);
            assert.equal(room.state.currentTurn, playerId);
            assert.equal(room.state.trick.length, trick.length);
            rejectedVoidDiscard = true;
          }
        }
        const legal = getLegalCards(
          handCards,
          trick,
          {
            firstTrick: room.state.trickNumber === 0,
            heartsBroken: room.state.heartsBroken,
          },
        );
        assert.ok(legal.length > 0, `no legal card for ${playerId}`);
        const cardId = legal[0].id;
        // Keep the expected scores independent of the production scoring helpers.
        if (cardId.startsWith("Heart")) {
          trickPoints += queenPlayed ? 2 : 1;
          if (queenPlayed) heartsAfterQueen += 1;
          else heartsBeforeQueen += 1;
        } else if (cardId === "SpadeQ") {
          trickPoints += 6;
          queenPlayed = true;
        }
        const resolvedMessage = trick.length === 3 ? clients[0].waitForMessage("trick_resolved") : undefined;
        const beforeCount = room.state.players.get(playerId)!.handCount;
        hand!.splice(hand!.indexOf(cardId), 1);
        byId.get(playerId).send("play", { cardId });
        await waitUntil(() => room.state.phase === "finished" || room.state.players.get(playerId)!.handCount < beforeCount);
        if (resolvedMessage) {
          const resolved = await resolvedMessage;
          assert.equal(resolved.points, trickPoints);
          assert.equal(room.state.lastTrickPoints, trickPoints);
          const winnerId = room.state.lastTrickWinner;
          expectedScores.set(winnerId, expectedScores.get(winnerId)! + trickPoints);
          assert.equal(room.state.players.get(winnerId)!.score, expectedScores.get(winnerId));
          trickPoints = 0;
        }
      }

      assert.equal(room.state.phase, "finished");
      assert.equal(room.state.trickNumber, 13);
      assert.ok(rejectedVoidDiscard, "the fixture must exercise rejecting a non-point void discard");
      assert.ok(heartsBeforeQueen > 0 && heartsAfterQueen > 0, "the fixture must exercise both heart values");
      assert.equal(heartsBeforeQueen + heartsAfterQueen, 13);
      assert.ok([...room.state.players.values()].every((player) => player.handCount === 0));
      for (const [playerId, score] of expectedScores) {
        assert.equal(room.state.players.get(playerId)!.score, score);
      }
      assert.equal([...room.state.players.values()].reduce((sum, player) => sum + player.score, 0), 19 + heartsAfterQueen);
      assert.equal([...room.state.players.values()].reduce((sum, player) => sum + player.payout, 0), 400);
      assert.equal([...room.state.players.values()].filter((player) => player.isTreating).length >= 1, true);
    };
    await playRound();

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
    await playRound();
    const savedProfiles = clients.map((client, index) => {
      const profile = getPlayerProfileStore().getByDevice(`settlement-device-${index}`);
      assert.equal(profile.chips, room.state.players.get(client.sessionId)!.chips);
      return profile;
    });
    await colyseus.cleanup();
    closePlayerProfileStore();
    const lobby = await colyseus.sdk.joinOrCreate("lobby", { deviceId: "settlement-device-0" });
    assert.deepEqual(await requestProfile(lobby), savedProfiles[0]);
    const { client: restoredClient, room: restoredRoom } = await consumeReservation(lobby, "create_room", {});
    const restored = restoredRoom.state.players.get(restoredClient.sessionId)!;
    assert.equal(restored.chips, savedProfiles[0].chips);
    assert.equal(restored.avatarId, savedProfiles[0].avatarId);
  });
});
