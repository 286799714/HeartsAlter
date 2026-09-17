using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Colyseus;
using Godot;
using HeartsAlter.Scripts;
using HeartsAlter.Scripts.Generated;
using HeartsAlter.Scripts.InGame;

namespace HeartsAlter.Tests;

/// <summary>Exercises the real connection/Intro/ready-room scenes against a disposable server.</summary>
public partial class IntroSmoke : Node
{
	private readonly List<Room<MyRoomState>> _hosts = new();
	private string _captureDirectory;

	public override async void _Ready()
	{
		string endpoint = "ws://127.0.0.1:2573";
		foreach (string arg in OS.GetCmdlineUserArgs())
		{
			if (arg.StartsWith("--endpoint=")) endpoint = arg[11..];
			if (arg.StartsWith("--capture-dir=")) _captureDirectory = arg[14..];
		}
		int exitCode = 0;
		try
		{
			// Keep this runner alive while the production scenes replace one another.
			await Frames();
			GetTree().CurrentScene = null;
			GameSession.Clear();
			GetTree().ChangeSceneToFile("res://scenes/Lobby.tscn");
			await Until(() => Scene is Lobby, "connection screen");
			var uri = new Uri(endpoint);
			Find<LineEdit>(Scene, "HostInput").Text = uri.Host;
			Find<LineEdit>(Scene, "PortInput").Text = uri.Port.ToString();
			await Capture("connection");
			await Click(Find<Button>(Scene, "ConnectButton"));
			await IntroReady();
			var lobby = GameSession.LobbyAdapter;
			Check(lobby.IsConnected, "Lobby did not hand off a live connection");
			Check(lobby.Profile.Name == $"玩家 {lobby.Profile.PlayerId[..4]}", "Server-assigned nickname missing");
			Check(U<Label>("PlayerName").Text == lobby.Profile.Name, "Saved name not displayed");
			Check(U<Label>("PlayerChips").Text == lobby.Profile.Chips.ToString("N0", CultureInfo.InvariantCulture), "Saved balance not displayed");
			Check(U<TextureRect>("PlayerAvatar").Texture.ResourcePath.EndsWith($"profile_icon_{lobby.Profile.AvatarId}.jpg"), "Wrong profile avatar");
			await Capture("intro-create");
			var originalProfile = lobby.Profile;
			await Click(U<Button>("EditPlayerName"));
			var nameInput = U<LineEdit>("PlayerNameInput");
			Check(nameInput.IsVisibleInTree() && nameInput.HasFocus() && !U<Label>("PlayerName").Visible,
				"Pencil did not replace name with a focused input");
			Check(nameInput.Text == originalProfile.Name && nameInput.MaxLength == 24, "Name editor not initialized");
			nameInput.Text = "尚未保存";
			await Click(U<Button>("CancelPlayerName"));
			Check(U<Label>("PlayerName").Text == originalProfile.Name && lobby.Profile == originalProfile,
				"Cancel changed the saved name");
			await Click(U<Button>("EditPlayerName"));
			nameInput.Text = " ";
			await Click(U<Button>("SavePlayerName"));
			Check(U<Label>("IntroStatus").Text == "请输入昵称" && nameInput.Visible, "Blank nickname accepted");
			nameInput.Text = "无效\u007f昵称";
			await Click(U<Button>("SavePlayerName"));
			await Until(() => U<Label>("IntroStatus").Text.StartsWith("昵称保存失败"), "server nickname validation error");
			Check(nameInput.Visible && nameInput.Text == "无效\u007f昵称" && !U<Button>("SavePlayerName").Disabled,
				"Failed save did not preserve the draft for retry");
			Check(lobby.Profile.Name == originalProfile.Name, "Rejected nickname changed local profile");
			nameInput.Text = "新昵称测试";
			await Capture("intro-name-edit");
			await Click(U<Button>("SavePlayerName"), repeat: 2);
			await Until(() => U<Label>("PlayerName").Visible && U<Label>("PlayerName").Text == "新昵称测试", "nickname saved");
			Check(GameSession.Profile.Name == "新昵称测试" && lobby.PlayerName == "新昵称测试", "Session name not synchronized");
			Check(U<LineEdit>("RoomNameInput").Text == "新昵称测试的房间", "Default room name was not updated");
			Check(lobby.Profile.PlayerId == originalProfile.PlayerId && lobby.Profile.Chips == originalProfile.Chips &&
				lobby.Profile.AvatarId == originalProfile.AvatarId, "Renaming changed unrelated profile fields");
			await Click(U<Button>("EditPlayerName"));
			nameInput.Text = "回车提交昵称";
			GetViewport().PushInput(new InputEventKey { Keycode = Key.Enter, Pressed = true }, true);
			GetViewport().PushInput(new InputEventKey { Keycode = Key.Enter, Pressed = false }, true);
			await Until(() => U<Label>("PlayerName").Visible && U<Label>("PlayerName").Text == "回车提交昵称", "nickname submitted with Enter");
			await Capture("intro-name-saved");
			await Click(Find<Button>(U<Control>("CreatePage"), "HistoryTab"));
			Check(U<Control>("HistoryPage").Visible && U<Label>("EmptyHistory").IsVisibleInTree(), "History tab did not open its empty state");
			Check(!Find<Control>(U<Control>("HistoryPage"), "战绩列表项").IsVisibleInTree(), "Sample history is still visible");
			await Capture("intro-history");
			await Click(Find<Button>(U<Control>("HistoryPage"), "JoinTab"));
			Check(U<Control>("JoinPage").Visible && U<Label>("EmptyRooms").Visible, "Empty room directory missing");
			await Click(Find<Button>(U<Control>("JoinPage"), "CreateTab"));
			var input = U<LineEdit>("RoomNameInput");
			await Click(input);
			Check(input.HasFocus(), "Room-name input cannot receive mouse focus");
			input.Text = " ";
			await Click(U<Button>("CreateRoomButton"));
			Check(U<Label>("IntroStatus").Text == "请输入房间名", "Blank room name was not validated");
			input.Text = "创建流程测试";
			await Click(U<Button>("CreateRoomButton"), repeat: 2);
			await Until(() => Scene is Table && GameSession.GameAdapter?.State?.players?.Count == 1, "created waiting table");
			Check(!lobby.IsConnected, "Intro leaked its lobby connection after entering a room");
			Check(GameSession.GameAdapter.State.players[GameSession.GameAdapter.SessionId].isHost, "Room creator is not the host");
			Check(GameSession.GameAdapter.State.players[GameSession.GameAdapter.SessionId].name == "回车提交昵称", "Room did not use saved nickname");
			var heartsToggle = Find<CheckButton>(Scene, "HeartsBreakingToggle");
			var discardToggle = Find<CheckButton>(Scene, "PointDiscardToggle");
			Check(!heartsToggle.ButtonPressed && !discardToggle.ButtonPressed && !heartsToggle.Disabled && !discardToggle.Disabled,
				"Room rules must default off and be editable by the host");
			await Click(heartsToggle);
			await Until(() => GameSession.GameAdapter.State.heartsBreakingEnabled, "hearts rule enabled");
			Check(!GameSession.GameAdapter.State.mustDiscardPointsWhenVoid, "Hearts toggle changed the discard rule");
			await Click(discardToggle);
			await Until(() => GameSession.GameAdapter.State.mustDiscardPointsWhenVoid, "discard rule enabled");
			await Click(heartsToggle);
			await Until(() => !GameSession.GameAdapter.State.heartsBreakingEnabled && !heartsToggle.ButtonPressed, "hearts rule disabled again");
			Check(discardToggle.ButtonPressed, "Discard toggle did not retain its independent value");
			await Capture("ready-room-rules");
			await ExerciseWaitingTable(endpoint);
			await ConfirmTableExit("解散房间");
			await IntroReady();
			await Until(() => GameSession.LobbyAdapter.State.rooms.Count == 0, "created room removed after leaving");
			Check(U<Label>("PlayerName").Text == "回车提交昵称", "Nickname did not persist after reconnecting");
			await ExerciseGuestExit(endpoint);

			// Seed enough real rooms to require scrolling; one is full and must not be joinable.
			var client = new Client(endpoint);
			for (int index = 0; index < 4; index++)
			{
				var host = await client.Create<MyRoomState>("hearts", new Dictionary<string, object>
				{
					["lobbyManaged"] = true, ["deviceId"] = $"intro-host-{index}",
					["name"] = $"测试房主{index + 1}", ["roomName"] = $"测试房间 {index + 1}", ["bots"] = index == 0,
				});
				_hosts.Add(host);
				host.OnMessage<object>("hand", _ => { });
				host.OnMessage<object>("player_joined", _ => { });
				host.OnMessage<object>("player_left", _ => { });
				host.OnMessage<object>("table_ready", _ => { });
				await host.WaitForFirstState();
			}
			await Until(() => U<Label>("RoomCount").Text == "4个房间在线", "room directory refresh");
			await Click(Find<Button>(U<Control>("CreatePage"), "JoinTab"));
			var fullRow = U<VBoxContainer>("RoomList").GetNode<Control>($"Room_{_hosts[0].RoomId}");
			Check(Find<Button>(fullRow, "JoinButton").Disabled, "Full room is joinable");
			Check(Find<Label>(fullRow, "Occupancy").Text == "4/4人", "Room occupancy did not update");
			await Capture("intro-rooms");
			await _hosts[0].Send("start_game");
			await Until(() => Find<Label>(fullRow, "JoinText").Text == "已开始", "started room disabled");
			Check(Find<Button>(fullRow, "JoinButton").Disabled, "Started room is joinable");
			var row = U<VBoxContainer>("RoomList").GetNode<Control>($"Room_{_hosts[3].RoomId}");
			var scroll = U<ScrollContainer>("RoomScroll");
			Check(scroll.GetVScrollBar().MaxValue > scroll.Size.Y, "Room list does not scroll");
			scroll.ScrollVertical = (int)scroll.GetVScrollBar().MaxValue;
			await Frames();
			await Click(Find<Button>(row, "JoinButton"));
			await Until(() => Scene is Table && GameSession.GameAdapter?.State?.players?.Count == 2, "joined waiting table");
			Check(!GameSession.GameAdapter.State.players[GameSession.GameAdapter.SessionId].isHost, "Joiner unexpectedly became host");
			heartsToggle = Find<CheckButton>(Scene, "HeartsBreakingToggle");
			discardToggle = Find<CheckButton>(Scene, "PointDiscardToggle");
			Check(heartsToggle.Disabled && discardToggle.Disabled, "Guest can edit room rules");
			await _hosts[3].Send("set_rules", new Dictionary<string, object>
			{
				["heartsBreakingEnabled"] = true, ["mustDiscardPointsWhenVoid"] = true,
			});
			await Until(() => heartsToggle.ButtonPressed && discardToggle.ButtonPressed, "guest sees changed rules");
			var guestTable = (Table)Scene;
			Check(!Find<Button>(Scene, "StartGameButton").IsVisibleInTree(), "Guest can see start action");
			Check(!Find<BaseButton>(guestTable.GetLocalPlayerInfo(1), "添加机器人按钮").IsVisibleInTree(), "Guest can add bots");
			await Click(Find<Button>(Scene, "ReadyButton"));
			await Until(() => GameSession.GameAdapter.State.players[GameSession.GameAdapter.SessionId].ready, "guest ready");
			Check(Find<Label>(guestTable.GetLocalPlayerInfo(0), "已准备Text").IsVisibleInTree(), "Ready badge not synchronized");
			await Click(Find<Button>(Scene, "CancelReadyButton"));
			await Until(() => !GameSession.GameAdapter.State.players[GameSession.GameAdapter.SessionId].ready, "guest cancelled ready");
			Check(Find<Label>(guestTable.GetLocalPlayerInfo(0), "未准备Text").IsVisibleInTree(), "Unready badge not synchronized");
			await _hosts[3].Leave(true);
			await Until(() => GameSession.GameAdapter.State.players[GameSession.GameAdapter.SessionId].isHost, "host transferred");
			Check(!heartsToggle.Disabled && Find<Button>(Scene, "StartGameButton").IsVisibleInTree(), "New host controls not refreshed");
			// Local seat is still 1: the former host's seat 0 appears at display seat 3.
			await Click(Find<BaseButton>(guestTable.GetLocalPlayerInfo(3), "添加机器人按钮"));
			await Until(() => GameSession.GameAdapter.State.players.Count == 2, "new host fills old host seat");
			Check(GameSession.GameAdapter.State.players.Keys.Cast<string>().Any(id =>
				GameSession.GameAdapter.State.players[id] is { isBot: true, seat: 0 }), "Relative seat mapped to wrong server seat");
			await ConfirmTableExit("房主将转交");
			await IntroReady();
			await Click(Find<Button>(U<Control>("CreatePage"), "JoinTab"));
			_hosts.RemoveAt(3);
			await Until(() => U<Label>("RoomCount").Text == "3个房间在线", "closed room removed");
			// A server-side rejection must release the action lock for a retry.
			await GameSession.LobbyAdapter.JoinRoomAsync("missing-room");
			await Until(() => U<Label>("IntroStatus").Text.StartsWith("操作失败"), "server rejection surfaced");
			Check(!U<Button>("CreateRoomButton").Disabled, "Server error left actions locked");
			await Click(U<Button>("BackToConnection"));
			await Until(() => Scene is Lobby, "return to connection screen");
			Check(GameSession.LobbyAdapter == null, "Lobby connection was not released");
			GD.Print("INTRO_SMOKE_OK: connection handoff, profile, nickname editing/validation/persistence, tab clicks, empty history, create/join, room rule toggles and guest synchronization, scrolling, live room updates, errors and return flow");
		}
		catch (Exception exception)
		{
			exitCode = 1;
			GD.PushError(exception.ToString());
		}
		finally
		{
			if (GameSession.GameAdapter != null) await GameSession.GameAdapter.DisconnectAsync();
			if (GameSession.LobbyAdapter != null) await GameSession.LobbyAdapter.DisconnectAsync();
			foreach (var host in _hosts) await host.Leave(true);
			GetTree().Quit(exitCode);
		}
	}

