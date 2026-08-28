# HeartsAlter

四人红心大战筹码奖池变体 demo：服务端使用 Colyseus，客户端使用 Godot .NET（C#）。

## 玩法

- 使用不含大小王的 52 张标准扑克牌，四名玩家各 13 张。
- 每人投入相同底注（默认 100，可由房间创建选项调整），汇集为奖池（默认 400）。
- 首墩由梅花 2 开始；有领出花色时必须跟花色；红心未破前不能主动领出红桃。
- 每张红桃计 1 点，黑桃 Q 计 6 点；不使用射月。
- 结算时最高分玩家（并列时全部并列者）共同请客，不拿奖池；其他玩家按点数比例分配奖池。整数余数按座位顺序分配。

服务端是唯一规则裁判。完整手牌通过私有 `hand` 消息发送给对应玩家，不会出现在公开 Colyseus state 中；断线或超时由服务端自动代打，牌局不会卡死。Godot 客户端默认以 `bots: true` 创建单人演示房间，服务端会补齐 3 个机器人；也可以用普通房间选项关闭机器人，等待四名真人。

## 运行服务端

```powershell
cd server
npm install
npm start
```

默认监听 `ws://localhost:2567`，房间名为 `hearts`（`my_room` 作为兼容别名保留）。可在 `http://localhost:2567` 打开 Colyseus playground。

验证服务端：

```powershell
npm test
npm run build
```

## 运行 Godot 客户端

1. 安装 Godot 4.7 .NET，并在编辑器中打开 `client/project.godot`。
2. 确认编辑器使用 .NET/C# 版本，构建并运行项目。
3. 点击“连接服务器”加入 `hearts` 房间；默认是单人演示模式（1 个客户端 + 3 个服务端机器人），机器人约每 600ms 自动出一张合法牌，你可以在轮到自己时点击手牌。没有服务端时客户端会自动进入离线预览，仍可查看发牌、整理手牌和牌面 tween。只有联网模式才执行服务端权威的完整规则与筹码结算。

客户端的发牌、出牌、整理手牌动画统一使用 `Tween.TransitionType.Sine` + `Tween.EaseType.InOut`（easeInOutSine）。牌面资源位于 `client/assets/cards`，仅包含 52 张标准牌和一张牌背。

## 协议速览

客户端消息：

- `request_hand`：请求重新发送当前玩家的私有手牌。
- `play`：发送 `{ "cardId": "HeartQ" }` 出牌意图。
- `restart`：结算后重新开始一局（所有玩家必须有足够底注）。

创建房间选项：`bots: true` 开启单人演示房间（服务端补齐 3 个机器人并限制为 1 个真实连接）；省略或设为 `false` 时使用四名真人模式。

服务端私有/广播消息包括 `hand`、`round_started`、`turn_started`、`card_played`、`trick_resolved`、`round_finished` 和 `invalid_play`。公开状态字段见 `server/src/rooms/schema/MyRoomState.ts`。

客户端连接后应注册 `hand` 处理器并主动发送一次 `request_hand`；服务端在入座和发牌后还会延迟重发一次，覆盖加入时的消息处理器竞态。

## 目录

- `server/src/game/rules.ts`：纯规则、发牌、计分和奖池分配。
- `server/src/rooms/MyRoom.ts`：Colyseus 权威房间与超时/重连处理。
- `client/Scripts/Main.cs`：Godot C# 牌桌、网络连接和离线演示。
- `client/Scripts/CardView.cs`：牌面加载与统一 tween。
- `CONTEXT.md`、`docs/adr/`：领域词汇与架构决策。
