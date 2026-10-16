using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.App;

public sealed record CacheGroup(CacheGroupKind Kind, IReadOnlyList<CacheItem> Items, string? Note = null)
{
	public long Bytes => Items.Sum((CacheItem i) => i.Bytes);
}
