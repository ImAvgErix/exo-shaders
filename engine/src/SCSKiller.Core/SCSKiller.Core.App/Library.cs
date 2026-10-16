using System;
using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.App;

public static class Library
{
	private static readonly StringComparer Names = StringComparer.CurrentCultureIgnoreCase;

	public static bool Shows(GameState s, LibraryShow show)
	{
		switch (show)
		{
		case LibraryShow.NotSupported:
			return s.Status == GameStatus.Unsupported && !ScsKiller.IsUnchecked(s);
		case LibraryShow.ReadyToCompile:
		{
			GameStatus status = s.Status;
			return (status == GameStatus.Ready || status == GameStatus.Stale) ? true : false;
		}
		case LibraryShow.CompiledByScsKiller:
			return s.WarmedAt.HasValue;
		case LibraryShow.Compiled:
			return s.WarmedAt.HasValue || s.CacheOnDisk > 0;
		default:
			return true;
		}
	}

	public static IEnumerable<GameState> Sorted(IEnumerable<GameState> games, LibrarySort sort)
	{
		return sort switch
		{
			LibrarySort.NameAscending => games.OrderBy((GameState g) => g.Game.Name, Names), 
			LibrarySort.NameDescending => games.OrderByDescending((GameState g) => g.Game.Name, Names), 
			LibrarySort.PipelinesDescending => By(games, ScsKiller.PlanPipelines, descending: true), 
			LibrarySort.PipelinesAscending => By(games, ScsKiller.PlanPipelines, descending: false), 
			LibrarySort.CacheDescending => By(games, CacheBytes, descending: true), 
			LibrarySort.CacheAscending => By(games, CacheBytes, descending: false), 
			_ => games.OrderBy((GameState g) => g.Status switch
			{
				GameStatus.Stale => 0, 
				GameStatus.Warmed => 1, 
				GameStatus.Ready => 2, 
				GameStatus.NeedsRecording => 3, 
				_ => 4, 
			}).ThenBy((GameState g) => g.Game.Name, Names), 
		};
	}

	public static long? CacheBytes(GameState s)
	{
		return s.CacheOnDisk ?? s.EstimatedCacheBytes;
	}

	private static IEnumerable<GameState> By(IEnumerable<GameState> games, Func<GameState, long?> size, bool descending)
	{
		IOrderedEnumerable<GameState> source = games.OrderBy((GameState g) => !size(g).HasValue);
		return (descending ? source.ThenByDescending(size) : source.ThenBy(size)).ThenBy((GameState g) => g.Game.Name, Names);
	}
}
