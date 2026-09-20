using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.InGame.Card;
using HeartsAlter.Scripts.Generated;
using HeartsAlter.Scripts;

namespace HeartsAlter.Scripts.InGame;

/// <summary>
/// The presentation seam for a four-seat table. Callers describe deals and
/// plays by seat/card index; hand ownership, pose calculation, and animation
/// sequencing remain inside the table and its layout modules.
/// </summary>
public partial class Table : Control
{
	private readonly record struct CollectFlight(
		CardControl Card,
		CardPose2D SourcePose,
		CardPose2D TargetPose
	);

	private sealed record PassingSelection(
		IReadOnlyList<string> CardIds,
		IReadOnlyList<int> CardIndexes
	);

	private readonly record struct PassingFlight(
		CardControl Card,
		Control Destination,
		CardPose2D SourcePose,
		CardPose2D TargetPose
	);

	private readonly record struct NetworkPlay(
		string PlayerId,
		string CardId,
		int RoundNumber,
		int PlaySequence
	);

	private readonly record struct NetworkTrickResolution(
		string WinnerId,
		int Points,
		int TrickNumber,
		int RoundNumber,
		int PlaySequence
	);

	public const int MainPlayerIndex = 0;
	public const int LeftPlayerIndex = 1;
	public const int OppositePlayerIndex = 2;
	public const int RightPlayerIndex = 3;
	public const int PlayerCount = 4;

	/// <summary>Set before entering the tree to use this table for a local lesson.</summary>
	[Export]
	public bool UseNetworkSession = true;

	[Export]
	private CardDeck _cardDeck = null!;

	[Export]
	private MainHandLayout _mainHandLayout = null!;

	[Export]
	private OtherHandLayout _otherHandLayout = null!;

	[Export]
	private OtherHandLayout _otherHandLayout2 = null!;

	[Export]
	private OtherHandLayout _otherHandLayout3 = null!;

	[Export]
	private AnimationLayer _animationLayer = null!;

	[Export]
	private PlayArea _mainPlayArea = null!;

	[Export]
	private PlayArea _leftPlayArea = null!;

	[Export]
	private PlayArea _oppositePlayArea = null!;

	[Export]
	private PlayArea _rightPlayArea = null!;

	[Export]
	private PlayerInfo _mainPlayerInfo = null!;

	[Export]
	private PlayerInfo _nextPlayerInfo = null!;

	[Export]
	private PlayerInfo _oppositePlayerInfo = null!;

	[Export]
	private PlayerInfo _previousPlayerInfo = null!;

	/// <summary>Seconds between the starts of consecutive deal animations.</summary>
	[Export]
	public float DealInterval = 0.12f;

	/// <summary>Pause after the last dealt card lands before arranging the hand.</summary>
	[Export]
	public float ArrangeDelay = 0.2f;

	/// <summary>Pause after all four played cards land before collecting the trick.</summary>
	[Export]
	public float TrickCollectDelay = 0.3f;

	/// <summary>
	/// Raised when the main player clicks an already-selected hand card. The
	/// receiver can validate the request and call <see cref="PlayMainPlayerCard"/>.
	/// </summary>
	public event Action<int, CardData> MainPlayerCardPlayRequested;

	private readonly List<OtherHandLayout> _opponentHands = new();
	private readonly List<CollectFlight> _preparedCollectFlights = new(PlayerCount);
	private int _dealGeneration;
	private int _dealFlights;
	private bool _dealDispatchCompleted;
	private bool _dealFinishing;
	private int _collectGeneration;
	private int _collectFlights;
	private bool _collectDispatchCompleted;
	private PlayerInfo _collectingPlayerInfo = null!;
	private int _collectingPlayerIndex = -1;
	private int _pendingRoundScoreDelta;
	private bool _collectCardsPrepared;
	private bool _rightPlayerPlayCompleted;
	private ColyseusClientAdapter _networkAdapter;
	private bool _networkDealStarted;
	private readonly SortedDictionary<int, NetworkPlay> _pendingNetworkPlays = new();
	private readonly SortedDictionary<int, NetworkTrickResolution> _pendingNetworkTrickResolutions = new();
	private int _networkProgressRound = -1;
	private int _networkLastPlaySequence;
	private int _networkLastResolvedTrick;
	private bool _networkPlayAnimating;
	private readonly List<CardData> _networkHandCards = new();
	private bool _networkTableReadySent;
	private bool _networkDealReadySent;
	private bool _networkTransitioning;
	private bool _networkSettlementScheduled;
	private bool _networkFinalTrickReceived;
	private readonly Dictionary<string, PassingSelection> _networkPassingSelections = new();
	private readonly List<CardData> _networkPassingReceivedCards = new();
	private int _networkPassingRound = -1;
	private int _networkPassingDuration = 30_000;
	private int _networkPassGeneration;
	private int _networkPassFlights;
	private bool _networkPassSubmitted;
	private bool _networkPassExpected;
	private bool _networkPassAnimationStarted;
	private bool _networkPassAnimating;
	private const double NetworkPlayRequestIntervalMsec = 100.0;
	private double _lastNetworkPlaySentMsec = double.NegativeInfinity;
	private ushort _networkScoreRound = ushort.MaxValue;

	public int RemainingCards =>
		_cardDeck is not null && IsInstanceValid(_cardDeck)
			? _cardDeck.CardCount
			: 0;

	public bool IsDealing { get; private set; }
	public bool IsCollectingTrick { get; private set; }

	/// <summary>
	/// Whether a request to play the main player's selected card may be forwarded
	/// to the game controller. This is deliberately independent of hand
	/// selection: a player can select a card while waiting for the play gate to
	/// open, then submit it once play becomes available.
	/// </summary>
	public bool CanMainPlayerPlay { get; private set; }

	/// <summary>
	/// Initializes the public information shown for all four seats. Seat order is
	/// main player, next player (left), opposite player, previous player (right).
	/// Round scores are reset to zero for the new table state.
	/// </summary>
	public void InitializePlayerInfo(
		string mainPlayerId,
		Texture2D mainPlayerAvatar,
		int mainPlayerChipCount,
		string nextPlayerId,
		Texture2D nextPlayerAvatar,
		int nextPlayerChipCount,
		string oppositePlayerId,
		Texture2D oppositePlayerAvatar,
		int oppositePlayerChipCount,
		string previousPlayerId,
		Texture2D previousPlayerAvatar,
		int previousPlayerChipCount)
	{
		ResolvePlayerInfoReferences();
		InitializeSeatPlayerInfo(
			_mainPlayerInfo,
			mainPlayerId,
			mainPlayerAvatar,
			mainPlayerChipCount
		);
		InitializeSeatPlayerInfo(
			_nextPlayerInfo,
			nextPlayerId,
			nextPlayerAvatar,
			nextPlayerChipCount
		);
		InitializeSeatPlayerInfo(
			_oppositePlayerInfo,
			oppositePlayerId,
			oppositePlayerAvatar,
			oppositePlayerChipCount
		);
		InitializeSeatPlayerInfo(
			_previousPlayerInfo,
			previousPlayerId,
			previousPlayerAvatar,
			previousPlayerChipCount
		);
	}

