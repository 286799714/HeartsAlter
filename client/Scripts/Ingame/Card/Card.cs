using System;
using Godot;

namespace HeartsAlter.Scripts.InGame.Card;

public partial class Card : Control
{
	[Export]
	internal CardVisual _visual;

	[Export] 
	internal CardInteraction _cardInteraction;
	
	public CardData Data { get; private set; }
	
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_visual.Bind(this);
		_cardInteraction.Bind(this);
	}

	public void Setup(CardData data)
	{
		Data = data;
		_visual.Setup(_visual.IsFront);
	}
	
	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
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