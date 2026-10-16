using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

public sealed class StutterList
{
	private sealed record Entry(string Name, string[] Ids, string Severity, string Reason, string Source, string Date);

	private sealed record ListFile(Entry[] Games, string? About = null);

	public const string ContentPath = "v1/content/known-stutter.json";

	public const string OwnMeasurement = "SCSKiller measurement";

	public const int MaxBytes = 65536;

	public const int MaxGames = 200;

	private static readonly string[] Stores = new string[11]
	{
		"steam", "xbox", "epic", "ea", "gog", "ubisoft", "battlenet", "purple", "hoyoplay", "gaijin",
		"riot"
	};

	private readonly Dictionary<string, KnownStutter> byId = new Dictionary<string, KnownStutter>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, KnownStutter> byName = new Dictionary<string, KnownStutter>();

	public int Count { get; }

	public static StutterList Embedded => field ?? (field = TryParse(ContentFile.Embedded("SCSKiller.Core.Games.known-stutter.json")));

	public static StutterList Current
	{
		get
		{
			return field ?? (field = Embedded);
		}
		set;
	}

	private StutterList(ListFile file)
	{
		Entry[] games = file.Games;
		foreach (Entry entry in games)
		{
			KnownStutter value = new KnownStutter((entry.Severity == "severe") ? StutterSeverity.Severe : StutterSeverity.Moderate, entry.Reason, entry.Source, entry.Date);
			string[] ids = entry.Ids;
			foreach (string key in ids)
			{
				byId[key] = value;
			}
			byName[Key(entry.Name)] = value;
		}
		Count = file.Games.Length;
	}

	public static StutterList? TryParse(string json)
	{
		ListFile listFile = ContentFile.Read<ListFile>(json);
		if ((object)listFile != null)
		{
			Entry[] games = listFile.Games;
			if (games != null)
			{
				int num = games.Length;
				if (num >= 1 && num <= 200 && (listFile.About == null || Text(listFile.About, 1000)) && listFile.Games.All(Valid))
				{
					return new StutterList(listFile);
				}
			}
		}
		return null;
	}

	private static bool Valid(Entry? e)
	{
		int num;
		if ((object)e != null && Text(e.Name, 100) && Key(e.Name).Length > 0 && Text(e.Reason, 300))
		{
			string[] ids = e.Ids;
			if (ids != null && ids.Length <= 8)
			{
				num = (e.Ids.All(IsId) ? 1 : 0);
				goto IL_0070;
			}
		}
		num = 0;
		goto IL_0070;
		IL_0070:
		bool flag = (byte)num != 0;
		if (flag)
		{
			string severity = e.Severity;
			bool flag2 = ((severity == "severe" || severity == "moderate") ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			if (!(e.Source == "SCSKiller measurement"))
			{
				string severity = e.Source;
				if (severity == null || severity.Length > 500 || !IsHttps(e.Source))
				{
					goto IL_00ef;
				}
			}
			DateOnly result;
			return DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
		}
		goto IL_00ef;
		IL_00ef:
		return false;
	}

	private static bool Text(string? s, int max)
	{
		return ContentFile.Text(s, max);
	}

	internal static bool IsId(string? id)
	{
		if (id != null && id.Length <= 120)
		{
			int num = id.IndexOf(':');
			if (num > 0 && Stores.Contains(id.Substring(0, num)) && id.Length > num + 1 && id.Substring(num + 1).All((char ch) =>
			{
				bool flag = char.IsAsciiLetterOrDigit(ch);
				if (!flag)
				{
					bool flag2 = ((ch == '-' || ch == '.' || ch == '_') ? true : false);
					flag = flag2;
				}
				return flag;
			}))
			{
				if (!(id.Substring(0, num) != "steam"))
				{
					return id.Substring(num + 1).All(char.IsAsciiDigit);
				}
				return true;
			}
		}
		return false;
	}

	internal static bool IsHttps(string url)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out Uri result) && result.Scheme == Uri.UriSchemeHttps && result.IsDefaultPort)
		{
			return result.UserInfo.Length == 0;
		}
		return false;
	}

	public static StutterList? Cached(string file)
	{
		return ContentFile.Cached(file, TryParse);
	}

	public static Task<StutterList?> FetchAsync(string cacheFile, RouteFailover? routes = null, TimeSpan? budget = null)
	{
		return ContentFile.FetchAsync("v1/content/known-stutter.json", cacheFile, TryParse, routes, budget);
	}

	public KnownStutter? Find(Game game)
	{
		return byId.GetValueOrDefault(game.Id) ?? byName.GetValueOrDefault(Key(game.Name));
	}

	public static List<GameState> Recommended(IEnumerable<GameState> games)
	{
		return (from s in games.Where((GameState s) =>
			{
				bool flag = s.KnownStutter != null;
				if (flag)
				{
					GameStatus status = s.Status;
					bool flag2 = (uint)(status - 1) <= 3u;
					flag = flag2;
				}
				return flag && !ScsKiller.NeedsOfflineSession(s);
			})
			orderby s.Status == GameStatus.Warmed, s.KnownStutter.Severity descending
			select s).ThenBy((GameState s) => s.Game.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
	}

	private static string Key(string name)
	{
		return string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
	}
}