	public override void _Ready()
	{
		ResolveSceneReferences();
		BindLayoutAnimations();
		BindInGameStatusUi();
		BindSettlementUi();
		BindWaitingUi();
		BindExitUi();
		SetMainPlayerPlayEnabled(false);

		if (_mainHandLayout is not null && IsInstanceValid(_mainHandLayout))
		{
			_mainHandLayout.CardPlayRequested += HandleMainPlayerCardPlayRequested;
			_mainHandLayout.PassCardsSubmitted += HandleMainPlayerPassCardsSubmitted;
		}

		if (_cardDeck is not null && IsInstanceValid(_cardDeck))
		{
			_cardDeck.ChangeMaxCardCount(52);
			_cardDeck.ChangeCardCount(0);
		}

		_networkAdapter = UseNetworkSession && GameSession.GameAdapter?.IsConnected == true ? GameSession.GameAdapter : null;
		RoomReservation reservation = UseNetworkSession ? GameSession.PendingReservation : null;
		if (UseNetworkSession) GameSession.PendingReservation = null;
		if (reservation is not null && _networkAdapter?.IsConnected != true)
			GameSession.GameAdapter = _networkAdapter = new ColyseusClientAdapter();
		SetWaitingVisible(UseNetworkSession && (_networkAdapter is null || reservation is not null || _networkAdapter.State?.phase == "waiting"));
		if (_networkAdapter is not null)
		{
			_networkAdapter.StateChanged += HandleNetworkStateChanged;
			_networkAdapter.HandReceived += HandleNetworkHandReceived;
			_networkAdapter.CardPlayed += HandleNetworkCardPlayed;
			_networkAdapter.TurnStarted += HandleNetworkTurnStarted;
			_networkAdapter.PassingStarted += HandleNetworkPassingStarted;
			_networkAdapter.PassingSelected += HandleNetworkPassingSelected;
			_networkAdapter.PassingReceived += HandleNetworkPassingReceived;
			_networkAdapter.PassingCompleted += HandleNetworkPassingCompleted;
			_networkAdapter.TrickResolved += HandleNetworkTrickResolved;
			_networkAdapter.RoundFinished += HandleNetworkRoundFinished;
			_networkAdapter.RoomReset += HandleNetworkRoomReset;
			_networkAdapter.ServerMessage += HandleNetworkStatus;
			_networkAdapter.InvalidPlay += HandleNetworkStatus;
			_networkAdapter.Error += HandleNetworkError;
			_networkAdapter.Left += HandleNetworkRoomLeft;
			MainPlayerCardPlayRequested += HandleNetworkCardPlayRequested;
			if (reservation is not null && !_networkAdapter.IsConnected)
				_ = ConnectWaitingRoomAsync(reservation);
			else
			{
				HandleNetworkStateChanged(_networkAdapter.State, true);
				_ = _networkAdapter.RequestHandAsync();
			}
		}
		else if (UseNetworkSession) HandleNetworkStatus("没有可用的房间席位，请返回大厅");
	}

	public override void _ExitTree()
	{
		CancelLocalPassing();
		_dealGeneration++;
		_collectGeneration++;
		IsDealing = false;
		IsCollectingTrick = false;
		CanMainPlayerPlay = false;

		if (_mainHandLayout is not null && IsInstanceValid(_mainHandLayout))
		{
			_mainHandLayout.CardPlayRequested -= HandleMainPlayerCardPlayRequested;
			_mainHandLayout.PassCardsSubmitted -= HandleMainPlayerPassCardsSubmitted;
		}

		_opponentHands.Clear();
		_pendingNetworkPlays.Clear();
		if (_networkAdapter is not null)
		{
			_networkAdapter.StateChanged -= HandleNetworkStateChanged;
			_networkAdapter.HandReceived -= HandleNetworkHandReceived;
			_networkAdapter.CardPlayed -= HandleNetworkCardPlayed;
			_networkAdapter.TurnStarted -= HandleNetworkTurnStarted;
			_networkAdapter.PassingStarted -= HandleNetworkPassingStarted;
			_networkAdapter.PassingSelected -= HandleNetworkPassingSelected;
			_networkAdapter.PassingReceived -= HandleNetworkPassingReceived;
			_networkAdapter.PassingCompleted -= HandleNetworkPassingCompleted;
			_networkAdapter.TrickResolved -= HandleNetworkTrickResolved;
			_networkAdapter.RoundFinished -= HandleNetworkRoundFinished;
			_networkAdapter.RoomReset -= HandleNetworkRoomReset;
			_networkAdapter.ServerMessage -= HandleNetworkStatus;
			_networkAdapter.InvalidPlay -= HandleNetworkStatus;
			_networkAdapter.Error -= HandleNetworkError;
			_networkAdapter.Left -= HandleNetworkRoomLeft;
			MainPlayerCardPlayRequested -= HandleNetworkCardPlayRequested;
			_networkHandCards.Clear();
			_networkPassingSelections.Clear();
			_networkPassingReceivedCards.Clear();
			_networkPassGeneration++;
			_lastNetworkPlaySentMsec = double.NegativeInfinity;
			if (!_networkTransitioning) _ = _networkAdapter.DisconnectAsync();
		}
	}

	private void HandleNetworkStateChanged(MyRoomState state, bool first)
	{
		if (state is null || _networkAdapter is null || _networkTransitioning || _returningToLobby) return;
		if (_exitConfirmation.Visible) UpdateExitConfirmation();
		UpdateWaitingUi(state);
		UpdateInGameStatus(state);
		ReconcileNetworkProgress(state);
		if (_networkSettlementScheduled && state.phase is ("table_ready" or "dealing" or "passing" or "playing"))
		{
			// A fresh table resets all deal/pass animation state and repeats the ready handshake.
			TransitionNetworkScene("res://scenes/in_game/Table.tscn");
			return;
		}
		if (state.phase == "waiting")
		{
			if (_lastNetworkPhase != "waiting") ResetNetworkPresentation();
			_lastNetworkPhase = state.phase;
			return;
		}
		_lastNetworkPhase = state.phase;
		if (state.phase == "finished")
		{
			_mainHandLayout?.ClearCountdown();
			SetMainPlayerPlayEnabled(false);
			_mainHandLayout?.SetSelectionEnabled(false);
			UpdateSettlementUi(state);
			RequestNetworkSettlement(!first);
			return;
		}
		var players = new Player[PlayerCount];
		foreach (string playerId in state.playerOrder.GetItems())
		{
			if (!state.players.TryGetValue(playerId, out Player player)) continue;
			int seat = (player.seat - GetLocalSeat(state) + PlayerCount) % PlayerCount;
			players[seat] = player;
		}
		bool resetScores = _networkScoreRound != state.roundNumber;
		if (resetScores)
		{
			_networkScoreRound = state.roundNumber;
			InitializePlayerInfo(
				players[0]?.name ?? "", null, players[0]?.chips ?? 0,
				players[1]?.name ?? "", null, players[1]?.chips ?? 0,
				players[2]?.name ?? "", null, players[2]?.chips ?? 0,
				players[3]?.name ?? "", null, players[3]?.chips ?? 0);
			_mainPlayerInfo?.SetRoundScore(players[0]?.score ?? 0);
			_nextPlayerInfo?.SetRoundScore(players[1]?.score ?? 0);
			_oppositePlayerInfo?.SetRoundScore(players[2]?.score ?? 0);
			_previousPlayerInfo?.SetRoundScore(players[3]?.score ?? 0);
		}
		UpdateSeatIdentity(_mainPlayerInfo, players[0]);
		UpdateSeatIdentity(_nextPlayerInfo, players[1]);
		UpdateSeatIdentity(_oppositePlayerInfo, players[2]);
		UpdateSeatIdentity(_previousPlayerInfo, players[3]);
		if (state.phase == "table_ready" && !_networkTableReadySent)
		{
			_networkTableReadySent = true;
			_ = _networkAdapter.TableReadyAsync();
		}
		if (state.phase == "passing")
		{
			_networkPassExpected = true;
			SetMainPlayerPlayEnabled(false);
			if (!IsDealing && !_networkPassAnimationStarted && !_networkPassSubmitted)
				_mainHandLayout?.SetPassSelectionEnabled(true);
			if (_networkPassSubmitted)
				_mainHandLayout?.ClearCountdown();
			else
				_mainHandLayout?.StartPassCountdown(_networkPassingDuration);
			return;
		}
		if (state.phase == "playing")
		{
			if (_networkPassAnimating)
			{
				SetMainPlayerPlayEnabled(false);
				return;
			}
			if (_networkPassExpected &&
				_networkPassingSelections.Count >= PlayerCount &&
				_networkPassingReceivedCards.Count == 3)
			{
				TryStartNetworkPassingAnimation();
				if (_networkPassAnimationStarted || IsDealing)
				{
					SetMainPlayerPlayEnabled(false);
					return;
				}
			}
			else if (_networkPassExpected && !_networkPassAnimationStarted)
			{
				// The state patch can arrive before the fourth passing_selected
				// message. Keep the play gate closed until the complete pass is
				// observed and its animation has been started.
				SetMainPlayerPlayEnabled(false);
				return;
			}
			UpdateNetworkTurn(state.currentTurn, state.turnDuration > 0 ? state.turnDuration : 15_000, state);
		}
	}

	private void HandleNetworkHandReceived(System.Collections.Generic.IReadOnlyList<string> cardIds, int roundNumber)
	{
		if (_networkTransitioning || _networkAdapter?.State?.phase is not ("table_ready" or "dealing" or "passing" or "playing") || _networkDealStarted || cardIds is null || cardIds.Count == 0)
			return;
		var cards = new List<CardData>(cardIds.Count);
		foreach (string cardId in cardIds)
		{
			if (TryParseCardId(cardId, out CardData card)) cards.Add(card);
		}
		if (cards.Count > 0)
		{
			_networkHandCards.Clear();
			_networkHandCards.AddRange(cards);
			_networkDealStarted = true;
			StartDeal(cards.ToArray());
		}
	}

