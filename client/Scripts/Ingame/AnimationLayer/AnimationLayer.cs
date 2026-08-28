using System;
using System.Collections.Generic;
using Godot;
using CardPose2D = HeartsAlter.Scripts.InGame.Card.CardPose2D;
using PlayingCard = HeartsAlter.Scripts.InGame.Card.Card;

namespace HeartsAlter.Scripts.InGame.AnimationLayer;

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
	[Export]
	public float CardFlightDuration { get; set; } = 0.55f;

	[Export]
	public float CardFlipDuration { get; set; } = 0.28f;

	[Export]
	public Tween.TransitionType FlightTransition { get; set; } =
		Tween.TransitionType.Sine;

	[Export]
	public Tween.EaseType FlightEase { get; set; } = Tween.EaseType.InOut;

	[Signal]
	public delegate void CardAnimationCompletedEventHandler();

	private sealed class FlightState
	{
		public PlayingCard Card = null!;
		public Node2D Carrier = null!;
		public Tween FlightTween = null!;
		public PlayingCard.FlipCompletedEventHandler FlipHandler = null!;
		public Action<PlayingCard> Completion = null!;
		public bool FlightFinished;
		public bool FlipFinished;
	}

	private readonly List<FlightState> _flights = new();

	/// <summary>
	/// First active card, retained as a convenience for single-card callers.
	/// Use <see cref="ActiveCards"/> when several cards are flying.
	/// </summary>
	public PlayingCard ActiveCard =>
		_flights.Count > 0 ? _flights[0].Card : null!;

	/// <summary>Snapshot of all cards currently owned by this layer.</summary>
	public IReadOnlyList<PlayingCard> ActiveCards
	{
		get
		{
			List<PlayingCard> cards = new();
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

	/// <summary>
	/// Animates <paramref name="card"/> from <paramref name="sourcePose"/> to
	/// <paramref name="targetPose"/>. The card must already be a child of this
	/// layer; the method then moves it under a private carrier. The completion
	/// callback runs after both the flight and an optional face flip finish, so
	/// callers can safely call <see cref="MainHandLayout.ReceiveCard"/> there.
	/// </summary>
	/// <returns>False when the layer/card is not ready or the card is busy.</returns>
	public bool PlayCardToPose(
		PlayingCard card,
		CardPose2D sourcePose,
		CardPose2D targetPose,
		Action<PlayingCard> completed = null)
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
		// matrix even when the source/target controls are rotated or scaled. The
		// carrier's origin is aligned to the card center, so rotation never pivots
		// around the card's top-left corner.
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
		card.Reparent(carrier, keepGlobalTransform: false);

		FlightState state = new()
		{
			Card = card,
			Carrier = carrier,
			Completion = completed,
			FlightFinished = false,
			FlipFinished = card.IsFaceUp == targetPose.IsFaceUp
		};
		_flights.Add(state);

		card.Visible = true;
		ApplyPose(carrier, card, sourceTransform, sourcePose.Size);

		if (!state.FlipFinished)
			StartFlip(state, targetPose.IsFaceUp);

		float duration = Mathf.Max(0.0f, CardFlightDuration);
		if (duration <= 0.0f)
		{
			ApplyPose(carrier, card, targetTransform, targetPose.Size);
			state.FlightFinished = true;
		}
		else
		{
			Tween tween = CreateTween();
			state.FlightTween = tween;

			tween.TweenMethod(
				Callable.From<float>(progress =>
				{
					if (!GodotObject.IsInstanceValid(card) ||
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
				}),
				0.0f,
				1.0f,
				duration
			)
			.SetTrans(FlightTransition)
			.SetEase(FlightEase);

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

	/// <summary>Cancels all active flights.</summary>
	public void CancelAnimation(bool freeCard = true)
	{
		FlightState[] states = _flights.ToArray();
		foreach (FlightState state in states)
			CancelState(state, freeCard);

		_flights.Clear();
	}

	/// <summary>Cancels only the specified card's flight.</summary>
	public void CancelAnimation(PlayingCard card, bool freeCard = true)
	{
		FlightState state = FindFlight(card);
		if (state is null)
			return;

		CancelState(state, freeCard);
		_flights.Remove(state);
	}

	private void StartFlip(FlightState state, bool toFront)
	{
		PlayingCard card = state.Card;
		if (!GodotObject.IsInstanceValid(card))
		{
			state.FlipFinished = true;
			return;
		}

		state.FlipHandler = () => HandleFlipCompleted(state);
		card.FlipCompleted += state.FlipHandler;
		card.PlayFlip(
			toFront,
			Mathf.Max(0.0f, CardFlipDuration),
			reverse: false,
			transitionType: FlightTransition,
			easeType: FlightEase
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

		PlayingCard card = state.Card;
		if (!GodotObject.IsInstanceValid(card))
		{
			CancelState(state, freeCard: false);
			_flights.Remove(state);
			return;
		}

		DetachFlipListener(state);
		_flights.Remove(state);
		Action<PlayingCard> callback = state.Completion;
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
			GodotObject.IsInstanceValid(state.Carrier) &&
			state.Card.GetParent() == state.Carrier)
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
		PlayingCard card = state.Card;
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

	private FlightState FindFlight(PlayingCard card)
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
		PlayingCard card,
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

	private static Transform2D ToCenterTransform(
		Transform2D topLeftTransform,
		Vector2 size)
	{
		return topLeftTransform * new Transform2D(0.0f, size * 0.5f);
	}
}
