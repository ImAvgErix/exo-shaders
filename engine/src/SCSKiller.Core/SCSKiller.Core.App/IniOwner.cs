namespace SCSKiller.Core.App;

public sealed record IniOwner(string Dir, string? Tracked = null, bool Users = false, bool Remove = false, string? Chain = null, string? Install = null);