	private void HandleNetworkCardPlayRequested(int cardIndex, CardData cardData)
	{
		double now = Time.GetTicksMsec();
		if (_networkAdapter is not null &&
			now - _lastNetworkPlaySentMsec >= NetworkPlayRequestIntervalMsec)
		{
			_lastNetworkPlaySentMsec = now;
			_ = _networkAdapter.PlayCardAsync(cardIndex, cardData);
		}
	}

	private void HandleNetworkPassingStarted(int duration, int deadline, int roundNumber)
	{
		_networkPassingDuration = duration > 0 ? duration : 30_000;
		_networkPassExpected = true;
		if (roundNumber >= 0 && roundNumber != _networkPassingRound)
		{
			_networkPassingRound = roundNumber;
			_networkPassingSelections.Clear();
			_networkPassingReceivedCards.Clear();
			_networkPassSubmitted = false;
			_networkPassAnimationStarted = false;
			_networkPassAnimating = false;
		}
		_mainHandLayout?.StartPassCountdown(_networkPassingDuration);
		if (!IsDealing && !_networkPassAnimationStarted)
		{
			SetMainPlayerPlayEnabled(false);
			_mainHandLayout?.SetPassSelectionEnabled(true);
		}
	}

	private void HandleNetworkPassingSelected(
		string playerId,
		System.Collections.Generic.IReadOnlyList<string> cardIds,
		System.Collections.Generic.IReadOnlyList<int> cardIndexes,
		int roundNumber)
	{
		if (_networkAdapter is null || string.IsNullOrWhiteSpace(playerId))
			return;
		if (roundNumber >= 0 && _networkAdapter.State?.roundNumber != roundNumber)
			return;
		_networkPassingSelections[playerId] = new PassingSelection(
			cardIds ?? Array.Empty<string>(),
			cardIndexes ?? Array.Empty<int>()
		);
		if (playerId == _networkAdapter.SessionId)
		{
			_mainHandLayout?.ApplyPassSelection(cardIds, lockSelection: true);
			_networkPassSubmitted = true;
			_mainHandLayout?.ClearCountdown();
		}
		else if (_networkAdapter.State.players.TryGetValue(playerId, out Player player))
		{
			int seat = (player.seat - GetLocalSeat(_networkAdapter.State) + PlayerCount) % PlayerCount;
			GetOtherHand(seat)?.SelectCards(cardIndexes);
		}
		TryStartNetworkPassingAnimation();
	}

	private void HandleNetworkPassingReceived(
		string fromPlayerId,
		System.Collections.Generic.IReadOnlyList<string> cardIds,
		System.Collections.Generic.IReadOnlyList<string> suits,
		System.Collections.Generic.IReadOnlyList<int> ranks,
		int roundNumber)
	{
		if (roundNumber >= 0 && _networkAdapter?.State?.roundNumber != roundNumber)
			return;
		_networkPassingReceivedCards.Clear();
		if (cardIds is not null)
		{
			foreach (string cardId in cardIds)
			{
				if (TryParseCardId(cardId, out CardData card))
					_networkPassingReceivedCards.Add(card);
			}
		}
		TryStartNetworkPassingAnimation();
	}

	private void HandleNetworkPassingCompleted()
	{
		TryStartNetworkPassingAnimation();
	}

	private void TryStartNetworkPassingAnimation()
	{
		if (_networkPassAnimationStarted || _networkAdapter is null ||
			_networkAdapter.State?.phase != "playing" ||
			_networkPassingSelections.Count < PlayerCount ||
			_networkPassingReceivedCards.Count != 3 ||
			_animationLayer is null || !IsInstanceValid(_animationLayer))
			return;

		ResolveSceneReferences();
		var flights = new List<PassingFlight>(PlayerCount * 3);
		MyRoomState state = _networkAdapter.State;
		// Every source seat sends to its clockwise successor. This deliberately
		// creates all four legs at once: main -> left, left -> opposite,
		// opposite -> right, and right -> main.
		for (int sourceSeat = 0; sourceSeat < PlayerCount; sourceSeat++)
		{
			string sourcePlayerId = GetPlayerIdForLocalSeat(state, sourceSeat);
			if (string.IsNullOrEmpty(sourcePlayerId) ||
				!_networkPassingSelections.TryGetValue(sourcePlayerId, out PassingSelection selection))
				return;
			Control sourceHand = GetHand(sourceSeat);
			Control destination = GetHand((sourceSeat + 1) % PlayerCount);
			if (sourceHand is null || destination is null ||
				!IsInstanceValid(sourceHand) || !IsInstanceValid(destination))
				return;

			for (int cardIndex = 0; cardIndex < 3; cardIndex++)
			{
				CardControl card = sourceSeat == MainPlayerIndex
					? FindMainCard(selection.CardIds.ElementAtOrDefault(cardIndex))
					: GetCardAt(sourceHand as OtherHandLayout, selection.CardIndexes.ElementAtOrDefault(cardIndex));
				if (card is null || !IsInstanceValid(card))
					return;

				CardPose2D sourcePose = new(
					CardPose2D.GetRenderedCanvasTransform(card),
					new Vector2(card.CardWidth, card.CardHeight),
					card.IsFaceUp
				);
				if (destination is MainHandLayout && sourceSeat == RightPlayerIndex)
				{
					// Only this viewer is the recipient of the source player's private
					// cards. Configure the card while it is still showing its back so
					// AnimationLayer can flip it as it reaches the local hand.
					card.Setup(_networkPassingReceivedCards[cardIndex], startFaceUp: false);
				}
				CardPose2D targetPose;
				try
				{
					targetPose = GetReceivePose(destination, card);
				}
				catch (Exception exception)
				{
					GD.PushWarning($"Unable to calculate a passing destination: {exception.Message}");
					return;
				}
				flights.Add(new PassingFlight(card, destination, sourcePose, targetPose));
			}
		}

		_networkPassAnimationStarted = true;
		_networkPassAnimating = true;
		_networkPassGeneration++;
		int generation = _networkPassGeneration;
		_networkPassFlights = 0;
		SetMainPlayerPlayEnabled(false);
		_mainHandLayout?.SetSelectionEnabled(false);

		foreach (PassingFlight flight in flights)
		{
			bool detached = flight.Destination is MainHandLayout
				? _otherHandLayout3.DetachCardForTransfer(flight.Card)
				: DetachPassingSourceCard(flight.Card);
			if (!detached)
				continue;
			flight.Card.Reparent(_animationLayer, keepGlobalTransform: true);
			_networkPassFlights++;
			bool started = _animationLayer.PlayPassToPose(
				flight.Card,
				flight.SourcePose,
				flight.TargetPose,
				card => HandleNetworkPassFlightCompleted(card, flight.Destination, generation)
			);
			if (!started)
			{
				_networkPassFlights = Math.Max(0, _networkPassFlights - 1);
				if (IsInstanceValid(flight.Card))
					ReceiveCard(flight.Destination, flight.Card);
			}
		}
		if (_networkPassFlights == 0)
			FinishNetworkPassingAnimation(generation);
	}

	private void HandleNetworkPassFlightCompleted(CardControl card, Control destination, int generation)
	{
		if (IsInstanceValid(card) && destination is not null && IsInstanceValid(destination))
			ReceiveCard(destination, card);
		if (generation != _networkPassGeneration)
			return;
		_networkPassFlights = Math.Max(0, _networkPassFlights - 1);
		if (_networkPassFlights == 0)
			FinishNetworkPassingAnimation(generation);
	}

	private void FinishNetworkPassingAnimation(int generation)
	{
		if (generation != _networkPassGeneration)
			return;
		_networkPassAnimating = false;
		_networkPassExpected = false;
		foreach (OtherHandLayout hand in _opponentHands)
			hand?.RetractSelectedCards();
		_mainHandLayout?.SetPassSelectionEnabled(false);
		_mainHandLayout?.ClearCountdown();

		if (_networkAdapter is not null &&
			_networkPassingSelections.TryGetValue(_networkAdapter.SessionId, out PassingSelection ownSelection))
		{
			HashSet<string> outgoing = new(ownSelection.CardIds, StringComparer.Ordinal);
			_networkHandCards.RemoveAll(card => outgoing.Contains(ToCardId(card)));
			_networkHandCards.AddRange(_networkPassingReceivedCards);
		}
		_mainHandLayout?.ArrangeHand();
		if (_networkAdapter?.State?.phase == "playing")
			UpdateNetworkTurn(_networkAdapter.State.currentTurn, _networkAdapter.State.turnDuration > 0
				? _networkAdapter.State.turnDuration : 15_000, _networkAdapter.State);
		DrainNetworkProgress();
	}

