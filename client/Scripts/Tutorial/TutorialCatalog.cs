using System;
using System.Linq;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.Tutorial;

public enum TutorialKind { Play, Pass, Settlement }

public sealed record TutorialStep(string Id, string Title, string Instruction, string Result,
	CardData[][] Hands, int Leader = 1, string[] BotCards = null, bool HeartsBroken = false,
	bool QueenPlayed = false, bool FirstTrick = false, TutorialKind Kind = TutorialKind.Play,
	string PracticeCard = null, bool MakeClubVoid = false);

public sealed record TutorialLesson(string Title, string Description, TutorialStep[] Steps);

public static class TutorialCatalog
{
	public static readonly string[] SeatNames = { "你", "小岚", "阿澈", "小满" };
	public static readonly int[] ExampleScores = { 9, 12, 6, 0 };
	private static CardData[] Cards(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(CardRules.Parse).ToArray();
	private static CardData[][] Hands(params string[] hands) => hands.Select(Cards).ToArray();

	public static readonly TutorialLesson[] Lessons =
	{
		new("跟牌与吃墩", "关键概念：领出花色、点数比拼。", new[]
		{
			new TutorialStep("follow", "跟牌",
				"小岚先出♦9，即领出花色为♦，手牌中有♦，则必须跟一张♦。点一下选牌，再点一次打出。",
				"四人轮流出一张牌，这四张牌即称为一墩。",
				Hands("DiamondJ ClubA Spade5", "Diamond9 Club2 Spade4", "Diamond3 Club4 Spade6", "Diamond7 Club5 Spade8"),
				BotCards: new[] { "", "Diamond9", "Diamond3", "Diamond7" }),
			new TutorialStep("control", "吃墩",
				"目前最大的领出花色牌是阿澈的 ♦J。出 ♦K 会吃墩，出 ♦2 会让阿澈吃墩。两张都可以出，选择你想要的结果。",
				"点数更大的玩家将首下这墩牌，其中如果有分数牌，收下的玩家将获得分数。得分的机制将在下一关讲解。",
				Hands("Diamond2 DiamondK ClubA", "Diamond9 Club2 Spade4", "DiamondJ Club4 Spade6", "Diamond7 Club5 Spade8"),
				BotCards: new[] { "", "Diamond9", "DiamondJ", "Diamond7" }),
			new TutorialStep("void", "缺门",
				"领出花色为♦，你已经没有♦。这种情况称为缺门。现在手里也没有分牌，三张都能出。",
				"你垫出的牌不属于领出花色，无论点数多少都无法吃下这墩。阿澈的♦Q最大，由他收牌。",
				Hands("SpadeA Club3 Club6", "Diamond9 Club2 Spade4", "DiamondQ Club4 Spade6", "Diamond7 Club5 Spade8"),
				BotCards: new[] { "", "Diamond9", "DiamondQ", "Diamond7" })

		}),
		new("得分牌、心碎、结算", "认识得分牌和奖池瓜分机制。", new[]
		{
			new TutorialStep("point-cards", "认识所有得分牌",
				"所有♥牌和♠Q都是得分牌。认识它们的分值，再从手中的得分牌里任选一张垫出。",
				"得分牌会给吃下这一墩的玩家加分。",
				Hands("Heart2 HeartK HeartA SpadeQ DiamondA SpadeK",
					"ClubA Club2 Diamond2 Spade2 Heart3 Heart4",
					"ClubK Club3 Diamond3 Spade3 Heart5 Heart6",
					"Club9 Club4 Diamond4 Spade4 Heart7 Heart8"),
				BotCards: new[] { "", "ClubA", "ClubK", "Club9" }),
			new TutorialStep("unbroken", "未心碎状态",
				"如果本局还没有人打出红桃，这个状态称为“未心碎”。你手里还有梅花和黑桃，不能主动领♥9。请选♣7或♠3；如果手里只剩红桃，才可以例外领出。",
				"本轮无人垫出♥牌，未心碎状态保持。",
				Hands("Heart9 Club7 Spade3", "Club3 Diamond4 Spade8", "Club4 Diamond5 Spade9", "Club5 Diamond6 Spade10"), Leader: 0),
			new TutorialStep("ordinary-spade", "普通♠牌",
				"普通黑桃和梅花、方块一样都是0分。只有♠Q特殊，计6分。现在跟出♠A，看看这墩得几分。",
				"你用♠A吃下这墩，但四张普通黑桃都不计分。",
				Hands("SpadeA Club4 Heart7", "Spade8 Club2 Diamond4", "SpadeJ Club5 Diamond5", "Spade10 Club6 Diamond6"),
				BotCards: new[] { "", "Spade8", "SpadeJ", "Spade10" }),
			new TutorialStep("break-hearts", "缺门垫牌、触发心碎",
				"领出花色是♣，你缺门。手里有♥5，是得分牌，必须优先垫出。",
				"♥5是全场第一张打出的♥牌，它让小岚加了1分，并触发心碎，接下来每墩第一张牌可以领♥。",
				Hands("Heart5 Diamond2 SpadeA", "ClubA Club2 Diamond4", "Club3 Club5 Diamond5", "Club4 Club6 Diamond6"),
				BotCards: new[] { "", "ClubA", "Club3", "Club4" }, FirstTrick: true),
			new TutorialStep("heart-lead", "心碎后，可以主动领♥",
				"如果本局已经触发心碎，轮到你领出，你可以领♥。",
				"你领出红桃，其他人有红桃也必须跟。阿澈用♥K收下4张红桃，加4分。",
				Hands("Heart9 Club2 Spade3", "Heart3 Club4 Diamond4", "HeartK Club5 Diamond5", "Heart7 Club6 Diamond6"),
				Leader: 0, BotCards: new[] { "", "Heart3", "HeartK", "Heart7" }, HeartsBroken: true, PracticeCard: "Heart9"),
			new TutorialStep("double", "翻倍机制",
				"♠Q一张牌即可计6分，并且它会让所有未打出的♥变成2分。",
				"这一墩的总分是 0 + 1 + 6 + 2 = 9。",
				Hands("Heart9 Diamond2 SpadeA", "ClubA Club6 Diamond8", "Heart5 Spade2 Diamond4", "SpadeQ Spade3 Diamond7"),
				BotCards: new[] { "", "ClubA", "Heart5", "SpadeQ" }),
			new TutorialStep("queen-not-heart", "♠Q不会触发心碎",
				"假设此局还未触发心碎。你缺方块，手中唯一分牌是♠Q，你必须先出它。它会开启红桃翻倍，但它本身不是红桃。",
				"小岚收进♠Q得6分。此时红桃已变为每张2分，但没有触发心碎：手里还有其他花色时，仍不能领红桃。",
				Hands("SpadeQ Club4 SpadeA", "DiamondA Club2 Spade4", "Diamond3 Club5 Spade6", "Diamond7 Club6 Spade8"),
				BotCards: new[] { "", "DiamondA", "Diamond3", "Diamond7" }),
			new TutorialStep("settlement", "奖池瓜分",
				"每人投入 100，奖池 400。",
				"尽可能拿更高分，但不要拿最高分，你将分到更多奖励。",
				Hands("", "", "", ""), Kind: TutorialKind.Settlement)
		}),
		new("传牌、先手与手牌计划", "实际传出三张，观察缺门和先手变化，尝试保留大小牌。", new[]
		{
			new TutorialStep("pass-void", "传走一个花色，制造缺门",
				"正式对局发牌后，每人选 3 张同时传给下家，并接上家的 3 张。本步练习把 ♣2、♣5、♣K 全传走。选满 3 张后，点击手牌上方的传牌箭头。",
				"你传走了全部梅花，接来的牌也没有梅花，因此形成缺门。缺门更容易垫分，却也可能被迫出分牌。",
				PassingHands("Club2 Club5 ClubK Diamond3 DiamondA Spade4 SpadeK Heart6", "Diamond5 Spade7 Heart8"),
				FirstTrick: true, Kind: TutorialKind.Pass, MakeClubVoid: true),
			new TutorialStep("pass-balance", "保留大牌和小牌，留住选择",
				"这次自由选 3 张传出，没有唯一答案。大牌有助于争取带分的墩，小牌有助于让墩；别机械地传光所有大牌，也别只留下难以脱手的大牌。",
				"大牌让你有机会争分，小牌让你有机会避开过多分数。制造缺门与保留大小牌都是不错的策略。",
				PassingHands("Club2 ClubA Diamond2 DiamondK Spade3 SpadeA Heart4 HeartK", "Club5 Diamond6 Spade7"),
				FirstTrick: true, Kind: TutorialKind.Pass)
		})
	};

	private static CardData[][] PassingHands(string local, string incoming)
	{
		CardData[] own = Cards(local), received = Cards(incoming);
		var remaining = Enum.GetValues<PokerSuit>().SelectMany(suit => Enumerable.Range(2, 13)
			.Select(rank => new CardData(suit, (PokerRank)rank)))
			.Except(own).Except(received).ToArray();
		int size = own.Length;
		return new[] { own, remaining.Take(size).ToArray(), remaining.Skip(size).Take(size).ToArray(),
			received.Concat(remaining.Skip(size * 2).Take(size - 3)).ToArray() };
	}
}
