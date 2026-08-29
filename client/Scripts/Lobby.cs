using System;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.Generated;

namespace HeartsAlter.Scripts;

/// <summary>Room directory UI. All room membership decisions remain server-side.</summary>
public partial class Lobby : Control
{
	[Export] public string Endpoint = ColyseusLobbyAdapter.DefaultEndpoint;
	private const string DefaultHost = "ddns.maydaymemory.com";
	private const string DefaultPort = "2567";

	private ColyseusLobbyAdapter _adapter;
	private VBoxContainer _roomList;
	private LineEdit _playerName;
	private LineEdit _roomName;
	private LineEdit _hostInput;
	private LineEdit _portInput;
	private OptionButton _endpointPreset;
	private Label _status;
	private Button _createButton;
	private Button _connectButton;
	private bool _transitioning;
	private bool _connected;

	public override void _Ready()
	{
		BuildUi();
		_adapter = new ColyseusLobbyAdapter();
		_adapter.StateChanged += HandleStateChanged;
		_adapter.RoomReservationReceived += HandleReservation;
		_adapter.ServerMessage += message => _status.Text = message;
		_adapter.Error += message => _status.Text = $"错误：{message}";
		SetConnectedUi(false);
	}

	public override void _ExitTree()
	{
		if (_adapter != null)
		{
			_adapter.StateChanged -= HandleStateChanged;
			_adapter.RoomReservationReceived -= HandleReservation;
			_ = _adapter.DisconnectAsync();
		}
	}

	private async Task ConnectAsync()
	{
		_adapter.PlayerName = _playerName.Text.Trim();
		string endpoint;
		if (!TryBuildEndpoint(out endpoint, out string error))
		{
			_status.Text = error;
			return;
		}
		_connectButton.Disabled = true;
		_status.Text = $"正在连接 {endpoint}…";
		if (!await _adapter.ConnectAsync(endpoint) && IsInsideTree())
		{
			_status.Text = "无法连接大厅，请确认服务端已启动";
			SetConnectedUi(false);
			return;
		}
		_connected = true;
		GameSession.ServerEndpoint = endpoint;
		SetConnectedUi(true);
		_status.Text = $"已连接 {endpoint}";
	}

	private async Task DisconnectAsync()
	{
		await _adapter.DisconnectAsync();
		_connected = false;
		SetConnectedUi(false);
		if (IsInsideTree()) _status.Text = "已断开大厅连接";
	}

	private void ToggleConnection()
	{
		if (_connected) _ = DisconnectAsync();
		else _ = ConnectAsync();
	}

	private void ApplyPreset(long index)
	{
		if (index == 0)
		{
			_hostInput.Text = DefaultHost;
			_portInput.Text = DefaultPort;
		}
		else
		{
			_hostInput.Text = "127.0.0.1";
			_portInput.Text = "2567";
		}
	}

	private bool TryBuildEndpoint(out string endpoint, out string error)
	{
		string host = _hostInput.Text.Trim();
		string portText = _portInput.Text.Trim();
		if (host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) host = host[5..];
		if (host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) host = host[6..];
		host = host.TrimEnd('/');
		if (host.Length == 0)
		{
			endpoint = string.Empty;
			error = "请输入服务器 IP 或域名";
			return false;
		}
		if (!int.TryParse(portText, out int port) || port < 1 || port > 65535)
		{
			endpoint = string.Empty;
			error = "端口号必须是 1-65535 之间的整数";
			return false;
		}
		endpoint = $"ws://{host}:{port}";
		error = string.Empty;
		return true;
	}

