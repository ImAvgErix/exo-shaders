using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Carved;

public sealed class CarvedReader : IEngineReader
{
	public sealed record Loc(string Path, long Offset, int Size);

	private delegate void Found(FileInfo file, long offset, ReadOnlySpan<byte> container);

	internal delegate void Hit(long offset, ReadOnlySpan<byte> container);

	public const string Family = "Carved";

	public const string EmbeddedRootSignatures = "+RTS0";

	public const string Platform = "D3D12";

	public const string RedEngine4 = "REDengine 4";

	public const string Northlight2 = "Northlight 2";

	public static readonly string[] Families = new string[3] { "Carved", "REDengine 4", "Northlight 2" };

	public const int MinGraphics = 256;

	private const long DetectBudget = 536870912L;

	private const long IndexSampleBudget = 4294967296L;

	private const int NamedWindow = 16777216;

	private const int OtherWindow = 4194304;

	internal const int Block = 8388608;

	private const int MaxRecordGap = 32;

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	private static readonly HashSet<string> NotShaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".exe", ".dll", ".pdb", ".bk2", ".bik", ".usm", ".mp4", ".webm", ".wem", ".bnk",
		".ogg", ".wav", ".mp3", ".fsb", ".xwm", ".awc", ".mkv", ".avi", ".wmv", ".flac",
		".opus", ".png", ".jpg", ".dds", ".ttf", ".otf"
	};

	private static readonly Dictionary<string, string> ApiDlls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["d3d12.dll"] = "D3D12",
		["d3d11.dll"] = "D3D11",
		["vulkan-1.dll"] = "Vulkan"
	};

	private static readonly string[] Middleware = new string[48]
	{
		"eossdk", "libxess", "dstorage", "gfsdk", "nvngx", "sl.", "amd_", "ffx", "nvlowlatency", "galaxy",
		"steam_api", "discord", "overlay", "igxess", "renderdoc", "reshade", "dxgi", "d3d1", "d3d9", "opengl32",
		"vulkan-1", "libegl", "libglesv2", "vk_swiftshader", "d3dcompiler", "dxcompiler", "dxil", "cef", "nvapi", "nvtt",
		"physx", "bink", "xess", "fsr", "dlss", "streamline", "gameoverlay", "twitch", "nvrtx", "nri",
		"nrd", "nrc", "ags", "libxell", "pix", "winpix", "aftermath", "gfnruntime"
	};

	public static string Fingerprint(string installDir)
	{
		if (!File.Exists(Path.Combine(installDir, "engine", "shader_final.cache")))
		{
			if (!IsNorthlight2(installDir))
			{
				return "Carved";
			}
			return "Northlight 2";
		}
		return "REDengine 4";
	}

	internal static bool IsNorthlight2(string installDir)
	{
		try
		{
			return Directory.EnumerateFiles(installDir, "*.rmdtoc", new EnumerationOptions
			{
				RecurseSubdirectories = true,
				MaxRecursionDepth = 3,
				IgnoreInaccessible = true,
				AttributesToSkip = FileAttributes.None
			}).Any();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return false;
		}
	}

	public int ReaderVersion(EngineInfo engine)
	{
		if (!(engine.Family == "Carved") || !engine.Version.EndsWith("+RTS0"))
		{
			return 1;
		}
		return 2;
	}

	public EngineInfo? Detect(Game game)
	{
		string notes;
		return Detect(game, out notes);
	}

	public EngineInfo? Detect(Game game, out string notes)
	{
		notes = "";
		if (!Directory.Exists(game.InstallDir))
		{
			return null;
		}
		string text = GodotApi(game.ExePath);
		if (text != null)
		{
			return new EngineInfo(Fingerprint(game.InstallDir), "-", null, text, Encrypted: false, "runs on " + text + " (Godot)");
		}
		HashSet<string> seen = new HashSet<string>();
		Dictionary<string, int> perFile = new Dictionary<string, int>();
		int graphics = 0;
		int compute = 0;
		int dxil = 0;
		int rts0 = 0;
		long num = Walk(game.InstallDir, detect: true, (FileInfo f, long _, ReadOnlySpan<byte> c) =>
		{
			int num2 = Dxbc.Kind(c);
			if (num2 >= 0 && seen.Add(Convert.ToHexStringLower(SHA1.HashData(c))))
			{
				perFile[f.FullName] = perFile.GetValueOrDefault(f.FullName) + 1;
				if (Dxbc.IsGraphics(num2))
				{
					graphics++;
				}
				else if (num2 == 5)
				{
					compute++;
				}
				if (!Dxbc.Part(c, "DXIL"u8).IsEmpty)
				{
					dxil++;
				}
				if (!Dxbc.Part(c, "RTS0"u8).IsEmpty)
				{
					rts0++;
				}
			}
		}, () => graphics >= 256);
		int count = seen.Count;
		notes = $"{(double)num / 1000000.0:F0} MB read; " + string.Join("; ", from p in perFile.OrderByDescending((KeyValuePair<string, int> p) => p.Value).Take(3)
			select $"{Path.GetRelativePath(game.InstallDir, p.Key)}: {p.Value}");
		string text2;
		if (dxil == count)
		{
			text2 = "DXIL";
		}
		else
		{
			text2 = ((dxil == 0) ? "DXBC" : "DXIL+DXBC");
		}
		string text3 = text2 + ((count > 0 && ((double)rts0 >= 0.9 * (double)count || DawnStores.Read(game.InstallDir) != null)) ? "+RTS0" : "");
		string unsupported;
		if (graphics >= 256)
		{
			unsupported = null;
		}
		else if (count != 0)
		{
			unsupported = ((graphics == 0) ? $"only {compute} raw compute shaders: graphics shaders are {"compressed or packed: needs an engine reader"}" : $"only {graphics} raw graphics shaders: the rest are {"compressed or packed: needs an engine reader"}");
		}
		else
		{
			unsupported = $"no raw DXBC/DXIL shaders in its files ({(double)num / 1000000000.0:F1} GB sampled): shaders are {"compressed or packed: needs an engine reader"}";
		}
		return new EngineInfo(Fingerprint(game.InstallDir), (count == 0) ? "-" : text3, null, GraphicsApi(game, dxil > 0), Encrypted: false, unsupported);
	}

	internal static string? GodotApi(string exe)
	{
		try
		{
			string text = Path.ChangeExtension(exe, ".pck");
			bool flag = !File.Exists(text);
			using FileStream fileStream = File.OpenRead(flag ? exe : text);
			Span<byte> span = stackalloc byte[12];
			long num = 0L;
			if (flag)
			{
				if (fileStream.Length < 24)
				{
					return null;
				}
				fileStream.Position = fileStream.Length - 12;
				fileStream.ReadExactly(span);
				if (!((ReadOnlySpan<byte>)span.Slice(8)).SequenceEqual("GDPC"u8))
				{
					return null;
				}
				num = fileStream.Length - 12 - BinaryPrimitives.ReadInt64LittleEndian(span);
				if (num < 0)
				{
					return null;
				}
			}
			fileStream.Position = num;
			fileStream.ReadExactly(span);
			string? result;
			if (((ReadOnlySpan<byte>)span.Slice(0, 4)).SequenceEqual("GDPC"u8))
			{
				result = ((BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8)) >= 4) ? "Vulkan" : "OpenGL");
			}
			else
			{
				result = null;
			}
			return result;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) ? true : false)
		{
			return null;
		}
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		Dictionary<string, Loc> locs = new Dictionary<string, Loc>();
		Dictionary<string, ShaderInfo> shaders = new Dictionary<string, ShaderInfo>();
		RootSigCarriers rootSigs = new RootSigCarriers();
		Dictionary<string, List<(long Off, int Size, int Kind, string Sha)>> perFile = new Dictionary<string, List<(long, int, int, string)>>();
		Dictionary<string, string> platformOf = new Dictionary<string, string>();
		int graphics = 0;
		int bad = 0;
		long num = Walk(game.InstallDir, detect: false, (FileInfo f, long off, ReadOnlySpan<byte> c) =>
		{
			ct.ThrowIfCancellationRequested();
			string text5 = Convert.ToHexStringLower(SHA1.HashData(c));
			int num3 = Dxbc.Kind(c);
			if (!perFile.TryGetValue(f.FullName, out List<(long, int, int, string)> value2))
			{
				value2 = (perFile[f.FullName] = new List<(long, int, int, string)>());
			}
			value2.Add((off, c.Length, num3, text5));
			if (locs.TryAdd(text5, new Loc(f.FullName, off, c.Length)))
			{
				rootSigs.See(c, text5, num3);
				if (num3 >= 0)
				{
					try
					{
						ShaderInfo shaderInfo = ShaderContainer.Parse(c, text5, new ResourceCounts(0, 0, 0, 0));
						if ((object)shaderInfo != null)
						{
							shaders[text5] = shaderInfo with
							{
								Counts = Counts(shaderInfo.Bindings)
							};
							if (Dxbc.IsGraphics(num3))
							{
								graphics++;
							}
							string text6 = LanePlatform(Dxbc.WaveLanes(c));
							if (text6 != null)
							{
								platformOf[text5] = text6;
							}
						}
					}
					catch (Exception ex) when ((ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
					{
						bad++;
					}
				}
			}
		}, () => graphics >= 256, ct);
		rootSigs.Apply(shaders);
		DawnStores.Stores dawn = DawnStores.Read(game.InstallDir);
		string text = ((dawn == null) ? null : perFile.Keys.FirstOrDefault((string p) => string.Equals(Path.GetFullPath(p), Path.GetFullPath(dawn.ShaderFile), StringComparison.OrdinalIgnoreCase)));
		string note = "";
		List<(List<string>, string)> list = ((text == null) ? null : DawnRuns(dawn, perFile[text], shaders, locs, out note));
		List<ShaderMap> list2 = new List<ShaderMap>();
		HashSet<string> hashSet = new HashSet<string>();
		int num2 = 0;
		IncrementalHash content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
		try
		{
			foreach (var (text3, list4) in perFile)
			{
				string relativePath = Path.GetRelativePath(game.InstallDir, text3);
				Stamp(text3);
				List<(List<string>, string)> list5 = ((text3 == text) ? list : Records(list4)?.Select((List<string> r) => ((List<string> Run, string Root))(Run: r, Root: null)).ToList());
				if (list5 != null)
				{
					foreach (var item3 in list5)
					{
						List<string> item = item3.Item1;
						string item2 = item3.Item2;
						num2++;
						string text4 = Sha1Hex(string.Join(',', item.Order(StringComparer.Ordinal)));
						if (hashSet.Add(text4 + "|" + item2))
						{
							list2.Add(new ShaderMap(text4, relativePath, item.Select(PlatformOf).FirstOrDefault((string p) => p != "D3D12", "D3D12"), item, IsPipeline: true, item2));
						}
					}
					continue;
				}
				foreach (IGrouping<string, string> item4 in list4.Where(((long Off, int Size, int Kind, string Sha) s) => shaders.ContainsKey(s.Sha)).Select(((long Off, int Size, int Kind, string Sha) s) => s.Sha).Distinct()
					.GroupBy(PlatformOf))
				{
					list2.Add(new ShaderMap(Sha1Hex((item4.Key == "D3D12") ? relativePath : (relativePath + "|" + item4.Key)), relativePath, item4.Key, item4.ToList()));
				}
			}
			if (list != null)
			{
				Stamp(dawn.RootFile);
				Stamp(dawn.PsoFile);
			}
			located[game.Id] = locs;
			int value = shaders.Values.Select((ShaderInfo s) => s.RootSignature).OfType<string>().Distinct()
				.Count();
			log?.Report(string.Concat(new string[6]
			{
				$"{perFile.Count} files with shaders, {(double)num / 1000000000.0:F2} GB read: {shaders.Count} shaders (",
				string.Join(", ", from s in shaders.Values
					group s by s.Stage into g
					orderby g.Count() descending
					select $"{g.Count()} {g.Key}"),
				$"){((bad > 0) ? $", {bad} unparseable" : "")}, {value} embedded root signatures, {num2} pipeline records -> {list2.Count} maps ({list2.Count((ShaderMap m) => m.IsPipeline)} exact)",
				string.Concat(from p in platformOf.Values
					group p by p into g
					select $"; {g.Count()} need {g.Key}, not planned for 32-lane GPUs"),
				note,
				$" ({stopwatch.Elapsed.TotalSeconds:F1}s)"
			}));
			Dictionary<string, byte[]> dictionary = new Dictionary<string, byte[]>(rootSigs.Blobs);
			if (list != null)
			{
				foreach (DawnStores.Root value3 in dawn.Roots.Values)
				{
					dictionary.TryAdd(value3.Sha, value3.Blob);
				}
			}
			string contentHash = Convert.ToHexStringLower(content.GetHashAndReset());
			List<string> list6 = new List<string>();
			list6.Add("D3D12");
			list6.AddRange(platformOf.Values.Distinct().Order(StringComparer.Ordinal));
			return new ShaderIndex(contentHash, new _003C_003Ez__ReadOnlyList<string>(list6), shaders, list2, dictionary);
		}
		finally
		{
			if (content != null)
			{
				((IDisposable)content).Dispose();
			}
		}
		string PlatformOf(string sha)
		{
			return platformOf.GetValueOrDefault(sha, "D3D12");
		}
		void Stamp(string path)
		{
			FileInfo fileInfo = new FileInfo(path);
			content.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(game.InstallDir, path)}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}\n"));
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
			using SafeFileHandle handle = File.OpenHandle(item.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
			foreach (var (text, loc) in item.OrderBy(((string Sha, Loc At) x) => x.At.Offset))
			{
				ct.ThrowIfCancellationRequested();
				byte[] array = new byte[loc.Size];
				if (RandomAccess.Read(handle, array, loc.Offset) == array.Length && Convert.ToHexStringLower(SHA1.HashData(array)) == text)
				{
					sink(text, array);
				}
			}
		}
	}

	private static List<(List<string> Run, string? Root)> DawnRuns(DawnStores.Stores dawn, List<(long Off, int Size, int Kind, string Sha)> seq, Dictionary<string, ShaderInfo> shaders, Dictionary<string, Loc> locs, out string note)
	{
		Dictionary<long, string> shaAt = new Dictionary<long, string>();
		foreach (var item3 in seq)
		{
			shaAt.TryAdd(item3.Off, item3.Sha);
		}
		List<(List<string>, string)> list = new List<(List<string>, string)>();
		Dictionary<string, HashSet<string>> dictionary = new Dictionary<string, HashSet<string>>();
		int num = 0;
		HashSet<string> value2;
		foreach (var pipeline in dawn.Pipelines)
		{
			uint item = pipeline.Root;
			uint[] item2 = pipeline.Stages;
			List<string> list2 = item2.Select((uint id) => shaAt.GetValueOrDefault(dawn.Shaders[id])).OfType<string>().Where(shaders.ContainsKey)
				.ToList();
			if (list2.Count < item2.Length)
			{
				num++;
				continue;
			}
			foreach (string item4 in list2)
			{
				HashSet<string> hashSet2;
				if (!dictionary.TryGetValue(item4, out var value))
				{
					value2 = (dictionary[item4] = new HashSet<string>());
					hashSet2 = value2;
				}
				else
				{
					hashSet2 = value;
				}
				hashSet2.Add(dawn.Roots[item].Sha);
			}
			list.Add((list2, dawn.Roots[item].Sha));
		}
		foreach (KeyValuePair<string, HashSet<string>> item5 in dictionary)
		{
			item5.Deconstruct(out var key, out value2);
			string key2 = key;
			HashSet<string> hashSet3 = value2;
			shaders[key2] = shaders[key2]with
			{
				RootSignature = ((hashSet3.Count == 1) ? hashSet3.First() : null)
			};
		}
		foreach (DawnStores.Root value3 in dawn.Roots.Values)
		{
			locs.TryAdd(value3.Sha, new Loc(dawn.RootFile, value3.Offset, value3.Size));
		}
		int num2 = dictionary.Count((KeyValuePair<string, HashSet<string>> r) => r.Value.Count > 1);
		note = $"; Dawn: {list.Count} pipeline records, {dictionary.Count - num2} shaders given their root signature" + ((num2 > 0) ? $", {num2} used with several left without: their pipelines take their record's" : "") + ((num + dawn.Skipped > 0) ? $"; left out: {num} records with a shader not indexed, {dawn.Skipped} naming another stage" : "");
		return list;
	}

	public static List<List<string>>? Records(IReadOnlyList<(long Off, int Size, int Kind, string Sha)> seq)
	{
		List<List<(int, string)>> list = new List<List<(int, string)>>();
		long num = long.MinValue;
		foreach (var item in seq)
		{
			if (list.Count == 0 || item.Off - num > 32)
			{
				list.Add(new List<(int, string)>());
			}
			num = item.Off + item.Size;
			if (item.Kind >= 0)
			{
				list[list.Count - 1].Add((item.Kind, item.Sha));
			}
		}
		list.RemoveAll((List<(int Kind, string Sha)> r) => r.Count == 0);
		if (list.Count <= 0 || !list.All(Pipeline) || !list.Any((List<(int Kind, string Sha)> r) => r.Count > 1))
		{
			return null;
		}
		return list.Select((List<(int Kind, string Sha)> r) => r.Select(((int Kind, string Sha) x) => x.Sha).ToList()).ToList();
		static bool Pipeline(List<(int Kind, string Sha)> r)
		{
			if (r.DistinctBy(((int Kind, string Sha) x) => x.Kind).Count() == r.Count)
			{
				if (!r.All(((int Kind, string Sha) x) => Dxbc.IsGraphics(x.Kind)) || !r.Any(((int Kind, string Sha) x) =>
				{
					var (num2, _) = x;
					return (num2 == 1 || num2 == 13) ? true : false;
				}))
				{
					if (r != null && r.Count == 1)
					{
						return r[0].Kind == 5;
					}
					return false;
				}
				return true;
			}
			return false;
		}
	}

	private static (List<FileInfo> Named, List<FileInfo> Other) Files(string dir)
	{
		EnumerationOptions enumerationOptions = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None,
			MaxRecursionDepth = 8
		};
		List<FileInfo> source = (from f in new DirectoryInfo(dir).EnumerateFiles("*", enumerationOptions)
			where f.Length >= 64 && !NotShaders.Contains(f.Extension)
			select f).ToList();
		return (Named: source.Where(Named).OrderBy((FileInfo f) => f.FullName, StringComparer.OrdinalIgnoreCase).ToList(), Other: (from f in source
			where !Named(f) && f.Length >= 65536
			orderby f.Length
			select f).ThenBy((FileInfo f) => f.FullName, StringComparer.OrdinalIgnoreCase).ToList());
		bool Named(FileInfo f)
		{
			string relativePath = Path.GetRelativePath(dir, f.FullName);
			if (!relativePath.Contains("shader", StringComparison.OrdinalIgnoreCase))
			{
				return relativePath.Contains("pso", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
	}

	private static long Walk(string dir, bool detect, Found found, Func<bool> enough, CancellationToken ct = default(CancellationToken))
	{
		(List<FileInfo> Named, List<FileInfo> Other) tuple = Files(dir);
		List<FileInfo> item = tuple.Named;
		List<FileInfo> item2 = tuple.Other;
		byte[] buf = new byte[8388608];
		long num = 0L;
		foreach (FileInfo f in item)
		{
			if (detect && (enough() || num >= 536870912))
			{
				return num;
			}
			num += Scan(f, buf, detect ? Windows(f.Length, 16777216L) : new (long, long)[1] { (0L, f.Length) }, (long o, ReadOnlySpan<byte> c) =>
			{
				found(f, o, c);
			}, ct);
		}
		if (enough())
		{
			return num;
		}
		foreach (FileInfo f2 in item2)
		{
			if (num >= (detect ? 536870912 : 4294967296L) || (detect && enough()))
			{
				break;
			}
			(long, long)[] array = Windows(f2.Length, 4194304L);
			if (detect || array.Length == 1)
			{
				num += Scan(f2, buf, array, (long o, ReadOnlySpan<byte> c) =>
				{
					found(f2, o, c);
				}, ct);
				continue;
			}
			int hits = 0;
			num += Scan(f2, buf, array, (long _, ReadOnlySpan<byte> _) =>
			{
				hits++;
			}, ct);
			if (hits >= 16)
			{
				num += Scan(f2, buf, new (long, long)[1] { (0L, f2.Length) }, (long o, ReadOnlySpan<byte> c) =>
				{
					found(f2, o, c);
				}, ct);
			}
		}
		return num;
	}

	private static (long Lo, long Hi)[] Windows(long len, long w)
	{
		if (len <= 3 * w)
		{
			return new (long, long)[1] { (0L, len) };
		}
		return new (long, long)[3]
		{
			(0L, w),
			(len / 2 - w / 2, len / 2 + w / 2),
			(len - w, len)
		};
	}

	internal static long Scan(FileInfo f, byte[] buf, (long Lo, long Hi)[] windows, Hit hit, CancellationToken ct, int maxSize = 67108864)
	{
		try
		{
			SafeFileHandle h = File.OpenHandle(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan, 0L);
			try
			{
				long len = RandomAccess.GetLength(h);
				return windows.Sum(((long Lo, long Hi) w) => Carve(h, len, w.Lo, Math.Min(w.Hi, len), buf, hit, ct, maxSize));
			}
			finally
			{
				if (h != null)
				{
					((IDisposable)h).Dispose();
				}
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return 0L;
		}
	}

	private static long Carve(SafeFileHandle h, long len, long lo, long hi, byte[] buf, Hit hit, CancellationToken ct, int maxSize)
	{
		long num = 0L;
		Span<byte> span = stackalloc byte[32];
		long num2 = lo;
		while (num2 < hi)
		{
			ct.ThrowIfCancellationRequested();
			int num3 = RandomAccess.Read(h, buf.AsSpan(0, (int)Math.Min(buf.Length, hi - num2)), num2);
			if (num3 <= 0)
			{
				break;
			}
			num += num3;
			Span<byte> span2 = buf.AsSpan(0, num3);
			long num4 = ((num2 + num3 < hi && num3 > 3) ? (num2 + num3 - 3) : (num2 + num3));
			int num5 = 0;
			while (true)
			{
				int num6 = ((ReadOnlySpan<byte>)span2.Slice(num5)).IndexOf("DXBC"u8);
				if (num6 < 0)
				{
					break;
				}
				int num7 = num5 + num6;
				long num8 = num2 + num7;
				num5 = num7 + 1;
				scoped Span<byte> span3;
				if (num7 + 32 <= num3)
				{
					span3 = span2.Slice(num7, 32);
				}
				else
				{
					span3 = ((RandomAccess.Read(h, span, num8) == 32) ? span : default(Span<byte>));
				}
				int num9 = Dxbc.HeaderSize(span3);
				if (num9 != 0 && num9 <= maxSize && num8 + num9 <= len)
				{
					ReadOnlySpan<byte> readOnlySpan = ((num7 + num9 <= num3) ? span2.Slice(num7, num9) : ((Span<byte>)ReadAt(h, num8, num9)));
					if (Dxbc.Valid(readOnlySpan))
					{
						hit(num8, readOnlySpan);
						num5 = Math.Min(num7 + num9, num3);
						num4 = Math.Max(num4, num8 + num9);
					}
				}
			}
			num2 = num4;
		}
		return num;
	}

	private static byte[] ReadAt(SafeFileHandle h, long off, int size)
	{
		byte[] array = new byte[size];
		if (RandomAccess.Read(h, array, off) != size)
		{
			return Array.Empty<byte>();
		}
		return array;
	}

	internal static string? LanePlatform((uint Min, uint Max)? lanes)
	{
		if (lanes.HasValue)
		{
			(uint, uint) valueOrDefault = lanes.GetValueOrDefault();
			if (valueOrDefault.Item1 > 32 || valueOrDefault.Item2 < 32)
			{
				return $"{"D3D12"} wave{valueOrDefault.Item1}{((valueOrDefault.Item2 != valueOrDefault.Item1) ? $"-{valueOrDefault.Item2}" : "")}";
			}
		}
		return null;
	}

	internal static ResourceCounts Counts(IReadOnlyList<Binding> b)
	{
		return new ResourceCounts(N("cbv"), N("srv"), N("uav"), N("sampler"));
		int N(string cls)
		{
			long num = b.Where((Binding x) => x.Class == cls).Sum((Func<Binding, long>)((Binding x) => Math.Max(1, x.Count)));
			if (num > int.MaxValue)
			{
				throw new ArgumentException(cls + " registers overflow");
			}
			return (int)num;
		}
	}

	internal static string Sha1Hex(string s)
	{
		return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)));
	}

	private static string GraphicsApi(Game game, bool dxil)
	{
		HashSet<string> hashSet = Imports(game);
		EnumerationOptions enumerationOptions = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			MaxRecursionDepth = 4,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None
		};
		bool num = dxil || hashSet.Contains("D3D12") || Directory.EnumerateFiles(game.InstallDir, "D3D12Core.dll", enumerationOptions).Any();
		bool flag = hashSet.Contains("D3D11");
		if (!num)
		{
			if (!flag)
			{
				if (!hashSet.Contains("Vulkan"))
				{
					return "D3D11 or D3D12";
				}
				return "Vulkan";
			}
			return "D3D11";
		}
		if (!flag || dxil)
		{
			return "D3D12";
		}
		return "D3D11 or D3D12";
	}

	private static HashSet<string> Imports(Game game)
	{
		HashSet<string> hashSet = new HashSet<string>();
		string directoryName = Path.GetDirectoryName(game.ExePath);
		if (directoryName == null || !Directory.Exists(directoryName) || GameFiles.DetectAntiCheat(game) != AntiCheat.None)
		{
			return hashSet;
		}
		foreach (string item in (from p in Directory.EnumerateFiles(directoryName, "*.dll")
			where !Middleware.Any((string m) => Path.GetFileName(p).StartsWith(m, StringComparison.OrdinalIgnoreCase))
			select p).Prepend(game.ExePath))
		{
			try
			{
				bool agility;
				foreach (string item2 in PeImports(item, out agility))
				{
					if (ApiDlls.TryGetValue(item2, out string value))
					{
						hashSet.Add(value);
					}
				}
				if (agility)
				{
					hashSet.Add("D3D12");
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException || ex is InvalidOperationException) ? true : false)
			{
			}
		}
		return hashSet;
	}

	public static List<string> PeImports(string path, out bool agility, bool delayed = true)
	{
		using PEReader pe = PeFile.Open(path);
		List<string> result = PeImports(pe, delayed);
		agility = PeFile.ExportNames(pe).Take(20000).Contains("D3D12SDKVersion");
		return result;
	}

	public static List<string> PeImports(PEReader pe, bool delayed)
	{
		PEHeader h = pe.PEHeaders.PEHeader ?? throw new BadImageFormatException("no optional header");
		List<string> dlls = new List<string>();
		Table(h.ImportTableDirectory, 20, 12, delay: false);
		if (delayed)
		{
			Table(h.DelayImportTableDirectory, 32, 4, delay: true);
		}
		return dlls;
		void Table(DirectoryEntry d, int stride, int nameAt, bool delay)
		{
			if (d.RelativeVirtualAddress != 0)
			{
				BlobReader reader = pe.GetSectionData(d.RelativeVirtualAddress).GetReader();
				while (reader.RemainingBytes >= stride)
				{
					byte[] array = reader.ReadBytes(stride);
					long num = BitConverter.ToUInt32(array, nameAt);
					if (num == 0L)
					{
						break;
					}
					if (delay && (array[0] & 1) == 0)
					{
						num -= (long)h.ImageBase;
					}
					dlls.Add(PeFile.Str(pe, (int)num));
				}
			}
		}
	}
}
