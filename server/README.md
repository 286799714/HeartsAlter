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

主房间名是 `hearts`；`my_room` 仅作为旧示例的兼容别名保留。普通房间固定四个真人座位，满员后自动收取默认 100 底注并发牌；创建房间时可通过 `ante` 选项配置其他正整数底注（服务端会限制在 schema 可表示的范围内）。

大厅房间名是 `lobby`。客户端先 `joinOrCreate("lobby")`，通过 `create_room`/`join_room` 获取具体 `hearts` 房间的 SeatReservation；大厅列表来自每个房间的公开 matchmaking metadata。大厅创建的房间进入准备阶段：房主固定准备，普通玩家发送 `ready`，房主可发送 `add_bot` 补齐空席位，并在四个席位全部准备后发送 `start_game`。

房主开始后，房间依次等待 `table_ready` 和 `deal_ready` 两轮客户端握手，各自超时 30 秒会广播 `room_reset` 并回到准备阶段。进入 `playing` 后真人座位的出牌时限为 15 秒，机器人席位使用短延迟自动出牌；真人超时和异常情况会从合法牌中随机代打。`trick_resolved` 提供本墩赢家和点数，结算后用 `next_round` 等待所有真实玩家同意下一局。

创建房间时传入 `{ bots: true }` 会开启单人演示模式：房间只接受一个真实连接，服务端补齐座位 2～4 为“机器人 2”～“机器人 4”，并使用同一套 `getLegalCards` 规则约每 600ms 自动出牌。机器人手牌仍只保存在服务端，不会通过私有 `hand` 消息泄露；省略 `bots` 或传 `false` 时保持四名真人模式。

## 结构

- `src/index.ts`: entry point — leave it alone if you plan to deploy to Colyseus Cloud
- `src/app.config.ts`: server configuration — rooms, HTTP routes, express middleware
- `src/rooms/MyRoom.ts`: 权威房间处理器
- `src/rooms/LobbyRoom.ts`: 大厅目录与席位预约
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

`hand` 在入座/发牌后会额外延迟重发一次，以覆盖客户端刚完成 join 但尚未注册消息处理器的竞态；客户端仍应在连接后主动发送 `request_hand`，重连时也使用同一消息恢复手牌。

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
