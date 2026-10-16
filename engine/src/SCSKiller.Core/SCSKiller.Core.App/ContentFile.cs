using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public static class ContentFile
{
	public const int MaxBytes = 65536;

	private static readonly JsonSerializerOptions Strict = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		RespectNullableAnnotations = true,
		RespectRequiredConstructorParameters = true
	};

	public static T? Read<T>(string json, int maxBytes = 65536) where T : class
	{
		if (Encoding.UTF8.GetByteCount(json) > maxBytes)
		{
			return null;
		}
		try
		{
			return JsonSerializer.Deserialize<T>(json, Strict);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public static T? Read<T>(JsonElement e) where T : class
	{
		try
		{
			return e.Deserialize<T>(Strict);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public static bool Text(string? s, int max)
	{
		if (!string.IsNullOrWhiteSpace(s) && s.Length <= max)
		{
			return !s.Any(char.IsControl);
		}
		return false;
	}

	public static string Embedded(string resource)
	{
		return new StreamReader(typeof(ContentFile).Assembly.GetManifestResourceStream(resource)).ReadToEnd();
	}

	public static T? Cached<T>(string file, Func<string, T?> parse) where T : class
	{
		try
		{
			return parse(File.ReadAllText(file));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public static async Task<T?> FetchAsync<T>(string path, string cacheFile, Func<string, T?> parse, RouteFailover? routes = null, TimeSpan? budget = null) where T : class
	{
		string text = await (routes ?? RouteFailover.Default).GetContentAsync(path, budget ?? TimeSpan.FromSeconds(3L), 65536);
		if (text != null)
		{
			T val = parse(text);
			if (val != null)
			{
				Keep(cacheFile, text);
				return val;
			}
		}
		return null;
	}

	public static void Keep(string cacheFile, string json)
	{
		try
		{
			string text = cacheFile + ".tmp";
			File.WriteAllText(text, json);
			File.Move(text, cacheFile, overwrite: true);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
	}
}
