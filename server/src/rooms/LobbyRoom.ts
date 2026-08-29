import { Client, Room, matchMaker } from "colyseus";
import { LobbyRoomInfo, LobbyState } from "./schema/MyRoomState.js";
import { MAX_PLAYERS, MyRoom } from "./MyRoom.js";

type RoomOptions = { name?: unknown; roomName?: unknown; ante?: unknown; playerName?: unknown; bots?: unknown };

/**
 * A lightweight, singleton-friendly lobby. Room instances remain authoritative
 * for readiness and game state; this room only mirrors public matchmaking
 * metadata and hands clients a Colyseus seat reservation when they select a
 * room. No private hands or player session details cross this boundary.
 */
export class LobbyRoom extends Room<{ state: LobbyState }> {
  state = new LobbyState();
  private disposed = false;

  messages = {
    refresh: async () => {
      await this.syncRooms();
    },

    create_room: async (client: Client, options: RoomOptions | undefined) => {
      try {
        const reservation = await matchMaker.create("hearts", {
          lobbyManaged: true,
          bots: options?.bots === true,
          roomName: this.readRoomName(options?.name ?? options?.roomName),
          ante: this.readAnte(options?.ante),
          name: this.readPlayerName(options?.playerName, client.sessionId),
        });
        this.sendReservation(client, reservation, "created");
        await this.syncRooms();
      } catch (error) {
        this.sendError(client, error);
      }
    },

    join_room: async (client: Client, payload: { roomId?: unknown; playerName?: unknown } | string | undefined) => {
      const roomId = typeof payload === "string" ? payload : payload?.roomId;
      if (typeof roomId !== "string" || roomId.trim().length === 0) {
        this.sendError(client, new Error("请选择一个房间"));
        return;
      }
      try {
        const reservation = await matchMaker.joinById(roomId, {
          lobbyManaged: true,
          name: this.readPlayerName(typeof payload === "string" ? undefined : payload?.playerName, client.sessionId),
        });
        this.sendReservation(client, reservation, "joined");
      } catch (error) {
        this.sendError(client, error);
      }
    },
  };

  onCreate() {
    this.state.message = "大厅已连接";
    this.setSimulationInterval(() => {
      void this.syncRooms();
    }, 500);
    void this.syncRooms();
  }

  async onJoin(client: Client) {
    client.send("lobby_ready", { sessionId: client.sessionId });
    await this.syncRooms();
  }

  onDispose() {
    this.disposed = true;
  }

  private async syncRooms() {
    if (this.disposed) return;
    let caches;
    try {
      const [hearts, legacy] = await Promise.all([
        matchMaker.query({ name: "hearts" }),
        matchMaker.query({ name: "my_room" }),
      ]);
      caches = [...hearts, ...legacy];
    } catch {
      // MatchMaker may be shutting down while the final lobby tick is in
      // flight. There is no client-visible work left in that case.
      return;
    }
    if (this.disposed) return;
    const visible = caches.filter((room) => !room.unlisted);
    const known = new Set<string>();
    for (const cache of visible) {
      const metadata = (cache.metadata ?? {}) as Partial<{
        displayName: string;
        phase: string;
        playerCount: number;
        maxPlayers: number;
        readyCount: number;
        bots: boolean;
        hostName: string;
      }>;
      const row = new LobbyRoomInfo();
      row.roomId = cache.roomId;
      row.name = metadata.displayName || "房间";
      row.phase = metadata.phase || (cache.locked ? "playing" : "waiting");
      row.playerCount = this.safeByte(metadata.playerCount ?? cache.clients);
      row.maxPlayers = this.safeByte(metadata.maxPlayers ?? (cache.maxClients || MAX_PLAYERS));
      row.readyCount = this.safeByte(metadata.readyCount ?? 0);
      row.bots = metadata.bots === true;
      row.hostName = metadata.hostName || "";
      this.state.rooms.set(cache.roomId, row);
      known.add(cache.roomId);
    }
    for (const roomId of [...this.state.rooms.keys()]) {
      if (!known.has(roomId)) this.state.rooms.delete(roomId);
    }
    this.state.message = `${this.state.rooms.size} 个房间在线`;
  }

  private sendReservation(client: Client, reservation: any, action: string) {
    client.send("room_joined", {
      action,
      name: reservation.name,
      sessionId: reservation.sessionId,
      roomId: reservation.roomId,
      publicAddress: reservation.publicAddress,
      processId: reservation.processId,
      reconnectionToken: reservation.reconnectionToken,
      devMode: reservation.devMode,
      protocol: reservation.protocol,
    });
  }

  private sendError(client: Client, error: unknown) {
    const message = error instanceof Error ? error.message : String(error);
    client.send("lobby_error", { message });
  }

  private readPlayerName(value: unknown, sessionId: string): string {
    if (typeof value === "string" && value.trim().length > 0) return value.trim().slice(0, 24);
    return `玩家 ${sessionId.slice(0, 4)}`;
  }

  private readRoomName(value: unknown): string {
    if (typeof value !== "string") return "新房间";
    const name = value.trim();
    return name.length > 0 ? name.slice(0, 32) : "新房间";
  }

  private readAnte(value: unknown): number {
    const ante = typeof value === "number" && Number.isSafeInteger(value) ? value : 100;
    return ante > 0 ? Math.min(ante, 500_000_000) : 100;
  }

  private safeByte(value: number): number {
    return Number.isFinite(value) ? Math.max(0, Math.min(255, Math.trunc(value))) : 0;
  }
}
