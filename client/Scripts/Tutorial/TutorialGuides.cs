using System;
using System.Collections.Generic;
using System.Linq;
using HeartsAlter.Scripts.InGame;

namespace HeartsAlter.Scripts.Tutorial;

public enum TutorialFocus { None, PlayedCard, Hand, Cards, Player }
public sealed record TutorialGuidePage(string Text, TutorialFocus Focus, int Seat = 0, string[] CardIds = null);

/// <summary>Beginner explanations ordered around the scoring and passing exercises.</summary>
public static class TutorialGuides
{
	private static TutorialGuidePage Text(string text) => new(text, TutorialFocus.None);
	private static TutorialGuidePage Played(string text, int seat) => new(text, TutorialFocus.PlayedCard, seat);
	private static TutorialGuidePage Cards(string text, params string[] ids) => new(text, TutorialFocus.Cards, CardIds: ids);
	private static TutorialGuidePage Hand(string text) => new(text, TutorialFocus.Hand);
	private static TutorialGuidePage Player(string text, int seat) => new(text, TutorialFocus.Player, seat);

	public static IReadOnlyList<TutorialGuidePage> Introduction(TutorialStep step)
	{
		return step.Id switch
		{
			"goal" => new[] {
				Text("抢红包是个讲究[color=#FFD987]边界感[/color]的游戏，如果抢的太多，成了出头鸟，总免不得成为[color=#FFD987]请客买单的冤大头[/color]。"),
				Text("所以，在我们的游戏中，[color=#FF9CB2]积分第一名[/color]的玩家将[color=#FF9CB2]一无所有[/color]，[color=#FFD987]第二名[/color]的玩家将[color=#FFD987]赢得最多[/color]！"),
				Text("如果你是想赚钱的聪明人，就努力把自己保持在[color=#FFD987]第二名[/color]；如果你是一位慷慨的富豪，那就勇夺第一吧！") },
			"rules" => new[] {
				Hand("游戏共进行[color=#FFD987]13轮[/color]。每轮每名玩家出1张牌。") },
			"points" => new[] {
				Cards("游戏中的得分牌包括所有[color=#FF9CB2]♥牌[/color]和[color=#8DE0DA]♠Q[/color]。\n每获得一张[color=#FF9CB2]♥牌[/color]，无论点数大小，[color=#FF9CB2]积1分[/color]。", "Heart2", "HeartK"),
				Cards("获得[color=#8DE0DA]♠Q[/color]，[color=#8DE0DA]积6分[/color]。它往往能瞬间改变局势。", "SpadeQ"),
				Cards("[color=#8DE0DA]♠Q[/color]被打出后，[color=#FFD987]之后打出的[/color]每张[color=#FF9CB2]♥牌[/color]将改为[color=#FF9CB2]积2分[/color]。", "Heart2", "HeartK", "SpadeQ") },
			"pass" => new[] {
				Hand("接下来学习[color=#8DE0DA]传牌[/color]。正式对局中，传牌发生在[color=#FFD987]发牌后、第一轮出牌前[/color]，每人有13张手牌。"),
				Hand("每位玩家从手牌中[color=#8DE0DA]选择3张牌传给下家[/color]，同时接收上家传来的3张牌。合理换牌，可以帮你提前调整策略、控制积分。\n选好后，点击[color=#8DE0DA]传牌箭头[/color]。") },
			_ => throw new ArgumentException($"Missing guide pages for {step.Id}.")
		};
	}

	/// <summary>Shown immediately after the triggering play finishes, before another player acts.</summary>
	public static IReadOnlyList<TutorialGuidePage> AfterPlay(TutorialStep step, TutorialRound round) => (step.Id, round.Trick.Count) switch
	{
		("rules", 1) => new[] {
			Played("率先出牌的玩家决定本轮的[color=#FFD987]指定花色[/color]。小岚打出了[color=#8DE0DA]♣2[/color]，所以本轮的[color=#FFD987]指定花色是[/color][color=#8DE0DA]♣[/color]。", round.Trick[0].Seat) },
		("points", 1) => new[] {
			Played("小满打出了[color=#8DE0DA]♠2[/color]，本轮的[color=#FFD987]指定花色是[/color][color=#8DE0DA]♠[/color]。", round.Trick[0].Seat) },
		("points", 2) => new[] {
			Played("你打出了[color=#8DE0DA]♠Q[/color]。它本身计[color=#8DE0DA]6分[/color]，并让[color=#FFD987]之后打出的每张[/color][color=#FF9CB2]♥牌[/color]从[color=#FF9CB2]1分[/color]变为[color=#FF9CB2]2分[/color]。", round.Trick[^1].Seat) },
		("points", 3) => new[] {
			Played("小岚没有[color=#8DE0DA]♠[/color]，垫出了[color=#FF9CB2]♥3[/color]。\n因为它在[color=#8DE0DA]♠Q[/color]之后打出，这张牌已经从1分变成了[color=#FFD987]2分[/color]。", round.Trick[^1].Seat) },
		("points", 4) => new[] {
			Played("阿澈也没有[color=#8DE0DA]♠[/color]，垫出了[color=#FF9CB2]♥5[/color]，同样计[color=#FFD987]2分[/color]。\n两张[color=#FF9CB2]♥牌[/color]都在[color=#8DE0DA]♠Q[/color]之后打出，所以这一轮合计[color=#FFD987]6＋2＋2＝10分[/color]。", round.Trick[^1].Seat) },
		_ => Array.Empty<TutorialGuidePage>()
	};

