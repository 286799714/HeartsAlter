using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.Generated;

namespace HeartsAlter.Scripts;

/// <summary>Four-seat ready room. The server owns ready/host invariants.</summary>
public partial class ReadyRoom : Control
{
	[Export] public string Endpoint = ColyseusClientAdapter.DefaultEndpoint;
	private ColyseusClientAdapter _adapter;
	private VBoxContainer _players;
	private Label _status;
	private Button _readyButton;
	private Button _botButton;
	private Button _startButton;
	private CheckButton _heartsRule;
	private CheckButton _discardPointsRule;
	private bool _transitioning;
	private string _lastError = string.Empty;

	public override void _Ready()
	{
		BuildUi();
		_adapter = GameSession.GameAdapter is { IsConnected: true }
			? GameSession.GameAdapter : new ColyseusClientAdapter();
		_adapter.StateChanged += HandleStateChanged;
		_adapter.ServerMessage += HandleServerMessage;
		_adapter.InvalidPlay += HandleServerMessage;
		_adapter.Error += HandleError;
		var reservation = GameSession.PendingReservation;
		GameSession.PendingReservation = null;
		if (GameSession.GameAdapter is not null && GameSession.GameAdapter.IsConnected)
		{
			HandleStateChanged(_adapter.State, true);
			return;
		}
		if (reservation == null)
		{
			_status.Text = "没有可用的房间席位";
			return;
		}
		GameSession.GameAdapter = _adapter;
		_ = ConnectAsync(reservation);
	}

	public override void _ExitTree()
	{
		if (_adapter != null)
		{
			_adapter.StateChanged -= HandleStateChanged;
			_adapter.ServerMessage -= HandleServerMessage;
			_adapter.InvalidPlay -= HandleServerMessage;
			_adapter.Error -= HandleError;
			if (!_transitioning)
				_ = _adapter.DisconnectAsync();
		}
	}

	private void HandleServerMessage(string message)
	{
		if (IsInsideTree()) _status.Text = message;
	}

	private void HandleError(int code, string message)
	{
		_lastError = message ?? string.Empty;
		if (IsInsideTree()) _status.Text = $"错误：{_lastError}";
	}

	private async Task ConnectAsync(RoomReservation reservation)
	{
		string endpoint = string.IsNullOrWhiteSpace(GameSession.ServerEndpoint)
			? Endpoint
			: GameSession.ServerEndpoint;
		if (!await _adapter.ConnectByReservationAsync(reservation, endpoint))
		{
			if (IsInsideTree())
				_status.Text = string.IsNullOrWhiteSpace(_lastError)
					? "无法进入房间，席位可能已被占用"
					: $"无法进入房间：{_lastError}";
			return;
		}
		if (IsInsideTree())
			HandleStateChanged(_adapter.State, true);
	}

