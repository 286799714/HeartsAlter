using System;
using Godot;

namespace HeartsAlter.Scripts.InGame.Card;

public partial class Card : Control
{
	public const float DefaultWidth = 120.0f;
	public const float DefaultHeight = 167.0f;

	[Export]
	internal CardVisual _visual;

	[Export] 
	internal CardInteraction _cardInteraction;

	/// <summary>
	/// Emitted when the card's interaction layer receives a click.
	/// Consumers (for example MainHandLayout) can subscribe without knowing
	/// about the visual/interaction child nodes.
	/// </summary>
	[Signal]
	public delegate void ClickedEventHandler();
	
	public CardData Data { get; private set; }

	/// <summary>
	/// Current untransformed width of the card. Hand layouts use this interface
	/// instead of depending on the authored scene offsets.
	/// </summary>
	public float CardWidth => ResolveDimension(
		Size.X,
		CustomMinimumSize.X,
		DefaultWidth
	);

	/// <summary>
	/// Current untransformed height (the portrait card's long side). The value
	/// excludes <see cref="CanvasItem.Scale"/>.
	/// </summary>
	public float CardHeight => ResolveDimension(
		Size.Y,
		CustomMinimumSize.Y,
		DefaultHeight
	);

	/// <summary>
	/// The position assigned by a hand layout before the selection lift is
	/// applied. Keeping this separate from <see cref="Position"/> allows the
	/// layout and selection animations to run at the same time.
	/// </summary>
	public Vector2 LayoutPosition { get; private set; }

	/// <summary>
	/// Vertical lift, in pixels, currently applied to this card. Positive
	/// values move the rendered card upwards.
	/// </summary>
	public float SelectionLift { get; private set; }

	/// <summary>
	/// Public access to the interaction child for callers that need to inspect
	/// or configure it. Clicks are normally consumed through <see cref="Clicked"/>.
	/// </summary>
	public CardInteraction Interaction => _cardInteraction;

	private bool _interactionForwardingBound;
	
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		// Preserve a scene-authored initial position until a layout takes over.
		LayoutPosition = Position;
		SelectionLift = 0.0f;

		_visual.Bind(this);
		_cardInteraction.Bind(this);
		if (!_interactionForwardingBound)
		{
			_cardInteraction.Clicked += ForwardInteractionClick;
			_interactionForwardingBound = true;
		}
	}

	public void Setup(CardData data)
	{
		Setup(data, _visual.IsFront);
	}

	public void Setup(CardData data, bool startFaceUp)
	{
		Data = data;
		_visual.Setup(startFaceUp);
	}

	/// <summary>
	/// Resizes the card proportionally so its long side equals
	/// <paramref name="targetHeight"/>. Non-positive or non-finite targets are
	/// ignored, leaving the current dimensions unchanged.
	/// </summary>
	public void ResizeToHeight(float targetHeight)
	{
		if (!float.IsFinite(targetHeight) || targetHeight <= 0.0f)
			return;

		float currentHeight = CardHeight;
		if (currentHeight <= 0.0f)
			return;
		if (Mathf.IsEqualApprox(currentHeight, targetHeight))
			return;

		float aspectRatio = CardWidth / currentHeight;
		Vector2 targetSize = new(targetHeight * aspectRatio, targetHeight);
		RelaxSizeConstraintsFor(targetSize);
		Size = targetSize;
	}

	/// <summary>
	/// Assigns the card's unlifted layout position and immediately applies the
	/// current selection lift to the actual Control position.
	/// </summary>
	public void SetLayoutPosition(Vector2 position)
	{
		LayoutPosition = position;
		ApplyAnimatedPosition();
	}

	/// <summary>
	/// Assigns the selection lift and immediately applies it to the actual
	/// Control position. This is intentionally independent from layout motion
	/// so either animation can update without clobbering the other.
	/// </summary>
	public void SetSelectionLift(float lift)
	{
		SelectionLift = Mathf.Max(0.0f, lift);
		ApplyAnimatedPosition();
	}

	/// <summary>
	/// Captures the current Control position as an unlifted layout position.
	/// Useful when a card is reparented after flying to the hand.
	/// </summary>
	public void CaptureCurrentPositionAsLayout()
	{
		LayoutPosition = Position + new Vector2(0.0f, SelectionLift);
	}

	private void ApplyAnimatedPosition()
	{
		Position = LayoutPosition + new Vector2(0.0f, -SelectionLift);
	}

	private void ForwardInteractionClick()
	{
		EmitSignal(SignalName.Clicked);
	}

	private static float ResolveDimension(
		float size,
		float minimumSize,
		float fallback)
	{
		if (size > 0.0f)
			return size;
		if (minimumSize > 0.0f)
			return minimumSize;
		return fallback;
	}

	private void RelaxSizeConstraintsFor(Vector2 targetSize)
	{
		Vector2 minimumSize = CustomMinimumSize;
		minimumSize.X = Mathf.Min(minimumSize.X, targetSize.X);
		minimumSize.Y = Mathf.Min(minimumSize.Y, targetSize.Y);
		CustomMinimumSize = minimumSize;

		Vector2 maximumSize = CustomMaximumSize;
		if (maximumSize.X > 0.0f)
			maximumSize.X = Mathf.Max(maximumSize.X, targetSize.X);
		if (maximumSize.Y > 0.0f)
			maximumSize.Y = Mathf.Max(maximumSize.Y, targetSize.Y);
		CustomMaximumSize = maximumSize;
	}

}

public enum PokerSuit
{
	Spade,
	Heart,
	Diamond,
	Club
}

public enum PokerRank
{
	Two = 2,
	Three = 3,
	Four = 4,
	Five = 5,
	Six = 6,
	Seven = 7,
	Eight = 8,
	Nine = 9,
	Ten = 10,
	Jack = 11,
	Queen = 12,
	King = 13,
	Ace = 14
}

public enum PokerColor
{
	Black,
	Red
}
	
public readonly record struct CardData(
	PokerSuit Suit,
	PokerRank Rank
);

public static class PokerRankTypeExtensions
{
	public static bool IsNumber(this PokerRank rank)
		=> rank is >= PokerRank.Two and <= PokerRank.Ten;

	public static bool IsFace(this PokerRank rank)
		=> rank is PokerRank.Jack
			or PokerRank.Queen
			or PokerRank.King;

	public static PokerColor GetColor(this PokerSuit suit)
	{
		return suit switch
		{
			PokerSuit.Heart or PokerSuit.Diamond
				=> PokerColor.Red,

			PokerSuit.Spade or PokerSuit.Club
				=> PokerColor.Black,
			
			_ => throw new ArgumentOutOfRangeException(nameof(suit), suit, null)
		};
	}
}
