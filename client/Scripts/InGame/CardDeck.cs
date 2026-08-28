using System;
using Godot;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.InGame;

public partial class CardDeck : Control
{
    [Export] private Godot.Collections.Array<Control> _layers = [];
    [Export] private CardControl _topCard = null!;

    public int MaxCardCount { get; private set; } = 1;
	
    public int CardCount { get; private set; }
	
    // Called when the node enters the scene tree for the first time.
    public override void _Ready()
    {
        // A deck scene can be previewed on its own.  Refreshing here keeps the
        // visual state consistent with CardCount (which starts at zero), while
        // callers such as Table can opt into a non-empty deck immediately
        // afterwards via ChangeCardCount.
        if (_layers.Count > 0 && IsInstanceValid(_topCard))
            RefreshLayers();
    }

    /// <summary>
    /// Creates a detached copy of the authored top card.  The copy is not
    /// added to the deck; callers normally add it to a shared animation layer,
    /// assign its <see cref="CardData"/>, and then hand it to a destination
    /// layout after the flight tween completes.
    /// </summary>
    public CardControl DuplicateTopCard()
    {
        if (!IsInstanceValid(_topCard))
            return null!;

        Node duplicateNode = _topCard.Duplicate();
        if (duplicateNode is not CardControl duplicate)
        {
            duplicateNode.QueueFree();
            return null!;
        }

        duplicate.Name = $"{_topCard.Name}_Copy";
        duplicate.Visible = true;
        return duplicate;
    }

    /// <summary>
    /// Try-pattern variant of <see cref="DuplicateTopCard"/> for callers that
    /// want to handle an incompletely configured deck without exceptions.
    /// </summary>
    public bool TryDuplicateTopCard(out CardControl duplicate)
    {
        duplicate = DuplicateTopCard();
        return duplicate is not null && IsInstanceValid(duplicate);
    }

    /// <summary>
    /// 获取当前牌堆顶的牌在画布坐标系中的姿态。
    /// </summary>
    /// <param name="topPose">牌堆顶的牌的画布变换、尺寸和正反面。</param>
    /// <returns> 如果牌堆顶没牌，返回 false </returns>
    /// <exception cref="InvalidOperationException">
    /// 牌堆场景没有配置厚度层或顶牌。
    /// </exception>
    public bool TryGetTopPose(out CardPose2D topPose)
    {
        int index = GetTopLayerIndex();
        if (index == -1)
        {
            topPose = default;
            return false;
        }

        if (!IsInstanceValid(_topCard))
        {
            throw new InvalidOperationException(
                "A valid top card must be assigned before querying its pose."
            );
        }

        topPose = new CardPose2D(
            _topCard.GetGlobalTransformWithCanvas(),
            new Vector2(_topCard.CardWidth, _topCard.CardHeight),
            _topCard.IsFaceUp
        );
        return true;
    }

    public void ChangeCardCount(int newCount)
    {
        CardCount = Math.Min(MaxCardCount, Math.Max(0, newCount));
        RefreshLayers();
    }

    public void ChangeMaxCardCount(int newMaxCardCount)
    {
        MaxCardCount = Math.Max(1, newMaxCardCount);
        ChangeCardCount(CardCount);
    }

    private void RefreshLayers()
    {
        int topLayerIndex = GetTopLayerIndex();

        if (topLayerIndex < 0)
        {
            _topCard.Visible = false;

            foreach (var layer in _layers)
                layer.Visible = false;

            return;
        }

        for (int i = 0; i < _layers.Count; i++)
        {
            _layers[i].Visible = i <= topLayerIndex;
        }

        _topCard.Visible = true;
        _topCard.GlobalPosition = _layers[topLayerIndex].GlobalPosition;
    }
	
    public int GetTopLayerIndex()
    {
        if (_layers.Count < 1)
        {
            throw new InvalidOperationException("Layers cannot be empty.");
        }
		
        int maxLayerIndex = _layers.Count;
		
        if (CardCount == 0 || MaxCardCount == 0)
            return -1;

        return -1 + Mathf.CeilToInt(
            CardCount / (float)MaxCardCount * maxLayerIndex
        );
    }

    // Called every frame. 'delta' is the elapsed time since the previous frame.
    public override void _Process(double delta)
    {
    }
}
