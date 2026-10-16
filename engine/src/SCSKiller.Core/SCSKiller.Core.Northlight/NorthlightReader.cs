using System;
using System.Buffers.Binary;
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

namespace SCSKiller.Core.Northlight;

public sealed class NorthlightReader : IEngineReader
{
	public sealed record Loc(string Path, long Offset, int Size);

	public readonly record struct Container(long Offset, int Size, int Kind);

	public const string Family = "Northlight";

	public const string Version = "DX12";

	public const string Libraries = "libraries";

	public const string Platform11 = "D3D11";

	private const string Dir = "data\\shaders\\build\\pc_dxil";

	private const string Dir11 = "data\\shaders\\build\\pc_dx11";

	private const int DetectFiles = 8;

	private const long MaxFile = 536870912L;

	private const int MaxShadersPerFile = 262144;

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	internal int MaxRecords { get; init; } = 1048576;

	public int ReaderVersion(EngineInfo engine)
	{
		return 2;
	}

	public EngineInfo? Detect(Game game)
	{
		string text = Path.Combine(game.InstallDir, "data\\shaders\\build\\pc_dxil");
		if (!Directory.Exists(text) || CarvedReader.IsNorthlight2(game.InstallDir))
		{
			return null;
		}
		foreach (FileInfo item in (from f in Effects(text)
			orderby f.Length
			select f).Take(8))
		{
			List<List<Container>> list = Records(Read(item.FullName), default);
			if (list != null && list.Any((List<Container> r) => r.Select((Container c) => c.Kind).Order().SequenceEqual(new _003C_003Ez__ReadOnlyArray<int>(new int[2] { 0, 1 }))))
			{
				return new EngineInfo("Northlight", "DX12", null, Effects(Path.Combine(game.InstallDir, "data\\shaders\\build\\pc_dx11")).Any() ? "D3D11 or D3D12" : "D3D12", Encrypted: false, null);
			}
		}
		return null;
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		Dictionary<string, ShaderInfo> shaders = new Dictionary<string, ShaderInfo>();
		Dictionary<string, Loc> dictionary = new Dictionary<string, Loc>();
		List<ShaderMap> list = new List<ShaderMap>();
		HashSet<string> hashSet = new HashSet<string>();
		List<string> list2 = new List<string>();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		int num6 = 0;
		int num7 = 0;
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
		(string, bool)[] array = new (string, bool)[2]
		{
			("data\\shaders\\build\\pc_dxil", false),
			("data\\shaders\\build\\pc_dx11", true)
		};
		int i;
		for (i = 0; i < array.Length; i++)
		{
			var (path, flag) = array[i];
			foreach (FileInfo item in Effects(Path.Combine(game.InstallDir, path)).OrderBy((FileInfo f) => f.Name, StringComparer.OrdinalIgnoreCase))
			{
				ct.ThrowIfCancellationRequested();
				string relativePath = Path.GetRelativePath(game.InstallDir, item.FullName);
				byte[] array2 = Read(item.FullName);
				List<List<Container>> list3 = Records(array2, ct);
				if (list3 == null)
				{
					continue;
				}
				incrementalHash.AppendData(Encoding.UTF8.GetBytes($"{relativePath}|{item.Length}|{item.LastWriteTimeUtc.Ticks}\n"));
				List<string> list4 = new List<string>();
				foreach (List<Container> item2 in list3)
				{
					if (num2++ >= MaxRecords)
					{
						num6++;
						continue;
					}
					List<string> list5 = new List<string>();
					foreach (Container item3 in item2)
					{
						Span<byte> span = array2.AsSpan((int)item3.Offset, item3.Size);
						string text = Convert.ToHexStringLower(SHA1.HashData(span));
						if (dictionary.TryAdd(text, new Loc(item.FullName, item3.Offset, item3.Size)))
						{
							try
							{
								ShaderInfo shaderInfo = ShaderContainer.Parse(span, text, new ResourceCounts(0, 0, 0, 0));
								if ((object)shaderInfo != null)
								{
									shaders[text] = shaderInfo with
									{
										Counts = CarvedReader.Counts(shaderInfo.Bindings),
										RootSignature = null
									};
								}
							}
							catch (Exception ex) when ((ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
							{
								num7++;
							}
						}
						if (shaders.ContainsKey(text))
						{
							list5.Add(text);
						}
					}
					if (flag)
					{
						list4.AddRange(list5.Where((string h) => Planner.IsD3D11(shaders[h])));
					}
					else
					{
						if (list5.Count != item2.Count)
						{
							continue;
						}
						if (item2 != null && item2.Count == 1 && item2[0].Kind == 6)
						{
							list2.Add(list5[0]);
							continue;
						}
						if (!Pipeline(item2))
						{
							list4.AddRange(list5);
							num3 += list5.Count;
							continue;
						}
						bool? flag2 = Feeds(list5.Select((string h) => shaders[h]).ToList());
						if (flag2.HasValue)
						{
							if (flag2 != true)
							{
								num4++;
								continue;
							}
							num++;
							string text2 = CarvedReader.Sha1Hex(string.Join(',', list5.Order(StringComparer.Ordinal)));
							if (hashSet.Add(text2))
							{
								list.Add(new ShaderMap(text2, relativePath, "D3D12", list5, IsPipeline: true));
							}
						}
						else
						{
							num5++;
						}
					}
				}
				if (list4.Count > 0)
				{
					list.Add(new ShaderMap(CarvedReader.Sha1Hex(relativePath), relativePath, flag ? "D3D11" : "D3D12", list4.Distinct().ToList()));
				}
			}
		}
		list2 = list2.Distinct().ToList();
		if (list2.Count > 0)
		{
			list.Add(new ShaderMap(CarvedReader.Sha1Hex("data\\shaders\\build\\pc_dxil|libraries"), "libraries", "D3D12", list2));
		}
		if (num == 0)
		{
			throw new InvalidDataException("data\\shaders\\build\\pc_dxil: no pipeline record in its effect files: scan the game again");
		}
		located[game.Id] = dictionary;
		int num8 = list.Where((ShaderMap m) => m.Platform == "D3D11").SelectMany((ShaderMap m) => m.Shaders).Distinct()
			.Count();
		log?.Report($"{shaders.Count} shaders ({string.Join(", ", from s in shaders.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select $"{g.Count()} {g.Key}")}), {num} pipeline records -> {list.Count((ShaderMap m) => m.IsPipeline)} distinct pipelines, {list2.Count} ray tracing libraries" + ((num4 > 0) ? $"; {num4} records left out: their VS doesn't feed their PS (the runtime rejects them)" : "") + ((num5 > 0) ? $"; {num5} records left out: a VS writes one output twice" : "") + ((num6 > 0) ? $"; warning: {num6} records over the cap of {MaxRecords} left out" : "") + ((num3 > 0) ? $"; {num3} shaders of records that aren't a pipeline, pooled per file" : "") + ((num7 > 0) ? $"; {num7} unparseable" : "") + ((num8 > 0) ? $"; {num8} of them DirectX 11 shaders ({"data\\shaders\\build\\pc_dx11"})" : "") + $" ({stopwatch.Elapsed.TotalSeconds:F1}s)");
		string contentHash = Convert.ToHexStringLower(incrementalHash.GetHashAndReset());
		string text3 = "D3D12";
		string[] array3 = ((num8 <= 0) ? Array.Empty<string>() : new string[1] { "D3D11" });
		i = 0;
		string[] array4 = new string[1 + array3.Length];
		array4[i] = text3;
		i++;
		ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(array3);
		readOnlySpan.CopyTo(new Span<string>(array4).Slice(i, readOnlySpan.Length));
		i += readOnlySpan.Length;
		return new ShaderIndex(contentHash, new _003C_003Ez__ReadOnlyArray<string>(array4), shaders, list);
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
					byte[] array = new byte[loc.Size];
					if (RandomAccess.Read(safeFileHandle, array, loc.Offset) == array.Length && Convert.ToHexStringLower(SHA1.HashData(array)) == text)
					{
						sink(text, array);
					}
				}
			}
		}
	}

	public static List<List<Container>>? Records(byte[] b, CancellationToken ct)
	{
		if (b.Length < 8 || !((ReadOnlySpan<byte>)b.AsSpan(0, 4)).SequenceEqual("RFX "u8))
		{
			return null;
		}
		List<List<Container>> list = new List<List<Container>>();
		int num = -1;
		int num2 = 0;
		foreach (var (num3, array) in Dxbc.Containers(b))
		{
			if (++num2 > 262144)
			{
				return null;
			}
			if (num2 % 4096 == 0)
			{
				ct.ThrowIfCancellationRequested();
			}
			if (num3 < 4 || BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(num3 - 4)) != array.Length)
			{
				return null;
			}
			Container item = new Container(num3, array.Length, Dxbc.Kind(array));
			if (num < 0 || !EntryPoint(b.AsSpan(num, num3 - num)))
			{
				list.Add(new List<Container>());
			}
			list[list.Count - 1].Add(item);
			num = num3 + array.Length;
		}
		return list;
	}

	private static bool EntryPoint(ReadOnlySpan<byte> gap)
	{
		if (gap.Length < 21)
		{
			return false;
		}
		int num = BinaryPrimitives.ReadInt32LittleEndian(gap);
		if (num < 1 || gap.Length != num + 20)
		{
			return false;
		}
		ReadOnlySpan<byte> readOnlySpan = gap.Slice(4, num);
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			byte b = readOnlySpan[i];
			if (!char.IsAsciiLetterOrDigit((char)b) && b != 95)
			{
				return false;
			}
		}
		return !gap.Slice(4 + num, 12).ContainsAnyExcept((byte)0);
	}

	private static bool Pipeline(List<Container> r)
	{
		if (r.DistinctBy((Container c) => c.Kind).Count() == r.Count)
		{
			if (!r.All((Container c) => Dxbc.IsGraphics(c.Kind)) || !r.Any((Container c) => c.Kind == 1))
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

	internal static bool? Feeds(IReadOnlyList<ShaderInfo> stages)
	{
		ShaderInfo shaderInfo = stages.FirstOrDefault((ShaderInfo s) => s.Stage == Stage.Vertex);
		if ((object)shaderInfo != null)
		{
			ShaderInfo shaderInfo2 = stages.FirstOrDefault((ShaderInfo s) => s.Stage == Stage.Pixel);
			if ((object)shaderInfo2 != null)
			{
				Dictionary<(string, int), int> outs = new Dictionary<(string, int), int>();
				foreach (SigElement output in shaderInfo.Outputs)
				{
					if (!outs.TryAdd((output.Semantic.ToUpperInvariant(), output.Index), output.Register))
					{
						return null;
					}
				}
				return Planner.Links(shaderInfo, shaderInfo2) && shaderInfo2.Inputs.All((SigElement i) => !outs.TryGetValue((i.Semantic.ToUpperInvariant(), i.Index), out var value) || value == i.Register);
			}
		}
		return true;
	}

	private static IEnumerable<FileInfo> Effects(string dir)
	{
		if (!Directory.Exists(dir))
		{
			return Array.Empty<FileInfo>();
		}
		return new DirectoryInfo(dir).EnumerateFiles("*.obj", new EnumerationOptions
		{
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None
		});
	}

	private static byte[] Read(string path)
	{
		try
		{
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			Span<byte> span = stackalloc byte[4];
			if (fileStream.Length > 536870912 || fileStream.ReadAtLeast(span, 4, throwOnEndOfStream: false) < 4 || !((ReadOnlySpan<byte>)span).SequenceEqual("RFX "u8))
			{
				return Array.Empty<byte>();
			}
			byte[] array = new byte[fileStream.Length];
			span.CopyTo(array);
			fileStream.ReadExactly(array.AsSpan(4));
			return array;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return Array.Empty<byte>();
		}
	}
}
