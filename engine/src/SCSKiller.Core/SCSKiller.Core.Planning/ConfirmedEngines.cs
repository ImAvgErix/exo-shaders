using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Planning;

public sealed class ConfirmedEngines
{
	private sealed record Entry(string Version, string Game, string Evidence, string? Fork = null);

	private sealed record ListFile(Entry[] Engines, string? About = null);

	public const string ContentPath = "v1/content/confirmed-engines.json";

	public const int MaxEngines = 500;

	private readonly HashSet<(string Version, string? Fork)> set;

	public static ConfirmedEngines Embedded => field ?? (field = TryParse(ContentFile.Embedded("SCSKiller.Core.Planning.confirmed-engines.json")));

	public static ConfirmedEngines Current
	{
		get
		{
			return field ?? (field = Embedded);
		}
		set;
	}

	private ConfirmedEngines(ListFile file)
	{
		set = file.Engines.Select((Entry e) => (Version: e.Version, Fork: e.Fork)).ToHashSet();
	}

	public static ConfirmedEngines? TryParse(string json)
	{
		ListFile listFile = ContentFile.Read<ListFile>(json);
		if ((object)listFile != null)
		{
			Entry[] engines = listFile.Engines;
			if (engines != null)
			{
				int num = engines.Length;
				if (num >= 1 && num <= 500 && (listFile.About == null || ContentFile.Text(listFile.About, 1000)) && listFile.Engines.All(Valid))
				{
					return new ConfirmedEngines(listFile);
				}
			}
		}
		return null;
	}

	private static bool Valid(Entry? e)
	{
		if ((object)e != null)
		{
			string version = e.Version;
			if (version != null)
			{
				int length = version.Length;
				if (length >= 3 && length <= 5 && version.Count((char c) => c == '.') == 1 && version.All((char c) => char.IsAsciiDigit(c) || c == '.') && char.IsAsciiDigit(version[0]))
				{
					if (char.IsAsciiDigit(version[version.Length - 1]))
					{
						if (e.Fork == null)
						{
							goto IL_0107;
						}
						string fork = e.Fork;
						if (fork != null)
						{
							length = fork.Length;
							if (length > 5 && length <= 64 && e.Fork.StartsWith("GAME_", StringComparison.Ordinal) && e.Fork.All((char c) => char.IsAsciiLetterOrDigit(c) || c == '_'))
							{
								goto IL_0107;
							}
						}
					}
				}
			}
		}
		goto IL_0127;
		IL_0107:
		if (ContentFile.Text(e.Game, 100))
		{
			return ContentFile.Text(e.Evidence, 300);
		}
		goto IL_0127;
		IL_0127:
		return false;
	}

	public bool Contains(EngineInfo e)
	{
		if (!set.Contains((e.Version, e.Fork)))
		{
			if (this != Embedded)
			{
				return Embedded.Contains(e);
			}
			return false;
		}
		return true;
	}

	public static ConfirmedEngines? Cached(string file)
	{
		return ContentFile.Cached(file, TryParse);
	}

	public static Task<ConfirmedEngines?> FetchAsync(string cacheFile, RouteFailover? routes = null, TimeSpan? budget = null)
	{
		return ContentFile.FetchAsync("v1/content/confirmed-engines.json", cacheFile, TryParse, routes, budget);
	}
}
