namespace SCSKiller.Core.App;

public sealed record OfflineSession(string GameId, string Exe, string InstallDir, int Pid = 0, long Started = 0L, bool Resumed = false, string[]? Created = null, bool Risk = false);
