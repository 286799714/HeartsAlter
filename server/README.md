# HeartsAlter Colyseus 服务端

This project was created with [⚔️ `create-colyseus-app`](https://github.com/colyseus/create-colyseus-app/).

[Documentation](https://docs.colyseus.io/)

## 用法

需要 Node.js **22.13 或更高版本**（使用内置 `node:sqlite`，无需额外安装数据库服务）。

```
cd server
npm install
npm start
```

服务端默认监听 `ws://localhost:2567`。浏览器打开 http://localhost:2567 可进入 playground，`/monitor` 为监控面板。

主房间名是 `hearts`；`my_room` 仅作为旧示例的兼容别名保留。普通房间固定四个真人座位，满员后自动收取默认 100 底注并发牌；创建房间时可通过 `ante` 选项配置其他正整数底注（服务端会限制在 schema 可表示的范围内）。

大厅房间名是 `lobby`。客户端先 `joinOrCreate("lobby", { deviceId, name })`，等待私有 `player_profile` 存档后，通过 `create_room`/`join_room` 获取具体 `hearts` 房间的 SeatReservation；大厅列表来自每个房间的公开 matchmaking metadata。大厅创建的房间进入准备阶段：房主固定准备，普通玩家发送 `ready`，房主可发送 `add_bot` 补齐空席位，并在四个席位全部准备后发送 `start_game`。

房主开始后，房间依次等待 `table_ready` 和 `deal_ready` 两轮客户端握手，各自超时 30 秒会广播 `room_reset` 并回到准备阶段。发牌完成后进入 `passing`，每名玩家通过 `pass_cards` 选择三张牌传给下家；传牌阶段固定等待 30 秒，超时由服务端随机补选。四名玩家的选择齐全后服务端直接交换手牌并进入 `playing`。进入 `playing` 后真人座位的出牌时限为 15 秒，机器人席位使用短延迟自动出牌；真人超时和异常情况会从合法牌中随机代打。`trick_resolved` 提供本墩赢家和点数，结算后用 `next_round` 等待所有真实玩家同意下一局。

结算界面的普通玩家可以直接离开房间；房主发送 `disband_room` 后服务端会关闭房间并断开全部客户端。

使用默认本地 MatchMaker 时，`ecosystem.config.cjs` 固定单进程运行；多进程部署需要额外配置共享 Redis presence/driver，否则创建房间的预约可能被另一个进程接收而无法消费。

创建房间时传入 `{ bots: true }` 会开启单人演示模式：房间只接受一个真实连接，服务端补齐座位 2～4 为“机器人 2”～“机器人 4”，并使用同一套 `getLegalCards` 规则约每 600ms 自动出牌。机器人手牌仍只保存在服务端，不会通过私有 `hand` 消息泄露；省略 `bots` 或传 `false` 时保持四名真人模式。

## 使用设备存档

首次连接会自动建档，初始筹码为 1000，头像随机分配为 1～4，对应客户端的 `profile_icon_1.jpg`～`profile_icon_4.jpg`。之后用相同设备标识连接会恢复同一玩家 ID、首次昵称、头像和最近结算的筹码。首版没有注册、密码、改名或头像选择界面。

默认数据库为 `server/data/players.sqlite`，开发和编译后启动使用同一路径。目录和表会自动创建；部署时应把这个目录保留在持久磁盘上。可以通过 `PLAYER_DB_PATH` 设置其他路径，例如 PowerShell：

```powershell
$env:PLAYER_DB_PATH = 'D:/HeartsAlterData/players.sqlite'
npm start
```

存档协议：

| 数据或消息 | 用途 |
| --- | --- |
| 加入大厅的 `deviceId` | 客户端安装 ID 的摘要，沿用此协议字段名；允许 1～128 个字母、数字、下划线或连字符，作为 SQLite 唯一索引；缺失或无效时拒绝连接 |
| 加入大厅的 `name` | 仅用于首次建档，最多 24 字符；再次连接不覆盖已有昵称 |
| 私有 `player_profile` | `{ playerId, name, avatarId, chips }`，仅发送给本人；`chips` 为最近完成对局的余额 |
| `request_profile` | 重新请求本人存档，客户端注册处理器后发送一次，避免遗漏入大厅的首条消息 |
| 游戏公开状态 `Player.profileId` / `avatarId` | 各座位的持久玩家 ID 和头像编号；设备标识不进入公开状态或房间摘要 |

大厅在服务端为席位绑定设备，游戏房间重新读库；客户端传入的头像、筹码和玩家 ID 不用于覆盖存档。同一存档同时只能占一个游戏席位。准备阶段离开、结算后离开或房间销毁会释放席位；进行中的对局离线后保留席位并由服务端代打，到结算时保存并释放已离开的玩家。机器人不建档。

筹码仅在完整对局结束时用一个 SQLite 事务保存。发牌和动画握手期间的底注只影响房间内余额；握手失败会退还。未结算即销毁房间或服务进程退出时，该局作废，数据库保留上一局完成后的余额。首版不恢复中断的牌局，也不支持多个服务进程共享同一存档玩家的活跃席位。

Godot 客户端首次连接前生成 UUID 并保存到 `user://device_identity.cfg`，后续启动始终读取此安装 ID，以带应用前缀的 SHA-256 摘要作为 `deviceId`。客户端不再读取系统设备号；只有标识文件不存在时才允许创建，损坏或读写失败会停止连接并提示重试。已有的本地 UUID 文件和摘要保持兼容；旧版仅凭系统设备号建立的存档仍留在数据库中，新安装身份不会自动关联它。

调试构建可在 Godot 的 `--` 后传 `--device-id=player-a`、`--device-id=player-b`，在同一电脑模拟不同玩家，此参数不读写正式安装 ID 文件。安装 ID 是首版的存档索引，不提供身份认证；清除应用数据或丢失标识文件后会被视作新玩家。SQLite 用法参见 [Node.js SQLite 文档](https://nodejs.org/api/sqlite.html)。

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

有领出花色时必须跟花色；缺门时必须优先出分牌（红桃、黑桃 Q），没有分牌才可出任意手牌。缺门出分牌的要求在首墩和红心未破时同样适用，真人、机器人和超时代打遵循相同规则。

本变体使用不含大小王的 52 张牌，每人 13 张；传牌后持有梅花 2 的玩家先出，但可选择任意合法牌。黑桃 Q 打出前的红桃各 1 点，打出后的红桃各 2 点；同一墩也按出牌顺序区分，已得分不追溯翻倍。黑桃 Q 仍为 6 点，移除射月。最高分玩家共同请客，其余玩家按点数加权领取奖池。完整手牌只通过私有 `hand` 消息发送，不进入公开状态。

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
