using Colyseus;
using HeartsAlter.Protocol;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HeartsAlter;

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

    private Client _client;
    private Room<MyRoomState> _room;

    public event Action<MyRoomState, bool> StateChanged;
    public event Action<IReadOnlyList<string>> HandReceived;
    public event Action<string, string> CardPlayed;
    public event Action<string> ServerMessage;
    public event Action<string> InvalidPlay;
    public event Action<int, string> Error;
    public event Action<int> Left;

    public bool IsConnected => _room != null;
    public string SessionId => _room?.SessionId ?? string.Empty;
    public MyRoomState State => _room?.State;

    /// <summary>
    /// Joins or creates the authoritative four-player room.  The hand handler
    /// is registered before requesting the private hand again; this covers the
    /// server's immediate onJoin hand message and avoids a race on reconnect.
    /// </summary>
    public async Task<bool> ConnectAsync(
        string endpoint = DefaultEndpoint,
        string roomName = DefaultRoomName,
        string playerName = "玩家 1",
        int ante = 100)
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
                ["ante"] = ante
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
        room.OnMessage<Dictionary<string, object>>("round_started", OnInformationalMessage);
        room.OnMessage<Dictionary<string, object>>("round_finished", OnInformationalMessage);
        room.OnMessage<Dictionary<string, object>>("trick_resolved", OnInformationalMessage);
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

        HandReceived?.Invoke(cards);
        ServerMessage?.Invoke($"收到私有手牌：{cards.Count} 张");
    }

    private void OnCardPlayedMessage(Dictionary<string, object> payload)
    {
        var playerId = ReadString(payload, "playerId");
        var cardId = ReadString(payload, "cardId");
        if (!string.IsNullOrEmpty(cardId))
        {
            CardPlayed?.Invoke(playerId, cardId);
        }
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
}
