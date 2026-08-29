using System;
using Godot;

namespace HeartsAlter.Scripts.InGame.Card;

public partial class CardInteraction : Control
{
	private CardControl _card = null!;

	/// <summary>
	/// Raised as soon as the primary button is pressed over this card.
	/// Selection belongs to the owning hand layout; this layer only reports
	/// the interaction and deliberately does not mutate the card face.
	/// </summary>
	[Signal]
	public delegate void ClickedEventHandler(Vector2 canvasPosition);
	
	public void Bind(CardControl card)
	{
		ArgumentNullException.ThrowIfNull(card);
		_card = card;
	}

	public override void _GuiInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton mouseEvent ||
			mouseEvent.ButtonIndex != MouseButton.Left ||
			!mouseEvent.Pressed)
		{
			return;
		}

		if (_card is null || !IsInstanceValid(_card))
			return;

		Vector2 canvasPosition = GetGlobalTransformWithCanvas() * mouseEvent.Position;
		EmitSignal(SignalName.Clicked, canvasPosition);
		AcceptEvent();
	}
}
