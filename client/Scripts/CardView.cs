using Godot;
using System;
using System.Collections.Generic;

namespace HeartsAlter;

/// <summary>
/// A deliberately small view object for one card in the table.
///
/// Card textures are loaded on demand.  This keeps the first frame light (the
/// offline table initially needs one face-up hand and a single shared back)
/// while still allowing the complete 52-card deck to be displayed.
/// </summary>
public partial class CardView : TextureButton
{
    public const string CardAssetRoot = "res://assets/cards/";
    public const string CardBackId = "Background";
    public static readonly Vector2 DefaultCardSize = new(90.0f, 126.0f);

    private static readonly Dictionary<string, Texture2D> TextureCache = new();

    private bool _faceUp = true;
    private bool _pressedSignalConnected;

    /// <summary>The protocol card id, for example <c>HeartQ</c>.</summary>
    public string CardId { get; private set; } = string.Empty;

    /// <summary>Whether the card currently renders its face or the shared back.</summary>
    public bool IsFaceUp => _faceUp;

    /// <summary>Raised after Godot's Pressed signal, with this card as its payload.</summary>
    public event Action<CardView> Clicked;

    public CardView()
    {
        // This node is laid out manually by Main; a Container must never
        // overwrite the positions that are being tweened.
        IgnoreTextureSize = true;
        StretchMode = StretchModeEnum.Scale;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        SetCardSize(DefaultCardSize);
    }

    public override void _Ready()
    {
        if (!_pressedSignalConnected)
        {
            Pressed += OnPressed;
            _pressedSignalConnected = true;
        }

        // SetCardId may have been called before AddChild.  Reapplying here is
        // cheap (and cached) and also covers cards instantiated from a scene.
        ApplyTexture();
    }

    /// <summary>
    /// Assigns the protocol id and updates the displayed texture.  The front
    /// image is not loaded when the card is face down.
    /// </summary>
    public void SetCardId(string cardId, bool faceUp = true)
    {
        CardId = cardId ?? string.Empty;
        _faceUp = faceUp;
        Name = string.IsNullOrEmpty(CardId) ? "Card" : $"Card_{CardId}";
        ApplyTexture();
    }

    public void SetFaceUp(bool faceUp)
    {
        if (_faceUp == faceUp)
        {
            return;
        }

        _faceUp = faceUp;
        ApplyTexture();
    }

    /// <summary>Sets a stable logical size, independent of source PNG dimensions.</summary>
    public void SetCardSize(Vector2 size)
    {
        CustomMinimumSize = size;
        Size = size;
        PivotOffset = size * 0.5f;
    }

    /// <summary>
    /// Moves this card with the project's canonical easeInOutSine curve.
    /// </summary>
    public Tween AnimateTo(Vector2 target, double duration = 0.32, double delay = 0.0)
    {
        var tween = CreateTween();
        tween.SetTrans(Tween.TransitionType.Sine);
        tween.SetEase(Tween.EaseType.InOut);
        var property = tween.TweenProperty(this, "position", target, duration);
        if (delay > 0.0)
        {
            property.SetDelay(delay);
        }

        return tween;
    }

    /// <summary>Animates both position and scale using the same easing curve.</summary>
    public Tween AnimateTo(Vector2 target, Vector2 targetScale, double duration = 0.32, double delay = 0.0)
    {
        var tween = CreateTween();
        tween.SetTrans(Tween.TransitionType.Sine);
        tween.SetEase(Tween.EaseType.InOut);
        var position = tween.TweenProperty(this, "position", target, duration);
        if (delay > 0.0)
        {
            position.SetDelay(delay);
        }

        tween.Parallel().TweenProperty(this, "scale", targetScale, duration);
        return tween;
    }

    /// <summary>Clears the process-wide texture cache (useful for a hot reload).</summary>
    public static void ClearTextureCache()
    {
        TextureCache.Clear();
    }

    private void OnPressed()
    {
        Clicked?.Invoke(this);
    }

    private void ApplyTexture()
    {
        var textureId = _faceUp && !string.IsNullOrEmpty(CardId) ? CardId : CardBackId;
        var texture = LoadTexture(textureId);
        TextureNormal = texture;
        TextureHover = texture;
        TexturePressed = texture;
        TextureDisabled = texture;
    }

    private static Texture2D LoadTexture(string id)
    {
        if (TextureCache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var texture = ResourceLoader.Load<Texture2D>($"{CardAssetRoot}{id}.png");
        if (texture != null)
        {
            TextureCache[id] = texture;
        }

        return texture;
    }
}
