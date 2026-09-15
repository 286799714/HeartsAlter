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
	public partial class Player : Schema {
#if UNITY_5_3_OR_NEWER
[Preserve]
#endif
public Player() { }
		[Type(0, "string")]
		public string name = default(string);

		[Type(1, "uint8")]
		public byte seat = default(byte);

		[Type(2, "int32")]
		public int chips = default(int);

		[Type(3, "int32")]
		public int stake = default(int);

		[Type(4, "uint16")]
		public ushort score = default(ushort);

		[Type(5, "uint8")]
		public byte handCount = default(byte);

		[Type(6, "uint8")]
		public byte tricksWon = default(byte);

		[Type(7, "int32")]
		public int payout = default(int);

		[Type(8, "boolean")]
		public bool connected = default(bool);

		[Type(9, "boolean")]
		public bool isTreating = default(bool);

		[Type(10, "boolean")]
		public bool ready = default(bool);

		[Type(11, "boolean")]
		public bool isBot = default(bool);

		[Type(12, "boolean")]
		public bool isHost = default(bool);

		[Type(13, "boolean")]
		public bool tableReady = default(bool);

		[Type(14, "boolean")]
		public bool dealReady = default(bool);

		[Type(15, "boolean")]
		public bool nextRoundReady = default(bool);

		[Type(16, "string")]
		public string profileId = default(string);

		[Type(17, "uint8")]
		public byte avatarId = default(byte);
	}
}
