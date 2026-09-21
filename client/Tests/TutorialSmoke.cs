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
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			GetTree().CurrentScene = null; // Keep the runner alive through returns to Intro.
			VerifyRules();
			_tutorial = GD.Load<PackedScene>("res://scenes/Tutorial.tscn").Instantiate<TutorialController>();
			_tutorial.SaveProgress = false;
			AddChild(_tutorial);
			Check(_tutorial.Phase == TutorialPhase.Animating, "The tutorial should start the lesson directly.");
			Check(_tutorial.FindChild("Lesson1", true, false) is null, "The entry still exposes multiple lessons.");
			Check(_tutorial.FindChild("Next", true, false) is null, "The tutorial still exposes a manual section-advance button.");
			Check(!_tutorial.GetNode<Control>("%InstructionPanel").IsVisibleInTree(), "Instructions must start hidden.");
			Table authoredTable = _tutorial.GetNode<Table>("Game/Table");
			await VerifyActionButtons();
			string[] expectedOrder = { "goal", "rules", "points", "pass" };
			await _tutorial.StartLessonAsync(0);
			for (int step = 0; step < expectedOrder.Length; step++)
			{
				string nextSection = step + 1 < expectedOrder.Length ? expectedOrder[step + 1] : null;
				TutorialRound sectionRound = _tutorial.Round;
				Check(_tutorial.Step.Id == expectedOrder[step], "The lesson must have four sections with tips inside passing.");
				Check(!_tutorial.ActiveTable.UseNetworkSession, "Tutorial attached to the online session.");
				Check(ReferenceEquals(_tutorial.ActiveTable, authoredTable), "The scene-authored table was replaced.");
				Check(authoredTable.Scale.IsEqualApprox(Vector2.One) && authoredTable.Position.IsZeroApprox() &&
					authoredTable.Size.IsEqualApprox(_tutorial.Size), "Tutorial table must fill the scene at its original scale.");
				Check(_tutorial.Phase == TutorialPhase.Guiding, "Each section should start with guidance.");
				if (_tutorial.Step.Kind == TutorialKind.Play)
				{
					CardControl[] initialCards = _tutorial.ActiveTable.LocalCardViews.ToArray();
					int leader = _tutorial.Step.Leader;
					Check(sectionRound.Trick.Count == 0, "Each exercise should explain its cards before the bots start.");
					await WaitForInput(stopAfterFirstPlay: true);
					Check(_tutorial.Phase == TutorialPhase.Guiding && sectionRound.Trick.Count == 1 && sectionRound.CurrentSeat == (leader + 1) % 4,
						"The lead-card guide must interrupt play immediately after the first player, before either following bot acts.");
					Check(!_tutorial.ActiveTable.IsLocalAnimating && _tutorial.ActiveTable.GetLocalPlayedCard(leader) is not null &&
						Enumerable.Range(0, 4).Where(seat => seat != leader).All(seat => _tutorial.ActiveTable.GetLocalPlayedCard(seat) is null),
						"The focused card must have landed, with the other play areas still empty.");
					await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
					Check(_tutorial.Phase == TutorialPhase.Guiding && sectionRound.Trick.Count == 1 &&
						Enumerable.Range(0, 4).Where(seat => seat != leader).All(seat => sectionRound.Hands[seat].Count == 3) && !_tutorial.ActiveTable.IsLocalAnimating,
						"Bots continued playing while the lead-card guide was waiting for the player.");
					await Capture($"{_tutorial.Step.Id}-lead-paused");
					await WaitForInput();
					Check(_tutorial.Phase == TutorialPhase.Play && !_tutorial.Round.Passed && _tutorial.Round.Trick.Count == (step == 1 ? 3 : 1),
						"Both rules and scoring must wait for the player's card without requiring a pass.");
					Check(_tutorial.StepIndex == step && ReferenceEquals(_tutorial.Round, sectionRound) &&
						_tutorial.ActiveTable.LocalCardViews.SequenceEqual(initialCards),
						"Explanations and practice must share the same section, round and actual hand cards.");
					Check(_tutorial.Round.Trick[0].Card == CardRules.Parse(step == 1 ? "Club2" : "Spade2"), "The practice led the wrong suit.");
					Check(_tutorial.ActiveTable.GetLocalPlayerInfo(0).RoundScore == 0, "Practice must start before any points are awarded.");
					var layout = _tutorial.ActiveTable.GetNode<MainHandLayout>("InGame/MainHandLayout");
					var legal = _tutorial.Round.LegalCards();
					Check(layout.PlayableCardIds.ToHashSet().SetEquals(legal.Select(CardRules.Id)), "Wrong card highlights.");
					Check(!_tutorial.TryPlay(_tutorial.Round.Hands[0].First(card => !legal.Contains(card))), "An off-suit play was accepted while holding the specified suit.");
					await WaitForInput();
					Check(_tutorial.Step.Id == expectedOrder[step] && _tutorial.Phase == TutorialPhase.Play, "Invalid playing advanced the tutorial.");
					int cardIndex = _tutorial.ActiveTable.LocalHand.ToList().IndexOf(legal[^1]);
					layout.EmitSignal(MainHandLayout.SignalName.CardPlayRequested, cardIndex);
					if (_tutorial.Step.Id == "points") await VerifyDoubledHearts();
					await WaitForInput(nextSection);
					Check(sectionRound.Complete && sectionRound.Winner == 0 && sectionRound.CurrentSeat == 0 &&
						sectionRound.Scores[0] == (step == 1 ? 0 : 10),
						"Rules practice must show who wins; scoring practice must award 10 points before passing.");
				}
				else if (_tutorial.Step.Kind == TutorialKind.Pass)
				{
					await WaitForInput();
					Check(_tutorial.Phase == TutorialPhase.Pass && _tutorial.Round.Trick.Count == 0, "Cards were played before passing.");
					Check(_tutorial.Round.Hands.All(hand => hand.Count == 13), "The exchange must begin with 13 cards per player.");
					var layout = _tutorial.ActiveTable.GetNode<MainHandLayout>("InGame/MainHandLayout");
					layout.ApplyPassSelection(_tutorial.Round.Hands[0].Take(2).Select(CardRules.Id), false);
					Check(!layout.SubmitPassSelection(), "Two-card pass was accepted.");
					Check(!_tutorial.TryPass(_tutorial.Round.Hands[0].Take(2).ToArray()), "Invalid pass bypassed validation.");
					await WaitForInput();
					Check(_tutorial.Step.Id == "pass" && _tutorial.Phase == TutorialPhase.Pass && !_tutorial.Round.Passed,
						"Invalid passing advanced the tutorial.");
					CardData[] selected = _tutorial.Round.Hands[0].Take(3).ToArray();
					layout.ApplyPassSelection(selected.Select(CardRules.Id), false);
					Check(layout.SubmitPassSelection(), "Passing signal did not fire.");
					await WaitForInput("pass");
					Check(_tutorial.Round.Passed && _tutorial.Phase == TutorialPhase.Guiding, "Passing did not show its result.");
					Check(_tutorial.GuidePageCount == 3, "The pass result must include the goal reminder and completion before finishing the lesson.");
					Check(_tutorial.Round.Hands[0].Count == 13 && _tutorial.ActiveTable.LocalHand.ToHashSet().SetEquals(_tutorial.Round.Hands[0]),
						"The pass result must retain the actual 13-card hand received after exchanging.");
					await WaitForInput(nextSection);
					Check(sectionRound.Trick.Count == 0, "Passing must proceed to tips without replaying the scoring practice.");
				}
				else
				{
					await WaitForInput(nextSection);
					Check(sectionRound.Trick.Count == 0, "A reading section unexpectedly played cards.");
				}
				if (nextSection is not null)
					Check(_tutorial.Phase == TutorialPhase.Guiding && _tutorial.Step.Id == nextSection && _tutorial.GuidePageIndex == 0,
						"The next section did not start automatically at its first dialogue page.");
				else Check(_tutorial.Phase == TutorialPhase.Exiting, "The final dialogue did not finish the lesson automatically.");
				GD.Print($"TUTORIAL_SECTION_OK: {expectedOrder[step]}");
			}
			Check(_tutorial.Phase == TutorialPhase.Exiting, "Finishing the lesson did not exit automatically.");
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			Check(GetTree().CurrentScene?.SceneFilePath == "res://scenes/Intro.tscn", "Finishing the lesson did not return to Intro.");
			await VerifyRulesOutcomes();
			await _tutorial.StartLessonAsync(0);
			await WaitForInput("rules");
			Check(_tutorial.Step.Id == "rules" && _tutorial.Phase == TutorialPhase.Guiding, "The goal must lead straight into the rules on replay.");
			await WaitForInput();
			Check(_tutorial.Step.Id == "rules" && _tutorial.Phase == TutorialPhase.Play, "The second section must stop for a practice round.");
			// Re-enter while a deal is in flight, then leave while passing is in flight.
			Task abandoned = _tutorial.StartLessonAsync(0, 2);
			await _tutorial.StartLessonAsync(0, 3);
			await abandoned;
			await WaitForInput();
			Check(_tutorial.Step.Id == "pass" && _tutorial.Phase == TutorialPhase.Pass, "Old scene work leaked into a restarted section.");
			Check(_tutorial.TryPass(_tutorial.Round.Hands[0].Take(3).ToArray()), "Could not start exit test pass.");
			await ClickAction(_tutorial.GetNode<BaseButton>("Game/Table/TableActions/Quit/ReturnToLobbyButton"));
			await ToSignal(GetTree().CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
			Check(_tutorial.Phase == TutorialPhase.Exiting && _tutorial.ActiveTable is null, "Exit did not cancel the lesson.");
			// A dismissed played-card guide must not resume bots in a restarted section.
			await _tutorial.StartLessonAsync(0, 1);
			await WaitForInput(stopAfterFirstPlay: true);
			ClickGuide();
			await _tutorial.StartLessonAsync(0, 2);
			await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
			Check(_tutorial.Step.Id == "points" && _tutorial.Phase == TutorialPhase.Guiding &&
				_tutorial.Round.Trick.Count == 0 && _tutorial.GuidePageIndex == 0,
				"A stale played-card guide resumed bots after restarting the section.");
			// Cancel a pending final-guide click before its deferred input unlock runs.
			await _tutorial.StartLessonAsync(0);
			while (_tutorial.GuidePageIndex + 1 < _tutorial.GuidePageCount)
			{
				ClickGuide();
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			}
			ClickGuide();
			_tutorial.ReturnToIntro();
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			Check(_tutorial.Phase == TutorialPhase.Exiting && !_tutorial.GetNode<TutorialSpotlight>("%GuideOverlay").Visible,
				"Deferred guidance completion escaped into a cancelled lesson.");
			// Restart while automatic advancement is queued; the old callback must not skip the new opening.
			await _tutorial.StartLessonAsync(0);
			while (_tutorial.GuidePageIndex + 1 < _tutorial.GuidePageCount) ClickGuide();
			ClickGuide();
			await _tutorial.StartLessonAsync(0);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			Check(_tutorial.Step.Id == "goal" && _tutorial.Phase == TutorialPhase.Guiding && _tutorial.GuidePageIndex == 0,
				"A stale automatic transition skipped dialogue after restarting.");
			Check(ReferenceEquals(GameSession.GameAdapter, sentinel) && ReferenceEquals(GameSession.Profile, previousProfile), "Tutorial mutated online session/profile.");
			GD.Print("TUTORIAL_SMOKE_OK: player queen precedes two doubled hearts, 6+2+2=10 points, paused per-card guides, accurate rules outcomes, safe resume and cancellation, shared scoring hand, uncovered deals, passing and session isolation");
		}
		catch (Exception exception) { exitCode = 1; GD.PushError(exception.ToString()); }
		finally
		{
			if (IsInstanceValid(_tutorial)) _tutorial.QueueFree();
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			GameSession.GameAdapter = previousAdapter;
			Engine.TimeScale = 1;
			GetTree().Quit(exitCode);
		}
	}
	private async Task VerifyActionButtons()
	{
		await _tutorial.StartLessonAsync(0);
		Check(_tutorial.FindChild("Navigation", true, false) is null, "The old navigation panel is still present.");
		var last = _tutorial.GetNode<BaseButton>("%LastSecButton");
		var retry = _tutorial.GetNode<BaseButton>("%RetryButton");
		var mute = _tutorial.GetNode<BaseButton>("Game/Table/TableActions/Mute/MuteButton");
		var exit = _tutorial.GetNode<BaseButton>("Game/Table/TableActions/Quit/ReturnToLobbyButton");
		Check(last.Disabled && retry.IsVisibleInTree() && mute.IsVisibleInTree() && exit.IsVisibleInTree(),
			"The first section must expose the actions and disable previous section.");
		await ClickAction(last);
		Check(_tutorial.StepIndex == 0 && _tutorial.GuidePageIndex == 0, $"Disabled previous-section input advanced the guide: step={_tutorial.StepIndex}, page={_tutorial.GuidePageIndex}, bounds={last.GetGlobalRect()}, transform={last.GetGlobalTransformWithCanvas()}.");
		bool wasMuted = AudioServer.IsBusMute(0);
		try
		{
			await ClickAction(mute);
			Check(AudioServer.IsBusMute(0) != wasMuted && mute.ButtonPressed != wasMuted && _tutorial.GuidePageIndex == 0,
				"The mute action was blocked by guidance or also advanced it.");
			await ClickAction(mute);
			Check(AudioServer.IsBusMute(0) == wasMuted && _tutorial.GuidePageIndex == 0, "The mute action did not restore audio.");
		}
		finally { AudioServer.SetBusMute(0, wasMuted); }
		ClickGuide();
		TutorialRound previous = _tutorial.Round;
		await ClickAction(retry);
		Check(!ReferenceEquals(previous, _tutorial.Round) && _tutorial.StepIndex == 0 && _tutorial.GuidePageIndex == 0,
			"Retry must restart the current section at its first explanation.");
		await _tutorial.StartLessonAsync(0, 2);
		Check(!last.Disabled, "Previous section stayed disabled beyond the first section.");
		await ClickAction(last);
		await WaitForInput("rules");
		Check(_tutorial.StepIndex == 1 && _tutorial.GuidePageIndex == 0, "Previous section did not restart the preceding section.");
		Task abandoned = _tutorial.StartLessonAsync(0, 3);
		await ClickAction(retry);
		await ClickAction(last);
		await abandoned;
		await WaitForInput("points");
		Check(_tutorial.StepIndex == 2 && _tutorial.Round.Trick.Count == 0 && _tutorial.GuidePageIndex == 0,
			"Actions during dealing leaked old work into the preceding section.");
		GD.Print("TUTORIAL_ACTIONS_OK: previous section, retry, mute, disabled first-section action, cancellation during dealing");
	}

	private async Task ClickAction(BaseButton button)
	{
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Vector2 point = button.GetGlobalTransformWithCanvas() * (button.Size * 0.5f);
		GetViewport().PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point }, true);
		GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = point, GlobalPosition = point }, true);
		GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = point, GlobalPosition = point }, true);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}

	private async Task VerifyDoubledHearts()
	{
		foreach (var (count, seat, cardId, points) in new[] { (3, 1, "Heart3", 2), (4, 2, "Heart5", 2) })
		{
			await WaitForInput("points");
			var round = _tutorial.Round;
			var guide = _tutorial.GetNode<TutorialSpotlight>("%GuideOverlay");
			Check(_tutorial.Phase == TutorialPhase.Guiding && round.Trick.Count == count && round.QueenPlayed &&
				round.Trick[^1] == new TutorialPlay(seat, CardRules.Parse(cardId), points),
					$"The scoring demonstration must play the queen, then two hearts worth two points each: expected={count}, phase={_tutorial.Phase}, trick={round.Trick.Count}, page={_tutorial.GuidePageIndex}.");
			Check(!_tutorial.ActiveTable.IsLocalAnimating && guide.Targets.Count == 1 &&
				ReferenceEquals(guide.Targets[0], _tutorial.ActiveTable.GetLocalPlayedCard(seat)),
				"Each scoring card must be explained and focused as soon as it lands.");
			Check(guide.CurrentText.Contains("2分"), "The scoring-card guide must show the doubled heart value.");
			Check(_tutorial.ActiveTable.GetLocalPlayerInfo(0).RoundScore == 0, "Points must only be awarded after collecting the trick.");
			await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
			Check(_tutorial.Phase == TutorialPhase.Guiding && round.Trick.Count == count && !_tutorial.ActiveTable.IsLocalAnimating,
				"Playing or collection continued before the scoring explanation was dismissed.");
			var instruction = _tutorial.GetNode<RichTextLabel>("%Instruction");
			Check(instruction.GetContentHeight() <= instruction.Size.Y + 1, "The doubled-heart explanation was clipped.");
			await Capture($"points-doubled-{cardId}");
			ClickGuide();
		}
	}

	private async Task VerifyRulesOutcomes()
	{
		foreach (var (cardId, winner, winningCard) in new[] { ("Club8", 3, "♣10"), ("ClubK", 0, "♣K") })
		{
			await _tutorial.StartLessonAsync(0, 1);
			await WaitForInput();
			var layout = _tutorial.ActiveTable.GetNode<MainHandLayout>("InGame/MainHandLayout");
			int cardIndex = _tutorial.ActiveTable.LocalHand.ToList().IndexOf(CardRules.Parse(cardId));
			layout.EmitSignal(MainHandLayout.SignalName.CardPlayRequested, cardIndex);
			await WaitForInput("rules");
			var round = _tutorial.Round;
			var guide = _tutorial.GetNode<TutorialSpotlight>("%GuideOverlay");
			Check(round.Complete && round.Winner == winner && round.CurrentSeat == winner,
				$"{cardId}: wrong trick winner or next leader.");
			Check(guide.Targets.Count == 1 && ReferenceEquals(guide.Targets[0], _tutorial.ActiveTable.GetLocalPlayerInfo(winner)),
				$"{cardId}: result must focus the actual winning player.");
			Check(guide.CurrentText.Contains(CardRules.Display(CardRules.Parse(cardId))) && guide.CurrentText.Contains(winningCard),
				$"{cardId}: result must name the actual played cards.");
			Check(guide.CurrentText.Contains("你赢得了这一轮") == (winner == 0), "A losing play was described as the player's win.");
			if (winner != 0)
				Check(guide.CurrentText.Contains($"{TutorialCatalog.SeatNames[winner]}获胜"),
					"Losing feedback must name the actual winning player.");
			await Capture($"rules-result-{cardId}");
			await WaitForInput("points");
			Check(_tutorial.Step.Id == "points" && _tutorial.Phase == TutorialPhase.Guiding,
				"Either practice outcome should continue after explaining the actual result.");
		}
		_tutorial.ReturnToIntro();
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

	private async Task WaitForInput(string stopAtSection = null, bool stopAfterFirstPlay = false)
	{
		ulong start = Time.GetTicksMsec();
		int pages = 0;
		string transitioningFrom = null;
		int uncoveredDealFrames = 0;
		while (_tutorial.Phase is TutorialPhase.Animating or TutorialPhase.Guiding)
		{
			if (Time.GetTicksMsec() - start > 15000) throw new TimeoutException("Tutorial step timed out.");
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			if (transitioningFrom is not null)
			{
				bool needsDeal = _tutorial.Step.Hands[0].Length > 0;
				bool showGuide = _tutorial.Step.Id == transitioningFrom || _tutorial.Phase == TutorialPhase.Guiding || !needsDeal;
				Check(_tutorial.GetNode<TutorialSpotlight>("%GuideOverlay").IsVisibleInTree() == showGuide &&
					_tutorial.GetNode<Control>("%InstructionPanel").IsVisibleInTree() == showGuide,
					"Dealing must hide both shade and dialogue; guidance and reading transitions must show both together.");
				if (!showGuide && _tutorial.ActiveTable.IsDealing) uncoveredDealFrames++;
				if (_tutorial.Phase == TutorialPhase.Guiding && _tutorial.Step.Id != transitioningFrom)
				{
					Check(!needsDeal || uncoveredDealFrames > 0, "The next section never showed an uncovered deal animation.");
					transitioningFrom = null;
				}
			}
			if (_tutorial.Phase != TutorialPhase.Guiding) continue;
			if (_tutorial.Step.Id == stopAtSection) return;
			if (stopAfterFirstPlay && _tutorial.Round.Trick.Count > 0) return;
			Check(++pages < 20, "Guidance did not finish.");
			var guide = _tutorial.GetNode<TutorialSpotlight>("%GuideOverlay");
			bool isPlayedCardGuide = guide.Targets.Any(target => Enumerable.Range(0, 4)
				.Any(seat => ReferenceEquals(target, _tutorial.ActiveTable.GetLocalPlayedCard(seat))));
			var instruction = _tutorial.GetNode<RichTextLabel>("%Instruction");
			var hand = _tutorial.ActiveTable.GetNode<MainHandLayout>("InGame/MainHandLayout");
			Check(!hand.SelectionEnabled && !_tutorial.ActiveTable.CanMainPlayerPlay, "Cards are interactive during guidance.");
			Check(guide.Visible && guide.FocusRects.All(rect => rect.Size.X > 0 && rect.Size.Y > 0), "Invalid focus bounds.");
			bool readingTips = _tutorial.Step.Id == "pass" && _tutorial.Round.Passed && _tutorial.GuidePageIndex > 0;
			Check((_tutorial.Step.Id == "goal" || readingTips) ? guide.FocusRects.Count == 0 : guide.FocusRects.Count > 0,
				"Reading pages should have no spotlight; card explanations must highlight their targets.");
			if (readingTips)
				Check(_tutorial.StepIndex == 3 && TutorialCatalog.Lessons[_tutorial.LessonIndex].Steps.Length == 4 &&
					!_tutorial.ActiveTable.IsLocalAnimating && _tutorial.ActiveTable.LocalHand.Count == 13 &&
					_tutorial.ActiveTable.LocalHand.ToHashSet().SetEquals(_tutorial.Round.Hands[0]),
					"Tips must continue in section four with the exchanged hand and no new deal.");
			if (_tutorial.Step.Id == "points" && _tutorial.Round.Trick.Count == 0)
			{
				string[][] expectedTargets = { new[] { "Heart2", "HeartK" }, new[] { "SpadeQ" }, new[] { "Heart2", "HeartK", "SpadeQ" } };
				Check(guide.Targets.OfType<CardControl>().Select(card => CardRules.Id(card.Data)).ToHashSet()
					.SetEquals(expectedTargets[_tutorial.GuidePageIndex]), "The scoring-card explanation highlights the wrong cards.");
			}
			if (isPlayedCardGuide)
				Check(!_tutorial.ActiveTable.IsLocalAnimating && guide.Targets.Count == 1 &&
					ReferenceEquals(guide.Targets[0], _tutorial.ActiveTable.GetLocalPlayedCard(_tutorial.Round.Trick[^1].Seat)),
					"A played-card explanation must highlight the latest card immediately after it lands.");
			Check(!guide.CurrentText.Contains("[color") && !guide.CurrentText.Contains("[/color]"), "Color markup leaked into displayed copy.");
			Check(instruction.GetContentHeight() <= instruction.Size.Y + 1, "Tutorial copy was clipped.");
			if (_tutorial.Round.Complete && !isPlayedCardGuide)
				Check(_tutorial.ActiveTable.GetNode<PlayerInfo>(new[] { "MainPlayerInfo", "PlayerInfo", "PlayerInfo2", "PlayerInfo3" }[_tutorial.Round.Winner])
					.RoundScore == _tutorial.Round.TrickPoints, "Round points were not presented before advancing to passing.");
			string stage = isPlayedCardGuide ? $"after-play-{_tutorial.Round.Trick.Count}" : _tutorial.Round.Complete || _tutorial.Round.Passed ? "result" : _tutorial.Round.Trick.Count > 0 ? "action" : "intro";
			await Capture($"{_tutorial.Step.Id}-{stage}-{_tutorial.GuidePageIndex}");
			int beforeCards = _tutorial.Round.Hands[0].Count;
			int beforePlays = _tutorial.Round.Trick.Count;
			int beforePage = _tutorial.GuidePageIndex;
			Check(!_tutorial.TryPlay(CardRules.Parse("DiamondJ")) && !_tutorial.TryPass(Array.Empty<CardData>()), "Actions bypassed guidance.");
			if (beforePage + 1 == _tutorial.GuidePageCount && _tutorial.StepIndex + 1 < TutorialCatalog.Lessons[0].Steps.Length &&
				(_tutorial.Step.Kind == TutorialKind.Explanation || (_tutorial.Round.Complete && !isPlayedCardGuide) || _tutorial.Round.Passed))
			{
				transitioningFrom = _tutorial.Step.Id;
				uncoveredDealFrames = 0;
			}
			ClickGuide();
			if (_tutorial.Round.Passed && beforePage + 1 < _tutorial.GuidePageCount)
				Check(guide.Visible && _tutorial.GetNode<Control>("%InstructionPanel").Visible && _tutorial.Phase == TutorialPhase.Guiding,
					"Passing must flow directly into tips without hiding the shade or dialogue.");
			if (transitioningFrom is not null)
				Check(guide.Visible && _tutorial.GetNode<Control>("%InstructionPanel").Visible, "The final click must retain the overlay until the next section starts.");
			Check(_tutorial.GuidePageIndex == beforePage + 1, "One click should advance exactly one explanation.");
			Check(_tutorial.Round.Hands[0].Count == beforeCards && _tutorial.Round.Trick.Count == beforePlays,
				"Advancing guidance accidentally played a card.");
		}
		Check(_tutorial.Phase != TutorialPhase.Error, "Tutorial reported an error.");
		if (_tutorial.Step.Kind == TutorialKind.Play && _tutorial.Phase == TutorialPhase.Play)
			await Capture($"{_tutorial.Step.Id}-play");
		if (_tutorial.ActiveTable is Table table)
		{
			foreach (string name in new[] { "MainPlayArea", "LeftPlayArea", "OppositePlayArea", "RightPlayArea" })
			{
				var area = table.GetNode<PlayArea>("InGame/" + name);
				if (area.PlayedCard is not CardControl played) continue;
				Vector2 expected = area.GetGlobalTransformWithCanvas() * area.GetCombinedPivotOffset();
				Vector2 center = played.GetGlobalTransformWithCanvas() * (played.Size * 0.5f);
				Vector2 pivot = played.GetGlobalTransformWithCanvas() * played.GetCombinedPivotOffset();
				Check(center.IsEqualApprox(expected) && pivot.IsEqualApprox(expected),
					$"{_tutorial.Step.Id}/{name}: card center {center} and pivot {pivot} must align with area pivot {expected}.");
			}
		}
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
		if (_tutorial.Step.Id == "points" && _tutorial.Round.Trick.Count == 0 && _tutorial.GuidePageIndex == 0)
		{
			GetViewport().PushInput(new InputEventScreenTouch { Index = 0, Pressed = true, Position = point }, true);
			GetViewport().PushInput(new InputEventScreenTouch { Index = 0, Pressed = false, Position = point }, true);
			// A synthesized mouse event from the same touch must not advance again.
			GetViewport().PushInput(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left, Pressed = false, Position = point, GlobalPosition = point }, true);
			return;
		}
		GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = point, GlobalPosition = point }, true);
		GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = point, GlobalPosition = point }, true);
	}

	private static void VerifyRules()
	{
		Check(TutorialCatalog.Lessons.Length == 1, "The beginner copy must be a single lesson.");
		foreach (TutorialStep practice in TutorialCatalog.Lessons[0].Steps.Where(step => step.Kind == TutorialKind.Play))
		{
			foreach (CardData choice in practice.Hands[0].Where(card => card.Suit == practice.Hands[practice.Leader][0].Suit))
			{
				var round = new TutorialRound(practice);
				while (round.CurrentSeat != 0) round.Play(round.LegalCards()[0]);
				round.Play(choice);
				while (!round.Complete) round.Play(round.LegalCards()[0]);
				int winner = practice.Id == "rules" && choice == CardRules.Parse("Club8") ? 3 : 0;
				Check(round.Complete && round.Winner == winner && round.CurrentSeat == winner && round.Scores[0] == (practice.Id == "rules" ? 0 : 10),
					"Practice choices must resolve the actual trick winner and give that player the next lead.");
				if (practice.Id == "points")
					Check(round.Trick.Select(play => play.Points).SequenceEqual(new[] { 0, 6, 2, 2 }) &&
						round.Trick.Select(play => CardRules.Id(play.Card)).SequenceEqual(new[] { "Spade2", "SpadeQ", "Heart3", "Heart5" }),
						"The scoring practice must double both hearts played after the player's queen.");
			}
		}
		TutorialStep exchange = TutorialCatalog.Lessons[0].Steps.Single(step => step.Kind == TutorialKind.Pass);
		Check(exchange.Hands.All(hand => hand.Length == 13) && exchange.Hands.SelectMany(hand => hand).Distinct().Count() == 52,
			"The exchange must use a complete standard deck.");
		for (int first = 0; first < 11; first++)
			for (int second = first + 1; second < 12; second++)
				for (int third = second + 1; third < 13; third++)
				{
					var round = new TutorialRound(exchange);
					round.Pass(new[] { exchange.Hands[0][first], exchange.Hands[0][second], exchange.Hands[0][third] });
					Check(round.Hands.All(hand => hand.Count == 13) && round.Hands.SelectMany(hand => hand).Distinct().Count() == 52, "Passing lost or duplicated cards.");
					Check(round.CurrentSeat == 1 && round.LegalCards().Contains(CardRules.Parse("Club2")), "A pass choice broke the Club2 example.");
				}
		Check(CardRules.Points(CardRules.Parse("Heart2"), false) == 1 && CardRules.Points(CardRules.Parse("HeartK"), false) == 1,
			"Hearts before the queen must be worth one point regardless of rank.");
		Check(CardRules.Points(CardRules.Parse("SpadeQ"), false) == 6 && CardRules.Points(CardRules.Parse("Heart2"), true) == 2,
			"Queen and subsequent heart values must match the copy.");
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
