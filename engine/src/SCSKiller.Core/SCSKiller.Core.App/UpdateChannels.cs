using System;
using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.App;

public static class UpdateChannels
{
	public const string Stable = "stable";

	public const string Beta = "beta";

	public const string Alpha = "alpha";

	public const string Internal = "internal";

	public static readonly string[] All = new string[4] { "stable", "beta", "alpha", "internal" };

	public static IReadOnlyList<string> Offered(IReadOnlyCollection<string>? ent)
	{
		return All.Where((string c) => c == "stable" || (ent?.Contains(c) ?? false)).ToList();
	}

	public static string Effective(string? chosen, string buildChannel, IReadOnlyCollection<string>? ent)
	{
		int num = Array.IndexOf(All, chosen ?? buildChannel);
		while (num > 0 && (ent == null || !ent.Contains(All[num])))
		{
			num--;
		}
		return All[Math.Max(num, 0)];
	}

	public static bool Installs(string staged, string? chosen, string buildChannel, IReadOnlyCollection<string>? ent)
	{
		if (ent == null)
		{
			int num = Array.IndexOf(All, staged);
			if (num >= 0)
			{
				return num <= Array.IndexOf(All, chosen ?? buildChannel);
			}
			return false;
		}
		return staged == Effective(chosen, buildChannel, ent);
	}
}
