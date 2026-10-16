using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.App;

public sealed record CacheItem(string Name, CacheGroupKind Kind, IReadOnlyList<CacheFile> Files, IReadOnlyList<string> GameIds, IReadOnlyCollection<string> Seen, string? Note = null)
{
	public long Bytes => Files.Sum((CacheFile f) => f.Length);
}
