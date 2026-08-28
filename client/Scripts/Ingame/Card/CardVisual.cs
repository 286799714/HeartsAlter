#nullable enable
using System;
using Godot;

namespace HeartsAlter.Scripts.InGame.Card;

public partial class CardVisual : Control
{
	[Export]
	private CardResource _resource = null!;
	
	[Export]
	private TextureRect _front = null!;

	[Export]
	private TextureRect _back = null!;

	[Signal]
	public delegate void FlipCompletedEventHandler();
	
	private Card _card = null!;
	private bool _isBound;
    private CardData Data => _card.Data;
	
	public bool IsFront { get; private set; }
	
	private ShaderMaterial _frontShader = null!;
	private ShaderMaterial _backShader = null!;
	
	private Tween? _activeFlipTween;

    private bool _hasSetup;

    private bool _initialFace;
	
	/// <summary>
	/// 当前翻牌动画的目标面。
	/// 与 IsFront 不同：
	///
	/// IsFront 代表最后一次稳定完成的状态；
	/// _flipTarget 代表当前正在尝试达到的状态。
	/// </summary>
	private bool _flipTarget;

	 public override void _Ready()
    {
        InitializeMaterials();

        // TextureRect 应当由 Card Control 决定尺寸。
        _front.ExpandMode =
            TextureRect.ExpandModeEnum.IgnoreSize;

        _back.ExpandMode =
            TextureRect.ExpandModeEnum.IgnoreSize;

        _front.StretchMode =
            TextureRect.StretchModeEnum.Scale;

        _back.StretchMode =
            TextureRect.StretchModeEnum.Scale;

        _front.MouseFilter = MouseFilterEnum.Ignore;
        _back.MouseFilter = MouseFilterEnum.Ignore;

        Resized += SyncShaderRectSize;

        SyncShaderRectSize();
        if (_hasSetup && _isBound)
            ApplySetup();
        else
            SetFace(false);
    }

    public void Setup(bool startFaceUp = false)
    {
        _initialFace = startFaceUp;
        _hasSetup = true;

        if (_isBound && IsNodeReady())
            ApplySetup();
    }

    private void ApplySetup()
    {
        _front.Texture = _resource.GetFront(Data.Suit, Data.Rank);

        _back.Texture ??= _resource.GetBack();

        SetFace(_initialFace);
    }
    
    // ============================================================
    // Material
    // ============================================================

    private void InitializeMaterials()
    {
        if (_front.Material is not ShaderMaterial frontMaterial)
        {
            throw new InvalidOperationException(
                "CardFront must have a ShaderMaterial."
            );
        }

        if (_back.Material is not ShaderMaterial backMaterial)
        {
            throw new InvalidOperationException(
                "CardBack must have a ShaderMaterial."
            );
        }

        /*
         * ShaderMaterial 是 Resource。
         * 如果 Front / Back 引用了同一个 Material，
         * 修改 rot_y_deg 会同时修改二者。
         *
         * 因此这里显式复制成两个独立实例。
         */
        _frontShader =
            (ShaderMaterial)frontMaterial.Duplicate();

        _backShader =
            (ShaderMaterial)backMaterial.Duplicate();

        _front.Material = _frontShader;
        _back.Material = _backShader;
    }


    private void SyncShaderRectSize()
    {
        /*
         * Front / Back 都是 Full Rect，
         * 因此其尺寸等于 PokerCard.Size。
         */
        _frontShader.SetShaderParameter(
            "rect_size",
            Size
        );

        _backShader.SetShaderParameter(
            "rect_size",
            Size
        );
    }


    // ============================================================
    // Face
    // ============================================================

    public void SetFace(bool front)
    {
        IsFront = front;
        _flipTarget = front;

        _front.Visible = front;
        _back.Visible = !front;

        ResetShaderRotation();
    }


    private void ResetShaderRotation()
    {
        _frontShader.SetShaderParameter(
            "rot_y_deg",
            0.0f
        );

        _frontShader.SetShaderParameter(
            "rot_x_deg",
            0.0f
        );

        _backShader.SetShaderParameter(
            "rot_y_deg",
            0.0f
        );

        _backShader.SetShaderParameter(
            "rot_x_deg",
            0.0f
        );
    }

