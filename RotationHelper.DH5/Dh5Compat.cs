using System;
using VoK.Sdk;
using VoK.Sdk.Ddo;

namespace RotationHelper;

internal static class Dh5Compat
{
	public static bool IsValid(this IPartyMember member) => member != null;

	public static bool Connected(this IPartyMember member) => member != null;

	public static DateTime? EffectExpiration(this IActiveEffect effect)
	{
		if (effect.Expiration.HasValue)
		{
			return effect.Expiration;
		}
		if (effect.ApplicationTimestamp.HasValue)
		{
			try
			{
				return DateTimeOffset.FromUnixTimeMilliseconds((long)effect.ApplicationTimestamp.Value).UtcDateTime;
			}
			catch
			{
			}
		}
		if (effect.TimerUpdatedDate.HasValue && effect.Duration.HasValue)
		{
			return effect.TimerUpdatedDate.Value.AddSeconds((double)effect.Duration.Value);
		}
		return null;
	}
}