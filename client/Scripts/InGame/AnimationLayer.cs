using System;
using System.Collections.Generic;
using Godot;
using HeartsAlter.Scripts.InGame.Card;
using CardPose2D = HeartsAlter.Scripts.InGame.Card.CardPose2D;

namespace HeartsAlter.Scripts.InGame;

/// <summary>
/// Shared overlay for transient card animations.
///
/// A pose is expressed in canvas coordinates. Every flight converts its two
/// poses into this layer's local coordinate system and animates a small Node2D
/// carrier. The carrier keeps the complete Transform2D (including rotation and
/// scale), with its origin aligned to the card center while the Card itself
/// remains an ordinary, top-left anchored Control. Multiple carriers are
/// independent, so rapid draw requests can be shown at the same time.
/// </summary>
public partial class AnimationLayer : Control
{
	[ExportCategory("Card Animation")]
	[ExportGroup("Draw / Deal")]
	[Export(PropertyHint.Range, "0,3,0.01,or_greater,suffix:s")]
	public float DrawFlightDuration = 0.55f;

	[Export]
	public Tween.TransitionType DrawFlightTransition =
		Tween.TransitionType.Sine;

	[Export]
	public Tween.EaseType DrawFlightEase = Tween.EaseType.InOut;

	[Export(PropertyHint.Range, "0,3,0.01,or_greater,suffix:s")]
	public float DrawFlipDuration = 0.28f;

	[Export]
	public Tween.TransitionType DrawFlipTransition =
		Tween.TransitionType.Sine;

	[Export]
	public Tween.EaseType DrawFlipEase = Tween.EaseType.InOut;

	[ExportGroup("Play")]
	[Export(PropertyHint.Range, "0,3,0.01,or_greater,suffix:s")]
	public float PlayFlightDuration = 0.55f;

	[Export]
	public Tween.TransitionType PlayFlightTransition =
		Tween.TransitionType.Sine;

	[Export]
	public Tween.EaseType PlayFlightEase = Tween.EaseType.InOut;

	[Export(PropertyHint.Range, "-4,4,1,suffix: turns")]
	public int PlayClockwiseTurns = 1;

	[Export(PropertyHint.Range, "0,3,0.01,or_greater,suffix:s")]
	public float PlayFlipDuration = 0.28f;

	[Export]
	public Tween.TransitionType PlayFlipTransition =
		Tween.TransitionType.Sine;

	[Export]
	public Tween.EaseType PlayFlipEase = Tween.EaseType.InOut;

	[ExportGroup("Passing")]
	[Export(PropertyHint.Range, "0,3,0.01,or_greater,suffix:s")]
	public float PassFlightDuration = 0.65f;

	[Export]
	public Tween.TransitionType PassFlightTransition = Tween.TransitionType.Sine;

	[Export]
	public Tween.EaseType PassFlightEase = Tween.EaseType.InOut;

	[Export(PropertyHint.Range, "0,3,0.01,or_greater,suffix:s")]
	public float PassFlipDuration = 0.25f;

	[Export]
	public Tween.TransitionType PassFlipTransition = Tween.TransitionType.Sine;

	[Export]
	public Tween.EaseType PassFlipEase = Tween.EaseType.InOut;

	[ExportGroup("Collect")]
	[Export(PropertyHint.Range, "0,3,0.01,or_greater,suffix:s")]
	public float CollectFlightDuration = 0.55f;

	[Export]
	public Tween.TransitionType CollectFlightTransition =
		Tween.TransitionType.Sine;

	[Export]
	public Tween.EaseType CollectFlightEase = Tween.EaseType.InOut;

	[Export(PropertyHint.Range, "0,3,0.01,or_greater,suffix:s")]
	public float CollectFlipDuration = 0.2f;

	[Export]
	public Tween.TransitionType CollectFlipTransition =
		Tween.TransitionType.Sine;

	[Export]
	public Tween.EaseType CollectFlipEase = Tween.EaseType.InOut;

	[Export(PropertyHint.Range, "-4,4,1,suffix: turns")]
	public int CollectClockwiseTurns;

	[Signal]
	public delegate void CardAnimationCompletedEventHandler();

