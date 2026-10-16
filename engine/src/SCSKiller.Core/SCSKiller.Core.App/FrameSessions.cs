using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SCSKiller.Core.App;

public static class FrameSessions
{
	public const int Keep = 20;

	private const string FileName = "frame-history.json";

	private static readonly long MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

	public static FrameSession Of(FrameReport r)
	{
		return new FrameSession(r.LaunchUnixMs, r.Duration, r.Low1PctFps, r.Hitches.Count((Hitch h) => h.Cause == HitchCause.Shader));
	}

	public static IReadOnlyList<FrameSession> Read(AppStore store, string gameId)
	{
		return Load(PathOf(store, gameId));
	}

	public static bool Note(AppStore store, string gameId, FrameReport r)
	{
		if (r.LaunchUnixMs <= 0)
		{
			return false;
		}
		FrameSession s = Of(r);
		string path = PathOf(store, gameId);
		bool changed = false;
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		AppStore.Locked(path, () =>
		{
			List<FrameSession> list = Load(path);
			if (!list.Contains(s))
			{
				list = (from e in list.Where((FrameSession e) => e.LaunchUnixMs != s.LaunchUnixMs).Append(s)
					orderby e.LaunchUnixMs descending
					select e).Take(20).ToList();
				AppStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(list, AppStore.Json));
				changed = true;
			}
		}, TimeSpan.FromSeconds(5L));
		return changed;
	}

	private static string PathOf(AppStore store, string gameId)
	{
		return Path.Combine(store.GameDir(gameId), "frame-history.json");
	}

	private static List<FrameSession> Load(string path)
	{
		try
		{
			using FileStream utf8Json = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			return (from e in (JsonSerializer.Deserialize<List<FrameSession>>(utf8Json, AppStore.Json) ?? new List<FrameSession>()).OfType<FrameSession>()
				where e.LaunchUnixMs > 0 && e.LaunchUnixMs <= MaxUnixMs && e.Duration >= TimeSpan.Zero
				select e).ToList();
		}
		catch (Exception ex) when ((ex is FileNotFoundException || ex is DirectoryNotFoundException || ex is JsonException || ex is UnauthorizedAccessException) ? true : false)
		{
			return new List<FrameSession>();
		}
	}
}
