using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;
using ValvePak;
using ZstdSharp;

namespace SCSKiller.Core.Source2;

public sealed class Source2Reader : IEngineReader
{
	internal sealed class Dictionaries(string binDir)
	{
		public const string Dll = "materialsystem2.dll";

		private const int Size = 65536;

		private readonly Dictionary<uint, Decompressor?> byId = new Dictionary<uint, Decompressor>();

		private byte[]? dll;

		public HashSet<uint> Missing { get; } = new HashSet<uint>();

		public Decompressor? For(ReadOnlySpan<byte> frame)
		{
			uint num = DictionaryId(frame);
			if (byId.TryGetValue(num, out Decompressor value))
			{
				return value;
			}
			if (dll == null)
			{
				dll = File.ReadAllBytes(Path.Combine(binDir, "materialsystem2.dll"));
			}
			Span<byte> span = stackalloc byte[8];
			BinaryPrimitives.WriteUInt32LittleEndian(span, 3962610743u);
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), num);
			int num2 = ((ReadOnlySpan<byte>)dll.AsSpan()).IndexOf((ReadOnlySpan<byte>)span);
			if (num2 < 0 || num2 + 65536 > dll.Length)
			{
				Missing.Add(num);
				return byId[num] = null;
			}
			value = new Decompressor();
			value.LoadDictionary(dll.AsSpan(num2, 65536));
			return byId[num] = value;
		}

		internal static uint DictionaryId(ReadOnlySpan<byte> f)
		{
			if (f.Length < 5 || BinaryPrimitives.ReadUInt32LittleEndian(f) != 4247762216u)
			{
				throw new InvalidDataException("bytecode block isn't a zstd frame");
			}
			byte b = f[4];
			int num = 5 + (((b & 0x20) == 0) ? 1 : 0);
			int num2 = (b & 3) switch
			{
				0 => 0, 
				1 => 1, 
				2 => 2, 
				_ => 4, 
			};
			if (f.Length < num + num2)
			{
				throw new InvalidDataException("bytecode block's zstd header is cut short");
			}
			return num2 switch
			{
				0 => 0u, 
				1 => f[num], 
				2 => BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(num)), 
				_ => BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(num)), 
			};
		}
	}

	public const string Family = "Source 2";

	public const string Platform = "PCD3D_SM5";

	private const string Vpk = "shaders_pc_dir.vpk";

	private const int MinVersion = 70;

	private const int MaxVersion = 72;

	private const int ResourceHeaderVersion = 12;

	private const int MaxBlock = 268435456;

	private static string Bin(Game game)
	{
		return Path.Combine(game.InstallDir, "game", "bin", "win64");
	}

	public EngineInfo? Detect(Game game)
	{
		if (!File.Exists(Path.Combine(Bin(game), "engine2.dll")))
		{
			return null;
		}
		string graphicsApi = Api(game, UnrealRhi.LaunchOptions(game));
		string text = Vpks(game).FirstOrDefault();
		if (text == null)
		{
			return new EngineInfo("Source 2", "", null, graphicsApi, Encrypted: false, "no shaders_pc_dir.vpk");
		}
		Package pak = Open(text);
		try
		{
			int? num = (from e in Programs(pak)
				select Version(Read(pak, e))).FirstOrDefault((int? v) => v.HasValue);
			string version = num?.ToString() ?? "";
			object unsupported;
			switch (num)
			{
			default:
				unsupported = $"shader files {((!num.HasValue) ? "of an older layout" : $"version {num}")}: SCSKiller reads versions {70} to {72}";
				break;
			case 70:
			case 71:
			case 72:
				unsupported = null;
				break;
			}
			return new EngineInfo("Source 2", version, null, graphicsApi, Encrypted: false, (string?)unsupported);
		}
		finally
		{
			if (pak != null)
			{
				((IDisposable)pak).Dispose();
			}
		}
	}

	public string DetectStamp(Game game, EngineInfo? engine)
	{
		return UnrealRhi.LaunchOptions(game) + "|" + string.Join(';', from f in BootConfigs(game)
			select new FileInfo(f) into f
			select $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}");
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		Dictionary<string, ShaderInfo> dictionary = new Dictionary<string, ShaderInfo>();
		Dictionary<string, List<string>> dictionary2 = new Dictionary<string, List<string>>();
		StringBuilder stringBuilder = new StringBuilder();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		int unreadable = 0;
		Dictionaries dictionaries = new Dictionaries(Bin(game));
		foreach (string item2 in Vpks(game))
		{
			FileInfo fileInfo = new FileInfo(item2);
			string relativePath = Path.GetRelativePath(game.InstallDir, item2);
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(3, 3, stringBuilder2);
			handler.AppendFormatted(relativePath);
			handler.AppendLiteral("|");
			handler.AppendFormatted(fileInfo.Length);
			handler.AppendLiteral("|");
			handler.AppendFormatted(fileInfo.LastWriteTimeUtc.Ticks);
			handler.AppendLiteral("\n");
			stringBuilder2.Append(ref handler);
			using Package pak = Open(item2);
			foreach (PackageEntry e in Programs(pak))
			{
				ct.ThrowIfCancellationRequested();
				byte[] array = Read(pak, e);
				switch (Version(array))
				{
				default:
					num3++;
					break;
				case 70:
				case 71:
				case 72:
				{
					num++;
					List<string> list3;
					if (!dictionary2.TryGetValue(relativePath + ":" + Program(e), out var value))
					{
						List<string> list = (dictionary2[relativePath + ":" + Program(e)] = new List<string>());
						list3 = list;
					}
					else
					{
						list3 = value;
					}
					List<string> list4 = list3;
					foreach (byte[] item3 in Readable(array, dictionaries, (Exception x) =>
					{
						int num5 = unreadable;
						unreadable = num5 + 1;
						if (num5 == 0)
						{
							log?.Report("warning: " + e.GetFullPath() + ": " + x.Message);
						}
					}))
					{
						num2++;
						foreach (var item4 in Dxbc.Containers(item3))
						{
							byte[] item = item4.Container;
							string text = Convert.ToHexStringLower(SHA1.HashData(item));
							try
							{
								if (!dictionary.ContainsKey(text))
								{
									ShaderInfo shaderInfo = ShaderContainer.Parse(item, text, new ResourceCounts(0, 0, 0, 0));
									if ((object)shaderInfo != null)
									{
										dictionary[text] = shaderInfo with
										{
											Counts = CarvedReader.Counts(shaderInfo.Bindings)
										};
									}
								}
							}
							catch (Exception ex) when ((ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
							{
								num4++;
							}
							if (dictionary.ContainsKey(text))
							{
								list4.Add(text);
							}
						}
					}
					break;
				}
				}
			}
		}
		if (dictionaries.Missing.Count > 0)
		{
			throw new InvalidDataException("no zstd dictionary " + string.Join(", ", dictionaries.Missing.Select((uint d) => d.ToString("x8"))) + " in game\\bin\\win64\\materialsystem2.dll");
		}
		List<ShaderMap> list5 = (from m in dictionary2
			where m.Value.Count > 0
			select new ShaderMap(CarvedReader.Sha1Hex(m.Key), m.Key, "PCD3D_SM5", m.Value.Distinct().ToList())).ToList();
		log?.Report($"{num} programs, {num2} static combos -> {dictionary.Count} shaders (" + string.Join(", ", from s in dictionary.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select $"{g.Count()} {g.Key}") + $"), {list5.Count} maps{((num3 > 0) ? $"; {num3} files of another version left out" : "")}{((unreadable > 0) ? $"; the rest of {unreadable} files that don't read whole left out" : "")}" + $"{((num4 > 0) ? $", {num4} unparseable" : "")} ({stopwatch.Elapsed.TotalSeconds:F1}s)");
		if (dictionary.Count == 0)
		{
			throw new InvalidDataException("no DirectX 11 shader in shaders_pc_dir.vpk");
		}
		return new ShaderIndex(CarvedReader.Sha1Hex(stringBuilder.ToString()), new _003C_003Ez__ReadOnlySingleElementList<string>("PCD3D_SM5"), dictionary, list5);
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		HashSet<string> hashSet = new HashSet<string>(sha1s);
		Dictionaries dicts = new Dictionaries(Bin(game));
		foreach (string item2 in Vpks(game))
		{
			using Package pak = Open(item2);
			foreach (PackageEntry item3 in Programs(pak))
			{
				ct.ThrowIfCancellationRequested();
				byte[] array = Read(pak, item3);
				int? num = Version(array);
				if (!num.HasValue)
				{
					continue;
				}
				int valueOrDefault = num.GetValueOrDefault();
				if (valueOrDefault < 70 || valueOrDefault > 72)
				{
					continue;
				}
				foreach (byte[] item4 in Readable(array, dicts, (Exception _) =>
				{
				}))
				{
					foreach (var item5 in Dxbc.Containers(item4))
					{
						byte[] item = item5.Container;
						string text = Convert.ToHexStringLower(SHA1.HashData(item));
						if (hashSet.Remove(text))
						{
							sink(text, item);
							if (hashSet.Count == 0)
							{
								return;
							}
						}
					}
				}
			}
		}
	}

	private static IEnumerable<string> Vpks(Game game)
	{
		string path = Path.Combine(game.InstallDir, "game");
		if (Directory.Exists(path))
		{
			return (from p in (from d in Directory.EnumerateDirectories(path)
					select Path.Combine(d, "shaders_pc_dir.vpk")).Where(File.Exists)
				orderby Path.GetFileName(Path.GetDirectoryName(p)).Equals("core", StringComparison.OrdinalIgnoreCase)
				select p).ThenBy((string p) => p, StringComparer.OrdinalIgnoreCase);
		}
		return Array.Empty<string>();
	}

	private static Package Open(string path)
	{
		Package package = new Package();
		try
		{
			package.Read(path);
			return package;
		}
		catch
		{
			package.Dispose();
			throw;
		}
	}

	private static IEnumerable<PackageEntry> Programs(Package pak)
	{
		return (pak.Entries?.GetValueOrDefault("vcs") ?? new List<PackageEntry>()).Where((PackageEntry e) => !e.FileName.EndsWith("_features", StringComparison.OrdinalIgnoreCase));
	}

	private static string Program(PackageEntry e)
	{
		return e.DirectoryName + "/" + Regex.Replace(e.FileName, "_pc_[0-9]+_[a-z]+$", "", RegexOptions.IgnoreCase);
	}

	private static byte[] Read(Package pak, PackageEntry e)
	{
		pak.ReadEntry(e, out byte[] output);
		return output;
	}

	internal static int? Version(ReadOnlySpan<byte> b)
	{
		if (b.Length >= 8)
		{
			uint num = BinaryPrimitives.ReadUInt32LittleEndian(b);
			if (num <= b.Length && BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(4)) == 12)
			{
				return BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(6));
			}
		}
		return null;
	}

	private static IEnumerable<byte[]> Readable(byte[] b, Dictionaries dicts, Action<Exception> error)
	{
		using IEnumerator<byte[]> blocks = Blocks(b, dicts).GetEnumerator();
		while (true)
		{
			bool flag;
			try
			{
				flag = blocks.MoveNext();
			}
			catch (Exception ex) when ((ex is InvalidDataException || ex is ZstdException) ? true : false)
			{
				error(ex);
				flag = false;
			}
			if (flag)
			{
				yield return blocks.Current;
				continue;
			}
			break;
		}
	}

	internal static IEnumerable<byte[]> Blocks(byte[] b, Dictionaries dicts)
	{
		long at = BinaryPrimitives.ReadUInt32LittleEndian(b);
		while (at < b.Length)
		{
			if (at + 12 > b.Length)
			{
				throw new InvalidDataException("bytecode block header past the end of the file");
			}
			int num = -BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan((int)at));
			int num2 = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan((int)at + 4));
			int num3 = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan((int)at + 8));
			bool flag = ((num2 < 0 || num2 > 268435456) ? true : false);
			if (flag || num3 < 0 || at + 12 + num3 > b.Length)
			{
				throw new InvalidDataException($"bytecode block at {at}: {num3} bytes");
			}
			Span<byte> span = b.AsSpan((int)at + 12, num3);
			at += 12 + num3;
			if ((num != 3 && num != 5) || 1 == 0)
			{
				throw new InvalidDataException($"bytecode block of kind {num}");
			}
			Decompressor decompressor = dicts.For(span);
			if (decompressor != null)
			{
				byte[] array = new byte[num2];
				if (decompressor.Unwrap(span, array) != num2)
				{
					throw new InvalidDataException("bytecode block doesn't decode to its size");
				}
				yield return array;
			}
		}
	}

	internal static string Api(Game game, string launch)
	{
		Match match = Regex.Match(launch, "(?:^|\\s)-(dx11|vulkan)\\b", RegexOptions.IgnoreCase);
		if (match != null && match.Success)
		{
			return Of(match.Groups[1].Value) + " (launch option)";
		}
		foreach (string item in BootConfigs(game))
		{
			string input;
			try
			{
				input = File.ReadAllText(item);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				continue;
			}
			Match match2 = Regex.Match(input, "\"DefaultRenderSystemOption\"\\s+\"\\s*-(dx11|vulkan)\\b", RegexOptions.IgnoreCase);
			if (match2 != null && match2.Success)
			{
				return Of(match2.Groups[1].Value) + " (boot.vcfg)";
			}
		}
		return "D3D11";
		static string Of(string flag)
		{
			if (!flag.Equals("vulkan", StringComparison.OrdinalIgnoreCase))
			{
				return "D3D11";
			}
			return "Vulkan";
		}
	}

	private static IEnumerable<string> BootConfigs(Game game)
	{
		string path = Path.Combine(game.InstallDir, "game");
		if (Directory.Exists(path))
		{
			return (from p in (from d in Directory.EnumerateDirectories(path)
					select Path.Combine(d, "cfg", "boot.vcfg")).Where(File.Exists)
				orderby Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(p))).Equals("core", StringComparison.OrdinalIgnoreCase)
				select p).ThenBy((string p) => p, StringComparer.OrdinalIgnoreCase);
		}
		return Array.Empty<string>();
	}
}
