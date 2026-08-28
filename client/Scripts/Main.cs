using Godot;
using System;
using HeartsAlter.Scripts.InGame.Card;

public partial class Main : Control
{
	private PackedScene _cardScene = null!;
	
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_cardScene =
			GD.Load<PackedScene>(
				"res://scenes/in_game/Card.tscn"
			);
		
		SpawnCard();
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}
	
	private void SpawnCard()
	{
		Card card = _cardScene.Instantiate<Card>();

		AddChild(card);

		card.Setup(
			new CardData(
				PokerSuit.Heart,
				PokerRank.Ace
			)
		);

		card.Position = new Vector2(300, 200);
	}
}
