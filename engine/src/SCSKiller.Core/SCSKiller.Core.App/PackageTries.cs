using System;
using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.App;

public sealed class PackageTries
{
	public List<DateTimeOffset> At { get; set; } = new List<DateTimeOffset>();

	public int Failures { get; set; }

	public DateTimeOffset? FailedAt { get; set; }

	public void Requested(DateTimeOffset now)
	{
		List<DateTimeOffset> list = new List<DateTimeOffset>();
		list.AddRange(At.Where((DateTimeOffset a) => UpdateFeeds.Recent(a, now)));
		list.Add(now);
		At = list;
	}

	public void Failed(DateTimeOffset now)
	{
		int failures = Failures + 1;
		DateTimeOffset? failedAt = now;
		Failures = failures;
		FailedAt = failedAt;
	}

	public void Succeeded()
	{
		Failures = 0;
		FailedAt = null;
	}
}
