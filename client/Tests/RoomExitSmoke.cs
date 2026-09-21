using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Colyseus;
using HeartsAlter.Scripts;
using HeartsAlter.Scripts.Generated;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.UI;

namespace HeartsAlter.Tests;

/// <summary>Exercises room exit and the fresh lobby connection against a disposable server.</summary>
public partial class RoomExitSmoke : Node
{
	private string _captureDirectory;
	private Room<MyRoomState> _host;

	public override async void _Ready()
	{
		string endpoint = "ws://127.0.0.1:2583";
		foreach (string arg in OS.GetCmdlineUserArgs())
		{
			if (arg.StartsWith("--endpoint=")) endpoint = arg[11..];
			if (arg.StartsWith("--capture-dir=")) _captureDirectory = arg[14..];
		}
		int exitCode = 0;
		try
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			GetTree().CurrentScene = null;
			GameSession.Clear();
			GameSession.ServerEndpoint = endpoint;
			Check(SceneNavigation.Change(this, "res://scenes/Intro.tscn") == Error.Ok, "Cannot open Intro");
			await IntroReady();
			int originalBalance = GameSession.Profile.Chips;
			for (int attempt = 0; attempt < 3; attempt++)
			{
				await GameSession.LobbyAdapter.CreateRoomAsync($"Exit regression {attempt}", bots: true);
				await Until(() => Scene is Table && GameSession.GameAdapter?.State?.players?.Count == 4, "created table");
				var adapter = GameSession.GameAdapter;
				await adapter.StartGameAsync();
				await Until(() => adapter.State.phase == "passing" && !((Table)Scene).IsDealing, "passing phase", 40);
				await adapter.SubmitPassingCardsAsync(((Table)Scene).LocalHand.Take(3).ToArray());
				await Until(() => adapter.State.phase == "playing", "playing phase");
				var table = (Table)Scene;
				table.GetNode<BaseButton>("%ReturnToLobbyButton").EmitSignal(BaseButton.SignalName.Pressed);
				var dialog = table.GetNode<RoomDialog>("RoomDialogs/QuitRoomWindow");
				Check(dialog.Visible && dialog.ConfirmText == "解散并退出", "Host exit confirmation missing");
				Check(dialog.Message.Contains("100 金币"), "Host refund missing from confirmation");
				if (attempt == 0)
				{
					await Capture("quit-host");
					await Click(dialog.GetNode<Control>("%CancelArt"));
					Check(!dialog.Visible && adapter.IsConnected, "Cancel artwork must keep the player in the room");
					table.GetNode<BaseButton>("%ReturnToLobbyButton").EmitSignal(BaseButton.SignalName.Pressed);
					await Click(dialog.GetNode<Button>("%CloseButton"));
					Check(!dialog.Visible && adapter.IsConnected, "Close must cancel exit");
					table.GetNode<BaseButton>("%ReturnToLobbyButton").EmitSignal(BaseButton.SignalName.Pressed);
				}
				await Click(dialog.GetNode<Control>("%ConfirmArt"));
				await IntroReady();
				await Until(() => GameSession.LobbyAdapter.State.rooms.Count == 0, "dissolved room removed");
				Check(GameSession.Profile.Chips == originalBalance, "Host ante not refunded");
				Check(!adapter.IsConnected, "Old game room still connected");
				GD.Print($"ROOM_EXIT_ROUNDTRIP_OK: {attempt + 1}");
			}
			await ExerciseHostTransfer(endpoint);
			await ExerciseDissolvedRoom(endpoint, false);
			await ExerciseDissolvedRoom(endpoint, true);
			GD.Print("ROOM_EXIT_SMOKE_OK: artwork hit areas, role changes, repeated host exits, guest dissolution notice and lobby reconnect");
		}
		catch (Exception exception)
		{
			exitCode = 1;
			GD.PushError(exception.ToString());
		}
		finally
		{
			if (_host is not null) await _host.Leave(true);
			if (GameSession.GameAdapter != null) await GameSession.GameAdapter.DisconnectAsync();
			if (GameSession.LobbyAdapter != null) await GameSession.LobbyAdapter.DisconnectAsync();
			GetTree().Quit(exitCode);
		}
	}

	private async Task JoinGuestRoom(string endpoint)
	{
		_host = await new Client(endpoint).Create<MyRoomState>("hearts", new Dictionary<string, object>
		{
			["lobbyManaged"] = true, ["deviceId"] = "room-dialog-host", ["roomName"] = "弹窗测试",
		});
		foreach (string message in new[] { "hand", "player_joined", "player_left", "game_ready", "deal_started",
			"passing_started", "passing_selected", "room_disbanded", "room_reset" })
			_host.OnMessage<object>(message, _ => { });
		await _host.WaitForFirstState();
		await GameSession.LobbyAdapter.JoinRoomAsync(_host.RoomId);
		await Until(() => Scene is Table && GameSession.GameAdapter?.State?.players?.Count == 2, "guest table");
	}

	private async Task ExerciseHostTransfer(string endpoint)
	{
		await JoinGuestRoom(endpoint);
		var table = (Table)Scene;
		var adapter = GameSession.GameAdapter;
		table.GetNode<BaseButton>("%ReturnToLobbyButton").EmitSignal(BaseButton.SignalName.Pressed);
		var dialog = table.GetNode<RoomDialog>("RoomDialogs/QuitRoomWindow");
		Check(!dialog.Message.Contains("房主") && !dialog.Message.Contains("金币"), "Waiting guest copy is incorrect");
		await _host.Leave(true);
		_host = null;
		await Until(() => adapter.State.players[adapter.SessionId].isHost && dialog.Message.Contains("房主将转交"), "open dialog updates after host transfer");
		Check(!table.GetNode<RoomDialog>("RoomDialogs/ReleaseRoomWindow").Visible, "Waiting host transfer must not show dissolution");
		GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
		Check(!dialog.Visible && adapter.IsConnected, "Escape must cancel exit");
		table.GetNode<BaseButton>("%ReturnToLobbyButton").EmitSignal(BaseButton.SignalName.Pressed);
		await Click(dialog.GetNode<Control>("%ConfirmArt"));
		await IntroReady();
	}

	private async Task ExerciseDissolvedRoom(string endpoint, bool hostLeaves)
	{
		int originalBalance = GameSession.Profile.Chips;
		await JoinGuestRoom(endpoint);
		var table = (Table)Scene;
		var adapter = GameSession.GameAdapter;
		await adapter.SetReadyAsync(true);
		foreach (int seat in new[] { 2, 3 })
			await _host.Send("add_bot", new Dictionary<string, object> { ["seat"] = seat });
		await Until(() => _host.State.players.Count == 4 && _host.State.players[adapter.SessionId].ready, "ready guest room");
		await _host.Send("start_game");
		await Until(() => adapter.State.phase == "table_ready", "guest table handshake");
		await _host.Send("table_ready");
		await Until(() => adapter.State.phase == "dealing", "guest dealing");
		await _host.Send("deal_ready");
		await Until(() => adapter.State.phase == "passing" && !table.IsDealing, "guest passing", 40);
		table.GetNode<BaseButton>("%ReturnToLobbyButton").EmitSignal(BaseButton.SignalName.Pressed);
		var quit = table.GetNode<RoomDialog>("RoomDialogs/QuitRoomWindow");
		Check(quit.Message.Contains("放弃本局投入的 100 金币") && quit.Message.Contains("机器人接替"), "Guest forfeiture copy missing");
		Check(quit.ConfirmText == "确定退出", "Guest button must not offer dissolution");
		await Capture("quit-guest");
		// Dissolution must replace a pending quit dialog without waiting for the guest to act.
		if (hostLeaves) await _host.Leave(true);
		else await _host.Send("disband_room");
		_host = null;
		var release = table.GetNode<RoomDialog>("RoomDialogs/ReleaseRoomWindow");
		await Until(() => release.Visible, "room dissolved notice");
		Check(ReferenceEquals(Scene, table) && !quit.Visible && !adapter.IsConnected, "Guest must acknowledge dissolution before leaving the table");
		await Capture("room-dissolved");
		await Click(release.GetNode<Control>(hostLeaves ? "%CloseButton" : "%ConfirmArt"));
		await IntroReady();
		Check(GameSession.Profile.Chips == originalBalance, "Dissolution must refund the guest's ante");
	}

	private async Task Click(Control control)
	{
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Vector2 point = control.GetGlobalTransform() * (control.Size / 2);
		foreach (bool pressed in new[] { true, false })
			GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point,
				ButtonIndex = MouseButton.Left, Pressed = pressed }, true);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}

	private async Task Capture(string name)
	{
		if (string.IsNullOrEmpty(_captureDirectory)) return;
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_captureDirectory, name + ".png"));
	}

	private Node Scene => GetTree().CurrentScene;

	private async Task IntroReady()
	{
		await Until(() => Scene?.Name == "Intro" && GameSession.LobbyAdapter?.IsConnected == true &&
			!Scene.GetNode<Button>("%CreateRoomButton").Disabled &&
			Scene.GetNode<Label>("%IntroStatus").Text == "大厅已连接", "connected Intro");
		Check(Scene.GetNode<TextureRect>("%AvatarTexture").Texture.ResourcePath ==
			$"res://assets/textures/ui/profile_icon_{GameSession.Profile.AvatarId}.jpg", "Saved avatar not restored");
		Check(Scene.GetNode<Label>("%PlayerName").Text == GameSession.Profile.Name, "Saved name not restored");
	}

	private async Task Until(Func<bool> condition, string description, int timeoutSeconds = 20)
	{
		var watch = Stopwatch.StartNew();
		while (!condition())
		{
			string status = Scene?.Name == "Intro" ? Scene.GetNode<Label>("%IntroStatus").Text : string.Empty;
			Check(!status.StartsWith("操作失败"), status);
			if (watch.Elapsed > TimeSpan.FromSeconds(timeoutSeconds)) throw new TimeoutException(description + ": " + status);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
	}

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
