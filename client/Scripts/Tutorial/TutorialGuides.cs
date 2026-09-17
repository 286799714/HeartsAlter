using System;
using System.Collections.Generic;
using System.Linq;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.Tutorial;

public enum TutorialFocus { PlayedCard, Hand, Cards, Player, Choices }
public sealed record TutorialGuidePage(string Text, TutorialFocus Focus, int Seat = 0, string[] CardIds = null);

/// <summary>Short, ordered explanations paired with the actual object being discussed.</summary>
public static class TutorialGuides
{
	private static TutorialGuidePage Played(string text, int seat) => new(text, TutorialFocus.PlayedCard, seat);
	private static TutorialGuidePage Cards(string text, params string[] ids) => new(text, TutorialFocus.Cards, CardIds: ids);
	private static TutorialGuidePage Hand(string text) => new(text, TutorialFocus.Hand);
	private static TutorialGuidePage Player(string text, int seat) => new(text, TutorialFocus.Player, seat);

	public static IReadOnlyList<TutorialGuidePage> BeforeAction(TutorialStep step, TutorialRound round)
	{
		if (step.Kind == TutorialKind.Pass && round.Passed) return AfterPassing(step, round);
		return step.Id switch
		{
			"follow" => new[] {
				Played("小岚先出♦9，即领出花色为♦。", 1),
				Cards("你的手牌中有♦J，与领出花色相同，必须跟一张♦。", "DiamondJ"),
				Cards("点一下选中♦J，再点一次打出。", "DiamondJ") },
			"void" => new[] {
				Played("这一墩由小岚领出♦9，领出花色是♦。", 1),
				Hand("你手里没有♦，这种情况称为缺门。在没有得分牌（后面的关卡会讲解）的情况下，你可以任选一张垫出。")},
			"control" => new[] {
				Played("目前领出花色中最大的是阿澈的♦J。", 2),
				Cards("出♦K，比♦J大，这一墩由你收走。", "DiamondK"),
				Cards("出♦2，则让阿澈收墩。由你选择。", "Diamond2") },
			"point-cards" => new[] {
				Cards("所有♥都是得分牌，从♥2到♥A共13张。♠Q打出之前，每张♥计1分，与牌面点数大小无关。", "Heart2", "HeartK", "HeartA"),
				Cards("♠Q也是得分牌，固定计6分。它打出后，之后打出的♥每张变为2分，后面有专门的关卡介绍。", "SpadeQ"),
				Cards("其他牌都不计分：♦、♣以及除♠Q外的♠牌都是0分。红色的♦A也不是得分牌。", "DiamondA", "SpadeK"),
				Cards("试从亮起的得分牌里任选一张垫出。", "Heart2", "HeartK", "HeartA", "SpadeQ") },
			"unbroken" => new[] {
				Cards("如果还没有人垫出过♥牌，且你的手里还有其他花色时，不能主动领♥牌。", "Heart9"),
				Cards("可以领出♣7或♠3。", "Club7", "Spade3") },
			"ordinary-spade" => new[] {
				Played("小岚领出♠8，这一墩要跟黑桃。", 1),
				Cards("你有♠A，必须跟出它。", "SpadeA") },
			"break-hearts" => new[] {
				Played("小岚先出♣A，领出花色为♣。", 1),
				Cards("你没有♣牌（缺门），而手牌有得分牌（♥5），试着打出它。", "Heart5") },
			"heart-lead" => new[] {
				Cards("如果本局已经触发过碎心，你可以主动领出♥牌。", "Heart9"),
				Cards("请试出♥9。", "Heart9") },
			"double" => new[] {
				Played("阿澈先打出♥5。此时♠Q还没出，因此这张红桃计1分。", 2),
				Played("小满随后打出♠Q，计6分。从这张牌之后，红桃每张变为2分。", 3),
				Cards("你缺梅花，必须先出♥9。它在♠Q之后打出，计2分。", "Heart9") },
			"queen-not-heart" => new[] {
				Played("小岚领出♦A，你手里没有方块。", 1),
				Cards("你的手牌里只有♠Q是得分牌。", "SpadeQ"),
				Cards("♠Q会让后续红桃翻倍，但它不是红桃，不会触发心碎。现在请试着出♠Q。", "SpadeQ") },
			"settlement" => new[] {
				Player("小岚12分最高（要请客），分不到奖池。", 1),
				Player("你9分，排第二。", 0),
				Player("阿澈6分，排第三。", 2),
				Player("小满0分，排第四。", 3),
				Player("按照规则，你、阿澈、小满三人将按得分高低瓜分奖池。你能够拿到 400×9÷(9+6)=240 的奖励。", 0),
				new TutorialGuidePage("比较两个结算结果：你停在 9 分，或拿到 13 分，你认为应该怎么选？", TutorialFocus.Choices) },
			"pass-void" => new[] {
				Hand("发牌后，每人选3张同时传给下家，再接到上家的3张牌。"),
				Cards("你只有这3张梅花。把♣2、♣5、♣K一起传走，就有机会制造梅花缺门。", "Club2", "Club5", "ClubK"),
				Player("这3张牌会传给下家小岚。若♣2传到他手里，传牌后就由他先出。", 1),
				Cards("本步请选中这3张梅花，再点击出现的传牌箭头。正式对局接来的牌也可能补回花色。", "Club2", "Club5", "ClubK") },
			"pass-balance" => new[] {
				Cards("大牌让你有机会争取带分的墩，不必机械地把所有大牌都传走。", "ClubA", "DiamondK", "SpadeA", "HeartK"),
				Cards("小牌有助于让墩，避免拿分过多。保留大小牌，可以保留更多选择。", "Club2", "Diamond2", "Spade3", "Heart4"),
				Hand("这次没有唯一答案。自由选3张传给下家，接到新牌后再打一墩。") },
			"opening" => new[] {
				Cards("先手在传牌结束后确定。现在♣2在你手里，由你第一个出牌。", "Club2") },
			_ => throw new ArgumentException($"Missing guide pages for {step.Id}.")
		};
	}

