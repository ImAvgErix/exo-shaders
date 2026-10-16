using System.Collections.Generic;

namespace SCSKiller.Core;

public sealed record CachePart(string Name, IReadOnlyList<string> Files, long Bytes);
