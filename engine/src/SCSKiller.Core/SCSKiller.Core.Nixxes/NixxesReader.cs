using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.NaughtyDog;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Nixxes;

public sealed class NixxesReader : IEngineReader
{
	public sealed record Loc(string Path, int Entry, long Offset, int Size);

	public sealed record Readable(long Length, ReadAt Read);

	public delegate void ReadAt(long from, Span<byte> into);

	public delegate void Found(long offset, ReadOnlySpan<byte> container);

	private sealed record SourceFile(string Path, int Kind, string Library);

	public sealed class Meter(long limit)
	{
		public long Read { get; private set; }

		public long Left => limit - Read;

		public bool Take(long n)
		{
			if (n > Left)
			{
				return false;
			}
			Read += n;
			return true;
		}
	}

	public const string Family = "Nixxes";

	public const string Version = "DSAR";

	public const string SuckerPunch = "Sucker Punch";

	public const string Insomniac = "Insomniac";

	public const string Stingray = "Stingray";

	private const long DetectBudget = 67108864L;

	private const int Window = 16777216;

	private const int Whole = -1;

	private const int Plain = -2;

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	private const int Archive = 0;

	public static string? Layout(string installDir)
	{
		if (Directory.Exists(Path.Combine(installDir, "cache_pc", "psarc")))
		{
			return "Sucker Punch";
		}
		if (File.Exists(Path.Combine(installDir, "toc")) && File.Exists(Path.Combine(installDir, "d", "materialgraph")))
		{
			return "Insomniac";
		}
		if (AssetArchives(installDir).Any())
		{
			return "Insomniac";
		}
		if (Files(Path.Combine(installDir, "data"), "bundles.*.nxa").Any())
		{
			return "Stingray";
		}
		return null;
	}

