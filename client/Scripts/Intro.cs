using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.Generated;

namespace HeartsAlter.Scripts;

/// <summary>Binds the imported Intro artwork to the server-owned lobby and profile.</summary>
public partial class Intro : Node
{
	private ColyseusLobbyAdapter _adapter;
	private Control _createPage;
	private Control _joinPage;
	private Control _historyPage;
	private Control _roomTemplate;
	private VBoxContainer _roomList;
	private Label _emptyRooms;
	private Label _roomCount;
	private Label _playerName;
	private LineEdit _playerNameInput;
	private Button _editNameButton;
	private Button _saveNameButton;
	private Button _cancelNameButton;
	private Label _playerChips;
	private TextureRect _avatar;
	private LineEdit _roomName;
	private Button _createButton;
	private Button _backButton;
	private Label _status;
	private readonly Dictionary<string, RoomRow> _rows = new();
	private readonly CancellationTokenSource _lifetime = new();
	private bool _requestPending;
	private bool _editingName;
	private bool _savingName;
	private bool _transitioning;
	private int _requestVersion;

	public override void _Ready()
	{
		_createPage = GetNode<Control>("%CreatePage");
		_joinPage = GetNode<Control>("%JoinPage");
		_historyPage = GetNode<Control>("%HistoryPage");
		_roomTemplate = GetNode<Control>("%RoomRowTemplate");
		_roomList = GetNode<VBoxContainer>("%RoomList");
		_emptyRooms = GetNode<Label>("%EmptyRooms");
		_roomCount = GetNode<Label>("%RoomCount");
		_playerName = GetNode<Label>("%PlayerName");
		_playerNameInput = GetNode<LineEdit>("%PlayerNameInput");
		_editNameButton = GetNode<Button>("%EditPlayerName");
		_saveNameButton = GetNode<Button>("%SavePlayerName");
		_cancelNameButton = GetNode<Button>("%CancelPlayerName");
		_playerChips = GetNode<Label>("%PlayerChips");
		_avatar = GetNode<TextureRect>("%PlayerAvatar");
		_roomName = GetNode<LineEdit>("%RoomNameInput");
		_createButton = GetNode<Button>("%CreateRoomButton");
		_backButton = GetNode<Button>("%BackToConnection");
		_status = GetNode<Label>("%IntroStatus");
		foreach (Control page in new[] { _createPage, _joinPage, _historyPage })
		{
			Find<Button>(page, "CreateTab").Pressed += () => ShowPage(_createPage);
			Find<Button>(page, "JoinTab").Pressed += () => ShowPage(_joinPage);
			Find<Button>(page, "HistoryTab").Pressed += () => ShowPage(_historyPage);
		}
		_createButton.Pressed += CreateRoom;
		_roomName.TextSubmitted += _ => CreateRoom();
		_backButton.Pressed += () => _ = ReturnToConnectionAsync();
		_editNameButton.Pressed += BeginNameEdit;
		_saveNameButton.Pressed += () => _ = SaveNameAsync();
		_playerNameInput.TextSubmitted += text => _ = SaveNameAsync();
		_cancelNameButton.Pressed += CancelNameEdit;
		_playerNameInput.GuiInput += input =>
		{
			if (input is InputEventKey { Pressed: true, Keycode: Key.Escape } && !_savingName)
			{
				CancelNameEdit();
				_playerNameInput.AcceptEvent();
			}
		};
		ShowPage(_createPage);
		_adapter = GameSession.LobbyAdapter ?? new ColyseusLobbyAdapter();
		GameSession.LobbyAdapter = _adapter;
		_adapter.StateChanged += HandleStateChanged;
		_adapter.ProfileReceived += HandleProfile;
		_adapter.RoomReservationReceived += HandleReservation;
		_adapter.Error += HandleError;
		_adapter.Left += HandleLeft;
		RefreshActions();
		if (_adapter.IsConnected)
		{
			ApplyConnectedState();
		}
		else if (!string.IsNullOrWhiteSpace(GameSession.ServerEndpoint))
		{
			_ = ReconnectAsync();
		}
		else
		{
			_status.Text = "请先返回连接页面，连接服务器";
		}
	}

	public override void _ExitTree()
	{
		_lifetime.Cancel();
		_lifetime.Dispose();
		if (_adapter == null) return;
		_adapter.StateChanged -= HandleStateChanged;
		_adapter.ProfileReceived -= HandleProfile;
		_adapter.RoomReservationReceived -= HandleReservation;
		_adapter.Error -= HandleError;
		_adapter.Left -= HandleLeft;
		if (GameSession.LobbyAdapter == _adapter) GameSession.LobbyAdapter = null;
		_ = _adapter.DisconnectAsync();
	}

