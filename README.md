# HeartsAlter

四人红心大战筹码奖池变体 demo：服务端使用 Colyseus，客户端使用 Godot .NET（C#）。

## 玩法

- 使用不含大小王的 52 张标准扑克牌，四名玩家各 13 张。
- 每人投入相同底注（默认 100，可由房间创建选项调整），汇集为奖池（默认 400）。
- 发牌后每人选择三张牌传给下家；传牌阶段最多等待 30 秒，未选择者由服务端随机补选。
- 传牌后持有梅花 2 的玩家第一个出牌，可选择任意合法牌，不强制出梅花 2；有领出花色时必须跟花色；红心未破前不能主动领出红桃。
- 缺门（没有领出花色）时，必须优先出红桃或黑桃 Q；没有分牌才可出任意手牌。首墩和红心未破时也适用。例如领出梅花，手里只有红桃 3、黑桃 Q、方块 5 时，只能出红桃 3 或黑桃 Q。
- 黑桃 Q 打出前，每张红桃计 1 点；打出后，每张红桃计 2 点；黑桃 Q 计 6 点，不使用射月。同一墩也按出牌先后计分，之前的红桃不追溯加分。例如依次出红桃、黑桃 Q、红桃，这三张牌分别计 1、6、2 点。
- 结算时最高分玩家（并列时全部并列者）共同请客，不拿奖池；其他玩家按点数比例分配奖池。整数余数按座位顺序分配。

服务端是唯一规则裁判。完整手牌通过私有 `hand` 消息发送给对应玩家，不会出现在公开 Colyseus state 中；断线或超时由服务端自动代打，牌局不会卡死。Godot 客户端默认以 `bots: true` 创建单人演示房间，服务端会补齐 3 个机器人；也可以用普通房间选项关闭机器人，等待四名真人。

## 本地教学

只想学习玩法时，可以直接启动 Godot 客户端，在大厅点击“教学关 · 无需联网”，不必启动服务端。目前只有一关，包含“游戏目标→出牌规则与实操→得分牌与实操→传牌与小技巧”四节，用颜色强调关键词。玩家在第二节亲自跟牌，观察不同选牌对应的实际胜负；第三节讲解得分牌后直接用同一副手牌打出黑桃Q，观察后续两张红桃各翻倍至2分，收墩时增加10分，再用每人13张的完整牌局练习交换3张牌。教学采用默认房间规则，碎心与缺门优先分牌均关闭，支持重看和独立通关记录，不影响正式筹码。详见[客户端教学说明](client/README.md#本地教学关)。

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
- `pass_cards`：传牌阶段发送 `{ "cardIds": ["HeartQ", "Club4", "Spade9"] }`。
- `play`：发送 `{ "cardId": "HeartQ" }` 出牌意图。
- `restart`：结算后重新开始一局（所有玩家必须有足够底注）。

创建房间选项：`bots: true` 开启单人演示房间（服务端补齐 3 个机器人并限制为 1 个真实连接）；省略或设为 `false` 时使用四名真人模式。

服务端私有/广播消息包括 `hand`、`round_started`、`passing_started`、`passing_selected`、`passing_received`、`passing_completed`、`turn_started`、`card_played`、`trick_resolved`、`round_finished` 和 `invalid_play`。公开状态字段见 `server/src/rooms/schema/MyRoomState.ts`。

客户端连接后应注册 `hand` 处理器并主动发送一次 `request_hand`；服务端在入座和发牌后还会延迟重发一次，覆盖加入时的消息处理器竞态。

## 目录

- `server/src/game/rules.ts`：纯规则、发牌、计分和奖池分配。
- `server/src/rooms/MyRoom.ts`：Colyseus 权威房间与超时/重连处理。
- `client/Scripts/Main.cs`：Godot C# 牌桌、网络连接和离线演示。
- `client/Scripts/CardView.cs`：牌面加载与统一 tween。
- `CONTEXT.md`、`docs/adr/`：领域词汇与架构决策。
