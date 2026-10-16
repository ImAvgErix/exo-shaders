using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.App;

public sealed class CompatFeed
{
	public sealed record Shares(double Gone, double Better, [property: JsonPropertyName("no_help")] double NoHelp, double Worse, [property: JsonPropertyName("no_start")] double NoStart, [property: JsonPropertyName("not_compiled")] double NotCompiled, [property: JsonPropertyName("no_stutter_before")] double NoStutterBefore)
	{
		internal double[] All => new double[7] { Gone, Better, NoHelp, Worse, NoStart, NotCompiled, NoStutterBefore };
	}

	private sealed record Class(string Vendor, string? Family, int Devices, Shares Shares);

	private sealed record Entry(string[] Ids, Class[] Classes);

	private sealed record Family(string Vendor, [property: JsonPropertyName("family")] string Name, string[] Match);

	private sealed record Doc(string Generated, [property: JsonPropertyName("min_devices")] int MinDevices, Family[] Families, Entry[] Games);

	public const string Name = "compat";

	public const string FileName = "compat.json";

	public const string ContentPath = "v1/content/compat.json";

	public const int MaxBytes = 1048576;

	public const double Warn = 0.2;

	public const double Mostly = 0.6;

	private const double Rounding = 1E-09;

	private static readonly string[] Vendors = new string[2] { "nvidia", "amd" };

	private readonly Doc doc;

	private readonly Dictionary<string, Entry> byId = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

	private CompatFeed(Doc d)
	{
		doc = d;
		Entry[] games = d.Games;
		foreach (Entry entry in games)
		{
			string[] ids = entry.Ids;
			foreach (string key in ids)
			{
				byId.TryAdd(key, entry);
			}
		}
	}

	public static CompatFeed? TryParse(string json)
	{
		Doc doc = ContentFile.Read<Doc>(json, 1048576);
		if ((object)doc == null || !Valid(doc))
		{
			return null;
		}
		return new CompatFeed(doc);
	}

	private static bool Valid(Doc d)
	{
		if (DateOnly.TryParseExact(d.Generated, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var _))
		{
			int minDevices = d.MinDevices;
			if (minDevices >= 1 && minDevices <= 1000000 && d.Families.Length <= 500 && d.Families.All((Family f) =>
			{
				if (Vendors.Contains(f.Vendor) && IsFamily(f.Name))
				{
					string[] match = f.Match;
					if (match != null)
					{
						int num = match.Length;
						if (num >= 1 && num <= 64)
						{
							return f.Match.All((string m) => ContentFile.Text(m, 64));
						}
					}
				}
				return false;
			}) && d.Games.Length <= 20000)
			{
				return d.Games.All((Entry g) =>
				{
					string[] ids = g.Ids;
					if (ids != null)
					{
						int num = ids.Length;
						if (num >= 1 && num <= 8 && g.Ids.All(GameVerdicts.IsId))
						{
							Class[] classes = g.Classes;
							if (classes != null)
							{
								num = classes.Length;
								if (num >= 1 && num <= 64)
								{
									return g.Classes.All((Class c) => Vendors.Contains(c.Vendor) && (c.Family == null || IsFamily(c.Family)) && c.Devices >= 0 && c.Shares.All.All((double v) => v >= 0.0 && v <= 1.0));
								}
							}
						}
					}
					return false;
				});
			}
		}
		return false;
	}

	private static bool IsFamily(string s)
	{
		int length = s.Length;
		if (length >= 1 && length <= 40)
		{
			return s.All((char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
		}
		return false;
	}

	public string? FamilyOf(string vendor, string? gpuModel)
	{
		if (gpuModel != null)
		{
			return doc.Families.FirstOrDefault((Family f) => f.Vendor == vendor && f.Match.Any((string m) => gpuModel.Contains(m, StringComparison.OrdinalIgnoreCase)))?.Name;
		}
		return null;
	}

	public SimilarSystems? For(Game game, string? vendor, string? gpuModel)
	{
		if (vendor == null || !byId.TryGetValue(game.Id, out Entry value))
		{
			return null;
		}
		string family = FamilyOf(vendor, gpuModel);
		Class obj = ((family == null) ? null : value.Classes.FirstOrDefault((Class c) => c.Vendor == vendor && c.Family == family && c.Devices >= doc.MinDevices)) ?? value.Classes.FirstOrDefault((Class c) => c.Vendor == vendor && c.Family == null && c.Devices >= doc.MinDevices);
		if (!(obj == null))
		{
			return Verdict(obj.Shares);
		}
		return null;
	}

	public static SimilarSystems? Verdict(Shares s)
	{
		if (!(s.Worse >= 0.199999999))
		{
			if (!(s.Gone + s.Better >= 0.599999999))
			{
				if (!(s.NoHelp + s.NotCompiled + s.NoStutterBefore >= 0.599999999))
				{
					return null;
				}
				return SimilarSystems.NoHelp;
			}
			return SimilarSystems.Helped;
		}
		return SimilarSystems.Warning;
	}

	public static CompatFeed? Cached(ContentTrust trust, string cacheFile)
	{
		try
		{
			return trust.Accept("compat", File.ReadAllText(cacheFile), TryParse);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public static async Task<CompatFeed?> FetchAsync(ContentTrust trust, string cacheFile, RouteFailover routes, TimeSpan? budget = null)
	{
		(string, HttpStatusCode?) tuple = await routes.GetContentStatusAsync("v1/content/compat.json", budget ?? TimeSpan.FromSeconds(3L), 1048576);
		var (text, _) = tuple;
		if (tuple.Item2 == HttpStatusCode.NotFound)
		{
			try
			{
				File.Delete(cacheFile);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
			return null;
		}
		if (text != null)
		{
			CompatFeed compatFeed = trust.Accept("compat", text, TryParse);
			if (compatFeed != null)
			{
				ContentFile.Keep(cacheFile, text);
				return compatFeed;
			}
		}
		return Cached(trust, cacheFile);
	}
}
