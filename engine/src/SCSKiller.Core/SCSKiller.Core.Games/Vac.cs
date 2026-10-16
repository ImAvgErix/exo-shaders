using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SCSKiller.Core.Games;

public static class Vac
{
	private sealed record SteamApp(bool? Vac, string[] LaunchExes);

	public static readonly IReadOnlySet<uint> Valve = new HashSet<uint>
	{
		10u, 80u, 240u, 300u, 320u, 440u, 500u, 550u, 570u, 730u,
		4000u, 1422450u
	};

	private static readonly TimeSpan RetryFailed = TimeSpan.FromSeconds(30L);

	private static readonly object Gate = new object();

	private static (string Path, string Stamp, Dictionary<uint, SteamApp>? Apps, string? Error, Stopwatch Age)? _cache;

	public static uint? AppId(Game g, string steamRoot, out bool steam)
	{
		steam = true;
		if (g.Id.StartsWith("steam:", StringComparison.Ordinal))
		{
			return SteamSource.AppId(g.Id);
		}
		string[] array = new string[2] { g.InstallDir, g.ExePath };
		foreach (string text in array)
		{
			try
			{
				(uint?, bool) tuple = SteamSource.AppAt(steamRoot, text);
				if (tuple.Item2)
				{
					return tuple.Item1;
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) ? true : false)
			{
				if (text.Contains("\\steamapps\\", StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}
			}
		}
		steam = false;
		return null;
	}

	public static bool Is(Game g, string? appinfo = null, string? steamRoot = null)
	{
		string text = steamRoot ?? SteamSource.Root();
		uint? num = AppId(g, text, out var steam);
		if (!steam)
		{
			return false;
		}
		if (num.HasValue)
		{
			uint valueOrDefault = num.GetValueOrDefault();
			if (Valve.Contains(valueOrDefault))
			{
				return true;
			}
			Dictionary<uint, SteamApp> dictionary = Apps(appinfo ?? SteamSource.AppInfoPath(text));
			if (dictionary != null)
			{
				SteamApp? valueOrDefault2 = dictionary.GetValueOrDefault(valueOrDefault);
				if ((object)valueOrDefault2 == null)
				{
					return true;
				}
				return valueOrDefault2.Vac != false;
			}
			return true;
		}
		return true;
	}

	public static IEnumerable<string> LaunchExes(Game g, string? steamRoot = null)
	{
		string text = steamRoot ?? SteamSource.Root();
		uint? num = AppId(g, text, out var _);
		if (num.HasValue)
		{
			uint valueOrDefault = num.GetValueOrDefault();
			SteamApp steamApp = Apps(SteamSource.AppInfoPath(text))?.GetValueOrDefault(valueOrDefault);
			if ((object)steamApp != null)
			{
				return steamApp.LaunchExes;
			}
		}
		return Array.Empty<string>();
	}

	public static string? ReadError(string? steamRoot = null)
	{
		string text = SteamSource.AppInfoPath(steamRoot ?? SteamSource.Root());
		if (text.Length == 0)
		{
			return "appinfo.vdf: STEAM_COMPAT_CLIENT_INSTALL_PATH has no Windows path";
		}
		lock (Gate)
		{
			object result;
			if (Apps(text) == null)
			{
				(string, string, Dictionary<uint, SteamApp>, string, Stopwatch)? cache = _cache;
				if (cache.HasValue)
				{
					(string, string, Dictionary<uint, SteamApp>, string, Stopwatch) valueOrDefault = cache.GetValueOrDefault();
					string item = valueOrDefault.Item4;
					if (item != null && valueOrDefault.Item1 == text)
					{
						result = text + ": " + item;
						goto IL_0078;
					}
				}
			}
			result = null;
			goto IL_0078;
			IL_0078:
			return (string?)result;
		}
	}

	private static Dictionary<uint, SteamApp>? Apps(string path)
	{
		if (path.Length == 0)
		{
			return null;
		}
		FileInfo fileInfo = new FileInfo(path);
		string text = (fileInfo.Exists ? $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}" : "");
		lock (Gate)
		{
			(string, string, Dictionary<uint, SteamApp>, string, Stopwatch)? cache = _cache;
			if (cache.HasValue)
			{
				(string, string, Dictionary<uint, SteamApp>, string, Stopwatch) valueOrDefault = cache.GetValueOrDefault();
				if (valueOrDefault.Item1 == path && valueOrDefault.Item2 == text && (valueOrDefault.Item3 != null || valueOrDefault.Item5.Elapsed < RetryFailed))
				{
					return valueOrDefault.Item3;
				}
			}
			Dictionary<uint, uint> states = new Dictionary<uint, uint>();
			string error = (fileInfo.Exists ? null : "missing");
			Dictionary<uint, SteamApp> dictionary = (fileInfo.Exists ? SteamSource.Apps(path, null, states, (string e) =>
			{
				error = e;
			}) : null)?.ToDictionary((KeyValuePair<uint, Dictionary<string, object>> a) => a.Key, (KeyValuePair<uint, Dictionary<string, object>> a) => new SteamApp(Verdict(a.Value, states.GetValueOrDefault(a.Key)), LaunchExes(a.Value)));
			_cache = (path, text, dictionary, (dictionary == null) ? (error ?? "unreadable") : null, Stopwatch.StartNew());
			return dictionary;
		}
	}

	private static bool? Verdict(Dictionary<string, object> app, uint state)
	{
		if (app.GetValueOrDefault("common") is Dictionary<string, object> dictionary)
		{
			if (!(dictionary.GetValueOrDefault("category") is Dictionary<string, object> dictionary2))
			{
				if (state != 2)
				{
					return null;
				}
				return false;
			}
			return dictionary2.ContainsKey("category_8");
		}
		return null;
	}

	private static string[] LaunchExes(Dictionary<string, object> app)
	{
		if (!(app.GetValueOrDefault("config") is Dictionary<string, object> dictionary) || !(dictionary.GetValueOrDefault("launch") is Dictionary<string, object> dictionary2))
		{
			return Array.Empty<string>();
		}
		return (from e in (from l in dictionary2.Values.OfType<Dictionary<string, object>>()
				select l.GetValueOrDefault("executable")).OfType<string>()
			select e.Split('\\', '/')[^1] into e
			where e.Length > 0
			select e).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	}
}
