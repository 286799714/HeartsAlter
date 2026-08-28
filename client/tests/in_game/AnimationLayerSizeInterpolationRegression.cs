using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.InGame.AnimationLayer;
using HeartsAlter.Scripts.InGame.Card;

public partial class AnimationLayerSizeInterpolationRegression : Node
{
	private const float FlightDuration = 0.45f;
	private const float Tolerance = 0.1f;
	private const int MaxFramesPerCase = 120;

	private static readonly Vector2 SourceSize = new(120.0f, 167.0f);

	private AnimationLayer _animationLayer = null!;
	private PackedScene _cardScene = null!;

	public override void _Ready()
	{
		CallDeferred(MethodName.RunRegression);
	}

	private async void RunRegression()
	{
		try
		{
			_animationLayer = new AnimationLayer
			{
				Name = "AnimationLayer",
				Size = new Vector2(1920.0f, 1080.0f),
				CardFlightDuration = FlightDuration,
				CardFlipDuration = 0.0f,
				FlightTransition = Tween.TransitionType.Linear,
				FlightEase = Tween.EaseType.InOut,
			};
			AddChild(_animationLayer);

			_cardScene = GD.Load<PackedScene>("res://scenes/in_game/Card.tscn");
			if (_cardScene is null)
				throw new InvalidOperationException("Unable to load Card.tscn.");

			OtherHandLayout otherHand = new()
			{
				Name = "OtherHand",
				Size = new Vector2(1000.0f, 105.0f),
				LayoutTweenDuration = 0.0f,
			};
			AddChild(otherHand);

			MainHandLayout mainHand = new()
			{
				Name = "MainHand",
				Position = new Vector2(0.0f, 300.0f),
				Size = new Vector2(1000.0f, 665.0f),
				LayoutTweenDuration = 0.0f,
				SelectionTweenDuration = 0.0f,
			};
			AddChild(mainHand);

			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

			await RunCase(
				"shrink-to-other-hand",
				startFaceUp: false,
				otherHand.GetCurrentReceivePose,
				otherHand.ReceiveCard
			);
			await RunCase(
				"grow-to-main-hand",
				startFaceUp: true,
				mainHand.GetCurrentReceivePose,
				mainHand.ReceiveCard
			);

			GD.Print("[SIZE-INTERPOLATION-REGRESSION] PASS");
			GetTree().Quit(0);
		}
		catch (Exception exception)
		{
			GD.PushError(
				$"[SIZE-INTERPOLATION-REGRESSION] FAIL: {exception}"
			);
			GetTree().Quit(1);
		}
	}

	private async Task RunCase(
		string label,
		bool startFaceUp,
		Func<Card, CardPose2D> getTargetPose,
		Action<Card> receiveCard)
	{
		Card card = _cardScene.Instantiate<Card>();
		_animationLayer.AddChild(card);
		card.Setup(
			new CardData(PokerSuit.Club, PokerRank.Two),
			startFaceUp
		);

		CardPose2D sourcePose = new(
			new Transform2D(0.0f, new Vector2(50.0f, 50.0f)),
			SourceSize,
			startFaceUp
		);
		CardPose2D targetPose = getTargetPose(card);
		if (targetPose.IsFaceUp != startFaceUp)
		{
			throw new InvalidOperationException(
				$"{label}: test must disable the independent flip animation."
			);
		}

		List<float> sampledHeights = new() { sourcePose.Size.Y };
		bool completed = false;
		Vector2 beforeReceive = Vector2.Zero;
		Vector2 afterReceive = Vector2.Zero;

		bool started = _animationLayer.PlayCardToPose(
			card,
			sourcePose,
			targetPose,
			animatedCard =>
			{
				beforeReceive = animatedCard.Size;
				receiveCard(animatedCard);
				afterReceive = animatedCard.Size;
				completed = true;
			}
		);
		if (!started)
			throw new InvalidOperationException($"{label}: flight was rejected.");

		for (int frame = 0; frame < MaxFramesPerCase && !completed; frame++)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			if (!GodotObject.IsInstanceValid(card))
				throw new InvalidOperationException($"{label}: card was freed in flight.");

			SampleVisualSizes(label, card);
			sampledHeights.Add(card.Size.Y);
		}

		if (!completed)
			throw new TimeoutException($"{label}: flight did not complete.");

		sampledHeights.Add(beforeReceive.Y);
		AssertNear(beforeReceive, targetPose.Size, $"{label}: pre-receive size");
		AssertNear(afterReceive, targetPose.Size, $"{label}: post-receive size");
		AssertNear(
			beforeReceive,
			afterReceive,
			$"{label}: landing must not change size"
		);
		AssertHasIntermediateValue(
			sampledHeights,
			sourcePose.Size.Y,
			targetPose.Size.Y,
			label
		);
		AssertContinuous(
			sampledHeights,
			sourcePose.Size.Y,
			targetPose.Size.Y,
			label
		);

		GD.Print(
			$"[SIZE-INTERPOLATION-REGRESSION] {label} " +
			$"source={sourcePose.Size} target={targetPose.Size} " +
			$"samples={sampledHeights.Count}"
		);
	}

	private static void SampleVisualSizes(string label, Card card)
	{
		Control visual = card.GetNode<Control>("CardVisual");
		TextureRect face = card.GetNode<TextureRect>("CardVisual/Face");
		TextureRect back = card.GetNode<TextureRect>("CardVisual/Back");

		AssertNear(visual.Size, card.Size, $"{label}: CardVisual size");
		AssertNear(face.Size, card.Size, $"{label}: face size");
		AssertNear(back.Size, card.Size, $"{label}: back size");

		if (face.Material is not ShaderMaterial faceShader ||
			back.Material is not ShaderMaterial backShader)
		{
			throw new InvalidOperationException($"{label}: missing card shader.");
		}

		AssertNear(
			faceShader.GetShaderParameter("rect_size").AsVector2(),
			card.Size,
			$"{label}: face shader rect_size"
		);
		AssertNear(
			backShader.GetShaderParameter("rect_size").AsVector2(),
			card.Size,
			$"{label}: back shader rect_size"
		);
	}

	private static void AssertHasIntermediateValue(
		IReadOnlyList<float> values,
		float source,
		float target,
		string label)
	{
		float lower = Mathf.Min(source, target) + Tolerance;
		float upper = Mathf.Max(source, target) - Tolerance;
		foreach (float value in values)
		{
			if (value > lower && value < upper)
				return;
		}

		throw new InvalidOperationException(
			$"{label}: no interpolated size between {source} and {target}."
		);
	}

	private static void AssertContinuous(
		IReadOnlyList<float> values,
		float source,
		float target,
		string label)
	{
		float totalChange = Mathf.Abs(target - source);
		float maximumAllowedStep = totalChange * 0.25f + Tolerance;
		for (int i = 1; i < values.Count; i++)
		{
			float step = Mathf.Abs(values[i] - values[i - 1]);
			if (step > maximumAllowedStep)
			{
				throw new InvalidOperationException(
					$"{label}: size jumped by {step} between samples " +
					$"{i - 1} and {i}."
				);
			}
		}
	}

	private static void AssertNear(Vector2 actual, Vector2 expected, string label)
	{
		if (!actual.IsEqualApprox(expected) &&
			(actual - expected).Length() > Tolerance)
		{
			throw new InvalidOperationException(
				$"{label}: expected {expected}, got {actual}."
			);
		}
	}
}
