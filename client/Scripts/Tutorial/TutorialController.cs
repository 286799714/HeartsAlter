using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.Tutorial;

public enum TutorialPhase { Menu, Animating, Guiding, Play, Pass, Choice, Complete, Error }

/// <summary>Drives the real table locally with authored hands and cancellable lesson steps.</summary>
public partial class TutorialController : Control
{
	private const string ProgressPath = "user://tutorial_progress.cfg";
	private readonly ConfigFile _progress = new();
	private Control _menu;
	private Control _game;
	private Table _table;
	private TutorialSpotlight _guide;
	private IReadOnlyList<TutorialGuidePage> _guidePages = Array.Empty<TutorialGuidePage>();
	private Label _heading, _title, _status;
	private BoxContainer _choices;
	private TutorialPhase _resumePhase;
	private Button _next, _retry;
	private int _generation;
	public int GuidePageIndex { get; private set; }
	public int GuidePageCount => _guidePages.Count;
	public TutorialPhase Phase { get; private set; } = TutorialPhase.Menu;
	public int LessonIndex { get; private set; }
	public int StepIndex { get; private set; }
	public Table ActiveTable { get; private set; }
	public TutorialRound Round { get; private set; }
	public TutorialStep Step => TutorialCatalog.Lessons[LessonIndex].Steps[StepIndex];
	/// <summary>Can be disabled by an embedded preview or test without touching player progress.</summary>
	public bool SaveProgress { get; set; } = true;

	public override void _Ready()
	{
		_progress.Load(ProgressPath);
		BindScene();
		ShowMenu();
	}

	public override void _ExitTree()
	{
		_generation++;
		if (_guide is not null && IsInstanceValid(_guide)) _guide.AdvanceRequested -= AdvanceGuide;
		if (_table is not null && IsInstanceValid(_table))
		{
			_table.MainPlayerCardPlayRequested -= HandlePlay;
			_table.MainPlayerPassCardsRequested -= HandlePass;
		}
	}

	public void ShowMenu()
	{
		_generation++;
		ResetTable();
		Round = null;
		Phase = TutorialPhase.Menu;
		_game.Hide();
		_menu.Show();
		for (int index = 0; index < TutorialCatalog.Lessons.Length; index++)
		{
			var button = _menu.FindChild($"Lesson{index}", true, false) as Button;
			bool completed = _progress.GetValue("lessons", index.ToString(), false).AsBool();
			if (button is not null) button.Text = completed ? "已完成 · 再练一次" : "开始这一关";
		}
	}

	public async Task StartLessonAsync(int lessonIndex, int stepIndex = 0)
	{
		if (lessonIndex < 0 || lessonIndex >= TutorialCatalog.Lessons.Length ||
			stepIndex < 0 || stepIndex >= TutorialCatalog.Lessons[lessonIndex].Steps.Length)
			throw new ArgumentOutOfRangeException(nameof(lessonIndex));
		int generation = ++_generation;
		ResetTable();
		LessonIndex = lessonIndex;
		StepIndex = stepIndex;
		Phase = TutorialPhase.Animating;
		_menu.Hide();
		_game.Show();
		_next.Hide();
		_choices.Hide();

		_heading.Text = $"第 {lessonIndex + 1} 关 · {TutorialCatalog.Lessons[lessonIndex].Title}";
		_title.Text = $"{stepIndex + 1} / {TutorialCatalog.Lessons[lessonIndex].Steps.Length}  {Step.Title}";
		_title.TooltipText = Step.Instruction;

		_status.Text = "情景练习 · 预设手牌 · 无出牌倒计时";
		ActiveTable = _table;
		ActiveTable.InitializePlayerInfo("你", null, 900, "小岚 · 下家", null, 900,
			"阿澈 · 对家", null, 900, "小满 · 上家", null, 900);
		Round = new TutorialRound(Step);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		if (!Current(generation)) return;
		if (Step.Kind == TutorialKind.Settlement)
		{
			ActiveTable.SetLocalScores(TutorialCatalog.ExampleScores);
			ActiveTable.SetLocalPlayableCards(Array.Empty<CardData>(), "", false);

			_status.Text = "比较终局结果，选择更有利的一种";
			_choices.Show();
			BeginGuide(TutorialPhase.Choice, TutorialGuides.BeforeAction(Step, Round));
			return;
		}
		if (!ActiveTable.StartDeal(Step.Hands[0])) throw new InvalidOperationException("无法初始化教学牌桌。");
		if (!await WaitTableAsync(generation)) return;
		if (Step.Kind == TutorialKind.Pass)
		{
			ActiveTable.SetLocalPassingEnabled(true);

			BeginGuide(TutorialPhase.Pass, TutorialGuides.BeforeAction(Step, Round));

			_status.Text = "请选择 3 张牌，再点击传牌箭头";
		}
		else await DriveAsync(generation);
	}