	private static IReadOnlyList<TutorialGuidePage> AfterPassing(TutorialStep step, TutorialRound round)
	{
		var pages = new List<TutorialGuidePage> {
			Hand(step.MakeClubVoid ? "你传走了全部梅花，这次接来的牌也没有梅花，因此形成了缺门。" : "传牌完成。现在这副手牌同时包含留下的牌和接来的3张牌，再看看你有哪些选择。") };
		if (round.Trick.Count > 0)
		{
			TutorialPlay lead = round.Trick[0];
			pages.Add(Played($"传牌后由持有♣2的{TutorialCatalog.SeatNames[lead.Seat]}先出。他选择了{CardRules.Display(lead.Card)}，首张不必是♣2。", lead.Seat));
			var legal = round.LegalCards();
			bool follows = round.Hands[0].Any(card => card.Suit == lead.Card.Suit);
			pages.Add(Cards(follows ? "现在轮到你，手里有领出花色，必须跟花色。请选择一张亮着的牌。" :
				legal.Any(CardRules.IsPointCard) ? "你已缺门，手里有分牌，必须先出红桃或♠Q。请选择一张亮着的牌。" :
				"你已缺门，手里也没有分牌，可以任意选择一张垫出。", legal.Select(CardRules.Id).ToArray()));
		}
		else
		{
			pages.Add(Cards("传牌后♣2仍在你手里，由你先出。", "Club2"));
			pages.Add(Hand("首张不强制出♣2。请选择一张合法牌，决定这一墩的领出花色。"));
		}
		return pages;
	}

	public static IReadOnlyList<TutorialGuidePage> Result(TutorialStep step, TutorialRound round)
	{
		// A settlement question has no played trick or trick winner; focus the learner's result.
		int focusSeat = step.Kind == TutorialKind.Settlement ? 0 : round.Winner;
		return step.Result.Split('。', StringSplitOptions.RemoveEmptyEntries)
			.Select(sentence => Player(sentence.Trim() + "。", focusSeat)).ToArray();
	}
}
