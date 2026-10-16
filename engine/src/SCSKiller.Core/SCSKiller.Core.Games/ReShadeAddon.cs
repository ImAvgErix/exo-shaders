namespace SCSKiller.Core.Games;

public sealed record ReShadeAddon(string Path, string? Mod, AddonKind Kind, bool Disabled = false);
