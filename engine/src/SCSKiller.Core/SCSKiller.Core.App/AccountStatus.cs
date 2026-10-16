using System;
using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.App;

public sealed record AccountStatus(IReadOnlyList<string> Ent, DateTimeOffset? Until)
{
	public string? Benefits
	{
		get
		{
			List<string> list = (from k in Ent.Where(Names.ContainsKey)
				select Names[k]).ToList();
			if (list == null || list.Count <= 0)
			{
				return null;
			}
			string text;
			if (list.Count <= 1)
			{
				text = list[0];
			}
			else
			{
				List<string> list2 = list;
				text = string.Join(", ", list2.Slice(0, list2.Count - 1)) + " and " + list[list.Count - 1];
			}
			DateTimeOffset? until = Until;
			string text2;
			if (until.HasValue)
			{
				DateTimeOffset valueOrDefault = until.GetValueOrDefault();
				text2 = $", until {valueOrDefault.ToLocalTime():d MMM yyyy}.";
			}
			else
			{
				text2 = ".";
			}
			return "Supporter: " + text + text2;
		}
	}

	private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
	{
		["db"] = "community shader hash database",
		["beta"] = "beta builds",
		["alpha"] = "alpha builds",
		["prio"] = "priority requests"
	};
}