	private async Task ReconnectAsync()
	{
		_status.Text = "正在连接大厅并同步玩家信息…";
		_backButton.Disabled = true;
		_adapter.PlayerName = GameSession.Profile?.Name ?? "玩家 1";
		bool connected = await _adapter.ConnectAsync(GameSession.ServerEndpoint);
		if (!IsInstanceValid(this) || !IsInsideTree())
		{
			await _adapter.DisconnectAsync();
			return;
		}
		_backButton.Disabled = false;
		if (connected) ApplyConnectedState();
		else RefreshActions();
	}

	private void ApplyConnectedState()
	{
		HandleProfile(_adapter.Profile);
		HandleStateChanged(_adapter.State, true);
		_status.Text = "大厅已连接";
		RefreshActions();
	}

	private void ShowPage(Control selected)
	{
		_createPage.Visible = selected == _createPage;
		_joinPage.Visible = selected == _joinPage;
		_historyPage.Visible = selected == _historyPage;
	}

	private void HandleProfile(PlayerProfile profile)
	{
		if (!IsInsideTree() || profile == null) return;
		if (string.IsNullOrWhiteSpace(_roomName.Text) || _roomName.Text == $"{_playerName.Text}的房间")
			_roomName.Text = $"{profile.Name}的房间";
		_playerName.Text = profile.Name;
		_playerName.TooltipText = $"{profile.Name}\n玩家 ID：{profile.PlayerId}";
		_playerChips.Text = profile.Chips.ToString("N0", CultureInfo.InvariantCulture);
		_playerChips.TooltipText = "最近结算后的筹码";
		_avatar.Texture = GD.Load<Texture2D>($"res://assets/textures/ui/profile_icon_{profile.AvatarId}.jpg");
	}

	private void HandleStateChanged(LobbyState state, bool first)
	{
		if (!IsInsideTree() || state?.rooms == null) return;
		var remaining = new HashSet<string>(_rows.Keys);
		foreach (string key in state.rooms.Keys)
		{
			LobbyRoomInfo room = state.rooms[key];
			remaining.Remove(key);
			if (!_rows.TryGetValue(key, out RoomRow row))
			{
				var view = (Control)_roomTemplate.Duplicate();
				view.UniqueNameInOwner = false;
				view.Name = $"Room_{key}";
				_roomList.AddChild(view);
				view.Show();
				row = new RoomRow(view);
				string roomId = room.roomId;
				row.Join.Pressed += () => JoinRoom(roomId);
				_rows.Add(key, row);
			}
			row.Name.Text = room.name;
			row.Name.TooltipText = $"{room.name}\n房主：{room.hostName}";
			row.Occupancy.Text = $"{room.playerCount}/{room.maxPlayers}人";
			row.Phase.Text = PhaseText(room.phase);
			row.CanJoin = room.phase == "waiting" && room.playerCount < room.maxPlayers;
			row.JoinText.Text = room.phase != "waiting" ? "已开始" : row.CanJoin ? "加入" : "已满";
		}
		foreach (string key in remaining)
		{
			_rows[key].View.QueueFree();
			_roomList.RemoveChild(_rows[key].View);
			_rows.Remove(key);
		}
		_roomCount.Text = $"{_rows.Count}个房间在线";
		_emptyRooms.Visible = _rows.Count == 0;
		RefreshActions();
	}

	private void RefreshActions()
	{
		bool available = _adapter?.IsConnected == true && !_requestPending && !_transitioning && !_savingName;
		bool enabled = available && !_editingName;
		_playerName.Visible = !_editingName;
		_editNameButton.Visible = !_editingName;
		_editNameButton.Disabled = !available;
		_playerNameInput.Visible = _editingName;
		_playerNameInput.Editable = available;
		_saveNameButton.Visible = _editingName;
		_saveNameButton.Disabled = !available;
		_cancelNameButton.Visible = _editingName;
		_cancelNameButton.Disabled = _savingName || _transitioning;
		_createButton.Disabled = !enabled;
		_roomName.Editable = enabled;
		foreach (RoomRow row in _rows.Values)
		{
			row.Join.Disabled = !enabled || !row.CanJoin;
			row.JoinArt.Modulate = row.Join.Disabled ? new Color(1, 1, 1, 0.5f) : Colors.White;
		}
		GetNode<Control>("%CreateButtonArt").Modulate = enabled ? Colors.White : new Color(1, 1, 1, 0.55f);
	}

	private void BeginNameEdit()
	{
		if (_editNameButton.Disabled) return;
		_editingName = true;
		_playerNameInput.Text = _adapter.Profile.Name;
		RefreshActions();
		_playerNameInput.GrabFocus();
		_playerNameInput.SelectAll();
	}

	private void CancelNameEdit()
	{
		if (_savingName) return;
		_editingName = false;
		_status.Text = _adapter.IsConnected ? "大厅已连接" : "大厅连接已断开，请返回连接页面重试";
		RefreshActions();
		_editNameButton.GrabFocus();
	}

