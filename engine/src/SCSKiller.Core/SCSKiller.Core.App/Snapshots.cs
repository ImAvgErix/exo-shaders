using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SCSKiller.Core.App;

public static class Snapshots
{
	public const int Keep = 20;

	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(AppStore.Json)
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false
	};

	private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5L);

	public static SessionSnapshot Of(FrameReport r, PlayWindow? played, string? driverId, string? driverVersion, string? build, DateTimeOffset? warmedAt)
	{
		FrameLog.LaunchContext launchContext = FrameLog.ContextOf(r);
		int num;
		if ((object)played != null)
		{
			if (played.From.ToUnixTimeMilliseconds() <= r.LaunchUnixMs)
			{
				num = ((r.LaunchUnixMs <= played.To.ToUnixTimeMilliseconds()) ? 1 : 0);
				goto IL_003e;
			}
		}
		num = 0;
		goto IL_003e;
		IL_003e:
		bool flag = (byte)num != 0;
		long launchUnixMs = r.LaunchUnixMs;
		double totalMilliseconds = r.Duration.TotalMilliseconds;
		double totalMilliseconds2 = r.Startup.TotalMilliseconds;
		long frames = r.Frames;
		double low1PctFps = r.Low1PctFps;
		double playSeconds = r.PlaySeconds;
		double avgFps = r.AvgFps;
		IReadOnlyList<Hitch> hitches = r.Hitches;
		IReadOnlyList<float> peaks = r.Peaks;
		CreateStats? creates = r.Creates;
		string? driverId2 = driverId;
		string? driverVersion2 = driverVersion;
		string? build2 = build;
		DateTimeOffset? warmedAt2 = warmedAt;
		string? swapChain = launchContext?.SwapChain;
		bool cleanExit = (launchContext?.Ended ?? false) | flag;
		bool overlay = launchContext?.Overlay ?? false;
		bool d3D = launchContext?.D3D11 ?? false;
		bool? frameGen;
		if (r.DisplayedFrames > 0)
		{
			frameGen = true;
		}
		else
		{
			frameGen = (((object)launchContext != null && launchContext.FrameGen) ? ((bool?)null) : new bool?(false));
		}
		return new SessionSnapshot(1, launchUnixMs, totalMilliseconds, totalMilliseconds2, frames, low1PctFps, playSeconds, avgFps, hitches, peaks, creates, driverId2, driverVersion2, build2, warmedAt2, swapChain, cleanExit, overlay, Pinned: false, null, d3D, frameGen);
	}

	public static void Write(AppStore store, string gameId, SessionSnapshot s)
	{
		string dir = Dir(store, gameId);
		string path = PathOf(dir, s.LaunchUnixMs);
		if (Same(Load(path), s))
		{
			return;
		}
		AppStore.Locked(dir, () =>
		{
			SessionSnapshot sessionSnapshot = Load(path);
			if (!Same(sessionSnapshot, s))
			{
				if (sessionSnapshot != null)
				{
					s = s with
					{
						Pinned = sessionSnapshot.Pinned,
						Tag = sessionSnapshot.Tag
					};
				}
				Save(path, s with
				{
					V = 1
				});
				Prune(dir);
			}
		}, LockWait);
	}

	private static bool Same(SessionSnapshot? old, SessionSnapshot s)
	{
		if (old != null && old.DurationMs == s.DurationMs && old.Frames == s.Frames)
		{
			return old.CleanExit == s.CleanExit;
		}
		return false;
	}

	public static SessionSnapshot? Read(AppStore store, string gameId, long launchUnixMs)
	{
		return Load(PathOf(Dir(store, gameId), launchUnixMs));
	}

	public static IReadOnlyList<SessionSnapshot> List(AppStore store, string gameId)
	{
		List<SessionSnapshot> list = new List<SessionSnapshot>();
		list.AddRange((from f in Files(Dir(store, gameId))
			select Load(f.Path)).OfType<SessionSnapshot>());
		return new _003C_003Ez__ReadOnlyList<SessionSnapshot>(list);
	}

	public static bool Pin(AppStore store, string gameId, long launchUnixMs, string? tag)
	{
		string dir = Dir(store, gameId);
		bool found = false;
		AppStore.Locked(dir, () =>
		{
			string path = PathOf(dir, launchUnixMs);
			SessionSnapshot sessionSnapshot = Load(path);
			if ((object)sessionSnapshot != null)
			{
				found = true;
				Save(path, sessionSnapshot with
				{
					Pinned = (tag != null),
					Tag = tag
				});
				if (tag == null)
				{
					Prune(dir);
				}
			}
		}, LockWait);
		return found;
	}

	private static string Dir(AppStore store, string gameId)
	{
		return Path.Combine(store.GameDir(gameId), "sessions");
	}

	private static string PathOf(string dir, long launchUnixMs)
	{
		return Path.Combine(dir, launchUnixMs.ToString(CultureInfo.InvariantCulture) + ".json");
	}

	private static IEnumerable<(long Launch, string Path)> Files(string dir)
	{
		if (Directory.Exists(dir))
		{
			return from f in Directory.EnumerateFiles(dir, "*.json")
				select (Ok: long.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result), Launch: result, Path: f) into x
				where x.Ok
				select (Launch: x.Launch, Path: x.Path) into x
				orderby x.Launch descending
				select x;
		}
		return Array.Empty<(long, string)>();
	}

	private static void Prune(string dir)
	{
		List<(long, string)> list = Files(dir).ToList();
		if (list.Count <= 20)
		{
			return;
		}
		foreach (var item in list.Where(((long Launch, string Path) f) => !(Load(f.Path)?.Pinned ?? false)).Skip(20))
		{
			File.Delete(item.Item2);
		}
	}

	private static SessionSnapshot? Load(string path)
	{
		try
		{
			using FileStream utf8Json = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			SessionSnapshot sessionSnapshot = JsonSerializer.Deserialize<SessionSnapshot>(utf8Json, Json);
			return ((object)sessionSnapshot != null && sessionSnapshot.V == 1) ? sessionSnapshot : null;
		}
		catch (Exception ex) when ((ex is FileNotFoundException || ex is DirectoryNotFoundException || ex is JsonException) ? true : false)
		{
			return null;
		}
	}

	private static void Save(string path, SessionSnapshot s)
	{
		AppStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(s, Json));
	}
}
