using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Nixxes;
using SCSKiller.Core.Northlight;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Decima;

public sealed class DecimaReader(string? dataDir = null) : IEngineReader
{
	public sealed record Loc(string Archive, long Offset, int Size);

	public const string Family = "Decima";

	public const string Version = "DX12";

	public const string HorizonZeroDawn = "Horizon Zero Dawn";

	public const string DeathStranding = "Death Stranding";

	public const int ProgramGap = 48;

	public const int DeathStrandingProgramGap = 52;

	private readonly DecimaKeys keys = new DecimaKeys(dataDir ?? AppStore.DefaultDir);

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	internal int Window { get; init; } = 16777216;

	public static int ProgramGapOf(string fork)
	{
		if (!(fork == "Death Stranding"))
		{
			return 48;
		}
		return 52;
	}

	public static (string Fork, List<FileInfo> Archives, int Unknown)? Layout(string installDir)
	{
		(string, uint, string)[] array = new (string, uint, string)[2]
		{
			("Packed_DX12", 540033104u, "Horizon Zero Dawn"),
			("data", 556810320u, "Death Stranding")
		};
		for (int i = 0; i < array.Length; i++)
		{
			(string, uint, string) tuple = array[i];
			string item = tuple.Item1;
			uint magic = tuple.Item2;
			string item2 = tuple.Item3;
			DirectoryInfo directoryInfo = new DirectoryInfo(Path.Combine(installDir, item));
			if (!directoryInfo.Exists)
			{
				continue;
			}
			List<FileInfo> list;
			try
			{
				list = directoryInfo.EnumerateFiles("*.bin", new EnumerationOptions
				{
					IgnoreInaccessible = true,
					AttributesToSkip = FileAttributes.None
				}).OrderBy((FileInfo f) => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				continue;
			}
			List<FileInfo> list2 = list.Where((FileInfo f) => DecimaArchive.Magic(f.FullName) == magic).ToList();
			if (list2.Count > 0 || (item2 == "Horizon Zero Dawn" && list.Count > 0))
			{
				return (item2, list2, list.Count - list2.Count);
			}
		}
		return null;
	}

	public string DetectStamp(Game game, EngineInfo? engine)
	{
		return keys.KeyStamp(game);
	}

	public EngineInfo? Detect(Game game)
	{
		string notes;
		return Detect(game, out notes);
	}

	public EngineInfo? Detect(Game game, out string notes)
	{
		notes = "";
		(string, List<FileInfo>, int)? tuple = Layout(game.InstallDir);
		if (tuple.HasValue)
		{
			(string, List<FileInfo>, int) valueOrDefault = tuple.GetValueOrDefault();
			string item = valueOrDefault.Item1;
			List<FileInfo> item2 = valueOrDefault.Item2;
			List<string> list = (from f in item2
				orderby f.Length
				select f.FullName).ToList();
			if (item == "Horizon Zero Dawn")
			{
				if (list.Count == 0)
				{
					return new EngineInfo("Decima", "DX12", item, "D3D12", Encrypted: false, "an archive version SCSKiller doesn't read");
				}
				string why = "";
				if (!list.Any((string f) =>
				{
					using DecimaArchive decimaArchive = DecimaArchive.Open(f, null, out why);
					return decimaArchive != null;
				}))
				{
					return new EngineInfo("Decima", "DX12", item, "D3D12", Encrypted: false, "its archives don't open (" + why + ")");
				}
				return new EngineInfo("Decima", "DX12", item, "D3D12", Encrypted: false, null);
			}
			DecimaArchive.Salts salts = keys.Get(game, list, out notes);
			return new EngineInfo("Decima", "DX12", item, "D3D12", salts == null, (salts == null) ? "encrypted game files (the game's exe doesn't give their salts)" : null);
		}
		return null;
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		(string Fork, List<FileInfo> Archives, int Unknown) tuple = Layout(game.InstallDir) ?? throw new InvalidDataException("not a Decima install: scan the game again");
		string item = tuple.Fork;
		List<FileInfo> item2 = tuple.Archives;
		int item3 = tuple.Unknown;
		DecimaArchive.Salts salts = Salts(game, item);
		try
		{
			Codecs.Load(zlib: false);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			throw new InvalidDataException("the Oodle codec can't be loaded", ex);
		}
		Dictionary<string, ShaderInfo> shaders = new Dictionary<string, ShaderInfo>();
		Dictionary<string, Loc> locs = new Dictionary<string, Loc>();
		HashSet<(string Vs, string Ps)> pairs = new HashSet<(string, string)>();
		List<(string, HashSet<string>)> list = new List<(string, HashSet<string>)>();
		int gap = ProgramGapOf(item);
		int containers = 0;
		int bad = 0;
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		long programs = 0L;
		long lonelyVs = 0L;
		Dictionary<string, int> dictionary = new Dictionary<string, int>();
		long num4 = 0L;
		byte[] buf = new byte[Window];
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
		foreach (FileInfo f in item2)
		{
			ct.ThrowIfCancellationRequested();
			string relativePath = Path.GetRelativePath(game.InstallDir, f.FullName);
			incrementalHash.AppendData(Encoding.UTF8.GetBytes($"{relativePath}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
			string why;
			using DecimaArchive decimaArchive = DecimaArchive.Open(f.FullName, salts, out why);
			if (decimaArchive == null)
			{
				dictionary[why] = dictionary.GetValueOrDefault(why) + 1;
				continue;
			}
			HashSet<string> seen = new HashSet<string>();
			(long End, int Kind, string Sha)? prev = null;
			NixxesReader.Meter meter = new NixxesReader.Meter(2 * decimaArchive.Length + Window);
			try
			{
				NixxesReader.Carve(decimaArchive.Readable, buf, (long at, ReadOnlySpan<byte> c) =>
				{
					int num7 = containers;
					containers = num7 + 1;
					string text5 = Convert.ToHexStringLower(SHA1.HashData(c));
					if (locs.TryAdd(text5, new Loc(f.FullName, at, c.Length)))
					{
						try
						{
							ShaderInfo shaderInfo = ShaderContainer.Parse(c, text5, new ResourceCounts(0, 0, 0, 0));
							if ((object)shaderInfo != null)
							{
								shaders[text5] = shaderInfo with
								{
									Counts = CarvedReader.Counts(shaderInfo.Bindings)
								};
							}
						}
						catch (Exception ex3) when ((ex3 is ArgumentException || ex3 is IndexOutOfRangeException) ? true : false)
						{
							num7 = bad;
							bad = num7 + 1;
						}
					}
					int num8;
					if (shaders.TryGetValue(text5, out ShaderInfo value) && value.Stage == Stage.Pixel && prev.HasValue)
					{
						(long, int, string) valueOrDefault = prev.GetValueOrDefault();
						if (valueOrDefault.Item2 == 1)
						{
							num8 = ((valueOrDefault.Item1 + gap == at) ? 1 : 0);
							goto IL_0140;
						}
					}
					num8 = 0;
					goto IL_0140;
					IL_0140:
					bool flag = (byte)num8 != 0;
					if (prev.HasValue && prev.GetValueOrDefault().Kind == 1 && !flag)
					{
						long num9 = lonelyVs;
						lonelyVs = num9 + 1;
					}
					if (value == null)
					{
						prev = null;
					}
					else
					{
						if (flag)
						{
							long num9 = programs;
							programs = num9 + 1;
							pairs.Add((prev.Value.Sha, value.Sha1));
						}
						seen.Add(value.Sha1);
						prev = (at + c.Length, (int)value.Stage, value.Sha1);
					}
				}, meter, () => false, ct);
				if (meter.Left < Window)
				{
					num3++;
				}
			}
			catch (InvalidDataException)
			{
				num++;
			}
			if (prev.HasValue && prev.GetValueOrDefault().Kind == 1)
			{
				long num5 = lonelyVs;
				lonelyVs = num5 + 1;
			}
			num4 += meter.Read;
			list.Add((relativePath, seen));
		}
		string text;
		if (programs < lonelyVs)
		{
			text = $"; warning: only {programs} of {programs + lonelyVs} VS have a PS {gap} bytes later: no programs, VS and PS pooled per archive";
		}
		else
		{
			text = ((lonelyVs > 0) ? $"; warning: {lonelyVs} VS not followed by a PS {gap} bytes later" : "");
		}
		if (programs < lonelyVs)
		{
			pairs.Clear();
		}
		string text2 = string.Concat(new string[5]
		{
			text,
			string.Concat(dictionary.Select((KeyValuePair<string, int> u) => $"; warning: {u.Value} archives don't open: {u.Key}")),
			(item3 > 0) ? $"; warning: {item3} .bin files of an unknown archive version skipped" : "",
			(num > 0) ? $"; warning: {num} archives stop at a chunk that doesn't decompress to its size (the shaders before it are kept)" : "",
			(num3 > 0) ? $"; warning: {num3} archives hit the read budget, the rest of them not scanned" : ""
		});
		if (shaders.Count == 0)
		{
			throw new InvalidDataException(item + ": no shader in its archives" + text2);
		}
		List<ShaderMap> list2 = new List<ShaderMap>();
		HashSet<string> paired = new HashSet<string>();
		foreach (var (text3, text4) in pairs)
		{
			if (NorthlightReader.Feeds(new _003C_003Ez__ReadOnlyArray<ShaderInfo>(new ShaderInfo[2]
			{
				shaders[text3],
				shaders[text4]
			})) != true)
			{
				num2++;
				continue;
			}
			paired.Add(text3);
			paired.Add(text4);
			list2.Add(new ShaderMap(CarvedReader.Sha1Hex(text3 + "," + text4), Library(locs[text3], game), "D3D12", new _003C_003Ez__ReadOnlyArray<string>(new string[2] { text3, text4 }), IsPipeline: true));
		}
		foreach (string item5 in from s in shaders.Values
			where s.Stage == Stage.Compute
			select s.Sha1)
		{
			list2.Add(new ShaderMap(CarvedReader.Sha1Hex(item5), Library(locs[item5], game), "D3D12", new _003C_003Ez__ReadOnlySingleElementList<string>(item5), IsPipeline: true));
		}
		int num6 = 0;
		foreach (var item6 in list)
		{
			string item4 = item6.Item1;
			List<string> list3 = item6.Item2.Where((string h) => shaders[h].Stage != Stage.Compute && !paired.Contains(h)).ToList();
			if (list3 != null && list3.Count > 0)
			{
				list2.Add(new ShaderMap(CarvedReader.Sha1Hex(item4), item4, "D3D12", list3));
				num6 += list3.Count;
			}
		}
		located[game.Id] = locs;
		log?.Report($"{item}: {item2.Count} archives ({(double)num4 / 1000000000.0:F2} GB unpacked): {containers} shader containers, {shaders.Count} shaders (" + string.Join(", ", from s in shaders.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select $"{g.Count()} {g.Key}") + $"), {pairs.Count - num2} VS+PS programs, {shaders.Values.Count((ShaderInfo s) => s.Stage == Stage.Library)} ray tracing libraries, {shaders.Values.Count((ShaderInfo s) => s.InlineRayTracing)} tracing rays inline" + ((num6 > 0) ? $"; {num6} shaders outside a program pooled per archive" : "") + ((num2 > 0) ? $"; warning: {num2} programs whose VS doesn't feed their PS, pooled instead" : "") + text2 + ((bad > 0) ? $"; {bad} unparseable" : "") + $" ({stopwatch.Elapsed.TotalSeconds:F1}s)");
		return new ShaderIndex(Convert.ToHexStringLower(incrementalHash.GetHashAndReset()), new _003C_003Ez__ReadOnlySingleElementList<string>("D3D12"), shaders, list2);
	}

	private static string Library(Loc at, Game game)
	{
		return Path.GetRelativePath(game.InstallDir, at.Archive);
	}

	private DecimaArchive.Salts? Salts(Game game, string fork)
	{
		if (!(fork == "Death Stranding"))
		{
			return null;
		}
		return keys.Stored(game) ?? throw new InvalidDataException("encrypted archives without their salts: scan the game again");
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		if (!located.TryGetValue(game.Id, out IReadOnlyDictionary<string, Loc> locs))
		{
			Index(game, engine, null, ct);
			locs = located[game.Id];
		}
		DecimaArchive.Salts salts = Salts(game, engine.Fork ?? "Horizon Zero Dawn");
		foreach (IGrouping<string, (string, Loc)> item in from s in sha1s.Where(locs.ContainsKey)
			select (Sha: s, At: locs[s]) into x
			group x by x.At.Archive)
		{
			using DecimaArchive decimaArchive = DecimaArchive.Open(item.Key, salts);
			if (decimaArchive == null)
			{
				continue;
			}
			foreach (var (text, loc) in item.OrderBy(((string Sha, Loc At) x) => x.At.Offset))
			{
				ct.ThrowIfCancellationRequested();
				if (loc.Offset <= decimaArchive.Length - loc.Size)
				{
					byte[] array = new byte[loc.Size];
					try
					{
						decimaArchive.Read(loc.Offset, array);
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
		}
	}
}
