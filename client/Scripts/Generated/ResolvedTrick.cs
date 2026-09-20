//
// THIS FILE HAS BEEN GENERATED AUTOMATICALLY
// DO NOT CHANGE MANUALLY UNLESS YOU KNOW WHAT YOU'RE DOING
//
// GENERATED USING @colyseus/schema 5.0.19
//

using Colyseus.Schema;
#if UNITY_5_3_OR_NEWER
using UnityEngine.Scripting;
#endif

namespace HeartsAlter.Scripts.Generated {
	public partial class ResolvedTrick : Schema {
#if UNITY_5_3_OR_NEWER
[Preserve]
#endif
public ResolvedTrick() { }
		[Type(0, "string")]
		public string winnerId = default(string);

		[Type(1, "uint8")]
		public byte points = default(byte);

		[Type(2, "uint8")]
		public byte playSequence = default(byte);
	}
}
