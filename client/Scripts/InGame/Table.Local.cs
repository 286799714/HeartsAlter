using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.InGame;

public partial class Table
{
	public event Action<IReadOnlyList<CardData>> MainPlayerPassCardsRequested;
	private int _localPassGeneration;
	private int _localPassFlights;
	public bool IsLocalPassing { get; private set; }
	public bool IsLocalAnimating => IsDealing || IsCollectingTrick || IsLocalPassing ||
		(_animationLayer?.IsAnimating ?? false) || (_mainHandLayout?.IsLayoutAnimating ?? false) ||
		_opponentHands.Any(hand => hand.IsLayoutAnimating);
	public IReadOnlyList<CardData> LocalHand => _mainHandLayout.Cards.Select(card => card.Data).ToArray();
	public IReadOnlyList<CardControl> LocalCardViews => _mainHandLayout.Cards;
	public PlayerInfo GetLocalPlayerInfo(int seat) => GetPlayerInfo(seat);
	public CardControl GetLocalPlayedCard(int seat) => (seat switch
	{
		0 => _mainPlayArea, 1 => _leftPlayArea, 2 => _oppositePlayArea, 3 => _rightPlayArea,
		_ => throw new ArgumentOutOfRangeException(nameof(seat))
	}).PlayedCard;

	/// <summary>Cancel the current exercise while retaining the scene-authored table and layout.</summary>
	public void ResetLocalPresentation()
	{
		RequireLocalTable();
		CancelLocalPassing();
		CancelCollectTrick();
		_dealGeneration++;
		_dealFlights = 0;
		_dealDispatchCompleted = false;
		_dealFinishing = false;
		IsDealing = false;
		_rightPlayerPlayCompleted = false;
		_animationLayer.CancelAnimation(freeCard: true);
		_mainHandLayout.SetPassSelectionEnabled(false);
		_mainHandLayout.ClearPlayableCards();
		_mainHandLayout.SetSelectionEnabled(false);
		SetMainPlayerPlayEnabled(false);
		ClearHandsAndPlayAreas();
		_cardDeck.ChangeCardCount(0);
	}

	public void SetLocalPlayableCards(IEnumerable<CardData> legal, string hint, bool enabled = true)
	{
		RequireLocalTable();
		SetMainPlayerPlayEnabled(enabled);
		_mainHandLayout.SetSelectionEnabled(enabled);
		_mainHandLayout.SetPlayableCards(legal, hint);
	}

	public void SetLocalPassingEnabled(bool enabled)
	{
		RequireLocalTable();
		SetMainPlayerPlayEnabled(false);
		_mainHandLayout.SetPassSelectionEnabled(enabled);
	}

	/// <summary>Temporarily pause hand input without clearing a passing selection.</summary>
	public void SetLocalInteractionEnabled(bool enabled)
	{
		RequireLocalTable();
		_mainHandLayout.SetSelectionEnabled(enabled);
		SetMainPlayerPlayEnabled(enabled && !_mainHandLayout.IsPassSelectionActive);
	}

	public void SetLocalScores(IReadOnlyList<int> scores, IReadOnlyList<int> chips = null)
	{
		RequireLocalTable();
		for (int seat = 0; seat < PlayerCount; seat++)
		{
			GetPlayerInfo(seat)?.SetRoundScore(scores[seat]);
			if (chips is not null) GetPlayerInfo(seat)?.SetChipCount(chips[seat]);
		}
	}

	/// <summary>Animate all four passing legs; opponents keep their cards face down.</summary>
	public bool StartLocalPassing(IReadOnlyList<CardData> outgoing, IReadOnlyList<CardData> incoming)
	{
		RequireLocalTable();
		if (IsLocalAnimating || outgoing.Count != 3 || incoming.Count != 3 || outgoing.Distinct().Count() != 3)
			return false;
		var flights = new List<PassingFlight>();
		for (int seat = 0; seat < PlayerCount; seat++)
		{
			Control destination = GetHand((seat + 1) % PlayerCount);
			for (int index = 0; index < 3; index++)
			{
				CardControl card = seat == 0 ? FindMainCard(CardRules.Id(outgoing[index])) : GetCardAt(GetOtherHand(seat), index);
				if (card is null) return false;
				var source = new CardPose2D(CardPose2D.GetRenderedCanvasTransform(card), new Vector2(card.CardWidth, card.CardHeight), card.IsFaceUp);
				if (seat == 3) card.Setup(incoming[index], startFaceUp: false);
				flights.Add(new PassingFlight(card, destination, source, GetReceivePose(destination, card)));
			}
		}
		int generation = ++_localPassGeneration;
		IsLocalPassing = true;
		_localPassFlights = flights.Count;
		SetLocalPassingEnabled(false);
		foreach (PassingFlight flight in flights)
		{
			DetachPassingSourceCard(flight.Card);
			flight.Card.Reparent(_animationLayer, keepGlobalTransform: false);
			if (!_animationLayer.PlayPassToPose(flight.Card, flight.SourcePose, flight.TargetPose,
				card => CompleteLocalPassFlight(card, flight.Destination, generation)))
				CompleteLocalPassFlight(flight.Card, flight.Destination, generation);
		}
		return true;
	}

	private void CompleteLocalPassFlight(CardControl card, Control destination, int generation)
	{
		if (generation != _localPassGeneration || !IsInsideTree()) return;
		if (IsInstanceValid(card) && IsInstanceValid(destination)) ReceiveCard(destination, card);
		if (--_localPassFlights > 0) return;
		IsLocalPassing = false;
		_mainHandLayout.ArrangeHand();
	}

	private void CancelLocalPassing()
	{
		_localPassGeneration++;
		IsLocalPassing = false;
		_localPassFlights = 0;
	}

	private void RequireLocalTable()
	{
		if (UseNetworkSession || _networkAdapter is not null)
			throw new InvalidOperationException("Local controls require an explicitly local table.");
	}
}