	public EngineInfo? Detect(Game game)
	{
		if (Directory.Exists(game.InstallDir))
		{
			string text = Layout(game.InstallDir);
			if (text != null)
			{
				string version = game.Version;
				EngineInfo result = new EngineInfo("Nixxes", "DSAR", text, "D3D12", Encrypted: false, null, NoRtPipelines: false, NoRayTracing: false, AftermathShaderDebug: false, VersionGuessed: false, ApiGuessed: false, (version != null) ? (game.Id + "@" + version) : game.Id);
				Meter meter = new Meter(67108864L);
				if (text == "Stingray")
				{
					if (!Files(game, text, detect: true).Any((SourceFile f) =>
					{
						using Dsar dsar = Dsar.Open(f.Path, meter);
						return dsar != null;
					}))
					{
						return null;
					}
					return result;
				}
				bool found = false;
				byte[] buf = new byte[16777216];
				foreach (SourceFile item in Files(game, text, detect: true))
				{
					Each(item, game.InstallDir, detect: true, meter, (int _, string _, Readable src) =>
					{
						try
						{
							Carve(src, buf, (long num, ReadOnlySpan<byte> c) =>
							{
								found |= Dxbc.Kind(c) >= 0;
							}, meter, () => found, CancellationToken.None);
						}
						catch (InvalidDataException)
						{
						}
						return !found && meter.Left > 0;
					});
					if (found)
					{
						return result;
					}
					if (meter.Left <= 0)
					{
						break;
					}
				}
				return null;
			}
		}
		return null;
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		string text = engine.Fork ?? Layout(game.InstallDir) ?? throw new InvalidDataException("not a Nixxes DSAR install: scan the game again");
		Dictionary<string, ShaderInfo> shaders = new Dictionary<string, ShaderInfo>();
		Dictionary<string, Loc> locs = new Dictionary<string, Loc>();
		Dictionary<string, string> platformOf = new Dictionary<string, string>();
		Dictionary<string, string> twinOf = new Dictionary<string, string>();
		HashSet<string> dxbcSeen = new HashSet<string>();
		List<(string Library, List<string> Shaders)> pools = new List<(string, List<string>)>();
		int containers = 0;
		int carryRs = 0;
		int bad = 0;
		int unreadable = 0;
		Meter meter = new Meter(long.MaxValue);
		byte[] buf = new byte[16777216];
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
		foreach (SourceFile f in Files(game, text, detect: false))
		{
			ct.ThrowIfCancellationRequested();
			FileInfo fileInfo = new FileInfo(f.Path);
			if (fileInfo != null && fileInfo.Exists)
			{
				incrementalHash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(game.InstallDir, f.Path)}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}\n"));
			}
			if (!Each(f, game.InstallDir, detect: false, meter, (int entry, string library, Readable src) =>
			{
				List<string> pool = new List<string>();
				(long At, int Size, int Kind, string Sha, (int, int)? Pair)? prev = null;
				try
				{
					Carve(src, buf, (long at, ReadOnlySpan<byte> c) =>
					{
						int num3 = containers;
						containers = num3 + 1;
						string text3 = Convert.ToHexStringLower(SHA1.HashData(c));
						num3 = Dxbc.Kind(c);
						bool flag = !Dxbc.Part(c, "DXIL"u8).IsEmpty;
						int num4 = num3;
						if (!flag)
						{
							dxbcSeen.Add(text3);
						}
						else if (prev.HasValue)
						{
							(long, int, int, string, (int, int)?) valueOrDefault = prev.GetValueOrDefault();
							(int, int)? item = valueOrDefault.Item5;
							if (item.HasValue)
							{
								(int, int) valueOrDefault2 = item.GetValueOrDefault();
								if (valueOrDefault.Item1 + valueOrDefault.Item2 == at && valueOrDefault.Item3 == num4)
								{
									(int, int) tuple2 = valueOrDefault2;
									num3 = valueOrDefault.Item2;
									int length = c.Length;
									if (tuple2.Item1 == num3 && tuple2.Item2 == length)
									{
										twinOf.TryAdd(valueOrDefault.Item4, text3);
									}
								}
							}
						}
						prev = (at, c.Length, num4, text3, flag ? (((int, int)?)null) : Twin(src, at, meter));
						if (locs.TryAdd(text3, new Loc(f.Path, entry, at, c.Length)))
						{
							try
							{
								ShaderInfo shaderInfo = ShaderContainer.Parse(c, text3, new ResourceCounts(0, 0, 0, 0));
								if ((object)shaderInfo != null)
								{
									shaders[text3] = shaderInfo with
									{
										Counts = CarvedReader.Counts(shaderInfo.Bindings)
									};
									string text4 = CarvedReader.LanePlatform(Dxbc.WaveLanes(c));
									if (text4 != null)
									{
										platformOf[text3] = text4;
									}
									if (!Dxbc.Part(c, "RTS0"u8).IsEmpty)
									{
										int length = carryRs;
										carryRs = length + 1;
									}
								}
							}
							catch (Exception ex2) when ((ex2 is ArgumentException || ex2 is IndexOutOfRangeException) ? true : false)
							{
								int length = bad;
								bad = length + 1;
							}
						}
						if (shaders.ContainsKey(text3))
						{
							pool.Add(text3);
						}
					}, meter, () => false, ct);
				}
				catch (InvalidDataException)
				{
					int num2 = unreadable;
					unreadable = num2 + 1;
				}
				if (pool.Count > 0)
				{
					pools.Add((library, pool));
				}
				return true;
			}))
			{
				int num = unreadable;
				unreadable = num + 1;
			}
		}
		List<string> list = (from t in twinOf
			where shaders.ContainsKey(t.Key) && shaders.ContainsKey(t.Value)
			select t.Key).ToList();
		foreach (string item2 in list)
		{
			shaders.Remove(item2);
		}
		int value = dxbcSeen.Count(shaders.ContainsKey);
		if (shaders.Count == 0)
		{
			throw new InvalidDataException(text + ": no shader in its archives: scan the game again");
		}
		List<ShaderMap> list2 = new List<ShaderMap>();
		foreach (var item3 in pools)
		{
			var (text2, _) = item3;
			foreach (IGrouping<string, string> item4 in from h in item3.Shaders.Where(shaders.ContainsKey).Distinct()
				group h by platformOf.GetValueOrDefault(h, "D3D12"))
			{
				list2.Add(new ShaderMap(CarvedReader.Sha1Hex((item4.Key == "D3D12") ? text2 : (text2 + "|" + item4.Key)), text2, item4.Key, item4.ToList()));
			}
		}
		located[game.Id] = locs;
		List<IGrouping<string, KeyValuePair<string, string>>> source = (from p in platformOf
			where shaders.ContainsKey(p.Key)
			group p by p.Value).ToList();
		log?.Report(string.Concat(new string[9]
		{
			$"{text}: {list2.Count} pools ({(double)meter.Read / 1000000000.0:F2} GB decompressed): {containers} shader containers, {shaders.Count} shaders (",
			string.Join(", ", from s in shaders.Values
				group s by s.Stage into g
				orderby g.Count() descending
				select $"{g.Count()} {g.Key}"),
			$"), {shaders.Values.Count((ShaderInfo s) => s.Stage == Stage.Library)} ray tracing libraries, {shaders.Values.Count((ShaderInfo s) => s.InlineRayTracing)} tracing rays inline",
			(list.Count > 0) ? $"; {list.Count} DXBC twins of DXIL shaders left out, {value} DXBC shaders without one kept" : "",
			$"; {carryRs} containers carry a root signature (not used)",
			(unreadable > 0) ? $"; warning: {unreadable} sources don't decompress (the shaders before the failing bytes are kept)" : "",
			(bad > 0) ? $"; {bad} unparseable" : "",
			string.Concat(source.Select((IGrouping<string, KeyValuePair<string, string>> g) => $"; {g.Count()} need {g.Key}, not planned for 32-lane GPUs")),
			$" ({stopwatch.Elapsed.TotalSeconds:F1}s)"
		}));
		string contentHash = Convert.ToHexStringLower(incrementalHash.GetHashAndReset());
		List<string> list3 = new List<string>();
		list3.Add("D3D12");
		list3.AddRange(source.Select((IGrouping<string, KeyValuePair<string, string>> g) => g.Key).Order(StringComparer.Ordinal));
		return new ShaderIndex(contentHash, new _003C_003Ez__ReadOnlyList<string>(list3), shaders, list2);
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		if (!located.TryGetValue(game.Id, out IReadOnlyDictionary<string, Loc> locs))
		{
			Index(game, engine, null, ct);
			locs = located[game.Id];
		}
		bool? flag = null;
		foreach (IGrouping<string, (string, Loc)> item in from s in sha1s.Where(locs.ContainsKey)
			select (Sha: s, At: locs[s]) into x
			group x by x.At.Path)
		{
			Dictionary<int, List<(string Sha, Loc At)>> byEntry = item.GroupBy(((string Sha, Loc At) x) => x.At.Entry).ToDictionary((IGrouping<int, (string Sha, Loc At)> g) => g.Key, (IGrouping<int, (string Sha, Loc At)> g) => g.OrderBy(((string Sha, Loc At) x) => x.At.Offset).ToList());
			int num = ((byEntry.Keys.Max() < 0) ? byEntry.Keys.First() : 0);
			if (num == -2)
			{
				bool valueOrDefault = flag == true;
				bool flag2;
				if (!flag.HasValue)
				{
					valueOrDefault = GameFiles.DetectAntiCheat(game) != AntiCheat.None;
					flag = valueOrDefault;
					flag2 = valueOrDefault;
				}
				else
				{
					flag2 = valueOrDefault;
				}
				if (flag2)
				{
					continue;
				}
			}
			Each(new SourceFile(item.Key, num, ""), game.InstallDir, detect: false, null, (int entry, string _, Readable src) =>
			{
				if (!byEntry.TryGetValue(entry, out List<(string, Loc)> value))
				{
					return true;
				}
				foreach (var (text, loc) in value)
				{
					ct.ThrowIfCancellationRequested();
					if (loc.Offset <= src.Length - loc.Size)
					{
						byte[] array = new byte[loc.Size];
						try
						{
							src.Read(loc.Offset, array);
						}
						catch (InvalidDataException)
						{
							continue;
						}
						if (Convert.ToHexStringLower(SHA1.HashData(array)) == text)
						{
							sink(text, array);
						}
					}
				}
				return true;
			}, byEntry.Keys.Where((int e) => e >= 0));
		}
	}

	private static bool Each(SourceFile f, string installDir, bool detect, Meter? meter, Func<int, string, Readable, bool> use, IEnumerable<int>? only = null)
	{
		switch (f.Kind)
		{
		case 0:
		{
			Psarc a = Psarc.Open(f.Path, meter);
			try
			{
				if (a == null)
				{
					return false;
				}
				string relativePath = Path.GetRelativePath(installDir, f.Path);
				foreach (int i in only?.Where((int num) => num > 0 && num < a.Count).ToList() ?? (from entry in Enumerable.Range(1, a.Count - 1)
					where a.Names[entry]?.EndsWith(".xpps", StringComparison.OrdinalIgnoreCase) ?? false
					orderby (!detect) ? 0 : a.Size(entry)
					select entry).ThenBy((int num) => a.Names[num], StringComparer.Ordinal).ToList())
				{
					if (!use(i, relativePath + ":" + a.Names[i], new Readable(a.Size(i), (long from, Span<byte> into) =>
					{
						a.Read(i, from, into);
					})))
					{
						break;
					}
				}
				return true;
			}
			finally
			{
				if (a != null)
				{
					((IDisposable)a).Dispose();
				}
			}
		}
		case -1:
		{
			using Dsar dsar = Dsar.Open(f.Path, meter);
			if (dsar == null)
			{
				return false;
			}
			use(-1, f.Library, new Readable(dsar.Length, dsar.Read));
			return true;
		}
		default:
		{
			SafeFileHandle h;
			try
			{
				h = File.OpenHandle(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				return false;
			}
			using (h)
			{
				use(-2, f.Library, new Readable(RandomAccess.GetLength(h), (long from, Span<byte> into) =>
				{
					if (RandomAccess.Read(h, into, from) != into.Length)
					{
						throw new InvalidDataException("file truncated");
					}
				}));
			}
			return true;
		}
		}
	}

	private static IEnumerable<SourceFile> Files(Game game, string layout, bool detect)
	{
		string dir = game.InstallDir;
		switch (layout)
		{
		case "Sucker Punch":
			foreach (FileInfo item in Order(Files(Path.Combine(dir, "cache_pc", "psarc"), "*.psarc")))
			{
				yield return new SourceFile(item.FullName, 0, "");
			}
			break;
		case "Insomniac":
		{
			string[] array = new string[2] { "material", "materialgraph" };
			foreach (string text in array)
			{
				if (File.Exists(Path.Combine(dir, "d", text)))
				{
					yield return new SourceFile(Path.Combine(dir, "d", text), -1, "d\\" + text);
				}
			}
			// Marvel's Spider-Man Remastered / Miles Morales: asset_archive\toc + DSAR archives (g00s000...) holding every
			// asset; the shaders sit in the materials spread over several of them, so each is carved whole, in name order
			// (also when detecting: the first archives are the ones with materials).
			foreach (string item3 in AssetArchives(dir))
			{
				yield return new SourceFile(item3, -1, Path.GetRelativePath(dir, item3));
			}
			if (!detect && File.Exists(game.ExePath) && GameFiles.DetectAntiCheat(game) == AntiCheat.None)
			{
				yield return new SourceFile(game.ExePath, -2, Path.GetRelativePath(dir, game.ExePath));
			}
			break;
		}
		case "Stingray":
			foreach (FileInfo item2 in Order(Files(Path.Combine(dir, "data"), "bundles.*.nxa")))
			{
				yield return new SourceFile(item2.FullName, -1, Path.GetRelativePath(dir, item2.FullName));
			}
			break;
		}
		IEnumerable<FileInfo> Order(IEnumerable<FileInfo> files)
		{
			if (!detect)
			{
				return files.OrderBy((FileInfo f) => f.Name, StringComparer.OrdinalIgnoreCase);
			}
			return files.OrderBy((FileInfo f) => f.Length);
		}
	}

	/// <summary>
	/// The DSAR archives of an Insomniac install that keeps its assets in asset_archive\ (with its toc there, not at the
	/// install's root): Marvel's Spider-Man Remastered and Miles Morales. Name order; files that aren't DSAR (the toc,
	/// dag, localized audio like a00s034.us) are skipped.
	/// </summary>
	private static IEnumerable<string> AssetArchives(string installDir)
	{
		string dir = Path.Combine(installDir, "asset_archive");
		if (!File.Exists(Path.Combine(dir, "toc")))
		{
			return Array.Empty<string>();
		}
		return from f in Files(dir, "*")
			where f.Extension.Length == 0 && f.Length >= 32 && IsDsar(f.FullName)
			orderby f.Name
			select f.FullName;
	}

	private static bool IsDsar(string path)
	{
		try
		{
			using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
			Span<byte> span = stackalloc byte[4];
			return RandomAccess.Read(handle, span, 0L) == 4 && ((ReadOnlySpan<byte>)span).SequenceEqual("DSAR"u8);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return false;
		}
	}

	private static List<FileInfo> Files(string dir, string pattern)
	{
		try
		{
			return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles(pattern, new EnumerationOptions
			{
				IgnoreInaccessible = true,
				AttributesToSkip = FileAttributes.None
			}).ToList() : new List<FileInfo>();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return new List<FileInfo>();
		}
	}

	private static (int, int)? Twin(Readable src, long at, Meter meter)
	{
		if (at < 12 || !meter.Take(12L))
		{
			return null;
		}
		Span<byte> span = stackalloc byte[12];
		try
		{
			src.Read(at - 12, span);
		}
		catch (InvalidDataException)
		{
			return null;
		}
		if (!((ReadOnlySpan<byte>)span).StartsWith("IGSH"u8))
		{
			return null;
		}
		return (BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4)), BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8)));
	}

	public static void Carve(Readable src, byte[] buf, Found hit, Meter meter, Func<bool> stop, CancellationToken ct)
	{
		if (buf.Length < 64)
		{
			throw new ArgumentException("a window under 64 bytes", "buf");
		}
		long num = 0L;
		while (num < src.Length && !stop())
		{
			ct.ThrowIfCancellationRequested();
			int num2 = (int)Math.Min(buf.Length, src.Length - num);
			int num3 = (int)Math.Min(num2, meter.Left);
			if ((num3 < num2 && num3 < 64) || !meter.Take(num3))
			{
				break;
			}
			InvalidDataException ex = null;
			try
			{
				src.Read(num, buf.AsSpan(0, num3));
			}
			catch (InvalidDataException ex2)
			{
				num3 = Prefix(src, num, buf.AsSpan(0, num3), meter);
				ex = ex2;
			}
			Span<byte> span = buf.AsSpan(0, num3);
			bool flag = ex != null || num + num3 == src.Length;
			long num4 = (flag ? (num + num3) : (num + num3 - 31));
			int num5 = 0;
			while (true)
			{
				int num6 = ((ReadOnlySpan<byte>)span.Slice(num5)).IndexOf("DXBC"u8);
				if (num6 < 0)
				{
					break;
				}
				ct.ThrowIfCancellationRequested();
				if (stop())
				{
					return;
				}
				int num7 = num5 + num6;
				long num8 = num + num7;
				num5 = num7 + 1;
				if (num7 + 32 > num3)
				{
					if (!flag)
					{
						break;
					}
					continue;
				}
				int num9 = Dxbc.HeaderSize(span.Slice(num7, 32));
				if (num9 == 0 || num9 > src.Length - num8)
				{
					continue;
				}
				ReadOnlySpan<byte> readOnlySpan;
				if (num7 + num9 <= num3)
				{
					readOnlySpan = span.Slice(num7, num9);
				}
				else
				{
					if (ex != null || !meter.Take(num9))
					{
						continue;
					}
					byte[] array = new byte[num9];
					try
					{
						src.Read(num8, array);
					}
					catch (InvalidDataException)
					{
						continue;
					}
					readOnlySpan = array;
				}
				if (Dxbc.Valid(readOnlySpan))
				{
					hit(num8, readOnlySpan);
					num5 = Math.Min(num7 + num9, num3);
					num4 = Math.Max(num4, num8 + num9);
				}
			}
			if (ex != null)
			{
				ExceptionDispatchInfo.Throw(ex);
			}
			num = num4;
		}
	}

	private static int Prefix(Readable src, long pos, Span<byte> span, Meter meter)
	{
		int num = 0;
		int num2 = 65536;
		while (num < span.Length && num2 > 0)
		{
			Span<byte> span2 = span.Slice(num, Math.Min(num2, span.Length - num));
			if (!meter.Take(span2.Length))
			{
				break;
			}
			try
			{
				src.Read(pos + num, span2);
				num += span2.Length;
			}
			catch (InvalidDataException)
			{
				num2 /= 4;
			}
		}
		return num;
	}
}
