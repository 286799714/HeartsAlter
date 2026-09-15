using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Colyseus;
using HeartsAlter.Scripts.Generated;

namespace HeartsAlter.Scripts;

/// <summary>Network boundary for the public room directory.</summary>
public sealed class ColyseusLobbyAdapter
{
	public const string DefaultEndpoint = ColyseusClientAdapter.DefaultEndpoint;
	private Client _client;
	private Room<LobbyState> _room;

	public event Action<LobbyState, bool> StateChanged;
	public event Action<RoomReservation> RoomReservationReceived;
	public event Action<string> ServerMessage;
	public event Action<string> Error;
	public event Action<PlayerProfile> ProfileReceived;
	public RoomReservation PendingReservation { get; private set; }
	public PlayerProfile Profile { get; private set; }
	public string PlayerName { get; set; } = "玩家 1";

	public async Task<bool> ConnectAsync(string endpoint = DefaultEndpoint)
	{
		if (_room != null) return true;
		try
		{
			Profile = null;
			GameSession.Profile = null;
			var profileReady = new TaskCompletionSource<PlayerProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
			_client = new Client(endpoint);
			_room = await _client.JoinOrCreate<LobbyState>("lobby", new Dictionary<string, object>
			{
				["name"] = PlayerName,
				["deviceId"] = DeviceIdentity.GetDeviceId(),
			});
			_room.OnStateChange += (state, first) => StateChanged?.Invoke(state, first);
			_room.OnError += (code, message) => Error?.Invoke(message ?? $"大厅错误 ({code})");
			_room.OnLeave += _ =>
			{
				_room = null;
				profileReady.TrySetException(new InvalidOperationException("同步存档时大厅连接已断开"));
			};
			_room.OnMessage<Dictionary<string, object>>("player_profile", payload =>
			{
				try
				{
					Profile = PlayerProfile.FromPayload(payload);
					PlayerName = Profile.Name;
					GameSession.Profile = Profile;
					ProfileReceived?.Invoke(Profile);
					profileReady.TrySetResult(Profile);
				}
				catch (Exception exception) { profileReady.TrySetException(exception); }
			});
			_room.OnMessage<Dictionary<string, object>>("lobby_ready", payload => ServerMessage?.Invoke("大厅已连接"));
			_room.OnMessage<Dictionary<string, object>>("lobby_error", payload => Error?.Invoke(ReadString(payload, "message")));
			_room.OnMessage<Dictionary<string, object>>("room_joined", OnRoomJoined);
			await _room.WaitForFirstState();
			// onJoin can beat C# message-handler registration. Explicitly request
			// the save again, then enable room actions only after it arrives.
			await _room.Send("request_profile");
			await profileReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await _room.Send("refresh");
			return true;
		}
		catch (Exception exception)
		{
			Error?.Invoke(exception.Message);
			await DisconnectAsync();
			return false;
		}
	}

	public Task CreateRoomAsync(string name, int ante = 100, bool bots = false)
	{
		return _room == null || Profile == null ? Task.CompletedTask : _room.Send("create_room", new Dictionary<string, object>
		{
			["name"] = name ?? string.Empty,
			["ante"] = ante,
			["bots"] = bots,
		});
	}

	public Task JoinRoomAsync(string roomId)
	{
		return _room == null || Profile == null ? Task.CompletedTask : _room.Send("join_room", new Dictionary<string, object>
		{
			["roomId"] = roomId ?? string.Empty,
		});
	}

	public async Task DisconnectAsync()
	{
		var room = _room;
		_room = null;
		if (room == null) return;
		try { await room.Leave(true); } catch { }
	}

	private void OnRoomJoined(Dictionary<string, object> payload)
	{
		var reservation = new RoomReservation
		{
			Name = ReadString(payload, "name"),
			SessionId = ReadString(payload, "sessionId"),
			RoomId = ReadString(payload, "roomId"),
			PublicAddress = ReadString(payload, "publicAddress"),
			ProcessId = ReadString(payload, "processId"),
			ReconnectionToken = ReadString(payload, "reconnectionToken"),
			Protocol = ReadString(payload, "protocol"),
		};
		PendingReservation = reservation;
		RoomReservationReceived?.Invoke(reservation);
	}

	private static string ReadString(Dictionary<string, object> payload, string key)
	{
		return payload != null && payload.TryGetValue(key, out var value) && value is string text
			? text : string.Empty;
	}
}
