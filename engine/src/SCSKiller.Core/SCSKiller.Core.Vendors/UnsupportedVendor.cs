using System;

namespace SCSKiller.Core.Vendors;

public sealed class UnsupportedVendor(GpuInfo gpu) : IGpuVendorBackend, IRefreshableGpu
{
	public GpuVendor Vendor => Gpu.Vendor;

	public GpuInfo Gpu { get; private set; } = gpu;

	public VendorCaps Caps { get; } = new VendorCaps("unsupported", CacheKeyedByExeName: false, StateIndependentCache: false, CacheSizeConfigurable: false);

	public bool Refresh(GpuInfo adapter)
	{
		Gpu = adapter;
		return true;
	}

	public string FallbackVersion(string umd)
	{
		return umd;
	}

	public CacheUsage GetCacheUsage()
	{
		return new CacheUsage("", 0L, UpperBound: true);
	}

	public CacheLimit? GetCacheLimit()
	{
		return null;
	}

	public void SetCacheLimit(CacheLimit limit)
	{
		throw new NotSupportedException("cache size is not configurable for " + Gpu.Name);
	}
}
