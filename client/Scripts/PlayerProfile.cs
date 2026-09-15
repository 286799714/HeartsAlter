using System;
using System.Collections.Generic;
using System.Globalization;

namespace HeartsAlter.Scripts;

/// <summary>Server-owned save received privately when joining the lobby.</summary>
public sealed class PlayerProfile
{
	public string PlayerId { get; }
	public string Name { get; }
	public int AvatarId { get; }
	public int Chips { get; }

	private PlayerProfile(string playerId, string name, int avatarId, int chips)
	{
		PlayerId = playerId;
		Name = name;
		AvatarId = avatarId;
		Chips = chips;
	}

	public static PlayerProfile FromPayload(Dictionary<string, object> payload)
	{
		if (payload == null || !payload.TryGetValue("playerId", out var id) || id is not string playerId ||
			string.IsNullOrWhiteSpace(playerId) || !payload.TryGetValue("name", out var value) || value is not string name ||
			!payload.TryGetValue("avatarId", out var avatar) || !payload.TryGetValue("chips", out var chips))
			throw new FormatException("服务器返回的玩家存档不完整");
		int avatarId = Convert.ToInt32(avatar, CultureInfo.InvariantCulture);
		int balance = Convert.ToInt32(chips, CultureInfo.InvariantCulture);
		if (avatarId < 1 || avatarId > 4 || balance < 0) throw new FormatException("服务器返回的玩家存档无效");
		return new PlayerProfile(playerId, name, avatarId, balance);
	}
}
