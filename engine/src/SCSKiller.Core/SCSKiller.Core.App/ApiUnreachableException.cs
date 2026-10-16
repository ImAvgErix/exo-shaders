using System;
using System.Net.Http;

namespace SCSKiller.Core.App;

public sealed class ApiUnreachableException : HttpRequestException
{
	public ApiUnreachableException(Exception? inner)
		: base("Can't reach the SCSKiller server.", inner)
	{
	}
}