	private async Task SaveNameAsync()
	{
		if (!_editingName || _saveNameButton.Disabled) return;
		string name = _playerNameInput.Text.Trim();
		if (name.Length == 0)
		{
			_status.Text = "请输入昵称";
			_playerNameInput.GrabFocus();
			return;
		}
		_savingName = true;
		_status.Text = "正在保存昵称…";
		RefreshActions();
		try
		{
			await _adapter.UpdatePlayerNameAsync(name);
			if (!IsInstanceValid(this) || !IsInsideTree() || _transitioning) return;
			_editingName = false;
			_status.Text = "昵称已保存";
		}
		catch (Exception exception)
		{
			if (!IsInstanceValid(this) || !IsInsideTree() || _transitioning) return;
			_status.Text = exception is TimeoutException
				? "保存昵称超时，请重试或重新连接确认"
				: $"昵称保存失败：{exception.Message}";
		}
		finally
		{
			_savingName = false;
			if (IsInstanceValid(this) && IsInsideTree() && !_transitioning) RefreshActions();
		}
	}

	private void CreateRoom()
	{
		if (_createButton.Disabled) return;
		string name = _roomName.Text.Trim();
		if (name.Length == 0)
		{
			_status.Text = "请输入房间名";
			_roomName.GrabFocus();
			return;
		}
		_ = RequestRoomAsync(() => _adapter.CreateRoomAsync(name), "正在创建房间…");
	}

	private void JoinRoom(string roomId)
	{
		if (_adapter?.IsConnected != true || _requestPending || _transitioning || _editingName || _savingName) return;
		_ = RequestRoomAsync(() => _adapter.JoinRoomAsync(roomId), "正在加入房间…");
	}

	private async Task RequestRoomAsync(Func<Task> send, string message)
	{
		_requestPending = true;
		int version = ++_requestVersion;
		CancellationToken cancellation = _lifetime.Token;
		_status.Text = message;
		RefreshActions();
		try
		{
			await send();
			await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
			if (!IsInsideTree() || _transitioning || !_requestPending || version != _requestVersion) return;
			// Drop the connection before allowing another request: a late reservation
			// cannot otherwise be distinguished from the user's next attempt.
			await _adapter.DisconnectAsync();
			HandleError("房间请求超时，请返回连接页面重试");
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
		catch (Exception exception)
		{
			HandleError(exception.Message);
		}
	}

	private void HandleError(string message)
	{
		if (!IsInstanceValid(this) || !IsInsideTree() || _transitioning) return;
		_requestPending = false;
		_requestVersion++;
		_status.Text = $"操作失败：{message}";
		RefreshActions();
	}

	private void HandleLeft(int code)
	{
		if (!IsInsideTree() || _transitioning) return;
		_requestPending = false;
		_requestVersion++;
		_status.Text = "大厅连接已断开，请返回连接页面重试";
		RefreshActions();
	}

	private void HandleReservation(RoomReservation reservation)
	{
		if (!IsInsideTree() || _transitioning) return;
		_transitioning = true;
		GameSession.PendingReservation = reservation;
		_status.Text = "已锁定席位，正在进入准备房间…";
		_backButton.Disabled = true;
		RefreshActions();
		_ = ChangeSceneAsync("res://scenes/in_game/Table.tscn");
	}

	private async Task ReturnToConnectionAsync()
	{
		if (_transitioning) return;
		_transitioning = true;
		_backButton.Disabled = true;
		RefreshActions();
		await ChangeSceneAsync("res://scenes/Lobby.tscn");
	}

	private async Task ChangeSceneAsync(string path)
	{
		await _adapter.DisconnectAsync();
		if (!IsInstanceValid(this) || !IsInsideTree()) return;
		Error error = SceneNavigation.Change(this, path);
		if (error == Error.Ok) return;
		GameSession.PendingReservation = null;
		_transitioning = false;
		_backButton.Disabled = false;
		HandleError($"无法切换场景：{error}，请返回连接页面重试");
	}

	private static T Find<T>(Node parent, string name) where T : Node => (T)parent.FindChild(name, true, false);

	private static string PhaseText(string phase) => phase switch
	{
		"playing" => "游戏中",
		"table_ready" => "进入牌桌",
		"dealing" => "发牌中",
		"passing" => "传牌中",
		"finished" => "已结束",
		_ => "等待中",
	};

	private sealed class RoomRow
	{
		public readonly Control View;
		public readonly Label Name;
		public readonly Label Occupancy;
		public readonly Label Phase;
		public readonly Label JoinText;
		public readonly Button Join;
		public readonly Control JoinArt;
		public bool CanJoin;

		public RoomRow(Control view)
		{
			View = view;
			Name = Find<Label>(view, "RoomName");
			Occupancy = Find<Label>(view, "Occupancy");
			Phase = Find<Label>(view, "Phase");
			JoinText = Find<Label>(view, "JoinText");
			Join = Find<Button>(view, "JoinButton");
			JoinArt = Find<Control>(view, "JoinArt");
		}
	}
}
