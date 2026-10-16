namespace SCSKiller.Core.App;

public sealed record CompatCompile(CompatStatus Status, int? CoveragePct, bool Recorded, bool Maximum);