	private bool DetachPassingSourceCard(CardControl card)
	{
		if (card.GetParent() is MainHandLayout mainHand)
			return mainHand.DetachCardForTransfer(card);
		if (card.GetParent() is OtherHandLayout otherHand)
			return otherHand.DetachCardForTransfer(card);
		return false;
	}

	private CardControl FindMainCard(string cardId)
	{
		if (string.IsNullOrEmpty(cardId) || _mainHandLayout is null)
			return null;
		return _mainHandLayout.Cards.FirstOrDefault(card => ToCardId(card.Data) == cardId);
	}

	private static CardControl GetCardAt(OtherHandLayout hand, int index)
	{
		return hand is not null && index >= 0 && index < hand.Cards.Count
			? hand.Cards[index]
			: null;
	}

	private string GetPlayerIdForLocalSeat(MyRoomState state, int localSeat)
	{
		if (state is null || _networkAdapter is null)
			return string.Empty;
		foreach (string playerId in state.playerOrder.GetItems())
		{
			if (!state.players.TryGetValue(playerId, out Player player)) continue;
			int seat = (player.seat - GetLocalSeat(state) + PlayerCount) % PlayerCount;
			if (seat == localSeat) return playerId;
		}
		return string.Empty;
	}

	private void HandleNetworkCardPlayed(string playerId, string cardId, int roundNumber, int playSequence)
	{
		QueueNetworkPlay(new NetworkPlay(playerId, cardId, roundNumber, playSequence));
	}

	private void HandleNetworkTurnStarted(string playerId, int duration, int trickNumber)
	{
		if (_networkAdapter?.State is MyRoomState state)
		{
			// A new turn may belong to the same player who just won the trick.
			_mainHandLayout?.ClearCountdown();
			UpdateNetworkTurn(playerId, duration, state);
		}
	}

	private void UpdateNetworkTurn(string playerId, int duration, MyRoomState state)
	{
		if (_networkPassAnimating)
		{
			SetMainPlayerPlayEnabled(false);
			_mainHandLayout?.SetSelectionEnabled(false);
			return;
		}
		if (IsCollectingTrick || _networkPlayAnimating || _pendingNetworkPlays.Count > 0 ||
			_pendingNetworkTrickResolutions.Count > 0)
		{
			SetMainPlayerPlayEnabled(false);
			_mainHandLayout?.SetSelectionEnabled(false);
			return;
		}
		bool localTurn = _networkAdapter is not null && playerId == _networkAdapter.SessionId;
		if (_mainHandLayout is not null && IsInstanceValid(_mainHandLayout))
		{
			if (localTurn) _mainHandLayout.StartTurnCountdown(duration);
			else _mainHandLayout.ClearCountdown();
		}
		if (!localTurn || _mainHandLayout is null || !IsInstanceValid(_mainHandLayout))
		{
			SetMainPlayerPlayEnabled(false);
			_mainHandLayout?.SetSelectionEnabled(false);
			_mainHandLayout?.ClearPlayableCards();
			return;
		}
		List<CardData> legal = GetLegalNetworkCards(state, out bool mustDiscardPoints);
		bool openingTurn = state.trickNumber == 0 && state.trick.Count == 0;
		// MainHandLayout controls hit testing; Table controls the network play
		// gate. Both must be open before the second click can be forwarded to the
		// server.
		SetMainPlayerPlayEnabled(true);
		_mainHandLayout.SetSelectionEnabled(true);
		_mainHandLayout.SetPlayableCards(
			legal,
			openingTurn ? "你持有 ♣2，由你第一个出牌。"
				: mustDiscardPoints ? "你已缺门，必须先出红桃或黑桃 Q。" : "");
	}

	private List<CardData> GetLegalNetworkCards(MyRoomState state, out bool mustDiscardPoints)
	{
		PokerSuit? lead = state.trick.Count > 0 && !string.IsNullOrEmpty(state.leadSuit)
			? ParseSuit(state.leadSuit) : null;
		return CardRules.LegalCards(_networkHandCards, lead, state.trickNumber == 0,
			state.heartsBroken, out mustDiscardPoints, state.heartsBreakingEnabled, state.mustDiscardPointsWhenVoid);
	}

	private static PokerSuit ParseSuit(string suit) => suit switch
	{
		"clubs" or "club" => PokerSuit.Club,
		"diamonds" or "diamond" => PokerSuit.Diamond,
		"hearts" or "heart" => PokerSuit.Heart,
		_ => PokerSuit.Spade,
	};

	private void HandleNetworkTrickResolved(
		string winnerId,
		int points,
		int trickNumber,
		int roundNumber,
		int playSequence)
	{
		QueueNetworkTrickResolution(new NetworkTrickResolution(
			winnerId,
			points,
			trickNumber,
			roundNumber,
			playSequence));
	}

	private void HandleNetworkRoundFinished()
	{
		// The phase patch is the authoritative trigger. The message can arrive
		// one dispatch tick before that patch, so do not schedule a one-shot
		// popup against the stale `playing` state here.
		if (_networkAdapter?.State?.phase == "finished")
			RequestNetworkSettlement();
	}

	private void RequestNetworkSettlement(bool waitForFinalTrick = true)
	{
		if (_networkSettlementScheduled) return;
		_networkSettlementScheduled = true;
		_ = ShowSettlementAfterFinalTrickAsync(waitForFinalTrick);
	}

	private async Task ShowSettlementAfterFinalTrickAsync(bool waitForFinalTrick)
	{
		// State patches and room messages are delivered on separate client lanes.
		// Wait briefly for the final trick_resolved message to start collection
		// before we inspect IsCollectingTrick.
		int dispatchFrames = 0;
		while (IsInsideTree() && !_networkTransitioning && waitForFinalTrick && !_networkFinalTrickReceived && dispatchFrames++ < 120)
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		while (IsInsideTree() && !_networkTransitioning && IsCollectingTrick)
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		if (!IsInsideTree() || _networkTransitioning || _networkAdapter?.State?.phase != "finished") return;
		_settlement.Open();
		UpdateSettlementUi(_networkAdapter.State);
	}

	private void HandleNetworkRoomReset(string reason)
	{
		// The message may precede the waiting patch. Reset immediately, and let
		// the authoritative patch refresh seats and enable waiting-room actions.
		ResetNetworkPresentation();
		SetWaitingVisible(true);
		HandleNetworkStatus(reason);
	}

	private void TransitionNetworkScene(string scenePath)
	{
		if (_networkTransitioning || !IsInsideTree()) return;
		_networkTransitioning = true;
		GameSession.GameAdapter = _networkAdapter;
		SceneNavigation.Change(this, scenePath);
	}

	private int GetLocalSeat(MyRoomState state)
	{
		return state is not null && _networkAdapter is not null &&
			state.players.TryGetValue(_networkAdapter.SessionId, out Player local)
			? local.seat : 0;
	}

	private void ReconcileNetworkProgress(MyRoomState state)
	{
		if (state is null || !EnsureNetworkProgressRound(state.roundNumber))
			return;

		if (state.playHistory is not null)
		{
			for (int index = 0; index < state.playHistory.Count; index++)
			{
				TrickCard play = state.playHistory[index];
				if (play is null) continue;
				QueueNetworkPlay(new NetworkPlay(
					play.playerId,
					play.cardId,
					state.roundNumber,
					index + 1));
			}
		}

		if (state.trickHistory is not null)
		{
			for (int index = 0; index < state.trickHistory.Count; index++)
			{
				ResolvedTrick trick = state.trickHistory[index];
				if (trick is null) continue;
				QueueNetworkTrickResolution(new NetworkTrickResolution(
					trick.winnerId,
					trick.points,
					index + 1,
					state.roundNumber,
					trick.playSequence));
			}
		}

		DrainNetworkProgress();
	}

