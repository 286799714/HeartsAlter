using Colyseus;
using HeartsAlter.Scripts.Generated;

namespace HeartsAlter.Scripts;

/// <summary>Small scene-to-scene hand-off for the reservation and game room.</summary>
public static class GameSession
{
	public static RoomReservation PendingReservation { get; set; }
	public static ColyseusLobbyAdapter LobbyAdapter { get; set; }
	public static ColyseusClientAdapter GameAdapter { get; set; }
	public static string ServerEndpoint { get; set; } = string.Empty;
	public static PlayerProfile Profile { get; set; }
	public static int? PendingTutorialLesson { get; set; }
	public static bool ShowTutorialPage { get; set; }

	public static void Clear()
	{
		PendingReservation = null;
		LobbyAdapter = null;
		GameAdapter = null;
		ServerEndpoint = string.Empty;
		Profile = null;
		PendingTutorialLesson = null;
		ShowTutorialPage = false;
	}
}

/// <summary>Wire-compatible subset of Colyseus.SeatReservation.</summary>
public sealed class RoomReservation
{
	public string Name { get; set; } = string.Empty;
	public string SessionId { get; set; } = string.Empty;
	public string RoomId { get; set; } = string.Empty;
	public string PublicAddress { get; set; } = string.Empty;
	public string ProcessId { get; set; } = string.Empty;
	public string ReconnectionToken { get; set; } = string.Empty;
	public bool DevMode { get; set; }
	public string Protocol { get; set; } = string.Empty;

	public SeatReservation ToSeatReservation()
	{
		return new SeatReservation
		{
			name = Name,
			sessionId = SessionId,
			roomId = RoomId,
			publicAddress = PublicAddress,
			processId = ProcessId,
			reconnectionToken = ReconnectionToken,
			devMode = DevMode,
			protocol = Protocol,
		};
	}
}
