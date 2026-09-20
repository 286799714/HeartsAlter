using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.InGame;

/// <summary>
/// Presents opponent cards as an overlapping, centered hand with no interaction state.
///
/// Layout calculation is deliberately separated from animation. Position
/// plans run serially: a running layout tween is never interrupted, and only
/// the newest pending plan is retained.
/// </summary>
public partial class OtherHandLayout : Control
{
	private const float DefaultLayoutWidth = 1000.0f;
	private sealed record LayoutPlan(
		IReadOnlyList<CardControl> Order,
		IReadOnlyDictionary<CardControl, Vector2> Targets
	);

	/// <summary>
	/// Maximum horizontal distance between adjacent card origins. Because
	/// cards overlap, the occupied width is card width + spacing * (count - 1).
	/// </summary>
	[Export]
	public float MaxSpacing = 42.0f;

	/// <summary>
	/// Lower bound used while fitting a hand. Set to zero to allow complete
	/// overlap when the hand is wider than the available area.
	/// </summary>
	[Export]
	public float MinSpacing = 0.0f;

	/// <summary>
	/// Optional width used for layout. A value of zero uses this Control's
	/// width, which lets a parent resize the hand naturally.
	/// </summary>
	[Export]
	public float LayoutWidth = 0.0f;

	/// <summary>
	/// Duration of a hand re-layout animation, in seconds.
	/// </summary>
	[Export]
	public float LayoutTweenDuration = 0.24f;

	[Export]
	public float SelectedLift = 32.0f;

	[Export]
	public float SelectionTweenDuration = 0.16f;

	[Export]
	public Tween.TransitionType LayoutTransition = Tween.TransitionType.Sine;

	[Export]
	public Tween.EaseType LayoutEase = Tween.EaseType.InOut;

	[Export]
	public Tween.TransitionType SelectionTransition = Tween.TransitionType.Sine;

	[Export]
	public Tween.EaseType SelectionEase = Tween.EaseType.InOut;

	private readonly List<CardControl> _cards = new();

	// The active layout tween owns these snapshots. They must not be replaced
	// while it is running, since they define its end state and the collection
	// position promised to callers.
	private readonly Dictionary<CardControl, Vector2> _activeLayoutStarts = new();
	private LayoutPlan _activeLayoutPlan;
	private LayoutPlan _pendingLayoutPlan;
	private Tween _layoutTween;
	private readonly Dictionary<CardControl, Tween> _selectionTweens = new();
	private AnimationLayer _animationLayer;
	private PlayArea _playArea;
	/// <summary>
	/// Cards currently managed by this hand in logical left-to-right order. The
	/// returned view is read-only and follows receive order.
	/// </summary>
	public IReadOnlyList<CardControl> Cards => _cards;

	/// <summary>
	/// True while the non-preemptive layout tween is running.
	/// </summary>
	public bool IsLayoutAnimating => _layoutTween is { } tween && tween.IsValid();

	/// <summary>
	/// Returns the target pose for <paramref name="card"/> when it is received
	/// by this hand. The transform is expressed in canvas coordinates so a
	/// shared animation layer can consume it even when this layout is rotated,
	/// scaled, or belongs to a different CanvasLayer. Other-hand poses always
	/// request the card-back presentation.
	/// </summary>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="card"/> is null.
	/// </exception>
	/// <exception cref="ArgumentException">
	/// <paramref name="card"/> has already been freed.
	/// </exception>
	public CardPose2D GetCurrentReceivePose(CardControl card)
	{
		ArgumentNullException.ThrowIfNull(card);
		if (!IsInstanceValid(card))
			throw new ArgumentException("Card must be a valid Godot instance.", nameof(card));

		Vector2 targetSize = CalculateReceiveSize(card);
		Vector2 localPosition = GetCurrentReceiveLocalPosition(
			targetSize.X,
			targetSize.Y
		);
		if (GetViewport().GuiSnapControlsToPixels)
			localPosition = (localPosition + Vector2.One * 0.5f).Floor();
		Transform2D localTransform = new(0.0f, localPosition);

		return new CardPose2D(
			CardPose2D.GetRenderedCanvasTransform(this) * localTransform,
			targetSize,
			IsFaceUp: false
		);
	}
	public override void _Ready()
	{
		// OtherHandLayout.tscn intentionally contains no sample card, but
		// registering authored children makes the component safe to reuse in a
		// scene that does provide initial cards.
		foreach (Node child in GetChildren())
		{
			if (child is CardControl card)
				RegisterCard(card, recalculate: false);
		}

		Resized += HandleResized;
		ChildEnteredTree += HandleChildEnteredTree;
		ChildExitingTree += HandleChildExitingTree;
		RecalculateLayout();
	}
	public override void _ExitTree()
	{
		Resized -= HandleResized;
		ChildEnteredTree -= HandleChildEnteredTree;
		ChildExitingTree -= HandleChildExitingTree;

		if (_layoutTween is { } layoutTween && layoutTween.IsValid())
			layoutTween.Kill();
		foreach (Tween tween in _selectionTweens.Values)
		{
			if (tween is { } && tween.IsValid()) tween.Kill();
		}
		_selectionTweens.Clear();

		_activeLayoutStarts.Clear();
		_activeLayoutPlan = null;
		_pendingLayoutPlan = null;
	}
	/// <summary>
	/// Recomputes the discrete target positions for all managed cards.
	/// Recalculation while an animation is in progress is coalesced: only the
	/// latest target is kept, and it starts after the active animation finishes.
	/// </summary>
	public void RecalculateLayout()
	{
		PruneInvalidCards();
		SubmitLayoutPlan(CreateLayoutPlan(_cards));
	}