	private void QueueNetworkPlay(NetworkPlay play)
	{
		if (string.IsNullOrEmpty(play.PlayerId) ||
			string.IsNullOrEmpty(play.CardId) ||
			play.PlaySequence < 1 ||
			!EnsureNetworkProgressRound(play.RoundNumber) ||
			play.PlaySequence <= _networkLastPlaySequence)
		{
			return;
		}

		_pendingNetworkPlays.TryAdd(play.PlaySequence, play);
		DrainNetworkProgress();
	}

	private void QueueNetworkTrickResolution(NetworkTrickResolution resolution)
	{
		if (string.IsNullOrEmpty(resolution.WinnerId) ||
			resolution.TrickNumber < 1 ||
			resolution.PlaySequence < PlayerCount ||
			!EnsureNetworkProgressRound(resolution.RoundNumber) ||
			resolution.TrickNumber <= _networkLastResolvedTrick)
		{
			return;
		}

		_pendingNetworkTrickResolutions.TryAdd(resolution.TrickNumber, resolution);
		DrainNetworkProgress();
	}

	private bool EnsureNetworkProgressRound(int roundNumber)
	{
		if (roundNumber < 0)
			roundNumber = _networkAdapter?.State?.roundNumber ?? -1;
		if (roundNumber < 0 || roundNumber < _networkProgressRound)
			return false;
		if (roundNumber == _networkProgressRound)
			return true;

		_networkProgressRound = roundNumber;
		_networkLastPlaySequence = 0;
		_networkLastResolvedTrick = 0;
		_networkPlayAnimating = false;
		_pendingNetworkPlays.Clear();
		_pendingNetworkTrickResolutions.Clear();
		return true;
	}

	private void DrainNetworkProgress()
	{
		if (_networkAdapter?.State is not MyRoomState state ||
			_networkPlayAnimating ||
			IsDealing ||
			_networkPassAnimating ||
			IsCollectingTrick)
		{
			return;
		}

		int nextTrickNumber = _networkLastResolvedTrick + 1;
		if (_pendingNetworkTrickResolutions.TryGetValue(
			nextTrickNumber,
			out NetworkTrickResolution resolution) &&
			resolution.PlaySequence <= _networkLastPlaySequence)
		{
			if (!state.players.TryGetValue(resolution.WinnerId, out Player winner))
				return;
			int seat = (winner.seat - GetLocalSeat(state) + PlayerCount) % PlayerCount;
			if (!CollectTrick(seat, resolution.Points))
				return;
			_pendingNetworkTrickResolutions.Remove(nextTrickNumber);
			_networkLastResolvedTrick = nextTrickNumber;
			if (nextTrickNumber >= 13)
				_networkFinalTrickReceived = true;
			return;
		}

		// Never let the first play of the next trick replace a card before the
		// preceding trick has been authoritatively collected.
		if (_networkLastResolvedTrick < _networkLastPlaySequence / PlayerCount)
			return;

		int nextPlaySequence = _networkLastPlaySequence + 1;
		if (!_pendingNetworkPlays.TryGetValue(nextPlaySequence, out NetworkPlay play) ||
			!TryStartNetworkPlay(play))
		{
			return;
		}

		_pendingNetworkPlays.Remove(nextPlaySequence);
		_networkLastPlaySequence = nextPlaySequence;
	}

	private bool TryStartNetworkPlay(NetworkPlay play)
	{
		if (_networkAdapter?.State is not MyRoomState state ||
			!TryParseCardId(play.CardId, out CardData card))
		{
			return false;
		}

		_networkPlayAnimating = true;
		Action<CardControl> completed = _ => HandleNetworkPlayAnimationCompleted(play.RoundNumber);
		bool started;
		if (play.PlayerId == _networkAdapter.SessionId)
		{
			_mainHandLayout?.ClearCountdown();
			int visualIndex = -1;
			if (_mainHandLayout is not null && IsInstanceValid(_mainHandLayout))
			{
				for (int index = 0; index < _mainHandLayout.Cards.Count; index++)
				{
					if (ToCardId(_mainHandLayout.Cards[index].Data) != play.CardId) continue;
					visualIndex = index;
					break;
				}
			}
			started = visualIndex >= 0 && _mainHandLayout.TryPlayCard(visualIndex, completed);
			if (started)
			{
				SetMainPlayerPlayEnabled(false);
				_mainHandLayout.SetSelectionEnabled(false);
				int localIndex = _networkHandCards.FindIndex(candidate => ToCardId(candidate) == play.CardId);
				if (localIndex >= 0) _networkHandCards.RemoveAt(localIndex);
			}
		}
		else if (state.players.TryGetValue(play.PlayerId, out Player player))
		{
			int seat = (player.seat - GetLocalSeat(state) + PlayerCount) % PlayerCount;
			started = seat != MainPlayerIndex && TryPlayOpponentCard(seat, GetOtherHand(seat), 0, card, completed);
		}
		else
		{
			started = false;
		}

		if (started)
		{
			SetMainPlayerPlayEnabled(false);
			_mainHandLayout?.SetSelectionEnabled(false);
		}
		if (!started)
			_networkPlayAnimating = false;
		return started;
	}

	private void HandleNetworkPlayAnimationCompleted(int roundNumber)
	{
		if (roundNumber != _networkProgressRound)
			return;
		_networkPlayAnimating = false;
		SetMainPlayerPlayEnabled(false);
		_mainHandLayout?.SetSelectionEnabled(false);
		DrainNetworkProgress();
		if (_networkAdapter?.State is MyRoomState state && state.phase == "playing")
			UpdateNetworkTurn(state.currentTurn, state.turnDuration > 0 ? state.turnDuration : 15_000, state);
	}

	private static string ToCardId(CardData card)
	{
		string suit = card.Suit switch
		{
			PokerSuit.Club => "Club",
			PokerSuit.Diamond => "Diamond",
			PokerSuit.Heart => "Heart",
			_ => "Spade",
		};
		string rank = card.Rank switch
		{
			PokerRank.Jack => "J",
			PokerRank.Queen => "Q",
			PokerRank.King => "K",
			PokerRank.Ace => "A",
			_ => ((int)card.Rank).ToString(),
		};
		return suit + rank;
	}

	private static bool TryParseCardId(string value, out CardData card)
	{
		card = default;
		if (string.IsNullOrWhiteSpace(value)) return false;
		PokerSuit suit;
		string rankText;
		if (value.StartsWith("Club", StringComparison.Ordinal)) { suit = PokerSuit.Club; rankText = value[4..]; }
		else if (value.StartsWith("Diamond", StringComparison.Ordinal)) { suit = PokerSuit.Diamond; rankText = value[7..]; }
		else if (value.StartsWith("Heart", StringComparison.Ordinal)) { suit = PokerSuit.Heart; rankText = value[5..]; }
		else if (value.StartsWith("Spade", StringComparison.Ordinal)) { suit = PokerSuit.Spade; rankText = value[5..]; }
		else return false;
		PokerRank rank = rankText switch
		{
			"J" => PokerRank.Jack,
			"Q" => PokerRank.Queen,
			"K" => PokerRank.King,
			"A" => PokerRank.Ace,
			_ when int.TryParse(rankText, out int number) && number >= 2 && number <= 10 => (PokerRank)number,
			_ => (PokerRank)0,
		};
		if ((int)rank < 2) return false;
		card = new CardData(suit, rank);
		return true;
	}

	/// <summary>
	/// Plays a card for seat 0..3, ordered main, left, opposite, right. The main
	/// hand already knows its card data; opponent hands use the supplied data to
	/// reveal the chosen card while it flies.
	/// </summary>
	public bool PlayCard(int playerIndex, int cardIndex, CardData cardData)
	{
		return playerIndex switch
		{
			MainPlayerIndex => PlayMainPlayerCard(cardIndex),
			LeftPlayerIndex => TryPlayOpponentCard(
				LeftPlayerIndex,
				_otherHandLayout,
				cardIndex,
				cardData
			),
			OppositePlayerIndex => TryPlayOpponentCard(
				OppositePlayerIndex,
				_otherHandLayout2,
				cardIndex,
				cardData
			),
			RightPlayerIndex => TryPlayOpponentCard(
				RightPlayerIndex,
				_otherHandLayout3,
				cardIndex,
				cardData
			),
			_ => false
		};
	}

	/// <summary>Plays the indexed card from the main player's visible hand.</summary>
	public bool PlayMainPlayerCard(int cardIndex)
	{
		if (!CanMainPlayerPlay ||
			_mainHandLayout is null ||
			!IsInstanceValid(_mainHandLayout) ||
			!_mainHandLayout.TryPlayCard(cardIndex))
		{
			return false;
		}

		_rightPlayerPlayCompleted = false;
		SetMainPlayerPlayEnabled(false);
		_mainHandLayout.SetSelectionEnabled(false);
		return true;
	}

