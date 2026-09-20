using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.Tutorial;

public enum TutorialPhase { Menu, Animating, Guiding, Play, Pass, Complete, Error }

/// <summary>Drives the real table locally with authored hands and cancellable lesson steps.</summary>
public partial class TutorialController : Control
{
	private const string ProgressPath = "user://tutorial_progress.cfg";
	private const string ProgressSection = "beginner_document_v1";
	private readonly ConfigFile _progress = new();
	private Control _menu;
	private Control _game;
	private Control _overlay;
	private Table _table;
	private TutorialSpotlight _guide;
	private IReadOnlyList<TutorialGuidePage> _guidePages = Array.Empty<TutorialGuidePage>();
	private Label _heading, _title, _status;
	private TutorialPhase _resumePhase;
	private Button _retry;
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
		_overlay.Hide();
		_menu.Show();
		for (int index = 0; index < TutorialCatalog.Lessons.Length; index++)
		{
			var button = _menu.FindChild($"Lesson{index}", true, false) as Button;
			bool completed = _progress.GetValue(ProgressSection, index.ToString(), false).AsBool();
			if (button is not null) button.Text = completed ? "已完成 · 再看一次" : "开始教程";
		}
	}

	public async Task StartLessonAsync(int lessonIndex, int stepIndex = 0)
	{
		if (lessonIndex < 0 || lessonIndex >= TutorialCatalog.Lessons.Length ||
			stepIndex < 0 || stepIndex >= TutorialCatalog.Lessons[lessonIndex].Steps.Length)
			throw new ArgumentOutOfRangeException(nameof(lessonIndex));
		int generation = ++_generation;
		bool needsDeal = TutorialCatalog.Lessons[lessonIndex].Steps[stepIndex].Hands[0].Length > 0;
		ResetTable(preserveGuide: _guide.Visible && !needsDeal);
		LessonIndex = lessonIndex;
		StepIndex = stepIndex;
		Phase = TutorialPhase.Animating;
		_menu.Hide();
		_game.Show();
		_overlay.Show();

		_heading.Text = $"第 {lessonIndex + 1} 关 · {TutorialCatalog.Lessons[lessonIndex].Title}";
		_title.Text = $"{stepIndex + 1} / {TutorialCatalog.Lessons[lessonIndex].Steps.Length}  {Step.Title}";
		_title.TooltipText = Step.Title;

		_status.Text = "跟随讲解，了解玩法";
		ActiveTable = _table;
		ActiveTable.InitializePlayerInfo("你", null, 900, "小岚 · 下家", null, 900,
			"阿澈 · 对家", null, 900, "小满 · 上家", null, 900);
		Round = new TutorialRound(Step);
		ActiveTable.SetLocalGameStatus(400, 0);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		if (!Current(generation)) return;
		if (needsDeal)
		{
			if (!ActiveTable.StartDeal(Step.Hands[0])) throw new InvalidOperationException("无法初始化教学牌桌。");
			if (!await WaitTableAsync(generation)) return;
		}
		if (Step.Kind == TutorialKind.Pass)
		{
			ActiveTable.SetLocalPassingEnabled(true);

			BeginGuide(TutorialPhase.Pass, TutorialGuides.Introduction(Step));

			_status.Text = "请选择 3 张牌，再点击传牌箭头";
		}
		else BeginGuide(Step.Kind == TutorialKind.Play ? TutorialPhase.Animating : TutorialPhase.Complete,
			TutorialGuides.Introduction(Step));
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
		int index = ActiveTable.LocalHand.ToList().IndexOf(card);
		if (index < 0 || !ActiveTable.PlayMainPlayerCard(index)) return false;
		Round.Play(card);
		ActiveTable.SetLocalGameStatus(400, 0, Round.Trick[0].Card.Suit);
		Phase = TutorialPhase.Animating;

		int generation = _generation;
		_ = RunSafely(async () =>
		{
			if (!await WaitTableAsync(generation)) return;
			if (!TryBeginAfterPlayGuide()) await DriveAsync(generation);
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
		CardData[] incoming = Round.Hands[3].Take(3).ToArray();
		if (!ActiveTable.StartLocalPassing(cards, incoming))
		{
			ActiveTable.SetLocalPassingEnabled(true);
			return false;
		}
		Round.Pass(cards);
		Phase = TutorialPhase.Animating;

		_status.Text = "正在换牌";
		int generation = _generation;
		_ = RunSafely(async () =>
		{
			if (!await WaitTableAsync(generation)) return;
			_status.Text = "";
			CompleteStep();
		});
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
			CardData card = legal[0];
			if (!legal.Contains(card)) throw new InvalidOperationException($"教学预设出牌不合法：{Step.Id} / {CardRules.Id(card)}");
			if (!ActiveTable.PlayCard(seat, 0, card)) throw new InvalidOperationException("无法播放对手出牌动画。");
			Round.Play(card);
			ActiveTable.SetLocalGameStatus(400, 0, Round.Trick[0].Card.Suit);

			if (!await WaitTableAsync(generation)) return;
			if (TryBeginAfterPlayGuide()) return;
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
		ActiveTable.SetLocalGameStatus(400, 1);
		CompleteStep();
	}

	private bool TryBeginAfterPlayGuide()
	{
		var pages = TutorialGuides.AfterPlay(Step, Round);
		if (pages.Count == 0) return false;
		_status.Text = "";
		// The played card has landed. End this drive before scheduling another
		// player; dismissing the guide resumes from Round.CurrentSeat.
		BeginGuide(TutorialPhase.Animating, pages);
		return true;
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
			TutorialFocus.None => Array.Empty<Control>(),
			_ => throw new ArgumentOutOfRangeException()
		};
		bool above = page.Focus is TutorialFocus.Hand or TutorialFocus.Cards or TutorialFocus.None ||
			(page.Focus == TutorialFocus.Player && page.Seat == 0);
		_guide.ShowPage(page.Text, GuidePageIndex, _guidePages.Count, targets, above, allowEmptyTargets: page.Focus == TutorialFocus.None);
	}

	private void AdvanceGuide()
	{
		if (Phase != TutorialPhase.Guiding) return;
		if (++GuidePageIndex < _guidePages.Count)
		{
			ShowGuidePage();
			return;
		}
		// Keep the page through input dispatch. The next section removes it for
		// dealing, or replaces it directly when no deal animation is needed.
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
			case TutorialPhase.Animating:
				_guide.HideGuide();
				_ = RunSafely(() => DriveAsync(generation));
				break;
			case TutorialPhase.Play:
				_guide.HideGuide();
				ActiveTable.SetLocalPlayableCards(Round.LegalCards(), "点一下选牌，再点同一张打出");
				_status.Text = "轮到你";
				break;
			case TutorialPhase.Pass:
				_guide.HideGuide();
				ActiveTable.SetLocalInteractionEnabled(true);
				_status.Text = "选择3张牌，点击传牌箭头";
				break;
			case TutorialPhase.Complete:
				bool last = StepIndex == TutorialCatalog.Lessons[LessonIndex].Steps.Length - 1;
				if (last)
				{
					_progress.SetValue(ProgressSection, LessonIndex.ToString(), true);
					if (SaveProgress && _progress.Save(ProgressPath) != Error.Ok)
						GD.PushWarning("通关记录未能保存，但可以继续练习。");
				}
				// The final dialogue click has already finished dispatching. Continue
				// immediately, retaining the same cancellation guard as manual replay.
				_ = RunSafely(NextAsync);
				break;
		}
	}
	private async Task NextAsync()
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
			if (Time.GetTicksMsec() - started > 15000) throw new TimeoutException("牌桌动画未能完成，请重看本节。");
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
			_status.Text = "本节暂时无法继续，请点击“重看本节”";
			SetFeedback(exception.Message);
			GD.PushError(exception.ToString());
		}
	}

	private void ResetTable(bool preserveGuide = false)
	{
		if (preserveGuide) _guide.HoldForTransition();
		else _guide.HideGuide();
		_table.ResetLocalPresentation();
		ActiveTable = null;
	}

	private void SetFeedback(string message)
	{
		if (Phase is TutorialPhase.Play or TutorialPhase.Pass)
			BeginGuide(Phase, new[] { new TutorialGuidePage(message, TutorialFocus.Hand) });
		else _status.Text = message;
	}
	/// <summary>Only bind authored nodes and actions; all geometry and styling live in Tutorial.tscn.</summary>
	private void BindScene()
	{
		_menu = GetNode<Control>("%Menu");
		_game = GetNode<Control>("%Game");
		_overlay = GetNode<Control>("%Overlay");
		_table = GetNode<Table>("%Table");
		_guide = GetNode<TutorialSpotlight>("%GuideOverlay");
		_guide.AdvanceRequested += AdvanceGuide;
		_heading = GetNode<Label>("%Heading");
		_title = GetNode<Label>("%StepTitle");

		_status = GetNode<Label>("%Status");

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

	}
}