	/// <summary>
	/// Supplies the shared animation layer and this seat's central play area.
	/// Table owns this wiring so ordinary callers only need an index and data.
	/// </summary>
	internal void BindPlayAnimation(AnimationLayer animationLayer, PlayArea playArea)
	{
		_animationLayer = animationLayer;
		_playArea = playArea;
	}

	/// <summary>
	/// Reveals and plays the indexed hidden card. The card data is assigned while
	/// it is still showing its back; AnimationLayer coordinates the flight and
	/// the flip to the face-up destination pose.
	/// </summary>
	public bool TryPlayCard(int cardIndex, CardData cardData)
	{
		return TryPlayCard(cardIndex, cardData, completed: null);
	}

	/// <summary>
	/// Internal table seam that also reports when the card has landed in its play
	/// area. Ordinary callers use the two-argument overload.
	/// </summary>
	internal bool TryPlayCard(
		int cardIndex,
		CardData cardData,
		Action<CardControl> completed)
	{
		PruneInvalidCards();
		if (cardIndex < 0 || cardIndex >= _cards.Count ||
			_animationLayer is null || !IsInstanceValid(_animationLayer) ||
			!_animationLayer.IsInsideTree() ||
			_playArea is null || !IsInstanceValid(_playArea) ||
			!_playArea.IsInsideTree())
		{
			return false;
		}

		CardControl card = _cards[cardIndex];
		if (!IsInstanceValid(card) || card.GetParent() != this)
			return false;

		card.Setup(cardData, startFaceUp: false);
		CardPose2D sourcePose = new(
			CardPose2D.GetRenderedCanvasTransform(card),
			new Vector2(card.CardWidth, card.CardHeight),
			IsFaceUp: false
		);
		CardPose2D targetPose;
		try
		{
			targetPose = _playArea.GetReceivePose(card);
		}
		catch (Exception exception)
		{
			GD.PushWarning($"Unable to calculate an opponent play pose: {exception.Message}");
			return false;
		}

		card.Reparent(_animationLayer, keepGlobalTransform: true);
		bool started = _animationLayer.PlayCardToPose(
			card,
			sourcePose,
			targetPose,
			_playArea,
			playedCard =>
			{
				_playArea.ReceiveCard(playedCard);
				completed?.Invoke(playedCard);
			},
			targetPoseProvider: () => _playArea.GetReceivePose(card)
		);

		if (!started && IsInstanceValid(card))
			ReceiveCard(card);

		return started;
	}

	/// <summary>Immediately clears all logical and visual cards from this hand.</summary>
	public void ClearCards()
	{
		if (_layoutTween is { } layoutTween && layoutTween.IsValid())
			layoutTween.Kill();
		_layoutTween = null;
		foreach (Tween tween in _selectionTweens.Values)
		{
			if (tween is { } && tween.IsValid()) tween.Kill();
		}
		_selectionTweens.Clear();

		CardControl[] cards = _cards.ToArray();
		_cards.Clear();
		_activeLayoutStarts.Clear();
		_activeLayoutPlan = null;
		_pendingLayoutPlan = null;

		foreach (CardControl card in cards)
		{
			if (IsInstanceValid(card))
				card.QueueFree();
		}
	}

