using System;

namespace SCSKiller.Core.App;

public sealed class AccountException : Exception
{
	public AccountException(string message)
		: base(message)
	{
	}
}
