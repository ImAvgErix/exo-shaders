using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using ZstdSharp;

namespace SCSKiller.Core.ReEngine;

public sealed class ReEngineReader(string dataDir, Func<string, string?>? download = null) : IEngineReader
{
	public sealed record Loc(string Pak, int Entry, int Offset, int Size);

	private sealed class Paks : IDisposable
	{
		public readonly List<RePak> All = new List<RePak>();

		public readonly RePak? Base;

		public string? Error;

		public string? Failed;

		private static readonly Regex Patch = new Regex("\\.patch_(\\d+)\\.pak$", RegexOptions.IgnoreCase);

		public Paks(string dir, IReadOnlyList<BigInteger> moduli)
		{
			string path = Path.Combine(dir, "dlc");
			IEnumerable<string> first = Directory.EnumerateFiles(dir, "re_chunk_*.pak");
			IEnumerable<string> second;
			if (!Directory.Exists(path))
			{
				IEnumerable<string> enumerable = Array.Empty<string>();
				second = enumerable;
			}
			else
			{
				second = Directory.EnumerateFiles(path, "*.pak");
			}
			foreach (string item in first.Concat(second).OrderBy((string p) =>
			{
				Match match = Patch.Match(p);
				return (match != null && match.Success) ? int.Parse(match.Groups[1].Value) : 0;
			}).ThenBy((string p) => p, StringComparer.OrdinalIgnoreCase))
			{
				try
				{
					if (!Patch.IsMatch(item) || new FileInfo(item).Length != 0L)
					{
						All.Add(RePak.Open(item, moduli));
					}
				}
				catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
					if (Path.GetFileName(item).Equals("re_chunk_000.pak", StringComparison.OrdinalIgnoreCase) || Patch.IsMatch(item))
					{
						string error = Error ?? (Path.GetFileName(item) + ": " + ex.Message);
						string failed = Failed ?? item;
						Error = error;
						Failed = failed;
					}
				}
			}
			Base = All.FirstOrDefault((RePak p) => Path.GetFileName(p.Path).Equals("re_chunk_000.pak", StringComparison.OrdinalIgnoreCase));
		}

		public IEnumerable<(RePak Pak, int Index)> Final()
		{
			Dictionary<ulong, (RePak, int)> dictionary = new Dictionary<ulong, (RePak, int)>();
			foreach (RePak item in All)
			{
				for (int i = 0; i < item.Entries.Count; i++)
				{
					dictionary[item.Entries[i].Hash] = (item, i);
				}
			}
			return dictionary.Values.Where(((RePak, int) f) =>
			{
				RePak.Entry entry = f.Item1.Entries[f.Item2];
				return entry.Size > 0 || (entry.Chunked && entry.Packed > 0);
			});
		}

