using System;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts;
using HeartsAlter.Scripts.InGame;

namespace HeartsAlter.Tests;

/// <summary>Runs against a disposable local server; see the client README.</summary>
public partial class ProfileSmoke : Node
{
	public override async void _Ready()
	{
		var lobby = new ColyseusLobbyAdapter();
		var game = new ColyseusClientAdapter();
		string endpoint = "ws://127.0.0.1:2571";
		foreach (string argument in OS.GetCmdlineUserArgs())
			if (argument.StartsWith("--endpoint=")) endpoint = argument[11..];
		lobby.Error += GD.PushError;
		game.Error += (_, message) => GD.PushError(message);
		int exitCode = 0;
		try
		{
			Check(await lobby.ConnectAsync(endpoint), "Lobby connection failed");
			PlayerProfile profile = lobby.Profile;
			Check(profile != null && GameSession.Profile == profile, "Private profile was not cached");
			Check(profile.Name == $"玩家 {profile.PlayerId[..4]}", "First connection did not receive a server-assigned nickname");
			Check(profile.AvatarId >= 1 && profile.AvatarId <= 4, "Invalid avatar id");
			await lobby.DisconnectAsync();
			Check(await lobby.ConnectAsync(endpoint), "Lobby reconnect failed");
			Check(lobby.Profile.PlayerId == profile.PlayerId && lobby.Profile.AvatarId == profile.AvatarId &&
				lobby.Profile.Name == profile.Name && lobby.Profile.Chips == profile.Chips, "Reconnect changed saved identity");

			var widget = GD.Load<PackedScene>("res://scenes/in_game/PlayerInfo.tscn").Instantiate<PlayerInfo>();
			AddChild(widget);
			widget.SetProfile(profile);
			Check(widget.PlayerId == profile.Name && widget.ChipCount == profile.Chips, "Widget did not apply the save");
			for (int avatarId = 1; avatarId <= 4; avatarId++)
			{
				widget.SetAvatarId(avatarId);
				Check(widget.AvatarTexture?.ResourcePath == $"res://assets/textures/ui/profile_icon_{avatarId}.jpg", "Wrong avatar resource");
				if (widget.FindChild("AvatarTexture", true, false) is TextureRect avatar)
					Check(avatar.Texture == widget.AvatarTexture, "Scene texture was not refreshed");
			}
			widget.SetAvatarId(255);
			Check(widget.AvatarTexture.ResourcePath.EndsWith("profile_icon_1.jpg"), "Unknown avatar did not fall back");
			widget.QueueFree();

			var reservation = new TaskCompletionSource<RoomReservation>();
			lobby.RoomReservationReceived += value => reservation.TrySetResult(value);
			await lobby.CreateRoomAsync("Profile smoke", bots: true);
			RoomReservation seat = await reservation.Task.WaitAsync(TimeSpan.FromSeconds(10));
			Check(await game.ConnectByReservationAsync(seat, endpoint), "Seat reservation failed");
			var player = game.State.players[game.SessionId];
			Check(player.profileId == profile.PlayerId && player.avatarId == profile.AvatarId &&
				player.name == profile.Name && player.chips == profile.Chips, "Game schema does not match saved identity");
			Check(game.State.players.Count == 4, "Demo bots missing");
			GD.Print("PROFILE_SMOKE_OK: server-assigned nickname, private save, reconnect, C# schema, reservation and all avatars");
		}
		catch (Exception exception)
		{
			exitCode = 1;
			GD.PushError(exception.ToString());
		}
		finally
		{
			await game.DisconnectAsync();
			await lobby.DisconnectAsync();
			GetTree().Quit(exitCode);
		}
	}

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
