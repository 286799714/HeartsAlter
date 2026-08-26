using Godot;
using System;
using System.Collections.Generic;

namespace HeartsAlter;

/// <summary>
/// Programmatic table for the HeartsAlter client demo.
///
/// The server remains authoritative in a networked match.  Until a Colyseus
/// room is connected this scene runs a small, intentionally honest offline
/// presentation: it deals all 52 cards, lets the local player select/play
/// cards, and previews the local score.  No offline action is sent as if it
/// were server state.
/// </summary>
public partial class Main : Control
{
    private const int PlayerCount = 4;
    private const int CardsPerPlayer = 13;
    private const int LocalSeat = 0;
    private const int DefaultAnte = 100;
    private const double DealDuration = 0.32;
    private const double DealStep = 0.025;
    private const double LayoutDuration = 0.28;

    private static readonly string[] Suits = { "Club", "Diamond", "Heart", "Spade" };
    private static readonly string[] Ranks =
        { "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A" };

    private readonly List<CardView> _allCards = new();
    private readonly List<CardView> _localHand = new();
    private readonly List<CardView> _playedCards = new();
    private readonly List<CardView>[] _seatCards =
    {
        new List<CardView>(),
        new List<CardView>(),
        new List<CardView>(),
        new List<CardView>()
    };

    private readonly HashSet<string> _pendingNetworkCards = new(StringComparer.Ordinal);
    private readonly ColyseusClientAdapter _network = new();

    private Label _statusLabel;
    private Label _potLabel;
    private Label _scoreLabel;
    private Label _localSeatLabel;
    private Label _opponentTopLabel;
    private Label _opponentLeftLabel;
    private Label _opponentRightLabel;
    private Button _dealButton;
    private Button _sortButton;
    private Button _connectButton;
    private CardView _selectedCard;
    private Vector2 _layoutSize = new(1280.0f, 720.0f);
    private Vector2 _deckOrigin;
    private int _localScore;
    private int _roundNumber;
    private bool _networkMode;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        _layoutSize = GetViewportRect().Size;
        if (_layoutSize.X < 320.0f || _layoutSize.Y < 240.0f)
        {
            _layoutSize = new Vector2(1280.0f, 720.0f);
        }

        _network.StateChanged += OnNetworkStateChanged;
        _network.HandReceived += OnNetworkHandReceived;
        _network.CardPlayed += OnNetworkCardPlayed;
        _network.ServerMessage += OnNetworkMessage;
        _network.InvalidPlay += OnNetworkInvalidPlay;
        _network.Error += OnNetworkError;
        _network.Left += OnNetworkLeft;

