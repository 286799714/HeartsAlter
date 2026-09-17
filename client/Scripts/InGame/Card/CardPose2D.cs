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
)
{
	/// <summary>
	/// Captures the rendered pose, including Control pixel snapping. Godot's
	/// GetGlobalTransformWithCanvas returns the unsnapped logical transform.
	/// </summary>
	public static Transform2D GetRenderedCanvasTransform(CanvasItem item)
	{
		Transform2D local = item.GetTransform();
		if (item is Control control &&
			control.GetViewport().GuiSnapControlsToPixels &&
			Mathf.Abs(Mathf.Sin(control.Rotation * 4.0f)) < 0.00001f)
		{
			local.Origin = (local.Origin + Vector2.One * 0.5f).Floor();
		}

		return !item.TopLevel && item.GetParent() is CanvasItem parent
			? GetRenderedCanvasTransform(parent) * local
			: item.GetCanvasTransform() * local;
	}
}
