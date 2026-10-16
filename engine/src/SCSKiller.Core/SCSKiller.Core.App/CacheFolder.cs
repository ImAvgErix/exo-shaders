namespace SCSKiller.Core.App;

public sealed record CacheFolder(string Label, string Path, bool Exists, string? Final, bool TargetMissing, long Bytes, long? Cap = null);