    // ============================================================
    // Flip animation
    // ============================================================

    public bool IsFlipping =>
        _activeFlipTween is { } tween &&
        tween.IsValid();
    
    public void ToggleFace(
        double duration = 0.3,
        bool reverse = false,
        Tween.TransitionType transitionType = Tween.TransitionType.Cubic,
        Tween.EaseType easeType = Tween.EaseType.Out)
    {
        if (IsFlipping)
            return;

        PlayFlip(!IsFront, duration, reverse, transitionType, easeType);
    }
    
    public void PlayFlip(
        bool toFront,
        double duration,
        bool reverse,
        Tween.TransitionType transitionType,
        Tween.EaseType easeType)
    {
        // 已经处于或正在前往相同状态。
        if (_flipTarget == toFront)
            return;

        // 中断当前翻牌。
        if (_activeFlipTween is { } oldTween &&
            oldTween.IsValid())
        {
            oldTween.Kill();

            _activeFlipTween = null;

            /*
             * 回到上一个稳定状态。
             *
             * 例如：
             *
             * Back -> 正在翻向 Front
             *           ↓
             *        被打断
             *           ↓
             *       回到 Back
             */
            SetFace(IsFront);
        }

        _flipTarget = toFront;


        // ===== 确定出场面 / 入场面 =====

        TextureRect outFace;
        ShaderMaterial outShader;

        TextureRect inFace;
        ShaderMaterial inShader;

        if (toFront)
        {
            outFace = _back;
            outShader = _backShader;

            inFace = _front;
            inShader = _frontShader;
        }
        else
        {
            outFace = _front;
            outShader = _frontShader;

            inFace = _back;
            inShader = _backShader;
        }

        Tween tween = CreateTween();

        _activeFlipTween = tween;
        
        float edgeAngle = reverse ? -89.9f : 89.9f;
        const float FlipScale = 0.85f;
        
        bool faceSwitched = false;
        
        tween.TweenMethod(
                Callable.From<float>(value =>
                {
                    if (value < 0.5f)
                    {
                        outShader.SetShaderParameter(
                            "rot_y_deg",
                            Mathf.Lerp(0.0f, edgeAngle, value * 2.0f)
                        );
                    }
                    else
                    {
                        // 半程切换上下面可见性
                        if (!faceSwitched)
                        {
                            faceSwitched = true;

                            outFace.Visible = false;
                            outShader.SetShaderParameter(
                                "rot_y_deg",
                                0.0f
                            );

                            inFace.Visible = true;
                            inShader.SetShaderParameter(
                                "rot_y_deg",
                                -edgeAngle
                            );
                        }
                        
                        inShader.SetShaderParameter(
                            "rot_y_deg",
                            Mathf.Lerp(-edgeAngle, 0.0f, (value - 0.5f) * 2.0f)
                        );
                    }
                    
                    float scaleProgress = Mathf.Sin(value * Mathf.Pi);
                    float scale = Mathf.Lerp(1.0f, FlipScale, scaleProgress);
                    Scale = Vector2.One * scale;
                }),
                0.0f,
                1.0f,
                duration
            )
            .SetTrans(transitionType)
            .SetEase(easeType);

        // 结束回调
        tween.TweenCallback(
            Callable.From(() =>
            {
                if (_activeFlipTween != tween)
                    return;

                Scale = Vector2.One;
                IsFront = toFront;
                _flipTarget = toFront;
                _activeFlipTween = null;

                EmitSignal(SignalName.FlipCompleted);
            })
        );
    }
	
	public void Bind(Card card)
	{
		ArgumentNullException.ThrowIfNull(card);
		_card = card;
		_isBound = true;

		// Setup may intentionally be called before the card is added to the
		// scene tree. In that case CardVisual._Ready had to defer loading the
		// texture until its owner was bound; apply it now that both conditions
		// are satisfied.
		if (_hasSetup && IsNodeReady())
			ApplySetup();
	}

    public void Release()
    {
        if (_activeFlipTween is { } flip &&
            flip.IsValid())
        {
            flip.Kill();
        }

        _activeFlipTween = null;

        QueueFree();
    }
}
