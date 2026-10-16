namespace SCSKiller.Core.App;

public sealed record CommunityEntry(string ContentHash, string Object, long Size, int Psos, int Uploaders, bool Full = false);
