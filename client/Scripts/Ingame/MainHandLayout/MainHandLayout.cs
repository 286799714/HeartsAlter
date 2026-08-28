using Godot;
using System;
using System.Collections.Generic;
using HeartsAlter.Scripts.InGame.Card;

/// <summary>
/// Presents the local player's cards as an overlapping, centered hand.
///
/// Layout calculation is deliberately separated from animation. A running
/// layout tween is never interrupted: the newest calculation is retained as a
/// pending target and starts only after the current tween reaches its target.
/// Card selection uses a second, per-card tween and therefore can be reversed
/// from its current position at any time.
/// </summary>
public partial class MainHandLayout : Control
{
	private const float DefaultCardWidth = 120.0f;
	private const float DefaultCardHeight = 167.0f;
	private const float DefaultLayoutWidth = 1000.0f;

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

	/// <summary>
	/// Height by which the selected card is raised.
	/// </summary>
	[Export]
	public float SelectedLift = 32.0f;

	/// <summary>
	/// Duration of the selection raise/retract animation, in seconds.
	/// </summary>
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

	private readonly List<Card> _cards = new();
	private readonly Dictionary<Card, Card.ClickedEventHandler> _clickHandlers = new();
	private readonly Dictionary<Card, Tween> _selectionTweens = new();

	// The active layout tween owns these two snapshots. They must not be
	// replaced while the tween is running, since they define its end state and
	// the collection position promised to callers.
	private readonly Dictionary<Card, Vector2> _activeLayoutStarts = new();
	private readonly Dictionary<Card, Vector2> _activeLayoutTargets = new();
	private Dictionary<Card, Vector2> _queuedLayoutTargets;
	private Tween _layoutTween;

	private Card _selectedCard;

	/// <summary>
	/// Cards currently managed by this hand, in draw/order (left-to-right)
	/// order. The returned view is read-only to callers.
	/// </summary>
	public IReadOnlyList<Card> Cards => _cards;

	/// <summary>
	/// Card currently selected by the player, or null when nothing is selected.
	/// </summary>
	public Card SelectedCard => _selectedCard;

	/// <summary>
	/// True while the non-preemptive layout tween is running.
	/// </summary>
	public bool IsLayoutAnimating => _layoutTween is { } tween && tween.IsValid();

	/// <summary>
	/// The destination used by the next card being collected.
	/// </summary>
	public Vector2 CurrentCollectPosition => GetCurrentCollectPosition();

	/// <summary>
	/// Alias using the hand's receive terminology. The coordinate is local to
	/// this layout control, just like <see cref="CurrentCollectPosition"/>.
	/// </summary>
	public Vector2 GetCurrentReceivePosition() => GetCurrentCollectPosition();

	public Vector2 CurrentReceivePosition => GetCurrentCollectPosition();

	public override void _Ready()
	{
		// MainHandLayout.tscn intentionally contains no sample card, but
		// registering authored children makes the component safe to reuse in a
		// scene that does provide initial cards.
		foreach (Node child in GetChildren())
		{
			if (child is Card card)
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
			if (tween is { } && tween.IsValid())
				tween.Kill();
		}

		foreach (KeyValuePair<Card, Card.ClickedEventHandler> pair in _clickHandlers)
		{
			if (IsInstanceValid(pair.Key))
				pair.Key.Clicked -= pair.Value;
		}

		_selectionTweens.Clear();
		_clickHandlers.Clear();
	}

	/// <summary>
	/// Recomputes the discrete target positions for all managed cards.
	/// Recalculation while an animation is in progress is coalesced: only the
	/// latest target is kept, and it starts after the active animation finishes.
	/// </summary>
	public void RecalculateLayout()
	{
		PruneInvalidCards();

		Dictionary<Card, Vector2> targets = CalculateLayoutTargets();

		if (_layoutTween is { } activeTween && activeTween.IsValid())
		{
			// Do not mutate _activeLayoutTargets. GetCurrentCollectPosition()
			// intentionally reports that snapshot until this tween completes.
			_queuedLayoutTargets = targets;
			return;
		}

		_layoutTween = null;
		StartLayoutTween(targets);
	}

