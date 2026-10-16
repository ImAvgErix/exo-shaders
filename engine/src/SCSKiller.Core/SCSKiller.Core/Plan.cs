namespace SCSKiller.Core;

public sealed record Plan(string GameId, string IndexContentHash, string Platform, string VendorProfile, PlanStats Stats, string FilePath);
