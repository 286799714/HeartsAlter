using System;
using Godot;

namespace HeartsAlter.Scripts.InGame.Card;

public partial class CardInteraction : Control
{
	private Card _card = null!;
	
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
	}
	
	public void Bind(Card card)
	{
		ArgumentNullException.ThrowIfNull(card);
		_card = card;
	}
	
	public override void _Process(double delta)
	{
	}
	
	public override void _GuiInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton
		    {
			    ButtonIndex: MouseButton.Left,
			    Pressed: false
		    }) return;
		
		_card._visual.ToggleFace();
		AcceptEvent();
	}
}