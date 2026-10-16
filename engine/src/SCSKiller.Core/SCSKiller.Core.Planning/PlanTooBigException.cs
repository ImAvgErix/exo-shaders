using System;

namespace SCSKiller.Core.Planning;

public sealed class PlanTooBigException : Exception
{
	public PlanTooBigException(string message)
		: base(message)
	{
	}
}