	private async Task ExerciseWaitingTable(string endpoint)
	{
		var table = (Table)Scene;
		var adapter = GameSession.GameAdapter;
		Check(table.GetNode<CanvasLayer>("Waiting").Visible && !table.GetNode<CanvasLayer>("InGame").Visible,
			"Wrong initial phase visibility");
		Check(!Find<BaseButton>(table.GetLocalPlayerInfo(0), "Waiting_踢出房间").IsVisibleInTree(), "Host can kick self");
		for (int seat = 1; seat < 4; seat++)
		{
			Check(table.GetLocalPlayerInfo(seat).GetNode<Control>("WaitingLayout").Visible, "Empty seat missing");
			Check(!table.GetLocalPlayerInfo(seat).GetNode<Control>("JoinedPlayerInfo").Visible, "Empty seat shows player");
		}
		await Click(Find<BaseButton>(table.GetLocalPlayerInfo(3), "添加机器人按钮"));
		await Until(() => adapter.State.players.Count == 2, "bot in selected seat");
		Check(adapter.State.players.Keys.Cast<string>().Any(id => adapter.State.players[id] is { isBot: true, seat: 3 }), "Bot seat incorrect");
		Check(Find<Button>(table, "StartGameButton").Disabled, "Incomplete room can start");
		await Click(Find<BaseButton>(table.GetLocalPlayerInfo(3), "Waiting_踢出房间"));
		await Until(() => adapter.State.players.Count == 1, "bot removed");
		Check(table.GetLocalPlayerInfo(3).GetNode<Control>("WaitingLayout").Visible, "Removed bot did not leave vacancy");

		var guest = await new Client(endpoint).JoinById<MyRoomState>(adapter.RoomId,
			new Dictionary<string, object> { ["deviceId"] = "waiting-table-guest" });
		_hosts.Add(guest);
		foreach (string message in new[] { "hand", "player_joined", "player_left", "game_ready", "room_reset" })
			guest.OnMessage<object>(message, _ => { });
		await guest.WaitForFirstState();
		await Until(() => adapter.State.players.Count == 2, "human joined vacancy");
		await guest.Send("ready", new Dictionary<string, object> { ["ready"] = true });
		for (int seat = 2; seat < 4; seat++)
		{
			await Click(Find<BaseButton>(table.GetLocalPlayerInfo(seat), "添加机器人按钮"));
			int count = seat + 1;
			await Until(() => adapter.State.players.Count == count, "bot joined");
		}
		await Until(() => !Find<Button>(table, "StartGameButton").Disabled, "all seats ready");
		await Click(Find<Button>(table, "StartGameButton"));
		await Until(() => adapter.State.phase == "table_ready", "start handshake");
		Check(ReferenceEquals(Scene, table) && !table.GetNode<CanvasLayer>("Waiting").Visible && table.GetNode<CanvasLayer>("InGame").Visible,
			"Start must switch layers in the same table");
		// The raw guest never sends table_ready; exercise the real server timeout.
		await Until(() => adapter.State.phase == "waiting", "handshake timeout returns to waiting", 40);
		Check(ReferenceEquals(Scene, table) && table.GetNode<CanvasLayer>("Waiting").Visible && !table.GetNode<CanvasLayer>("InGame").Visible,
			"Reset must reuse the table with waiting layers restored");
		Check(!adapter.State.players[guest.SessionId].ready, "Timeout did not reset guest readiness");
		bool kicked = false;
		guest.OnLeave += code => kicked = code == 4000;
		await Click(Find<BaseButton>(table.GetLocalPlayerInfo(1), "Waiting_踢出房间"));
		await Until(() => kicked && adapter.State.players.Count == 3, "human kicked and disconnected");
		_hosts.Remove(guest);
		await Click(Find<BaseButton>(table.GetLocalPlayerInfo(1), "添加机器人按钮"));
		await Until(() => adapter.State.players.Count == 4, "vacancy filled after kick");
		await Click(Find<Button>(table, "StartGameButton"));
		await Until(() => adapter.State.phase == "passing" && !table.IsDealing, "retry handshake and deal", 40);
		Check(ReferenceEquals(Scene, table) && table.LocalHand.Count == 13, "Retry did not deal in same table");
		Check(!Find<BaseButton>(table.GetLocalPlayerInfo(1), "Waiting_踢出房间").IsVisibleInTree(), "Kick action visible during game");
		Check(table.GetLocalPlayerInfo(1).GetNode<Control>("JoinedPlayerInfo/InnerContainer/InGame_得分").Visible, "In-game score hidden");
		await adapter.SubmitPassingCardsAsync(table.LocalHand.Take(3).ToArray());
		await Until(() => adapter.State.phase == "playing", "playing after waiting migration");
		GD.Print("WAITING_TABLE_SMOKE_OK: fixed seats, add/remove bot, kick/disconnect, phase visibility, timeout reset, retry and deal");
	}

