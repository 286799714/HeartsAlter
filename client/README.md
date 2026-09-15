# HeartsAlter Godot 客户端

客户端使用 Godot 4.7 .NET 与 C#，入口场景为 `Lobby.tscn`。打开
`project.godot` 后即可运行；牌面资源位于 `assets/cards/`。

启动后先进入大厅；客户端不会自动连接服务器，需要先在服务器栏输入 IP/域名和端口，或从
预设下拉框选择 `ddns.maydaymemory.com:2567`（默认）或 `127.0.0.1:2567`，再点击连接。
首次连接前可输入昵称；连接后从服务端恢复本设备存档中的昵称、头像和筹码。房间列表由服务端同步。输入房间名可以创建房间，点击列表中的
“加入”会进入 `ReadyRoom.tscn`；房主固定准备，可添加机器人，所有四个席位准备后由房主
开启游戏并切换到 `Table.tscn`。席位预约只在场景切换期间保存在进程内会话对象中。

进入牌桌后客户端发送 `table_ready`；收到私有手牌并完成发牌 tween 后发送 `deal_ready`。
服务端会在握手阶段各等待 30 秒；发牌完成后进入传牌阶段，客户端选择三张牌后发送
`pass_cards`，服务端固定等待 30 秒并为未提交者随机补选。传牌完成后才进入出牌阶段。
对局中按服务端
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

头像由 `PlayerInfo.SetProfileIdentity` 根据服务端的 `avatarId` 加载 `assets/textures/ui/profile_icon_1.jpg`～`profile_icon_4.jpg`，缺失或未知编号回退到第 1 张。首次建档随机分配，重新连接沿用存档。`GameSession.Profile` 保存进入大厅时收到的本人存档；游戏中余额以房间状态为准，下次连接大厅时重新读取最近结算。数据库配置和保存时机见[服务端说明](../server/README.md#使用设备存档)。

正常启动使用本地持久化的安装 ID：首次连接前生成 UUID，写入并验证 `user://device_identity.cfg` 后才连接服务器；后续启动始终读取该文件，不再读取系统设备号。已有的本地 UUID 文件继续沿用。标识文件损坏、不可读或无法创建时，连接会失败并提示重试，不会自动覆盖或更换身份。清除应用数据或丢失文件后会创建新玩家；固定项目名和用户数据目录设置，避免切换到另一个存储目录。

旧版仅使用系统设备号、没有本地 UUID 文件的玩家会创建新的安装身份，服务端旧存档仍保留。调试构建的 `--device-id` 参数仅用于指定独立测试身份，不读写正式安装 ID 文件。

## 验证存档与头像

安装 ID 的文件创建、冷读取、旧 UUID 兼容、损坏文件和读写失败可以单独验证，无需启动服务端（下文假设 Godot .NET 可通过 `godot` 启动）：

```powershell
dotnet build client/HeartsAlter.csproj
godot --headless --path client --scene res://Tests/DeviceIdentitySmoke.tscn
```

出现 `DEVICE_IDENTITY_SMOKE_OK` 表示通过；测试使用临时目录，不读写正式安装标识。

在仓库根目录打开两个 PowerShell 终端。第一个启动独立的内存数据库服务端，避免修改开发存档：

```powershell
cd server
$env:PORT = '2571'
$env:PLAYER_DB_PATH = ':memory:'
node --import tsx src/index.ts
```

第二个构建客户端并运行测试场景。下面假设 Godot .NET 可通过 `godot` 启动，否则使用本机 Godot 可执行文件路径：

```powershell
dotnet build client/HeartsAlter.csproj
godot --headless --path client --scene res://Tests/ProfileSmoke.tscn -- --device-id=profile-smoke --endpoint=ws://127.0.0.1:2571
```

日志出现 `PROFILE_SMOKE_OK` 表示私有存档同步、同设备重连、C# schema 解码、席位预约，以及三个玩家组件对四张头像的加载全部通过。测试完成后停止第一个终端的服务端即可。
