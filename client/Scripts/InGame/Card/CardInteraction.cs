using System;
using Godot;

namespace HeartsAlter.Scripts.InGame.Card;

public partial class CardInteraction : Control
{
	private CardControl _card = null!;

	/// <summary>
	/// Raised after a primary-button click is released over this card.
	/// Selection belongs to the owning hand layout; this layer only reports
	/// the interaction and deliberately does not mutate the card face.
	/// </summary>
	[Signal]
	public delegate void ClickedEventHandler();
	
	public void Bind(CardControl card)
	{
		ArgumentNullException.ThrowIfNull(card);
		_card = card;
	}
	
	public override void _GuiInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton
		    {
			    ButtonIndex: MouseButton.Left,
			    Pressed: false
		    }) return;

		if (_card is null || !IsInstanceValid(_card))
			return;
		
		EmitSignal(SignalName.Clicked);
		AcceptEvent();
	}
}
