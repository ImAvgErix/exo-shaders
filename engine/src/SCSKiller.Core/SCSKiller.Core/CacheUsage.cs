namespace SCSKiller.Core;

public sealed record CacheUsage(string Path, long BytesOnDisk, bool UpperBound);