	private readonly record struct AnimationSettings(
		float FlightDuration,
		Tween.TransitionType FlightTransition,
		Tween.EaseType FlightEase,
		float FlipDuration,
		Tween.TransitionType FlipTransition,
		Tween.EaseType FlipEase,
		int ClockwiseTurns
	);

	private sealed class FlightState
	{
		public CardControl Card = null!;
		public Node2D Carrier = null!;
		public Node2D SpinCarrier = null!;
		public Tween FlightTween = null!;
		public CardControl.FlipCompletedEventHandler FlipHandler = null!;
		public Action<CardControl> Completion = null!;
		public AnimationSettings Settings;
		public bool FlightFinished;
		public bool FlipFinished;
	}

	private readonly List<FlightState> _flights = new();

	/// <summary>
	/// First active card, retained as a convenience for single-card callers.
	/// Use <see cref="ActiveCards"/> when several cards are flying.
	/// </summary>
	public CardControl ActiveCard =>
		_flights.Count > 0 ? _flights[0].Card : null!;

	/// <summary>Snapshot of all cards currently owned by this layer.</summary>
	public IReadOnlyList<CardControl> ActiveCards
	{
		get
		{
			List<CardControl> cards = new();
			foreach (FlightState state in _flights)
			{
				if (state.Card is not null &&
					GodotObject.IsInstanceValid(state.Card))
				{
					cards.Add(state.Card);
				}
			}

			return cards;
		}
	}

	/// <summary>True when one or more flights are still active.</summary>
	public bool IsAnimating => _flights.Count > 0;

	public override void _ExitTree()
	{
		// Stop all transient work if the table is removed while cards are moving.
		FlightState[] states = _flights.ToArray();
		foreach (FlightState state in states)
			CancelState(state, freeCard: true);

		_flights.Clear();
	}

	/// <summary>Animates a card drawn/dealt from the deck to a hand.</summary>
	public bool PlayDrawToPose(
		CardControl card,
		CardPose2D sourcePose,
		CardPose2D targetPose,
		Action<CardControl> completed = null)
	{
		return PlayToPose(
			card,
			sourcePose,
			targetPose,
			CreateDrawSettings(),
			startDelay: 0.0f,
			completed: completed
		);
	}

	/// <summary>Animates a card played from a hand to its play area.</summary>
	public bool PlayCardToPose(
		CardControl card,
		CardPose2D sourcePose,
		CardPose2D targetPose,
		Action<CardControl> completed = null)
	{
		return PlayToPose(
			card,
			sourcePose,
			targetPose,
			CreatePlaySettings(),
			startDelay: 0.0f,
			completed: completed
		);
	}

	/// <summary>Animates one card from a hand to its passing recipient.</summary>
	public bool PlayPassToPose(
		CardControl card,
		CardPose2D sourcePose,
		CardPose2D targetPose,
		Action<CardControl> completed = null)
	{
		return PlayToPose(
			card,
			sourcePose,
			targetPose,
			CreatePassSettings(),
			startDelay: 0.0f,
			completed: completed
		);
	}

	/// <summary>Animates a completed trick from a play area to its collector.</summary>
	public bool PlayCollectToPose(
		CardControl card,
		CardPose2D sourcePose,
		CardPose2D targetPose,
		Action<CardControl> completed = null)
	{
		return PlayCollectToPose(
			card,
			sourcePose,
			targetPose,
			startDelay: 0.0f,
			completed: completed
		);
	}

	/// <summary>
	/// Owns a completed trick immediately, holds it at its exact source pose for
	/// <paramref name="startDelay"/>, then animates it to the collector.
	/// </summary>
	public bool PlayCollectToPose(
		CardControl card,
		CardPose2D sourcePose,
		CardPose2D targetPose,
		float startDelay,
		Action<CardControl> completed = null)
	{
		return PlayToPose(
			card,
			sourcePose,
			targetPose,
			CreateCollectSettings(),
			startDelay,
			completed
		);
	}

