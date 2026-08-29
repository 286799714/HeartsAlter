// Generated from server/src/rooms/schema/MyRoomState.ts.
using Colyseus.Schema;

namespace HeartsAlter.Scripts.Generated {
	public partial class LobbyState : Schema {
		[Type(0, "map", typeof(MapSchema<LobbyRoomInfo>))]
		public MapSchema<LobbyRoomInfo> rooms = null;
		[Type(1, "string")]
		public string message = default(string);
	}
}
