namespace SCSKiller.Core.Games;

public sealed record OfflineEacGame(string Id, string Name, string Exe, string AppId, string? EacProduct = null);
