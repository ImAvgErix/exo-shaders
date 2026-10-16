using System;
using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.App;

public sealed record CacheInventory(IReadOnlyList<CacheFolder> Folders, IReadOnlyList<CacheGroup> Groups, DateTime? DriverInstalled = null)
{
	public IReadOnlyList<CacheItem> Everything(bool withOthers)
	{
		return Groups.Where((CacheGroup g) => withOthers || g.Kind != CacheGroupKind.Unattributed).SelectMany((CacheGroup g) => g.Items).ToList();
	}

	public long Bytes(CacheGroupKind kind)
	{
		return Groups.Where((CacheGroup g) => g.Kind == kind).Sum((CacheGroup g) => g.Bytes);
	}
}