	/// <summary>
	/// Queues collection of the current four-card trick for seat 0..3. The table
	/// waits for outstanding play flights, moves every play-area card to the
	/// selected player's authored collect pose, then applies the score delta.
	/// </summary>
	public bool CollectTrick(int playerIndex, int roundScoreDelta)
	{
		ResolveSceneReferences();
		PlayerInfo collector = GetPlayerInfo(playerIndex);
		if (IsDealing ||
			IsCollectingTrick ||
			!IsInsideTree() ||
			collector is null ||
			!IsInstanceValid(collector) ||
			_animationLayer is null ||
			!IsInstanceValid(_animationLayer) ||
			!_animationLayer.IsInsideTree())
		{
			return false;
		}

		int generation = ++_collectGeneration;
		_collectFlights = 0;
		_collectDispatchCompleted = false;
		_collectCardsPrepared = false;
		_preparedCollectFlights.Clear();
		_collectingPlayerInfo = collector;
		_collectingPlayerIndex = playerIndex;
		_pendingRoundScoreDelta = roundScoreDelta;
		IsCollectingTrick = true;

		// A trick is still in progress until every collect flight has landed,
		// regardless of which seat receives it. Keep the main player's play gate
		// closed for the whole collection animation.
		SetMainPlayerPlayEnabled(false);
		_mainHandLayout?.SetCountdownSuppressed(true);

		if (!_animationLayer.IsAnimating)
			TryPrepareCollectTrickCards(generation);

		_ = CollectTrickAsync(generation);
		return true;
	}

	/// <summary>Returns the current logical hand size for seat 0..3.</summary>
	public int GetPlayerCardCount(int playerIndex)
	{
		return playerIndex switch
		{
			MainPlayerIndex => GetCardCount(_mainHandLayout),
			LeftPlayerIndex => GetCardCount(_otherHandLayout),
			OppositePlayerIndex => GetCardCount(_otherHandLayout2),
			RightPlayerIndex => GetCardCount(_otherHandLayout3),
			_ => 0
		};
	}

	/// <summary>
	/// Clears the table and deals an equal number of cards to all four seats.
	/// Main-player cards are configured from <paramref name="mainPlayerCards"/>;
	/// opponent cards remain unconfigured backs until they are played.
	/// </summary>
	public bool StartDeal(CardData[] mainPlayerCards)
	{
		ArgumentNullException.ThrowIfNull(mainPlayerCards);
		if (!HasCompleteTable())
			return false;

		CancelLocalPassing();
		CancelCollectTrick();
		int generation = ++_dealGeneration;
		_animationLayer.CancelAnimation(freeCard: true);
		_rightPlayerPlayCompleted = false;
		SetMainPlayerPlayEnabled(false);
		ClearHandsAndPlayAreas();

		int totalCardCount = checked(mainPlayerCards.Length * PlayerCount);
		_cardDeck.ChangeMaxCardCount(Math.Max(1, totalCardCount));
		_cardDeck.ChangeCardCount(totalCardCount);

		_dealFlights = 0;
		_dealDispatchCompleted = mainPlayerCards.Length == 0;
		_dealFinishing = false;
		IsDealing = true;

		if (mainPlayerCards.Length > 0)
			_ = DealCardsAsync(mainPlayerCards.ToArray(), generation);
		else
			TryCompleteDeal(generation);

		return true;
	}

