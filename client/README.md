# HeartsAlter Godot 客户端

客户端使用 Godot 4.7 .NET 与 C#，入口场景为 `Main.tscn`。打开
`project.godot` 后即可运行；牌面资源位于 `assets/cards/`。

“连接服务器”按钮通过 Colyseus C# SDK 加入 `hearts` 房间。连接成功后，公开状态来自
服务端，自己的 13 张牌通过私有 `hand` 消息同步；如果本地没有运行服务端，场景会保留离线
发牌/出牌演示。发牌、出牌和整理手牌均使用 Sine transition + InOut easing。

```powershell
dotnet restore HeartsAlter.csproj
dotnet build HeartsAlter.csproj
```
