using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Colyseus;
using HeartsAlter.Scripts.Generated;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts;

/// <summary>
/// Thin network boundary for the Godot scene.
///
/// The adapter deliberately contains no presentation code.  It translates the
/// room's typed state and small message payloads into events that Main can
/// consume, while keeping the Colyseus SDK out of the view classes.  The room
/// server is authoritative: a card is removed from the view only after the
/// corresponding <c>card_played</c> message arrives.
/// </summary>
public sealed class ColyseusClientAdapter
{
    public const string DefaultEndpoint = "ws://127.0.0.1:2567";
    public const string DefaultRoomName = "hearts";
    public const bool DefaultDemoBots = true;

    private Client _client;
    private Room<MyRoomState> _room;

    public event Action<MyRoomState, bool> StateChanged;
    /// <summary>
    /// Private hand plus the authoritative round number that produced it.
    /// A negative round means a legacy server omitted the field.
    /// </summary>
    public event Action<IReadOnlyList<string>, int> HandReceived;
    /// <summary>
    /// Public play notification plus its authoritative round number.
    /// A negative round means a legacy server omitted the field.
    /// </summary>
    public event Action<string, string, int> CardPlayed;
    public event Action<string, string, int, int> CardPlayedDetailed;
    public event Action<string, int, int> TurnStarted;
    public event Action<string, int, int> TrickResolved;
    public event Action RoundFinished;
    public event Action<string> RoomReset;
    public event Action<string> ServerMessage;
    public event Action<string> InvalidPlay;
    public event Action<int, string> Error;
    public event Action<int> Left;

    public bool IsConnected => _room != null;
    public string SessionId => _room?.SessionId ?? string.Empty;
    public MyRoomState State => _room?.State;

    /// <summary>
    /// Joins or creates the authoritative four-player room.  The Godot demo
    /// enables three server-side bot seats by default, so one client can start
    /// a complete round; pass <c>false</c> for the regular four-human room.
    /// The hand handler is registered before requesting the private hand again;
    /// this covers the server's immediate onJoin hand message and avoids a race
    /// on reconnect.
    /// </summary>
    public async Task<bool> ConnectAsync(
        string endpoint = DefaultEndpoint,
        string roomName = DefaultRoomName,
        string playerName = "玩家 1",
        int ante = 100,
        bool bots = DefaultDemoBots)
    {
        if (IsConnected)
        {
            return true;
        }

        try
        {
            _client = new Client(endpoint);
            var options = new Dictionary<string, object>
            {
                ["name"] = string.IsNullOrWhiteSpace(playerName) ? "玩家 1" : playerName,
                ["ante"] = ante,
                ["bots"] = bots
            };

            _room = await _client.JoinOrCreate<MyRoomState>(roomName, options);
            RegisterRoomHandlers(_room);

            // Joining resolves before the first schema patch in the C# SDK.
            // Await it before reading collections such as players/trick.
            await _room.WaitForFirstState();

            // onJoin/startRound sends a hand immediately.  Requesting once
            // after handlers are installed guarantees a hand even if that
            // first packet raced the registration above.
            await _room.Send("request_hand");
            ServerMessage?.Invoke("已连接服务器，等待牌局状态…");
            return true;
        }
        catch (Exception exception)
        {
            Error?.Invoke(0, exception.Message);
            await DisconnectSilentlyAsync();
            return false;
        }
    }

    /// <summary>
    /// Consumes the seat reservation issued by the lobby. This keeps room
    /// creation/joining atomic on the server and avoids a second matchmaking
    /// race between the lobby and ready-room scenes.
    /// </summary>
    public async Task<bool> ConnectByReservationAsync(
        RoomReservation reservation,
        string endpoint = DefaultEndpoint)
    {
        if (reservation == null || string.IsNullOrWhiteSpace(reservation.RoomId))
        {
            Error?.Invoke(0, "房间预约无效");
            return false;
        }
        if (IsConnected) return true;
        try
        {
            _client = new Client(endpoint);
            _room = await _client.ConsumeSeatReservation<MyRoomState>(
                reservation.ToSeatReservation(), new Dictionary<string, string>());
            RegisterRoomHandlers(_room);
            await _room.WaitForFirstState();
            await _room.Send("request_hand");
            ServerMessage?.Invoke("已进入准备房间");
            return true;
        }
        catch (Exception exception)
        {
            Error?.Invoke(0, exception.Message);
            await DisconnectSilentlyAsync();
            return false;
        }
    }

    public Task SetReadyAsync(bool ready)
    {
        return _room == null ? Task.CompletedTask : _room.Send("ready", new Dictionary<string, object>
        {
            ["ready"] = ready,
        });
    }

    public Task AddBotAsync()
    {
        return _room == null ? Task.CompletedTask : _room.Send("add_bot");
    }

    public Task StartGameAsync()
    {
        return _room == null ? Task.CompletedTask : _room.Send("start_game");
    }

