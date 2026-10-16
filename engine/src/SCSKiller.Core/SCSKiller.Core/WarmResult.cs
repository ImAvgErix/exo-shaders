using System;
using System.Collections.Generic;

namespace SCSKiller.Core;

public sealed record WarmResult(WarmOutcome Outcome, long Done, long Total, long Failed, TimeSpan Elapsed, long CacheGrowthBytes, string LogPath, string? Error, long Skipped = 0L, IReadOnlyCollection<string>? Crashed = null, long DriverSkipped = 0L, bool Resumable = false);
