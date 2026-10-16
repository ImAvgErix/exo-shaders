using System.Linq;

namespace SCSKiller.Core.Planning;

public sealed record DescData(uint Flags, uint[][] Rows, bool AppendRanges = false, bool Version10 = false, bool RangeOffsets = false)
{
	public RootSig.Desc ToDesc()
	{
		return new RootSig.Desc(Flags, Rows.ToList(), AppendRanges, Version10, RangeOffsets);
	}
}
