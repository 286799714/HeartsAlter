using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.Generated;

namespace HeartsAlter.Scripts.InGame;

public partial class Table
{
	private Settlement _settlement;
	private Control _roundActions;
	private Label _roundStatus;
	private Button _nextRoundButton;
	private Button _lobbyButton;
	private Button _viewSettlementButton;
	private bool _returningToLobby;
	private bool _nextRoundPending;

	private void BindSettlementUi()
	{
		_settlement = GetNode<Settlement>("SettlementLayer/Settlement");
		_roundActions = GetNode<Control>("RoundActions");
		_roundStatus = GetNode<Label>("RoundActions/Status");
		_nextRoundButton = GetNode<Button>("RoundActions/Buttons/NextRound");
		_lobbyButton = GetNode<Button>("RoundActions/Buttons/ReturnToLobby");
		_viewSettlementButton = GetNode<Button>("RoundActions/Buttons/ViewSettlement");
		_nextRoundButton.Pressed += () => _ = AgreeNextRoundAsync();
		_lobbyButton.Pressed += () => _ = ReturnToLobbyAsync();
		_viewSettlementButton.Pressed += _settlement.Open;
		_settlement.VisibilityChanged += () =>
		{
			var focus = _settlement.Visible ? FocusModeEnum.None : FocusModeEnum.All;
			_nextRoundButton.FocusMode = _lobbyButton.FocusMode = _viewSettlementButton.FocusMode = focus;
		};
		_settlement.Closed += () =>
		{
			if (!_nextRoundButton.Disabled) _nextRoundButton.GrabFocus();
			else _viewSettlementButton.GrabFocus();
		};
	}

	private void UpdateSettlementUi(MyRoomState state)
	{
		_settlement.Render(state, _networkAdapter.SessionId);
		Player local = state.players.TryGetValue(_networkAdapter.SessionId, out Player current) ? current : null;
		_nextRoundButton.Disabled = local is null || local.nextRoundReady || _nextRoundPending || _returningToLobby;
		_nextRoundButton.Text = local?.nextRoundReady == true ? "已同意下一局" : "下一局";
		_lobbyButton.TooltipText = local?.isHost == true ? "房主退出将解散房间并返回大厅" : "离开房间并返回大厅";
		var humans = state.players.Keys.Cast<string>().Select(id => state.players[id]).Where(player => !player.isBot).ToList();
		_roundStatus.Text = $"{state.message}  ·  下一局 {humans.Count(player => player.nextRoundReady)}/{humans.Count} 人已同意";
		foreach (Player player in state.players.Keys.Cast<string>().Select(id => state.players[id]))
		{
			int seat = (player.seat - GetLocalSeat(state) + PlayerCount) % PlayerCount;
			PlayerInfo info = GetPlayerInfo(seat);
			UpdateSeatIdentity(info, player);
			if (_roundActions.Visible && !IsCollectingTrick) info?.SetRoundScore(player.score);
		}
	}

	private async Task AgreeNextRoundAsync()
	{
		if (_networkAdapter?.State?.phase != "finished" || _nextRoundButton.Disabled) return;
		_nextRoundPending = true;
		_nextRoundButton.Disabled = true;
		try { await _networkAdapter.NextRoundAsync(); }
		catch (Exception exception) { HandleNetworkStatus($"无法同意下一局：{exception.Message}"); }
		finally
		{
			_nextRoundPending = false;
			if (IsInsideTree() && !_networkTransitioning && _networkAdapter?.State?.phase == "finished")
				_nextRoundButton.Disabled = _returningToLobby ||
					!_networkAdapter.State.players.TryGetValue(_networkAdapter.SessionId, out Player local) || local.nextRoundReady;
		}
	}

	private async Task ReturnToLobbyAsync()
	{
		if (_returningToLobby || _networkTransitioning || _networkAdapter is null) return;
		_returningToLobby = true;
		_lobbyButton.Disabled = _nextRoundButton.Disabled = true;
		try
		{
			if (_networkAdapter.State?.players.TryGetValue(_networkAdapter.SessionId, out Player local) == true && local.isHost)
			{
				_roundStatus.Text = "正在解散房间…";
				await _networkAdapter.DisbandRoomAsync();
			}
			await _networkAdapter.DisconnectAsync();
			TransitionToLobby();
		}
		catch (Exception exception)
		{
			if (!IsInsideTree() || _networkTransitioning) return;
			_returningToLobby = false;
			_lobbyButton.Disabled = false;
			UpdateSettlementUi(_networkAdapter.State);
			HandleNetworkStatus($"无法退出房间：{exception.Message}");
		}
	}

	private void HandleNetworkStatus(string message)
	{
		if (IsInsideTree() && !_networkTransitioning) _roundStatus.Text = message;
	}

	private void HandleNetworkError(int code, string message) => HandleNetworkStatus($"错误：{message}");
	private void HandleNetworkRoomLeft(int code) => TransitionToLobby();

	private void TransitionToLobby()
	{
		if (_networkTransitioning || !IsInsideTree()) return;
		_networkTransitioning = true;
		GameSession.GameAdapter = null;
		SceneNavigation.Change(this, "res://scenes/Intro.tscn");
	}
}
