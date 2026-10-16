using System;
using System.Collections.Generic;

namespace SCSKiller.Core.App;

internal static class ListAlso
{
	public static List<T> Also<T>(this List<T> list, Action? then)
	{
		then?.Invoke();
		return list;
	}
}