        BuildTable();
        DealOffline();
    }

    private void BuildTable()
    {
        var tableTop = 74.0f;
        var tableBottom = _layoutSize.Y - 152.0f;
        var tableRect = new Rect2(28.0f, tableTop, _layoutSize.X - 56.0f, tableBottom - tableTop);
        _deckOrigin = new Vector2(_layoutSize.X * 0.5f - CardView.DefaultCardSize.X * 0.5f, tableTop + 22.0f);

        var background = new ColorRect
        {
            Color = new Color("101827"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        background.SetAnchorsAndOffsetsPreset(
            LayoutPreset.FullRect,
            LayoutPresetMode.Minsize,
            0);
        AddChild(background);

        var table = new Panel
        {
            Position = tableRect.Position,
            Size = tableRect.Size,
            MouseFilter = MouseFilterEnum.Ignore
        };
        table.AddThemeStyleboxOverride("panel", MakePanelStyle(
            new Color("174d3a"), new Color("3e8963"), 2, 22));
        AddChild(table);

        // Header and status area.
        AddLabel("HEARTS ALTER", new Vector2(30, 18), new Vector2(340, 42), 28,
            new Color("f8fafc"), HorizontalAlignment.Left);
        AddLabel("四人红心大战 · 筹码奖池变体", new Vector2(32, 48), new Vector2(390, 24), 13,
            new Color("9fb3c8"), HorizontalAlignment.Left);

        _potLabel = AddLabel($"奖池 0  ·  底注 {DefaultAnte}",
            new Vector2(_layoutSize.X - 330, 20), new Vector2(300, 30), 19,
            new Color("f7d774"), HorizontalAlignment.Right);
        _scoreLabel = AddLabel("你的点数 0", new Vector2(_layoutSize.X - 330, 49), new Vector2(300, 22), 13,
            new Color("d4e5dc"), HorizontalAlignment.Right);

        _statusLabel = AddLabel("离线演示：正在发牌…", new Vector2(330, 20), new Vector2(620, 28), 15,
            new Color("d9f2e6"), HorizontalAlignment.Center);

        _dealButton = AddButton("重新发牌", new Vector2(30, _layoutSize.Y - 44), new Vector2(118, 32));
        _dealButton.Pressed += OnDealPressed;
        _sortButton = AddButton("整理手牌", new Vector2(158, _layoutSize.Y - 44), new Vector2(118, 32));
        _sortButton.Pressed += OnSortPressed;
        _connectButton = AddButton("连接服务器", new Vector2(286, _layoutSize.Y - 44), new Vector2(142, 32));
        _connectButton.Pressed += OnConnectionPressed;

        AddLabel("点击一张牌选中，再次点击出牌 · 仅用于离线界面预览",
            new Vector2(446, _layoutSize.Y - 40), new Vector2(_layoutSize.X - 474, 28), 13,
            new Color("9fb3c8"), HorizontalAlignment.Left);

        // Four fixed seats.  The seat order is also the tie-break order used
        // by the authoritative server when it distributes integer remainders.
        _opponentTopLabel = AddSeatLabel("玩家 2\n13 张", new Vector2(_layoutSize.X * 0.5f - 100, tableTop + 8));
        _opponentLeftLabel = AddSeatLabel("玩家 3\n13 张", new Vector2(45, tableTop + 190));
        _opponentRightLabel = AddSeatLabel("玩家 4\n13 张", new Vector2(_layoutSize.X - 145, tableTop + 190));
        _localSeatLabel = AddSeatLabel("你 · 玩家 1\n13 张", new Vector2(_layoutSize.X * 0.5f - 100, _layoutSize.Y - 190));

        // Central trick well.  Cards are added after this node, so they draw
        // above the well without needing a separate CanvasLayer.
        var trickWell = new Panel
        {
            Position = new Vector2(_layoutSize.X * 0.5f - 112, tableTop + 118),
            Size = new Vector2(224, 176),
            MouseFilter = MouseFilterEnum.Ignore
        };
        trickWell.AddThemeStyleboxOverride("panel", MakePanelStyle(
            new Color("123f31"), new Color("2f7153"), 1, 18));
        AddChild(trickWell);
        AddLabel("本墩", new Vector2(_layoutSize.X * 0.5f - 80, tableTop + 126), new Vector2(160, 24), 14,
            new Color("8fbca5"), HorizontalAlignment.Center);

        AddLabel("计分：每张红桃 1 点，黑桃 Q 6 点 · 本变体不使用射月",
            new Vector2(32, tableTop + tableRect.Size.Y - 28), new Vector2(620, 22), 12,
            new Color("a5c4b1"), HorizontalAlignment.Left);
    }

    private void DealOffline()
    {
        _networkMode = false;
        _pendingNetworkCards.Clear();
        ClearCards();
        _localScore = 0;
        _selectedCard = null;
        _roundNumber++;
        if (_roundNumber > 9999)
        {
            _roundNumber = 1;
        }

        foreach (var seat in _seatCards)
        {
            seat.Clear();
        }

        var deck = BuildDeck();
        Shuffle(deck);

        for (var seat = 0; seat < PlayerCount; seat++)
        {
            for (var index = 0; index < CardsPerPlayer; index++)
            {
                var card = new CardView();
                var cardId = deck[seat * CardsPerPlayer + index];
                var isLocal = seat == LocalSeat;
                card.SetCardSize(isLocal ? CardView.DefaultCardSize : OpponentCardSize);
                card.SetCardId(cardId, isLocal);
                card.Position = _deckOrigin;
                card.ZIndex = isLocal ? 100 + index : 10 + index;
                card.Clicked += OnCardClicked;
                AddChild(card);

                _allCards.Add(card);
                _seatCards[seat].Add(card);
                if (isLocal)
                {
                    _localHand.Add(card);
                }

                var target = GetSeatCardPosition(seat, index);
                var scale = isLocal ? Vector2.One : Vector2.One;
                AnimateCard(card, target, scale, 0.0f, index * DealStep + seat * 0.018);
            }
        }

        _potLabel.Text = $"奖池 {PlayerCount * DefaultAnte}  ·  底注 {DefaultAnte}";
        _scoreLabel.Text = "你的点数 0";
        _localSeatLabel.Text = "你 · 玩家 1\n13 张";
        _opponentTopLabel.Text = "玩家 2\n13 张";
        _opponentLeftLabel.Text = "玩家 3\n13 张";
        _opponentRightLabel.Text = "玩家 4\n13 张";
        _statusLabel.Text = $"离线演示 · 第 {_roundNumber} 局：52 张牌已发完";
    }

    private void ClearCards()
    {
        foreach (var card in _allCards)
        {
            if (GodotObject.IsInstanceValid(card))
            {
                card.QueueFree();
            }
        }

        _allCards.Clear();
        _localHand.Clear();
        _playedCards.Clear();
    }

    private void OnDealPressed()
    {
        if (_networkMode && _network.IsConnected)
        {
            _statusLabel.Text = "已向服务器请求重新开始；须等本局结算后才能押注";
            _ = _network.RestartAsync();
            return;
        }

        DealOffline();
    }

    private async void OnConnectionPressed()
    {
        if (_network.IsConnected)
        {
            _connectButton.Disabled = true;
            _statusLabel.Text = "正在断开服务器连接…";
            await _network.DisconnectAsync();
            _connectButton.Disabled = false;
            _connectButton.Text = "连接服务器";
            if (_networkMode)
            {
                DealOffline();
            }

            return;
        }

        _connectButton.Disabled = true;
        _statusLabel.Text = $"正在连接 {ColyseusClientAdapter.DefaultEndpoint}…";
        var connected = await _network.ConnectAsync(
            ColyseusClientAdapter.DefaultEndpoint,
            ColyseusClientAdapter.DefaultRoomName,
            "玩家 1",
            DefaultAnte);
        _connectButton.Disabled = false;

        if (connected)
        {
            _networkMode = true;
            _connectButton.Text = "断开连接";
            _statusLabel.Text = "已连接服务器 · 等待四名玩家加入";
        }
        else
        {
            _networkMode = false;
            _connectButton.Text = "连接服务器";
            _statusLabel.Text = "服务器不可用，继续离线演示";
        }
    }

    private void OnNetworkHandReceived(IReadOnlyList<string> cards)
    {
        _networkMode = true;
        _pendingNetworkCards.Clear();
        if (cards == null || cards.Count == 0)
        {
            // A newly joined seat receives an empty hand while the room is
            // still waiting for four players.  Remove the initial offline
            // preview so it can never be mistaken for authoritative cards.
            ClearCards();
            foreach (var seat in _seatCards)
            {
                seat.Clear();
            }
            _selectedCard = null;
            _localSeatLabel.Text = "你\n等待四名玩家加入";
            _statusLabel.Text = "已连接服务器 · 等待四名玩家加入";
            return;
        }

        ClearCards();
        foreach (var seat in _seatCards)
        {
            seat.Clear();
        }

        _localScore = 0;
        _selectedCard = null;
        var cardCount = Math.Min(cards.Count, CardsPerPlayer);
        for (var index = 0; index < cardCount; index++)
        {
            var card = CreateCard(cards[index], true, CardView.DefaultCardSize, 100 + index, LocalSeat);
            card.Position = _deckOrigin;
            _localHand.Add(card);
            AnimateCard(card, GetLocalCardPosition(index, cardCount), Vector2.One, 0.0f,
                index * DealStep);
        }

        // Opponent cards stay private.  Their backs provide a useful hand-size
        // cue while the public schema supplies names and counts.
        var hiddenDeck = BuildDeck();
        for (var seat = 1; seat < PlayerCount; seat++)
        {
            for (var index = 0; index < CardsPerPlayer; index++)
            {
                var card = CreateCard(hiddenDeck[(seat * CardsPerPlayer + index) % hiddenDeck.Count],
                    false, OpponentCardSize, 10 + index, seat);
                card.Position = _deckOrigin;
                AnimateCard(card, GetSeatCardPosition(seat, index), Vector2.One, 0.0f,
                    index * DealStep + seat * 0.018);
            }
        }

        _localSeatLabel.Text = $"你 · 玩家 1\n{cardCount} 张";
        _statusLabel.Text = $"服务器牌局 · 收到 {cardCount} 张私有手牌";
        if (_network.State != null)
        {
            UpdateNetworkState(_network.State);
        }
    }

    private void OnNetworkCardPlayed(string playerId, string cardId)
    {
        if (!_networkMode || string.IsNullOrEmpty(cardId))
        {
            return;
        }

        // The server starts a new trick after four cards.  Keep the last trick
        // visible until the next play, then recycle its views with a short
        // fade/scale tween-free removal (the play itself is always tweened).
        if (_playedCards.Count >= PlayerCount)
        {
            ClearPlayedCards();
        }

        var seat = FindNetworkSeat(playerId);
        CardView card = null;
        if (seat == LocalSeat)
        {
            for (var index = 0; index < _localHand.Count; index++)
            {
                if (string.Equals(_localHand[index].CardId, cardId, StringComparison.Ordinal))
                {
                    card = _localHand[index];
                    _localHand.RemoveAt(index);
                    _seatCards[LocalSeat].Remove(card);
                    if (_selectedCard == card)
                    {
                        _selectedCard = null;
                    }
                    break;
                }
            }

            _pendingNetworkCards.Remove(cardId);
        }
        else if (seat >= 0 && seat < _seatCards.Length && _seatCards[seat].Count > 0)
        {
            card = _seatCards[seat][0];
            _seatCards[seat].RemoveAt(0);
            card.SetCardSize(new Vector2(74.0f, 104.0f));
            card.SetCardId(cardId, true);
        }

        // A reconnect can deliver a public play before the hand-back packet.
        // Create a standalone face-up card rather than dropping the event.
        if (card == null)
        {
            card = CreateCard(cardId, true, new Vector2(74.0f, 104.0f), 220 + _playedCards.Count, -1);
            card.Position = _deckOrigin;
        }

        card.Disabled = false;
        card.MouseFilter = MouseFilterEnum.Ignore;
        card.ZIndex = 220 + _playedCards.Count;
        _playedCards.Add(card);
        AnimateCard(card, GetTrickCardPosition(_playedCards.Count - 1), new Vector2(0.82f, 0.82f), 0.0f, 0.0);
        AnimateLocalHand();

        if (seat == LocalSeat)
        {
            _localSeatLabel.Text = $"你 · 玩家 1\n{_localHand.Count} 张";
        }
    }

    private void OnNetworkStateChanged(HeartsAlter.Protocol.MyRoomState state, bool isFirstState)
    {
        _networkMode = true;
        if (state != null)
        {
            UpdateNetworkState(state);
        }
    }

    private void UpdateNetworkState(HeartsAlter.Protocol.MyRoomState state)
    {
        _potLabel.Text = $"奖池 {state.pot}  ·  底注 {state.ante}";
        var localPlayer = FindNetworkPlayerById(state, _network.SessionId);
        if (localPlayer != null)
        {
            _scoreLabel.Text = $"你的点数 {localPlayer.score}  ·  筹码 {localPlayer.chips}";
            _localSeatLabel.Text = $"{localPlayer.name}\n{localPlayer.handCount} 张 · {localPlayer.score} 点";
        }

        // Physical UI slots are relative to this client, while the server's
        // seat numbers remain authoritative for turn order/tie breaks.  Sort
        // the other players by their server seat and place them consistently
        // around the table even when we joined after somebody else.
        var opponents = new List<HeartsAlter.Protocol.Player>();
        if (state.players != null)
        {
            state.players.ForEach((string playerId, HeartsAlter.Protocol.Player player) =>
            {
                if (player != null && playerId != _network.SessionId)
                {
                    opponents.Add(player);
                }
            });
        }

        opponents.Sort((left, right) => left.seat.CompareTo(right.seat));
        UpdateSeatLabel(_opponentTopLabel,
            opponents.Count > 0 ? opponents[0] : null, "玩家 2");
        UpdateSeatLabel(_opponentLeftLabel,
            opponents.Count > 1 ? opponents[1] : null, "玩家 3");
        UpdateSeatLabel(_opponentRightLabel,
            opponents.Count > 2 ? opponents[2] : null, "玩家 4");

        var message = state.message ?? string.Empty;
        if (!string.IsNullOrEmpty(state.currentTurn))
        {
            message += state.currentTurn == _network.SessionId ? " · 轮到你" : " · 等待其他玩家出牌";
        }

        if (!string.IsNullOrEmpty(message))
        {
            _statusLabel.Text = message;
        }
    }

    private void UpdateSeatLabel(Label label, HeartsAlter.Protocol.Player player, string fallback)
    {
        if (player == null)
        {
            label.Text = fallback;
            return;
        }

        var treating = player.isTreating ? " · 请客" : string.Empty;
        label.Text = $"{player.name}{treating}\n{player.handCount} 张 · {player.score} 点";
    }

    private void OnNetworkMessage(string message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            _statusLabel.Text = message;
        }
    }

    private void OnNetworkInvalidPlay(string message)
    {
        foreach (var pendingCardId in _pendingNetworkCards)
        {
            foreach (var card in _localHand)
            {
                if (card.CardId == pendingCardId)
                {
                    card.Disabled = false;
                }
            }
        }

        _pendingNetworkCards.Clear();
        _statusLabel.Text = string.IsNullOrEmpty(message) ? "出牌不合法" : message;
    }

    private void OnNetworkError(int code, string message)
    {
        _statusLabel.Text = string.IsNullOrEmpty(message)
            ? $"服务器错误 ({code})"
            : $"服务器错误：{message}";
    }

    private void OnNetworkLeft(int code)
    {
        _networkMode = false;
        _connectButton.Text = "连接服务器";
        _statusLabel.Text = $"服务器连接已断开 ({code})，已切换离线演示";
        DealOffline();
    }

    private CardView CreateCard(string cardId, bool faceUp, Vector2 size, int zIndex, int seat)
    {
        var card = new CardView();
        card.SetCardSize(size);
        card.SetCardId(cardId, faceUp);
        card.ZIndex = zIndex;
        card.Clicked += OnCardClicked;
        AddChild(card);
        _allCards.Add(card);
        if (seat >= 0 && seat < _seatCards.Length)
        {
            _seatCards[seat].Add(card);
        }

        return card;
    }

    private int FindNetworkSeat(string playerId)
    {
        if (string.Equals(playerId, _network.SessionId, StringComparison.Ordinal))
        {
            return LocalSeat;
        }

        var state = _network.State;
        if (state?.players == null)
        {
            return -1;
        }

        // Convert authoritative server seats to the visual slots used by the
        // client.  The local hand always occupies slot 0; opponents are
        // sorted by server seat into slots 1..3.  Returning raw server seat
        // numbers here would collide with slot 0 when this client joined
        // after the first player.
        var opponents = new List<(string Id, int Seat)>();
        state.players.ForEach((string id, HeartsAlter.Protocol.Player player) =>
        {
            if (player != null && id != _network.SessionId)
            {
                opponents.Add((id, player.seat));
            }
        });

        opponents.Sort((left, right) => left.Seat.CompareTo(right.Seat));
        for (var index = 0; index < opponents.Count; index++)
        {
            if (opponents[index].Id == playerId)
            {
                return index + 1;
            }
        }

        return -1;
    }

    private static HeartsAlter.Protocol.Player FindNetworkPlayerById(
        HeartsAlter.Protocol.MyRoomState state, string playerId)
    {
        if (state?.players == null || string.IsNullOrEmpty(playerId))
        {
            return null;
        }

        HeartsAlter.Protocol.Player result = null;
        state.players.ForEach((string id, HeartsAlter.Protocol.Player player) =>
        {
            if (result == null && id == playerId)
            {
                result = player;
            }
        });
        return result;
    }

    private void ClearPlayedCards()
    {
        foreach (var card in _playedCards)
        {
            _allCards.Remove(card);
            if (GodotObject.IsInstanceValid(card))
            {
                card.QueueFree();
            }
        }

        _playedCards.Clear();
    }

    private void OnSortPressed()
    {
        if (_selectedCard != null)
        {
            SetSelected(_selectedCard, false);
            _selectedCard = null;
        }

        _localHand.Sort(CompareCards);
        for (var index = 0; index < _localHand.Count; index++)
        {
            var card = _localHand[index];
            card.ZIndex = 100 + index;
            AnimateCard(card, GetLocalCardPosition(index, _localHand.Count), Vector2.One, 0.0f,
                index * 0.012);
        }

        _statusLabel.Text = "手牌已按花色、点数整理";
    }

    private void OnCardClicked(CardView card)
    {
        // Cards in the central well and the three opponents' hands are not
        // actionable in the offline preview.
        if (!_localHand.Contains(card))
        {
            return;
        }

        if (_selectedCard != card)
        {
            if (_selectedCard != null)
            {
                SetSelected(_selectedCard, false);
            }

            _selectedCard = card;
            SetSelected(card, true);
            _statusLabel.Text = $"已选中 {card.CardId} · 再次点击出牌";
            return;
        }

        if (_networkMode && _network.IsConnected)
        {
            if (_pendingNetworkCards.Contains(card.CardId))
            {
                return;
            }

            _pendingNetworkCards.Add(card.CardId);
            card.Disabled = true;
            _statusLabel.Text = $"正在向服务器提交 {card.CardId}…";
            _ = _network.PlayCardAsync(card.CardId);
            return;
        }

        PlayLocalCard(card);
    }

    private void SetSelected(CardView card, bool selected)
    {
        var index = _localHand.IndexOf(card);
        if (index < 0)
        {
            return;
        }

        var target = GetLocalCardPosition(index, _localHand.Count);
        if (selected)
        {
            target.Y -= 20.0f;
        }

        AnimateCard(card, target, selected ? new Vector2(1.08f, 1.08f) : Vector2.One,
            0.0f, 0.0);
    }

    private void PlayLocalCard(CardView card)
    {
        var index = _localHand.IndexOf(card);
        if (index < 0)
        {
            return;
        }

        _localHand.RemoveAt(index);
        _selectedCard = null;
        card.MouseFilter = MouseFilterEnum.Ignore;
        card.ZIndex = 220 + _playedCards.Count;
        _playedCards.Add(card);

        var trickTarget = GetTrickCardPosition(_playedCards.Count - 1);
        AnimateCard(card, trickTarget, new Vector2(0.82f, 0.82f), 0.0f, 0.0);
        AnimateLocalHand();

        _localScore += CardPoints(card.CardId);
        _scoreLabel.Text = $"你的点数 {_localScore}";
        _localSeatLabel.Text = $"你 · 玩家 1\n{_localHand.Count} 张";

        if (_playedCards.Count == PlayerCount)
        {
            _statusLabel.Text = $"离线预览：本墩完成 · 你的累计点数 {_localScore}";
        }
        else
        {
            _statusLabel.Text = $"已出 {card.CardId} · 等待本墩其余玩家（离线预览）";
        }
    }

    private void AnimateLocalHand()
    {
        for (var index = 0; index < _localHand.Count; index++)
        {
            var card = _localHand[index];
            card.ZIndex = 100 + index;
            AnimateCard(card, GetLocalCardPosition(index, _localHand.Count), Vector2.One, 0.0f,
                index * 0.012);
        }
    }

    private void AnimateCard(CardView card, Vector2 target, Vector2 targetScale, float targetRotation, double delay)
    {
        var tween = CreateTween();
        tween.SetTrans(Tween.TransitionType.Sine);
        tween.SetEase(Tween.EaseType.InOut);
        var position = tween.TweenProperty(card, "position", target, DealDuration);
        if (delay > 0.0)
        {
            position.SetDelay(delay);
        }

        tween.Parallel().TweenProperty(card, "scale", targetScale, DealDuration);
        tween.Parallel().TweenProperty(card, "rotation", targetRotation, DealDuration);
    }

    private Vector2 GetSeatCardPosition(int seat, int index)
    {
        switch (seat)
        {
            case 0:
                return GetLocalCardPosition(index, CardsPerPlayer);
            case 1:
            {
                var spacing = 18.0f;
                var totalWidth = OpponentCardSize.X + spacing * (CardsPerPlayer - 1);
                return new Vector2((_layoutSize.X - totalWidth) * 0.5f + index * spacing, 102.0f);
            }
            case 2:
                return new Vector2(66.0f, 260.0f + index * 5.0f);
            default:
                return new Vector2(_layoutSize.X - OpponentCardSize.X - 66.0f, 260.0f + index * 5.0f);
        }
    }

    private Vector2 GetLocalCardPosition(int index, int count)
    {
        var spacing = 66.0f;
        if (count > 1)
        {
            var available = _layoutSize.X - 64.0f;
            spacing = Math.Min(spacing, (available - CardView.DefaultCardSize.X) / (count - 1));
        }

        var totalWidth = CardView.DefaultCardSize.X + spacing * Math.Max(0, count - 1);
        var x = (_layoutSize.X - totalWidth) * 0.5f + index * spacing;
        return new Vector2(x, _layoutSize.Y - CardView.DefaultCardSize.Y - 18.0f);
    }

    private Vector2 GetTrickCardPosition(int index)
    {
        var center = new Vector2(_layoutSize.X * 0.5f - CardView.DefaultCardSize.X * 0.5f,
            74.0f + 142.0f);
        var offsets = new[]
        {
            new Vector2(-40, -18),
            new Vector2(40, -18),
            new Vector2(-40, 22),
            new Vector2(40, 22)
        };
        return center + offsets[Math.Clamp(index, 0, offsets.Length - 1)];
    }

    private static readonly Vector2 OpponentCardSize = new(52.0f, 73.0f);

    private static int CardPoints(string cardId)
    {
        if (cardId.StartsWith("Heart", StringComparison.Ordinal))
        {
            return 1;
        }

        return string.Equals(cardId, "SpadeQ", StringComparison.Ordinal) ? 6 : 0;
    }

    private static int CompareCards(CardView left, CardView right)
    {
        var suit = GetSuitOrder(left.CardId).CompareTo(GetSuitOrder(right.CardId));
        return suit != 0 ? suit : GetRankOrder(left.CardId).CompareTo(GetRankOrder(right.CardId));
    }

    private static int GetSuitOrder(string cardId)
    {
        for (var index = 0; index < Suits.Length; index++)
        {
            if (cardId.StartsWith(Suits[index], StringComparison.Ordinal))
            {
                return index;
            }
        }

        return Suits.Length;
    }

    private static int GetRankOrder(string cardId)
    {
        for (var index = 0; index < Ranks.Length; index++)
        {
            if (cardId.EndsWith(Ranks[index], StringComparison.Ordinal))
            {
                return index;
            }
        }

        return Ranks.Length;
    }

    private static List<string> BuildDeck()
    {
        var deck = new List<string>(PlayerCount * CardsPerPlayer);
        foreach (var suit in Suits)
        {
            foreach (var rank in Ranks)
            {
                deck.Add(suit + rank);
            }
        }

        return deck;
    }

    private static void Shuffle(List<string> deck)
    {
        var random = new Random();
        for (var index = deck.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (deck[index], deck[swapIndex]) = (deck[swapIndex], deck[index]);
        }
    }

    private Label AddSeatLabel(string text, Vector2 position)
    {
        var label = AddLabel(text, position, new Vector2(200, 48), 14,
            new Color("d9f2e6"), HorizontalAlignment.Center);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.45f));
        label.AddThemeConstantOverride("shadow_offset_x", 1);
        label.AddThemeConstantOverride("shadow_offset_y", 1);
        return label;
    }

    private Label AddLabel(string text, Vector2 position, Vector2 size, int fontSize, Color color,
        HorizontalAlignment alignment)
    {
        var label = new Label
        {
            Text = text,
            Position = position,
            Size = size,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        AddChild(label);
        return label;
    }

    private Button AddButton(string text, Vector2 position, Vector2 size)
    {
        var button = new Button
        {
            Text = text,
            Position = position,
            Size = size,
            FocusMode = FocusModeEnum.None
        };
        button.AddThemeFontSizeOverride("font_size", 13);
        AddChild(button);
        return button;
    }

    private static StyleBoxFlat MakePanelStyle(Color background, Color border, int borderWidth, int radius)
    {
        var style = new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            ShadowColor = new Color(0, 0, 0, 0.22f),
            ShadowSize = 6
        };
        return style;
    }
}