	/// <summary>Raises the specified hidden cards to show a remote selection.</summary>
	public void SelectCards(IEnumerable<int> cardIndices)
	{
		HashSet<int> selected = cardIndices is null
			? new()
			: new(cardIndices.Where(index => index >= 0));
		for (int index = 0; index < _cards.Count; index++)
			AnimateSelection(_cards[index], selected.Contains(index));
	}

	/// <summary>Retracts every card that was raised for a remote selection.</summary>
	public void RetractSelectedCards()
	{
		foreach (CardControl card in _cards)
			AnimateSelection(card, selected: false);
	}

	/// <summary>Detaches a card without freeing it so Table can animate a pass.</summary>
	public bool DetachCardForTransfer(CardControl card)
	{
		if (card is null || !_cards.Remove(card))
			return false;
		CancelSelectionTween(card);
		card.SetSelectionLift(0.0f);
		UpdateZIndices();
		RecalculateLayout();
		return true;
	}
	/// <summary>
	/// Returns the local destination used by the next received card. While a
	/// layout tween is active, its target snapshot keeps this position stable.
	/// </summary>
	private Vector2 GetCurrentReceiveLocalPosition(
		float emptyCardWidth,
		float emptyCardHeight)
	{
		PruneInvalidCards();

		if (_layoutTween is { } activeTween && activeTween.IsValid())
		{
			if (TryGetRightmost(_activeLayoutPlan, out Vector2 activePosition))
				return activePosition;
		}

		if (_pendingLayoutPlan is not null &&
			TryGetRightmost(_pendingLayoutPlan, out Vector2 pendingPosition))
		{
			return pendingPosition;
		}

		if (_cards.Count > 0)
		{
			CardControl rightmostCard = _cards[0];
			for (int i = 1; i < _cards.Count; i++)
			{
				if (_cards[i].LayoutPosition.X > rightmostCard.LayoutPosition.X)
					rightmostCard = _cards[i];
			}

			return rightmostCard.LayoutPosition;
		}

		return CalculateEmptyCollectPosition(emptyCardWidth, emptyCardHeight);
	}

	/// <summary>
	/// Reparents <paramref name="card"/> into this layout, preserves its current
	/// visual position as the layout animation's start, and queues a new hand
	/// layout.
	/// </summary>
	public void ReceiveCard(CardControl card)
	{
		ArgumentNullException.ThrowIfNull(card);
		if (!IsInstanceValid(card))
			return;

		PruneInvalidCards();
		ResizeCardToLayoutHeight(card);
		// A card can arrive from MainHandLayout after the submit lock, where all
		// local cards are intentionally dimmed. Remote hands have no legal-card
		// dimming state, so restore the normal presentation on receipt.
		card.Modulate = Colors.White;

		// Receiving the same instance twice should not teleport a card that may
		// currently be moving. It is already part of the hand, so only ensure its
		// parent/anchors are normalized and refresh the layout if necessary.
		if (_cards.Contains(card))
		{
			if (card.GetParent() != this)
			{
				if (card.GetParent() is not null)
					card.Reparent(this, keepGlobalTransform: true);
				else
					AddChild(card);
			}

			NormalizeCardAnchors(card);
			// Other-hand cards never carry a selection lift. Clear one that may
			// have been left behind if the card came from another hand.
			card.SetSelectionLift(0.0f);
			UpdateZIndices();
			RecalculateLayout();
			return;
		}

		bool hasIncomingCanvasPosition = card.IsInsideTree();
		Vector2 incomingCanvasPosition = hasIncomingCanvasPosition
			? card.GetGlobalTransformWithCanvas().Origin
			: Vector2.Zero;
		Vector2 fallbackPosition = hasIncomingCanvasPosition
			? Vector2.Zero
			: GetCurrentReceiveLocalPosition(card.CardWidth, card.CardHeight);

		// Register before reparenting so ChildEnteredTree (when this method is
		// called with a card from another parent) cannot start a tween from the
		// card's old position. The incoming canvas position is snapshotted before
		// reparenting and converted into this layout's coordinates below, so a
		// card arriving from an overlapping flight never jumps to a stale slot.
		if (!_cards.Contains(card))
			RegisterCard(card, recalculate: false);

		if (card.GetParent() != this)
		{
			if (card.GetParent() is not null)
				card.Reparent(this, keepGlobalTransform: true);
			else
				AddChild(card);
		}

		// Incoming cards are always unlifted at their current visual position.
		// RecalculateLayout then interpolates that position to the final slot,
		// avoiding a snap when concurrent flights share one receive pose.
		NormalizeCardAnchors(card);
		card.SetSelectionLift(0.0f);
		card.SetLayoutPosition(
			hasIncomingCanvasPosition
				? CanvasToLocalPosition(incomingCanvasPosition)
				: fallbackPosition
		);

		UpdateZIndices();
		RecalculateLayout();
	}
	/// <summary>
	/// Alias kept for callers that naturally describe the operation as adding
	/// a card rather than receiving it.
	/// </summary>
	public void AddCard(CardControl card) => ReceiveCard(card);