    public Task TableReadyAsync()
    {
        return _room == null ? Task.CompletedTask : _room.Send("table_ready");
    }

    public Task DealReadyAsync()
    {
        return _room == null ? Task.CompletedTask : _room.Send("deal_ready");
    }

    public Task NextRoundAsync()
    {
        return _room == null ? Task.CompletedTask : _room.Send("next_round");
    }

    public Task RequestHandAsync()
    {
        return _room == null ? Task.CompletedTask : _room.Send("request_hand");
    }

    public Task PlayCardAsync(string cardId)
    {
        if (_room == null || string.IsNullOrWhiteSpace(cardId))
        {
            return Task.CompletedTask;
        }

        return _room.Send("play", new Dictionary<string, object>
        {
            ["cardId"] = cardId
        });
    }

    public Task PlayCardAsync(int cardIndex, CardData card)
    {
        if (_room == null) return Task.CompletedTask;
        return _room.Send("play", new Dictionary<string, object>
        {
            ["cardIndex"] = cardIndex,
            ["cardId"] = ToCardId(card),
            ["suit"] = ToSuitName(card.Suit),
            ["rank"] = (int)card.Rank,
        });
    }

    public Task RestartAsync()
    {
        return _room == null ? Task.CompletedTask : _room.Send("restart");
    }

    public async Task DisconnectAsync()
    {
        if (_room == null)
        {
            return;
        }

        var room = _room;
        _room = null;
        try
        {
            await room.Leave(true);
        }
        catch (Exception exception)
        {
            Error?.Invoke(0, exception.Message);
        }
    }

    private async Task DisconnectSilentlyAsync()
    {
        var room = _room;
        _room = null;
        if (room == null)
        {
            return;
        }

        try
        {
            await room.Leave(true);
        }
        catch
        {
            // Connection setup already failed; preserve the original error.
        }
    }

    private void RegisterRoomHandlers(Room<MyRoomState> room)
    {
        room.OnStateChange += OnStateChange;
        room.OnError += OnRoomError;
        room.OnLeave += OnRoomLeave;
        room.OnReconnect += OnRoomReconnect;

        room.OnMessage<Dictionary<string, object>>("hand", OnHandMessage);
        room.OnMessage<Dictionary<string, object>>("card_played", OnCardPlayedMessage);
        // Register every server message type, including informational events
        // that do not carry a dedicated client-side event.  The Colyseus SDK
        // logs a warning for unregistered messages; keeping these on the same
        // adapter boundary makes a live match quiet and forwards any optional
        // `message` field to the status label.
        room.OnMessage<Dictionary<string, object>>("player_joined", OnInformationalMessage);
        room.OnMessage<Dictionary<string, object>>("player_left", OnInformationalMessage);
        room.OnMessage<Dictionary<string, object>>("player_disconnected", OnInformationalMessage);
        room.OnMessage<Dictionary<string, object>>("turn_started", OnTurnStartedMessage);
        room.OnMessage<Dictionary<string, object>>("round_started", OnInformationalMessage);
        room.OnMessage<Dictionary<string, object>>("round_finished", OnRoundFinishedMessage);
        room.OnMessage<Dictionary<string, object>>("trick_resolved", OnTrickResolvedMessage);
        room.OnMessage<Dictionary<string, object>>("room_reset", OnRoomResetMessage);
        room.OnMessage<Dictionary<string, object>>("deal_started", OnInformationalMessage);
        room.OnMessage<Dictionary<string, object>>("game_ready", OnInformationalMessage);
        room.OnMessage<Dictionary<string, object>>("invalid_play", OnInvalidPlayMessage);
        room.OnMessage<Dictionary<string, object>>("auto_play", OnInformationalMessage);
    }

    private void OnStateChange(MyRoomState state, bool isFirstState)
    {
        StateChanged?.Invoke(state, isFirstState);
    }

    private void OnHandMessage(Dictionary<string, object> payload)
    {
        var cards = new List<string>();
        if (payload != null && payload.TryGetValue("cards", out var rawCards) && rawCards is IEnumerable enumerable)
        {
            foreach (var rawCard in enumerable)
            {
                if (rawCard is string cardId && !string.IsNullOrWhiteSpace(cardId))
                {
                    cards.Add(cardId);
                }
            }
        }

        var roundNumber = ReadInt(payload, "roundNumber", -1);
        HandReceived?.Invoke(cards, roundNumber);
        ServerMessage?.Invoke($"收到私有手牌：{cards.Count} 张");
    }

    private void OnCardPlayedMessage(Dictionary<string, object> payload)
    {
        var playerId = ReadString(payload, "playerId");
        var cardId = ReadString(payload, "cardId");
        if (!string.IsNullOrEmpty(cardId))
        {
            var roundNumber = ReadInt(payload, "roundNumber", -1);
            CardPlayed?.Invoke(playerId, cardId, roundNumber);
            CardPlayedDetailed?.Invoke(playerId, cardId, ReadInt(payload, "cardIndex", -1), roundNumber);
        }
    }

