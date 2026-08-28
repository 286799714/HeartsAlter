using System;
using System.Collections.Generic;
using Godot;
using CardPose2D = HeartsAlter.Scripts.InGame.Card.CardPose2D;
using CardData = HeartsAlter.Scripts.InGame.Card.CardData;
using PlayingCard = HeartsAlter.Scripts.InGame.Card.Card;
using PokerRank = HeartsAlter.Scripts.InGame.Card.PokerRank;
using PokerSuit = HeartsAlter.Scripts.InGame.Card.PokerSuit;
using HeartsAlter.Scripts.InGame.CardDeck;
using HeartsAlter.Scripts.InGame.AnimationLayer;

/// <summary>
/// Small offline table controller used by the scene preview.  It coordinates
/// the deck, hand, and shared animation layer; game/network state can replace
/// this controller later without changing those reusable components.
/// </summary>
public partial class Table : Control
{
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
	private Button _drawCardButton = null!;

	/// <summary>
	/// Number of cards shown when a scene starts with an empty deck.
	/// </summary>
	[Export]
	public int StartingDeckCount = 52;

	/// <summary>
	/// Capacity used by the thickness calculation for the demo deck.
	/// </summary>
	[Export]
	public int StartingDeckCapacity = 52;

	/// <summary>
	/// Keyboard key accepted by the draw-card shortcut.  Space is deliberately
	/// also printed on the button in Table.tscn so the affordance is discoverable.
	/// </summary>
	[Export]
	public Key DrawKey = Key.Space;

	private readonly RandomNumberGenerator _random = new();
	private readonly List<Control> _handLayouts = new();
	private int _drawsInFlight;
	private int _nextHandIndex;

	/// <summary>
	/// The deck count at the last frame.  Exposing it makes the preview easy to
	/// inspect from a debugger and keeps callers from reaching into private
	/// scene fields.
	/// </summary>
	public int RemainingCards =>
		_cardDeck is not null && GodotObject.IsInstanceValid(_cardDeck)
			? _cardDeck.CardCount
			: 0;

	public bool IsDrawInProgress => _drawsInFlight > 0;

	/// <summary>
	/// Number of transient cards that have been accepted by the animation layer
	/// but have not reached the hand yet.  Draws are intentionally allowed to
	/// overlap, so this is a count rather than a single busy flag.
	/// </summary>
	public int ActiveDrawCount => _drawsInFlight;

	public override void _Ready()
	{
		ResolveSceneReferences();
		_nextHandIndex = 0;
		_random.Randomize();
		InitializeDeck();

		if (_drawCardButton is not null &&
			GodotObject.IsInstanceValid(_drawCardButton))
		{
			_drawCardButton.Pressed += HandleDrawButtonPressed;
		}

		UpdateDrawButtonState();
	}

	public override void _ExitTree()
	{
		if (_drawCardButton is not null &&
			GodotObject.IsInstanceValid(_drawCardButton))
		{
			_drawCardButton.Pressed -= HandleDrawButtonPressed;
		}

		_drawsInFlight = 0;
		_nextHandIndex = 0;
		_handLayouts.Clear();
	}

