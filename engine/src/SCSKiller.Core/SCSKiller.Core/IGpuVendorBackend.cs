using System;

namespace SCSKiller.Core;

public interface IGpuVendorBackend
{
	GpuVendor Vendor { get; }

	GpuInfo Gpu { get; }

	VendorCaps Caps { get; }

	IAppCache? AppCache => null;

	CacheUsage GetCacheUsage();

	CacheLimit? GetCacheLimit();

	void SetCacheLimit(CacheLimit limit);

	AutoShaderState? GetAutoShaderCompilation()
	{
		return null;
	}

	string SetAutoShaderCompilation(AutoShaderCompilation level)
	{
		throw new NotSupportedException("Auto Shader Compilation is an NVIDIA feature (" + Gpu.Name + ")");
	}
}
