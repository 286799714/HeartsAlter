using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Colyseus;
using Godot;
using HeartsAlter.Scripts;
using HeartsAlter.Scripts.Generated;

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
			Find<LineEdit>(Scene, "PlayerNameInput").Text = "大厅测试玩家";
			await Click(Find<Button>(Scene, "ConnectButton"));
			await IntroReady();
			var lobby = GameSession.LobbyAdapter;
			Check(lobby.IsConnected, "Lobby did not hand off a live connection");
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
			await Until(() => Scene is ReadyRoom && GameSession.GameAdapter?.State?.players?.Count == 1, "created ready room");
			Check(!lobby.IsConnected, "Intro leaked its lobby connection after entering a room");
			Check(GameSession.GameAdapter.State.players[GameSession.GameAdapter.SessionId].isHost, "Room creator is not the host");
			Check(GameSession.GameAdapter.State.players[GameSession.GameAdapter.SessionId].name == "回车提交昵称", "Room did not use saved nickname");
			await Click(FindButton("返回大厅"));
			await IntroReady();
			await Until(() => GameSession.LobbyAdapter.State.rooms.Count == 0, "created room removed after leaving");
			Check(U<Label>("PlayerName").Text == "回车提交昵称", "Nickname did not persist after reconnecting");

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
			await Until(() => Scene is ReadyRoom && GameSession.GameAdapter?.State?.players?.Count == 2, "joined ready room");
			Check(!GameSession.GameAdapter.State.players[GameSession.GameAdapter.SessionId].isHost, "Joiner unexpectedly became host");
			await Click(FindButton("返回大厅"));
			await IntroReady();
			await Click(Find<Button>(U<Control>("CreatePage"), "JoinTab"));
			await _hosts[3].Leave(true);
			_hosts.RemoveAt(3);
			await Until(() => U<Label>("RoomCount").Text == "3个房间在线", "closed room removed");
			// A server-side rejection must release the action lock for a retry.
			await GameSession.LobbyAdapter.JoinRoomAsync("missing-room");
			await Until(() => U<Label>("IntroStatus").Text.StartsWith("操作失败"), "server rejection surfaced");
			Check(!U<Button>("CreateRoomButton").Disabled, "Server error left actions locked");
			await Click(U<Button>("BackToConnection"));
			await Until(() => Scene is Lobby, "return to connection screen");
			Check(GameSession.LobbyAdapter == null, "Lobby connection was not released");
			GD.Print("INTRO_SMOKE_OK: connection handoff, profile, nickname editing/validation/persistence, tab clicks, empty history, create/join, scrolling, live room updates, errors and return flow");
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
	private async Task Until(Func<bool> condition, string description)
	{
		var watch = Stopwatch.StartNew();
		while (!condition())
		{
			if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(description);
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
