using System;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.Generated;

namespace HeartsAlter.Scripts;

/// <summary>Room directory UI. All room membership decisions remain server-side.</summary>
public partial class Lobby : Control
{
	[Export] public string Endpoint = ColyseusLobbyAdapter.DefaultEndpoint;

	private ColyseusLobbyAdapter _adapter;
	private VBoxContainer _roomList;
	private LineEdit _playerName;
	private LineEdit _roomName;
	private Label _status;
	private Button _createButton;
	private bool _transitioning;

	public override void _Ready()
	{
		BuildUi();
		_adapter = new ColyseusLobbyAdapter();
		_adapter.StateChanged += HandleStateChanged;
		_adapter.RoomReservationReceived += HandleReservation;
		_adapter.ServerMessage += message => _status.Text = message;
		_adapter.Error += message => _status.Text = $"错误：{message}";
		_ = ConnectAsync();
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
		if (!await _adapter.ConnectAsync(Endpoint) && IsInsideTree())
			_status.Text = "无法连接大厅，请确认服务端已启动";
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
		_adapter.PlayerName = _playerName.Text.Trim();
		_status.Text = "正在进入准备房间…";
		_ = _adapter.JoinRoomAsync(roomId);
	}

	private void CreateRoom()
	{
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
		_status = new Label { Text = "正在连接大厅…" };
		column.AddChild(_status);
		var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		_roomList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		scroll.AddChild(_roomList);
		column.AddChild(scroll);
	}

	private static string PhaseText(string phase) => phase switch
	{
		"playing" => "游戏中",
		"table_ready" => "进入牌桌",
		"dealing" => "发牌中",
		"finished" => "已结束",
		_ => "等待中",
	};
}
