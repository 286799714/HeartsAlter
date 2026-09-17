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
	public event Action AddBotRequested;
	public event Action KickPlayerRequested;
	private Control _waitingLayout;
	private Control _joinedAvatar;
	private Control _joinedInfo;
	private Control _waitingStatus;
	private Control _gameScore;
	private Control _gameChips;
	private BaseButton _addBotButton;
	private BaseButton _kickButton;
	private CanvasItem _readyText;
	private CanvasItem _notReadyText;
	private bool _occupied = true;
	private bool _waiting;
	private bool _ready;
	private bool _canManage;
	private const string DisconnectedPlayerText = "未连接";
	private static readonly Texture2D[] ProfileAvatars = new Texture2D[4];

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

	[Export]
	public bool IsHost
	{
		get => _isHost;
		set
		{
			_isHost = value;
			RefreshRole();
		}
	}

	[Export]
	public bool IsLocalPlayer
	{
		get => _isLocalPlayer;
		set
		{
			_isLocalPlayer = value;
			RefreshRole();
		}
	}

	[Export]
	public bool IsBot
	{
		get => _isBot;
		set
		{
			_isBot = value;
			RefreshRole();
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
	private Label _roleLabel = null!;

	[Export]
	private Label _chipCountLabel = null!;

	[Export]
	private Label _scoreValueLabel = null!;

	[Export]
	private Label _scoreDeltaLabel = null!;

	[Export]
	private Marker2D _collectPoseMarker = null!;

	private Texture2D _avatarTexture = null!;
	private Texture2D _fallbackAvatar = null!;
	private string _playerId = string.Empty;
	private int _chipCount;
	private int _roundScore;
	private bool _isHost;
	private bool _isLocalPlayer;
	private bool _isBot;
	private Tween _scoreRollTween = null!;
	private Tween _scoreDeltaTween = null!;

	public override void _Ready()
	{
		ResolveSceneReferences();
		BindWaitingUi();

		if (IsValid(_avatarTextureRect))
			_fallbackAvatar = _avatarTextureRect.Texture;

		RefreshDisplay();
	}

	private void BindWaitingUi()
	{
		_waitingLayout = GetNodeOrNull<Control>("WaitingLayout");
		_joinedAvatar = GetNodeOrNull<Control>("JoinedPlayerAvatar");
		_joinedInfo = GetNodeOrNull<Control>("JoinedPlayerInfo");
		_waitingStatus = GetNodeOrNull<Control>("JoinedPlayerInfo/InnerContainer/Waiting_准备状态");
		_gameScore = GetNodeOrNull<Control>("JoinedPlayerInfo/InnerContainer/InGame_得分");
		_gameChips = GetNodeOrNull<Control>("JoinedPlayerInfo/InnerContainer/InGame_金币");
		_addBotButton = GetNodeOrNull<BaseButton>("WaitingLayout/InnerContainer/添加机器人按钮");
		_kickButton = GetNodeOrNull<BaseButton>("JoinedPlayerInfo/InnerContainer/Waiting_踢出房间");
		_readyText = _waitingStatus?.FindChild("已准备Text", true, false) as CanvasItem;
		_notReadyText = _waitingStatus?.FindChild("未准备Text", true, false) as CanvasItem;
		IgnoreDecorativeMouseInput(this);
		if (_addBotButton is not null) _addBotButton.Pressed += () => AddBotRequested?.Invoke();
		if (_kickButton is not null) _kickButton.Pressed += () => KickPlayerRequested?.Invoke();
		RefreshSeatState();
	}

	/// <summary>Public room state controls occupancy, stage, readiness and host actions.</summary>
	public void SetSeatState(bool occupied, bool waiting, bool ready, bool canManage)
	{
		_occupied = occupied;
		_waiting = waiting;
		_ready = ready;
		_canManage = canManage;
		RefreshSeatState();
	}

	private void RefreshSeatState()
	{
		if (_waitingLayout is not null) _waitingLayout.Visible = !_occupied;
		if (_joinedAvatar is not null) _joinedAvatar.Visible = _occupied;
		if (_joinedInfo is not null) _joinedInfo.Visible = _occupied;
		if (_waitingStatus is not null) _waitingStatus.Visible = _waiting;
		if (_gameScore is not null) _gameScore.Visible = !_waiting;
		if (_gameChips is not null) _gameChips.Visible = !_waiting;
		if (_readyText is not null) _readyText.Visible = _ready;
		if (_notReadyText is not null) _notReadyText.Visible = !_ready;
		if (_addBotButton is not null)
			_addBotButton.Visible = _waiting && !_occupied && _canManage;
		if (_kickButton is not null)
			_kickButton.Visible = _waiting && _occupied && _canManage && !_isHost && !_isLocalPlayer;
	}

	private static void IgnoreDecorativeMouseInput(Node node)
	{
		if (node is Control control && node is not BaseButton)
			control.MouseFilter = MouseFilterEnum.Ignore;
		foreach (Node child in node.GetChildren()) IgnoreDecorativeMouseInput(child);
	}

	public override void _ExitTree()
	{
		CancelScoreAnimations();
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
			? Card.CardPose2D.GetRenderedCanvasTransform(_collectPoseMarker)
			: Card.CardPose2D.GetRenderedCanvasTransform(this) * new Transform2D(0.0f, Size * 0.5f);
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
					_scoreValueLabel.Text = $"{displayedScore}";
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
			_chipCount,
			roundScore,
			avatarTexture
		);
	}

	/// <summary>
	/// Updates the complete player display
	/// </summary>
	public void SetPlayerInfo(
		string playerId,
		int chipCount,
		int roundScore,
		Texture2D avatarTexture = null)
	{
		_playerId = playerId?.Trim() ?? string.Empty;
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

	/// <summary>Applies a server save without resetting in-progress score animations.</summary>
	public void SetProfile(PlayerProfile profile)
	{
		ArgumentNullException.ThrowIfNull(profile);
		SetProfileIdentity(profile.Name, profile.Chips, profile.AvatarId);
	}

	/// <summary>Also accepts the public save fields synchronized for another seat.</summary>
	public void SetProfileIdentity(string playerName, int chips, int avatarId)
	{
		PlayerId = playerName;
		ChipCount = chips;
		SetAvatarId(avatarId);
	}

	public void SetAvatarId(int avatarId)
	{
		// Missing/unknown IDs from older servers use the first bundled avatar.
		int index = avatarId >= 1 && avatarId <= ProfileAvatars.Length ? avatarId - 1 : 0;
		if (!IsValid(ProfileAvatars[index]))
			ProfileAvatars[index] = GD.Load<Texture2D>($"res://assets/textures/ui/profile_icon_{index + 1}.jpg");
		AvatarTexture = ProfileAvatars[index];
	}

	public void SetPlayerId(string playerId)
	{
		PlayerId = playerId;
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
			// All three layouts expose the avatar with the same scene-unique name.
			_avatarTextureRect = GetNodeOrNull<TextureRect>("%AvatarTexture") ?? GetNodeOrNull<TextureRect>(
				"Card/Content/Column/AvatarBlock/AvatarFrame/AvatarPadding/AvatarTexture"
			);
		}

		if (!IsValid(_playerIdLabel))
		{
			_playerIdLabel = GetNodeOrNull<Label>(
				"Card/Content/Column/PlayerIdLabel"
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
		if (!IsValid(_roleLabel))
			_roleLabel = GetNodeOrNull<Label>("RoleLabel");
	}

	private void RefreshDisplay()
	{
		RefreshAvatar();
		RefreshPlayerId();
		RefreshRole();
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

	private void RefreshRole()
	{
		if (!IsValid(_roleLabel)) return;
		_roleLabel.Text = (_isBot, _isLocalPlayer, _isHost) switch
		{
			(true, _, _) => "机器人",
			(_, true, true) => "你·房主",
			(_, true, false) => "你",
			(_, false, true) => "房主",
			_ => "房客",
		};
	}

	private void RefreshChipCount()
	{
		if (IsValid(_chipCountLabel))
		{
			_chipCountLabel.Text = string.Format(
				CultureInfo.InvariantCulture,
				"{0:N0}",
				_chipCount
			);
		}
	}

	private void RefreshRoundScore()
	{
		if (IsValid(_scoreValueLabel))
			_scoreValueLabel.Text = $"{_roundScore}";
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
