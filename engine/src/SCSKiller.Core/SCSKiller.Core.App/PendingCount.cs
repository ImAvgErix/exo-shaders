namespace SCSKiller.Core.App;

public sealed record PendingCount(string Key, long Recorded, long? Planned, bool Unknown, long Upscaler = 0L);
