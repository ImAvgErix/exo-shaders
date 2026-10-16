namespace SCSKiller.Core.Vendors;

public interface IRefreshableGpu
{
	bool Refresh(GpuInfo adapter);

	string FallbackVersion(string umd);
}
