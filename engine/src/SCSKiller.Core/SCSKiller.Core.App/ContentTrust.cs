using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using NSec.Cryptography;

namespace SCSKiller.Core.App;

public sealed class ContentTrust(AppStore store, IReadOnlyDictionary<string, string> keys, string? only = null, int maxBytes = 65536)
{
	private sealed record Envelope(int V, string Name, [property: JsonPropertyName("signed_at")] string SignedAt, string Sha256, string Kid, string Sig, string Body);

	public static readonly IReadOnlyDictionary<string, string> RulesKeys = new Dictionary<string, string>
	{
		["rules-a"] = "Hm0YVaMre3b7eyz2Y9W6t2QWGFtFkVQXkPgg2968TNk=",
		["rules-b"] = "PAx76G91Ugb7A2B3CFaGzrYawhMtd5Nsuj1ewy/IZiM="
	};

	public static readonly IReadOnlyDictionary<string, string> CompatKeys = new Dictionary<string, string>();

	private static readonly SignatureAlgorithm Ed = SignatureAlgorithm.Ed25519;

	private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

	public static readonly string[] Channels = new string[2] { "alpha", "beta" };

	public string? Rejected { get; private set; }

	public static byte[] Message(string name, string signedAt, string sha256)
	{
		return Encoding.UTF8.GetBytes($"scskiller-content-v1\n{name}\n{signedAt}\n{sha256}");
	}

	public static string Sign(ReadOnlySpan<byte> seed, string kid, string name, string body, DateTimeOffset signedAt)
	{
		using Key key = Key.Import(Ed, seed, KeyBlobFormat.RawPrivateKey, default(KeyCreationParameters));
		string text = signedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
		string sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
		return JsonSerializer.Serialize(new
		{
			v = 1,
			name = name,
			signed_at = text,
			sha256 = sha,
			kid = kid,
			sig = Convert.ToBase64String(Ed.Sign(key, Message(name, text, sha))),
			body = body
		});
	}

	public T? Accept<T>(string name, string json, Func<string, T?> parse, string? key = null) where T : class
	{
		string text = (Rejected = Check(name, json, out string body, out DateTimeOffset at));
		if (text != null)
		{
			return null;
		}
		T val = parse(body);
		if (val == null)
		{
			Rejected = "the body isn't valid";
			return null;
		}
		if (key == null)
		{
			key = name;
		}
		lock (store)
		{
			Dictionary<string, DateTimeOffset> dictionary = store.LoadContentTimes();
			if (dictionary.TryGetValue(key, out var value) && at < value)
			{
				Rejected = $"older than one already seen ({value:u})";
				return null;
			}
			dictionary[key] = at;
			store.SaveContentTimes(dictionary);
			return val;
		}
	}

	public static string ChannelKey(string name, string? channel)
	{
		if (channel != null)
		{
			return name + "@" + channel;
		}
		return name;
	}

	public static string CacheFile(string cacheFile, string? channel)
	{
		if (channel != null)
		{
			return Path.Combine(Path.GetDirectoryName(cacheFile) ?? "", Path.GetFileNameWithoutExtension(cacheFile) + "@" + channel + ".json");
		}
		return cacheFile;
	}

	private static bool ChannelFirst(string name)
	{
		if (name == "game-verdicts" || name == "maximum-mode")
		{
			return true;
		}
		return false;
	}

	public T? Cached<T>(string name, Func<string, T?> parse, string cacheFile, IReadOnlyCollection<string>? channels) where T : class
	{
		T val = null;
		(T, string)? tuple = null;
		foreach (string item in Channels.Prepend(null))
		{
			string path = CacheFile(cacheFile, item);
			if (item != null && channels != null && !channels.Contains(item))
			{
				try
				{
					File.Delete(path);
				}
				catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
				}
				continue;
			}
			string json;
			try
			{
				json = File.ReadAllText(path);
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
			{
				continue;
			}
			T val2 = Accept(name, json, parse, ChannelKey(name, item));
			if (val2 == null)
			{
				continue;
			}
			if (item == null && ChannelFirst(name))
			{
				val = val2;
				continue;
			}
			string signedAt = ContentFile.Read<Envelope>(json, maxBytes).SignedAt;
			if (!tuple.HasValue || string.CompareOrdinal(signedAt, tuple.Value.Item2) > 0)
			{
				tuple = (val2, signedAt);
			}
		}
		return (tuple.HasValue ? tuple.GetValueOrDefault().Item1 : null) ?? val;
	}

	public async Task<T?> FetchAsync<T>(string name, Func<string, T?> parse, string cacheFile, IReadOnlyCollection<string>? channels, string? token, RouteFailover? routes, TimeSpan? budget = null) where T : class
	{
		if (routes == null)
		{
			routes = RouteFailover.Default;
		}
		TimeSpan wait = budget ?? TimeSpan.FromSeconds(3L);
		string[] first = new string[1];
		IEnumerable<string> second;
		if (token != null)
		{
			second = (channels ?? Array.Empty<string>()).Where(((IEnumerable<string>)Channels).Contains<string>);
		}
		else
		{
			IEnumerable<string> enumerable = Array.Empty<string>();
			second = enumerable;
		}
		string[] from = first.Concat(second).ToArray();
		(string Body, HttpStatusCode? Status)[] served = await Task.WhenAll(from.Select((string c) => routes.GetContentStatusAsync((c == null) ? ("v1/content/" + name + ".json") : UpdateFeeds.EdgePath(c, name + ".json"), wait, 65536, (c == null) ? null : token)));
		for (int num = 0; num < from.Length; num++)
		{
			string item = served[num].Body;
			if (item != null && Accept(name, item, parse, ChannelKey(name, from[num])) != null)
			{
				ContentFile.Keep(CacheFile(cacheFile, from[num]), item);
			}
		}
		if (!ChannelFirst(name))
		{
			return Cached(name, parse, cacheFile, channels);
		}
		IEnumerable<string> second2 = from.Where((string _, int i) => served[i].Status == HttpStatusCode.NotFound).OfType<string>();
		return Cached(name, parse, cacheFile, channels?.Except(second2).ToArray());
	}

	private string? Check(string name, string json, out string body, out DateTimeOffset at)
	{
		body = "";
		at = default;
		if (only != null && name != only)
		{
			return "these keys don't sign '" + name + "'";
		}
		Envelope envelope = ContentFile.Read<Envelope>(json, maxBytes);
		if ((object)envelope == null)
		{
			return "malformed envelope";
		}
		if (envelope.V != 1)
		{
			return "unknown envelope version";
		}
		if (envelope.Name != name)
		{
			return "signed for '" + envelope.Name + "'";
		}
		if (!keys.TryGetValue(envelope.Kid, out string value))
		{
			return "unknown key '" + envelope.Kid + "'";
		}
		byte[] array;
		try
		{
			array = Convert.FromBase64String(envelope.Sig);
		}
		catch (FormatException)
		{
			return "malformed signature";
		}
		if (!PublicKey.TryImport(Ed, Convert.FromBase64String(value), KeyBlobFormat.RawPublicKey, out PublicKey result) || !Ed.Verify(result, Message(name, envelope.SignedAt, envelope.Sha256), array))
		{
			return "bad signature";
		}
		if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Body))), envelope.Sha256, StringComparison.Ordinal))
		{
			return "the body doesn't match its signature";
		}
		if (!DateTimeOffset.TryParseExact(envelope.SignedAt, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at))
		{
			return "malformed signed_at";
		}
		body = envelope.Body;
		return null;
	}
}
