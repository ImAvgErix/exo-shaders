using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

public sealed class MaximumMode
{
	private sealed record Entry(string Name, string[] Ids, string[] Vendors, string Source, string Date, string? Note = null);

	public sealed record Suggestion(string? Note);

	public const string Name = "maximum-mode";

	public const string FileName = "maximum-mode.json";

	private static readonly string[] Members = new string[6] { "name", "ids", "vendors", "source", "date", "note" };

	private static readonly string[] VendorNames = new string[2] { "amd", "nvidia" };

	private readonly Dictionary<string, List<Entry>> byId = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

	public int Count { get; }

	public static MaximumMode Embedded => field ?? (field = TryParse(ContentFile.Embedded("SCSKiller.Core.Games.maximum-mode.json")));

	public static MaximumMode Current
	{
		get
		{
			return field ?? (field = Embedded);
		}
		set;
	}

	private MaximumMode(List<Entry> entries)
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

	public static MaximumMode? TryParse(string json)
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
			return new MaximumMode(value.EnumerateArray().Select(Read).OfType<Entry>()
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
		if (ContentFile.Text(e.Name, 100))
		{
			string[] ids = e.Ids;
			if (ids != null)
			{
				int num = ids.Length;
				if (num >= 1 && num <= 8 && e.Ids.All(GameVerdicts.IsId))
				{
					ids = e.Vendors;
					if (ids != null)
					{
						num = ids.Length;
						DateOnly result;
						if (num >= 1 && num <= 2 && e.Vendors.All((string v) => VendorNames.Contains(v, StringComparer.Ordinal)) && e.Vendors.Distinct().Count() == e.Vendors.Length && (e.Note == null || ContentFile.Text(e.Note, 120)) && (e.Source == "" || ContentFile.Text(e.Source, 500)))
						{
							return DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
						}
					}
				}
			}
		}
		return false;
	}

	public Suggestion? For(Game game, string? vendor)
	{
		if (vendor != null)
		{
			Entry entry = byId.GetValueOrDefault(game.Id)?.FirstOrDefault((Entry e) => e.Vendors.Contains(vendor, StringComparer.Ordinal));
			if ((object)entry != null)
			{
				return new Suggestion(entry.Note);
			}
		}
		return null;
	}
}
