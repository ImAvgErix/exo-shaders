namespace SCSKiller.Core.Games;

public sealed record ManualEntry(string Exe, string InstallDir, string Name, bool Confirmed = false);
