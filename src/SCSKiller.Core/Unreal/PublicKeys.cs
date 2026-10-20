// Exo Shaders, an unofficial fork based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 The SCSKiller authors
// Copyright (C) 2026 Exo Shaders contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
// version, with the additional permission in LICENSE-EXCEPTION.txt. It is distributed WITHOUT ANY WARRANTY; see LICENSE.
//
// AES keys of encrypted Unreal games from public, community-kept key lists: the fallback for games whose exe the
// static scan can't read the key from (a protected exe, as Wuthering Waves' is). The lists are pinned here per game;
// nothing in a list is trusted beyond being a candidate: UnrealKeys only keeps a key that opens the game's files.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Unreal;

public sealed class PublicKeys(string dataDir)
{
	/// <summary>One game's lists: Names are normalized (letters and digits, lower case) game titles or Unreal project names.</summary>
	public sealed record Source(string Slug, string Title, string[] Names, string[] Urls);

	/// <summary>The pinned lists. Add a game here: its names and the raw URL(s) of its key list, best first.</summary>
	public static readonly IReadOnlyList<Source> Sources = new Source[]
	{
		new Source("wuthering-waves", "Wuthering Waves", new string[2] { "wutheringwaves", "wutheringwavesgame" },
			new string[1] { "https://raw.githubusercontent.com/yarik0chka/wuwa-keys/main/keys.json" }),
	};

	public const string SourceName = "public list";

	public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

