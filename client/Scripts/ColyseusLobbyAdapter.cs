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
	private TaskCompletionSource<PlayerProfile> _nameUpdate;
	private string _nameUpdateId;

	public event Action<LobbyState, bool> StateChanged;
	public event Action<RoomReservation> RoomReservationReceived;
	public event Action<string> ServerMessage;
	public event Action<string> Error;
	public event Action<PlayerProfile> ProfileReceived;
	public event Action<int> Left;
	public bool IsConnected => _room != null && Profile != null;
	public LobbyState State => _room?.State;
	public RoomReservation PendingReservation { get; private set; }
	public PlayerProfile Profile { get; private set; }
	public string PlayerName { get; private set; } = string.Empty;

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
				["deviceId"] = DeviceIdentity.GetDeviceId(),
			});
			_room.OnStateChange += (state, first) => StateChanged?.Invoke(state, first);
			_room.OnError += (code, message) => Error?.Invoke(message ?? $"大厅错误 ({code})");
			var connectedRoom = _room;
			_room.OnLeave += code =>
			{
				if (_room != connectedRoom) return;
				_room = null;
				_nameUpdate?.TrySetException(new InvalidOperationException("大厅连接已断开"));
				profileReady.TrySetException(new InvalidOperationException("同步存档时大厅连接已断开"));
				Left?.Invoke(code);
			};
			_room.OnMessage<Dictionary<string, object>>("player_profile", payload =>
			{
				try
				{
					ApplyProfile(payload);
					profileReady.TrySetResult(Profile);
				}
				catch (Exception exception) { profileReady.TrySetException(exception); }
			});
			_room.OnMessage<Dictionary<string, object>>("player_name_updated", payload =>
			{
				if (_room != connectedRoom || _nameUpdate == null || ReadString(payload, "requestId") != _nameUpdateId) return;
				try
				{
					string error = ReadString(payload, "error");
					if (error.Length > 0) throw new InvalidOperationException(error);
					ApplyProfile(payload);
					_nameUpdate.TrySetResult(Profile);
				}
				catch (Exception exception) { _nameUpdate?.TrySetException(exception); }
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

	public Task CreateRoomAsync(string name, int ante = 100, bool bots = false,
		bool heartsBreakingEnabled = false, bool mustDiscardPointsWhenVoid = false)
	{
		return _room == null || Profile == null ? Task.CompletedTask : _room.Send("create_room", new Dictionary<string, object>
		{
			["name"] = name ?? string.Empty,
			["ante"] = ante,
			["bots"] = bots,
			["heartsBreakingEnabled"] = heartsBreakingEnabled,
			["mustDiscardPointsWhenVoid"] = mustDiscardPointsWhenVoid,
		});
	}

	public Task JoinRoomAsync(string roomId)
	{
		return _room == null || Profile == null ? Task.CompletedTask : _room.Send("join_room", new Dictionary<string, object>
		{
			["roomId"] = roomId ?? string.Empty,
		});
	}

	public async Task<PlayerProfile> UpdatePlayerNameAsync(string name)
	{
		if (!IsConnected) throw new InvalidOperationException("请先连接大厅");
		if (_nameUpdate != null) throw new InvalidOperationException("昵称正在保存，请稍候");
		var completion = new TaskCompletionSource<PlayerProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
		_nameUpdate = completion;
		_nameUpdateId = Guid.NewGuid().ToString("N");
		try
		{
			await _room.Send("update_player_name", new Dictionary<string, object>
			{
				["name"] = name ?? string.Empty,
				["requestId"] = _nameUpdateId,
			});
			return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
		}
		finally
		{
			_nameUpdate = null;
			_nameUpdateId = null;
		}
	}

	private void ApplyProfile(Dictionary<string, object> payload)
	{
		Profile = PlayerProfile.FromPayload(payload);
		PlayerName = Profile.Name;
		GameSession.Profile = Profile;
		ProfileReceived?.Invoke(Profile);
	}

	public async Task DisconnectAsync()
	{
		var room = _room;
		_room = null;
		_nameUpdate?.TrySetException(new InvalidOperationException("大厅连接已断开"));
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
