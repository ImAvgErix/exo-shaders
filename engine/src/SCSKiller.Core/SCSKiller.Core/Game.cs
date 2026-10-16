namespace SCSKiller.Core;

public sealed record Game(string Id, string Name, Store Store, string InstallDir, string ExePath, string? Version = null);
