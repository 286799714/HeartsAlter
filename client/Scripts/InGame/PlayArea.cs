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
	[Export]
	public float PlayedCardHeight = 117.0f;

	public CardControl PlayedCard { get; private set; }

	public override void _Ready()
	{
		ChildExitingTree += HandleChildExitingTree;

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
		PlayedCard = null;
	}

	/// <summary>
	/// Returns the face-up pose at the center of this area. The area's own
	/// rotation is part of the transform, so all four seats can reuse this scene.
	/// </summary>
	public CardPose2D GetReceivePose(CardControl card)
	{
		ArgumentNullException.ThrowIfNull(card);
		if (!IsInstanceValid(card))
			throw new ArgumentException("Card must be a valid Godot instance.", nameof(card));

		Vector2 targetSize = CalculateCardSize(card);
		Vector2 localPosition = (Size - targetSize) * 0.5f;
		Transform2D localTransform = new(0.0f, localPosition);

		return new CardPose2D(
			GetGlobalTransformWithCanvas() * localTransform,
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
		Vector2 targetPosition = (Size - targetSize) * 0.5f;

		card.SetAnchorsPreset(LayoutPreset.TopLeft, keepOffsets: false);
		card.Rotation = 0.0f;
		card.Scale = Vector2.One;
		card.ResizeToSize(targetSize);
		card.SetSelectionLift(0.0f);
		card.SetLayoutPosition(targetPosition);
		card.SetFace(front: true);
		card.ZIndex = 1;

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
		float targetHeight = PlayedCardHeight > 0.0f
			? PlayedCardHeight
			: Mathf.Max(1.0f, Size.Y);
		float sourceHeight = Mathf.Max(1.0f, card.CardHeight);
		return new Vector2(card.CardWidth * targetHeight / sourceHeight, targetHeight);
	}

	private void HandleChildExitingTree(Node node)
	{
		if (node is CardControl card && ReferenceEquals(card, PlayedCard))
			PlayedCard = null;
	}
}
