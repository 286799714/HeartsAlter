//
// THIS FILE HAS BEEN GENERATED AUTOMATICALLY
// DO NOT CHANGE IT MANUALLY UNLESS YOU KNOW WHAT YOU'RE DOING
//
// GENERATED USING @colyseus/schema 5.0.19
//

using Colyseus.Schema;
#if UNITY_5_3_OR_NEWER
using UnityEngine.Scripting;
#endif

namespace HeartsAlter.Scripts.Generated {
	public partial class MyRoomState : Schema {
#if UNITY_5_3_OR_NEWER
[Preserve]
#endif
public MyRoomState() { }
		[Type(0, "string")]
		public string phase = default(string);

		[Type(1, "map", typeof(MapSchema<Player>))]
		public MapSchema<Player> players = null;

		[Type(2, "array", typeof(ArraySchema<string>), "string")]
		public ArraySchema<string> playerOrder = null;

		[Type(3, "string")]
		public string currentTurn = default(string);

		[Type(4, "float64")]
		public double turnDeadline = default(double);

		[Type(5, "uint16")]
		public ushort turnDuration = default(ushort);

		[Type(6, "uint16")]
		public ushort turnCount = default(ushort);

		[Type(7, "uint8")]
		public byte trickNumber = default(byte);

		[Type(8, "string")]
		public string leadSuit = default(string);

		[Type(9, "boolean")]
		public bool heartsBroken = default(bool);

		[Type(10, "array", typeof(ArraySchema<TrickCard>))]
		public ArraySchema<TrickCard> trick = null;

		[Type(11, "array", typeof(ArraySchema<TrickCard>))]
		public ArraySchema<TrickCard> lastTrick = null;

		[Type(12, "string")]
		public string lastTrickWinner = default(string);

		[Type(13, "uint8")]
		public byte lastTrickPoints = default(byte);

		[Type(14, "int32")]
		public int pot = default(int);

		[Type(15, "int32")]
		public int ante = default(int);

		[Type(16, "uint16")]
		public ushort roundNumber = default(ushort);

		[Type(17, "string")]
		public string message = default(string);

		[Type(18, "string")]
		public string hostId = default(string);

		[Type(19, "boolean")]
		public bool lobbyManaged = default(bool);

		[Type(20, "float64")]
		public double phaseDeadline = default(double);
	}
}
