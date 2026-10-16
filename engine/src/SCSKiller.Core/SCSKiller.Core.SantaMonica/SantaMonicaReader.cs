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
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.SantaMonica;

public sealed class SantaMonicaReader : IEngineReader
{
	public sealed record Loc(string Pack, long Offset, int Size, int Packed = 0);

	public const string Family = "Santa Monica";

	public const string Version = "SHDRPACK 11+RTS0";

	public const string Version1 = "SHDRPACK 1";

	private const string Platform11 = "PCD3D_SM5";

	private static readonly string PackDir = Path.Combine("exec", "wad", "pc_le");

	private const string RootPack = "root.shaderpack";

	private const long DetectBudget = 16777216L;

	private static readonly Stage[] Slots = new Stage[6]
	{
		Stage.Vertex,
		Stage.Pixel,
		Stage.Vertex,
		Stage.Hull,
		Stage.Domain,
		Stage.Compute
	};

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	public long MaxRecords { get; init; } = 4194304L;

	public int ReaderVersion(EngineInfo engine)
	{
		if (!(engine.Version == "SHDRPACK 11+RTS0"))
		{
			return 1;
		}
		return 2;
	}

	public EngineInfo? Detect(Game game)
	{
		long read = 0L;
		foreach (FileInfo item in from f in Packs(game)
			orderby !f.Name.Equals("root.shaderpack", StringComparison.OrdinalIgnoreCase), f.Length
			select f)
		{
			if (read >= 16777216)
			{
				break;
			}
			byte[] array = ShaderPack1.Head(item.FullName, ref read);
			if (array != null && ShaderPack1.IsPack(array))
			{
				return new EngineInfo("Santa Monica", "SHDRPACK 1", null, "D3D11", Encrypted: false, null);
			}
			byte[] array2 = Lz4Frame.First(item.FullName, 16777216 - read, ref read);
			if (array2 != null && ShaderPack11.IsPack(array2))
			{
				return new EngineInfo("Santa Monica", "SHDRPACK 11+RTS0", null, "D3D12", Encrypted: false, null);
			}
		}
		return null;
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		if (engine.Version == "SHDRPACK 1")
		{
			return Index1(game, log, ct);
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		Dictionary<string, ShaderInfo> shaders = new Dictionary<string, ShaderInfo>();
		Dictionary<string, Loc> dictionary = new Dictionary<string, Loc>();
		Dictionary<string, string> platformOf = new Dictionary<string, string>();
		Dictionary<ulong, string> dictionary2 = new Dictionary<ulong, string>();
		Dictionary<ulong, string> dictionary3 = new Dictionary<ulong, string>();
		List<(string, ShaderPack11.Program)> list = new List<(string, ShaderPack11.Program)>();
		Dictionary<string, RootSig.Ranges> ranges = new Dictionary<string, RootSig.Ranges>();
		Dictionary<string, byte[]> dictionary4 = new Dictionary<string, byte[]>();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		int num6 = 0;
		long num7 = 0L;
		long budget = MaxRecords;
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
		foreach (FileInfo item4 in Packs(game).OrderBy((FileInfo f) => f.Name, StringComparer.OrdinalIgnoreCase))
		{
			ct.ThrowIfCancellationRequested();
			using Lz4Frame lz4Frame = Lz4Frame.Open(item4.FullName);
			if (lz4Frame == null)
			{
				num2++;
				continue;
			}
			string rel = Path.GetRelativePath(game.InstallDir, item4.FullName);
			ShaderPack11 shaderPack;
			try
			{
				shaderPack = ShaderPack11.Read(lz4Frame, ref budget);
			}
			catch (InvalidDataException)
			{
				num2++;
				goto end_IL_00f5;
			}
			if (budget < 0)
			{
				num4++;
				continue;
			}
			if (shaderPack == null)
			{
				num3++;
				continue;
			}
			num++;
			incrementalHash.AppendData(Encoding.UTF8.GetBytes($"{rel}|{item4.Length}|{item4.LastWriteTimeUtc.Ticks}\n"));
			list.AddRange(shaderPack.Programs.Select((ShaderPack11.Program p) => (rel: rel, p: p)));
			List<(ulong, string, Loc, ShaderInfo, string)> list2 = new List<(ulong, string, Loc, ShaderInfo, string)>();
			List<(ulong, string, Loc, RootSig.Ranges, byte[])> list3 = new List<(ulong, string, Loc, RootSig.Ranges, byte[])>();
			int num8 = 0;
			int num9 = 0;
			long num10 = 0L;
			try
			{
				HashSet<string> hashSet = new HashSet<string>();
				foreach (ShaderPack11.Shader item5 in shaderPack.Shaders.OrderBy((ShaderPack11.Shader s) => s.Offset))
				{
					ct.ThrowIfCancellationRequested();
					byte[] array = lz4Frame.Read(item5.Offset, item5.Size);
					num10 += array.Length;
					if (!Dxbc.Valid(array) || Dxbc.Kind(array) < 0)
					{
						num8++;
						continue;
					}
					string text = Convert.ToHexStringLower(SHA1.HashData(array));
					if (dictionary.ContainsKey(text) || !hashSet.Add(text))
					{
						list2.Add((item5.Hash, text, null, null, null));
						continue;
					}
					ShaderInfo shaderInfo = null;
					try
					{
						ShaderInfo shaderInfo2 = ShaderContainer.Parse(array, text, new ResourceCounts(0, 0, 0, 0));
						if ((object)shaderInfo2 != null)
						{
							shaderInfo = shaderInfo2 with
							{
								Counts = CarvedReader.Counts(shaderInfo2.Bindings),
								RootSignature = null
							};
						}
					}
					catch (Exception ex2) when ((ex2 is ArgumentException || ex2 is IndexOutOfRangeException) ? true : false)
					{
						num8++;
					}
					list2.Add((item5.Hash, text, new Loc(item4.FullName, item5.Offset, item5.Size), shaderInfo, (shaderInfo == null) ? null : CarvedReader.LanePlatform(Dxbc.WaveLanes(array))));
				}
				foreach (ShaderPack11.Root root in shaderPack.Roots)
				{
					byte[] array2 = lz4Frame.Read(root.Offset, root.Size);
					if (!Dxbc.IsRootSignatureOnly(array2) || !Dxbc.RootSignatureValid(array2))
					{
						num9++;
					}
					else
					{
						list3.Add((root.Hash, Convert.ToHexStringLower(SHA1.HashData(array2)), new Loc(item4.FullName, root.Offset, root.Size), RootSig.Parse(array2), array2));
					}
				}
			}
			catch (InvalidDataException)
			{
				num2++;
				goto end_IL_00f5;
			}
			long num11 = num7 + num10;
			int num12 = num5 + num8;
			num6 += num9;
			num5 = num12;
			num7 = num11;
			foreach (var (key, text2, loc, shaderInfo3, text3) in list2)
			{
				if (loc != null && dictionary.TryAdd(text2, loc) && shaderInfo3 != null)
				{
					shaders[text2] = shaderInfo3;
					if (text3 != null)
					{
						platformOf[text2] = text3;
					}
				}
				if (shaders.ContainsKey(text2))
				{
					dictionary2.TryAdd(key, text2);
				}
			}
			foreach (var (key2, text4, value, value2, value3) in list3)
			{
				dictionary.TryAdd(text4, value);
				dictionary3.TryAdd(key2, text4);
				ranges.TryAdd(text4, value2);
				dictionary4.TryAdd(text4, value3);
			}
			end_IL_00f5:;
		}
		if (num == 0)
		{
			throw new InvalidDataException(PackDir + ": no SHDRPACK 11 shader pack: scan the game again");
		}
		Dictionary<string, HashSet<string>> dictionary5 = new Dictionary<string, HashSet<string>>();
		HashSet<string> value6;
		foreach (var item6 in list)
		{
			ShaderPack11.Program item = item6.Item2;
			for (int num13 = 0; num13 < Slots.Length; num13++)
			{
				if (Has(item.Slots[num13]) && dictionary2.TryGetValue(item.Slots[num13], out var value4))
				{
					HashSet<string> hashSet3;
					if (!dictionary5.TryGetValue(value4, out var value5))
					{
						value6 = (dictionary5[value4] = new HashSet<string>());
						hashSet3 = value6;
					}
					else
					{
						hashSet3 = value5;
					}
					hashSet3.Add(dictionary3.GetValueOrDefault(item.RootSignature, ""));
				}
			}
		}
		foreach (KeyValuePair<string, HashSet<string>> item7 in dictionary5)
		{
			item7.Deconstruct(out var key3, out value6);
			string key4 = key3;
			HashSet<string> hashSet4 = value6;
			if (hashSet4.Count == 1)
			{
				string text5 = hashSet4.First();
				if (text5 != "")
				{
					shaders[key4] = shaders[key4]with
					{
						RootSignature = text5
					};
				}
			}
		}
		List<ShaderMap> list4 = new List<ShaderMap>();
		HashSet<string> hashSet5 = new HashSet<string>();
		int num14 = 0;
		int num15 = 0;
		int num16 = 0;
		int num17 = 0;
		int num18 = 0;
		int num19 = 0;
		int num20 = 0;
		string text6 = null;
		foreach (var item8 in list)
		{
			string item2 = item8.Item1;
			ShaderPack11.Program item3 = item8.Item2;
			if (!dictionary3.TryGetValue(item3.RootSignature, out var rs))
			{
				num14++;
				continue;
			}
			List<string> list5 = new List<string>();
			bool flag = true;
			for (int num21 = 0; (num21 < Slots.Length) & flag; num21++)
			{
				if (Has(item3.Slots[num21]))
				{
					if (!dictionary2.TryGetValue(item3.Slots[num21], out var value7))
					{
						num15++;
						flag = false;
					}
					else if (shaders[value7].Stage != Slots[num21])
					{
						num16++;
						flag = false;
					}
					else
					{
						list5.Add(value7);
					}
				}
			}
			if (!flag)
			{
				continue;
			}
			if (!Pipeline(item3.Slots))
			{
				num19++;
				continue;
			}
			bool? flag2 = Chain(list5.Select((string h) => shaders[h]).ToList());
			if (flag2.HasValue)
			{
				if (flag2 != true)
				{
					num17++;
					continue;
				}
				string text7 = list5.Select((string h) => RootSig.Uncovered(ranges[rs], shaders[h].Stage, shaders[h])).FirstOrDefault((string w) => w != null);
				if (text7 != null)
				{
					num20++;
					if (text6 == null)
					{
						text6 = text7;
					}
					continue;
				}
				string text8 = CarvedReader.Sha1Hex(string.Join(',', list5.Order(StringComparer.Ordinal)));
				if (hashSet5.Add(text8 + "|" + rs))
				{
					list4.Add(new ShaderMap(text8, item2, list5.Select((string h) => platformOf.GetValueOrDefault(h, "D3D12")).FirstOrDefault((string x) => x != "D3D12", "D3D12"), list5, IsPipeline: true, rs));
				}
			}
			else
			{
				num18++;
			}
		}
		int num22 = dictionary5.Keys.Count((string h) => shaders[h].RootSignature == null);
		located[game.Id] = dictionary;
		IOrderedEnumerable<IGrouping<Stage, ShaderInfo>> source = from s in shaders.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select g;
		log?.Report(string.Concat(new string[19]
		{
			$"{num} shader packs ({(double)num7 / 1000000000.0:F2} GB of containers): {shaders.Count} shaders (",
			string.Join(", ", source.Select((IGrouping<Stage, ShaderInfo> g) => $"{g.Count()} {g.Key}")),
			$"), {dictionary3.Values.Distinct().Count()} root signatures; ",
			$"{list.Count} programs -> {list4.Count} distinct pipelines, {dictionary5.Count - num22} shaders given their root signature",
			(num22 > 0) ? $", {num22} used with several (or one no pack has) left without: their pipelines take their program's" : "",
			(num20 > 0) ? $"; {num20} programs left out: their root signature doesn't cover their shaders (e.g. {text6})" : "",
			(num14 > 0) ? $"; warning: {num14} programs name a root signature no pack has" : "",
			(num15 > 0) ? $"; warning: {num15} programs name a shader no pack has" : "",
			(num16 > 0) ? $"; warning: {num16} programs name a shader of another stage" : "",
			(num19 > 0) ? $"; warning: {num19} programs aren't a pipeline" : "",
			(num17 > 0) ? $"; {num17} programs left out: their stages don't link" : "",
			(num18 > 0) ? $"; {num18} programs left out: a stage writes one output twice" : "",
			(num6 > 0) ? $"; {num6} root signatures the runtime would reject, left out" : "",
			(num3 > 0) ? $"; {num3} packs of another version not read" : "",
			(num4 > 0) ? $"; warning: {num4} packs over the cap of {MaxRecords:N0} table records not read" : "",
			(num2 > 0) ? $"; warning: {num2} packs don't decompress" : "",
			(num5 > 0) ? $"; {num5} unparseable" : "",
			string.Concat(from x in platformOf.Values
				group x by x into g
				select $"; {g.Count()} need {g.Key}, not planned for 32-lane GPUs"),
			$" ({stopwatch.Elapsed.TotalSeconds:F1}s)"
		}));
		string contentHash = Convert.ToHexStringLower(incrementalHash.GetHashAndReset());
		List<string> list6 = new List<string>();
		list6.Add("D3D12");
		list6.AddRange(platformOf.Values.Distinct().Order(StringComparer.Ordinal));
		return new ShaderIndex(contentHash, new _003C_003Ez__ReadOnlyList<string>(list6), shaders, list4, dictionary4);
	}

	private ShaderIndex Index1(Game game, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		Dictionary<string, ShaderInfo> dictionary = new Dictionary<string, ShaderInfo>();
		Dictionary<string, Loc> dictionary2 = new Dictionary<string, Loc>();
		List<ShaderMap> list = new List<ShaderMap>();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		long num4 = 0L;
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
		foreach (FileInfo item in Packs(game).OrderBy((FileInfo f) => f.Name, StringComparer.OrdinalIgnoreCase))
		{
			ct.ThrowIfCancellationRequested();
			using SafeFileHandle safeFileHandle = ShaderPack1.Open(item.FullName);
			if (safeFileHandle == null)
			{
				goto IL_009b;
			}
			List<ShaderPack1.Shader> list2 = ShaderPack1.Read(safeFileHandle);
			if (list2 == null)
			{
				goto IL_009b;
			}
			num++;
			string relativePath = Path.GetRelativePath(game.InstallDir, item.FullName);
			incrementalHash.AppendData(Encoding.UTF8.GetBytes($"{relativePath}|{item.Length}|{item.LastWriteTimeUtc.Ticks}\n"));
			List<string> list3 = new List<string>();
			foreach (ShaderPack1.Shader item2 in list2)
			{
				ct.ThrowIfCancellationRequested();
				num4 += item2.Packed;
				byte[] array = ShaderPack1.Decode(safeFileHandle, item2);
				if (array == null || !Dxbc.Valid(array) || Dxbc.Kind(array) < 0)
				{
					num3++;
					continue;
				}
				string text = Convert.ToHexStringLower(SHA1.HashData(array));
				if (dictionary2.TryAdd(text, new Loc(item.FullName, item2.Offset, item2.Size, item2.Packed)))
				{
					try
					{
						ShaderInfo shaderInfo = ShaderContainer.Parse(array, text, new ResourceCounts(0, 0, 0, 0));
						if ((object)shaderInfo != null)
						{
							dictionary[text] = shaderInfo with
							{
								Counts = CarvedReader.Counts(shaderInfo.Bindings)
							};
						}
					}
					catch (Exception ex) when ((ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
					{
						num3++;
					}
				}
				if (dictionary.ContainsKey(text))
				{
					list3.Add(text);
				}
			}
			if (list3.Count > 0)
			{
				list.Add(new ShaderMap(CarvedReader.Sha1Hex(relativePath), relativePath, "PCD3D_SM5", list3.Distinct().ToList()));
			}
			goto end_IL_008a;
			IL_009b:
			num2++;
			end_IL_008a:;
		}
		if (num == 0)
		{
			throw new InvalidDataException(PackDir + ": no SHDRPACK 1 shader pack: scan the game again");
		}
		located[game.Id] = dictionary2;
		log?.Report($"{num} shader packs ({(double)num4 / 1000000000.0:F2} GB read): {dictionary.Count} DirectX 11 shaders (" + string.Join(", ", from s in dictionary.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select $"{g.Count()} {g.Key}") + "), one pool per pack" + ((num2 > 0) ? $"; {num2} packs of another version not read" : "") + ((num3 > 0) ? $"; {num3} unparseable" : "") + $" ({stopwatch.Elapsed.TotalSeconds:F1}s)");
		return new ShaderIndex(Convert.ToHexStringLower(incrementalHash.GetHashAndReset()), new _003C_003Ez__ReadOnlySingleElementList<string>("PCD3D_SM5"), dictionary, list);
	}

	internal static bool? Chain(IReadOnlyList<ShaderInfo> stages)
	{
		List<ShaderInfo> list = (from x in stages
			where x.Stage != Stage.Compute
			orderby x.Stage switch
			{
				Stage.Vertex => 0, 
				Stage.Hull => 1, 
				Stage.Domain => 2, 
				_ => 3, 
			}
			select x).ToList();
		for (int num = 1; num < list.Count; num++)
		{
			ShaderInfo shaderInfo = list[num - 1];
			ShaderInfo shaderInfo2 = list[num];
			ShaderInfo shaderInfo3 = shaderInfo;
			Dictionary<(string, int), SigElement> dictionary = new Dictionary<(string, int), SigElement>();
			foreach (SigElement output in shaderInfo3.Outputs)
			{
				if (!dictionary.TryAdd((output.Semantic.ToUpperInvariant(), output.Index), output))
				{
					return null;
				}
			}
			if (!Planner.Links(shaderInfo3, shaderInfo2))
			{
				return false;
			}
			if (shaderInfo2.Stage == Stage.Domain && Planner.Sig(shaderInfo3.Outputs) != Planner.Sig(shaderInfo2.Inputs))
			{
				return false;
			}
			foreach (SigElement input in shaderInfo2.Inputs)
			{
				if (dictionary.TryGetValue((input.Semantic.ToUpperInvariant(), input.Index), out var value) && ((input.SysValue == 0 && value.CompType != input.CompType) || value.Register != input.Register))
				{
					return false;
				}
			}
		}
		return true;
	}

	private static bool Has(ulong slot)
	{
		bool flag = ((slot == 0L || slot == ulong.MaxValue) ? true : false);
		return !flag;
	}

	private static bool Pipeline(ulong[] s)
	{
		bool flag = Has(s[0]);
		bool flag2 = Has(s[1]);
		bool flag3 = Has(s[2]);
		bool flag4 = Has(s[3]);
		bool flag5 = Has(s[4]);
		if (!Has(s[5]))
		{
			if (!flag)
			{
				return flag3 & flag4 & flag5;
			}
			return !(flag3 | flag4 | flag5);
		}
		return !(flag | flag2 | flag3 | flag4 | flag5);
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
			group x by x.At.Pack)
		{
			if (engine.Version == "SHDRPACK 1")
			{
				using (SafeFileHandle safeFileHandle = ShaderPack1.Open(item.Key))
				{
					if (safeFileHandle == null)
					{
						continue;
					}
					foreach (var (text, loc) in item.OrderBy(((string Sha, Loc At) x) => x.At.Offset))
					{
						ct.ThrowIfCancellationRequested();
						byte[] array = ShaderPack1.Decode(safeFileHandle, new ShaderPack1.Shader(0uL, 0, loc.Offset, loc.Packed, loc.Size));
						if (array != null && Convert.ToHexStringLower(SHA1.HashData(array)) == text)
						{
							sink(text, array);
						}
					}
				}
				continue;
			}
			using Lz4Frame lz4Frame = Lz4Frame.Open(item.Key);
			if (lz4Frame == null)
			{
				continue;
			}
			foreach (var (text2, loc2) in item.OrderBy(((string Sha, Loc At) x) => x.At.Offset))
			{
				ct.ThrowIfCancellationRequested();
				byte[] array2;
				try
				{
					array2 = lz4Frame.Read(loc2.Offset, loc2.Size);
				}
				catch (InvalidDataException)
				{
					continue;
				}
				if (Convert.ToHexStringLower(SHA1.HashData(array2)) == text2)
				{
					sink(text2, array2);
				}
			}
		}
	}

	private static IEnumerable<FileInfo> Packs(Game game)
	{
		DirectoryInfo directoryInfo = new DirectoryInfo(Path.Combine(game.InstallDir, PackDir));
		if (!directoryInfo.Exists)
		{
			return Array.Empty<FileInfo>();
		}
		return directoryInfo.EnumerateFiles("*.shaderpack", new EnumerationOptions
		{
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None
		});
	}
}