	private async Task ExerciseGuestExit(string endpoint)
	{
		var host = await new Client(endpoint).Create<MyRoomState>("hearts", new Dictionary<string, object>
		{
			["lobbyManaged"] = true, ["deviceId"] = "exit-ui-host", ["roomName"] = "退出测试",
		});
		_hosts.Add(host);
		var hostHand = new TaskCompletionSource<string[]>();
		host.OnMessage<Dictionary<string, object>>("hand", payload =>
		{
			if (payload["cards"] is System.Collections.IEnumerable cards)
			{
				string[] selection = cards.Cast<object>().OfType<string>().Take(3).ToArray();
				if (selection.Length == 3) hostHand.TrySetResult(selection);
			}
		});
		foreach (string message in new[] { "player_joined", "player_left", "game_ready", "deal_started", "round_started",
			"passing_started", "passing_selected", "passing_received", "passing_completed", "turn_started", "card_played",
			"player_disconnected", "auto_play", "trick_resolved", "room_reset" })
			host.OnMessage<object>(message, _ => { });
		await host.WaitForFirstState();
		await GameSession.LobbyAdapter.JoinRoomAsync(host.RoomId);
		await Until(() => Scene is Table && GameSession.GameAdapter?.State?.players?.Count == 2, "guest enters exit test room");
		var adapter = GameSession.GameAdapter;
		string guestId = adapter.SessionId;
		await Click(Find<Button>(Scene, "ReadyButton"));
		for (int seat = 2; seat < 4; seat++)
			await host.Send("add_bot", new Dictionary<string, object> { ["seat"] = seat });
		await Until(() => host.State.players.Count == 4 && host.State.players[guestId].ready, "exit test room ready");
		await host.Send("start_game");
		await Until(() => adapter.State.phase == "table_ready", "exit test started");
		await host.Send("table_ready");
		await Until(() => adapter.State.phase == "dealing", "exit test dealing");
		await host.Send("deal_ready");
		await Until(() => adapter.State.phase == "passing" && !((Table)Scene).IsDealing, "exit test passing", 40);
		await host.Send("request_hand");
		await host.Send("pass_cards", new Dictionary<string, object> { ["cardIds"] = await hostHand.Task.WaitAsync(TimeSpan.FromSeconds(10)) });
		await adapter.SubmitPassingCardsAsync(((Table)Scene).LocalHand.Take(3).ToArray());
		await Until(() => adapter.State.phase == "playing", "exit test playing");
		int expectedBalance = adapter.State.players[guestId].chips;
		await ConfirmTableExit("机器人接替");
		await Until(() => host.State.players[guestId].isBot, "guest exit replaced by bot");
		Check(host.State.players.Count == 4 && host.State.phase == "playing", "Guest exit disrupted the round");
		Check(GameSession.Profile.Chips == expectedBalance, "Lobby did not refresh the forfeited balance");
		await GameSession.LobbyAdapter.CreateRoomAsync("退出后立即重开");
		await Until(() => Scene is Table && GameSession.GameAdapter?.State?.players?.Count == 1, "guest immediately creates another room");
		var newAdapter = GameSession.GameAdapter;
		Check(newAdapter.State.players[newAdapter.SessionId].chips == expectedBalance, "New room used a stale balance");
		await host.Leave(true);
		_hosts.Remove(host);
		Check(newAdapter.IsConnected, "Old host dissolution disconnected the guest's new room");
		await ConfirmTableExit("房主将转交");
		Check(GameSession.Profile.Chips == expectedBalance, "Old room refunded a forfeited ante");
		await Until(() => GameSession.LobbyAdapter.State.rooms.Count == 0, "exit test host dissolved room");
		GD.Print("EXIT_ROOM_SMOKE_OK: confirmation, forfeiture, immediate new room, bot takeover and host refunds");
	}