	/// <summary>
	/// Animates a card between two canvas poses. Settings are snapshotted per
	/// flight, so simultaneous draw and play animations remain independent.
	/// </summary>
	private bool PlayToPose(
		CardControl card,
		CardPose2D sourcePose,
		CardPose2D targetPose,
		AnimationSettings settings,
		float startDelay,
		Action<CardControl> completed)
	{
		if (card is null ||
			!GodotObject.IsInstanceValid(card) ||
			!IsInsideTree() ||
			card.GetParent() != this ||
			FindFlight(card) is not null ||
			!IsValidSize(sourcePose.Size) ||
			!IsValidSize(targetPose.Size))
		{
			return false;
		}

		// CardPose2D stores the card's top-left transform. Convert both poses to
		// center transforms before interpolation so a rotation follows the card
		// center instead of making the center orbit around a corner when the
		// source and destination orientations differ.
		Transform2D sourceTransform = ToCenterTransform(
			CanvasToLocal(sourcePose.CanvasTransform),
			sourcePose.Size
		);
		Transform2D targetTransform = ToCenterTransform(
			CanvasToLocal(targetPose.CanvasTransform),
			targetPose.Size
		);

		// A Control's Transform2D setter is not exposed by GodotSharp. Normalize
		// the card and animate a Node2D carrier instead, preserving the complete
		// matrix even when the source/target controls are rotated or scaled. Keep
		// the decorative full-turn spin on a separate child: composing it into the
		// pose matrix can make Node2D decompose the final matrix as an equivalent
		// negative scale, which appears as a one-frame vertical mirror.
		card.SetAnchorsPreset(LayoutPreset.TopLeft, keepOffsets: true);
		card.Position = Vector2.Zero;
		card.Rotation = 0.0f;
		card.Scale = Vector2.One;
		// A duplicated Card may enter this layer with a scene/parent-derived
		// LayoutPosition. Reset both the logical layout state and the rendered
		// position before attaching it to the flight carrier; otherwise the
		// selection helper below reapplies that stale offset and moves the card
		// away from the carrier (often completely off-canvas).
		card.SetLayoutPosition(Vector2.Zero);
		card.SetSelectionLift(0.0f);

		Node2D carrier = new()
		{
			Name = $"{card.Name}_Flight",
			ZIndex = 1
		};
		AddChild(carrier);
		Node2D spinCarrier = new()
		{
			Name = $"{card.Name}_Spin"
		};
		carrier.AddChild(spinCarrier);
		card.Reparent(spinCarrier, keepGlobalTransform: false);

		FlightState state = new()
		{
			Card = card,
			Carrier = carrier,
			SpinCarrier = spinCarrier,
			Completion = completed,
			Settings = settings,
			FlightFinished = false,
			FlipFinished = card.IsFaceUp == targetPose.IsFaceUp
		};
		_flights.Add(state);

		card.Visible = true;
		ApplyPose(carrier, card, sourceTransform, sourcePose.Size);
		ApplySpin(spinCarrier, settings.ClockwiseTurns, 0.0f);

		float delay = Mathf.Max(0.0f, startDelay);
		if (!state.FlipFinished && delay <= 0.0f)
			StartFlip(state, targetPose.IsFaceUp);

		float duration = Mathf.Max(0.0f, settings.FlightDuration);
		if (duration <= 0.0f && delay <= 0.0f)
		{
			ApplyPose(carrier, card, targetTransform, targetPose.Size);
			ResetSpin(spinCarrier);
			state.FlightFinished = true;
		}
		else
		{
			Tween tween = CreateTween();
			state.FlightTween = tween;

			if (delay > 0.0f)
			{
				tween.TweenInterval(delay);
				if (!state.FlipFinished)
				{
					tween.TweenCallback(
						Callable.From(() =>
						{
							if (_flights.Contains(state) && !state.FlipFinished)
								StartFlip(state, targetPose.IsFaceUp);
						})
					);
				}
			}

			if (duration > 0.0f)
			{
				tween.TweenMethod(
					Callable.From<float>(progress =>
					{
						if (!GodotObject.IsInstanceValid(card) ||
							!GodotObject.IsInstanceValid(spinCarrier) ||
							!_flights.Contains(state))
						{
							return;
						}

						Transform2D interpolated = sourceTransform.InterpolateWith(
							targetTransform,
							progress
						);
						Vector2 interpolatedSize = sourcePose.Size.Lerp(
							targetPose.Size,
							progress
						);
						ApplyPose(carrier, card, interpolated, interpolatedSize);
						ApplySpin(spinCarrier, settings.ClockwiseTurns, progress);
					}),
					0.0f,
					1.0f,
					duration
				)
				.SetTrans(settings.FlightTransition)
				.SetEase(settings.FlightEase);
			}

			tween.TweenCallback(
				Callable.From(() => HandleFlightCompleted(
					state,
					targetTransform,
					targetPose.Size,
					tween
				))
			);
		}

		TryComplete(state);
		return true;
	}