	/// <summary>
	/// Alias for code that uses the game's "collect" terminology.
	/// </summary>
	public void CollectCard(CardControl card) => ReceiveCard(card);

	private void RegisterCard(CardControl card, bool recalculate)
	{
		if (!IsInstanceValid(card) || _cards.Contains(card))
			return;

		_cards.Add(card);
		card.Modulate = Colors.White;

		// Capture the position before changing the card's standalone anchors. The
		// helper preserves the visible position, but this also makes authored
		// cards (and cards registered before _Ready) start from their true slot.
		card.CaptureCurrentPositionAsLayout();
		NormalizeCardAnchors(card);
		// This hand has no selection state. A card may arrive via a direct
		// reparent from MainHandLayout, so clear any lift it retained there even
		// when the caller did not use ReceiveCard.
		card.SetSelectionLift(0.0f);
		UpdateZIndices();

		if (recalculate)
			RecalculateLayout();
	}

	private void AnimateSelection(CardControl card, bool selected)
	{
		if (!IsInstanceValid(card)) return;
		CancelSelectionTween(card);
		float start = card.SelectionLift;
		float target = selected ? Mathf.Max(0.0f, SelectedLift) : 0.0f;
		if (Mathf.IsEqualApprox(start, target) || SelectionTweenDuration <= 0.0f ||
			!IsInsideTree() || !card.IsInsideTree())
		{
			card.SetSelectionLift(target);
			return;
		}
		Tween tween = card.CreateTween();
		_selectionTweens[card] = tween;
		tween.TweenMethod(
			Callable.From<float>(value =>
			{
				if (IsInstanceValid(card)) card.SetSelectionLift(value);
			}),
			start,
			target,
			SelectionTweenDuration
		).SetTrans(SelectionTransition).SetEase(SelectionEase);
		tween.TweenCallback(Callable.From(() =>
		{
			if (!_selectionTweens.TryGetValue(card, out Tween active) ||
				!ReferenceEquals(active, tween)) return;
			_selectionTweens.Remove(card);
			if (IsInstanceValid(card)) card.SetSelectionLift(target);
		}));
	}

	private void CancelSelectionTween(CardControl card)
	{
		if (!_selectionTweens.TryGetValue(card, out Tween tween)) return;
		if (tween is { } && tween.IsValid()) tween.Kill();
		_selectionTweens.Remove(card);
	}
	private void HandleChildEnteredTree(Node node)
	{
		if (node is CardControl card)
			RegisterCard(card, recalculate: true);
	}

	private void HandleChildExitingTree(Node node)
	{
		if (node is not CardControl card || !_cards.Remove(card))
			return;

		CancelSelectionTween(card);
		UpdateZIndices();
		RecalculateLayout();
	}

	private LayoutPlan CreateLayoutPlan(IReadOnlyList<CardControl> order)
	{
		CardControl[] orderSnapshot = order.ToArray();
		return new LayoutPlan(
			orderSnapshot,
			CalculateLayoutTargets(orderSnapshot)
		);
	}

	private Dictionary<CardControl, Vector2> CalculateLayoutTargets(
		IReadOnlyList<CardControl> order)
	{
		Dictionary<CardControl, Vector2> targets = new();
		if (order.Count == 0)
			return targets;

		float cardWidth = 0.0f;
		float cardHeight = 0.0f;
		foreach (CardControl card in order)
		{
			ResizeCardToLayoutHeight(card);
			cardWidth = Mathf.Max(cardWidth, card.CardWidth);
			cardHeight = Mathf.Max(cardHeight, card.CardHeight);
		}

		float layoutWidth = ResolveLayoutWidth();
		float maxSpacing = Mathf.Max(0.0f, MaxSpacing);
		// Never let an optional minimum violate the advertised maximum.
		float minSpacing = Mathf.Min(maxSpacing, Mathf.Max(0.0f, MinSpacing));

		float spacing = 0.0f;
		if (order.Count > 1)
		{
			// Overlap means card width is counted once in the envelope, not once
			// per card. This is the key fit rule for a fan-like hand.
			float fittingSpacing = (layoutWidth - cardWidth) / (order.Count - 1);
			fittingSpacing = Mathf.Max(0.0f, fittingSpacing);
			spacing = Mathf.Min(maxSpacing, fittingSpacing);

			// Honour a configured minimum whenever the available width permits it.
			if (fittingSpacing >= minSpacing)
				spacing = Mathf.Max(spacing, minSpacing);
		}

		float occupiedWidth = cardWidth + spacing * Mathf.Max(0, order.Count - 1);
		float originX = ResolveLayoutOriginX(layoutWidth);
		float firstX = originX + (layoutWidth - occupiedWidth) * 0.5f;
		float baselineY = ResolveBaselineY(cardHeight);

		for (int i = 0; i < order.Count; i++)
			targets[order[i]] = new Vector2(firstX + spacing * i, baselineY);

		return targets;
	}