	private async Task ConfirmTableExit(string expectedMessage)
	{
		var table = (Table)Scene;
		var adapter = GameSession.GameAdapter;
		await Click(table.GetNode<BaseButton>("%ReturnToLobbyButton"));
		var dialog = table.GetNode<ConfirmationDialog>("ExitConfirmation");
		Check(dialog.Visible && dialog.DialogText.Contains(expectedMessage), "Exit confirmation did not explain the role-specific consequence");
		dialog.GetCancelButton().EmitSignal(BaseButton.SignalName.Pressed);
		await Frames();
		Check(!dialog.Visible && ReferenceEquals(Scene, table) && adapter.IsConnected, "Cancel must keep the room connection and table");
		await Click(table.GetNode<BaseButton>("%ReturnToLobbyButton"));
		dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
		await IntroReady();
	}

	private Node Scene => GetTree().CurrentScene;
	private T U<T>(string name) where T : Node => Scene.GetNode<T>($"%{name}");
	private static T Find<T>(Node parent, string name) where T : Node => (T)parent.FindChild(name, true, false);
	private Button FindButton(string text)
	{
		foreach (var node in Scene.FindChildren("*", "Button", true, false))
			if (node is Button button && button.Text == text) return button;
		throw new InvalidOperationException($"Button not found: {text}");
	}
	private async Task IntroReady()
	{
		await Until(() => Scene?.Name == "Intro" && GameSession.LobbyAdapter?.IsConnected == true &&
			Scene.GetNodeOrNull<Button>("%CreateRoomButton")?.Disabled == false, "connected Intro scene");
		await Frames();
	}
	private async Task Click(Control control, int repeat = 1)
	{
		Check(control.IsVisibleInTree(), $"Control is hidden: {control.Name}");
		Vector2 point = control.GetGlobalTransform() * (control.Size / 2);
		for (int attempt = 0; attempt < repeat; attempt++)
		{
			GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point,
				ButtonIndex = MouseButton.Left, Pressed = true }, true);
			GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point,
				ButtonIndex = MouseButton.Left, Pressed = false }, true);
		}
		await Frames();
	}
	private async Task Frames()
	{
		for (int frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}
	private async Task Until(Func<bool> condition, string description, int timeoutSeconds = 20)
	{
		var watch = Stopwatch.StartNew();
		while (!condition())
		{
			if (watch.Elapsed > TimeSpan.FromSeconds(timeoutSeconds)) throw new TimeoutException(description);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
	}
	private async Task Capture(string name)
	{
		if (string.IsNullOrEmpty(_captureDirectory)) return;
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_captureDirectory, name + ".png"));
	}
	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
