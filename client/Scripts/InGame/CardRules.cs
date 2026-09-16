using System;
using System.Collections.Generic;
using System.Linq;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.InGame;

/// <summary>Client legality hints and local tutorial rules. Network play is still validated by the server.</summary>
public static class CardRules
{
	public static bool IsPointCard(CardData card) => card.Suit == PokerSuit.Heart || IsQueen(card);
	public static bool IsQueen(CardData card) => card == new CardData(PokerSuit.Spade, PokerRank.Queen);
	public static int Points(CardData card, bool queenPlayed) =>
		card.Suit == PokerSuit.Heart ? (queenPlayed ? 2 : 1) : IsQueen(card) ? 6 : 0;

	public static List<CardData> LegalCards(IEnumerable<CardData> cards, PokerSuit? lead,
		bool firstTrick, bool heartsBroken, out bool mustDiscardPoints)
	{
		var hand = cards.ToList();
		mustDiscardPoints = false;
		if (lead is PokerSuit suit)
		{
			var follow = hand.FindAll(card => card.Suit == suit);
			if (follow.Count > 0) hand = follow;
			else
			{
				var points = hand.FindAll(IsPointCard);
				mustDiscardPoints = points.Count > 0;
				return mustDiscardPoints ? points : hand;
			}
		}
		else if (!heartsBroken && hand.Exists(card => card.Suit != PokerSuit.Heart))
			hand = hand.FindAll(card => card.Suit != PokerSuit.Heart);
		if (firstTrick && hand.Exists(card => !IsPointCard(card)))
			hand = hand.FindAll(card => !IsPointCard(card));
		return hand;
	}

	/// <summary>Same largest-remainder allocation as server settlePot, for teaching examples only.</summary>
	public static int[] Payouts(IReadOnlyList<int> scores, int pot)
	{
		if (scores.Count != 4 || scores.Any(score => score < 0) || pot < 0)
			throw new ArgumentException("Expected four non-negative scores and a non-negative pot.");
		int highest = scores.Max();
		var eligible = Enumerable.Range(0, 4).Where(seat => scores[seat] < highest).ToArray();
		if (eligible.Length == 0) eligible = new[] { 0, 1, 2, 3 };
		long weight = eligible.Sum(seat => (long)scores[seat]);
		bool equal = weight == 0;
		if (equal) weight = eligible.Length;
		var payouts = new int[4];
		var remainders = new Dictionary<int, long>();
		foreach (int seat in eligible)
		{
			long numerator = (long)pot * (equal ? 1 : scores[seat]);
			payouts[seat] = (int)(numerator / weight);
			remainders[seat] = numerator % weight;
		}
		foreach (int seat in eligible.OrderByDescending(seat => remainders[seat]).ThenBy(seat => seat)
			.Take(pot - payouts.Sum())) payouts[seat]++;
		return payouts;
	}

	public static string Id(CardData card) => $"{card.Suit}{RankText(card.Rank)}";
	public static string Display(CardData card) =>
		$"{card.Suit switch { PokerSuit.Club => "♣", PokerSuit.Diamond => "♦", PokerSuit.Heart => "♥", _ => "♠" }}{RankText(card.Rank)}";
	private static string RankText(PokerRank rank) => rank switch
	{
		PokerRank.Jack => "J", PokerRank.Queen => "Q", PokerRank.King => "K", PokerRank.Ace => "A", _ => ((int)rank).ToString()
	};
	public static CardData Parse(string id)
	{
		foreach (PokerSuit suit in Enum.GetValues<PokerSuit>())
		{
			string prefix = suit.ToString();
			if (!id.StartsWith(prefix, StringComparison.Ordinal)) continue;
			string rank = id[prefix.Length..];
			int value = rank switch { "J" => 11, "Q" => 12, "K" => 13, "A" => 14, _ => int.Parse(rank) };
			if (value is >= 2 and <= 14) return new CardData(suit, (PokerRank)value);
		}
		throw new ArgumentException($"Invalid card: {id}");
	}
}
