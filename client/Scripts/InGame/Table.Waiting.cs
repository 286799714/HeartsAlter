using System;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.Generated;

namespace HeartsAlter.Scripts.InGame;

public partial class Table
{
	private CanvasLayer _waitingLayer;
	private CanvasLayer _inGameLayer;
	private Control _startAction;
	private Control _readyAction;
	private Control _cancelReadyAction;
	private Control _occupancyHint;
	private Label _occupancyText;
	private Label _waitingStatus;
	private Button _startGameButton;
	private CheckButton _heartsRule;
	private CheckButton _discardPointsRule;
	private readonly string[] _waitingPlayerIds = new string[PlayerCount];
	private string _lastNetworkPhase;

	private void BindWaitingUi()
	{
		_waitingLayer = GetNode<CanvasLayer>("Waiting");
		_inGameLayer = GetNode<CanvasLayer>("InGame");
		// Imported artwork is decorative; explicit buttons own its hit targets.
		IgnoreWaitingMouseInput(_waitingLayer);
		_startAction = _waitingLayer.GetNode<Control>("开始游戏按钮(仅房主)");
		_readyAction = _waitingLayer.GetNode<Control>("准备按钮(仅房客)");
		_cancelReadyAction = _waitingLayer.GetNode<Control>("取消准备按钮(仅房客)");
		_occupancyHint = _waitingLayer.GetNode<Control>("人数不足提示(仅房主)");
		_occupancyText = _occupancyHint.FindChild("需满4人才可开始游戏，目前1_4  xIDx81_8492x", true, false) as Label;
		_startGameButton = BindWaitingAction(_startAction, "StartGameButton", "开始游戏", () =>
			_ = SendWaitingActionAsync(() => _networkAdapter.StartGameAsync(), hostOnly: true));
		BindWaitingAction(_readyAction, "ReadyButton", "准备", () =>
			_ = SendWaitingActionAsync(() => _networkAdapter.SetReadyAsync(true)));
		BindWaitingAction(_cancelReadyAction, "CancelReadyButton", "取消准备", () =>
			_ = SendWaitingActionAsync(() => _networkAdapter.SetReadyAsync(false)));

		_heartsRule = _waitingLayer.GetNode<CheckButton>("WaitingOptions/HeartsBreakingToggle");
		_discardPointsRule = _waitingLayer.GetNode<CheckButton>("WaitingOptions/PointDiscardToggle");
		_heartsRule.Toggled += enabled => _ = SendWaitingActionAsync(
			() => _networkAdapter.SetRulesAsync(heartsBreakingEnabled: enabled), hostOnly: true);
		_discardPointsRule.Toggled += enabled => _ = SendWaitingActionAsync(
			() => _networkAdapter.SetRulesAsync(mustDiscardPointsWhenVoid: enabled), hostOnly: true);
		_waitingStatus = new Label { Name = "WaitingStatus", Text = "正在连接…", MouseFilter = MouseFilterEnum.Ignore,
			HorizontalAlignment = HorizontalAlignment.Center };
		_waitingLayer.AddChild(_waitingStatus);
		_waitingStatus.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		_waitingStatus.OffsetTop = -42;
		_waitingStatus.OffsetBottom = -12;
		for (int index = 0; index < PlayerCount; index++)
		{
			int displaySeat = index;
			PlayerInfo info = GetPlayerInfo(index);
			info.AddBotRequested += () => _ = SendWaitingActionAsync(() =>
				_networkAdapter.AddBotAsync((GetLocalSeat(_networkAdapter.State) + displaySeat) % PlayerCount), hostOnly: true);
			info.KickPlayerRequested += () =>
			{
				string playerId = _waitingPlayerIds[displaySeat];
				if (!string.IsNullOrEmpty(playerId))
					_ = SendWaitingActionAsync(() => _networkAdapter.KickPlayerAsync(playerId), hostOnly: true);
			};
		}
		if (UseNetworkSession) UpdateWaitingUi(null);
	}

	private static void IgnoreWaitingMouseInput(Node node)
	{
		if (node is Control control && node is not BaseButton) control.MouseFilter = MouseFilterEnum.Ignore;
		foreach (Node child in node.GetChildren()) IgnoreWaitingMouseInput(child);
	}

	private static Button BindWaitingAction(Control artwork, string name, string tooltip, Action pressed)
	{
		var button = new Button { Name = name, Flat = true, TooltipText = tooltip, MouseDefaultCursorShape = CursorShape.PointingHand };
		// A plain Control child avoids ScrollContainer's layout shrinking the hit area.
		artwork.GetNode<Control>("InnerContainer").AddChild(button);
		button.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		button.CustomMinimumSize = new Vector2(476, 108);
		foreach (string style in new[] { "normal", "hover", "pressed", "disabled" })
			button.AddThemeStyleboxOverride(style, new StyleBoxEmpty());
		button.Pressed += pressed;
		return button;
	}

	private void SetWaitingVisible(bool waiting)
	{
		_waitingLayer.Visible = waiting;
		_inGameLayer.Visible = !waiting;
		_animationLayer.Visible = !waiting;
	}

