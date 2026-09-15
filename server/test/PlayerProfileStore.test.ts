import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { PlayerProfileStore, readDeviceId } from "../src/persistence/PlayerProfileStore.js";

describe("SQLite player saves", () => {
  it("keeps the same identity, name, avatar and balance after reopening the file", () => {
    const directory = mkdtempSync(join(tmpdir(), "hearts-save-tests-"));
    const path = join(directory, "players.sqlite");
    let store = new PlayerProfileStore(path);
    try {
      const profile = store.getOrCreate("device-a", " Alice '");
      const other = store.getOrCreate("device-b", "Bob");
      assert.notEqual(profile.playerId, other.playerId);
      assert.ok(profile.avatarId >= 1 && profile.avatarId <= 4);
      assert.equal(profile.chips, 1000);
      store.claimSeat(profile.playerId, "seat-a");
      store.saveBalances([{ playerId: profile.playerId, chips: 875, owner: "seat-a" }]);
      store.close();
      store = new PlayerProfileStore(path);
      assert.deepEqual(store.getOrCreate("device-a", "overwrite"), { ...profile, chips: 875 });
      assert.deepEqual(store.getByDevice("device-b"), other);
    } finally {
      store.close();
      rmSync(directory, { recursive: true, force: true });
    }
  });

  it("rolls back every balance if any update in a settlement fails", () => {
    const store = new PlayerProfileStore(":memory:");
    try {
      const profile = store.getOrCreate("atomic-device");
      store.claimSeat(profile.playerId, "seat-a");
      store.claimSeat("missing-profile", "seat-b");
      assert.throws(() => store.saveBalances([
        { playerId: profile.playerId, chips: 900, owner: "seat-a" },
        { playerId: "missing-profile", chips: 1100, owner: "seat-b" },
      ]), /存档不存在/);
      assert.equal(store.getByDevice("atomic-device").chips, 1000);
      assert.throws(() => store.saveBalances([
        { playerId: profile.playerId, chips: -1, owner: "seat-a" },
      ]), /筹码/);
    } finally { store.close(); }
  });

  it("prevents stale or competing seats from writing the same save", () => {
    const store = new PlayerProfileStore(":memory:");
    try {
      const profile = store.getOrCreate("exclusive-device");
      store.claimSeat(profile.playerId, "original");
      assert.throws(() => store.claimSeat(profile.playerId, "duplicate"), /已在游戏房间/);
      store.releaseSeat(profile.playerId, "duplicate");
      assert.throws(() => store.claimSeat(profile.playerId, "duplicate"), /已在游戏房间/);
      store.releaseSeat(profile.playerId, "original");
      store.claimSeat(profile.playerId, "replacement");
      assert.throws(() => store.saveBalances([
        { playerId: profile.playerId, chips: 500, owner: "original" },
      ]), /席位已失效/);
      assert.equal(store.getByDevice("exclusive-device").chips, 1000);
    } finally { store.close(); }
  });

  it("rejects missing, malformed and oversized device keys", () => {
    for (const value of [undefined, null, {}, 123, "", " ", "a' OR 1=1", "x".repeat(129)]) {
      assert.throws(() => readDeviceId(value), /设备标识无效/);
    }
    assert.equal(readDeviceId("test-device_01"), "test-device_01");
  });
});
