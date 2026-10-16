using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;
using ZstdSharp;

namespace SCSKiller.Core.Dagor;

public sealed class DagorReader : IEngineReader
{
	internal sealed record Entry(byte[] Blob, byte[] Metadata);

	public const string Family = "Dagor";

	public const string Version = "11.3";

	public const string Platform = "PCD3D_SM5";

	public const string Platform12 = "PCD3D_SM6";

	private const int HeaderSize = 64;

	private const int MaxShader = 268435456;

	private const int NoShader = 65535;

	private const int MaxNullShader = 65536;

	private const long MaxExe = 2147483648L;

	private const int VprCount = 16;

	private const int FshCount = 20;

	private const int Classes = 140;

	private const int Sizes = 172;

	private const int Metadata = 180;

	private const int Codes = 188;

	private const int Dictionary = 196;

	private const int ClassSize = 108;

	private const int CodeSize = 72;

	private const int CodePasses = 20;

	private const int PassSize = 20;

	private const int SingleShader = 0;

	private const int CombinedVertexShader = 1;

	private const int ShaderSize = 104;

	public const string CbvRangesFork = "cbv-ranges";

	public int ReaderVersion(EngineInfo engine)
	{
		return 3;
	}

	public EngineInfo? Detect(Game game)
	{
		if (!Directory.Exists(Path.Combine(game.InstallDir, "compiledShaders")))
		{
			return null;
		}
		string item = Dumps(game).FirstOrDefault().Path;
		if (item == null)
		{
			return null;
		}
		Span<byte> span = stackalloc byte[12];
		try
		{
			using FileStream fileStream = Open(item);
			if (fileStream.ReadAtLeast(span, 12, throwOnEndOfStream: false) < 12 || !((ReadOnlySpan<byte>)span).StartsWith("VSPSdump"u8))
			{
				return null;
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
		string text = Encoding.ASCII.GetString(span.Slice(8));
		string text2 = GaijinSource.Exe(game.InstallDir);
		string version = text;
		string fork = (CbvRanges(game) ? "cbv-ranges" : null);
		string graphicsApi = Api(game);
		string? unsupported;
		if (text != "11.3")
		{
			unsupported = "shader dump version " + text + ": SCSKiller reads version 11.3";
		}
		else
		{
			unsupported = ((text2 != null && !string.Equals(text2, Path.GetFullPath(game.ExePath), StringComparison.OrdinalIgnoreCase)) ? ("listed with " + Path.GetFileName(game.ExePath) + ", but the game runs " + Path.GetRelativePath(game.InstallDir, text2)) : null);
		}
		return new EngineInfo("Dagor", version, fork, graphicsApi, Encrypted: false, unsupported);
	}

	public string DetectStamp(Game game, EngineInfo? engine)
	{
		FileInfo fileInfo = new FileInfo(Path.Combine(game.InstallDir, "config.blk"));
		return (fileInfo.Exists ? $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}" : "-") + (CbvRanges(game) ? "|cbv" : "");
	}

	internal static bool CbvRanges(Game game)
	{
		Span<byte> span = stackalloc byte[32];
		try
		{
			using FileStream fileStream = Open(Path.Combine(game.InstallDir, "cache", "dx12.cache"));
			if (fileStream.ReadAtLeast(span, 32, throwOnEndOfStream: false) == 32 && ((ReadOnlySpan<byte>)span).StartsWith("CX12"u8) && BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4)) == 34)
			{
				return (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(28)) & 1) != 0;
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
		return true;
	}

	public string IndexStamp(Game game)
	{
		if (Directory.Exists(Path.Combine(game.InstallDir, "compiledShaders")))
		{
			return string.Join(';', from d in Dumps(game)
				select new FileInfo(d.Path) into f
				select $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}") + (CbvRanges(game) ? "|cbv" : "");
		}
		return "";
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		Dictionary<string, ShaderInfo> shaders = new Dictionary<string, ShaderInfo>();
		Dictionary<string, ShaderMap> maps = new Dictionary<string, ShaderMap>();
		SortedSet<string> platforms = new SortedSet<string>(StringComparer.Ordinal);
		StringBuilder stamp = new StringBuilder();
		Dictionary<string, string> lanes = new Dictionary<string, string>();
		int bad = 0;
		foreach (var item4 in Dumps(game))
		{
			string item = item4.Path;
			bool item2 = item4.Dx12;
			string relativePath = Path.GetRelativePath(game.InstallDir, item);
			FileInfo fileInfo = new FileInfo(item);
			StringBuilder stringBuilder = stamp;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(3, 3, stringBuilder);
			handler.AppendFormatted(relativePath);
			handler.AppendLiteral("|");
			handler.AppendFormatted(fileInfo.Length);
			handler.AppendLiteral("|");
			handler.AppendFormatted(fileInfo.LastWriteTimeUtc.Ticks);
			handler.AppendLiteral("\n");
			stringBuilder.Append(ref handler);
			byte[] array = Body(Read(item));
			if (!item2)
			{
				int num = 0;
				foreach (Entry item5 in Entries(array, ct))
				{
					num++;
					List<string> list = (from c in Dxbc.Containers(item5.Blob)
						select Add(c.Container, null)).OfType<string>().ToList();
					if (list != null && list.Count > 0)
					{
						Map(relativePath, "PCD3D_SM5", list, pipeline: false);
					}
				}
				log?.Report($"{relativePath}: {num} entries");
				continue;
			}
			int skipped = 0;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			string nullPs = null;
			bool nullRead = false;
			List<List<string>> list2 = Entries(array, ct).Select((Entry e) =>
			{
				List<(byte[], byte[])> list5 = Stages(e);
				if (list5 == null)
				{
					skipped++;
					return (List<string>)null;
				}
				List<string> list6 = list5.Select(((byte[] Header, byte[] Container) s) => Add(s.Container, s.Header)).ToList();
				return (!list6.Contains(null)) ? list6.OfType<string>().ToList() : null;
			}).ToList();
			int num5 = (int)U(array, 16);
			foreach (var (num6, num7) in Passes(array, num5, list2.Count - num5))
			{
				List<string>[] source;
				if (num7 == 65535)
				{
					num2++;
					string text = NullPs();
					if (text == null)
					{
						continue;
					}
					List<string>[] array2 = new List<string>[2]
					{
						list2[num6],
						null
					};
					int num8 = 1;
					List<string> list3 = new List<string>(num8);
					CollectionsMarshal.SetCount(list3, num8);
					CollectionsMarshal.AsSpan(list3)[0] = text;
					array2[1] = list3;
					source = array2;
				}
				else
				{
					source = ((num6 != 65535) ? new List<string>[2]
					{
						list2[num6],
						list2[num5 + num7]
					} : new List<string>[1] { list2[num5 + num7] });
				}
				if (source.Any((List<string> p) => p == null))
				{
					num3++;
					continue;
				}
				List<string> list4 = source.SelectMany((List<string> p) => p).ToList();
				Map(relativePath, list4.Select(((IReadOnlyDictionary<string, string>)lanes).GetValueOrDefault<string, string>).FirstOrDefault((string x) => x != null) ?? "PCD3D_SM6", list4, pipeline: true);
				num4++;
			}
			log?.Report($"{relativePath}: {list2.Count} entries ({skipped} of a kind not read: stream output, mesh, library), {num4} pipelines in its passes ({num2} drawn with the driver's null pixel shader{((num2 > 0 && nullPs == null) ? ", left out: the exe has no single one" : "")}; {num3} with an unparseable shader left out)");
			string? NullPs()
			{
				if (nullRead)
				{
					return nullPs;
				}
				nullRead = true;
				(FileInfo, byte[])? tuple2 = NullPixelShader(game, ct);
				if (tuple2.HasValue)
				{
					(FileInfo, byte[]) valueOrDefault = tuple2.GetValueOrDefault();
					var (fileInfo2, _) = valueOrDefault;
					if (fileInfo2 != null)
					{
						byte[] item3 = valueOrDefault.Item2;
						if (item3 != null)
						{
							StringBuilder stringBuilder2 = stamp;
							StringBuilder.AppendInterpolatedStringHandler handler2 = new StringBuilder.AppendInterpolatedStringHandler(3, 3, stringBuilder2);
							handler2.AppendFormatted(Path.GetRelativePath(game.InstallDir, fileInfo2.FullName));
							handler2.AppendLiteral("|");
							handler2.AppendFormatted(fileInfo2.Length);
							handler2.AppendLiteral("|");
							handler2.AppendFormatted(fileInfo2.LastWriteTimeUtc.Ticks);
							handler2.AppendLiteral("\n");
							stringBuilder2.Append(ref handler2);
							return nullPs = Add(item3, null);
						}
					}
				}
				return null;
			}
		}
		log?.Report($"{shaders.Count} shaders (" + string.Join(", ", from s in shaders.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select $"{g.Count()} {g.Key}") + $"){((bad > 0) ? $", {bad} unparseable" : "")} -> {maps.Count} maps ({stopwatch.Elapsed.TotalSeconds:F1}s)");
		stamp.Append(engine.Fork);
		return new ShaderIndex(CarvedReader.Sha1Hex(stamp.ToString()), new _003C_003Ez__ReadOnlyArray<string>(platforms.ToArray()), shaders, maps.Values.ToList());
		string? Add(byte[] c, byte[]? header)
		{
			string text2 = Convert.ToHexStringLower(SHA1.HashData(c));
			(uint, uint)? tuple2 = Dxbc.WaveLanes(c);
			if (tuple2.HasValue)
			{
				(uint, uint) valueOrDefault = tuple2.GetValueOrDefault();
				if (valueOrDefault.Item1 > 32 || valueOrDefault.Item2 < 32)
				{
					lanes[text2] = $"{"PCD3D_SM6"} wave{valueOrDefault.Item1}{((valueOrDefault.Item2 != valueOrDefault.Item1) ? $"-{valueOrDefault.Item2}" : "")}";
				}
			}
			try
			{
				if (!shaders.ContainsKey(text2))
				{
					ShaderInfo shaderInfo = ShaderContainer.Parse(c, text2, new ResourceCounts(0, 0, 0, 0));
					if ((object)shaderInfo != null)
					{
						shaders[text2] = shaderInfo with
						{
							Counts = CarvedReader.Counts(shaderInfo.Bindings),
							EngineHeader = header
						};
					}
				}
			}
			catch (Exception ex) when ((ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
			{
				bad++;
			}
			if (!shaders.ContainsKey(text2))
			{
				return null;
			}
			return text2;
		}
		void Map(string rel, string platform, List<string> shas, bool pipeline)
		{
			string text2 = CarvedReader.Sha1Hex(platform + "|" + string.Join(',', shas));
			if (maps.TryAdd(text2, new ShaderMap(text2, rel, platform, shas, pipeline)))
			{
				platforms.Add(platform);
			}
		}
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		HashSet<string> hashSet = new HashSet<string>(sha1s);
		foreach (var (path, flag) in Dumps(game))
		{
			foreach (Entry item2 in Entries(Body(Read(path)), ct))
			{
				foreach (byte[] item3 in (!flag) ? (from c in Dxbc.Containers(item2.Blob)
					select c.Container) : (Stages(item2)?.Select(((byte[] Header, byte[] Container) s) => s.Container) ?? Array.Empty<byte[]>()))
				{
					string text = Convert.ToHexStringLower(SHA1.HashData(item3));
					if (hashSet.Remove(text))
					{
						sink(text, item3);
						if (hashSet.Count == 0)
						{
							return;
						}
					}
				}
			}
		}
		if (!Dumps(game).Any(((string Path, bool Dx12) d) => d.Dx12))
		{
			return;
		}
		(FileInfo, byte[])? tuple2 = NullPixelShader(game, ct);
		if (!tuple2.HasValue)
		{
			return;
		}
		byte[] item = tuple2.GetValueOrDefault().Item2;
		if (item != null)
		{
			string text2 = Convert.ToHexStringLower(SHA1.HashData(item));
			if (hashSet.Contains(text2))
			{
				sink(text2, item);
			}
		}
	}

	internal static (FileInfo Exe, byte[] Container)? NullPixelShader(Game game, CancellationToken ct)
	{
		FileInfo fileInfo = new FileInfo(GaijinSource.Exe(game.InstallDir) ?? game.ExePath);
		if (!fileInfo.Exists || fileInfo.Length > 2147483648u)
		{
			return null;
		}
		byte[] found = null;
		int n = 0;
		CarvedReader.Scan(fileInfo, new byte[8388608], new (long, long)[1] { (0L, fileInfo.Length) }, (long _, ReadOnlySpan<byte> c) =>
		{
			if (Dxbc.Part(c, "DXIL"u8).IsEmpty)
			{
				return;
			}
			try
			{
				ShaderInfo shaderInfo = ShaderContainer.Parse(c, "", new ResourceCounts(0, 0, 0, 0));
				if ((object)shaderInfo != null && shaderInfo.Stage == Stage.Pixel)
				{
					IReadOnlyList<SigElement> inputs = shaderInfo.Inputs;
					if (inputs != null && inputs.Count == 0)
					{
						IReadOnlyList<SigElement> outputs = shaderInfo.Outputs;
						if (outputs != null && outputs.Count == 0)
						{
							IReadOnlyList<Binding> bindings = shaderInfo.Bindings;
							if (bindings != null && bindings.Count == 0 && n++ == 0)
							{
								found = c.ToArray();
							}
						}
					}
				}
			}
			catch (Exception ex) when ((ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
			{
			}
		}, ct, 65536);
		if (n != 1)
		{
			return null;
		}
		return (fileInfo, found);
	}

	private static IEnumerable<(string Path, bool Dx12)> Dumps(Game game)
	{
		bool flag;
		switch (Video(game, "compatibilityMode"))
		{
		case "yes":
		case "true":
		case "1":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		bool flag2 = flag;
		string dir = Path.Combine(game.InstallDir, "compiledShaders");
		(string, bool)[] array = new (string, bool)[2]
		{
			(flag2 ? "game.compatibility" : "game", false),
			(flag2 ? "game.compatibilityDX12" : "gameDX12", true)
		};
		for (int i = 0; i < array.Length; i++)
		{
			(string, bool) tuple = array[i];
			string item = tuple.Item1;
			bool item2 = tuple.Item2;
			string text = Path.Combine(dir, item + ".ps50.shdump.bin");
			if (File.Exists(text))
			{
				yield return (Path: text, Dx12: item2);
			}
		}
	}

	internal static byte[] Body(byte[] dump)
	{
		if (dump.Length < 76 || !((ReadOnlySpan<byte>)dump.AsSpan()).StartsWith("VSPSdump11.3"u8))
		{
			throw new InvalidDataException("not a version 11.3 shader dump");
		}
		(int At, int Count) tuple = List(dump, 68, 1);
		int item = tuple.At;
		int item2 = tuple.Count;
		byte[] array = new byte[U(dump, 64)];
		using Decompressor z = new Decompressor();
		if (Unwrap(z, dump.AsSpan(item, item2), array) != array.Length)
		{
			throw new InvalidDataException("the dump's body doesn't decode to its size");
		}
		return array;
	}

	internal static IEnumerable<Entry> Entries(byte[] main, CancellationToken ct)
	{
		long count = (long)U(main, 16) + (long)U(main, 20);
		var (sizes, num) = List(main, 172, 4);
		var (metas, num2) = List(main, 180, 8);
		var (codes, num3) = List(main, 188, 8);
		var (start, length) = List(main, 196, 1);
		if (num != count || num3 != count || num2 != count)
		{
			throw new InvalidDataException($"{count} shaders, but {num} sizes, {num2} metadata and {num3} codes");
		}
		using Decompressor zd = new Decompressor();
		zd.LoadDictionary(main.AsSpan(start, length));
		for (int i = 0; i < count; i++)
		{
			ct.ThrowIfCancellationRequested();
			(int At, int Count) tuple5 = List(main, codes + 8 * i, 1);
			int item = tuple5.At;
			int item2 = tuple5.Count;
			(int At, int Count) tuple6 = List(main, metas + 8 * i, 1);
			int item3 = tuple6.At;
			int item4 = tuple6.Count;
			uint num4 = U(main, sizes + 4 * i);
			if (num4 > 268435456)
			{
				throw new InvalidDataException($"shader {i}: {num4} bytes");
			}
			byte[] array = new byte[(num4 + 3) & 0xFFFFFFFCu];
			int num5 = Unwrap(zd, main.AsSpan(item, item2), array);
			yield return new Entry(array[..num5], main[item3..(item3 + item4)]);
		}
	}

	internal static List<(byte[] Header, byte[] Container)>? Stages(Entry e)
	{
		byte[] m = e.Metadata;
		if (m.Length < 32)
		{
			throw new InvalidDataException("shader metadata too short");
		}
		ushort num = BinaryPrimitives.ReadUInt16LittleEndian(m);
		ushort num2 = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(2));
		ushort num3 = num;
		bool flag = (num2 & 1) != 0;
		if (!flag)
		{
			bool flag2 = (uint)num3 <= 1u;
			flag = !flag2;
		}
		if (flag)
		{
			return null;
		}
		var (data, n) = List(m, 24, 1);
		return ((num3 != 0) ? (from k in new int[4] { 0, 4, 8, 12 }
			select Pointer(m, data + k, data + n) into p
			where p != 0
			select p).ToArray() : new int[1] { data }).Select((int p) =>
		{
			if (p < data || p + 104 > data + n)
			{
				throw new InvalidDataException("shader header outside its metadata");
			}
			uint num4 = U(m, p + 96);
			uint num5 = U(m, p + 96 + 4);
			uint num6 = num4;
			if ((long)num6 + (long)num5 > e.Blob.Length || !Dxbc.Valid(e.Blob.AsSpan((int)num6, (int)num5)))
			{
				throw new InvalidDataException("shader bytecode outside its entry");
			}
			return (m[p..(p + 96)], e.Blob[(int)num6..(int)(num6 + num5)]);
		}).ToList();
	}

	private static int Pointer(byte[] b, int field, int end)
	{
		if (field + 4 > end)
		{
			throw new InvalidDataException("pointer outside its layout");
		}
		int num = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(field));
		if (num != 0)
		{
			long num2 = (long)field + (long)num;
			if (num2 < 0 || num2 >= end)
			{
				throw new InvalidDataException("pointer outside its layout");
			}
			return (int)num2;
		}
		return 0;
	}

	internal static HashSet<(int Vpr, int Fsh)> Passes(byte[] main, int vprCount, int fshCount)
	{
		HashSet<(int, int)> hashSet = new HashSet<(int, int)>();
		(int At, int Count) tuple = List(main, 140, 108);
		int item = tuple.At;
		int item2 = tuple.Count;
		for (int i = 0; i < item2; i++)
		{
			(int At, int Count) tuple2 = List(main, item + i * 108, 72);
			int item3 = tuple2.At;
			int item4 = tuple2.Count;
			for (int j = 0; j < item4; j++)
			{
				(int At, int Count) tuple3 = List(main, item3 + j * 72 + 20, 20);
				int item5 = tuple3.At;
				int item6 = tuple3.Count;
				for (int k = 0; k < item6; k++)
				{
					int num = Pointer(main, item5 + k * 20, main.Length);
					if (num != 0)
					{
						if (num + 4 > main.Length)
						{
							throw new InvalidDataException("pass outside the dump");
						}
						int num2 = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(num));
						int num3 = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(num + 2));
						if ((num2 != 65535 && num2 >= vprCount) || (num3 != 65535 && num3 >= fshCount) || (num2 == 65535 && num3 == 65535))
						{
							throw new InvalidDataException($"pass names shader {num2}/{num3}, the dump has {vprCount}/{fshCount}");
						}
						hashSet.Add((num2, num3));
					}
				}
			}
		}
		return hashSet;
	}

	private static int Unwrap(Decompressor z, ReadOnlySpan<byte> src, Span<byte> dest)
	{
		try
		{
			return z.Unwrap(src, dest);
		}
		catch (ZstdException ex)
		{
			throw new InvalidDataException("the dump doesn't decode: " + ex.Message, ex);
		}
	}

	private static (int At, int Count) List(byte[] b, int field, int elementSize)
	{
		if (field < 0 || field + 8 > b.Length)
		{
			throw new InvalidDataException($"list field at {field} is outside the dump");
		}
		long num = (long)field + (long)BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(field));
		long num2 = U(b, field + 4);
		if (num2 > 0 && (num < 0 || num + num2 * elementSize > b.Length))
		{
			throw new InvalidDataException($"list at {field} is outside the dump");
		}
		return (At: (int)num, Count: (int)num2);
	}

	private static uint U(byte[] b, int at)
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
	}

