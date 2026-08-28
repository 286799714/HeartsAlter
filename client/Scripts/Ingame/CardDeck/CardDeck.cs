using System;
using Godot;

namespace HeartsAlter.Scripts.InGame.CardDeck;

public partial class CardDeck : Control
{
    [Export] private Godot.Collections.Array<Control> _layers = [];
    [Export] private Control _topCard = null!;

    public int MaxCardCount { get; private set; } = 1;
	
    public int CardCount { get; private set; }
	
    // Called when the node enters the scene tree for the first time.
    public override void _Ready()
    {
    }

    /// <summary>
    /// 获取当前牌堆顶的牌的 Global Transform
    /// </summary>
    /// <param name="topTransform"> 牌堆顶的牌的 Global Transform </param>
    /// <returns> 如果牌堆顶没牌，返回 false </returns>
    public bool TryGetTopTransform(out Transform2D topTransform)
    {
        int index = GetTopLayerIndex();
        if (index == -1)
        {
            topTransform = default;
            return false;
        }
        topTransform = _layers[index].GetGlobalTransform();
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