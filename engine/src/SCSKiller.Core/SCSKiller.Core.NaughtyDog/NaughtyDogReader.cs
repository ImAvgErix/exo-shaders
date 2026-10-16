using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Nixxes;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.NaughtyDog;

public sealed class NaughtyDogReader : IEngineReader
{
	public sealed record Loc(string Archive, int Entry, long Offset, int Size);

	public const string Family = "Naughty Dog";

	public const string Version = "ndshader";

	private const string Globals = "shaders.psarc";

	private const long DetectBudget = 67108864L;

	private const long MaxEntry = 1073741824L;

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	public int MaxRootsPerShader { get; init; } = 16;

	public int MaxExtraRoots { get; init; } = 1048576;

	public int ReaderVersion(EngineInfo engine)
	{
		return 2;
	}

	public EngineInfo? Detect(Game game)
	{
		NixxesReader.Meter meter = new NixxesReader.Meter(67108864L);
		foreach (FileInfo item in from f in Archives(game)
			orderby !f.Name.Equals("shaders.psarc", StringComparison.OrdinalIgnoreCase), f.Length
			select f)
		{
			using Psarc psarc = Psarc.Open(item.FullName, meter);
			if (psarc == null)
			{
				continue;
			}
			for (int num = 1; num < psarc.Count && meter.Left > 0; num++)
			{
				if (Scanned(item.Name, psarc.Names[num]) && meter.Take(psarc.Size(num)))
				{
					byte[] d;
					try
					{
						d = psarc.Read(num, 0L, (int)psarc.Size(num));
					}
					catch (InvalidDataException)
					{
						continue;
					}
					if (Shaders(d).Any(((int At, int Size, int RsSize) s) => Dxbc.Kind(d.AsSpan(s.At, s.Size)) >= 0))
					{
						return new EngineInfo("Naughty Dog", "ndshader", null, "D3D12", Encrypted: false, null);
					}
				}
			}
			if (meter.Left > 0)
			{
				continue;
			}
			break;
		}
		return null;
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		Dictionary<string, ShaderInfo> shaders = new Dictionary<string, ShaderInfo>();
		Dictionary<string, Loc> dictionary = new Dictionary<string, Loc>();
		Dictionary<string, List<(string, string)>> dictionary2 = new Dictionary<string, List<(string, string)>>();
		HashSet<string> hashSet = new HashSet<string>();
		HashSet<string> hashSet2 = new HashSet<string>();
		HashSet<string> hashSet3 = hashSet;
		int num = 0;
		Dictionary<string, string> platformOf = new Dictionary<string, string>();
		RootSigCarriers rootSigCarriers = new RootSigCarriers();
		List<ShaderMap> list = new List<ShaderMap>();
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		int num6 = 0;
		int num7 = 0;
		long num8 = 0L;
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
		foreach (FileInfo item4 in Archives(game).OrderBy((FileInfo f) => f.FullName, StringComparer.OrdinalIgnoreCase))
		{
			using Psarc psarc = Psarc.Open(item4.FullName);
			if (psarc == null)
			{
				continue;
			}
			if (psarc.Oodle)
			{
				Codecs.Load(zlib: false);
			}
			string relativePath = Path.GetRelativePath(game.InstallDir, item4.FullName);
			incrementalHash.AppendData(Encoding.UTF8.GetBytes($"{relativePath}|{item4.Length}|{item4.LastWriteTimeUtc.Ticks}\n"));
			for (int num9 = 1; num9 < psarc.Count; num9++)
			{
				if (!Scanned(item4.Name, psarc.Names[num9]))
				{
					continue;
				}
				ct.ThrowIfCancellationRequested();
				if (psarc.Size(num9) > 1073741824)
				{
					num6++;
					continue;
				}
				byte[] array;
				try
				{
					array = psarc.Read(num9, 0L, (int)psarc.Size(num9));
				}
				catch (InvalidDataException)
				{
					num7++;
					continue;
				}
				num8 += array.Length;
				string text = relativePath + ":" + psarc.Names[num9];
				List<string> list2 = new List<string>();
				foreach (var item5 in Shaders(array))
				{
					int item = item5.At;
					int item2 = item5.Size;
					int item3 = item5.RsSize;
					num2++;
					Span<byte> span = array.AsSpan(item, item2);
					string text2 = Convert.ToHexStringLower(SHA1.HashData(span));
					if (dictionary.TryAdd(text2, new Loc(item4.FullName, num9, item, item2)))
					{
						try
						{
							ShaderInfo shaderInfo = ShaderContainer.Parse(span, text2, new ResourceCounts(0, 0, 0, 0));
							if ((object)shaderInfo != null)
							{
								shaders[text2] = shaderInfo with
								{
									Counts = CarvedReader.Counts(shaderInfo.Bindings)
								};
								string text3 = CarvedReader.LanePlatform(Dxbc.WaveLanes(span));
								if (text3 != null)
								{
									platformOf[text2] = text3;
								}
								if (!Dxbc.Part(span, "RTS0"u8).IsEmpty)
								{
									if (Dxbc.RootSignatureValid(span))
									{
										rootSigCarriers.See(span, text2, Dxbc.Kind(span));
									}
									else
									{
										num4++;
									}
								}
							}
						}
						catch (Exception ex2) when ((ex2 is ArgumentException || ex2 is IndexOutOfRangeException) ? true : false)
						{
							num5++;
						}
					}
					if (!shaders.ContainsKey(text2))
					{
						continue;
					}
					list2.Add(text2);
					if (item3 == 0)
					{
						continue;
					}
					Span<byte> span2 = array.AsSpan(item + item2, item3);
					if (!Dxbc.RootSignatureValid(span2))
					{
						num4++;
						continue;
					}
					string rsSha = Convert.ToHexStringLower(SHA1.HashData(span2));
					if (!dictionary2.TryGetValue(text2, out var value))
					{
						int num10 = 1;
						List<(string, string)> list3 = new List<(string, string)>(num10);
						CollectionsMarshal.SetCount(list3, num10);
						CollectionsMarshal.AsSpan(list3)[0] = (rsSha, text);
						dictionary2[text2] = list3;
					}
					else if (!value.Exists(((string Rs, string Lib) r) => r.Rs == rsSha))
					{
						if (shaders[text2].Stage != Stage.Compute)
						{
							hashSet2.Add(text2);
							continue;
						}
						if (value.Count >= MaxRootsPerShader || num >= MaxExtraRoots)
						{
							hashSet3.Add(text2);
							continue;
						}
						value.Add((rsSha, text));
						num++;
					}
					dictionary.TryAdd(rsSha, new Loc(item4.FullName, num9, item + item2, item3));
					rootSigCarriers.See(span2, rsSha, -1);
				}
				if (list2.Count == 0)
				{
					continue;
				}
				num3++;
				foreach (IGrouping<string, string> item6 in from h in list2.Distinct()
					group h by platformOf.GetValueOrDefault(h, "D3D12"))
				{
					list.Add(new ShaderMap(CarvedReader.Sha1Hex((item6.Key == "D3D12") ? text : (text + "|" + item6.Key)), text, item6.Key, item6.ToList()));
				}
			}
		}
		if (shaders.Count == 0)
		{
			throw new InvalidDataException("build\\pc: no shader in its archives: scan the game again");
		}
		rootSigCarriers.Apply(shaders);
		foreach (var (text5, list5) in dictionary2)
		{
			shaders[text5] = shaders[text5]with
			{
				RootSignature = list5[0].Item1
			};
			if (list5.Count <= 1)
			{
				continue;
			}
			foreach (var (rootSignature, library) in list5)
			{
				list.Add(new ShaderMap(CarvedReader.Sha1Hex(text5), library, platformOf.GetValueOrDefault(text5, "D3D12"), new _003C_003Ez__ReadOnlySingleElementList<string>(text5), IsPipeline: true, rootSignature));
			}
		}
		List<List<(string, string)>> list6 = dictionary2.Values.Where((List<(string Rs, string Lib)> r) => r.Count > 1).ToList();
		located[game.Id] = dictionary;
		IOrderedEnumerable<IGrouping<Stage, ShaderInfo>> source = from s in shaders.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select g;
		log?.Report(string.Concat(new string[14]
		{
			$"{num3} packages with shaders ({(double)num8 / 1000000000.0:F2} GB decompressed): {num2} shader containers, {shaders.Count} shaders (",
			string.Join(", ", source.Select((IGrouping<Stage, ShaderInfo> g) => $"{g.Count()} {g.Key}")),
			$"), {shaders.Values.Select((ShaderInfo s) => s.RootSignature).Concat(list.Select((ShaderMap m) => m.RootSignature)).OfType<string>().Distinct().Count()} root signatures, carried by ",
			string.Join(", ", source.Select((IGrouping<Stage, ShaderInfo> g) => $"{g.Count((ShaderInfo s) => s.RootSignature != null)} {g.Key}")),
			$"; {shaders.Values.Count((ShaderInfo s) => s.Stage == Stage.Library)} ray tracing libraries, {shaders.Values.Count((ShaderInfo s) => s.InlineRayTracing)} tracing rays inline",
			(list6.Count > 0) ? $"; {list6.Count} compute shaders ship with several root signatures: a pipeline per root signature, {list6.Sum((List<(string Rs, string Lib)> r) => r.Count)} in all" : "",
			string.Concat(from h in hashSet2
				group h by shaders[h].Stage into g
				select $"; {g.Count()} {g.Key} shaders ship with several root signatures: the first kept, the files don't pair them"),
			(hashSet3.Count > 0) ? $"; warning: {hashSet3.Count} shaders ship with more root signatures than kept ({MaxRootsPerShader} a shader, {MaxExtraRoots:N0} more in all)" : "",
			(num4 > 0) ? $"; {num4} root signatures the runtime would reject, left out" : "",
			(num6 > 0) ? $"; warning: {num6} entries over {1L} GB not read" : "",
			(num7 > 0) ? $"; warning: {num7} entries don't decompress" : "",
			(num5 > 0) ? $"; {num5} unparseable" : "",
			string.Concat(from p in platformOf.Values
				group p by p into g
				select $"; {g.Count()} need {g.Key}, not planned for 32-lane GPUs"),
			$" ({stopwatch.Elapsed.TotalSeconds:F1}s)"
		}));
		string contentHash = Convert.ToHexStringLower(incrementalHash.GetHashAndReset());
		List<string> list7 = new List<string>();
		list7.Add("D3D12");
		list7.AddRange(platformOf.Values.Distinct().Order(StringComparer.Ordinal));
		return new ShaderIndex(contentHash, new _003C_003Ez__ReadOnlyList<string>(list7), shaders, list, rootSigCarriers.Blobs);
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
			group x by x.At.Archive)
		{
			using Psarc psarc = Psarc.Open(item.Key);
			if (psarc == null)
			{
				continue;
			}
			if (psarc.Oodle)
			{
				Codecs.Load(zlib: false);
			}
			foreach (var (text, loc) in item.OrderBy(((string Sha, Loc At) x) => x.At.Entry).ThenBy(((string Sha, Loc At) x) => x.At.Offset))
			{
				ct.ThrowIfCancellationRequested();
				byte[] array;
				try
				{
					array = psarc.Read(loc.Entry, loc.Offset, loc.Size);
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

	public static IEnumerable<(int At, int Size, int RsSize)> Shaders(byte[] d)
	{
		bool any = false;
		foreach (var item2 in Records(d))
		{
			any = true;
			yield return item2;
		}
		if (any)
		{
			yield break;
		}
		foreach (var (item, array) in Dxbc.Containers(d))
		{
			yield return (At: item, Size: array.Length, RsSize: 0);
		}
	}

	public static IEnumerable<(int At, int Size, int RsSize)> Records(byte[] d)
	{
		int num = ((ReadOnlySpan<byte>)d.AsSpan()).IndexOf("ndshader"u8);
		while (num >= 0)
		{
			int num2 = num + 8;
			if (num + 40 <= d.Length)
			{
				long size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(num + 32));
				long rsSize = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(num + 36));
				int at = num + 40;
				if (size >= 32 && at + size + rsSize <= d.Length && Dxbc.HeaderSize(d.AsSpan(at)) == size && Dxbc.Valid(d.AsSpan(at, (int)size)) && (rsSize == 0L || Dxbc.IsRootSignatureOnly(d.AsSpan(at + (int)size, (int)rsSize))))
				{
					yield return (At: at, Size: (int)size, RsSize: (int)rsSize);
					num2 = at + (int)(size + rsSize);
				}
			}
			int num3 = ((ReadOnlySpan<byte>)d.AsSpan(num2)).IndexOf("ndshader"u8);
			num = ((num3 < 0) ? (-1) : (num2 + num3));
		}
	}

	private static bool Scanned(string archive, string? name)
	{
		if (name != null)
		{
			if (!archive.Equals("shaders.psarc", StringComparison.OrdinalIgnoreCase))
			{
				if (name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && !name.Contains("psodict", StringComparison.OrdinalIgnoreCase))
				{
					return !name.Contains("texturedict", StringComparison.OrdinalIgnoreCase);
				}
				return false;
			}
			return true;
		}
		return false;
	}

	private static IEnumerable<FileInfo> Archives(Game game)
	{
		DirectoryInfo directoryInfo = new DirectoryInfo(game.InstallDir);
		if (!directoryInfo.Exists)
		{
			return Array.Empty<FileInfo>();
		}
		EnumerationOptions flat = new EnumerationOptions
		{
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None
		};
		(IReadOnlyList<CollectionGame>, CollectionGame)? tuple = Collections.Member(game.Id);
		object obj;
		if (tuple.HasValue)
		{
			(IReadOnlyList<CollectionGame>, CollectionGame) valueOrDefault = tuple.GetValueOrDefault();
			obj = Path.GetFullPath(Path.Combine(directoryInfo.FullName, valueOrDefault.Item2.Data));
		}
		else
		{
			obj = null;
		}
		string only = (string)obj;
		return (from d in (from d in new DirectoryInfo[1] { directoryInfo }.Concat(directoryInfo.EnumerateDirectories("*", flat))
				select new DirectoryInfo(Path.Combine(d.FullName, "build", "pc")) into d
				where d.Exists
				select d).SelectMany((DirectoryInfo pc) => pc.EnumerateDirectories("*", flat))
			where File.Exists(Path.Combine(d.FullName, "shaders.psarc")) && (only == null || Path.GetFullPath(d.FullName).Equals(only, StringComparison.OrdinalIgnoreCase))
			select d).SelectMany((DirectoryInfo d) =>
		{
			List<FileInfo> list = d.EnumerateFiles("*.psarc", new EnumerationOptions
			{
				RecurseSubdirectories = true,
				IgnoreInaccessible = true,
				AttributesToSkip = FileAttributes.None
			}).ToList();
			return (!list.Any(Fx)) ? list : list.Where((FileInfo f) => Fx(f) || f.Name.Equals("shaders.psarc", StringComparison.OrdinalIgnoreCase));
		});
		static bool Fx(FileInfo f)
		{
			return f.Name.StartsWith("updated-fx-paks", StringComparison.OrdinalIgnoreCase);
		}
	}
}
