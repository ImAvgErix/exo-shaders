namespace SCSKiller.Core;

public sealed record WarmProgress(long Done, long Total, long Failed, double PerSecond, long CacheGrowthBytes = 0L, long Skipped = 0L, string? Note = null, int Workers = 0, int Threads = 0, long DriverSkipped = 0L);