    private void OnTurnStartedMessage(Dictionary<string, object> payload)
    {
        string playerId = ReadString(payload, "playerId");
        TurnStarted?.Invoke(
            playerId,
            ReadInt(payload, "duration", 15_000),
            ReadInt(payload, "trickNumber", 0));
    }

    private void OnTrickResolvedMessage(Dictionary<string, object> payload)
    {
        TrickResolved?.Invoke(
            ReadString(payload, "winnerId"),
            ReadInt(payload, "points", 0),
            ReadInt(payload, "trickNumber", 0));
    }

    private void OnRoundFinishedMessage(Dictionary<string, object> payload)
    {
        RoundFinished?.Invoke();
        OnInformationalMessage(payload);
    }

    private void OnRoomResetMessage(Dictionary<string, object> payload)
    {
        string reason = ReadString(payload, "reason");
        RoomReset?.Invoke(reason);
        if (!string.IsNullOrEmpty(reason)) ServerMessage?.Invoke(reason);
    }

    private void OnInvalidPlayMessage(Dictionary<string, object> payload)
    {
        var reason = ReadString(payload, "reason");
        var message = string.IsNullOrEmpty(reason) ? "出牌不合法" : reason;
        InvalidPlay?.Invoke(message);
        ServerMessage?.Invoke(message);
    }

    private void OnInformationalMessage(Dictionary<string, object> payload)
    {
        var message = ReadString(payload, "message");
        if (!string.IsNullOrEmpty(message))
        {
            ServerMessage?.Invoke(message);
        }
    }

    private void OnRoomError(int code, string message)
    {
        Error?.Invoke(code, message ?? string.Empty);
    }

    private void OnRoomLeave(int code)
    {
        _room = null;
        Left?.Invoke(code);
    }

    private void OnRoomReconnect()
    {
        // The room currently sends the hand from onReconnect; request it once
        // more so a reconnect that races a state patch still restores the
        // private hand deterministically.
        ServerMessage?.Invoke("服务器连接已恢复，正在同步手牌…");
        _ = RequestHandAsync();
    }

    private static string ReadString(Dictionary<string, object> payload, string key)
    {
        if (payload != null && payload.TryGetValue(key, out var value) && value is string text)
        {
            return text;
        }

        return string.Empty;
    }

    private static string ToCardId(CardData card)
    {
        string rank = card.Rank switch
        {
            PokerRank.Jack => "J",
            PokerRank.Queen => "Q",
            PokerRank.King => "K",
            PokerRank.Ace => "A",
            _ => ((int)card.Rank).ToString(CultureInfo.InvariantCulture),
        };
        return ToSuitName(card.Suit) switch
        {
            "club" => "Club" + rank,
            "diamond" => "Diamond" + rank,
            "heart" => "Heart" + rank,
            _ => "Spade" + rank,
        };
    }

    private static string ToSuitName(PokerSuit suit) => suit switch
    {
        PokerSuit.Club => "club",
        PokerSuit.Diamond => "diamond",
        PokerSuit.Heart => "heart",
        _ => "spade",
    };

    /// <summary>
    /// Colyseus payload numbers can be materialized as any primitive numeric
    /// CLR type depending on the JSON decoder and platform.  Normalize them at
    /// the network boundary so presentation code never has to cast blindly.
    /// </summary>
    private static int ReadInt(Dictionary<string, object> payload, string key, int fallback)
    {
        if (payload == null || !payload.TryGetValue(key, out var value) || value == null)
        {
            return fallback;
        }

        long integer;
        switch (value)
        {
            case byte number:
                integer = number;
                break;
            case sbyte number:
                integer = number;
                break;
            case short number:
                integer = number;
                break;
            case ushort number:
                integer = number;
                break;
            case int number:
                integer = number;
                break;
            case uint number when number <= int.MaxValue:
                integer = number;
                break;
            case long number:
                integer = number;
                break;
            case ulong number when number <= int.MaxValue:
                integer = (long)number;
                break;
            case float number when !float.IsNaN(number) && !float.IsInfinity(number):
                return number >= int.MinValue && number <= int.MaxValue
                    ? (int)Math.Truncate(number)
                    : fallback;
            case double number when !double.IsNaN(number) && !double.IsInfinity(number):
                return number >= int.MinValue && number <= int.MaxValue
                    ? (int)Math.Truncate(number)
                    : fallback;
            case decimal number:
                return number >= int.MinValue && number <= int.MaxValue
                    ? decimal.ToInt32(decimal.Truncate(number))
                    : fallback;
            case string text when int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed):
                return parsed;
            default:
                return fallback;
        }

        return integer >= int.MinValue && integer <= int.MaxValue
            ? (int)integer
            : fallback;
    }
}
