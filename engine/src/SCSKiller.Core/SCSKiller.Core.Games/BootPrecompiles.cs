using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

public sealed class BootPrecompiles
{
	private sealed record Entry(string Name, string[] Ids, string Source, string Date);

	private sealed record ListFile(Entry[] Games, string? About = null);

	public const string ContentPath = "v1/content/precompiles-at-boot.json";

	public const string Reason = "builds its shaders at the main menu; in-game stutter isn't from shader compilation";

	public const int MaxGames = 200;

	private readonly HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public static BootPrecompiles Embedded => field ?? (field = TryParse(ContentFile.Embedded("SCSKiller.Core.Games.precompiles-at-boot.json")));

	public static BootPrecompiles Current
	{
		get
		{
			return field ?? (field = Embedded);
		}
		set;
	}

	private BootPrecompiles(ListFile file)
	{
		Entry[] games = file.Games;
		foreach (Entry entry in games)
		{
			ids.UnionWith(entry.Ids);
		}
	}

	public static BootPrecompiles? TryParse(string json)
	{
		ListFile listFile = ContentFile.Read<ListFile>(json);
		if ((object)listFile != null)
		{
			Entry[] games = listFile.Games;
			if (games != null)
			{
				int num = games.Length;
				if (num >= 1 && num <= 200 && (listFile.About == null || ContentFile.Text(listFile.About, 1000)) && listFile.Games.All(Valid))
				{
					return new BootPrecompiles(listFile);
				}
			}
		}
		return null;
	}

	private static bool Valid(Entry? e)
	{
		if ((object)e != null && ContentFile.Text(e.Name, 100))
		{
			string[] array = e.Ids;
			if (array != null)
			{
				int num = array.Length;
				if (num >= 1 && num <= 8 && e.Ids.All(StutterList.IsId))
				{
					string source = e.Source;
					DateOnly result;
					if (source != null && source.Length <= 500 && StutterList.IsHttps(e.Source))
					{
						return DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
					}
				}
			}
		}
		return false;
	}

	public static BootPrecompiles? Cached(string file)
	{
		return ContentFile.Cached(file, TryParse);
	}

	public static Task<BootPrecompiles?> FetchAsync(string cacheFile, RouteFailover? routes = null, TimeSpan? budget = null)
	{
		return ContentFile.FetchAsync("v1/content/precompiles-at-boot.json", cacheFile, TryParse, routes, budget);
	}

	public bool Has(Game game)
	{
		return ids.Contains(game.Id);
	}
}
