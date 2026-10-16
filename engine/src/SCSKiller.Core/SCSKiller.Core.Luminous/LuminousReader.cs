using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Luminous;

public sealed class LuminousReader : IEngineReader
{
	public sealed record Loc(string Path, Earc.Entry? Entry, long Offset, int Size);

	public const string Family = "Luminous";

	public const string Version = "EARC 3.20";

	public const string Platform = "PCD3D_SM5";

	public const string Exe = "ffxv_s.exe";

	private const string Datas = "datas";

	private const string Shadergen = "datas\\shader\\shadergen\\autoexternal.earc";

	private const string Suffix = ".sb";

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	internal int Window { get; init; } = 33554432;

	internal int Overlap { get; init; } = 4194304;

	public EngineInfo? Detect(Game game)
	{
		uint? num = Earc.VersionOf(Path.Combine(game.InstallDir, "datas\\shader\\shadergen\\autoexternal.earc"));
		if (num.HasValue)
		{
			uint valueOrDefault = num.GetValueOrDefault();
			string unsupported;
			if (valueOrDefault != 196628)
			{
				unsupported = "an archive version SCSKiller doesn't read";
			}
			else
			{
				unsupported = ((!File.Exists(Path.Combine(game.InstallDir, "ffxv_s.exe"))) ? "a Luminous game SCSKiller doesn't read" : null);
			}
			return new EngineInfo("Luminous", $"EARC {valueOrDefault >> 16}.{valueOrDefault & 0xFFFF}", null, "D3D11", Encrypted: false, unsupported);
		}
		return null;
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		Dictionary<string, ShaderInfo> shaders = new Dictionary<string, ShaderInfo>();
		Dictionary<string, Loc> locs = new Dictionary<string, Loc>();
		List<ShaderMap> maps = new List<ShaderMap>();
		StringBuilder stamp = new StringBuilder();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int unreadable = 0;
		int bad = 0;
		foreach (FileInfo item in Listed(() => Archives(game), "datas"))
		{
			ct.ThrowIfCancellationRequested();
			string relativePath = Path.GetRelativePath(game.InstallDir, item.FullName);
			List<string> map = new List<string>();
			try
			{
				Stamp(item, relativePath);
				using SafeFileHandle h = File.OpenHandle(item.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
				List<Earc.Entry> list = Earc.Entries(h, ".sb");
				if (list == null)
				{
					num3++;
					continue;
				}
				num++;
				foreach (Earc.Entry item2 in list)
				{
					byte[] array = Earc.Read(h, item2);
					if (Dxbc.Valid(array))
					{
						num2++;
						Add(map, array, new Loc(item.FullName, item2, item2.Offset, item2.Size));
					}
				}
			}
			catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				Unreadable(relativePath, ex);
			}
			Map(relativePath, map);
		}
		int carved = 0;
		foreach (FileInfo f in Listed(() => Binaries(game), "the exe and DLLs"))
		{
			ct.ThrowIfCancellationRequested();
			string relativePath2 = Path.GetRelativePath(game.InstallDir, f.FullName);
			List<string> map2 = new List<string>();
			try
			{
				Stamp(f, relativePath2);
				Carve(f.FullName, (long at, byte[] c) =>
				{
					int num4 = carved;
					carved = num4 + 1;
					Add(map2, c, new Loc(f.FullName, null, at, c.Length));
				}, ct);
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
			{
				Unreadable(relativePath2, ex2);
			}
			Map(relativePath2, map2);
		}
		log?.Report($"{num} archives, {num2} shader entries, {carved} containers in the exe and DLLs -> {shaders.Count} shaders (" + string.Join(", ", from s in shaders.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select $"{g.Count()} {g.Key}") + $"), {maps.Count} maps{((num3 > 0) ? $"; {num3} archives of another version left out" : "")}" + $"{((unreadable > 0) ? $"; {unreadable} files that don't read left out" : "")}{((bad > 0) ? $", {bad} unparseable" : "")} ({stopwatch.Elapsed.TotalSeconds:F1}s)");
		if (shaders.Count == 0)
		{
			throw new InvalidDataException("no DirectX 11 shader in datas");
		}
		located[game.Id] = locs;
		return new ShaderIndex(CarvedReader.Sha1Hex(stamp.ToString()), new _003C_003Ez__ReadOnlySingleElementList<string>("PCD3D_SM5"), shaders, maps);
		void Add(List<string> list2, byte[] c, Loc at)
		{
			string text = Convert.ToHexStringLower(SHA1.HashData(c));
			if (locs.TryAdd(text, at))
			{
				try
				{
					ShaderInfo shaderInfo = ShaderContainer.Parse(c, text, new ResourceCounts(0, 0, 0, 0));
					if ((object)shaderInfo != null)
					{
						shaders[text] = shaderInfo with
						{
							Counts = CarvedReader.Counts(shaderInfo.Bindings)
						};
					}
				}
				catch (Exception ex3) when ((ex3 is ArgumentException || ex3 is IndexOutOfRangeException) ? true : false)
				{
					bad++;
				}
			}
			if (shaders.ContainsKey(text))
			{
				list2.Add(text);
			}
		}
		List<FileInfo> Listed(Func<IEnumerable<FileInfo>> files, string what)
		{
			List<FileInfo> list2 = new List<FileInfo>();
			try
			{
				foreach (FileInfo item3 in files())
				{
					list2.Add(item3);
				}
			}
			catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException) ? true : false)
			{
				Unreadable(what, ex3);
			}
			return list2;
		}
		void Map(string rel, List<string> list2)
		{
			if (list2.Count > 0)
			{
				maps.Add(new ShaderMap(CarvedReader.Sha1Hex(rel), rel, "PCD3D_SM5", list2.Distinct().ToList()));
			}
		}
		void Stamp(FileInfo fileInfo, string rel)
		{
			StringBuilder stringBuilder = stamp;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(3, 3, stringBuilder);
			handler.AppendFormatted(rel);
			handler.AppendLiteral("|");
			handler.AppendFormatted(fileInfo.Length);
			handler.AppendLiteral("|");
			handler.AppendFormatted(fileInfo.LastWriteTimeUtc.Ticks);
			handler.AppendLiteral("\n");
			stringBuilder.Append(ref handler);
		}
		void Unreadable(string what, Exception x)
		{
			if (unreadable++ == 0)
			{
				log?.Report("warning: " + what + ": " + x.Message);
			}
		}
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		if (!located.TryGetValue(game.Id, out IReadOnlyDictionary<string, Loc> locs))
		{
			Index(game, engine, null, ct);
			locs = located[game.Id];
		}
		foreach (IGrouping<string, (string, Loc)> item in from s in sha1s.Where(locs.ContainsKey)
			select (Sha: s, At: locs[s]) into x
			group x by x.At.Path)
		{
			SafeFileHandle safeFileHandle;
			try
			{
				safeFileHandle = File.OpenHandle(item.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				continue;
			}
			using (safeFileHandle)
			{
				foreach (var (text, loc) in item.OrderBy(((string Sha, Loc At) x) => x.At.Offset))
				{
					ct.ThrowIfCancellationRequested();
					byte[] array;
					try
					{
						if (loc.Entry != null)
						{
							array = Earc.Read(safeFileHandle, loc.Entry);
						}
						else
						{
							array = new byte[loc.Size];
							if (RandomAccess.Read(safeFileHandle, array, loc.Offset) != array.Length)
							{
								continue;
							}
						}
					}
					catch (Exception ex2) when ((ex2 is InvalidDataException || ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
					{
						continue;
					}
					if (Convert.ToHexStringLower(SHA1.HashData(array)) == text)
					{
						sink(text, array);
					}
				}
			}
		}
	}

	private static IEnumerable<FileInfo> Archives(Game game)
	{
		DirectoryInfo directoryInfo = new DirectoryInfo(Path.Combine(game.InstallDir, "datas"));
		if (directoryInfo.Exists)
		{
			return directoryInfo.EnumerateFiles("*.earc", new EnumerationOptions
			{
				RecurseSubdirectories = true,
				IgnoreInaccessible = true,
				AttributesToSkip = FileAttributes.None
			}).OrderBy((FileInfo f) => f.FullName, StringComparer.OrdinalIgnoreCase);
		}
		return Array.Empty<FileInfo>();
	}

	private static IEnumerable<FileInfo> Binaries(Game game)
	{
		EnumerationOptions enumerationOptions = new EnumerationOptions
		{
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None
		};
		DirectoryInfo directoryInfo = new DirectoryInfo(game.InstallDir);
		DirectoryInfo directoryInfo2 = new DirectoryInfo(Path.Combine(game.InstallDir, "HairWorks"));
		IEnumerable<FileInfo> first = directoryInfo.EnumerateFiles("*.exe", enumerationOptions).Concat(directoryInfo.EnumerateFiles("*.dll", enumerationOptions));
		IEnumerable<FileInfo> second;
		if (!directoryInfo2.Exists)
		{
			IEnumerable<FileInfo> enumerable = Array.Empty<FileInfo>();
			second = enumerable;
		}
		else
		{
			second = directoryInfo2.EnumerateFiles("*.bin", enumerationOptions);
		}
		return first.Concat(second).OrderBy((FileInfo f) => f.FullName, StringComparer.OrdinalIgnoreCase);
	}

	internal void Carve(string path, Action<long, byte[]> found, CancellationToken ct)
	{
		using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
		long length = RandomAccess.GetLength(handle);
		byte[] array = new byte[(int)Math.Min(Window, length)];
		for (long num = 0L; num < length; num += Window - Overlap)
		{
			ct.ThrowIfCancellationRequested();
			int num2 = RandomAccess.Read(handle, array, num);
			byte[] b = ((num2 == array.Length) ? array : array[..num2]);
			bool flag = num + num2 >= length;
			foreach (var (num3, arg) in Dxbc.Containers(b))
			{
				if (flag || num3 < Window - Overlap)
				{
					found(num + num3, arg);
				}
			}
			if (flag)
			{
				break;
			}
		}
	}
}
