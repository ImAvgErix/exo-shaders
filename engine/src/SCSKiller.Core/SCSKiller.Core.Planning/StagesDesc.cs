using System.Text.Json.Serialization;

namespace SCSKiller.Core.Planning;

public sealed record StagesDesc(DescData? Desc = null, string[]? Any = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool BindsNothing = false, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Dxbc = false)
{
	internal bool Predicated
	{
		get
		{
			if (!BindsNothing)
			{
				return Dxbc;
			}
			return true;
		}
	}
}