	private void HandleStateChanged(MyRoomState state, bool first)
	{
		if (!IsInsideTree() || state == null) return;
		_status.Text = state.message;
		foreach (Node child in _players.GetChildren()) child.QueueFree();
		Player local = state.players.TryGetValue(_adapter.SessionId, out var current) ? current : null;
		bool isHost = local?.isHost == true;
		_heartsRule.SetPressedNoSignal(state.heartsBreakingEnabled);
		_discardPointsRule.SetPressedNoSignal(state.mustDiscardPointsWhenVoid);
		_heartsRule.Disabled = _discardPointsRule.Disabled = !isHost || state.phase != "waiting";
		bool allReady = state.players.Count == 4;
		foreach (string playerId in state.players.Keys)
		{
			Player player = state.players[playerId];
			allReady &= player.ready;
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 12);
			string tags = player.isHost ? "房主" : player.isBot ? "机器人" : "玩家";
			row.AddChild(new Label
			{
				Text = $"座位 {player.seat + 1}  {player.name}  [{tags}]",
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			});
			row.AddChild(new Label { Text = player.ready ? "已准备" : "未准备" });
			_players.AddChild(row);
		}
		_readyButton.Disabled = local == null || local.isHost || local.isBot;
		_readyButton.Text = local?.ready == true ? "取消准备" : "准备";
		_botButton.Visible = isHost;
		_botButton.Disabled = state.players.Count >= 4 || state.phase != "waiting";
		_startButton.Visible = isHost;
		_startButton.Disabled = !isHost || !allReady || state.phase != "waiting";
		if (state.phase is "table_ready" or "dealing" or "passing" or "playing")
			TransitionToTable();
	}

	private void ToggleReady()
	{
		Player local = _adapter.State?.players.TryGetValue(_adapter.SessionId, out var current) == true ? current : null;
		_ = _adapter.SetReadyAsync(local?.ready != true);
	}

	private void GoBack()
	{
		_ = LeaveToLobbyAsync();
	}

	private async Task LeaveToLobbyAsync()
	{
		if (_transitioning) return;
		_transitioning = true;
		await _adapter.DisconnectAsync();
		GameSession.GameAdapter = null;
		if (IsInsideTree()) SceneNavigation.Change(this, "res://scenes/Intro.tscn");
	}

	private void TransitionToTable()
	{
		if (_transitioning) return;
		_transitioning = true;
		GameSession.GameAdapter = _adapter;
		SceneNavigation.Change(this, "res://scenes/in_game/Table.tscn");
	}

	private void BuildUi()
	{
		var margin = new MarginContainer();
		margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		margin.AddThemeConstantOverride("margin_left", 140);
		margin.AddThemeConstantOverride("margin_right", 140);
		margin.AddThemeConstantOverride("margin_top", 70);
		margin.AddThemeConstantOverride("margin_bottom", 70);
		AddChild(margin);
		var column = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		column.AddThemeConstantOverride("separation", 16);
		margin.AddChild(column);
		var title = new Label { Text = "准备房间" };
		title.AddThemeFontSizeOverride("font_size", 30);
		column.AddChild(title);
		_status = new Label { Text = "正在连接…" };
		column.AddChild(_status);
		_players = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		_players.AddThemeConstantOverride("separation", 10);
		column.AddChild(_players);
		var rules = new VBoxContainer();
		rules.AddThemeConstantOverride("separation", 6);
		column.AddChild(rules);
		rules.AddChild(new Label { Text = "房间规则（仅房主可在开局前修改）" });
		_heartsRule = new CheckButton
		{
			Name = "HeartsBreakingToggle", Text = "启用碎心规则", ButtonPressed = true, Disabled = true,
			TooltipText = "开启：红心未破时不能领出红桃，除非手中只剩红桃。关闭：解除此限制。首墩避免领出分牌的限制仍适用。",
		};
		_heartsRule.Toggled += enabled => _ = _adapter.SetRulesAsync(heartsBreakingEnabled: enabled);
		rules.AddChild(_heartsRule);
		_discardPointsRule = new CheckButton
		{
			Name = "PointDiscardToggle", Text = "缺门时必须优先垫得分牌", ButtonPressed = true, Disabled = true,
			TooltipText = "开启：缺门时有红桃或黑桃 Q 必须先出分牌，首墩也适用。关闭：缺门时可出任意手牌。",
		};
		_discardPointsRule.Toggled += enabled => _ = _adapter.SetRulesAsync(mustDiscardPointsWhenVoid: enabled);
		rules.AddChild(_discardPointsRule);
		rules.AddChild(new Label { Text = "修改规则后，其他玩家需要重新准备。" });
		var actions = new HBoxContainer();
		_readyButton = new Button { Text = "准备" };
		_readyButton.Pressed += ToggleReady;
		actions.AddChild(_readyButton);
		_botButton = new Button { Text = "添加机器人" };
		_botButton.Pressed += () => _ = _adapter.AddBotAsync();
		actions.AddChild(_botButton);
		_startButton = new Button { Text = "开始游戏" };
		_startButton.Pressed += () => _ = _adapter.StartGameAsync();
		actions.AddChild(_startButton);
		var back = new Button { Text = "返回大厅" };
		back.Pressed += GoBack;
		actions.AddChild(back);
		column.AddChild(actions);
	}
}
