using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

public static class Collections
{
	private sealed record Entry(string Id, string Name, string[] Exes, string Data);

	private sealed record Collection(Entry[] Games);

	private sealed record ListFile(Collection[] Collections, string? About = null);

	private static readonly List<IReadOnlyList<CollectionGame>> All = Parse(ContentFile.Embedded("SCSKiller.Core.Games.collections.json"));

	public static IReadOnlyList<IReadOnlyList<CollectionGame>> Current { get; }

	private static bool InOwnFolders(IReadOnlyList<CollectionGame> c)
	{
		return Path.GetFileName(c[0].Exes[0]) != c[0].Exes[0];
	}

	internal static List<IReadOnlyList<CollectionGame>> Parse(string json)
	{
		ListFile? listFile = ContentFile.Read<ListFile>(json) ?? throw new InvalidDataException("collections.json");
		List<IReadOnlyList<CollectionGame>> list = new List<IReadOnlyList<CollectionGame>>();
		HashSet<string> hashSet = new HashSet<string>();
		Collection[] collections = listFile.Collections;
		foreach (Collection collection in collections)
		{
			if (collection.Games.Length < 2)
			{
				throw new InvalidDataException("collections.json: a collection of one game");
			}
			string[] exes = collection.Games[0].Exes;
			int num;
			if (exes != null && exes.Length >= 1)
			{
				string text = exes[0];
				num = ((Path.GetFileName(text) != text) ? 1 : 0);
			}
			else
			{
				num = 0;
			}
			bool own = (byte)num != 0;
			Entry[] games = collection.Games;
			foreach (Entry e in games)
			{
				if (!ContentFile.Text(e.Id, 128) || !ContentFile.Text(e.Name, 128) || !hashSet.Add(e.Id) || (e != collection.Games[0] && !e.Id.StartsWith(collection.Games[0].Id + ":", StringComparison.Ordinal)) || e.Exes.Length == 0 || e.Exes.Any((string x) =>
				{
					if (x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
					{
						if (!own)
						{
							return Path.GetFileName(x) != x;
						}
						if (x.StartsWith(e.Data + "\\", StringComparison.OrdinalIgnoreCase))
						{
							return x.Split('\\', '/').Contains("..");
						}
						return true;
					}
					return true;
				}) || !ContentFile.Text(e.Data, 260) || Path.IsPathRooted(e.Data) || e.Data.Split('\\', '/').Contains(".."))
				{
					throw new InvalidDataException("collections.json: " + e.Id);
				}
			}
			List<CollectionGame> list2 = new List<CollectionGame>();
			list2.AddRange(collection.Games.Select((Entry entry) => new CollectionGame(entry.Id, entry.Name, entry.Exes, entry.Data)));
			list.Add(new _003C_003Ez__ReadOnlyList<CollectionGame>(list2));
		}
		return list;
	}

	public static (IReadOnlyList<CollectionGame> All, CollectionGame Game)? Member(string gameId)
	{
		foreach (IReadOnlyList<CollectionGame> item in Current)
		{
			CollectionGame collectionGame = item.FirstOrDefault((CollectionGame e) => e.Id == gameId);
			if ((object)collectionGame != null)
			{
				return (item, collectionGame);
			}
		}
		return null;
	}

	public static string? RecordedBy(string gameId)
	{
		(IReadOnlyList<CollectionGame>, CollectionGame)? tuple = Member(gameId);
		if (tuple.HasValue)
		{
			(IReadOnlyList<CollectionGame>, CollectionGame) valueOrDefault = tuple.GetValueOrDefault();
			if (valueOrDefault.Item1[0].Id != gameId)
			{
				return valueOrDefault.Item1[0].Id;
			}
		}
		return null;
	}

	public static string[]? Exes(Game g)
	{
		string[] array = Member(g.Id)?.Game.Exes;
		if (array == null || !array.Contains(Path.GetFileName(g.ExePath), StringComparer.OrdinalIgnoreCase))
		{
			return null;
		}
		return array;
	}

	public static IEnumerable<string> ExesBeside(string exe)
	{
		return from x in Current.Where((IReadOnlyList<CollectionGame> c) => c.Any((CollectionGame e) => e.Exes.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase))).SelectMany((IReadOnlyList<CollectionGame> c) => c.SelectMany((CollectionGame e) => e.Exes))
			select Path.Combine(Path.GetDirectoryName(exe), x);
	}

	public static List<Game> Expand(Game app)
	{
		IReadOnlyList<CollectionGame> readOnlyList = All.FirstOrDefault((IReadOnlyList<CollectionGame> c) => c[0].Id == app.Id);
		if (readOnlyList == null)
		{
			return new List<Game>();
		}
		bool flag = InOwnFolders(readOnlyList);
		List<Game> list = new List<Game>();
		foreach (CollectionGame item in readOnlyList)
		{
			string text = item.Exes.Select((string x) => Path.Combine(app.InstallDir, x)).FirstOrDefault(File.Exists);
			if (text != null)
			{
				list.Add(app with
				{
					Id = item.Id,
					Name = item.Name,
					ExePath = text,
					InstallDir = (flag ? Path.Combine(app.InstallDir, item.Data) : app.InstallDir)
				});
			}
		}
		if (list.Count <= 0 || !(list[0].Id == readOnlyList[0].Id))
		{
			return new List<Game>();
		}
		return list;
	}

	public static string AppDir(Game g)
	{
		CollectionGame collectionGame = All.Where(InOwnFolders).SelectMany((IReadOnlyList<CollectionGame> c) => c).FirstOrDefault((CollectionGame e) => e.Id == g.Id);
		if ((object)collectionGame != null)
		{
			string text = Path.TrimEndingDirectorySeparator(g.InstallDir);
			if (text.EndsWith("\\" + collectionGame.Data, StringComparison.OrdinalIgnoreCase))
			{
				string text2 = text;
				int num = collectionGame.Data.Length + 1;
				return text2.Substring(0, text2.Length - num);
			}
		}
		return g.InstallDir;
	}

	static Collections()
	{
		List<IReadOnlyList<CollectionGame>> list = new List<IReadOnlyList<CollectionGame>>();
		list.AddRange(All.Where((IReadOnlyList<CollectionGame> c) => !InOwnFolders(c)));
		Current = new _003C_003Ez__ReadOnlyList<IReadOnlyList<CollectionGame>>(list);
	}
}
