using System;

namespace SCSKiller.Core.App;

public sealed record LaunchCheck(DateTimeOffset At, long Hits, long Compiles)
{
	public double Compiled
	{
		get
		{
			if (Hits + Compiles <= 0)
			{
				return 0.0;
			}
			return (double)Compiles / (double)(Hits + Compiles);
		}
	}
}
