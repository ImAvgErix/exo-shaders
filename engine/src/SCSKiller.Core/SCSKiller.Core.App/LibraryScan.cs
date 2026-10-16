namespace SCSKiller.Core.App;

public sealed class LibraryScan
{
	private bool started;

	public bool Start()
	{
		if (!started)
		{
			return started = true;
		}
		return false;
	}

	public void Ended(bool ok)
	{
		started = ok;
	}
}
