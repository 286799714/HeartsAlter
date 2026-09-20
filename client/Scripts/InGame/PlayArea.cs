using System;
using Godot;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.InGame;

/// <summary>
/// Owns the card currently shown in one player's central play area and exposes
/// its canvas-space destination pose to the shared animation layer.
/// </summary>
public partial class PlayArea : Control
{
	public CardControl PlayedCard { get; private set; }

	public override void _Ready()
	{
		ChildExitingTree += HandleChildExitingTree;
		Resized += LayoutPlayedCard;

		foreach (Node child in GetChildren())
		{
			if (child is CardControl card)
			{
				PlayedCard = card;
				break;
			}
		}
	}

	public override void _ExitTree()
	{
		ChildExitingTree -= HandleChildExitingTree;
		Resized -= LayoutPlayedCard;
		PlayedCard = null;
	}

	/// <summary>
	/// Returns the face-up pose with the card pivot aligned to this area's pivot.
	/// The area's own rotation is part of the transform, so all four seats can
	/// reuse this scene.
	/// </summary>
	public CardPose2D GetReceivePose(CardControl card)
	{
		ArgumentNullException.ThrowIfNull(card);
		if (!IsInstanceValid(card))
			throw new ArgumentException("Card must be a valid Godot instance.", nameof(card));

		Vector2 targetSize = CalculateCardSize(card);
		Vector2 localPosition = CalculateCardPosition(targetSize);
		if (GetViewport().GuiSnapControlsToPixels)
			localPosition = (localPosition + Vector2.One * 0.5f).Floor();
		Transform2D localTransform = new(0.0f, localPosition);

		return new CardPose2D(
			CardPose2D.GetRenderedCanvasTransform(this) * localTransform,
			targetSize,
			IsFaceUp: true
		);
	}

	/// <summary>
	/// Places a completed flight in this area. A previous trick card in the same
	/// seat is discarded when the replacement arrives.
	/// </summary>
	public void ReceiveCard(CardControl card)
	{
		ArgumentNullException.ThrowIfNull(card);
		if (!IsInstanceValid(card))
			return;

		if (PlayedCard is not null &&
			IsInstanceValid(PlayedCard) &&
			!ReferenceEquals(PlayedCard, card))
		{
			PlayedCard.QueueFree();
		}

		if (card.GetParent() != this)
		{
			if (card.GetParent() is not null)
				card.Reparent(this, keepGlobalTransform: false);
			else
				AddChild(card);
		}

		Vector2 targetSize = CalculateCardSize(card);

		card.SetAnchorsPreset(LayoutPreset.TopLeft, keepOffsets: false);
		card.Rotation = 0.0f;
		card.Scale = Vector2.One;
		card.ResizeToSize(targetSize);
		card.PivotOffset = Vector2.Zero;
		card.PivotOffsetRatio = Vector2.One * 0.5f;
		card.SetSelectionLift(0.0f);
		card.SetLayoutPosition(CalculateCardPosition(targetSize));
		card.SetFace(front: true);
		// Discard the hand/flight order and inherit the editor-authored play area
		// order. Arrival time must not change which seat's card covers another.
		card.ZAsRelative = true;
		card.ZIndex = 0;

		if (card.Interaction is not null && IsInstanceValid(card.Interaction))
			card.Interaction.MouseFilter = MouseFilterEnum.Ignore;

		PlayedCard = card;
	}

	public void ClearCard()
	{
		CardControl card = PlayedCard;
		PlayedCard = null;

		if (card is not null && IsInstanceValid(card))
			card.QueueFree();
	}

	private Vector2 CalculateCardSize(CardControl card)
	{
		// The editor-authored area controls the played card's size. Fit both
		// dimensions without stretching the artwork when the aspect ratio changes.
		Vector2 sourceSize = new(card.CardWidth, card.CardHeight);
		Vector2 availableSize = Size.Max(Vector2.One);
		float fit = Mathf.Min(availableSize.X / sourceSize.X, availableSize.Y / sourceSize.Y);
		return sourceSize * fit;
	}

	private Vector2 CalculateCardPosition(Vector2 cardSize) =>
		// The scene's proportional pivot is added to its fixed pixel offset.
		GetCombinedPivotOffset() - cardSize * 0.5f;

	private void LayoutPlayedCard()
	{
		if (PlayedCard is not CardControl card || !IsInstanceValid(card) || card.GetParent() != this)
			return;

		Vector2 targetSize = CalculateCardSize(card);
		card.ResizeToSize(targetSize);
		card.SetLayoutPosition(CalculateCardPosition(targetSize));
	}

	private void HandleChildExitingTree(Node node)
	{
		if (node is CardControl card && ReferenceEquals(card, PlayedCard))
			PlayedCard = null;
	}
}
