using System.Collections.Generic;

namespace SCSKiller.Core;

public sealed record WarmOptions(int Threads, WarmPriority Priority, long StartAt = 0L, int MemoryMB = 0, IReadOnlyCollection<string>? SkipKeys = null, int CarefulThreads = 0);
