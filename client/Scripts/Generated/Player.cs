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

namespace HeartsAlter.Protocol {
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
	}
}