	private async Task CollectTrickAsync(int generation)
	{
		while (IsCollectOperationCurrent(generation) && !_collectCardsPrepared)
		{
			if (!_animationLayer.IsAnimating)
			{
				if (!TryPrepareCollectTrickCards(generation))
				{
					AbortCollectTrick(generation);
					return;
				}

				break;
			}

			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
	}

	private void HandleCollectFlightCompleted(CardControl card, int generation)
	{
		if (IsInstanceValid(card))
			card.QueueFree();

		if (generation != _collectGeneration)
			return;

		_collectFlights = Math.Max(0, _collectFlights - 1);
		TryCompleteCollectTrick(generation);
	}

	private void TryCompleteCollectTrick(int generation)
	{
		if (!IsCollectOperationCurrent(generation) ||
			!_collectDispatchCompleted ||
			_collectFlights > 0)
		{
			return;
		}

		PlayerInfo collector = _collectingPlayerInfo;
		int collectingPlayerIndex = _collectingPlayerIndex;
		int roundScoreDelta = _pendingRoundScoreDelta;
		ResetCollectTrickState();

		if (collector is not null && IsInstanceValid(collector))
			collector.ApplyRoundScoreDelta(roundScoreDelta);

		if (collectingPlayerIndex == MainPlayerIndex ||
			(collectingPlayerIndex != MainPlayerIndex && _rightPlayerPlayCompleted))
		{
			SetMainPlayerPlayEnabled(true);
		}
		if (_networkAdapter?.State is MyRoomState state && state.phase == "playing")
			UpdateNetworkTurn(state.currentTurn, state.turnDuration > 0 ? state.turnDuration : 15_000, state);
		DrainNetworkProgress();
	}

	private bool TryPrepareCollectTrickCards(int generation)
	{
		if (!IsCollectOperationCurrent(generation))
			return false;
		if (_collectCardsPrepared)
			return true;
		if (!TryCreateCollectFlights(
			_collectingPlayerInfo,
			out List<CollectFlight> flights
		))
		{
			return false;
		}

		_preparedCollectFlights.Clear();
		_preparedCollectFlights.AddRange(flights);
		_collectCardsPrepared = _preparedCollectFlights.Count == PlayerCount;
		if (!_collectCardsPrepared)
			return false;

		foreach (CollectFlight flight in flights)
		{
			CardControl card = flight.Card;
			if (!IsInstanceValid(card))
			{
				FreePreparedCollectCards();
				return false;
			}

			// Hand the card to AnimationLayer and let its transform carrier apply the
			// snapshotted source pose immediately. Control.Reparent only preserves the
			// position here and loses a rotated PlayArea's basis (notably the opposite
			// seat), which caused a visible one-frame jump before collection.
			card.Reparent(_animationLayer, keepGlobalTransform: false);
			_collectFlights++;
			bool started = _animationLayer.PlayCollectToPose(
				card,
				flight.SourcePose,
				flight.TargetPose,
				TrickCollectDelay,
				collectedCard => HandleCollectFlightCompleted(
					collectedCard,
					generation
				)
			);

			if (!started)
			{
				_collectFlights = Math.Max(0, _collectFlights - 1);
				if (IsInstanceValid(card))
					card.QueueFree();
			}
		}

		_collectDispatchCompleted = true;
		TryCompleteCollectTrick(generation);
		return _collectCardsPrepared;
	}

	private bool TryCreateCollectFlights(
		PlayerInfo collector,
		out List<CollectFlight> flights)
	{
		flights = new List<CollectFlight>(PlayerCount);
		PlayArea[] playAreas =
		{
			_mainPlayArea,
			_leftPlayArea,
			_oppositePlayArea,
			_rightPlayArea
		};

		foreach (PlayArea playArea in playAreas)
		{
			CardControl card = playArea?.PlayedCard;
			if (playArea is null ||
				!IsInstanceValid(playArea) ||
				card is null ||
				!IsInstanceValid(card))
			{
				flights.Clear();
				return false;
			}

			try
			{
				Vector2 cardSize = new(card.CardWidth, card.CardHeight);
				CardPose2D sourcePose = new(
					CardPose2D.GetRenderedCanvasTransform(card),
					cardSize,
					card.IsFaceUp
				);
				CardPose2D targetPose = collector.GetCollectPose(card);
				flights.Add(new CollectFlight(card, sourcePose, targetPose));
			}
			catch (Exception exception)
			{
				GD.PushWarning($"Unable to calculate a trick collect pose: {exception.Message}");
				flights.Clear();
				return false;
			}
		}

		// Preserve the play areas' rendered order when moving their cards under
		// one animation parent. Equal canvas Z values follow scene-tree order.
		flights.Sort((left, right) =>
		{
			int order = GetCanvasZIndex(left.Card).CompareTo(GetCanvasZIndex(right.Card));
			return order != 0 ? order :
				left.Card.GetParent().GetIndex().CompareTo(right.Card.GetParent().GetIndex());
		});

		return flights.Count == PlayerCount;
	}

	private static int GetCanvasZIndex(CanvasItem item)
	{
		int zIndex = item.ZIndex;
		while (item.ZAsRelative && item.GetParent() is CanvasItem parent)
		{
			item = parent;
			zIndex += item.ZIndex;
		}
		return zIndex;
	}

	private bool IsCollectOperationCurrent(int generation)
	{
		return generation == _collectGeneration &&
			IsCollectingTrick &&
			IsInsideTree() &&
			_animationLayer is not null &&
			IsInstanceValid(_animationLayer);
	}

	private void AbortCollectTrick(int generation)
	{
		if (generation == _collectGeneration)
		{
			int collectingPlayerIndex = _collectingPlayerIndex;
			FreePreparedCollectCards();
			ResetCollectTrickState();
			if (collectingPlayerIndex != MainPlayerIndex && _rightPlayerPlayCompleted)
				SetMainPlayerPlayEnabled(true);
		}
	}

	private void CancelCollectTrick()
	{
		_collectGeneration++;
		FreePreparedCollectCards();
		ResetCollectTrickState();
	}

	private void FreePreparedCollectCards()
	{
		foreach (CollectFlight flight in _preparedCollectFlights)
		{
			if (IsInstanceValid(flight.Card))
				flight.Card.QueueFree();
		}

		_preparedCollectFlights.Clear();
	}

	private void ResetCollectTrickState()
	{
		_collectFlights = 0;
		_collectDispatchCompleted = false;
		_collectCardsPrepared = false;
		_preparedCollectFlights.Clear();
		_collectingPlayerInfo = null!;
		_collectingPlayerIndex = -1;
		_pendingRoundScoreDelta = 0;
		IsCollectingTrick = false;
		if (_mainHandLayout is not null && IsInstanceValid(_mainHandLayout))
			_mainHandLayout.SetCountdownSuppressed(false);
	}

	private async Task DealCardsAsync(CardData[] mainPlayerCards, int generation)
	{
		int totalDispatches = mainPlayerCards.Length * PlayerCount;
		int dispatched = 0;

		for (int cardIndex = 0; cardIndex < mainPlayerCards.Length; cardIndex++)
		{
			for (int playerIndex = 0; playerIndex < PlayerCount; playerIndex++)
			{
				if (generation != _dealGeneration || !IsInsideTree())
					return;

				CardData? mainCard = playerIndex == MainPlayerIndex
					? mainPlayerCards[cardIndex]
					: null;
				TryStartDealFlight(playerIndex, mainCard, generation);
				dispatched++;

				if (dispatched >= totalDispatches || DealInterval <= 0.0f)
					continue;

				SceneTreeTimer timer = GetTree().CreateTimer(DealInterval);
				await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
			}
		}

		if (generation != _dealGeneration)
			return;

		_dealDispatchCompleted = true;
		TryCompleteDeal(generation);
	}

	private bool TryStartDealFlight(
		int playerIndex,
		CardData? mainPlayerCard,
		int generation)
	{
		Control destination = GetHand(playerIndex);
		if (destination is null || !IsInstanceValid(destination) ||
			_cardDeck.CardCount <= 0 ||
			!_cardDeck.TryGetTopPose(out CardPose2D sourcePose) ||
			!_cardDeck.TryDuplicateTopCard(out CardControl card))
		{
			return false;
		}

		_animationLayer.AddChild(card);
		if (mainPlayerCard is CardData data)
			card.Setup(data, startFaceUp: sourcePose.IsFaceUp);

		CardPose2D targetPose;
		try
		{
			targetPose = GetReceivePose(destination, card);
		}
		catch (Exception exception)
		{
			GD.PushWarning($"Unable to calculate a deal destination: {exception.Message}");
			card.QueueFree();
			return false;
		}

		_dealFlights++;
		bool started = _animationLayer.PlayDrawToPose(
			card,
			sourcePose,
			targetPose,
			dealtCard => HandleDealFlightCompleted(
				destination,
				dealtCard,
				generation
			)
		);

		if (!started)
		{
			_dealFlights = Math.Max(0, _dealFlights - 1);
			if (IsInstanceValid(card))
				card.QueueFree();
			return false;
		}

		_cardDeck.ChangeCardCount(_cardDeck.CardCount - 1);
		return true;
	}

	private void HandleDealFlightCompleted(
		Control destination,
		CardControl card,
		int generation)
	{
		if (generation != _dealGeneration)
		{
			if (IsInstanceValid(card))
				card.QueueFree();
			return;
		}

		if (IsInstanceValid(card) && IsInstanceValid(destination))
			ReceiveCard(destination, card);
		else if (IsInstanceValid(card))
			card.QueueFree();

		_dealFlights = Math.Max(0, _dealFlights - 1);
		TryCompleteDeal(generation);
	}

	private void TryCompleteDeal(int generation)
	{
		if (generation != _dealGeneration ||
			!_dealDispatchCompleted ||
			_dealFlights > 0 ||
			_dealFinishing)
		{
			return;
		}

		_dealFinishing = true;
		_ = FinishDealAsync(generation);
	}

	private async Task FinishDealAsync(int generation)
	{
		if (ArrangeDelay > 0.0f)
		{
			SceneTreeTimer timer = GetTree().CreateTimer(ArrangeDelay);
			await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
		}

		if (generation != _dealGeneration || !IsInsideTree())
			return;

		_mainHandLayout.ArrangeHand();
		while (generation == _dealGeneration &&
			IsInsideTree() &&
			_mainHandLayout.IsLayoutAnimating)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}

		if (generation != _dealGeneration || !IsInsideTree())
			return;

		_dealFinishing = false;
		IsDealing = false;
		ApplyStoredPassingSelectionsToHands();
		if (_networkAdapter?.State?.phase == "dealing")
		{
			_networkDealReadySent = true;
			SetMainPlayerPlayEnabled(false);
			_mainHandLayout.SetSelectionEnabled(false);
			_ = _networkAdapter.DealReadyAsync();
		}
		else if (_networkAdapter?.State?.phase == "passing")
		{
			SetMainPlayerPlayEnabled(false);
			_mainHandLayout.SetPassSelectionEnabled(true);
			ApplyStoredPassingSelectionsToHands();
			if (_networkPassSubmitted)
				_mainHandLayout?.ClearCountdown();
			else
				_mainHandLayout?.StartPassCountdown(_networkPassingDuration);
		}
		else if (_networkAdapter?.State?.phase == "playing" &&
			_networkPassingSelections.Count >= PlayerCount &&
			_networkPassingReceivedCards.Count == 3)
		{
			SetMainPlayerPlayEnabled(false);
			TryStartNetworkPassingAnimation();
			if (!_networkPassAnimationStarted)
				UpdateNetworkTurn(_networkAdapter.State.currentTurn, _networkAdapter.State.turnDuration > 0
					? _networkAdapter.State.turnDuration : 15_000, _networkAdapter.State);
		}
		else
		{
			SetMainPlayerPlayEnabled(true);
			_mainHandLayout.SetSelectionEnabled(true);
		}
		DrainNetworkProgress();
	}

	private void ApplyStoredPassingSelectionsToHands()
	{
		if (_networkAdapter is null || _networkAdapter.State is null)
			return;
		foreach (KeyValuePair<string, PassingSelection> pair in _networkPassingSelections)
		{
			if (pair.Key == _networkAdapter.SessionId)
			{
				_mainHandLayout?.ApplyPassSelection(pair.Value.CardIds, lockSelection: true);
				_networkPassSubmitted = true;
				continue;
			}
			if (!_networkAdapter.State.players.TryGetValue(pair.Key, out Player player))
				continue;
			int seat = (player.seat - GetLocalSeat(_networkAdapter.State) + PlayerCount) % PlayerCount;
			GetOtherHand(seat)?.SelectCards(pair.Value.CardIndexes);
		}
	}

	private void HandleMainPlayerCardPlayRequested(int cardIndex)
	{
		if (!CanMainPlayerPlay ||
			_mainHandLayout is null ||
			!IsInstanceValid(_mainHandLayout) ||
			cardIndex < 0 ||
			cardIndex >= _mainHandLayout.Cards.Count)
		{
			return;
		}

		CardData data = _mainHandLayout.Cards[cardIndex].Data;
		MainPlayerCardPlayRequested?.Invoke(cardIndex, data);
	}

