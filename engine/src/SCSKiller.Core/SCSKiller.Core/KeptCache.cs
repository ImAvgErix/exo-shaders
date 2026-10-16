using System;

namespace SCSKiller.Core;

public sealed record KeptCache(long Bytes, long Frees, DateTimeOffset? PutBackAt = null, int Unkept = 0, string? HeldBy = null, bool Late = false);
