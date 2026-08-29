using System;
using System.Globalization;
using Godot;

namespace HeartsAlter.Scripts.InGame;

/// <summary>
/// Displays the public information for one seat at the table.
/// Network code can update the complete view with <see cref="SetPlayerInfo"/>
/// or update individual values through the exported properties.
/// </summary>
public partial class PlayerInfo : Control
{
	private const string DisconnectedPlayerText = "未连接";

	[ExportGroup("Player")]

	[Export]
	public Texture2D AvatarTexture
	{
		get => _avatarTexture;
		set
		{
			_avatarTexture = value;
			RefreshAvatar();
		}
	}

	[Export]
	public string PlayerId
	{
		get => _playerId;
		set
		{
			_playerId = value?.Trim() ?? string.Empty;
			RefreshPlayerId();
		}
	}

	[Export(PropertyHint.Range, "1,999,1,or_greater")]
	public int Level
	{
		get => _level;
		set
		{
			_level = Math.Max(1, value);
			RefreshLevel();
		}
	}

	[Export(PropertyHint.Range, "0,1000000000,1,or_greater")]
	public int ChipCount
	{
		get => _chipCount;
		set
		{
			_chipCount = Math.Max(0, value);
			RefreshChipCount();
		}
	}

	[Export(PropertyHint.Range, "0,32,1,or_greater")]
	public int RoundScore
	{
		get => _roundScore;
		set
		{
			_roundScore = Math.Max(0, value);
			CancelScoreAnimations();
			RefreshRoundScore();
			HideScoreDelta();
		}
	}

	[ExportGroup("Collection")]

	[Export(PropertyHint.Range, "1,200,1,or_greater,suffix:px")]
	public float CollectCardHeight = 72.0f;

	[Export]
	public bool CollectCardsFaceUp;

	[ExportGroup("Score Animation")]

	[Export(PropertyHint.Range, "0,2,0.01,or_greater,suffix:s")]
	public float ScoreRollDuration = 0.35f;

	[Export(PropertyHint.Range, "0,5,0.05,or_greater,suffix:s")]
	public float ScoreDeltaHoldDuration = 0.85f;

	[Export(PropertyHint.Range, "0,2,0.01,or_greater,suffix:s")]
	public float ScoreDeltaFadeDuration = 0.3f;

	[ExportGroup("Scene References")]

	[Export]
	private TextureRect _avatarTextureRect = null!;

	[Export]
	private Label _playerIdLabel = null!;

	[Export]
	private Label _levelLabel = null!;

	[Export]
	private Label _chipCountLabel = null!;

	[Export]
	private Label _scoreValueLabel = null!;

	[Export]
	private Label _scoreDeltaLabel = null!;

	[Export]
	private Marker2D _collectPoseMarker = null!;

	[Export]
	private Label _turnStatusLabel = null!;

	[Export]
	private Label _turnCountdownLabel = null!;

	private Texture2D _avatarTexture = null!;
	private Texture2D _fallbackAvatar = null!;
	private string _playerId = string.Empty;
	private int _level = 1;
	private int _chipCount;
	private int _roundScore;
	private Tween _scoreRollTween = null!;
	private Tween _scoreDeltaTween = null!;
	private double _turnDeadlineMsec;

	public override void _Ready()
	{
		ResolveSceneReferences();

		if (IsValid(_avatarTextureRect))
			_fallbackAvatar = _avatarTextureRect.Texture;

		RefreshDisplay();
	}

	public override void _ExitTree()
	{
		CancelScoreAnimations();
	}

	public override void _Process(double delta)
	{
		if (_turnDeadlineMsec <= 0.0 || !IsValid(_turnCountdownLabel))
			return;
		double remaining = _turnDeadlineMsec - Time.GetTicksMsec();
		if (remaining <= 0.0)
		{
			ClearTurnCountdown();
			return;
		}
		int seconds = Math.Max(0, (int)Math.Ceiling(remaining / 1000.0));
		_turnCountdownLabel.Text = $"剩余时间：{seconds} 秒";
	}

	/// <summary>
	/// Returns the authored canvas-space pose used when this player collects a
	/// trick. The marker supplies position and rotation; the configured height
	/// preserves the source card's aspect ratio.
	/// </summary>
	public Card.CardPose2D GetCollectPose(Card.CardControl card)
	{
		ArgumentNullException.ThrowIfNull(card);
		if (!IsValid(card))
			throw new ArgumentException("Card must be a valid Godot instance.", nameof(card));

		ResolveSceneReferences();

		float sourceHeight = Mathf.Max(1.0f, card.CardHeight);
		float targetHeight = Mathf.Max(1.0f, CollectCardHeight);
		Vector2 targetSize = new(
			card.CardWidth * targetHeight / sourceHeight,
			targetHeight
		);

		Transform2D centerTransform = IsValid(_collectPoseMarker)
			? _collectPoseMarker.GetGlobalTransformWithCanvas()
			: GetGlobalTransformWithCanvas() * new Transform2D(0.0f, Size * 0.5f);
		Transform2D topLeftTransform = centerTransform * new Transform2D(
			0.0f,
			-targetSize * 0.5f
		);

		return new Card.CardPose2D(
			topLeftTransform,
			targetSize,
			CollectCardsFaceUp
		);
	}

