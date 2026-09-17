using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.InGame.Card;
using HeartsAlter.Scripts.Tutorial;

namespace HeartsAlter.Tests;

/// <summary>Offline end-to-end exercises through the same hand and pass signals used by players.</summary>
public partial class TutorialSmoke : Node
{
	private TutorialController _tutorial;
	public override async void _Ready()
	{
		int exitCode = 0;
		ColyseusClientAdapter previousAdapter = GameSession.GameAdapter;
		PlayerProfile previousProfile = GameSession.Profile;
		var sentinel = new ColyseusClientAdapter();
		GameSession.GameAdapter = sentinel;
		Engine.TimeScale = 8;
		try
		{
			VerifyRules();
			_tutorial = GD.Load<PackedScene>("res://scenes/Tutorial.tscn").Instantiate<TutorialController>();
			_tutorial.SaveProgress = false;
			AddChild(_tutorial);
			Check(_tutorial.Phase == TutorialPhase.Menu, "The tutorial should start with lesson selection.");
			Check(!_tutorial.GetNode<Control>("%InstructionPanel").IsVisibleInTree(), "Instructions must start hidden.");
			Table authoredTable = _tutorial.GetNode<Table>("Game/Table");
			await Capture("menu");
			int steps = 0;
			for (int lesson = 0; lesson < TutorialCatalog.Lessons.Length; lesson++)
			{
				for (int step = 0; step < TutorialCatalog.Lessons[lesson].Steps.Length; step++)
				{
					Task entering = _tutorial.StartLessonAsync(lesson, step);
					Check(!_tutorial.GetNode<TutorialSpotlight>("%GuideOverlay").Visible, "Guidance appeared before dealing/animation finished.");
					await entering;
					Check(!_tutorial.ActiveTable.UseNetworkSession, "Tutorial attached to the online session.");
					Check(ReferenceEquals(_tutorial.ActiveTable, authoredTable), "The scene-authored table was replaced.");
					Check(authoredTable.Scale.IsEqualApprox(Vector2.One) && authoredTable.Position.IsZeroApprox() &&
						authoredTable.Size.IsEqualApprox(_tutorial.Size), "Tutorial table must fill the scene at its original scale.");
					if (_tutorial.Step.Id is "follow" or "point-cards" or "double" or "pass-void" or "settlement") await Capture(_tutorial.Step.Id);
					Check(_tutorial.Phase == TutorialPhase.Guiding, "Each action should start with focused guidance.");
					await WaitForInput();
					if (_tutorial.Phase == TutorialPhase.Pass)
					{
						var hand = _tutorial.ActiveTable.GetNode<MainHandLayout>("InGame/MainHandLayout");
						hand.ApplyPassSelection(_tutorial.Round.Hands[0].Take(2).Select(CardRules.Id), false);
						Check(!hand.SubmitPassSelection(), "Two-card pass was accepted.");
						if (_tutorial.Step.MakeClubVoid)
						{
							var wrong = _tutorial.Round.Hands[0].Where(card => card.Suit != PokerSuit.Club).Take(3).ToArray();
							Check(!_tutorial.TryPass(wrong), "Void exercise accepted the wrong practice goal.");
							await WaitForInput();
							Check(hand.SelectionEnabled, "Rejected practice choice did not unlock the hand.");
						}
						CardData[] selected = _tutorial.Step.MakeClubVoid
							? _tutorial.Round.Hands[0].Where(card => card.Suit == PokerSuit.Club).ToArray()
							: _tutorial.Round.Hands[0].Take(3).ToArray();
						hand.ApplyPassSelection(selected.Select(CardRules.Id), false);
						Check(hand.SubmitPassSelection(), "Passing signal did not fire.");
						await WaitForInput();
						Check(_tutorial.ActiveTable.LocalHand.ToHashSet().SetEquals(_tutorial.Round.Hands[0]), "Animated hand differs from passed model.");
						if (_tutorial.Step.MakeClubVoid)
						{
							Check(_tutorial.Round.Hands[0].All(card => card.Suit != PokerSuit.Club), "Passing failed to create the void.");
							Check(_tutorial.Round.Trick[0].Seat == 1 && _tutorial.Round.Trick[0].Card == CardRules.Parse("Club5"), "Club2 recipient did not open with another card.");
						}
					}
					if (_tutorial.Phase == TutorialPhase.Choice)
					{
						Check(!_tutorial.ChooseSettlement(false), "Incorrect settlement answer completed the step.");
						await WaitForInput();
						Check(_tutorial.ChooseSettlement(true), "Correct settlement answer was rejected.");
						await WaitForInput();
						Check(_tutorial.ActiveTable.GetNode<PlayerInfo>("MainPlayerInfo").ChipCount == 1140, "Incorrect tutorial payout.");
					}
					else
					{
						Check(_tutorial.Phase == TutorialPhase.Play, "Lesson did not wait for player input.");
						var legal = _tutorial.Round.LegalCards();
						var layout = _tutorial.ActiveTable.GetNode<MainHandLayout>("InGame/MainHandLayout");
						Check(layout.PlayableCardIds.ToHashSet().SetEquals(legal.Select(CardRules.Id)), "Wrong card highlights.");
						var illegal = _tutorial.Round.Hands[0].Where(card => !legal.Contains(card)).ToArray();
						if (illegal.Length > 0)
						{
							Check(!_tutorial.TryPlay(illegal[0]), "Illegal tutorial play was accepted.");
							await WaitForInput();
						}
						CardData chosen = _tutorial.Step.PracticeCard is string target ? CardRules.Parse(target) : legal[0];
						if (_tutorial.Step.Id == "opening") chosen = CardRules.Parse("Diamond3");
						if (_tutorial.Step.Id == "control") chosen = CardRules.Parse("DiamondK");
						int cardIndex = _tutorial.ActiveTable.LocalHand.ToList().IndexOf(chosen);
						layout.EmitSignal(MainHandLayout.SignalName.CardPlayRequested, cardIndex);
						await WaitForInput();
						Check(_tutorial.Round.Complete, "Trick did not complete.");
						Check(_tutorial.ActiveTable.GetNode<PlayerInfo>(new[] { "MainPlayerInfo", "PlayerInfo", "PlayerInfo2", "PlayerInfo3" }[_tutorial.Round.Winner])
							.RoundScore == _tutorial.Round.TrickPoints, "Trick points were not presented to the winner.");
						if (_tutorial.Step.Id == "double") Check(_tutorial.Round.TrickPoints == 9, "Queen timing should yield 0 + 1 + 6 + 2.");
						if (_tutorial.Step.Id == "queen-not-heart") Check(!_tutorial.Round.HeartsBroken && _tutorial.Round.QueenPlayed, "Queen incorrectly broke hearts.");
					}
					Check(_tutorial.Phase == TutorialPhase.Complete, "Step did not expose its result.");
					Check((_tutorial.FindChild("Next", true, false) as Button)?.IsVisibleInTree() == true, "Next-step action is not visible.");
					if (_tutorial.Step.Id is "double" or "settlement" or "pass-void") await Capture(_tutorial.Step.Id + "-result");
					GD.Print($"TUTORIAL_STEP_OK: {_tutorial.Step.Id}");
					steps++;
				}
			}
			await _tutorial.NextAsync();
			Check(_tutorial.Phase == TutorialPhase.Menu, "Finishing the last lesson did not return to selection.");
			await _tutorial.StartLessonAsync(0);
			await WaitForInput();
			Check(_tutorial.TryPlay(CardRules.Parse("DiamondJ")), "Could not replay a completed step.");
			await WaitForInput();
			await _tutorial.NextAsync();
			await WaitForInput();
			Check(_tutorial.Step.Id == "control" && _tutorial.Phase == TutorialPhase.Play, "Next did not load the next exercise.");
			// Re-enter while a deal is in flight, then leave while passing is in flight.
			Task abandoned = _tutorial.StartLessonAsync(0);
			await _tutorial.StartLessonAsync(2);
			await abandoned;
			await WaitForInput();
			Check(_tutorial.Step.Id == "pass-void" && _tutorial.Phase == TutorialPhase.Pass, "Old scene work leaked into a restarted step.");
			Check(_tutorial.TryPass(_tutorial.Round.Hands[0].Where(card => card.Suit == PokerSuit.Club).ToArray()), "Could not start exit test pass.");
			_tutorial.ShowMenu();
			await ToSignal(GetTree().CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
			Check(_tutorial.Phase == TutorialPhase.Menu && _tutorial.ActiveTable is null, "Exit did not cancel the lesson.");
			// Cancel a pending final-guide click before its deferred input unlock runs.
			await _tutorial.StartLessonAsync(0);
			while (_tutorial.GuidePageIndex + 1 < _tutorial.GuidePageCount)
			{
				ClickGuide();
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			}
			ClickGuide();
			_tutorial.ShowMenu();
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			Check(_tutorial.Phase == TutorialPhase.Menu && !_tutorial.GetNode<TutorialSpotlight>("%GuideOverlay").Visible,
				"Deferred guidance completion escaped into a cancelled lesson.");
			Check(ReferenceEquals(GameSession.GameAdapter, sentinel) && ReferenceEquals(GameSession.Profile, previousProfile), "Tutorial mutated online session/profile.");
			GD.Print($"TUTORIAL_SMOKE_OK: {steps} steps, legal-card parity, passing, scoring, settlement, cancellation and session isolation");
		}
		catch (Exception exception) { exitCode = 1; GD.PushError(exception.ToString()); }
		finally
		{
			GameSession.GameAdapter = previousAdapter;
			Engine.TimeScale = 1;
			GetTree().Quit(exitCode);
		}
	}

	private async Task Capture(string name)
	{
		string option = OS.GetCmdlineUserArgs().FirstOrDefault(arg => arg.StartsWith("--capture-dir="));
		if (option is null) return;
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		string directory = option["--capture-dir=".Length..];
		System.IO.Directory.CreateDirectory(directory);
		using Image image = GetViewport().GetTexture().GetImage();
		Check(image.SavePng(System.IO.Path.Combine(directory, name + ".png")) == Error.Ok, "Could not capture tutorial preview.");
	}

	private async Task WaitForInput()
	{
		ulong start = Time.GetTicksMsec();
		int pages = 0;
		while (_tutorial.Phase is TutorialPhase.Animating or TutorialPhase.Guiding)
		{
			if (Time.GetTicksMsec() - start > 15000) throw new TimeoutException("Tutorial step timed out.");
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			if (_tutorial.Phase != TutorialPhase.Guiding) continue;
			Check(++pages < 20, "Guidance did not finish.");
			var guide = _tutorial.GetNode<TutorialSpotlight>("%GuideOverlay");
			var hand = _tutorial.ActiveTable.GetNode<MainHandLayout>("InGame/MainHandLayout");
			Check(!hand.SelectionEnabled && !_tutorial.ActiveTable.CanMainPlayerPlay, "Cards are interactive during guidance.");
			Check(guide.Visible && guide.FocusRects.Count > 0 && guide.FocusRects.All(rect => rect.Size.X > 0 && rect.Size.Y > 0),
				"The highlighted region is missing or empty.");
			if (_tutorial.Step.Id == "follow" && !_tutorial.Round.Complete && _tutorial.GuidePageCount == 3)
			{
				if (_tutorial.GuidePageIndex == 0)
					Check(guide.Targets.Count == 1 && ReferenceEquals(guide.Targets[0], _tutorial.ActiveTable.GetLocalPlayedCard(1)),
						"The lead explanation must highlight Xiaolan's actual Diamond9.");
				else Check(guide.Targets.Count == 1 && guide.Targets[0] is CardControl card && card.Data == CardRules.Parse("DiamondJ"),
					"Following-suit explanation must highlight the player's DiamondJ.");
				if (_tutorial.GuidePageIndex == 1) await Capture("follow-hand");
			}
			if (_tutorial.Step.Id == "double" && !_tutorial.Round.Complete && _tutorial.GuidePageIndex == 1)
				await Capture("double-queen");
			if (_tutorial.Step.Id == "point-cards" && !_tutorial.Round.Complete && _tutorial.GuidePageCount == 4)
			{
				string[][] expectedTargets = {
					new[] { "Heart2", "HeartK", "HeartA" }, new[] { "SpadeQ" },
					new[] { "DiamondA", "SpadeK" }, new[] { "Heart2", "HeartK", "HeartA", "SpadeQ" }
				};
				Check(guide.Targets.OfType<CardControl>().Select(card => CardRules.Id(card.Data)).ToHashSet()
					.SetEquals(expectedTargets[_tutorial.GuidePageIndex]), "Point-card introduction highlights the wrong cards.");
				if (_tutorial.GuidePageIndex is 1 or 2) await Capture($"point-cards-{_tutorial.GuidePageIndex}");
			}
			int beforeCards = _tutorial.Round.Hands[0].Count;
			int beforePlays = _tutorial.Round.Trick.Count;
			int beforePage = _tutorial.GuidePageIndex;
			Check(!_tutorial.TryPlay(CardRules.Parse("DiamondJ")) && !_tutorial.ChooseSettlement(true), "Actions bypassed guidance.");
			ClickGuide();
			Check(_tutorial.GuidePageIndex == beforePage + 1, "One click should advance exactly one explanation.");
			Check(_tutorial.Round.Hands[0].Count == beforeCards && _tutorial.Round.Trick.Count == beforePlays,
				"Advancing guidance accidentally played a card.");
		}
		Check(_tutorial.Phase != TutorialPhase.Error, "Tutorial reported an error.");
		Check(!_tutorial.GetNode<Control>("%InstructionPanel").IsVisibleInTree(), "Instruction panel stayed visible after guidance.");
		var layout = _tutorial.ActiveTable?.GetNode<MainHandLayout>("InGame/MainHandLayout");
		if (layout is not null)
		{
			Check(layout.SelectedCard is null, "The final guidance click leaked into hand selection.");
			Check(layout.Cards.Select((card, index) => card.ZIndex == index).All(correct => correct), "Guidance did not restore hand stacking.");
		}
	}

	private void ClickGuide()
	{
		// Deliberately click where a real hand card sits, including on the final page.
		Control card = _tutorial.ActiveTable.LocalCardViews.FirstOrDefault();
		Vector2 point = card is null ? new Vector2(640, 360) : card.GetGlobalTransformWithCanvas() * (card.Size * 0.5f);
		if (_tutorial.Step.Id == "unbroken" && !_tutorial.Round.Complete && _tutorial.GuidePageIndex == 0 && _tutorial.GuidePageCount == 2)
		{
			Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Pressed = true, Position = point });
			Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Pressed = false, Position = point });
			// A synthesized mouse event from the same touch must not advance again.
			Input.ParseInputEvent(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left, Pressed = false, Position = point, GlobalPosition = point });
			Input.FlushBufferedEvents();
			return;
		}
		Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = point, GlobalPosition = point });
		Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = point, GlobalPosition = point });
		Input.FlushBufferedEvents();
	}

	private static void VerifyRules()
	{
		TutorialStep pointCards = TutorialCatalog.Lessons[1].Steps[0];
		Check(pointCards.Id == "point-cards", "The second lesson must begin by introducing all scoring cards.");
		string[] scoringCards = { "Heart2", "HeartK", "HeartA", "SpadeQ" };
		foreach (string cardId in scoringCards)
		{
			var round = new TutorialRound(pointCards);
			for (int seat = 1; seat <= 3; seat++) round.Play(CardRules.Parse(pointCards.BotCards[seat]));
			Check(round.LegalCards().Select(CardRules.Id).ToHashSet().SetEquals(scoringCards), "The introductory exercise should allow every held scoring card.");
			round.Play(CardRules.Parse(cardId));
			Check(round.Complete && round.Winner == 1 && round.TrickPoints == (cardId == "SpadeQ" ? 6 : 1), "Wrong scoring-card value or collector in the introduction.");
		}
		using JsonDocument fixtures = JsonDocument.Parse(Godot.FileAccess.GetFileAsString("res://Tests/fixtures/legal-cards.json"));
		foreach (JsonElement fixture in fixtures.RootElement.EnumerateArray())
		{
			CardData[] hand = fixture.GetProperty("hand").EnumerateArray().Select(value => CardRules.Parse(value.GetString())).ToArray();
			CardData[] trick = fixture.GetProperty("trick").EnumerateArray().Select(value => CardRules.Parse(value.GetString())).ToArray();
			string[] expected = fixture.GetProperty("legal").EnumerateArray().Select(value => value.GetString()).ToArray();
			Check(CardRules.LegalCards(hand, trick.Length > 0 ? trick[0].Suit : null,
				fixture.GetProperty("firstTrick").GetBoolean(), fixture.GetProperty("heartsBroken").GetBoolean(), out bool mustDiscard,
				!fixture.TryGetProperty("heartsBreakingEnabled", out var heartsRule) || heartsRule.GetBoolean(),
				!fixture.TryGetProperty("mustDiscardPointsWhenVoid", out var discardRule) || discardRule.GetBoolean())
				.Select(CardRules.Id).SequenceEqual(expected), fixture.GetProperty("name").GetString());
			if (fixture.TryGetProperty("mustDiscardPoints", out var expectedDiscard))
				Check(mustDiscard == expectedDiscard.GetBoolean(), $"Incorrect discard hint: {fixture.GetProperty("name").GetString()}");
		}
		Check(CardRules.Payouts(new[] { 9, 12, 6, 0 }, 400).SequenceEqual(new[] { 240, 0, 160, 0 }), "Weighted payouts.");
		Check(CardRules.Payouts(new[] { 10, 10, 5, 5 }, 401).SequenceEqual(new[] { 0, 0, 201, 200 }), "Tied highest payouts.");
		Check(CardRules.Payouts(new[] { 14, 0, 0, 0 }, 400).SequenceEqual(new[] { 0, 134, 133, 133 }), "Zero-weight fallback.");
		Check(CardRules.Payouts(new[] { 3, 3, 3, 3 }, 401).SequenceEqual(new[] { 101, 100, 100, 100 }), "All-tied fallback.");
	}
	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
