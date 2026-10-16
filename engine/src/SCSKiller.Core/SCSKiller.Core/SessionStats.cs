using System;

namespace SCSKiller.Core;

public sealed record SessionStats(TimeSpan Duration, long Requests, long FromGameLibrary, long CacheHits, long Compiles, double WorstCompileMs, long RayQueryRecompiles = 0L, long StateObjectsReady = 0L, long StateObjectsCompiled = 0L, long StartupCompiles = 0L, long StateObjectsStartupCompiled = 0L);