		public void Dispose()
		{
			foreach (RePak item in All)
			{
				item.Dispose();
			}
		}
	}

	public const string Family = "RE Engine";

	public const string ReePakRs = "https://raw.githubusercontent.com/eigeen/ree-pak-rs/25562e6a6f9a52a43b80d54be91e4feb7ac7668b/ree-pak-core/src/pak/cipher/pak.rs";

	private static readonly uint[] ShaderMagics = new uint[2] { 4605011u, 1330201423u };

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	private static readonly Regex Capability = new Regex("^\\s*Capability\\s*=\\s*DirectX(1[12])", RegexOptions.IgnoreCase | RegexOptions.Multiline);

	public string ModulusFile(Game game)
	{
		return Path.Combine(new AppStore(dataDir).GameDir(game.Id), "pak.modulus");
	}

	private List<BigInteger> Moduli(Game game)
	{
		try
		{
			string text = string.Concat(File.ReadAllText(ModulusFile(game)).Replace("0x", "", StringComparison.OrdinalIgnoreCase).Where(Uri.IsHexDigit));
			return (text.Length == 0 || text.Length % 2 != 0) ? new List<BigInteger>() : BothOrders(Convert.FromHexString(text));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return new List<BigInteger>();
		}
	}

	private static List<BigInteger> BothOrders(byte[] b)
	{
		return new BigInteger[2]
		{
			new BigInteger(b, isUnsigned: true),
			new BigInteger(b, isUnsigned: true, isBigEndian: true)
		}.Where((BigInteger m) => m > 1L).ToList();
	}

	public static List<byte[]> ReePakModuli(string source)
	{
		return (from m in Regex.Matches(source, "\\[([^\\[\\]]*)\\]")
			select m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) into t
			where t.Length >= 64 && t.All((string x) => Regex.IsMatch(x, "^0x[0-9A-Fa-f]{1,2}$"))
			select t.Select((string x) => Convert.ToByte(x.Substring(2), 16)).ToArray()).ToList();
	}

	private Paks Open(Game game, string dir)
	{
		Paks paks = new Paks(dir, Moduli(game));
		string? error = paks.Error;
		if (error == null || !error.EndsWith("the entry table is encrypted and no table-key modulus opens it"))
		{
			return paks;
		}
		string text = (download ?? new Func<string, string>(Download.Text))("https://raw.githubusercontent.com/eigeen/ree-pak-rs/25562e6a6f9a52a43b80d54be91e4feb7ac7668b/ree-pak-core/src/pak/cipher/pak.rs");
		if (text != null)
		{
			byte[] array = ReePakModuli(text).FirstOrDefault((byte[] b) => Opens(paks.Failed, BothOrders(b)));
			if (array != null)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(ModulusFile(game)));
				File.WriteAllText(ModulusFile(game), Convert.ToHexString(array));
				paks.Dispose();
				return new Paks(dir, BothOrders(array));
			}
		}
		Paks paks2 = paks;
		paks2.Error = paks2.Error + ", and none that opens it could be downloaded (ree-pak-rs's): put the game's table-key modulus (hex) in " + ModulusFile(game);
		return paks;
	}

	private static bool Opens(string pak, List<BigInteger> moduli)
	{
		try
		{
			RePak.Open(pak, moduli).Dispose();
			return true;
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return false;
		}
	}

	public static string? Find(Game game)
	{
		return new string[2]
		{
			Path.GetDirectoryName(game.ExePath),
			game.InstallDir
		}.FirstOrDefault((string d) => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "re_chunk_000.pak")));
	}

	public static string? Fork(Game game)
	{
		string text = string.Concat(Path.GetFileNameWithoutExtension(game.ExePath).Where((char c) =>
		{
			bool flag = char.IsAsciiLetterOrDigit(c);
			if (!flag)
			{
				bool flag2 = ((c == '-' || c == '_') ? true : false);
				flag = flag2;
			}
			return flag;
		}).Take(64));
		if (text == null || text.Length <= 0)
		{
			return null;
		}
		return text;
	}

	public int ReaderVersion(EngineInfo engine)
	{
		return 3;
	}

	public EngineInfo? Detect(Game game)
	{
		string text = Find(game);
		if (text == null)
		{
			return null;
		}
		using Paks paks = Open(game, text);
		RePak rePak = paks.Base;
		string version = ((rePak != null) ? $"PAK {rePak.Major}.{rePak.Minor}" : "PAK ?");
		if (paks.Error != null)
		{
			return new EngineInfo("RE Engine", version, Fork(game), Api(text, null), Encrypted: false, paks.Error);
		}
		Dictionary<uint, int> dictionary = new Dictionary<uint, int>();
		foreach (var item3 in paks.Final())
		{
			RePak item = item3.Pak;
			int item2 = item3.Index;
			uint? num = Magic(item, item2);
			if (!num.HasValue)
			{
				continue;
			}
			uint valueOrDefault = num.GetValueOrDefault();
			dictionary[valueOrDefault] = dictionary.GetValueOrDefault(valueOrDefault) + 1;
			if (!ShaderMagics.Contains(valueOrDefault))
			{
				continue;
			}
			byte[] b;
			try
			{
				b = item.Read(item.Entries[item2]);
			}
			catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is ZstdException) ? true : false)
			{
				continue;
			}
			List<byte[]> list = (from x in Dxbc.Containers(b)
				select x.Container).ToList();
			if (list.Count > 0)
			{
				return new EngineInfo("RE Engine", version, Fork(game), Api(text, list.Any((byte[] c) => !Dxbc.Part(c, "DXIL"u8).IsEmpty)), Encrypted: false, null);
			}
		}
		string text2 = string.Join(", ", from p in dictionary.OrderByDescending((KeyValuePair<uint, int> p) => p.Value).Take(5)
			select $"{Name(p.Key)} x{p.Value}");
		return new EngineInfo("RE Engine", version, Fork(game), Api(text, null), Encrypted: false, "no shader containers in its packages' material files (commonest files: " + text2 + ")");
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		string text = Find(game) ?? throw new DirectoryNotFoundException(game.InstallDir + ": no re_chunk_000.pak");
		ConcurrentDictionary<string, ShaderInfo> shaders;
		using (Paks paks = Open(game, text))
		{
			if (paks.Error != null)
			{
				throw new InvalidDataException(paks.Error);
			}
			List<(RePak, int)> list = paks.Final().ToList();
			int encrypted = 0;
			int unreadable = 0;
			List<(RePak, int)> list2 = list.AsParallel().WithCancellation(ct).Where(((RePak Pak, int Index) f) =>
			{
				if (f.Pak.Entries[f.Index].Encryption != 0)
				{
					Interlocked.Increment(ref encrypted);
					return false;
				}
				uint? num4 = Magic(f.Pak, f.Index);
				if (num4.HasValue)
				{
					uint valueOrDefault = num4.GetValueOrDefault();
					return ShaderMagics.Contains(valueOrDefault);
				}
				return false;
			})
				.ToList();
			double totalSeconds = stopwatch.Elapsed.TotalSeconds;
			ConcurrentDictionary<string, Loc> locs = new ConcurrentDictionary<string, Loc>();
			shaders = new ConcurrentDictionary<string, ShaderInfo>();
			ConcurrentDictionary<string, string> platformOf = new ConcurrentDictionary<string, string>();
			ConcurrentBag<(string Library, List<string> Shas, List<List<string>>? Pipes)> perFile = new ConcurrentBag<(string, List<string>, List<List<string>>)>();
			long bytes = 0L;
			long containers = 0L;
			long bad = 0L;
			Parallel.ForEach(list2, new ParallelOptions
			{
				TaskScheduler = TaskScheduler.Current,
				CancellationToken = ct
			}, ((RePak Pak, int Index) f) =>
			{
				byte[] array;
				try
				{
					array = f.Pak.Read(f.Pak.Entries[f.Index]);
				}
				catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is ZstdException) ? true : false)
				{
					Interlocked.Increment(ref unreadable);
					return;
				}
				Interlocked.Add(ref bytes, array.Length);
				List<string> list8 = new List<string>();
				Dictionary<long, string> at = new Dictionary<long, string>();
				foreach (var (num4, array2) in Dxbc.Containers(array))
				{
					Interlocked.Increment(ref containers);
					string text4 = Convert.ToHexStringLower(SHA1.HashData(array2));
					list8.Add(text4);
					at[num4] = text4;
					if (locs.TryAdd(text4, new Loc(f.Pak.Path, f.Index, num4, array2.Length)) && Dxbc.Kind(array2) >= 0)
					{
						try
						{
							ShaderInfo shaderInfo = ShaderContainer.Parse(array2, text4, new ResourceCounts(0, 0, 0, 0));
							if ((object)shaderInfo != null)
							{
								platformOf[text4] = Platform(array2);
								shaders[text4] = shaderInfo with
								{
									Counts = CarvedReader.Counts(shaderInfo.Bindings)
								};
							}
						}
						catch (Exception ex2) when ((ex2 is ArgumentException || ex2 is IndexOutOfRangeException) ? true : false)
						{
							Interlocked.Increment(ref bad);
						}
					}
				}
				perFile.Add(($"{Path.GetFileName(f.Pak.Path)}|{f.Pak.Entries[f.Index].Hash:x16}", list8, Pipelines(array, at.Keys.ToHashSet())?.Select((List<long> x) => x.Select((long o) => at[o]).ToList()).ToList()));
			});
			located[game.Id] = locs;
			List<ShaderMap> list3 = new List<ShaderMap>();
			HashSet<string> hashSet = new HashSet<string>();
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			foreach (var item3 in perFile.OrderBy(((string Library, List<string> Shas, List<List<string>> Pipes) p) => p.Library, StringComparer.Ordinal))
			{
				string item = item3.Library;
				List<string> item2 = item3.Shas;
				List<List<string>> list4 = item3.Pipes;
				HashSet<string> named = new HashSet<string>();
				if (list4 != null)
				{
					num2++;
				}
				if (list4 == null)
				{
					list4 = new List<List<string>>();
				}
				foreach (List<string> item4 in list4)
				{
					if (!item4.All(shaders.ContainsKey))
					{
						continue;
					}
					if (!Creatable(item4.Select((string h) => shaders[h]).ToList()))
					{
						num3++;
						continue;
					}
					named.UnionWith(item4);
					num++;
					string text2 = CarvedReader.Sha1Hex(string.Join(',', item4.Order(StringComparer.Ordinal)));
					if (hashSet.Add(text2))
					{
						list3.Add(new ShaderMap(text2, item, platformOf[item4[0]], item4, IsPipeline: true));
					}
				}
				List<string> source = item2.Where(shaders.ContainsKey).Distinct().ToList();
				List<string> list5 = source.Where((string h) => !named.Contains(h)).ToList();
				if (list5.Count == 0)
				{
					continue;
				}
				IEnumerable<string> second = source.Where((string h) => named.Contains(h) && Source(h));
				List<(string, IEnumerable<string>)> list6 = new List<(string, IEnumerable<string>)> { ("", list5.Concat(second)) };
				List<string> list7 = source.Where((string h) => named.Contains(h) && shaders[h].Stage == Stage.Pixel).ToList();
				if (list7.Count > 0 && list5.Any(Source))
				{
					list6.Add(("|listed-ps", list5.Where(Source).Concat(list7)));
				}
				foreach (var item5 in list6)
				{
					var (text3, _) = item5;
					foreach (IGrouping<string, string> item6 in from h in item5.Item2
						group h by platformOf[h])
					{
						list3.Add(new ShaderMap(CarvedReader.Sha1Hex(item + "|" + item6.Key + text3), item, item6.Key, item6.ToList()));
					}
				}
			}
			using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
			foreach (RePak item7 in paks.All)
			{
				FileInfo fileInfo = new FileInfo(item7.Path);
				incrementalHash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(text, item7.Path)}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}\n"));
			}
			log?.Report($"{paks.All.Count} packages, {list.Count} files (probed in {totalSeconds:F1}s{((encrypted > 0) ? $", {encrypted} resource-encrypted skipped" : "")}): {list2.Count} shader files{((unreadable > 0) ? $" ({unreadable} unreadable)" : "")}, {(double)bytes / 1000000000.0:F2} GB decompressed, {containers} containers -> {shaders.Count} shaders (" + string.Join(", ", from s in shaders.Values
				group s by s.Stage into g
				orderby g.Count() descending
				select $"{g.Count()} {g.Key}") + $"){((bad > 0) ? $", {bad} unparseable" : "")}, {shaders.Values.Count((ShaderInfo s) => s.RootSignature != null)} with a root signature; {num2} of {list2.Count} files list their pipelines: " + $"{num} -> {list3.Count((ShaderMap m) => m.IsPipeline)} distinct{((num3 > 0) ? $" ({num3} more whose stages don't link left out)" : "")}, {list3.Count((ShaderMap m) => !m.IsPipeline)} pools ({stopwatch.Elapsed.TotalSeconds:F1}s)");
			return new ShaderIndex(Convert.ToHexStringLower(incrementalHash.GetHashAndReset()), list3.Select((ShaderMap m) => m.Platform).Distinct().Order(StringComparer.Ordinal)
				.ToList(), new Dictionary<string, ShaderInfo>(shaders), list3);
		}
		bool Source(string h)
		{
			Stage stage = shaders[h].Stage;
			if (stage == Stage.Vertex || stage == Stage.Mesh)
			{
				return true;
			}
			return false;
		}
	}

	internal static List<List<long>>? Pipelines(byte[] data, IReadOnlySet<long> containers)
	{
		if (data.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 4605011)
		{
			return null;
		}
		var (num, num2) = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) switch
		{
			4 => (32, 272), 
			5 => (24, 264), 
			_ => (0, 0), 
		};
		if (num == 0)
		{
			return null;
		}
		long num3 = 4L * (long)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
		if (num + num3 * num2 > data.Length)
		{
			return null;
		}
		List<List<long>> list = new List<List<long>>();
		for (int i = 0; i < num3; i++)
		{
			Span<byte> span = data.AsSpan(num + i * num2, 56);
			List<long> list2 = new List<long>();
			for (int j = 0; j < 7; j++)
			{
				long num4 = (long)BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8 * j));
				if (num4 != 0L)
				{
					if (!containers.Contains(num4))
					{
						return null;
					}
					if (j != 1)
					{
						list2.Add(num4);
					}
				}
			}
			if (list2.Count > 0)
			{
				list.Add(list2);
			}
		}
		return list;
	}

	internal static bool Creatable(IReadOnlyList<ShaderInfo> stages)
	{
		if (stages.DistinctBy((ShaderInfo s) => s.Stage).Count() < stages.Count)
		{
			return false;
		}
		Dictionary<Stage, ShaderInfo> by = stages.ToDictionary((ShaderInfo s) => s.Stage);
		if (by.TryGetValue(Stage.Mesh, out var value))
		{
			if (!by.ContainsKey(Stage.Vertex) && (!by.TryGetValue(Stage.Amplification, out var value2) || Planner.Launches(value2, value)))
			{
				if (by.TryGetValue(Stage.Pixel, out var value3))
				{
					return Written(value, value3, sameRegister: false);
				}
				return true;
			}
			return false;
		}
		ShaderInfo shaderInfo = null;
		foreach (ShaderInfo item in from x in new Stage[5]
			{
				Stage.Vertex,
				Stage.Hull,
				Stage.Domain,
				Stage.Geometry,
				Stage.Pixel
			}.Where(@by.ContainsKey)
			select @by[x])
		{
			bool flag = shaderInfo != null;
			bool flag2;
			ShaderInfo src;
			ShaderInfo dst;
			bool sameRegister;
			if (flag)
			{
				flag2 = !Planner.Links(shaderInfo, item);
				if (!flag2)
				{
					src = shaderInfo;
					dst = item;
					string[] array = item.ShaderModel.Split('_');
					if (array != null && array.Length >= 2)
					{
						string text = array[1];
						if (text == "4" || text == "5")
						{
							sameRegister = true;
							goto IL_018d;
						}
					}
					sameRegister = false;
					goto IL_018d;
				}
				goto IL_019d;
			}
			goto IL_01a1;
			IL_019d:
			flag = flag2;
			goto IL_01a1;
			IL_018d:
			flag2 = !Written(src, dst, sameRegister);
			goto IL_019d;
			IL_01a1:
			if (flag)
			{
				return false;
			}
			shaderInfo = item;
		}
		return true;
		static bool Written(ShaderInfo shaderInfo2, ShaderInfo shaderInfo3, bool sameRegister)
		{
			Dictionary<(string, int Index), SigElement> outs = (from o in shaderInfo2.Outputs
				group o by (o.Semantic.ToUpperInvariant(), Index: o.Index)).ToDictionary((IGrouping<(string, int Index), SigElement> g) => g.Key, (IGrouping<(string, int Index), SigElement> g) => g.First());
			return shaderInfo3.Inputs.All((SigElement i) => outs.TryGetValue((i.Semantic.ToUpperInvariant(), i.Index), out var value4) ? ((i.SysValue != 0 || value4.CompType == i.CompType) && (!sameRegister || value4.Register == i.Register)) : (i.SysValue != 0));
		}
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		if (!located.TryGetValue(game.Id, out IReadOnlyDictionary<string, Loc> locs))
		{
			Index(game, engine, null, ct);
			locs = located[game.Id];
		}
		List<BigInteger> moduli = Moduli(game);
		foreach (IGrouping<string, (string, Loc)> item in from s in sha1s.Where(locs.ContainsKey)
			select (Sha: s, At: locs[s]) into x
			group x by x.At.Pak)
		{
			using RePak rePak = RePak.Open(item.Key, moduli);
			foreach (IGrouping<int, (string, Loc)> item2 in item.GroupBy(((string Sha, Loc At) x) => x.At.Entry))
			{
				ct.ThrowIfCancellationRequested();
				byte[] array;
				try
				{
					array = rePak.Read(rePak.Entries[item2.Key]);
				}
				catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is ZstdException || ex is ArgumentOutOfRangeException) ? true : false)
				{
					continue;
				}
				foreach (var (text, loc) in item2)
				{
					if (loc.Offset + loc.Size <= array.Length)
					{
						Span<byte> span = array.AsSpan(loc.Offset, loc.Size);
						if (Convert.ToHexStringLower(SHA1.HashData(span)) == text)
						{
							sink(text, span.ToArray());
						}
					}
				}
			}
		}
	}

	private static string Platform(byte[] c)
	{
		if (!Dxbc.Part(c, "DXIL"u8).IsEmpty)
		{
			return "PCD3D_SM6";
		}
		return "PCD3D_SM5";
	}

	private static uint? Magic(RePak pak, int i)
	{
		try
		{
			byte[] array = pak.Read(pak.Entries[i], 4);
			return (array != null && array.Length == 4) ? new uint?(BinaryPrimitives.ReadUInt32LittleEndian(array)) : ((uint?)null);
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is ZstdException) ? true : false)
		{
			return null;
		}
	}

	private static string Name(uint m)
	{
		return string.Concat(BitConverter.GetBytes(m).Select((byte c) =>
		{
			if (c < 32 || c >= 127)
			{
				return $"\\x{c:x2}";
			}
			char c2 = (char)c;
			return c2.ToString();
		}));
	}

	private static string Api(string dir, bool? dxil)
	{
		try
		{
			Match match = Capability.Match(File.ReadAllText(Path.Combine(dir, "config.ini")));
			if (match != null && match.Success)
			{
				return "D3D" + match.Groups[1].Value;
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
		if (dxil != true)
		{
			return "D3D11 or D3D12";
		}
		return "D3D12";
	}
}