	/// <summary>
	/// Applies one trick's score change and owns its complete presentation:
	/// rolling the total, showing the signed delta, then fading the delta out.
	/// </summary>
	public void ApplyRoundScoreDelta(int scoreDelta)
	{
		ResolveSceneReferences();
		CancelScoreAnimations();

		int previousScore = _roundScore;
		long requestedScore = (long)previousScore + scoreDelta;
		int nextScore = (int)Math.Clamp(requestedScore, 0L, int.MaxValue);
		int appliedDelta = nextScore - previousScore;
		_roundScore = nextScore;

		if (IsValid(_scoreValueLabel) && ScoreRollDuration > 0.0f)
		{
			_scoreRollTween = CreateTween();
			_scoreRollTween.TweenMethod(
				Callable.From<float>(progress =>
				{
					if (!IsValid(_scoreValueLabel))
						return;

					int displayedScore = Mathf.RoundToInt(
						Mathf.Lerp(previousScore, nextScore, progress)
					);
					_scoreValueLabel.Text = $"{displayedScore} 分";
				}),
				0.0f,
				1.0f,
				ScoreRollDuration
			)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.InOut);
			_scoreRollTween.TweenCallback(Callable.From(RefreshRoundScore));
		}
		else
		{
			RefreshRoundScore();
		}
		if (appliedDelta == 0)
		{
			HideScoreDelta();
			return;
		}

