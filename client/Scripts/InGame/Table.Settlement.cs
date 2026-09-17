using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.Generated;

namespace HeartsAlter.Scripts.InGame;

public partial class Table
{
	private Settlement _settlement;
	private Label _roundStatus;
	private TextureButton _nextRoundButton;
	private TextureButton _lobbyButton;
	private BaseButton _exitButton;
	private ConfirmationDialog _exitConfirmation;
	private bool _returningToLobby;
	private bool _nextRoundPending;

	private void BindSettlementUi()
	{
		_settlement = GetNode<Settlement>("SettlementLayer/Settlement");
		_roundStatus = _settlement.GetNode<Label>("%RoundStatus");
		_nextRoundButton = _settlement.GetNode<TextureButton>("%NextRoundButton");
		_lobbyButton = _settlement.GetNode<TextureButton>("%ReturnToLobbyButton");
		_nextRoundButton.Pressed += () => _ = AgreeNextRoundAsync();
		_lobbyButton.Pressed += ConfirmExitRoom;
		UpdateSettlementButtons();
	}

	private void BindExitUi()
	{
		_exitButton = GetNode<BaseButton>("%ReturnToLobbyButton");
		_exitConfirmation = GetNode<ConfirmationDialog>("ExitConfirmation");
		_exitButton.Pressed += ConfirmExitRoom;
		_exitConfirmation.Confirmed += () => _ = ReturnToLobbyAsync();
		GetNode<CanvasLayer>("TableActions").Visible = UseNetworkSession;
	}

	private void ConfirmExitRoom()
	{
		if (!UseNetworkSession || _returningToLobby || _networkTransitioning || _exitConfirmation.Visible) return;
		UpdateExitConfirmation();
		_exitConfirmation.PopupCentered(new Vector2I(520, 180));
	}

	private void UpdateExitConfirmation()
	{
		MyRoomState state = _networkAdapter?.State;
		Player local = state?.players.TryGetValue(_networkAdapter.SessionId, out Player current) == true ? current : null;
		bool host = local?.isHost == true;
		int stake = local?.stake ?? 0;
		_exitConfirmation.DialogText = state is null ? "确定退出并返回大厅吗？"
			: state.phase == "waiting" ? host
				? "确定退出房间并返回大厅吗？房主将转交给其他玩家。"
				: "确定退出房间并返回大厅吗？"
			: host ? state.phase != "finished" && stake > 0
				? $"退出后将解散房间，各席位拿回本局投入的 {stake} 金币，所有玩家返回大厅。确定退出吗？"
				: "你是房主，退出后将解散房间，所有玩家都会返回大厅。确定退出吗？"
				: state.phase == "finished" ? "确定退出并返回大厅吗？机器人将接替你的席位。"
				: stake > 0 ? $"退出将放弃本局投入的 {stake} 金币，由机器人接替。无需等待结算，可立即加入其他房间。确定退出吗？"
				: "退出后由机器人接替，你尚未投入底注，可立即加入其他房间。确定退出吗？";
		_exitConfirmation.OkButtonText = host && state?.phase != "waiting" ? "解散并退出" : "确认退出";
	}

	private void UpdateSettlementUi(MyRoomState state)
	{
		_settlement.Render(state, _networkAdapter.SessionId);
		UpdateSettlementButtons();
		var humans = state.players.Keys.Cast<string>().Select(id => state.players[id]).Where(player => !player.isBot).ToList();
		_roundStatus.Text = $"{state.message}  ·  下一局 {humans.Count(player => player.nextRoundReady)}/{humans.Count} 人已同意";
		foreach (Player player in state.players.Keys.Cast<string>().Select(id => state.players[id]))
		{
			int seat = (player.seat - GetLocalSeat(state) + PlayerCount) % PlayerCount;
			PlayerInfo info = GetPlayerInfo(seat);
			UpdateSeatIdentity(info, player);
			if (_settlement.Visible && !IsCollectingTrick) info?.SetRoundScore(player.score);
		}
	}

	private void UpdateSettlementButtons()
	{
		MyRoomState state = _networkAdapter?.State;
		Player local = state?.players.TryGetValue(_networkAdapter.SessionId, out Player current) == true ? current : null;
		_nextRoundButton.Disabled = state?.phase != "finished" || local is null || local.nextRoundReady || _nextRoundPending || _returningToLobby;
		_nextRoundButton.TooltipText = local?.nextRoundReady == true ? "已同意下一局，等待其他玩家"
			: _nextRoundPending ? "正在同意下一局…" : "下一局";
		_lobbyButton.Disabled = _networkAdapter is null || _returningToLobby;
		_lobbyButton.TooltipText = local?.isHost == true ? "房主退出将解散房间并返回大厅" : "退出到大厅";
		_nextRoundButton.SelfModulate = _nextRoundButton.Disabled ? new Color(0.55f, 0.55f, 0.55f) : Colors.White;
		_lobbyButton.SelfModulate = _lobbyButton.Disabled ? new Color(0.55f, 0.55f, 0.55f) : Colors.White;
	}

	private async Task AgreeNextRoundAsync()
	{
		if (_networkAdapter?.State?.phase != "finished" || _nextRoundButton.Disabled) return;
		_nextRoundPending = true;
		UpdateSettlementButtons();
		try { await _networkAdapter.NextRoundAsync(); }
		catch (Exception exception) { HandleNetworkStatus($"无法同意下一局：{exception.Message}"); }
		finally
		{
			_nextRoundPending = false;
			if (IsInsideTree() && !_networkTransitioning && _networkAdapter?.State?.phase == "finished")
				UpdateSettlementButtons();
		}
	}

	private async Task ReturnToLobbyAsync()
	{
		if (_returningToLobby || _networkTransitioning) return;
		_returningToLobby = true;
		_exitButton.Disabled = true;
		UpdateSettlementButtons();
		HandleNetworkStatus("正在离开房间…");
		try
		{
			if (_networkAdapter?.State is MyRoomState state && state.phase != "waiting" &&
				state.players.TryGetValue(_networkAdapter.SessionId, out Player local) && local.isHost)
			{
				HandleNetworkStatus("正在解散房间…");
				await _networkAdapter.DisbandRoomAsync();
			}
			if (_networkAdapter is not null) await _networkAdapter.DisconnectAsync();
			TransitionToLobby();
		}
		catch (Exception exception)
		{
			if (!IsInsideTree() || _networkTransitioning) return;
			_returningToLobby = false;
			_exitButton.Disabled = false;
			UpdateSettlementButtons();
			HandleNetworkStatus($"无法退出房间：{exception.Message}");
		}
	}

	private void HandleNetworkStatus(string message)
	{
		if (!IsInsideTree() || _networkTransitioning) return;
		_roundStatus.Text = message;
		if (_waitingStatus is not null) _waitingStatus.Text = message;
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
