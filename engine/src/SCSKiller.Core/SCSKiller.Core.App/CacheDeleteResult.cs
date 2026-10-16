using System.Collections.Generic;

namespace SCSKiller.Core.App;

public sealed record CacheDeleteResult(long Bytes, int Files, IReadOnlyList<string> Skipped);