	private void SubmitLayoutPlan(LayoutPlan plan)
	{
		if (_layoutTween is { } activeTween && activeTween.IsValid())
		{
			// Reflow requests are latest-wins. Keeping only one pending snapshot
			// avoids replaying stale intermediate layouts after rapid receives or
			// viewport resizes.
			_pendingLayoutPlan = plan;
			return;
		}

		_layoutTween = null;
		_activeLayoutPlan = null;
		StartLayoutTween(plan);
	}

	private void StartLayoutTween(LayoutPlan plan)
	{
		_activeLayoutStarts.Clear();
		_activeLayoutPlan = plan;

		foreach (KeyValuePair<CardControl, Vector2> pair in plan.Targets)
		{
			if (!IsInstanceValid(pair.Key) || pair.Key.GetParent() != this)
				continue;

			_activeLayoutStarts[pair.Key] = pair.Key.LayoutPosition;
		}

		if (_activeLayoutStarts.Count == 0)
		{
			_layoutTween = null;
			_activeLayoutPlan = null;
			StartPendingLayoutIfAny();
			return;
		}

		bool hasMotion = false;
		foreach (KeyValuePair<CardControl, Vector2> pair in _activeLayoutStarts)
		{
			if (plan.Targets.TryGetValue(pair.Key, out Vector2 target) &&
				pair.Value.DistanceTo(target) > 0.01f)
			{
				hasMotion = true;
				break;
			}
		}

		if (!hasMotion || LayoutTweenDuration <= 0.0f || !IsInsideTree())
		{
			ApplyLayoutTargets(plan);

			_activeLayoutStarts.Clear();
			_activeLayoutPlan = null;
			_layoutTween = null;
			StartPendingLayoutIfAny();
			return;
		}

		Tween tween = CreateTween();
		_layoutTween = tween;

		tween.TweenMethod(
				Callable.From<float>(progress =>
				{
					foreach (KeyValuePair<CardControl, Vector2> pair in plan.Targets)
					{
						CardControl card = pair.Key;
						if (!IsInstanceValid(card) ||
							card.GetParent() != this ||
							!_activeLayoutStarts.TryGetValue(card, out Vector2 start))
							continue;

						card.SetLayoutPosition(start.Lerp(pair.Value, progress));
					}
				}),
				0.0f,
				1.0f,
				LayoutTweenDuration
			)
			.SetTrans(LayoutTransition)
			.SetEase(LayoutEase);

		tween.TweenCallback(Callable.From(() => CompleteLayoutTween(tween, plan)));
	}

	private void CompleteLayoutTween(Tween completedTween, LayoutPlan completedPlan)
	{
		if (!ReferenceEquals(_layoutTween, completedTween) ||
			!ReferenceEquals(_activeLayoutPlan, completedPlan))
			return;

		ApplyLayoutTargets(completedPlan);

		_layoutTween = null;
		_activeLayoutStarts.Clear();
		_activeLayoutPlan = null;
		StartPendingLayoutIfAny();
	}

	private void ApplyLayoutTargets(LayoutPlan plan)
	{
		foreach (KeyValuePair<CardControl, Vector2> pair in plan.Targets)
		{
			if (IsInstanceValid(pair.Key) && pair.Key.GetParent() == this)
				pair.Key.SetLayoutPosition(pair.Value);
		}
	}

