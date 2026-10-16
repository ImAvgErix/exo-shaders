namespace SCSKiller.Core.Planning;

public sealed record UnitPolicy(string Name, bool ReadLayout, bool Topology, bool NextStage, bool ExportShape, uint FreeRsFlags, bool PartnerReads = false, UvClampLayouts UvClamp = UvClampLayouts.None, bool ExactExport = false)
{
	public const uint DenyFlags = 830u;

	public static readonly UnitPolicy Amd = new UnitPolicy("amd", ReadLayout: true, Topology: true, NextStage: true, ExportShape: true, 830u, PartnerReads: true, UvClampLayouts.Recorded);

	public static readonly UnitPolicy Nvidia = new UnitPolicy("nvidia", ReadLayout: false, Topology: false, NextStage: false, ExportShape: false, 0u);

	public static readonly UnitPolicy Intel = new UnitPolicy("intel", ReadLayout: false, Topology: false, NextStage: true, ExportShape: true, 0u, PartnerReads: true, UvClampLayouts.None, ExactExport: true);

	public static UnitPolicy? For(VendorCaps caps)
	{
		if (caps.PerStageCache)
		{
			if (!(caps.Profile == "intel-1"))
			{
				if (!caps.StateIndependentCache)
				{
					return Amd;
				}
				return Nvidia;
			}
			return Intel;
		}
		return null;
	}
}
