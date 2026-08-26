# HeartsAlter Colyseus 服务端

This project was created with [⚔️ `create-colyseus-app`](https://github.com/colyseus/create-colyseus-app/).

[Documentation](https://docs.colyseus.io/)

## 用法

```
cd server
npm install
npm start
```

服务端默认监听 `ws://localhost:2567`。浏览器打开 http://localhost:2567 可进入 playground，`/monitor` 为监控面板。

主房间名是 `hearts`；`my_room` 仅作为旧示例的兼容别名保留。房间固定四个座位，满员后自动收取 100 底注并发牌。

## 结构

- `src/index.ts`: entry point — leave it alone if you plan to deploy to Colyseus Cloud
- `src/app.config.ts`: server configuration — rooms, HTTP routes, express middleware
- `src/rooms/MyRoom.ts`: 权威房间处理器
- `src/rooms/schema/MyRoomState.ts`: the state synchronized to every client in the room
- `test/MyRoom.test.ts`: boots the real server and connects a real client
- `loadtest/example.ts`: scriptable client for `npm run loadtest`
- `ecosystem.config.cjs`: pm2 configuration, used when deploying to Colyseus Cloud

## 脚本

- `npm start`: run the server in watch mode (`tsx watch src/index.ts`)
- `npm test`: run the mocha test suite
- `npm run build`: compile to `build/`
- `npm run loadtest`: 默认使用 4 个模拟客户端连接 `hearts` 房间（可按需覆盖 CLI 参数）

## 玩法与权威边界

本变体使用不含大小王的 52 张牌，每人 13 张；红桃各 1 点、黑桃 Q 6 点，移除射月。最高分玩家共同请客，其余玩家按点数加权领取奖池。完整手牌只通过私有 `hand` 消息发送，不进入公开状态。

## What's included

### Turn-based

`MyRoom` owns the turn order: `state.currentTurn` names whose turn it is, and a
`play` message from anyone else is ignored. The room locks once it is full, and
each turn carries a deadline — a `clock.setTimeout` skips a player who runs out
the clock, so one idle client cannot stall the match.

`state.turnDeadline` is stamped from `this.clock.currentTime`, the room's own
clock, so a reconnecting client can render the remaining time without the server
sending a countdown.

- https://docs.colyseus.io/room/timing-events

### Reconnection

`MyRoom.onDrop()` holds a dropped client's seat for 30 seconds via
`allowReconnection()`. The SDK retries automatically with exponential backoff;
`onReconnect()` fires if it gets back in time, `onLeave()` if it does not.

- https://docs.colyseus.io/room/reconnection
