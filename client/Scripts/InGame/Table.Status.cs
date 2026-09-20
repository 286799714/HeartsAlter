using System;
using Godot;
using HeartsAlter.Scripts.Generated;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.InGame;

public partial class Table
{
	private Label _potAmount;
	private Label _currentTrickNumber;
	private Label _leadSuit;

	private void BindInGameStatusUi()
	{
		_potAmount = GetNode<Label>("%奖池数量");
		_currentTrickNumber = GetNode<Label>("%当前回合数");
		_leadSuit = GetNode<Label>("%本墩花色");
		SetInGameStatus(0, 0, null);
	}

	private void UpdateInGameStatus(MyRoomState state)
	{
		PokerSuit? leadSuit = state.trick?.Count > 0 ? state.leadSuit switch
		{
			"clubs" or "club" => PokerSuit.Club,
			"diamonds" or "diamond" => PokerSuit.Diamond,
			"hearts" or "heart" => PokerSuit.Heart,
			"spades" or "spade" => PokerSuit.Spade,
			_ => null,
		} : null;
		SetInGameStatus(state.pot, state.trickNumber, leadSuit);
	}

	private void SetInGameStatus(int pot, int completedTricks, PokerSuit? leadSuit)
	{
		_potAmount.Text = pot.ToString();
		// The server counts completed tricks; roundNumber counts whole games.
		_currentTrickNumber.Text = (Math.Clamp(completedTricks, 0, 12) + 1).ToString();
		string symbol = leadSuit switch
		{
			PokerSuit.Club => "♣",
			PokerSuit.Diamond => "♦",
			PokerSuit.Heart => "♥",
			PokerSuit.Spade => "♠",
			_ => "—",
		};
		_leadSuit.Text = $"本墩花色  {symbol}";
	}
}
