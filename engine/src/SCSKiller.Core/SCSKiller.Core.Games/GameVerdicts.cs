using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

public sealed class GameVerdicts
{
	public sealed record Verdict(string Kind, string Text);

	private sealed record Entry(string Name, string[] Ids, string Verdict, string Reason, string Source, string Date, string? Note = null, string[]? Engines = null, string[]? Builds = null);

	public const string Name = "game-verdicts";

	public const string FileName = "game-verdicts.json";

	public const string NoStutterVerdict = "no-stutter";

	public const string UnsupportedYetVerdict = "unsupported-yet";

	public const string UnsupportedVerdict = "unsupported";

	public const string OtherReason = "other";

	public const int MaxGames = 500;

	private static readonly string[] Verdicts = new string[3] { "no-stutter", "unsupported-yet", "unsupported" };

	private static readonly string[] Members = new string[9] { "name", "ids", "verdict", "reason", "note", "source", "date", "engines", "builds" };

	public static readonly IReadOnlyDictionary<string, string> Reasons = new Dictionary<string, string>
	{
		["engine-precompiles"] = "Its engine prepares shaders at startup",
		["driver-cache-ignored"] = "The driver doesn't use a compile's cache for this game",
		["anti-cheat"] = "Its anti-cheat doesn't allow it",
		["crashes"] = "Compiling crashes this game or the driver",
		["engine-unreadable"] = "SCSKiller can't read this game's shaders yet"
	};

	private readonly Dictionary<string, List<Entry>> byId = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

	public int Count { get; }

	public static GameVerdicts Embedded => field ?? (field = TryParse(ContentFile.Embedded("SCSKiller.Core.Games.game-verdicts.json")));

	public static GameVerdicts Current
	{
		get
		{
			return field ?? (field = Embedded);
		}
		set;
	}

	private GameVerdicts(List<Entry> entries)
	{
		foreach (Entry entry in entries)
		{
			string[] ids = entry.Ids;
			foreach (string key in ids)
			{
				List<Entry> list3;
				if (!byId.TryGetValue(key, out List<Entry> value))
				{
					List<Entry> list = (byId[key] = new List<Entry>());
					list3 = list;
				}
				else
				{
					list3 = value;
				}
				list3.Add(entry);
			}
		}
		Count = entries.Count;
	}

	public static GameVerdicts? TryParse(string json)
	{
		if (Encoding.UTF8.GetByteCount(json) > 65536)
		{
			return null;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.ValueKind != JsonValueKind.Object || !rootElement.TryGetProperty("games", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 500)
			{
				return null;
			}
			return new GameVerdicts(value.EnumerateArray().Select(Read).OfType<Entry>()
				.ToList());
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static Entry? Read(JsonElement e)
	{
		if (e.ValueKind == JsonValueKind.Object && e.EnumerateObject().All((JsonProperty p) => Members.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
		{
			Entry entry = ContentFile.Read<Entry>(e);
			if ((object)entry != null && Valid(entry))
			{
				return entry;
			}
		}
		return null;
	}

	private static bool Valid(Entry e)
	{
		if (Verdicts.Contains(e.Verdict) && ContentFile.Text(e.Name, 100))
		{
			string[] ids = e.Ids;
			if (ids != null)
			{
				int num = ids.Length;
				if (num >= 1 && num <= 8 && e.Ids.All(IsId))
				{
					string reason = e.Reason;
					if (reason != null)
					{
						num = reason.Length;
						DateOnly result;
						if (num >= 1 && num <= 40 && e.Reason.All((char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') && (e.Note == null || ContentFile.Text(e.Note, 120)) && (e.Source == "" || ContentFile.Text(e.Source, 500)) && Scope(e.Engines) && Scope(e.Builds))
						{
							return DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
						}
					}
				}
			}
		}
		return false;
	}

	internal static bool IsId(string? id)
	{
		if (id != null && id.Length <= 120)
		{
			int num = id.IndexOf(':');
			if (num > 0 && num < id.Length - 1)
			{
				return !id.Any(char.IsWhiteSpace);
			}
		}
		return false;
	}

	private static bool Scope(string[]? s)
	{
		if (s != null)
		{
			if (s != null)
			{
				int num = s.Length;
				if (num >= 1 && num <= 16)
				{
					return s.All((string v) => ContentFile.Text(v, 60));
				}
			}
			return false;
		}
		return true;
	}

	public static string Text(string verdict, string reason, string? note)
	{
		string text;
		if (!(reason != "other") || !Reasons.TryGetValue(reason, out string value))
		{
			text = note;
			if (text == null)
			{
				if (!(verdict == "no-stutter"))
				{
					return "SCSKiller can't compile this game";
				}
				return "This game prepares its shaders itself";
			}
		}
		else
		{
			text = value;
		}
		return text;
	}

	public string? NoStutter(Game game, EngineInfo? engine)
	{
		return Find(game, engine, "no-stutter")?.Text;
	}

	public Verdict? Unsupported(Game game, EngineInfo? engine)
	{
		return Find(game, engine, "unsupported") ?? Find(game, engine, "unsupported-yet");
	}

	private Verdict? Find(Game game, EngineInfo? engine, string verdict)
	{
		Entry entry = byId.GetValueOrDefault(game.Id)?.FirstOrDefault((Entry e) =>
		{
			if (e.Verdict == verdict)
			{
				if (e.Engines == null)
				{
					goto IL_0071;
				}
				if (engine != null)
				{
					string have = engine.Family + " " + engine.Version;
					if (e.Engines.Any((string v) => have.Equals(v, StringComparison.OrdinalIgnoreCase) || have.StartsWith(v + ".", StringComparison.OrdinalIgnoreCase)))
					{
						goto IL_0071;
					}
				}
			}
			return false;
			IL_0071:
			if (e.Builds != null)
			{
				if (game.Version != null)
				{
					return e.Builds.Contains(game.Version);
				}
				return false;
			}
			return true;
		});
		if ((object)entry == null)
		{
			return null;
		}
		return new Verdict(entry.Verdict, Text(entry.Verdict, entry.Reason, entry.Note));
	}
}