	private void HandleMainPlayerPassCardsSubmitted(IReadOnlyList<CardData> cards)
	{
		if (!UseNetworkSession)
		{
			MainPlayerPassCardsRequested?.Invoke(cards);
			return;
		}
		if (_networkAdapter is null || _networkAdapter.State?.phase != "passing" ||
			cards is null || cards.Count != 3)
			return;
		_networkPassSubmitted = true;
		_mainHandLayout?.ClearCountdown();
		_ = _networkAdapter.SubmitPassingCardsAsync(cards);
	}

	private void ResolveSceneReferences()
	{
		_cardDeck = Resolve(_cardDeck, "CardDeck");
		_mainHandLayout = Resolve(_mainHandLayout, "MainHandLayout");
		_otherHandLayout = Resolve(_otherHandLayout, "OtherHandLayout");
		_otherHandLayout2 = Resolve(_otherHandLayout2, "OtherHandLayout2");
		_otherHandLayout3 = Resolve(_otherHandLayout3, "OtherHandLayout3");
		_animationLayer = Resolve(_animationLayer, "InGame/AnimationLayer");
		_mainPlayArea = Resolve(_mainPlayArea, "MainPlayArea");
		_leftPlayArea = Resolve(_leftPlayArea, "LeftPlayArea");
		_oppositePlayArea = Resolve(_oppositePlayArea, "OppositePlayArea");
		_rightPlayArea = Resolve(_rightPlayArea, "RightPlayArea");
		ResolvePlayerInfoReferences();

		_opponentHands.Clear();
		if (_otherHandLayout is not null)
			_opponentHands.Add(_otherHandLayout);
		if (_otherHandLayout2 is not null)
			_opponentHands.Add(_otherHandLayout2);
		if (_otherHandLayout3 is not null)
			_opponentHands.Add(_otherHandLayout3);
	}

	private void BindLayoutAnimations()
	{
		_mainHandLayout?.BindPlayAnimation(_animationLayer, _mainPlayArea);
		_otherHandLayout?.BindPlayAnimation(_animationLayer, _leftPlayArea);
		_otherHandLayout2?.BindPlayAnimation(_animationLayer, _oppositePlayArea);
		_otherHandLayout3?.BindPlayAnimation(_animationLayer, _rightPlayArea);
	}

	private void ResolvePlayerInfoReferences()
	{
		_mainPlayerInfo = Resolve(_mainPlayerInfo, "MainPlayerInfo");
		_nextPlayerInfo = Resolve(_nextPlayerInfo, "PlayerInfo");
		_oppositePlayerInfo = Resolve(_oppositePlayerInfo, "PlayerInfo2");
		_previousPlayerInfo = Resolve(_previousPlayerInfo, "PlayerInfo3");
		for (int seat = 0; seat < PlayerCount; seat++)
		{
			PlayerInfo info = GetPlayerInfo(seat);
			if (info is not null && IsInstanceValid(info))
				info.IsLocalPlayer = seat == MainPlayerIndex;
		}
	}

	private static void InitializeSeatPlayerInfo(
		PlayerInfo playerInfo,
		string playerId,
		Texture2D avatar,
		int chipCount)
	{
		if (playerInfo is null || !IsInstanceValid(playerInfo))
			return;

		playerInfo.SetPlayerId(playerId);
		playerInfo.SetAvatar(avatar);
		playerInfo.SetChipCount(chipCount);
		playerInfo.SetRoundScore(0);
		playerInfo.SetSeatState(!string.IsNullOrWhiteSpace(playerId), false, false, false);
	}

	private static void UpdateSeatIdentity(PlayerInfo playerInfo, Player player)
	{
		if (playerInfo is null || !IsInstanceValid(playerInfo) || player is null)
			return;
		playerInfo.SetProfileIdentity(player.name, player.chips, player.avatarId);
		playerInfo.IsHost = player.isHost;
		playerInfo.IsBot = player.isBot;
	}

	private bool HasCompleteTable()
	{
		return _cardDeck is not null && IsInstanceValid(_cardDeck) &&
			_mainHandLayout is not null && IsInstanceValid(_mainHandLayout) &&
			_otherHandLayout is not null && IsInstanceValid(_otherHandLayout) &&
			_otherHandLayout2 is not null && IsInstanceValid(_otherHandLayout2) &&
			_otherHandLayout3 is not null && IsInstanceValid(_otherHandLayout3) &&
			_animationLayer is not null && IsInstanceValid(_animationLayer) &&
			_mainPlayArea is not null && IsInstanceValid(_mainPlayArea) &&
			_leftPlayArea is not null && IsInstanceValid(_leftPlayArea) &&
			_oppositePlayArea is not null && IsInstanceValid(_oppositePlayArea) &&
			_rightPlayArea is not null && IsInstanceValid(_rightPlayArea);
	}

	private void ClearHandsAndPlayAreas()
	{
		_mainHandLayout.ClearCards();
		foreach (OtherHandLayout hand in _opponentHands)
			hand.ClearCards();

		_mainPlayArea.ClearCard();
		_leftPlayArea.ClearCard();
		_oppositePlayArea.ClearCard();
		_rightPlayArea.ClearCard();
	}

	private Control GetHand(int playerIndex)
	{
		return playerIndex switch
		{
			MainPlayerIndex => _mainHandLayout,
			LeftPlayerIndex => _otherHandLayout,
			OppositePlayerIndex => _otherHandLayout2,
			RightPlayerIndex => _otherHandLayout3,
			_ => null
		};
	}

	private OtherHandLayout GetOtherHand(int playerIndex)
	{
		return playerIndex switch
		{
			LeftPlayerIndex => _otherHandLayout,
			OppositePlayerIndex => _otherHandLayout2,
			RightPlayerIndex => _otherHandLayout3,
			_ => null
		};
	}

	private PlayerInfo GetPlayerInfo(int playerIndex)
	{
		return playerIndex switch
		{
			MainPlayerIndex => _mainPlayerInfo,
			LeftPlayerIndex => _nextPlayerInfo,
			OppositePlayerIndex => _oppositePlayerInfo,
			RightPlayerIndex => _previousPlayerInfo,
			_ => null
		};
	}

	private static CardPose2D GetReceivePose(Control hand, CardControl card)
	{
		return hand switch
		{
			MainHandLayout mainHand => mainHand.GetCurrentReceivePose(card),
			OtherHandLayout otherHand => otherHand.GetCurrentReceivePose(card),
			_ => throw new InvalidOperationException("Unsupported hand layout type.")
		};
	}

	private static void ReceiveCard(Control hand, CardControl card)
	{
		switch (hand)
		{
			case MainHandLayout mainHand:
				mainHand.ReceiveCard(card);
				break;
			case OtherHandLayout otherHand:
				otherHand.ReceiveCard(card);
				break;
		}
	}

	private static int GetCardCount(Control hand)
	{
		return hand switch
		{
			MainHandLayout mainHand when IsInstanceValid(mainHand) => mainHand.Cards.Count,
			OtherHandLayout otherHand when IsInstanceValid(otherHand) => otherHand.Cards.Count,
			_ => 0
		};
	}

	private bool TryPlayOpponentCard(
		int playerIndex,
		OtherHandLayout hand,
		int cardIndex,
		CardData cardData,
		Action<CardControl> completed = null)
	{
		return hand is not null &&
			IsInstanceValid(hand) &&
				hand.TryPlayCard(
				cardIndex,
				cardData,
				playedCard =>
				{
					HandleOpponentPlayCompleted(playerIndex, playedCard);
					completed?.Invoke(playedCard);
				}
			);
	}

	private void HandleOpponentPlayCompleted(int playerIndex, CardControl playedCard)
	{
		if (playerIndex != RightPlayerIndex ||
			playedCard is null ||
			!IsInstanceValid(playedCard))
		{
			return;
		}

		_rightPlayerPlayCompleted = true;
		if (IsCollectingTrick)
		{
			TryPrepareCollectTrickCards(_collectGeneration);
			// Collection owns the play gate until all of its flights finish. The
			// completion path re-opens it after ResetCollectTrickState().
			return;
		}

		SetMainPlayerPlayEnabled(true);
	}

	private void SetMainPlayerPlayEnabled(bool enabled)
	{
		CanMainPlayerPlay = enabled && !IsDealing && !IsCollectingTrick;
	}

	private T Resolve<T>(T current, NodePath fallbackPath) where T : Node
	{
		return current is not null && IsInstanceValid(current)
			? current
			: GetNodeOrNull<T>(fallbackPath);
	}
}