	private void StartPendingLayoutIfAny()
	{
		if (_pendingLayoutPlan is null)
			return;

		LayoutPlan next = _pendingLayoutPlan;
		_pendingLayoutPlan = null;
		StartLayoutTween(next);
	}
	private void HandleResized()
	{
		RecalculateLayout();
	}
	private void PruneInvalidCards()
	{
		for (int i = _cards.Count - 1; i >= 0; i--)
		{
			CardControl card = _cards[i];
			if (IsInstanceValid(card))
				continue;

			_cards.RemoveAt(i);
		}

		UpdateZIndices();
	}
	private void UpdateZIndices()
	{
		for (int i = 0; i < _cards.Count; i++)
		{
			CardControl card = _cards[i];
			if (!IsInstanceValid(card) || card.GetParent() != this)
				continue;

			// Later cards are drawn above earlier cards, so increasing z-index
			// preserves the intended right-over-left overlap after reparenting.
			card.ZIndex = i;
		}
	}
	private bool TryGetRightmost(
		LayoutPlan plan,
		out Vector2 rightmost)
	{
		rightmost = Vector2.Zero;
		if (plan is null)
			return false;

		// Use the plan's order rather than the latest logical hand order so a
		// running animation keeps a stable collection destination. A fully
		// overlapped hand (spacing == 0) also chooses its last card
		// deterministically.
		for (int i = plan.Order.Count - 1; i >= 0; i--)
		{
			CardControl card = plan.Order[i];
			if (IsInstanceValid(card) &&
				card.GetParent() == this &&
				plan.Targets.TryGetValue(card, out rightmost))
				return true;
		}

		return false;
	}

	private Vector2 CanvasToLocalPosition(Vector2 canvasPosition)
	{
		return GetGlobalTransformWithCanvas().AffineInverse() * canvasPosition;
	}

	private static void NormalizeCardAnchors(CardControl card)
	{
		// Card.tscn is also useful as a standalone bottom-centred card. Once it
		// belongs to a hand, top-left anchors make Position a stable local
		// coordinate; parent resizes then cannot move it behind our tween.
		if (card.GetParent() is not Control)
			return;

		Vector2 position = card.Position;
		card.SetAnchorsPreset(LayoutPreset.TopLeft, keepOffsets: true);
		card.Position = position;
	}
	private float ResolveLayoutWidth()
	{
		if (LayoutWidth > 0.0f)
			return LayoutWidth;

		if (Size.X > 0.0f)
			return Size.X;

		if (CustomMinimumSize.X > 0.0f)
			return CustomMinimumSize.X;

		// CanvasItem.GetViewportRect() logs an engine error while detached. A
		// layout can be queried before it is added to a scene tree (for example,
		// while an animation request is being prepared), so use the stable
		// fallback until a viewport is available.
		if (!IsInsideTree())
			return DefaultLayoutWidth;

		Vector2 viewportSize = GetViewportRect().Size;
		return viewportSize.X > 0.0f ? viewportSize.X : DefaultLayoutWidth;
	}

	private float ResolveLayoutOriginX(float layoutWidth)
	{
		if (Size.X > 0.0f)
			return (Size.X - layoutWidth) * 0.5f;

		// A zero-width Control is commonly anchored at the viewport center. In
		// that case use a symmetric local coordinate system around its origin.
		return -layoutWidth * 0.5f;
	}

	private float ResolveBaselineY(float cardHeight)
	{
		if (Size.Y > 0.0f)
			return Size.Y - cardHeight;

		// For a bottom-center zero-height container, negative card height places
		// the card immediately above the anchor.
		return -cardHeight;
	}

	private Vector2 CalculateEmptyCollectPosition(float cardWidth, float cardHeight)
	{
		float layoutWidth = ResolveLayoutWidth();
		float originX = ResolveLayoutOriginX(layoutWidth);
		float x = originX + (layoutWidth - cardWidth) * 0.5f;
		return new Vector2(x, ResolveBaselineY(cardHeight));
	}

	private float ResolveLayoutHeight()
	{
		if (Size.Y > 0.0f)
			return Size.Y;

		return CustomMinimumSize.Y;
	}

	private Vector2 CalculateReceiveSize(CardControl card)
	{
		float cardWidth = card.CardWidth;
		float cardHeight = card.CardHeight;
		float layoutHeight = ResolveLayoutHeight();

		if (layoutHeight <= 0.0f || cardHeight <= 0.0f)
			return new Vector2(cardWidth, cardHeight);

		return new Vector2(cardWidth * layoutHeight / cardHeight, layoutHeight);
	}

	private void ResizeCardToLayoutHeight(CardControl card)
	{
		float layoutHeight = ResolveLayoutHeight();
		if (layoutHeight > 0.0f)
			card.ResizeToHeight(layoutHeight);
	}
}