	/// <summary>
	/// Returns the local position of the rightmost card in the current layout.
	/// While a layout tween is active, its target snapshot is used so repeated
	/// card collection does not make the fly-in destination jump every time.
	/// With an empty hand, the centered position of a card-sized slot is used.
	/// </summary>
	public Vector2 GetCurrentCollectPosition()
	{
		PruneInvalidCards();

		if (_layoutTween is { } activeTween && activeTween.IsValid())
		{
			if (TryGetRightmost(_activeLayoutTargets, out Vector2 activePosition))
				return activePosition;
		}

		if (_queuedLayoutTargets is not null &&
			TryGetRightmost(_queuedLayoutTargets, out Vector2 queuedPosition))
		{
			return queuedPosition;
		}

		if (_cards.Count > 0)
		{
			Card rightmostCard = _cards[0];
			for (int i = 1; i < _cards.Count; i++)
			{
				if (_cards[i].LayoutPosition.X > rightmostCard.LayoutPosition.X)
					rightmostCard = _cards[i];
			}

			return rightmostCard.LayoutPosition;
		}

		return CalculateEmptyCollectPosition(DefaultCardWidth, DefaultCardHeight);
	}

	/// <summary>
	/// Reparents <paramref name="card"/> into this layout, places it at the
	/// current collection destination, and queues a new hand layout.
	/// </summary>
	public void ReceiveCard(Card card)
	{
		ArgumentNullException.ThrowIfNull(card);
		if (!IsInstanceValid(card))
			return;

		PruneInvalidCards();

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
			UpdateZIndices();
			RecalculateLayout();
			return;
		}

		bool hasCurrentLayout = _cards.Count > 0 ||
			(_layoutTween is { } activeTween &&
				activeTween.IsValid() &&
				_activeLayoutTargets.Count > 0) ||
			(_queuedLayoutTargets is not null && _queuedLayoutTargets.Count > 0);

		Vector2 collectPosition = hasCurrentLayout
			? GetCurrentCollectPosition()
			: CalculateEmptyCollectPosition(GetCardWidth(card), GetCardHeight(card));

		// Register before reparenting so ChildEnteredTree (when this method is
		// called with a card from another parent) cannot start a tween from the
		// card's old position. The final collection position is assigned below.
		if (!_cards.Contains(card))
			RegisterCard(card, recalculate: false);

		if (card.GetParent() != this)
		{
			if (card.GetParent() is not null)
				card.Reparent(this, keepGlobalTransform: true);
			else
				AddChild(card);
		}

		// An incoming card always starts unselected at the collection point.
		NormalizeCardAnchors(card);
		CancelSelectionTween(card);
		card.SetSelectionLift(0.0f);
		card.SetLayoutPosition(collectPosition);