	private void HandlePlay(int cardIndex, CardData card) => TryPlay(card);
	private void HandlePass(IReadOnlyList<CardData> cards) => TryPass(cards);

	public bool TryPlay(CardData card)
	{
		if (Phase != TutorialPhase.Play || Round.CurrentSeat != 0) return false;
		if (!Round.LegalCards().Contains(card))
		{
			SetFeedback("这张牌不符合本步出牌规则，请选择亮着的牌。");
			return false;
		}
		if (Step.PracticeCard is string target && CardRules.Id(card) != target)
		{
			SetFeedback($"这张牌在正式对局里合法。本步专门练习领红桃，请试出 {CardRules.Display(CardRules.Parse(target))}。");
			return false;
		}
		int index = ActiveTable.LocalHand.ToList().IndexOf(card);
		if (index < 0 || !ActiveTable.PlayMainPlayerCard(index)) return false;
		Round.Play(card);
		Phase = TutorialPhase.Animating;

		int generation = _generation;
		_ = RunSafely(async () =>
		{
			if (await WaitTableAsync(generation)) await DriveAsync(generation);
		});
		return true;
	}

	public bool TryPass(IReadOnlyList<CardData> cards)
	{
		if (Phase != TutorialPhase.Pass) return false;
		if (cards.Count != 3 || cards.Distinct().Count() != 3 || cards.Any(card => !Round.Hands[0].Contains(card)))
		{
			ActiveTable.SetLocalPassingEnabled(true);
			SetFeedback("请选择自己手里 3 张不同的牌。");
			return false;
		}
		if (Step.MakeClubVoid && cards.Any(card => card.Suit != PokerSuit.Club))
		{
			ActiveTable.SetLocalPassingEnabled(true);
			SetFeedback("这组传牌在正式对局里合法。本步练习制造缺门，请把仅有的 3 张梅花一起传走。");
			return false;
		}
		CardData[] incoming = Round.Hands[3].Take(3).ToArray();
		if (!ActiveTable.StartLocalPassing(cards, incoming))
		{
			ActiveTable.SetLocalPassingEnabled(true);
			return false;
		}
		Round.Pass(cards);
		Phase = TutorialPhase.Animating;

		_status.Text = "四家同时传牌，接到手牌后继续试打一墩";
		int generation = _generation;
		_ = RunSafely(async () =>
		{
			if (await WaitTableAsync(generation)) await DriveAsync(generation);
		});
		return true;
	}

	public bool ChooseSettlement(bool stayAtNine)
	{
		if (Phase != TutorialPhase.Choice) return false;
		if (!stayAtNine)
		{
			SetFeedback("你若拿到 13 分，就超过小岚的 12 分，成为最高分请客者，分不到奖池。再比较一下 9 分的结果。");
			return false;
		}
		SetFeedback("你答对啦。");
		int[] payouts = CardRules.Payouts(TutorialCatalog.ExampleScores, 400);
		ActiveTable.SetLocalScores(TutorialCatalog.ExampleScores, payouts.Select(value => 900 + value).ToArray());
		_choices.Hide();
		CompleteStep();
		return true;
	}

