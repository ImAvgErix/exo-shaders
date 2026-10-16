using System;
using System.IO;

namespace SCSKiller.Core.Vendors;

public sealed class IntelBackend(GpuInfo dxgi) : IGpuVendorBackend, IRefreshableGpu
{
	public const string Profile = "intel-1";

	public const long PerGameCap = 536870912L;

	public GpuVendor Vendor => GpuVendor.Intel;

	public GpuInfo Gpu { get; private set; } = dxgi;

	public VendorCaps Caps { get; } = new VendorCaps("intel-1", CacheKeyedByExeName: true, StateIndependentCache: false, CacheSizeConfigurable: false, PerStageCache: true, RtCacheGranularity.WholeObject, PackageKeyed: false, 536870912L);

	private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

	public static string CacheDir => Path.Combine(LocalAppData + "Low", "Intel", "ShaderCache");

	public bool Refresh(GpuInfo adapter)
	{
		Gpu = adapter;
		return adapter.DriverVersion.Length > 0;
	}

	public string FallbackVersion(string umd)
	{
		return umd;
	}

	public CacheUsage GetCacheUsage()
	{
		return new CacheUsage(CacheDir, AmdBackend.Bytes(CacheDir), UpperBound: false);
	}

	public CacheLimit? GetCacheLimit()
	{
		return null;
	}

	public void SetCacheLimit(CacheLimit limit)
	{
		throw new NotSupportedException("the Intel shader cache size is not configurable (" + Gpu.Name + ")");
	}
}
