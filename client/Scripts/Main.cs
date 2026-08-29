using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts;

/// <summary>Offline wiring used to preview dealing and four-seat play.</summary>
public partial class Main : Control
{
	[Export]
	private Table _table = null!;

	[Export]
	private Button _startDealButton = null!;

	[Export]
	public int DemoHandSize = 13;

	[Export]
	public float OpponentPlayInterval = 0.7f;

	[Export(PropertyHint.Range, "1,32,1,or_greater")]
	public int DemoScoreDeltaMin = 1;

	[Export(PropertyHint.Range, "1,32,1,or_greater")]
	public int DemoScoreDeltaMax = 6;

	private readonly RandomNumberGenerator _random = new();
	private int _demoGeneration;

	public override void _Ready()
	{
		if (_table is null || !IsInstanceValid(_table))
			_table = GetNodeOrNull<Table>("Table");
		if (_startDealButton is null || !IsInstanceValid(_startDealButton))
			_startDealButton = GetNodeOrNull<Button>("StartDealButton");

		_random.Randomize();

		if (_table is not null && IsInstanceValid(_table))
			_table.MainPlayerCardPlayRequested += HandleMainPlayerCardPlayRequested;
		if (_startDealButton is not null && IsInstanceValid(_startDealButton))
			_startDealButton.Pressed += HandleStartDealPressed;
	}

	public override void _ExitTree()
	{
		_demoGeneration++;

		if (_table is not null && IsInstanceValid(_table))
			_table.MainPlayerCardPlayRequested -= HandleMainPlayerCardPlayRequested;
		if (_startDealButton is not null && IsInstanceValid(_startDealButton))
			_startDealButton.Pressed -= HandleStartDealPressed;
	}

	private void HandleStartDealPressed()
	{
		if (_table is null || !IsInstanceValid(_table))
			return;

		_demoGeneration++;
		_table.StartDeal(CreateShuffledMainHand());
	}

	private void HandleMainPlayerCardPlayRequested(int cardIndex, CardData cardData)
	{
		if (_table is null ||
			!IsInstanceValid(_table) ||
			!_table.PlayMainPlayerCard(cardIndex))
		{
			return;
		}

		int generation = _demoGeneration;
		_ = PlayOpponentTurnsAsync(generation);
	}

	private async Task PlayOpponentTurnsAsync(int generation)
	{
		for (int playerIndex = Table.LeftPlayerIndex;
			playerIndex <= Table.RightPlayerIndex;
			playerIndex++)
		{
			if (OpponentPlayInterval > 0.0f)
			{
				SceneTreeTimer timer = GetTree().CreateTimer(OpponentPlayInterval);
				await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
			}

			if (generation != _demoGeneration ||
				!IsInsideTree() ||
				_table is null ||
				!IsInstanceValid(_table))
			{
				return;
			}

			int cardCount = _table.GetPlayerCardCount(playerIndex);
			if (cardCount <= 0)
				continue;

			int randomCardIndex = _random.RandiRange(0, cardCount - 1);
			_table.PlayCard(
				playerIndex,
				randomCardIndex,
				CreateRandomCardData()
			);
		}

		if (generation != _demoGeneration ||
			!IsInsideTree() ||
			_table is null ||
			!IsInstanceValid(_table))
		{
			return;
		}

		int collectingPlayerIndex = _random.RandiRange(
			Table.MainPlayerIndex,
			Table.RightPlayerIndex
		);
		int minimumScoreDelta = Mathf.Max(1, DemoScoreDeltaMin);
		int maximumScoreDelta = Mathf.Max(minimumScoreDelta, DemoScoreDeltaMax);
		int scoreDelta = _random.RandiRange(minimumScoreDelta, maximumScoreDelta);
		_table.CollectTrick(
			collectingPlayerIndex,
			scoreDelta
		);
	}

	private CardData[] CreateShuffledMainHand()
	{
		List<CardData> deck = new(52);
		for (PokerSuit suit = PokerSuit.Spade; suit <= PokerSuit.Club; suit++)
		{
			for (PokerRank rank = PokerRank.Two; rank <= PokerRank.Ace; rank++)
				deck.Add(new CardData(suit, rank));
		}

		int handSize = Mathf.Clamp(DemoHandSize, 0, 13);
		CardData[] hand = new CardData[handSize];
		for (int i = 0; i < hand.Length; i++)
		{
			int index = _random.RandiRange(0, deck.Count - 1);
			hand[i] = deck[index];
			deck.RemoveAt(index);
		}

		return hand;
	}

	private CardData CreateRandomCardData()
	{
		return new CardData(
			(PokerSuit)_random.RandiRange(
				(int)PokerSuit.Spade,
				(int)PokerSuit.Club
			),
			(PokerRank)_random.RandiRange(
				(int)PokerRank.Two,
				(int)PokerRank.Ace
			)
		);
	}
}
