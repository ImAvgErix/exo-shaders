using System;

namespace SCSKiller.Core.App;

public sealed record CacheFile(string Path, long Length, DateTime WrittenUtc);