	public static IReadOnlyList<TutorialGuidePage> BeforeAction(TutorialStep step, TutorialRound round) => step.Id switch
	{
		"rules" => new[] {
			Hand("由于本墩指定花色是[color=#8DE0DA]♣[/color]，所以如果你的手中有[color=#8DE0DA]♣[/color]，就[color=#FFD987]必须打出一张[/color][color=#8DE0DA]♣[/color]。\n如果没有[color=#8DE0DA]♣[/color]，可以打出任意一张牌，但[color=#FFD987]无法赢得本轮[/color]。"),
			Hand("四名玩家都出牌后，[color=#FFD987]指定花色中点数最大[/color]的玩家赢得本轮，并在下一轮[color=#8DE0DA]率先出牌[/color]。\n试着选一张能赢下本轮的[color=#8DE0DA]♣[/color]，[color=#8DE0DA]点一下选牌，再点一次打出[/color]。") },
		"points" => new[] {
			Cards("轮到你了。你手中有[color=#8DE0DA]♠[/color]，[color=#FFD987]必须跟出[/color][color=#8DE0DA]♠Q[/color]。\n请你打出[color=#8DE0DA]♠Q[/color]。", "SpadeQ") },
		_ => throw new ArgumentException($"Missing action pages for {step.Id}.")
	};

	public static IReadOnlyList<TutorialGuidePage> Result(TutorialStep step, TutorialRound round) => step.Id switch
	{
		"rules" => RulesResult(round),
		"points" => new[] {
			Player($"你赢得了这一轮，收下其中的得分牌：\n[color=#8DE0DA]♠Q计6分[/color]，它之后打出的[color=#FF9CB2]♥3和♥5各计2分[/color]，合计[color=#FFD987]{round.TrickPoints}分[/color]。", round.Winner)},
		"pass" => new[] {
			Hand("传牌完成！你交出了3张牌，也收到了上家的3张牌，手中仍有[color=#FFD987]13张牌[/color]。"),
			Text("这是一个需要随机应变、动态博弈的游戏。\n保留一些[color=#8DE0DA]大牌和小牌[/color]，能让你更灵活地决定何时赢下一轮、何时避开积分。"),
			Text("[color=#FF9CB2]♥牌[/color]和[color=#8DE0DA]♠Q[/color]会直接影响最终排名，因此，把它们留到[color=#FFD987]合适的时机[/color]再打出，往往是制胜的关键。"),
			Text("别忘了你的真正目标：[color=#FFD987]不是分数越高越好，而是恰到好处地成为第二名[/color]。") },
		_ => throw new ArgumentException($"Missing result pages for {step.Id}.")
	};

	private static IReadOnlyList<TutorialGuidePage> RulesResult(TutorialRound round)
	{
		string ownCard = CardRules.Display(round.Trick.Single(play => play.Seat == 0).Card);
		string winningCard = CardRules.Display(round.Trick.Single(play => play.Seat == round.Winner).Card);
		string winner = TutorialCatalog.SeatNames[round.Winner];
		string text = round.Winner == 0
			? $"你打出的[color=#8DE0DA]{ownCard}[/color]是本轮[color=#FFD987]指定花色牌中点数最大的牌[/color]，所以你赢得了这一轮！"
			: $"你打出的[color=#8DE0DA]{ownCard}[/color]比[color=#FFD987]{winner}[/color]的[color=#8DE0DA]{winningCard}[/color]更小。\n因此这轮由[color=#FFD987]{winner}获胜[/color]。想要赢得这一轮，你应该打出点数更大的[color=#8DE0DA]♣K[/color]";
		return new[] { Player(text, round.Winner) };
	}
}
