using System;
using System.Collections.Generic;
using System.Linq;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.Tutorial;

public readonly record struct TutorialPlay(int Seat, CardData Card, int Points);

/// <summary>One deterministic local exercise. Never reads or writes an online session or profile.</summary>
public sealed class TutorialRound
{
	public List<CardData>[] Hands { get; }
	public List<TutorialPlay> Trick { get; } = new();
	public int[] Scores { get; } = new int[4];
	public int CurrentSeat { get; private set; }
	public bool HeartsBroken { get; private set; }
	public bool QueenPlayed { get; private set; }
	public bool FirstTrick { get; }
	public bool Passed { get; private set; }
	public bool Complete => Trick.Count == 4;
	public int TrickPoints => Trick.Sum(play => play.Points);
	public int Winner { get; private set; } = -1;

	public TutorialRound(TutorialStep step)
	{
		Hands = step.Hands.Select(hand => hand.ToList()).ToArray();
		if (Hands.Length != 4 || Hands.Any(hand => hand.Count != Hands[0].Count) ||
			Hands.SelectMany(hand => hand).Distinct().Count() != Hands.Sum(hand => hand.Count))
			throw new ArgumentException("A lesson must have four equal hands with unique cards.");
		CurrentSeat = step.Leader;
		FirstTrick = step.FirstTrick;
	}

	// Teach the default room rules; optional heart-breaking and forced discards stay off.
	public List<CardData> LegalCards() => CardRules.LegalCards(Hands[CurrentSeat],
		Trick.Count > 0 ? Trick[0].Card.Suit : null, FirstTrick, HeartsBroken, out _,
		heartsBreakingEnabled: false, mustDiscardPointsWhenVoid: false);

	public TutorialPlay Play(CardData card)
	{
		if (Complete || !LegalCards().Contains(card)) throw new InvalidOperationException("Illegal tutorial play.");
		var play = new TutorialPlay(CurrentSeat, card, CardRules.Points(card, QueenPlayed));
		Hands[CurrentSeat].Remove(card);
		Trick.Add(play);
		HeartsBroken |= card.Suit == PokerSuit.Heart;
		QueenPlayed |= CardRules.IsQueen(card);
		if (Complete)
		{
			Winner = Trick.Where(item => item.Card.Suit == Trick[0].Card.Suit)
				.OrderByDescending(item => item.Card.Rank).First().Seat;
			Scores[Winner] += TrickPoints;
			CurrentSeat = Winner;
		}
		else CurrentSeat = (CurrentSeat + 1) % 4;
		return play;
	}

	public void Pass(IReadOnlyList<CardData> localSelection)
	{
		if (Passed || Trick.Count > 0) throw new InvalidOperationException("Passing already ended.");
		CardData[][] selections = { localSelection.ToArray(), Hands[1].Take(3).ToArray(),
			Hands[2].Take(3).ToArray(), Hands[3].Take(3).ToArray() };
		for (int seat = 0; seat < 4; seat++)
			if (selections[seat].Length != 3 || selections[seat].Distinct().Count() != 3 ||
				selections[seat].Any(card => !Hands[seat].Contains(card)))
				throw new InvalidOperationException("Select three different cards from your hand.");
		for (int seat = 0; seat < 4; seat++) Hands[seat].RemoveAll(card => selections[seat].Contains(card));
		for (int seat = 0; seat < 4; seat++) Hands[(seat + 1) % 4].AddRange(selections[seat]);
		CurrentSeat = Array.FindIndex(Hands, hand => hand.Contains(CardRules.Parse("Club2")));
		if (CurrentSeat < 0) throw new InvalidOperationException("The passing exercise must include Club2.");
		Passed = true;
	}
}
