import { randomInt, randomUUID } from "node:crypto";
import { mkdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { fileURLToPath } from "node:url";

export const PROFILE_STARTING_CHIPS = 1_000;
export const AVATAR_COUNT = 4;

/** The device key is deliberately excluded from the client-facing save. */
export interface PlayerProfile {
  playerId: string;
  name: string;
  avatarId: number;
  chips: number;
}

export function readDeviceId(value: unknown): string {
  if (typeof value !== "string" || !/^[A-Za-z0-9_-]{1,128}$/.test(value)) {
    throw new Error("设备标识无效，请重新启动客户端");
  }
  return value;
}

/** One connection and one set of active seats for this single-process server. */
export class PlayerProfileStore {
  private readonly database: DatabaseSync;
  private readonly activeSeats = new Map<string, string>();

  constructor(path: string) {
    if (path !== ":memory:") mkdirSync(dirname(resolve(path)), { recursive: true });
    this.database = new DatabaseSync(path);
    this.database.exec(`
      PRAGMA busy_timeout = 5000;
      PRAGMA journal_mode = WAL;
      CREATE TABLE IF NOT EXISTS player_profiles (
        device_id TEXT PRIMARY KEY,
        player_id TEXT NOT NULL UNIQUE,
        name TEXT NOT NULL,
        avatar_id INTEGER NOT NULL CHECK (avatar_id BETWEEN 1 AND 4),
        chips INTEGER NOT NULL CHECK (chips BETWEEN 0 AND 2147483647)
      ) STRICT;
    `);
  }

  getOrCreate(deviceId: string, initialName?: unknown): PlayerProfile {
    readDeviceId(deviceId);
    const playerId = randomUUID();
    const name = typeof initialName === "string" && initialName.trim()
      ? initialName.trim().slice(0, 24) : `玩家 ${playerId.slice(0, 4)}`;
    this.database.prepare(`
      INSERT INTO player_profiles (device_id, player_id, name, avatar_id, chips)
      VALUES (?, ?, ?, ?, ?) ON CONFLICT(device_id) DO NOTHING
    `).run(deviceId, playerId, name, randomInt(1, AVATAR_COUNT + 1), PROFILE_STARTING_CHIPS);
    return this.getByDevice(deviceId);
  }

  getByDevice(deviceId: string): PlayerProfile {
    readDeviceId(deviceId);
    const row = this.database.prepare(`
      SELECT player_id AS playerId, name, avatar_id AS avatarId, chips
      FROM player_profiles WHERE device_id = ?
    `).get(deviceId);
    if (!row) throw new Error("玩家存档不存在");
    return { playerId: String(row.playerId), name: String(row.name),
      avatarId: Number(row.avatarId), chips: Number(row.chips) };
  }

  updateName(deviceId: string, value: unknown): PlayerProfile {
    readDeviceId(deviceId);
    const name = typeof value === "string" ? value.trim() : "";
    if (!name) throw new Error("请输入昵称");
    if ([...name].length > 24) throw new Error("昵称最多 24 个字符");
    if (/[\u0000-\u001f\u007f-\u009f\u2028\u2029]/u.test(name)) throw new Error("昵称不能包含换行或控制字符");
    const result = this.database.prepare("UPDATE player_profiles SET name = ? WHERE device_id = ?")
      .run(name, deviceId);
    if (result.changes !== 1) throw new Error("玩家存档不存在");
    return this.getByDevice(deviceId);
  }

  claimSeat(playerId: string, owner: string) {
    const existing = this.activeSeats.get(playerId);
    if (existing && existing !== owner) throw new Error("此设备已在游戏房间中，请先离开原房间");
    this.activeSeats.set(playerId, owner);
  }

  releaseSeat(playerId: string, owner: string) {
    if (this.activeSeats.get(playerId) === owner) this.activeSeats.delete(playerId);
  }

  /** Commit all real players together, only after a complete round. */
  saveBalances(balances: { playerId: string; chips: number; owner: string }[]) {
    for (const balance of balances) {
      if (this.activeSeats.get(balance.playerId) !== balance.owner) throw new Error("玩家席位已失效");
      if (!Number.isSafeInteger(balance.chips) || balance.chips < 0 || balance.chips > 2_147_483_647) {
        throw new Error("玩家筹码超出存档范围");
      }
    }
    this.database.exec("BEGIN IMMEDIATE");
    try {
      const update = this.database.prepare("UPDATE player_profiles SET chips = ? WHERE player_id = ?");
      for (const balance of balances) {
        if (update.run(balance.chips, balance.playerId).changes !== 1) throw new Error("玩家存档不存在");
      }
      this.database.exec("COMMIT");
    } catch (error) {
      this.database.exec("ROLLBACK");
      throw error;
    }
  }

  close() {
    this.database.close();
    this.activeSeats.clear();
  }
}

let sharedStore: PlayerProfileStore | undefined;

export function getPlayerProfileStore(): PlayerProfileStore {
  return sharedStore ??= new PlayerProfileStore(process.env.PLAYER_DB_PATH ||
    fileURLToPath(new URL("../../data/players.sqlite", import.meta.url)));
}

export function closePlayerProfileStore() {
  sharedStore?.close();
  sharedStore = undefined;
}
