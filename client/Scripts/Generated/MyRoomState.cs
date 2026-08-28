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
		public ushort turnCount = default(ushort);

		[Type(6, "uint8")]
		public byte trickNumber = default(byte);

		[Type(7, "string")]
		public string leadSuit = default(string);

		[Type(8, "boolean")]
		public bool heartsBroken = default(bool);

		[Type(9, "array", typeof(ArraySchema<TrickCard>))]
		public ArraySchema<TrickCard> trick = null;

		[Type(10, "array", typeof(ArraySchema<TrickCard>))]
		public ArraySchema<TrickCard> lastTrick = null;

		[Type(11, "string")]
		public string lastTrickWinner = default(string);

		[Type(12, "uint8")]
		public byte lastTrickPoints = default(byte);

		[Type(13, "int32")]
		public int pot = default(int);

		[Type(14, "int32")]
		public int ante = default(int);

		[Type(15, "uint16")]
		public ushort roundNumber = default(ushort);

		[Type(16, "string")]
		public string message = default(string);
	}
}
