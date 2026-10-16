namespace SCSKiller.Core;

public sealed record VendorCaps(string Profile, bool CacheKeyedByExeName, bool StateIndependentCache, bool CacheSizeConfigurable, bool PerStageCache = false, RtCacheGranularity RtCacheGranularity = RtCacheGranularity.WholeObject, bool PackageKeyed = false, long? PerGameCacheCap = null);
