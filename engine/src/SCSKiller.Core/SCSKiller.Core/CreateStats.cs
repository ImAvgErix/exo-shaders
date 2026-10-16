namespace SCSKiller.Core;

public sealed record CreateStats(long PlayHits, long PlayCompiles, long StartupCompiles, long Library, long StateObjectsReady, long StateObjectsCompiled)
{
	public double? ReadyShare
	{
		get
		{
			if (PlayHits + PlayCompiles <= 0)
			{
				return null;
			}
			return (double)PlayHits / (double)(PlayHits + PlayCompiles);
		}
	}
}
