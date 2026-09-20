# HeartsAlter Godot 客户端

客户端使用 Godot 4.7 .NET 与 C#，入口场景为 `Lobby.tscn`。打开
`project.godot` 后即可运行；牌面资源位于 `assets/cards/`。

启动后先进入连接页面 `Lobby.tscn`；客户端不会自动连接服务器，需要先在服务器栏输入 IP/域名和端口，或从
预设下拉框选择 `ddns.maydaymemory.com:2567`（默认）或 `127.0.0.1:2567`，再点击连接。
连接页使用 `assets/textures/ui/banner.png` 作为全屏背景，IP、端口、快捷下拉选择及“连接大厅”“教学关”按钮位于底部中央；布局和样式可直接在 `Lobby.tscn` 中编辑。
连接时无需输入昵称，首次进入由服务器自动分配并保存（例如“玩家 a3f2”），以后连接沿用存档。连接成功并同步存档后，进入 `Intro.tscn`，继续使用同一条大厅连接；可在大厅点击昵称旁的编辑按钮改名。
顶部显示服务端保存的昵称、头像和筹码。左侧可切换创建房间、加入房间和我的战绩；战绩目前显示空列表。
房间列表由服务端同步，显示房间名、人数和阶段；已满或已开始的房间不能加入。输入房间名可以创建房间，点击列表中的
“加入”会进入 `ReadyRoom.tscn`；房主固定准备，可添加机器人，所有四个席位准备后由房主
开启游戏并切换到 `Table.tscn`。席位预约只在场景切换期间保存在进程内会话对象中。

准备房间提供“启用碎心规则”和“缺门时必须优先垫得分牌”两个开关，默认开启。房主可在开局前独立修改，其他玩家同步查看；修改后其他真人需重新准备。关闭碎心规则后可在红心未破时领出红桃，但首墩避免领出分牌的限制仍适用；关闭缺门优先分牌后，缺门时可任选手牌，包括首墩。牌桌合法牌提示与服务端、机器人保持一致，教学仍使用默认规则。

准备房间或结算页返回大厅时进入 `Intro.tscn`，自动重连上一次服务器并重新获取玩家信息。
Intro 的“返回连接”会断开大厅连接并回到 `Lobby.tscn`；断线后可从这里重试。
`Intro.tscn` 保留根节点上的 Figma 导入器，`Controller` 子节点挂载 `Scripts/Intro.cs`，绑定场景中的输入框、按钮、玩家信息和房间列表模板。
界面布局与点击区域在场景中编辑，脚本负责数据同步和页面切换。

三个页面共用一个 `scenes/ui/SidebarPanel.tscn` 实例，侧栏背景固定，`TabScroll/Tabs` 中的按钮纵向排列；内容超过组件高度时可用滚轮或滚动条上下滚动。组件用 `assets/textures/ui/intro/selected.png` 显示当前选中项，切页时保留侧栏实例和滚动位置，并将选中或获得键盘焦点的页签滚入可见区域。
新增页签时，在组件场景的 `TabScroll/Tabs` 下复制一个 Button，设置唯一节点名、文字和图标，再在 `Intro.cs` 的 `ShowPage` 中绑定对应页面。`InitialTab` 指定默认页签，`SelectTab(节点名)` 可由代码切换；`TabSelected` 信号只在选中项变化时发出。侧栏的布局和样式统一在组件场景中修改。

无需服务器即可验证侧栏切页、选中框、12 个页签的滚动、键盘焦点和实例隔离：

```powershell
dotnet build client/HeartsAlter.csproj
godot --headless --path client --resolution 1280x720 --scene res://Tests/SidebarSmoke.tscn
```

输出 `SIDEBAR_SMOKE_OK` 表示通过。

Intro 的合成图片保存在 `assets/textures/ui/intro/generated/`，场景通过外部纹理引用加载，避免把像素数组写入 `.tscn` 导致切换卡顿。
如果重新从 Figma 导入后出现内嵌图片，在仓库根目录运行以下命令，再让 Godot 导入生成的资源：

```powershell
python tools/externalize_scene_images.py client/scenes/Intro.tscn
```

转换脚本只依赖 Python 标准库，导出无损 PNG、合并相同图片，并保持场景节点和布局不变。生成的 `.png` 和 `.png.import` 应一起保留；导入设置关闭透明边缘修正，以保持原始像素。

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

## 场景资源驻留

