using System;
using System.Linq;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.Tutorial;

public enum TutorialKind { Explanation, Play, Pass }

public sealed record TutorialStep(string Id, string Title, CardData[][] Hands,
	TutorialKind Kind = TutorialKind.Explanation, bool FirstTrick = false, int Leader = 1);

public sealed record TutorialLesson(string Title, string Description, TutorialStep[] Steps);

public static class TutorialCatalog
{
	public static readonly string[] SeatNames = { "你", "小岚", "阿澈", "小满" };
	private static CardData[] Cards(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(CardRules.Parse).ToArray();
	private static CardData[][] Hands(params string[] hands) => hands.Select(Cards).ToArray();

	// Learn the rules, see scoring in action, then practice passing in one continuous lesson.
	public static readonly TutorialLesson[] Lessons =
	{
		new("新手教程", "游戏目标 → 出牌规则 → 得分牌与实战 → 传牌与小技巧", new[]
		{
			new TutorialStep("goal", "游戏目标", Hands("", "", "", "")),
			new TutorialStep("rules", "出牌规则",
				Hands("Club8 ClubK Diamond2", "Club2 Club3 Club4", "Club5 Diamond3 Diamond4", "Club10 Diamond5 Diamond6"), TutorialKind.Play),
			new TutorialStep("points", "如何获得积分？",
				Hands("Heart2 HeartK SpadeQ", "Heart3 Club2 Diamond2", "Heart5 Club3 Diamond3", "Spade2 Spade3 Spade4"), TutorialKind.Play, Leader: 3),
			new TutorialStep("pass", "传牌与小技巧", PassingHands(), TutorialKind.Pass, FirstTrick: true)
		})
	};

	private static CardData[][] PassingHands()
	{
		CardData[] own = Cards("Club4 Club8 ClubK Diamond2 Diamond6 DiamondQ Spade3 Spade9 SpadeA Heart2 Heart5 Heart10 HeartK");
		CardData[] incoming = Cards("Club7 Diamond9 SpadeJ");
		CardData clubTwo = CardRules.Parse("Club2");
		var remaining = Enum.GetValues<PokerSuit>().SelectMany(suit => Enumerable.Range(2, 13)
			.Select(rank => new CardData(suit, (PokerRank)rank)))
			.Except(own).Except(incoming).Where(card => card != clubTwo).ToArray();
		// Keep Club2 outside the next player's three-card pass so the source example
		// remains valid for every selection the learner can make.
		return new[] { own,
			remaining.Take(3).Append(clubTwo).Concat(remaining.Skip(3).Take(9)).ToArray(),
			remaining.Skip(12).Take(13).ToArray(),
			incoming.Concat(remaining.Skip(25)).ToArray() };
	}
}