	private AnimationSettings CreateDrawSettings()
	{
		return new AnimationSettings(
			DrawFlightDuration,
			DrawFlightTransition,
			DrawFlightEase,
			DrawFlipDuration,
			DrawFlipTransition,
			DrawFlipEase,
			ClockwiseTurns: 0
		);
	}

	private AnimationSettings CreatePlaySettings()
	{
		return new AnimationSettings(
			PlayFlightDuration,
			PlayFlightTransition,
			PlayFlightEase,
			PlayFlipDuration,
			PlayFlipTransition,
			PlayFlipEase,
			PlayClockwiseTurns
		);
	}

	private AnimationSettings CreatePassSettings()
	{
		return new AnimationSettings(
			PassFlightDuration,
			PassFlightTransition,
			PassFlightEase,
			PassFlipDuration,
			PassFlipTransition,
			PassFlipEase,
			ClockwiseTurns: 0
		);
	}

	private AnimationSettings CreateCollectSettings()
	{
		return new AnimationSettings(
			CollectFlightDuration,
			CollectFlightTransition,
			CollectFlightEase,
			CollectFlipDuration,
			CollectFlipTransition,
			CollectFlipEase,
			CollectClockwiseTurns
		);
	}

	/// <summary>Cancels all active flights.</summary>
	public void CancelAnimation(bool freeCard = true)
	{
		FlightState[] states = _flights.ToArray();
		foreach (FlightState state in states)
			CancelState(state, freeCard);

		_flights.Clear();
	}

	/// <summary>Cancels only the specified card's flight.</summary>
	public void CancelAnimation(CardControl card, bool freeCard = true)
	{
		FlightState state = FindFlight(card);
		if (state is null)
			return;

		CancelState(state, freeCard);
		_flights.Remove(state);
	}

	private void StartFlip(FlightState state, bool toFront)
	{
		CardControl card = state.Card;
		if (!GodotObject.IsInstanceValid(card))
		{
			state.FlipFinished = true;
			return;
		}

		state.FlipHandler = () => HandleFlipCompleted(state);
		card.FlipCompleted += state.FlipHandler;
		card.PlayFlip(
			toFront,
			Mathf.Max(0.0f, state.Settings.FlipDuration),
			reverse: false,
			transitionType: state.Settings.FlipTransition,
			easeType: state.Settings.FlipEase
		);

		// CardVisual does not emit a signal when it is already at the target face.
		if (!card.IsFlipping)
		{
			state.FlipFinished = true;
			DetachFlipListener(state);
		}
	}

	private void HandleFlipCompleted(FlightState state)
	{
		if (!_flights.Contains(state))
			return;

		state.FlipFinished = true;
		DetachFlipListener(state);
		TryComplete(state);
	}

	private void HandleFlightCompleted(
		FlightState state,
		Transform2D targetTransform,
		Vector2 targetSize,
		Tween tween)
	{
		if (!_flights.Contains(state) ||
			!ReferenceEquals(state.FlightTween, tween))
		{
			return;
		}

		if (GodotObject.IsInstanceValid(state.Card))
			ApplyPose(state.Carrier, state.Card, targetTransform, targetSize);
		// A complete number of turns is visually identical to zero rotation.
		// Normalize before the card is reparented into the play area so both sides
		// of the hand-off use exactly the same transform.
		ResetSpin(state.SpinCarrier);

		state.FlightFinished = true;
		state.FlightTween = null;
		TryComplete(state);
	}

