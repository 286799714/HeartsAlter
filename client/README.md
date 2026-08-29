# HeartsAlter Godot 客户端

客户端使用 Godot 4.7 .NET 与 C#，入口场景为 `Lobby.tscn`。打开
`project.godot` 后即可运行；牌面资源位于 `assets/cards/`。

启动后先进入大厅；客户端不会自动连接服务器，需要先在服务器栏输入 IP/域名和端口，或从
预设下拉框选择 `ddns.maydaymemory.com:2567`（默认）或 `127.0.0.1:2567`，再点击连接。
房间列表由服务端同步。输入昵称和房间名可以创建房间，点击列表中的
“加入”会进入 `ReadyRoom.tscn`；房主固定准备，可添加机器人，所有四个席位准备后由房主
开启游戏并切换到 `Table.tscn`。席位预约只在场景切换期间保存在进程内会话对象中。

进入牌桌后客户端发送 `table_ready`；收到私有手牌并完成发牌 tween 后发送 `deal_ready`。
服务端会在两个阶段各等待 30 秒，超时就把所有客户端带回准备房间。对局中按服务端
`turn_started.duration` 在 `MainPlayerInfo` 本地倒计时；牌局结束进入 `Settlement.tscn`，
点击“下一局”会发送 `next_round`，等待所有真人同意。

“连接服务器”按钮通过 Colyseus C# SDK 以 `bots: true` 加入 `hearts` 房间。服务端会
自动补齐 3 个机器人，所以只启动一个客户端也能看到牌局推进；连接成功后，公开状态来自
服务端，自己的手牌通过私有 `hand` 消息同步；中央墩区、对手手牌数量和结算面板都从
服务端状态恢复。若本地没有运行服务端，场景会保留明确标注的离线牌面/动画预览，不代表
联网规则状态。发牌、出牌和整理手牌均使用 Sine transition + InOut easing。

机器人使用服务端同一套合法出牌规则，收到轮次后约 600ms 自动出牌；轮到本地玩家时仍可
点击手牌操作。需要四名真人时，把适配器 `ConnectAsync` 的 `bots` 参数设为 `false`。

```powershell
dotnet restore HeartsAlter.csproj
dotnet build HeartsAlter.csproj
```
