using Godot;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts;

public partial class Main : Control
{
	[Export]
	private PackedScene _cardScene = null!;

	[Export]
	private MainHandLayout _mainHandLayout = null!;

	[Export]
	private Button _addCardButton = null!;

	[Export]
	private Button _arrangeHandButton = null!;

	private readonly RandomNumberGenerator _random = new();
	
	public override void _Ready()
	{
		// Scene exports are assigned by Main.tscn. These fallbacks keep the demo
		// usable when Main is instanced from code or opened as a partial scene.
		if (_cardScene is null || !IsInstanceValid(_cardScene))
			_cardScene = GD.Load<PackedScene>("res://scenes/in_game/Card.tscn");
		if (_mainHandLayout is null || !IsInstanceValid(_mainHandLayout))
			_mainHandLayout = GetNodeOrNull<MainHandLayout>("MainHandLayout");
		if (_addCardButton is null || !IsInstanceValid(_addCardButton))
			_addCardButton = GetNodeOrNull<Button>("AddCardButton");
		if (_arrangeHandButton is null || !IsInstanceValid(_arrangeHandButton))
			_arrangeHandButton = GetNodeOrNull<Button>("ArrangeHandButton");

		_random.Randomize();
		if (_addCardButton is not null && IsInstanceValid(_addCardButton))
			_addCardButton.Pressed += AddRandomCard;
		if (_arrangeHandButton is not null && IsInstanceValid(_arrangeHandButton))
			_arrangeHandButton.Pressed += ArrangeHand;
	}

	public override void _ExitTree()
	{
		if (_addCardButton is not null && IsInstanceValid(_addCardButton))
			_addCardButton.Pressed -= AddRandomCard;
		if (_arrangeHandButton is not null && IsInstanceValid(_arrangeHandButton))
			_arrangeHandButton.Pressed -= ArrangeHand;
	}

	private void ArrangeHand()
	{
		if (_mainHandLayout is not null &&
		    IsInstanceValid(_mainHandLayout))
		{
			_mainHandLayout.ArrangeHand();
		}
	}
	
	private void AddRandomCard()
	{
		if (_cardScene is null ||
		    _mainHandLayout is null ||
		    !IsInstanceValid(_cardScene) ||
		    !IsInstanceValid(_mainHandLayout))
			return;

		CardControl card = _cardScene.Instantiate<CardControl>();

		AddChild(card);

		card.Setup(
			new CardData(
				(PokerSuit)_random.RandiRange(
					(int)PokerSuit.Spade,
					(int)PokerSuit.Club
				),
				(PokerRank)_random.RandiRange(
					(int)PokerRank.Two,
					(int)PokerRank.Ace
				)
			),
			startFaceUp: true
		);

		_mainHandLayout.ReceiveCard(card);
	}
}