	private void TryComplete(FlightState state)
	{
		if (!_flights.Contains(state) ||
			!state.FlightFinished ||
			!state.FlipFinished)
		{
			return;
		}

		CardControl card = state.Card;
		if (!GodotObject.IsInstanceValid(card))
		{
			CancelState(state, freeCard: false);
			_flights.Remove(state);
			return;
		}

		DetachFlipListener(state);
		_flights.Remove(state);
		Action<CardControl> callback = state.Completion;
		state.Completion = null;
		state.FlightTween = null;

		EmitSignal(SignalName.CardAnimationCompleted);
		try
		{
			callback?.Invoke(card);
		}
		finally
		{
			// A successful callback normally reparents the card into the hand. If
			// it did not, freeing the carrier also cleans up the transient card.
			// Free immediately so ReceiveCard's reparent cannot race a queued free.
			if (GodotObject.IsInstanceValid(state.SpinCarrier) &&
				state.SpinCarrier.GetChildCount() == 0)
			{
				state.SpinCarrier.Free();
			}

			if (GodotObject.IsInstanceValid(state.Carrier) &&
				state.Carrier.GetChildCount() == 0)
			{
				state.Carrier.Free();
			}
			else if (GodotObject.IsInstanceValid(state.Carrier))
			{
				state.Carrier.QueueFree();
			}
		}
	}

	private void CancelState(FlightState state, bool freeCard)
	{
		if (state.FlightTween is { } tween && tween.IsValid())
			tween.Kill();

		DetachFlipListener(state);
		state.FlightTween = null;
		state.Completion = null;

		if (freeCard &&
			state.Card is not null &&
			GodotObject.IsInstanceValid(state.Card))
		{
			state.Card.QueueFree();
		}
		else if (!freeCard &&
			state.Card is not null &&
			GodotObject.IsInstanceValid(state.Card) &&
			GodotObject.IsInstanceValid(state.SpinCarrier) &&
			state.Card.GetParent() == state.SpinCarrier)
		{
			// Detach before freeing the private carrier so the caller can reclaim
			// the cancelled card as promised by the public API.
			state.Card.Reparent(this, keepGlobalTransform: true);
		}

		if (GodotObject.IsInstanceValid(state.Carrier))
			state.Carrier.QueueFree();
	}

	private void DetachFlipListener(FlightState state)
	{
		CardControl card = state.Card;
		if (state.FlipHandler is null ||
			card is null ||
			!GodotObject.IsInstanceValid(card))
		{
			state.FlipHandler = null;
			return;
		}

		card.FlipCompleted -= state.FlipHandler;
		state.FlipHandler = null;
	}

	private FlightState FindFlight(CardControl card)
	{
		if (card is null)
			return null!;

		foreach (FlightState state in _flights)
		{
			if (ReferenceEquals(state.Card, card))
				return state;
		}

		return null!;
	}

	private Transform2D CanvasToLocal(Transform2D canvasTransform)
	{
		Transform2D layerCanvasTransform = GetGlobalTransformWithCanvas();
		return layerCanvasTransform.AffineInverse() * canvasTransform;
	}

	private static bool IsValidSize(Vector2 size)
	{
		return float.IsFinite(size.X) &&
			float.IsFinite(size.Y) &&
			size.X > 0.0f &&
			size.Y > 0.0f;
	}

	private static void ApplyPose(
		Node2D carrier,
		CardControl card,
		Transform2D localTransform,
		Vector2 size)
	{
		if (!GodotObject.IsInstanceValid(carrier) ||
			!GodotObject.IsInstanceValid(card) ||
			!IsValidSize(size))
		{
			return;
		}

		// The incoming transform describes the card center. Keep the Card's local
		// origin at its top-left by offsetting it from the carrier; recompute this
		// offset on every frame because the flight may also interpolate its size.
		card.ResizeToSize(size);
		Vector2 halfSize = size * 0.5f;
		card.Position = -halfSize;
		carrier.Transform = localTransform;
	}

	private static void ApplySpin(
		Node2D spinCarrier,
		int clockwiseTurns,
		float progress)
	{
		if (!GodotObject.IsInstanceValid(spinCarrier))
			return;

		spinCarrier.Rotation = Mathf.Tau * clockwiseTurns * progress;
	}

	private static void ResetSpin(Node2D spinCarrier)
	{
		if (GodotObject.IsInstanceValid(spinCarrier))
			spinCarrier.Rotation = 0.0f;
	}

	private static Transform2D ToCenterTransform(
		Transform2D topLeftTransform,
		Vector2 size)
	{
		return topLeftTransform * new Transform2D(0.0f, size * 0.5f);
	}
}
