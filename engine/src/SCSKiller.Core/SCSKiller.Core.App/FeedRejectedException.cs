using System;

namespace SCSKiller.Core.App;

public sealed class FeedRejectedException : Exception
{
	public FeedRejectedException(string message)
		: base(message)
	{
	}
}