	private static string Api(Game game)
	{
		return Video(game, "driver") switch
		{
			"dx11" => "D3D11", 
			"dx12" => "D3D12", 
			"vulkan" => "Vulkan", 
			_ => "D3D11 or D3D12", 
		};
	}

	internal static string? Video(Game game, string name)
	{
		string text;
		try
		{
			text = File.ReadAllText(Path.Combine(game.InstallDir, "config.blk"));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
		string text2 = VideoBlock(text);
		if (text2 == null)
		{
			return null;
		}
		Match match = Regex.Match(text2, "(?:^|[\\s;])" + Regex.Escape(name) + ":[a-z0-9]+\\s*=\\s*\"?([^\"\\s;}]*)", RegexOptions.IgnoreCase);
		if (!match.Success)
		{
			return null;
		}
		return match.Groups[1].Value.ToLowerInvariant();
	}

	private static string? VideoBlock(string text)
	{
		Match match = Regex.Match(text, "(?:^|[\\s;])video\\s*\\{");
		if (!match.Success)
		{
			return null;
		}
		StringBuilder stringBuilder = new StringBuilder();
		int num = 1;
		for (int i = match.Index + match.Length; i < text.Length; i++)
		{
			if (num <= 0)
			{
				break;
			}
			char c = text[i];
			switch (c)
			{
			case '{':
				num++;
				break;
			case '}':
				num--;
				break;
			default:
				if (num == 1)
				{
					stringBuilder.Append(c);
				}
				break;
			}
			if ((c == '{' || c == '}') ? true : false)
			{
				stringBuilder.Append(' ');
			}
		}
		return stringBuilder.ToString();
	}

	private static byte[] Read(string path)
	{
		using FileStream fileStream = Open(path);
		byte[] array = new byte[fileStream.Length];
		fileStream.ReadExactly(array);
		return array;
	}

	private static FileStream Open(string path)
	{
		return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
	}
}