		ShowScoreDelta(appliedDelta);
	}

	/// <summary>Starts the local countdown shown when this is the active seat.</summary>
	public void StartTurnCountdown(int durationMilliseconds)
	{
		StartCountdown(durationMilliseconds, "到你出牌");
	}

	/// <summary>Starts the shared countdown with the passing-stage label.</summary>
	public void StartPassCountdown(int durationMilliseconds)
	{
		StartCountdown(durationMilliseconds, "选择传牌");
	}

	private void StartCountdown(int durationMilliseconds, string status)
	{
		ResolveSceneReferences();
		_turnDeadlineMsec = (double)Time.GetTicksMsec() + Math.Max(0, durationMilliseconds);
		if (IsValid(_turnStatusLabel))
		{
			_turnStatusLabel.Text = status;
			_turnStatusLabel.Visible = true;
		}
		if (IsValid(_turnCountdownLabel))
		{
			_turnCountdownLabel.Visible = true;
			_turnCountdownLabel.Text = $"剩余时间：{Math.Max(0, (int)Math.Ceiling(durationMilliseconds / 1000.0))} 秒";
		}
	}

	public void ClearTurnCountdown()
	{
		_turnDeadlineMsec = 0.0;
		if (IsValid(_turnStatusLabel)) _turnStatusLabel.Visible = false;
		if (IsValid(_turnCountdownLabel)) _turnCountdownLabel.Visible = false;
	}

	/// <summary>
	/// Updates all fields shown by this component in one call.
	/// Passing a null avatar uses the scene's built-in placeholder.
	/// </summary>
	public void SetPlayerInfo(
		string playerId,
		int roundScore,
		Texture2D avatarTexture = null)
	{
		SetPlayerInfo(
			playerId,
			_level,
			_chipCount,
			roundScore,
			avatarTexture
		);
	}

	/// <summary>
	/// Updates the complete player display, including level and chip balance.
	/// </summary>
	public void SetPlayerInfo(
		string playerId,
		int level,
		int chipCount,
		int roundScore,
		Texture2D avatarTexture = null)
	{
		_playerId = playerId?.Trim() ?? string.Empty;
		_level = Math.Max(1, level);
		_chipCount = Math.Max(0, chipCount);
		_roundScore = Math.Max(0, roundScore);
		_avatarTexture = avatarTexture;
		CancelScoreAnimations();
		HideScoreDelta();
		RefreshDisplay();
	}

	public void SetAvatar(Texture2D avatarTexture)
	{
		AvatarTexture = avatarTexture;
	}

	public void SetPlayerId(string playerId)
	{
		PlayerId = playerId;
	}

	public void SetLevel(int level)
	{
		Level = level;
	}

	public void SetChipCount(int chipCount)
	{
		ChipCount = chipCount;
	}

	public void SetRoundScore(int roundScore)
	{
		RoundScore = roundScore;
	}

	private void ResolveSceneReferences()
	{
		if (!IsValid(_avatarTextureRect))
		{
			_avatarTextureRect = GetNodeOrNull<TextureRect>(
				"Card/Content/Column/AvatarBlock/AvatarFrame/AvatarPadding/AvatarTexture"
			);
		}

		if (!IsValid(_playerIdLabel))
		{
			_playerIdLabel = GetNodeOrNull<Label>(
				"Card/Content/Column/PlayerIdLabel"
			);
		}

		if (!IsValid(_levelLabel))
		{
			_levelLabel = GetNodeOrNull<Label>(
				"Card/Content/Column/AvatarBlock/LevelBadge/LevelLabel"
			);
		}

		if (!IsValid(_chipCountLabel))
		{
			_chipCountLabel = GetNodeOrNull<Label>(
				"Card/Content/Column/ChipCountLabel"
			);
		}

		if (!IsValid(_scoreValueLabel))
		{
			_scoreValueLabel = GetNodeOrNull<Label>(
				"Card/Content/Column/ScoreRow/ScoreBadge/ScorePadding/ScoreValueLabel"
			);
		}

		if (!IsValid(_scoreDeltaLabel))
		{
			_scoreDeltaLabel = GetNodeOrNull<Label>(
				"Card/Content/Column/ScoreRow/ScoreDeltaLabel"
			) ?? GetNodeOrNull<Label>(
				"Card/Content/Row/Details/ScoreRow/ScoreDeltaLabel"
			) ?? GetNodeOrNull<Label>(
				"Padding/Row/ScoreGroup/ScoreDeltaLabel"
			);
		}

		if (!IsValid(_collectPoseMarker))
			_collectPoseMarker = GetNodeOrNull<Marker2D>("CollectPoseMarker");
		if (!IsValid(_turnStatusLabel))
			_turnStatusLabel = GetNodeOrNull<Label>("TurnStatusLabel");
		if (!IsValid(_turnCountdownLabel))
			_turnCountdownLabel = GetNodeOrNull<Label>("TurnCountdownLabel");
	}

	private void RefreshDisplay()
	{
		RefreshAvatar();
		RefreshPlayerId();
		RefreshLevel();
		RefreshChipCount();
		RefreshRoundScore();
	}

	private void RefreshAvatar()
	{
		if (!IsValid(_avatarTextureRect))
			return;

		_avatarTextureRect.Texture = IsValid(_avatarTexture)
			? _avatarTexture
			: _fallbackAvatar;
	}

	private void RefreshPlayerId()
	{
		if (!IsValid(_playerIdLabel))
			return;

		string displayText = string.IsNullOrWhiteSpace(_playerId)
			? DisconnectedPlayerText
			: _playerId;

		_playerIdLabel.Text = displayText;
		_playerIdLabel.TooltipText = displayText;
	}

	private void RefreshLevel()
	{
		if (IsValid(_levelLabel))
			_levelLabel.Text = _level.ToString(CultureInfo.InvariantCulture);
	}

	private void RefreshChipCount()
	{
		if (IsValid(_chipCountLabel))
		{
			_chipCountLabel.Text = string.Format(
				CultureInfo.InvariantCulture,
				"{0:N0} 筹码",
				_chipCount
			);
		}
	}

	private void RefreshRoundScore()
	{
		if (IsValid(_scoreValueLabel))
			_scoreValueLabel.Text = $"{_roundScore} 分";
	}

	private void ShowScoreDelta(int scoreDelta)
	{
		if (!IsValid(_scoreDeltaLabel))
			return;

		_scoreDeltaLabel.Text = scoreDelta >= 0
			? $"+{scoreDelta}"
			: scoreDelta.ToString(CultureInfo.InvariantCulture);
		_scoreDeltaLabel.Visible = true;
		_scoreDeltaLabel.Modulate = Colors.White;

		_scoreDeltaTween = CreateTween();
		if (ScoreDeltaHoldDuration > 0.0f)
			_scoreDeltaTween.TweenInterval(ScoreDeltaHoldDuration);

		if (ScoreDeltaFadeDuration > 0.0f)
		{
			_scoreDeltaTween.TweenProperty(
				_scoreDeltaLabel,
				"modulate",
				new Color(1.0f, 1.0f, 1.0f, 0.0f),
				ScoreDeltaFadeDuration
			)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.In);
		}

		_scoreDeltaTween.TweenCallback(Callable.From(HideScoreDelta));
	}

	private void HideScoreDelta()
	{
		if (!IsValid(_scoreDeltaLabel))
			return;

		// Keep the label in its container so the reserved delta width never
		// changes. Toggling Visible would make the centered score row reflow and
		// visibly push "红包点数" left/right at the start and end of the effect.
		_scoreDeltaLabel.Visible = true;
		_scoreDeltaLabel.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
	}

	private void CancelScoreAnimations()
	{
		if (_scoreRollTween is { } rollTween && rollTween.IsValid())
			rollTween.Kill();
		if (_scoreDeltaTween is { } deltaTween && deltaTween.IsValid())
			deltaTween.Kill();

		_scoreRollTween = null!;
		_scoreDeltaTween = null!;
	}

	private static bool IsValid(GodotObject instance)
	{
		return instance is not null && GodotObject.IsInstanceValid(instance);
	}
}
