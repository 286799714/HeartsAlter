using Godot;

namespace HeartsAlter.Scripts.InGame.Card;

/// <summary>
/// Describes where and how a card should be presented on a shared canvas.
/// The transform maps the card's local coordinates into its viewport's canvas
/// coordinates; <see cref="Size"/> is kept separately because a Control's
/// untransformed size is not part of its <see cref="Transform2D"/>.
/// </summary>
public readonly record struct CardPose2D(
	Transform2D CanvasTransform,
	Vector2 Size,
	bool IsFaceUp
);
