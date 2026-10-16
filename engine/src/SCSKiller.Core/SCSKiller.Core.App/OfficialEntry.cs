namespace SCSKiller.Core.App;

public sealed record OfficialEntry(string ContentHash, string Object, long Size, int Psos, OfficialVisibility Visibility);
