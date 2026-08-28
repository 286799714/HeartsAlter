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
	public partial class TrickCard : Schema {
#if UNITY_5_3_OR_NEWER
[Preserve]
#endif
public TrickCard() { }
		[Type(0, "string")]
		public string playerId = default(string);

		[Type(1, "string")]
		public string cardId = default(string);
	}
}
