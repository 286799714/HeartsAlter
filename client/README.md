# HeartsAlter Godot 客户端

客户端使用 Godot 4.7 .NET 与 C#，入口场景为 `Main.tscn`。打开
`project.godot` 后即可运行；牌面资源位于 `assets/cards/`。

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
