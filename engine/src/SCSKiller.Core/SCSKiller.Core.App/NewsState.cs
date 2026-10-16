using System;
using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.App;

public static class NewsState
{
	private const int Keep = 200;

	private static readonly object Lock = new object();

	public static (IReadOnlyList<NewsItem> Shown, IReadOnlySet<string> Unread) Of(AppStore store, IEnumerable<NewsItem> items)
	{
		NewsSeen seen = store.LoadNewsSeen();
		List<NewsItem> list = items.Where((NewsItem i) =>
		{
			string[]? dismissed = seen.Dismissed;
			return dismissed == null || !dismissed.Contains(i.Id);
		}).ToList();
		return (Shown: list, Unread: list.Select((NewsItem i) => i.Id).Except(seen.Read ?? Array.Empty<string>()).ToHashSet());
	}

	public static void MarkRead(AppStore store, IEnumerable<string> ids, IReadOnlyCollection<string> feed)
	{
		Update(store, (NewsSeen s) => s with
		{
			Read = Add(s.Read, ids, feed)
		});
	}

	public static void Dismiss(AppStore store, string id, IReadOnlyCollection<string> feed)
	{
		Update(store, (NewsSeen s) => s with
		{
			Dismissed = Add(s.Dismissed, new _003C_003Ez__ReadOnlySingleElementList<string>(id), feed),
			Read = Add(s.Read, new _003C_003Ez__ReadOnlySingleElementList<string>(id), feed)
		});
	}

	private static void Update(AppStore store, Func<NewsSeen, NewsSeen> change)
	{
		lock (Lock)
		{
			store.SaveNewsSeen(change(store.LoadNewsSeen()));
		}
	}

	private static string[] Add(string[]? ids, IEnumerable<string> more, IReadOnlyCollection<string> feed)
	{
		List<string> source = (ids ?? Array.Empty<string>()).Concat(more).Distinct().ToList();
		HashSet<string> gone = source.Where((string i) => !feed.Contains(i)).TakeLast(200).ToHashSet();
		return source.Where((string i) => feed.Contains(i) || gone.Contains(i)).ToArray();
	}
}
