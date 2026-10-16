using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public sealed class News
{
	private sealed record Doc(JsonElement[] Items, string? About = null);

	public const string Name = "news";

	public const string ContentPath = "v1/content/news.json";

	private const int MaxItems = 50;

	public static readonly News Empty = new News(Array.Empty<NewsItem>());

	public IReadOnlyList<NewsItem> Items { get; }

	private News(IReadOnlyList<NewsItem> items)
	{
		Items = items;
	}

	public static News? TryParse(string json)
	{
		Doc doc = ContentFile.Read<Doc>(json);
		if ((object)doc != null)
		{
			JsonElement[] items = doc.Items;
			if (items != null && items.Length <= 50 && (doc.About == null || ContentFile.Text(doc.About, 1000)))
			{
				List<NewsItem> list = new List<NewsItem>();
				items = doc.Items;
				foreach (JsonElement e in items)
				{
					NewsItem i2 = ContentFile.Read<NewsItem>(e);
					if ((object)i2 != null && Valid(i2) && list.All((NewsItem x) => x.Id != i2.Id))
					{
						list.Add(i2);
					}
				}
				return new News(list);
			}
		}
		return null;
	}

	private static bool Valid(NewsItem i)
	{
		string id = i.Id;
		if (id != null)
		{
			int length = id.Length;
			if (length > 0 && length <= 64 && i.Id.All((char c) =>
			{
				bool flag = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c);
				if (!flag)
				{
					bool flag2 = ((c == '-' || c == '.') ? true : false);
					flag = flag2;
				}
				return flag;
			}) && Day(i.Date).HasValue && (i.Expires == null || Day(i.Expires).HasValue) && ContentFile.Text(i.Title, 100) && ContentFile.Text(i.Body, 500) && (i.Link == null || IsAllowedLink(i.Link)) && (i.Game == null || ContentFile.Text(i.Game, 200)) && (i.MinVersion == null || AppVersion.Parse(i.MinVersion) != null) && (i.MaxVersion == null || AppVersion.Parse(i.MaxVersion) != null) && List(i.Channels) && List(i.Ent))
			{
				return List(i.Gpu);
			}
		}
		return false;
	}

	private static bool List(string[]? values)
	{
		if (values != null)
		{
			if (values.Length != 0)
			{
				return values.All((string v) => ContentFile.Text(v, 32));
			}
			return false;
		}
		return true;
	}

	public static DateOnly? Day(string? s)
	{
		if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
		{
			return null;
		}
		return result;
	}

	public static bool IsAllowedLink(string url)
	{
		if (WelcomeContent.IsAllowedLink(url))
		{
			return true;
		}
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri u) || u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo.Length != 0)
		{
			return false;
		}
		if (!Host("discord.gg") && !Host("discord.com") && !u.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase))
		{
			if (Host("github.com"))
			{
				if (!u.AbsolutePath.Equals("/BlueHeisenberg", StringComparison.OrdinalIgnoreCase))
				{
					return u.AbsolutePath.StartsWith("/BlueHeisenberg/", StringComparison.OrdinalIgnoreCase);
				}
				return true;
			}
			return false;
		}
		return true;
		bool Host(string h)
		{
			return u.Host.Equals(h, StringComparison.OrdinalIgnoreCase);
		}
	}

	public IReadOnlyList<NewsItem> For(NewsTarget t, DateOnly today)
	{
		return (from i in Items
			where Shown(i, t, today)
			orderby Day(i.Date) descending
			select i).ThenBy((NewsItem i) => i.Id, StringComparer.Ordinal).ToList();
	}

	private static bool Shown(NewsItem i, NewsTarget t, DateOnly today)
	{
		if (i.Expires != null)
		{
			DateOnly? dateOnly = Day(i.Expires);
			if (!(today < dateOnly))
			{
				goto IL_013d;
			}
		}
		if ((i.Channels == null || i.Channels.Contains(t.Version.Channel)) && (i.Ent == null || i.Ent.Any(((IEnumerable<string>)t.Ent).Contains<string>) || (t.Ent.Count == 0 && i.Ent.Contains("none"))) && (i.MinVersion == null || t.Version.CompareTo(AppVersion.Parse(i.MinVersion)) >= 0) && (i.MaxVersion == null || t.Version.CompareTo(AppVersion.Parse(i.MaxVersion)) <= 0) && (i.Gpu == null || i.Gpu.Contains(t.Gpu.ToString().ToLowerInvariant())))
		{
			if (i.Game != null)
			{
				return t.HasGame(i.Game);
			}
			return true;
		}
		goto IL_013d;
		IL_013d:
		return false;
	}

	public static News? Cached(string file, ContentTrust trust, IReadOnlyCollection<string>? channels)
	{
		return trust.Cached("news", TryParse, file, channels);
	}

	public static string[] Channels(string updateChannel)
	{
		switch (updateChannel)
		{
		case "alpha":
		case "internal":
			return new string[1] { "alpha" };
		case "beta":
			return new string[1] { "beta" };
		default:
			return Array.Empty<string>();
		}
	}

	public static Task<News?> FetchAsync(string cacheFile, ContentTrust trust, RouteFailover? routes = null, string? token = null, IReadOnlyCollection<string>? channels = null, TimeSpan? budget = null)
	{
		return trust.FetchAsync("news", TryParse, cacheFile, channels, token, routes, budget);
	}
}