	private async Task DriveAsync(int generation)
	{
		if (!Current(generation)) return;
		Phase = TutorialPhase.Animating;
		ActiveTable.SetLocalPlayableCards(Array.Empty<CardData>(), "", false);
		while (!Round.Complete && Round.CurrentSeat != 0)
		{
			_status.Text = $"{TutorialCatalog.SeatNames[Round.CurrentSeat]}正在出牌";
			await ToSignal(GetTree().CreateTimer(0.45), SceneTreeTimer.SignalName.Timeout);
			if (!Current(generation)) return;
			int seat = Round.CurrentSeat;
			var legal = Round.LegalCards();
			CardData card = Step.BotCards is not null ? CardRules.Parse(Step.BotCards[seat]) :
				Step.MakeClubVoid && Round.Trick.Count == 0 ? CardRules.Parse("Club5") :
				legal.FirstOrDefault(candidate => CardRules.Id(candidate) != "Club2", legal[0]);
			if (!legal.Contains(card)) throw new InvalidOperationException($"教学预设出牌不合法：{Step.Id} / {CardRules.Id(card)}");
			if (!ActiveTable.PlayCard(seat, 0, card)) throw new InvalidOperationException("无法播放对手出牌动画。");
			Round.Play(card);

			if (!await WaitTableAsync(generation)) return;
		}
		if (!Round.Complete)
		{
			BeginGuide(TutorialPhase.Play, TutorialGuides.BeforeAction(Step, Round));

			_status.Text = "轮到你";
			return;
		}
		_status.Text = "";
		await ToSignal(GetTree().CreateTimer(0.65), SceneTreeTimer.SignalName.Timeout);
		if (!Current(generation)) return;
		if (!ActiveTable.CollectTrick(Round.Winner, Round.TrickPoints))
			throw new InvalidOperationException("无法播放收墩动画。");
		if (!await WaitTableAsync(generation)) return;
		ActiveTable.SetLocalPlayableCards(Array.Empty<CardData>(), "", false);
		CompleteStep();
	}

	private void CompleteStep() => BeginGuide(TutorialPhase.Complete, TutorialGuides.Result(Step, Round));

	private void BeginGuide(TutorialPhase resumePhase, IReadOnlyList<TutorialGuidePage> pages)
	{
		_resumePhase = resumePhase;
		_guidePages = pages;
		GuidePageIndex = 0;
		Phase = TutorialPhase.Guiding;
		// During explanations all hand cards retain their original appearance;
		// the spotlight alone determines the current focus. Actual legal-card
		// dimming is restored once the player is allowed to act.
		if (resumePhase == TutorialPhase.Play)
			ActiveTable.SetLocalPlayableCards(ActiveTable.LocalHand, "", false);
		ActiveTable.SetLocalInteractionEnabled(false);
		ShowGuidePage();
	}

	private void ShowGuidePage()
	{
		TutorialGuidePage page = _guidePages[GuidePageIndex];
		IEnumerable<Control> targets = page.Focus switch
		{
			TutorialFocus.PlayedCard => new Control[] { ActiveTable.GetLocalPlayedCard(page.Seat) },
			TutorialFocus.Hand => ActiveTable.LocalCardViews.Cast<Control>(),
			TutorialFocus.Cards => ActiveTable.LocalCardViews.Where(card => page.CardIds.Contains(CardRules.Id(card.Data))).Cast<Control>(),
			TutorialFocus.Player => new Control[] { ActiveTable.GetLocalPlayerInfo(page.Seat) },
			TutorialFocus.Choices => new Control[] { _choices },
			_ => throw new ArgumentOutOfRangeException()
		};
		bool above = page.Focus is TutorialFocus.Hand or TutorialFocus.Cards ||
			(page.Focus == TutorialFocus.Player && page.Seat == 0);
		_guide.ShowPage(page.Text, GuidePageIndex, _guidePages.Count, targets, above);
	}

	private void AdvanceGuide()
	{
		if (Phase != TutorialPhase.Guiding) return;
		if (++GuidePageIndex < _guidePages.Count)
		{
			ShowGuidePage();
			return;
		}
		_guide.HideGuide();
		Phase = TutorialPhase.Animating;
		int generation = _generation;
		// Unlock only after the advancing input event has finished dispatching.
		Callable.From(() => ResumeAfterGuide(generation)).CallDeferred();
	}