	private void UpdateWaitingUi(MyRoomState state)
	{
		bool waiting = state is null || state.phase == "waiting";
		SetWaitingVisible(waiting);
		Player local = state?.players.TryGetValue(_networkAdapter.SessionId, out Player current) == true ? current : null;
		bool host = local?.isHost == true;
		bool canManage = waiting && host && !_returningToLobby;
		bool allReady = state?.players.Count == PlayerCount;
		var players = new Player[PlayerCount];
		Array.Clear(_waitingPlayerIds);
		if (state is not null)
		{
			foreach (string id in state.players.Keys)
			{
				Player player = state.players[id];
				int displaySeat = (player.seat - GetLocalSeat(state) + PlayerCount) % PlayerCount;
				players[displaySeat] = player;
				_waitingPlayerIds[displaySeat] = id;
				allReady &= player.ready && (player.isBot || player.connected);
			}
			_waitingStatus.Text = state.message;
			_heartsRule.SetPressedNoSignal(state.heartsBreakingEnabled);
			_discardPointsRule.SetPressedNoSignal(state.mustDiscardPointsWhenVoid);
		}
		for (int index = 0; index < PlayerCount; index++)
		{
			PlayerInfo info = GetPlayerInfo(index);
			Player player = players[index];
			if (player is not null) UpdateSeatIdentity(info, player);
			else
			{
				info.SetPlayerInfo("", 0, 0);
				info.IsHost = info.IsBot = false;
			}
			info.SetSeatState(player is not null, waiting, player?.ready == true, canManage);
		}
		_heartsRule.Disabled = _discardPointsRule.Disabled = !canManage;
		_startAction.Visible = host;
		_startGameButton.Disabled = !canManage || !allReady;
		// The imported shader matches vertex RGB to replace its placeholder fill.
		// Fade alpha only so disabling the action preserves the button artwork.
		_startAction.Modulate = allReady ? Colors.White : new Color(1, 1, 1, 0.55f);
		_readyAction.Visible = waiting && local is { isHost: false, isBot: false, ready: false };
		_cancelReadyAction.Visible = waiting && local is { isHost: false, isBot: false, ready: true };
		// The authored hint overlaps the button; place it just above the disabled action.
		_occupancyHint.Visible = host && !allReady;
		_occupancyHint.Position = new Vector2(_occupancyHint.Position.X, _startAction.Position.Y - 38);
		if (_occupancyText is not null)
			_occupancyText.Text = state?.players.Count < PlayerCount
				? $"需满4人才可开始游戏，目前 {state.players.Count}/4"
				: "等待所有玩家连接并准备";
		_startGameButton.TooltipText = allReady ? "开始游戏" : _occupancyText?.Text ?? "等待玩家准备";
	}

	private async Task ConnectWaitingRoomAsync(RoomReservation reservation)
	{
		string endpoint = string.IsNullOrWhiteSpace(GameSession.ServerEndpoint)
			? ColyseusClientAdapter.DefaultEndpoint : GameSession.ServerEndpoint;
		bool connected = await _networkAdapter.ConnectByReservationAsync(reservation, endpoint);
		if (!IsInsideTree() || _networkTransitioning || _returningToLobby)
		{
			await _networkAdapter.DisconnectAsync();
			return;
		}
		if (connected) HandleNetworkStateChanged(_networkAdapter.State, true);
		else HandleNetworkStatus("无法进入房间，请返回大厅重试");
	}

	private async Task SendWaitingActionAsync(Func<Task> action, bool hostOnly = false)
	{
		if (_networkTransitioning || _returningToLobby || _networkAdapter?.State is not MyRoomState state || state.phase != "waiting" ||
			!state.players.TryGetValue(_networkAdapter.SessionId, out Player local) || (hostOnly && !local.isHost)) return;
		try { await action(); }
		catch (Exception exception) { HandleNetworkStatus($"操作失败：{exception.Message}"); }
	}

	private void ResetNetworkPresentation()
	{
		CancelCollectTrick();
		_dealGeneration++;
		_dealFlights = 0;
		_dealDispatchCompleted = _dealFinishing = IsDealing = false;
		_networkPassGeneration++;
		_networkPassFlights = 0;
		_networkPassSubmitted = _networkPassExpected = _networkPassAnimationStarted = _networkPassAnimating = false;
		_networkPassingSelections.Clear();
		_networkPassingReceivedCards.Clear();
		_networkPassingRound = -1;
		_networkHandCards.Clear();
		_pendingNetworkPlays.Clear();
		_networkTableReadySent = _networkDealReadySent = _networkDealStarted = false;
		_networkSettlementScheduled = _networkFinalTrickReceived = _rightPlayerPlayCompleted = false;
		_networkScoreRound = ushort.MaxValue;
		_nextRoundPending = false;
		_animationLayer.CancelAnimation(freeCard: true);
		_mainHandLayout.SetPassSelectionEnabled(false);
		_mainHandLayout.ClearPlayableCards();
		_mainHandLayout.SetSelectionEnabled(false);
		SetMainPlayerPlayEnabled(false);
		ClearHandsAndPlayAreas();
		_cardDeck.ChangeCardCount(0);
		_settlement.Hide();
		for (int seat = 0; seat < PlayerCount; seat++)
		{
			GetPlayerInfo(seat).SetRoundScore(0);
		}
	}
}