		UpdateZIndices();
		RecalculateLayout();
	}

	/// <summary>
	/// Alias kept for callers that naturally describe the operation as adding
	/// a card rather than receiving it.
	/// </summary>
	public void AddCard(Card card) => ReceiveCard(card);

	/// <summary>
	/// Alias for code that uses the game's "collect" terminology.
	/// </summary>
	public void CollectCard(Card card) => ReceiveCard(card);

	/// <summary>
	/// Selects a managed card. Passing null clears the selection. Clicking the
	/// already selected card is idempotent; callers that need to clear the
	/// choice can use <see cref="ClearSelection"/>.
	/// </summary>
	public void SelectCard(Card card)
	{
		PruneInvalidCards();

		if (card is not null && !_cards.Contains(card))
			return;

		SetSelectedCard(card);
	}

	public void ClearSelection() => SetSelectedCard(null);

	private void RegisterCard(Card card, bool recalculate)
	{
		if (!IsInstanceValid(card) || _cards.Contains(card))
			return;

		_cards.Add(card);

		// Capture the position before changing the card's standalone anchors. The
		// helper preserves the visible position, but this also makes authored
		// cards (and cards registered before _Ready) start from their true slot.
		card.CaptureCurrentPositionAsLayout();
		NormalizeCardAnchors(card);

		Card.ClickedEventHandler handler = () => HandleCardClicked(card);
		card.Clicked += handler;
		_clickHandlers[card] = handler;

		card.SetSelectionLift(ReferenceEquals(card, _selectedCard) ? SelectedLift : 0.0f);
		UpdateZIndices();

		if (recalculate)
			RecalculateLayout();
	}

	private void HandleCardClicked(Card card)
	{
		if (IsInstanceValid(card))
			SelectCard(card);
	}

	private void HandleChildEnteredTree(Node node)
	{
		if (node is Card card)
			RegisterCard(card, recalculate: true);
	}

	private void HandleChildExitingTree(Node node)
	{
		if (node is not Card card || !_cards.Remove(card))
			return;

		CancelSelectionTween(card);
		if (_clickHandlers.Remove(card, out Card.ClickedEventHandler handler) &&
			IsInstanceValid(card))
		{
			card.Clicked -= handler;
		}
		if (ReferenceEquals(_selectedCard, card))
			_selectedCard = null;

		UpdateZIndices();
		RecalculateLayout();
	}

	private void SetSelectedCard(Card next)
	{
		if (ReferenceEquals(_selectedCard, next))
			return;

		Card previous = _selectedCard;
		_selectedCard = next;

		if (previous is not null && IsInstanceValid(previous))
			AnimateSelection(previous, selected: false);

		if (next is not null && IsInstanceValid(next))
			AnimateSelection(next, selected: true);

		UpdateZIndices();
	}

	private void AnimateSelection(Card card, bool selected)
	{
		CancelSelectionTween(card);

		float start = card.SelectionLift;
		float target = selected ? Mathf.Max(0.0f, SelectedLift) : 0.0f;

		if (Mathf.IsEqualApprox(start, target) ||
			SelectionTweenDuration <= 0.0f ||
			!IsInsideTree() ||
			!card.IsInsideTree())
		{
			card.SetSelectionLift(target);
			return;
		}

		Tween tween = card.CreateTween();
		_selectionTweens[card] = tween;

		tween.TweenMethod(
			Callable.From<float>(value =>
			{
				if (IsInstanceValid(card))
					card.SetSelectionLift(value);
			}),
			start,
			target,
			SelectionTweenDuration
		)
		.SetTrans(SelectionTransition)
		.SetEase(SelectionEase);

		tween.TweenCallback(Callable.From(() =>
		{
			if (!_selectionTweens.TryGetValue(card, out Tween active) ||
				!ReferenceEquals(active, tween))
				return;

			_selectionTweens.Remove(card);

			if (IsInstanceValid(card))
				card.SetSelectionLift(target);
		}));
	}

	private void CancelSelectionTween(Card card)
	{
		if (!_selectionTweens.TryGetValue(card, out Tween tween))
			return;

		if (tween is { } && tween.IsValid())
			tween.Kill();

		_selectionTweens.Remove(card);
	}

	private Dictionary<Card, Vector2> CalculateLayoutTargets()
	{
		Dictionary<Card, Vector2> targets = new();
		if (_cards.Count == 0)
			return targets;

		float cardWidth = 0.0f;
		float cardHeight = 0.0f;
		foreach (Card card in _cards)
		{
			cardWidth = Mathf.Max(cardWidth, GetCardWidth(card));
			cardHeight = Mathf.Max(cardHeight, GetCardHeight(card));
		}

		float layoutWidth = ResolveLayoutWidth();
		float maxSpacing = Mathf.Max(0.0f, MaxSpacing);
		// Never let an optional minimum violate the advertised maximum.
		float minSpacing = Mathf.Min(maxSpacing, Mathf.Max(0.0f, MinSpacing));

		float spacing = 0.0f;
		if (_cards.Count > 1)
		{
			// Overlap means card width is counted once in the envelope, not once
			// per card. This is the key fit rule for a fan-like hand.
			float fittingSpacing = (layoutWidth - cardWidth) / (_cards.Count - 1);
			fittingSpacing = Mathf.Max(0.0f, fittingSpacing);
			spacing = Mathf.Min(maxSpacing, fittingSpacing);

			// Honour a configured minimum whenever the available width permits it.
			if (fittingSpacing >= minSpacing)
				spacing = Mathf.Max(spacing, minSpacing);
		}

		float occupiedWidth = cardWidth + spacing * Mathf.Max(0, _cards.Count - 1);
		float originX = ResolveLayoutOriginX(layoutWidth);
		float firstX = originX + (layoutWidth - occupiedWidth) * 0.5f;
		float baselineY = ResolveBaselineY(cardHeight);

		for (int i = 0; i < _cards.Count; i++)
			targets[_cards[i]] = new Vector2(firstX + spacing * i, baselineY);

		return targets;
	}

	private void StartLayoutTween(Dictionary<Card, Vector2> targets)
	{
		_activeLayoutStarts.Clear();
		_activeLayoutTargets.Clear();

		foreach (KeyValuePair<Card, Vector2> pair in targets)
		{
			if (!IsInstanceValid(pair.Key))
				continue;

			_activeLayoutStarts[pair.Key] = pair.Key.LayoutPosition;
			_activeLayoutTargets[pair.Key] = pair.Value;
		}

		if (_activeLayoutTargets.Count == 0)
		{
			_layoutTween = null;
			StartQueuedLayoutIfAny();
			return;
		}

		bool hasMotion = false;
		foreach (KeyValuePair<Card, Vector2> pair in _activeLayoutTargets)
		{
			if (pair.Key.LayoutPosition.DistanceTo(pair.Value) > 0.01f)
			{
				hasMotion = true;
				break;
			}
		}

		if (!hasMotion || LayoutTweenDuration <= 0.0f || !IsInsideTree())
		{
			foreach (KeyValuePair<Card, Vector2> pair in _activeLayoutTargets)
				pair.Key.SetLayoutPosition(pair.Value);

			_activeLayoutStarts.Clear();
			_activeLayoutTargets.Clear();
			_layoutTween = null;
			StartQueuedLayoutIfAny();
			return;
		}

		Tween tween = CreateTween();
		_layoutTween = tween;

		tween.TweenMethod(
			Callable.From<float>(progress =>
			{
				foreach (KeyValuePair<Card, Vector2> pair in _activeLayoutTargets)
				{
					Card card = pair.Key;
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

		tween.TweenCallback(Callable.From(() => CompleteLayoutTween(tween)));
	}

	private void CompleteLayoutTween(Tween completedTween)
	{
		if (!ReferenceEquals(_layoutTween, completedTween))
			return;

		foreach (KeyValuePair<Card, Vector2> pair in _activeLayoutTargets)
		{
			if (IsInstanceValid(pair.Key) && pair.Key.GetParent() == this)
				pair.Key.SetLayoutPosition(pair.Value);
		}

		_layoutTween = null;
		_activeLayoutStarts.Clear();
		_activeLayoutTargets.Clear();
		StartQueuedLayoutIfAny();
	}

	private void StartQueuedLayoutIfAny()
	{
		if (_queuedLayoutTargets is null)
			return;

		Dictionary<Card, Vector2> next = _queuedLayoutTargets;
		_queuedLayoutTargets = null;
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
			Card card = _cards[i];
			if (IsInstanceValid(card))
				continue;

			_cards.RemoveAt(i);
			_clickHandlers.Remove(card);
			_selectionTweens.Remove(card);
		}

		if (_selectedCard is not null && !IsInstanceValid(_selectedCard))
			_selectedCard = null;

		UpdateZIndices();
	}

	private void UpdateZIndices()
	{
		for (int i = 0; i < _cards.Count; i++)
		{
			Card card = _cards[i];
			if (!IsInstanceValid(card) || card.GetParent() != this)
				continue;

			// Later cards are drawn above earlier cards, so increasing z-index
			// preserves the intended right-over-left overlap even after a card is
			// reparented. Selection is conveyed by the vertical lift; retaining
			// this order keeps the right card's overlap and hit area intact.
			card.ZIndex = i;
		}
	}

	private bool TryGetRightmost(
		Dictionary<Card, Vector2> positions,
		out Vector2 rightmost)
	{
		rightmost = Vector2.Zero;

		// Use hand order rather than dictionary enumeration so a fully
		// overlapped hand (spacing == 0) still chooses the last/rightmost card
		// deterministically.
		for (int i = _cards.Count - 1; i >= 0; i--)
		{
			Card card = _cards[i];
			if (IsInstanceValid(card) &&
				card.GetParent() == this &&
				positions.TryGetValue(card, out rightmost))
				return true;
		}

		return false;
	}

	private static void NormalizeCardAnchors(Card card)
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

	private static float GetCardWidth(Card card)
	{
		float width = card.Size.X;
		if (width <= 0.0f)
			width = card.CustomMinimumSize.X;
		return width > 0.0f ? width : DefaultCardWidth;
	}

	private static float GetCardHeight(Card card)
	{
		float height = card.Size.Y;
		if (height <= 0.0f)
			height = card.CustomMinimumSize.Y;
		return height > 0.0f ? height : DefaultCardHeight;
	}
}
