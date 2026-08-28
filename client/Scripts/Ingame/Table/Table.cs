using System;
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
	private int _drawsInFlight;

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
			receivePose = _mainHandLayout.GetCurrentReceivePose(card);
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
			HandleCardAnimationCompleted
		);

		if (!started)
		{
			_drawsInFlight = Math.Max(0, _drawsInFlight - 1);
			if (GodotObject.IsInstanceValid(card))
				card.QueueFree();
			UpdateDrawButtonState();
			return false;
		}

		// Consume the deck slot once the animation has been accepted.  Capturing
		// sourcePose first keeps the card flying from the old top position even
		// when this draw empties the deck and hides its authored top card.
		_cardDeck.ChangeCardCount(_cardDeck.CardCount - 1);
		UpdateDrawButtonState();
		return true;
	}

	private void HandleDrawButtonPressed() => TryDrawCard();

	private void HandleCardAnimationCompleted(PlayingCard card)
	{
		try
		{
			if (GodotObject.IsInstanceValid(card) &&
				_mainHandLayout is not null &&
				GodotObject.IsInstanceValid(_mainHandLayout))
			{
				_mainHandLayout.ReceiveCard(card);
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
			_mainHandLayout is not null &&
			GodotObject.IsInstanceValid(_mainHandLayout) &&
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

		if (_animationLayer is null || !GodotObject.IsInstanceValid(_animationLayer))
			_animationLayer = GetNodeOrNull<AnimationLayer>("AnimationLayer");

		if (_drawCardButton is null || !GodotObject.IsInstanceValid(_drawCardButton))
			_drawCardButton = GetNodeOrNull<Button>("DrawCardButton");
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
