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
		_potAmount.Text = state.pot.ToString();
		// Trick labels follow presented plays and completed collect animations;
		// state patches can already describe a later trick while cards still fly.
	}

	private void SetInGameStatus(int pot, int completedTricks, PokerSuit? leadSuit)
	{
		_potAmount.Text = pot.ToString();
		SetTrickStatus(completedTricks, leadSuit);
	}

	private void SetTrickStatus(int completedTricks, PokerSuit? leadSuit)
	{
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
