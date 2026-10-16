namespace SCSKiller.Core.App;

public sealed record SignedFeed(byte[] Feed, byte[] Sig);