	private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);

	private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);

	private const int MaxCandidates = 1024;

	private const int MaxBytes = 4 << 20;

	private static readonly Regex KeyText = new Regex("(?<![0-9A-Fa-f])(?:0x)?([0-9A-Fa-f]{64})(?![0-9A-Fa-f])", RegexOptions.Compiled);

	private static readonly Regex VersionText = new Regex("^[vV]?(\\d+(?:\\.\\d+){0,3})", RegexOptions.Compiled);

	private static readonly HttpClient Http = MakeClient();

	private static readonly ConcurrentDictionary<string, DateTime> Failed = new ConcurrentDictionary<string, DateTime>();

	private static readonly ConcurrentDictionary<string, Task> Refreshing = new ConcurrentDictionary<string, Task>();

	private sealed record Cache(string Source, DateTime Fetched, string ExeStamp, string Hash, List<string> Keys);

	private static HttpClient MakeClient()
	{
		HttpClient httpClient = new HttpClient
		{
			Timeout = FetchTimeout
		};
		httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ExoShaders/1 (+public AES key lists)");
		return httpClient;
	}

	public static Source? For(Game game, IEnumerable<string?>? hints = null)
	{
		HashSet<string> names = new string[1] { game.Name }.Concat(hints ?? Array.Empty<string>()).OfType<string>().Where((string s) => s.Length > 0).Select((string s) => Norm(s.StartsWith("GAME_") ? s.Substring(5) : s)).ToHashSet();
		return Sources.FirstOrDefault((Source s) => s.Names.Any(names.Contains));
	}

	private static string Norm(string s)
	{
		return Regex.Replace(s, "[^A-Za-z0-9]", "").ToLowerInvariant();
	}

	private string CacheFile(Source s)
	{
		return Path.Combine(dataDir, "public-keys", s.Slug + ".json");
	}

	private Cache? Load(Source s)
	{
		try
		{
			return File.Exists(CacheFile(s)) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(CacheFile(s))) : null;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
		{
			return null;
		}
	}

	/// <summary>What the snapshot keys a game's detection on: changes only when the cached list's keys change.</summary>
	public string Stamp(Source s)
	{
		return Load(s)?.Hash ?? "";
	}

	private bool Due(Source s, Cache? c, string exeStamp)
	{
		if (Failed.TryGetValue(s.Slug, out var value) && DateTime.UtcNow - value < RetryAfterFailure)
		{
			return false;
		}
		if (c != null && DateTime.UtcNow - c.Fetched < MaxAge)
		{
			return c.ExeStamp != exeStamp;
		}
		return true;
	}

	/// <summary>Fetches the list in the background when it's due (the snapshot's re-check picks up a changed list by Stamp).</summary>
	public void RefreshInBackground(Source s, string exeStamp)
	{
		if (!Due(s, Load(s), exeStamp) || Refreshing.ContainsKey(s.Slug))
		{
			return;
		}
		Refreshing.TryAdd(s.Slug, Task.Run(delegate
		{
			try
			{
				Fetch(s, exeStamp, out var _);
			}
			finally
			{
				Refreshing.TryRemove(s.Slug, out var _);
			}
		}));
	}

	/// <summary>The candidate keys, newest first: the cache, refetched first when it's a day old or the exe changed.</summary>
	public IReadOnlyList<string> Candidates(Source s, string exeStamp, out string how)
	{
		Cache? c = Load(s);
		if (Due(s, c, exeStamp))
		{
			if (Refreshing.TryGetValue(s.Slug, out var value))
			{
				value.Wait(FetchTimeout + TimeSpan.FromSeconds(2));
				c = Load(s);
			}
			else
			{
				c = Fetch(s, exeStamp, out var why) ?? c;
				if (why != null)
				{
					how = ((c != null) ? $"{why}; using the copy from {c.Fetched:yyyy-MM-dd}" : why);
					return c?.Keys ?? new List<string>();
				}
			}
		}
		if (c == null)
		{
			how = "the public list couldn't be fetched";
			return Array.Empty<string>();
		}
		how = $"{c.Keys.Count} keys, fetched {c.Fetched:yyyy-MM-dd HH:mm} UTC";
		return c.Keys;
	}

	public DateTime? Fetched(Source s)
	{
		return Load(s)?.Fetched;
	}

	private Cache? Fetch(Source s, string exeStamp, out string? why)
	{
		why = null;
		List<string> list = new List<string>();
		string? text = null;
		foreach (string url in s.Urls)
		{
			try
			{
				if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				using HttpResponseMessage httpResponseMessage = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
				httpResponseMessage.EnsureSuccessStatusCode();
				if (httpResponseMessage.Content.Headers.ContentLength > MaxBytes)
				{
					why = "the public list is too big";
					continue;
				}
				byte[] array = httpResponseMessage.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
				if (array.Length > MaxBytes)
				{
					why = "the public list is too big";
					continue;
				}
				foreach (string item in Parse(Encoding.UTF8.GetString(array)))
				{
					if (!list.Contains(item))
					{
						list.Add(item);
					}
				}
				text = text ?? url;
			}
			catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException || ex is IOException)
			{
				why = "the public list couldn't be fetched (" + ((ex is TaskCanceledException) ? "timed out" : ex.Message) + ")";
			}
		}
		if (text == null)
		{
			Failed[s.Slug] = DateTime.UtcNow;
			why = why ?? "no list to fetch";
			return null;
		}
		why = null;
		Failed.TryRemove(s.Slug, out var _);
		list = list.Take(MaxCandidates).ToList();
		string hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join('\n', list)))).Substring(0, 16);
		Cache cache = new Cache(text, DateTime.UtcNow, exeStamp, hash, list);
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(CacheFile(s))!);
			string text2 = CacheFile(s) + ".tmp";
			File.WriteAllText(text2, JsonSerializer.Serialize(cache, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
			File.Move(text2, CacheFile(s), overwrite: true);
		}
		catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException)
		{
		}
		return cache;
	}

	/// <summary>
	/// The keys in a list, newest first: JSON is walked in order, with entries named by a version (an object's
	/// "3.7": ..., or an array of objects with a "version") sorted newest first and a "main" key ahead of its
	/// siblings; anything else is read as text, keys in the order they appear.
	/// </summary>
	public static List<string> Parse(string text)
	{
		List<string> keys = new List<string>();
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(text);
		}
		catch (JsonException)
		{
			node = null;
		}
		if (node != null)
		{
			Walk(node);
		}
		else
		{
			foreach (Match item in KeyText.Matches(text))
			{
				Add(item.Groups[1].Value);
			}
		}
		return keys;
		void Add(string hex)
		{
			string text2 = "0x" + hex.ToUpperInvariant();
			if (!keys.Contains(text2))
			{
				keys.Add(text2);
			}
		}
		void Walk(JsonNode? n)
		{
			if (n is JsonValue jsonValue)
			{
				if (jsonValue.TryGetValue<string>(out var value))
				{
					Match match = KeyText.Match(value.Trim());
					if (match.Success && match.Length == value.Trim().Length)
					{
						Add(match.Groups[1].Value);
					}
				}
			}
			else if (n is JsonArray source)
			{
				foreach (JsonNode? item2 in source.Select((JsonNode? e, int i) => (e: e, i: i)).OrderByDescending(((JsonNode? e, int i) x) => VersionOf((x.e as JsonObject)?["version"]?.ToString())).ThenBy(((JsonNode? e, int i) x) => x.i).Select(((JsonNode? e, int i) x) => x.e))
				{
					Walk(item2);
				}
			}
			else if (n is JsonObject source2)
			{
				foreach (KeyValuePair<string, JsonNode?> item3 in source2.Select((KeyValuePair<string, JsonNode?> p, int i) => (p: p, i: i)).OrderByDescending(((KeyValuePair<string, JsonNode?> p, int i) x) => VersionOf(x.p.Key)).ThenByDescending(((KeyValuePair<string, JsonNode?> p, int i) x) => x.p.Key.Contains("main", StringComparison.OrdinalIgnoreCase))
					.ThenBy(((KeyValuePair<string, JsonNode?> p, int i) x) => x.i)
					.Select(((KeyValuePair<string, JsonNode?> p, int i) x) => x.p))
				{
					Walk(item3.Value);
				}
			}
		}
	}

	private static Version VersionOf(string? s)
	{
		Match? match = ((s == null) ? null : VersionText.Match(s.Trim()));
		if (match == null || !match.Success)
		{
			return new Version(0, 0);
		}
		string text = match.Groups[1].Value;
		if (!text.Contains('.'))
		{
			text += ".0";
		}
		return Version.TryParse(text, out Version? result) ? result : new Version(0, 0);
	}
}