项目使用 Godot 内置的 [ResourcePreloader](https://docs.godotengine.org/en/stable/classes/class_resourcepreloader.html)，通过 `project.godot` 的 Autoload 配置常驻 `scenes/ResidentResources.tscn`。
启动时一次加载连接页、Intro、准备房间、牌桌、结算页、教学关和四张头像；预加载器持续持有 PackedScene 及其依赖，包括牌面、纹理、字体和材质，直到游戏退出。
各场景通过 `SceneNavigation.Change(this, scenePath)` 调用 `ChangeSceneToPacked`，直接使用驻留资源。这个入口不会在资源缺失时退回同步读盘；新增场景必须先添加到驻留清单，资源键使用完整的 `res://` 路径。

资源复用会增加启动时的加载量和常驻内存。场景节点仍按 Godot 的正常切换流程销毁、重新实例化，按钮、输入框、信号订阅和网络会话不会从旧场景继承；服务器重连和存档同步仍按前述流程执行。
`GameSession.Clear()` 只清理会话，资源驻留不受影响。

评估过 [Scene-Manager](https://github.com/glass-brick/Scene-Manager/wiki/API-Reference) 的异步过渡与预加载，以及 [Long Scene Manager](https://store.godotengine.org/asset/awauox/long-scene-manager/) 的多级缓存。后者在评估时的 1.8.0 商店版本标注为不稳定；当前固定场景集的驻留需求直接使用官方预加载器即可，不需要额外的第三方场景管理依赖。

在仓库根目录运行无需服务器的驻留验证：

```powershell
dotnet build client/HeartsAlter.csproj
godot --headless --path client --resolution 1280x720 --scene res://Tests/SceneResidencySmoke.tscn
```

出现 `SCENE_RESIDENCY_OK` 表示连续十次 Intro 与准备房间往返、强制 GC、会话清理后，场景及依赖仍使用相同资源对象；旧场景节点已释放、输入状态已重置、教学关进入和返回也正常。输出包含资源获取、切换调用及场景初始化完成的耗时，不包含网络等待。

## 验证 Intro 大厅流程

从仓库根目录先启动独立的内存数据库测试服务器：

```powershell
$env:PORT = '2573'
$env:PLAYER_DB_PATH = ':memory:'
cd server
node --import tsx src/index.ts
```

另开终端，在仓库根目录构建并运行：

```powershell
dotnet build client/HeartsAlter.csproj
godot --headless --path client --resolution 1280x720 --scene res://Tests/IntroSmoke.tscn -- --device-id=intro-smoke --endpoint=ws://127.0.0.1:2573
```

出现 `INTRO_SMOKE_OK` 表示连接交接、玩家信息、三个页面的鼠标点击、空战绩、房间名输入、重复点击保护、创建与加入、列表滚动与实时刷新、服务器拒绝提示和返回流程通过。测试使用独立身份，不读取正式安装标识；结束后停止测试服务器。

## 本地教学关

从连接页面点击“教学关”，或直接运行 `scenes/Tutorial.tscn`。无需服务端，不使用正式房间、玩家余额或身份存档；教学进度独立保存在 `user://tutorial_progress.cfg`。

| 教学顺序 | 教学内容 |
| --- | --- |
| 游戏目标 | 抢红包的边界感、第一名一无所有、第二名赢得最多 |
| 出牌规则 | 共13轮、指定花色、有同花色必须跟牌、缺门可垫牌；玩家亲自跟出梅花，赢得本轮并取得下一轮先手 |
| 如何获得积分？ | 展示红心与黑桃Q，讲解1分、6分和Q之后的红心2分；玩家跟出黑桃Q后，两家依次垫出红桃，逐张观察翻倍，收墩后积分从0增加到10 |
| 传牌与小技巧 | 说明正式对局在发牌后、第一轮出牌前传牌；每人13张牌，自选3张传给下家，收到上家的3张后接着讲解保留大小牌、掌握分牌时机、恰到好处地成为第二名 |

目前只做一关，共四节。第二节讲解出牌规则并实操一轮，第三节把得分牌讲解和加分实操合在同一节，第四节在传牌结果后连续讲解技巧，保留换牌后的手牌与遮罩。游戏目标与技巧只需阅读，出牌规则、得分牌与传牌都需要玩家亲自操作；无需打满正式对局的13轮。教学顺序与正式牌局顺序不同，传牌讲解会明确其在正式对局中的时机。采用默认房间规则，碎心与缺门优先分牌均关闭。讲解使用富文本：金色突出目标和规则，红色突出分牌与分值，青色突出操作。

牌桌全屏显示。规则和得分牌部分使用精简牌例；传牌部分使用完整52张标准牌。节内机器人行动和玩家操作时隐藏讲解面板；切换到需要发牌的下一节时，先隐藏讲解和遮罩，完整展示发牌动画，待发牌结束、下一页对话出现时一同恢复遮罩。不需要发牌的切节保留上一页讲解与遮罩，清除旧聚焦目标，再直接显示下一页，避免亮屏闪烁。出现半透明灰色覆层时，高亮当前涉及的牌或区域，纯文案页不强行高亮对象。点击屏幕逐段阅读；纯讲解结束后自动进入下一部分，需要操作时则开放手牌，操作和结果讲解结束后自动继续。没有“下一节”或额外的“继续”按钮，最终讲解结束后自动记录通关并返回教程入口；鼠标、触摸和 Enter/空格均可推进对话。讲解期间手牌不响应操作，结束讲解的点击也不会顺带选牌、出牌或跳过下一部分的首段对话。

操作时点一次选牌，再点同一张出牌；传牌时自由选三张，再点击手牌上方的箭头。灰色牌受当前规则限制。第二节由小岚用梅花2领出，其他两家跟梅花5、10；玩家跟梅花8会输给小满，跟梅花K则获胜。两张牌都符合跟花色规则，结果讲解根据实际出牌说明胜负并聚焦真正的胜者，读完后继续下一节。第三节在红桃2、红桃K和黑桃Q的展示手牌上直接实操：小满领黑桃2，玩家跟黑桃Q，小岚和阿澈随后分别垫红桃3、5。三张得分牌各自在落桌后暂停讲解，突出两张红桃因为在Q之后打出，都从1分变为2分。收墩后聚焦玩家积分，展示6+2+2=10分，再进入传牌练习；传牌完成后保留收到的手牌讲解结果，然后在同一节内接着阅读技巧。导航面板可以重看本节或返回教程入口。新版一关的通关记录使用独立配置分节，不沿用旧三关的完成标记。

布局、样式、选关页和所有教学面板都在 `scenes/Tutorial.tscn` 中编辑；其中的 `Game/Table` 是铺满场景、保持原始缩放的 `Table.tscn` 实例。`TutorialController.cs` 绑定节点并推进流程，`TutorialCatalog.cs` 定义牌例与结果，`TutorialGuides.cs` 定义每段讲解及其聚焦对象。`TutorialSpotlight.cs` 只跟踪实际目标的边界、切换场景中预设的上下讲解位置、拦截推进输入；灰色覆层与描边使用 `TutorialSpotlight.gdshader`。牌桌实例在场景中设置 `UseNetworkSession = false` 以隔离正式会话；重玩时通过 `Table.Local.cs` 清理动画和手牌，保留场景实例。网络手牌提示和教学共用 `CardRules`，正式出牌仍由服务端权威校验。

针对已打出牌的讲解，在该玩家出牌动画结束后立即弹出并聚焦实际牌面，暂停其余玩家出牌；点击读完后，才由下一位玩家继续。第二节的梅花2和第三节的黑桃2都采用这个时机，不再等其他对手全部出完牌。玩家跟牌前的手牌提示仍在真正轮到玩家时出现。

在仓库根目录验证四节顺序、梅花8输与梅花K赢的结果文案和聚焦、黑桃Q先于两张红桃及6+2+2=10分、两次实操、每张得分牌落桌后即时聚焦及暂停出牌/收墩、关闭讲解后的继续与重看取消、得分牌讲解与实操的手牌连续性、发牌无遮挡及对话与遮罩同步恢复、不发牌切节的遮罩保持、实际聚焦目标、点击/触摸推进与防误触、规则一致性、传牌、计分、重玩/退出时的取消处理，以及正式会话隔离：

```powershell
dotnet build client/HeartsAlter.csproj
godot --headless --path client --scene res://Tests/TutorialSmoke.tscn
```

日志出现 `TUTORIAL_SMOKE_OK` 表示通过。测试不保存教学进度，也不连接服务器。`Tests/fixtures/legal-cards.json` 同时被该测试和服务端 `npm test` 使用，用于检查两端合法牌规则保持一致。

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

日志出现 `PROFILE_SMOKE_OK` 表示服务器自动分配昵称、私有存档同步、同设备重连、C# schema 解码、席位预约，以及玩家组件对四张头像的加载全部通过。测试完成后停止第一个终端的服务端即可。
