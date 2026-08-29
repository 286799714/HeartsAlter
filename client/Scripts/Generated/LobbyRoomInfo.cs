// Generated from server/src/rooms/schema/MyRoomState.ts.
using Colyseus.Schema;

namespace HeartsAlter.Scripts.Generated {
	public partial class LobbyRoomInfo : Schema {
		[Type(0, "string")]
		public string roomId = default(string);
		[Type(1, "string")]
		public string name = default(string);
		[Type(2, "string")]
		public string phase = default(string);
		[Type(3, "uint8")]
		public byte playerCount = default(byte);
		[Type(4, "uint8")]
		public byte maxPlayers = default(byte);
		[Type(5, "uint8")]
		public byte readyCount = default(byte);
		[Type(6, "boolean")]
		public bool bots = default(bool);
		[Type(7, "string")]
		public string hostName = default(string);
	}
}