	/// <summary>
	/// Handles the keyboard shortcut without consuming key events intended for
	/// text controls.  Echo events are ignored, while separate key presses are
	/// allowed to enqueue independent overlapping draws.
	/// </summary>
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventKey keyEvent ||
			!keyEvent.Pressed ||
			keyEvent.Echo ||
			!IsDrawKey(keyEvent))
		{
			return;
		}

		GetViewport()?.SetInputAsHandled();
		TryDrawCard();
	}

	/// <summary>
	/// Button-friendly entry point.  Godot signal handlers are void-returning,
	/// while code-driven callers can use <see cref="TryDrawCard"/> for a result.
	/// </summary>
	public void DrawCard() => TryDrawCard();

	/// <summary>
	/// Starts one draw animation when the table is ready. Other cards may still
	/// be flying; each accepted draw owns an independent animation lane.
	/// </summary>
	/// <returns>True if a transient card was created and animated.</returns>
	public bool TryDrawCard()
	{
		if (!CanDraw())
			return false;

		if (!TryGetNextHand(out Control destination, out int destinationIndex))
			return false;

		if (!_cardDeck.TryGetTopPose(out CardPose2D sourcePose))
			return false;

		if (!_cardDeck.TryDuplicateTopCard(out PlayingCard card) ||
			card is null ||
			!GodotObject.IsInstanceValid(card))
		{
			return false;
		}

		CardData data = new(
			(PokerSuit)_random.RandiRange(
				(int)PokerSuit.Spade,
				(int)PokerSuit.Club
			),
			(PokerRank)_random.RandiRange(
				(int)PokerRank.Two,
				(int)PokerRank.Ace
			)
		);

		// AddChild runs Card._Ready before Setup, allowing the visual and
		// interaction children to bind correctly even for a duplicated scene.
		_animationLayer.AddChild(card);
		card.Setup(data, startFaceUp: sourcePose.IsFaceUp);

		CardPose2D receivePose;
		try
		{
			receivePose = GetCurrentReceivePose(destination, card);
		}
		catch (Exception exception)
		{
			GD.PushWarning($"Unable to calculate the hand receive pose: {exception.Message}");
			card.QueueFree();
			return false;
		}

		_drawsInFlight++;
		bool started = _animationLayer.PlayCardToPose(
			card,
			sourcePose,
			receivePose,
			drawnCard => HandleCardAnimationCompleted(destination, drawnCard)
		);

		if (!started)
		{
			_drawsInFlight = Math.Max(0, _drawsInFlight - 1);
			if (GodotObject.IsInstanceValid(card))
				card.QueueFree();
			UpdateDrawButtonState();
			return false;
		}

		// Reserve the next seat only after the animation layer accepts this draw.
		// The selected destination is captured by the completion callback above,
		// so overlapping flights cannot be redirected by a later button press.
		_nextHandIndex = (destinationIndex + 1) % _handLayouts.Count;

		// Consume the deck slot once the animation has been accepted.  Capturing
		// sourcePose first keeps the card flying from the old top position even
		// when this draw empties the deck and hides its authored top card.
		_cardDeck.ChangeCardCount(_cardDeck.CardCount - 1);
		UpdateDrawButtonState();
		return true;
	}

	private void HandleDrawButtonPressed() => TryDrawCard();

	private void HandleCardAnimationCompleted(Control destination, PlayingCard card)
	{
		try
		{
			if (GodotObject.IsInstanceValid(card) &&
				IsValidHandLayout(destination))
			{
				ReceiveCard(destination, card);
			}
			else if (GodotObject.IsInstanceValid(card))
			{
				// If a table was partially instanced without a hand, avoid leaking
				// the transient card on the animation layer.
				card.QueueFree();
			}
		}
		finally
		{
			_drawsInFlight = Math.Max(0, _drawsInFlight - 1);
			UpdateDrawButtonState();
		}
	}

	private bool CanDraw()
	{
		return _cardDeck is not null &&
			GodotObject.IsInstanceValid(_cardDeck) &&
			_handLayouts.Count > 0 &&
			_animationLayer is not null &&
			GodotObject.IsInstanceValid(_animationLayer) &&
			_animationLayer.IsInsideTree() &&
			_cardDeck.CardCount > 0;
	}

	private bool IsDrawKey(InputEventKey keyEvent)
	{
		return keyEvent.Keycode == DrawKey ||
			keyEvent.PhysicalKeycode == DrawKey;
	}

	private void ResolveSceneReferences()
	{
		if (_cardDeck is null || !GodotObject.IsInstanceValid(_cardDeck))
			_cardDeck = GetNodeOrNull<CardDeck>("CardDeck");

		if (_mainHandLayout is null || !GodotObject.IsInstanceValid(_mainHandLayout))
			_mainHandLayout = GetNodeOrNull<MainHandLayout>("MainHandLayout");

		if (_otherHandLayout is null || !GodotObject.IsInstanceValid(_otherHandLayout))
			_otherHandLayout = GetNodeOrNull<OtherHandLayout>("OtherHandLayout");

		if (_otherHandLayout2 is null || !GodotObject.IsInstanceValid(_otherHandLayout2))
			_otherHandLayout2 = GetNodeOrNull<OtherHandLayout>("OtherHandLayout2");

		if (_otherHandLayout3 is null || !GodotObject.IsInstanceValid(_otherHandLayout3))
			_otherHandLayout3 = GetNodeOrNull<OtherHandLayout>("OtherHandLayout3");

		if (_animationLayer is null || !GodotObject.IsInstanceValid(_animationLayer))
			_animationLayer = GetNodeOrNull<AnimationLayer>("AnimationLayer");

		if (_drawCardButton is null || !GodotObject.IsInstanceValid(_drawCardButton))
			_drawCardButton = GetNodeOrNull<Button>("DrawCardButton");

		_handLayouts.Clear();
		AddHandLayout(_mainHandLayout);
		AddHandLayout(_otherHandLayout);
		AddHandLayout(_otherHandLayout2);
		AddHandLayout(_otherHandLayout3);
	}

	private void AddHandLayout(Control handLayout)
	{
		if (IsValidHandLayout(handLayout) && !_handLayouts.Contains(handLayout))
			_handLayouts.Add(handLayout);
	}

	private bool TryGetNextHand(out Control destination, out int destinationIndex)
	{
		destination = null!;
		destinationIndex = -1;

		int handCount = _handLayouts.Count;
		if (handCount == 0)
			return false;

		int startIndex = _nextHandIndex % handCount;
		if (startIndex < 0)
			startIndex += handCount;

		for (int offset = 0; offset < handCount; offset++)
		{
			int index = (startIndex + offset) % handCount;
			Control candidate = _handLayouts[index];
			if (!IsValidHandLayout(candidate))
				continue;

			destination = candidate;
			destinationIndex = index;
			return true;
		}

		return false;
	}

	private static bool IsValidHandLayout(Control handLayout)
	{
		return handLayout is MainHandLayout or OtherHandLayout &&
			GodotObject.IsInstanceValid(handLayout) &&
			handLayout.IsInsideTree();
	}

	private static CardPose2D GetCurrentReceivePose(Control handLayout, PlayingCard card)
	{
		return handLayout switch
		{
			MainHandLayout mainHand => mainHand.GetCurrentReceivePose(card),
			OtherHandLayout otherHand => otherHand.GetCurrentReceivePose(card),
			_ => throw new InvalidOperationException("Unsupported hand layout type.")
		};
	}

	private static void ReceiveCard(Control handLayout, PlayingCard card)
	{
		switch (handLayout)
		{
			case MainHandLayout mainHand:
				mainHand.ReceiveCard(card);
				break;
			case OtherHandLayout otherHand:
				otherHand.ReceiveCard(card);
				break;
			default:
				throw new InvalidOperationException("Unsupported hand layout type.");
		}
	}

	private void InitializeDeck()
	{
		if (_cardDeck is null || !GodotObject.IsInstanceValid(_cardDeck))
			return;

		int capacity = Math.Max(1, StartingDeckCapacity);
		_cardDeck.ChangeMaxCardCount(capacity);

		// A scene-authored non-empty count is respected.  The stock Table scene
		// starts at zero and is filled to a standard 52-card deck here.
		if (_cardDeck.CardCount <= 0 && StartingDeckCount > 0)
		{
			_cardDeck.ChangeCardCount(
				Math.Min(StartingDeckCount, _cardDeck.MaxCardCount)
			);
		}
	}

	private void UpdateDrawButtonState()
	{
		if (_drawCardButton is null || !GodotObject.IsInstanceValid(_drawCardButton))
			return;

		// Keep the control enabled while cards are flying: a subsequent press
		// should create another independent flight.  Only an empty deck disables
		// the trigger.
		_drawCardButton.Disabled = RemainingCards <= 0;
	}
}
