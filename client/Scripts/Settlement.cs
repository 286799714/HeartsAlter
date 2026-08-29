using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.Generated;

namespace HeartsAlter.Scripts;

/// <summary>Displays the authoritative scores/chip changes and coordinates the next round.</summary>
public partial class Settlement : Control
{
	private ColyseusClientAdapter _adapter;
	private VBoxContainer _rows;
	private Label _status;
	private Button _nextButton;
	private Button _backButton;
	private bool _transitioning;
	private bool _returningToLobby;

	public override void _Ready()
	{
		BuildUi();
		_adapter = GameSession.GameAdapter;
		if (_adapter is null)
		{
			_status.Text = "没有可用的结算数据";
			return;
		}
		_adapter.StateChanged += HandleStateChanged;
		_adapter.ServerMessage += HandleServerMessage;
		_adapter.InvalidPlay += HandleServerMessage;
		_adapter.Left += HandleRoomLeft;
		HandleStateChanged(_adapter.State, true);
	}

	public override void _ExitTree()
	{
		if (_adapter is null) return;
		_adapter.StateChanged -= HandleStateChanged;
		_adapter.ServerMessage -= HandleServerMessage;
		_adapter.InvalidPlay -= HandleServerMessage;
		_adapter.Left -= HandleRoomLeft;
		if (!_transitioning) _ = _adapter.DisconnectAsync();
	}

	private void HandleStateChanged(MyRoomState state, bool first)
	{
		if (!IsInsideTree() || state is null) return;
		if (state.phase is "table_ready" or "dealing" or "playing")
		{
			Transition("res://scenes/in_game/Table.tscn");
			return;
		}
		if (state.phase == "waiting")
		{
			Transition("res://scenes/ReadyRoom.tscn");
			return;
		}
		_status.Text = state.message;
		foreach (Node child in _rows.GetChildren()) child.QueueFree();
		Player local = state.players.TryGetValue(_adapter.SessionId, out var current) ? current : null;
		_nextButton.Disabled = local is null || local.nextRoundReady;
		_nextButton.Text = local?.nextRoundReady == true ? "已同意下一局" : "下一局";
		_backButton.Text = local?.isHost == true ? "解散房间并返回大厅" : "返回大厅";
		var orderedPlayers = new List<Player>();
		foreach (string playerId in state.players.Keys)
			orderedPlayers.Add(state.players[playerId]);
		orderedPlayers.Sort((left, right) =>
		{
			int score = right.score.CompareTo(left.score);
			return score != 0 ? score : left.seat.CompareTo(right.seat);
		});
		for (int index = 0; index < orderedPlayers.Count; index++)
		{
			Player player = orderedPlayers[index];
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 18);
			row.AddChild(new Label
			{
				Text = $"#{index + 1}  {player.name}  {(player.isHost ? "房主" : player.isBot ? "机器人" : "")}",
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			});
			row.AddChild(new Label { Text = $"点数 {player.score}" });
			row.AddChild(new Label { Text = $"筹码 {player.chips}" });
			int chipChange = player.payout - player.stake;
			row.AddChild(new Label { Text = chipChange >= 0 ? $"筹码变化 +{chipChange}" : $"筹码变化 {chipChange}" });
			row.AddChild(new Label { Text = player.nextRoundReady ? "已同意" : "等待同意" });
			_rows.AddChild(row);
		}
	}

	private void HandleServerMessage(string message) => _status.Text = message;

	private void AgreeNextRound() => _ = _adapter?.NextRoundAsync();

	private void HandleRoomLeft(int code)
	{
		TransitionToLobby();
	}

	private async Task ReturnToLobbyAsync()
	{
		if (_returningToLobby || _adapter is null) return;
		_returningToLobby = true;
		_backButton.Disabled = true;
		Player local = _adapter.State?.players.TryGetValue(_adapter.SessionId, out var current) == true ? current : null;
		if (local?.isHost == true)
		{
			_status.Text = "正在解散房间…";
			await _adapter.DisbandRoomAsync();
		}
		else
		{
			_status.Text = "正在离开房间…";
		}
		await _adapter.DisconnectAsync();
		TransitionToLobby();
	}

	private void TransitionToLobby()
	{
		if (_transitioning || !IsInsideTree()) return;
		_transitioning = true;
		GameSession.GameAdapter = null;
		GetTree().ChangeSceneToFile("res://scenes/Lobby.tscn");
	}

	private void Transition(string scene)
	{
		if (_transitioning) return;
		_transitioning = true;
		GameSession.GameAdapter = _adapter;
		GetTree().ChangeSceneToFile(scene);
	}

	private void BuildUi()
	{
		var margin = new MarginContainer();
		margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		margin.AddThemeConstantOverride("margin_left", 120);
		margin.AddThemeConstantOverride("margin_right", 120);
		margin.AddThemeConstantOverride("margin_top", 70);
		margin.AddThemeConstantOverride("margin_bottom", 70);
		AddChild(margin);
		var column = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		column.AddThemeConstantOverride("separation", 16);
		margin.AddChild(column);
		var title = new Label { Text = "本局结算" };
		title.AddThemeFontSizeOverride("font_size", 30);
		column.AddChild(title);
		_status = new Label { Text = "正在同步结算…" };
		column.AddChild(_status);
		_rows = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		_rows.AddThemeConstantOverride("separation", 10);
		column.AddChild(_rows);
		_nextButton = new Button { Text = "下一局" };
		_nextButton.Pressed += AgreeNextRound;
		var actions = new HBoxContainer();
		actions.AddChild(_nextButton);
		_backButton = new Button { Text = "返回大厅" };
		_backButton.Pressed += () => _ = ReturnToLobbyAsync();
		actions.AddChild(_backButton);
		column.AddChild(actions);
	}
}