	private void ResumeAfterGuide(int generation)
	{
		if (!Current(generation)) return;
		Phase = _resumePhase;
		switch (Phase)
		{
			case TutorialPhase.Play:
				ActiveTable.SetLocalPlayableCards(Round.LegalCards(), "点一下选牌，再点同一张打出");
				_status.Text = "轮到你";
				break;
			case TutorialPhase.Pass:
				ActiveTable.SetLocalInteractionEnabled(true);
				_status.Text = "选择3张牌，点击传牌箭头";
				break;
			case TutorialPhase.Choice:
				_status.Text = "比较终局结果，选择更有利的一种";
				break;
			case TutorialPhase.Complete:
				_next.Show();
				bool last = StepIndex == TutorialCatalog.Lessons[LessonIndex].Steps.Length - 1;
				_next.Text = !last ? "下一步练习" : LessonIndex < 2 ? "进入下一关" : "完成，返回选关";
				if (!last) break;
				_status.Text = $"第 {LessonIndex + 1} 关完成";
				_progress.SetValue("lessons", LessonIndex.ToString(), true);
				if (SaveProgress && _progress.Save(ProgressPath) != Error.Ok)
					_status.Text = "通关记录未能保存，但可以继续练习";
				break;
		}
	}
	public async Task NextAsync()
	{
		if (Phase != TutorialPhase.Complete) return;
		if (StepIndex + 1 < TutorialCatalog.Lessons[LessonIndex].Steps.Length)
			await StartLessonAsync(LessonIndex, StepIndex + 1);
		else if (LessonIndex + 1 < TutorialCatalog.Lessons.Length)
			await StartLessonAsync(LessonIndex + 1);
		else ShowMenu();
	}

	private bool Current(int generation) => generation == _generation && IsInsideTree();
	private async Task<bool> WaitTableAsync(int generation)
	{
		ulong started = Time.GetTicksMsec();
		while (Current(generation) && ActiveTable.IsLocalAnimating)
		{
			if (Time.GetTicksMsec() - started > 15000) throw new TimeoutException("牌桌动画未能完成，请重玩本步。");
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
		return Current(generation);
	}

	private async Task RunSafely(Func<Task> action)
	{
		int generation = _generation;
		try
		{
			Task pending = action();
			generation = _generation;
			await pending;
		}
		catch (Exception exception)
		{
			if (!Current(generation)) return;
			_guide.HideGuide();
			Phase = TutorialPhase.Error;
			_status.Text = "本步暂时无法继续，请点击“重玩本步”";
			SetFeedback(exception.Message);
			GD.PushError(exception.ToString());
		}
	}

	private void ResetTable()
	{
		_guide.HideGuide();
		_table.ResetLocalPresentation();
		ActiveTable = null;
	}

	private void SetFeedback(string message)
	{
		if (Phase is TutorialPhase.Play or TutorialPhase.Pass or TutorialPhase.Choice)
		{
			var focus = Phase == TutorialPhase.Choice ? TutorialFocus.Choices : TutorialFocus.Hand;
			BeginGuide(Phase, new[] { new TutorialGuidePage(message, focus) });
		}
		else _status.Text = message;
	}
	/// <summary>Only bind authored nodes and actions; all geometry and styling live in Tutorial.tscn.</summary>
	private void BindScene()
	{
		_menu = GetNode<Control>("%Menu");
		_game = GetNode<Control>("%Game");
		_table = GetNode<Table>("%Table");
		_guide = GetNode<TutorialSpotlight>("%GuideOverlay");
		_guide.AdvanceRequested += AdvanceGuide;
		_heading = GetNode<Label>("%Heading");
		_title = GetNode<Label>("%StepTitle");

		_status = GetNode<Label>("%Status");

		_choices = GetNode<BoxContainer>("%Choices");

		_next = GetNode<Button>("%Next");
		_retry = GetNode<Button>("%Retry");

		_table.MainPlayerCardPlayRequested += HandlePlay;
		_table.MainPlayerPassCardsRequested += HandlePass;
		for (int index = 0; index < TutorialCatalog.Lessons.Length; index++)
		{
			int lesson = index;
			GetNode<Label>($"%LessonTitle{index}").Text = $"0{index + 1}  {TutorialCatalog.Lessons[index].Title}";
			GetNode<Label>($"%LessonDescription{index}").Text = TutorialCatalog.Lessons[index].Description;
			GetNode<Button>($"%Lesson{index}").Pressed += () => _ = RunSafely(() => StartLessonAsync(lesson));
		}
		GetNode<Button>("%BackToLobby").Pressed += () => SceneNavigation.Change(this, "res://scenes/Lobby.tscn");
		GetNode<Button>("%LessonMenu").Pressed += ShowMenu;
		_retry.Pressed += () => _ = RunSafely(() => StartLessonAsync(LessonIndex, StepIndex));
		_next.Pressed += () => _ = RunSafely(NextAsync);
		GetNode<Button>("%ChooseNine").Pressed += () => ChooseSettlement(true);
		GetNode<Button>("%ChooseThirteen").Pressed += () => ChooseSettlement(false);

	}
}