	private void HandleStateChanged(LobbyState state, bool first)
	{
		if (!IsInsideTree()) return;
		_status.Text = state.message;
		foreach (Node child in _roomList.GetChildren()) child.QueueFree();
		foreach (string roomKey in state.rooms.Keys)
		{
			LobbyRoomInfo info = state.rooms[roomKey];
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 16);
			var label = new Label
			{
				Text = $"{info.name}    {info.playerCount}/{info.maxPlayers}    准备 {info.readyCount}/{info.playerCount}    {PhaseText(info.phase)}",
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			};
			var join = new Button { Text = info.phase == "waiting" && info.playerCount < info.maxPlayers ? "加入" : "观望" };
			join.Disabled = info.phase != "waiting" || info.playerCount >= info.maxPlayers;
			string roomId = info.roomId;
			join.Pressed += () => JoinRoom(roomId);
			row.AddChild(label);
			row.AddChild(join);
			_roomList.AddChild(row);
		}
	}

	private void JoinRoom(string roomId)
	{
		if (!_connected) return;
		_adapter.PlayerName = _playerName.Text.Trim();
		_status.Text = "正在进入准备房间…";
		_ = _adapter.JoinRoomAsync(roomId);
	}

	private void CreateRoom()
	{
		if (!_connected) return;
		_adapter.PlayerName = _playerName.Text.Trim();
		_createButton.Disabled = true;
		_status.Text = "正在创建房间…";
		_ = CreateRoomAsync();
	}

	private async Task CreateRoomAsync()
	{
		await _adapter.CreateRoomAsync(_roomName.Text.Trim());
		if (_createButton is not null && IsInstanceValid(_createButton))
			_createButton.Disabled = false;
	}

	private void HandleReservation(RoomReservation reservation)
	{
		if (_transitioning) return;
		_transitioning = true;
		GameSession.PendingReservation = reservation;
		_status.Text = "已锁定席位，正在进入准备房间…";
		_ = TransitionToReadyRoomAsync();
	}

	private async Task TransitionToReadyRoomAsync()
	{
		await _adapter.DisconnectAsync();
		GetTree().ChangeSceneToFile("res://scenes/ReadyRoom.tscn");
	}

	private void BuildUi()
	{
		var margin = new MarginContainer();
		margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		margin.AddThemeConstantOverride("margin_left", 72);
		margin.AddThemeConstantOverride("margin_right", 72);
		margin.AddThemeConstantOverride("margin_top", 42);
		margin.AddThemeConstantOverride("margin_bottom", 42);
		AddChild(margin);
		var column = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		column.AddThemeConstantOverride("separation", 14);
		margin.AddChild(column);
		var title = new Label { Text = "红心大战 · 大厅" };
		title.AddThemeFontSizeOverride("font_size", 30);
		column.AddChild(title);
		var endpointRow = new HBoxContainer();
		endpointRow.AddChild(new Label { Text = "服务器" });
		_hostInput = new LineEdit { Text = DefaultHost, PlaceholderText = "IP / 域名", CustomMinimumSize = new Vector2(250, 0) };
		endpointRow.AddChild(_hostInput);
		endpointRow.AddChild(new Label { Text = ":" });
		_portInput = new LineEdit { Text = DefaultPort, PlaceholderText = "端口", CustomMinimumSize = new Vector2(90, 0) };
		_portInput.MaxLength = 5;
		endpointRow.AddChild(_portInput);
		_endpointPreset = new OptionButton { CustomMinimumSize = new Vector2(280, 0) };
		_endpointPreset.AddItem("ddns.maydaymemory.com:2567（默认）");
		_endpointPreset.AddItem("127.0.0.1:2567");
		_endpointPreset.Selected = 0;
		_endpointPreset.ItemSelected += ApplyPreset;
		endpointRow.AddChild(_endpointPreset);
		_connectButton = new Button { Text = "连接大厅" };
		_connectButton.Pressed += ToggleConnection;
		endpointRow.AddChild(_connectButton);
		column.AddChild(endpointRow);
		var profile = new HBoxContainer();
		profile.AddChild(new Label { Text = "昵称" });
		_playerName = new LineEdit { Text = "玩家 1", CustomMinimumSize = new Vector2(180, 0) };
		profile.AddChild(_playerName);
		profile.AddChild(new Label { Text = "房间名" });
		_roomName = new LineEdit { Text = "新房间", CustomMinimumSize = new Vector2(220, 0) };
		profile.AddChild(_roomName);
		_createButton = new Button { Text = "创建房间" };
		_createButton.Pressed += CreateRoom;
		profile.AddChild(_createButton);
		column.AddChild(profile);
		_status = new Label { Text = "请输入服务器地址后连接大厅" };
		column.AddChild(_status);
		var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		_roomList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		scroll.AddChild(_roomList);
		column.AddChild(scroll);
	}

	private void SetConnectedUi(bool connected)
	{
		if (_connectButton is not null && IsInstanceValid(_connectButton))
		{
			_connectButton.Disabled = false;
			_connectButton.Text = connected ? "断开大厅" : "连接大厅";
		}
		if (_createButton is not null && IsInstanceValid(_createButton))
			_createButton.Disabled = !connected;
		if (_endpointPreset is not null && IsInstanceValid(_endpointPreset))
			_endpointPreset.Disabled = connected;
		if (_hostInput is not null && IsInstanceValid(_hostInput)) _hostInput.Editable = !connected;
		if (_portInput is not null && IsInstanceValid(_portInput)) _portInput.Editable = !connected;
	}

	private static string PhaseText(string phase) => phase switch
	{
		"playing" => "游戏中",
		"table_ready" => "进入牌桌",
		"dealing" => "发牌中",
		"passing" => "传牌中",
		"finished" => "已结束",
		_ => "等待中",
	};
}
