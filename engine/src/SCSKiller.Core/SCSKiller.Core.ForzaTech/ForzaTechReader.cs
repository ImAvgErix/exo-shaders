using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Northlight;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.ForzaTech;

public sealed class ForzaTechReader : IEngineReader
{
	public sealed record Loc(string Zip, string Entry);

	private sealed class Counts
	{
		public int Broken;

		public int Huge;

		public int Unreadable;

		public int NotOne;

		public int Capped;

		public string? FirstBroken;

		public string Warnings => ((Broken > 0) ? $"; warning: {Broken} zips don't open ({FirstBroken}), left out" : "") + ((Huge > 0) ? $"; warning: {Huge} zips declare too many entries, left out" : "") + ((Unreadable > 0) ? $"; {Unreadable} shader entries don't read (encrypted or damaged), left out" : "") + ((NotOne > 0) ? $"; {NotOne} shader entries aren't one container, left out" : "") + ((Capped > 0) ? "; warning: too many shader entries, the rest left out" : "");
	}

	public const string Family = "ForzaTech";

	public const string Format = "pcdxil";

	private const string Media = "media";

	private const string EngineZip = "media\\ShadersDXIL.zip";

	private const string Library = "media\\_library";

	private const int DetectSample = 256;

	private static readonly Regex ShaderName = new Regex("\\.(pcdxil\\.[a-z]so|[a-z]{1,2}shader)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	internal int MaxZipEntries { get; init; } = 1048576;

	internal int MaxShaderEntries { get; init; } = 2097152;

	public EngineInfo? Detect(Game game)
	{
		string path = Path.Combine(game.InstallDir, "media\\ShadersDXIL.zip");
		if (!File.Exists(path) || !Directory.Exists(Path.Combine(game.InstallDir, "media\\_library")))
		{
			return null;
		}
		int num = 0;
		int num2 = 0;
		Counts counts = new Counts();
		using (ZipArchive zipArchive = Open(path, counts, MaxZipEntries))
		{
			IEnumerable<ZipArchiveEntry> enumerable;
			if (zipArchive != null)
			{
				enumerable = ShaderEntries(zipArchive).Take(256);
			}
			else
			{
				IEnumerable<ZipArchiveEntry> enumerable2 = Enumerable.Empty<ZipArchiveEntry>();
				enumerable = enumerable2;
			}
			foreach (ZipArchiveEntry item in enumerable)
			{
				byte[] array = Read(item, counts);
				if (array == null)
				{
					continue;
				}
				int num3 = Dxbc.Kind(array);
				if (num3 >= 0 && num3 != 6)
				{
					num++;
					if (!Dxbc.Part(array, "RTS0"u8).IsEmpty)
					{
						num2++;
					}
				}
			}
		}
		return new EngineInfo("ForzaTech", "pcdxil" + ((num > 0 && (double)num2 >= 0.9 * (double)num) ? "+RTS0" : ""), null, "D3D12", Encrypted: false, (num == 0) ? "no shader in media\\ShadersDXIL.zip" : null);
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		Dictionary<string, ShaderInfo> dictionary = new Dictionary<string, ShaderInfo>();
		Dictionary<string, Loc> dictionary2 = new Dictionary<string, Loc>();
		RootSigCarriers rootSigCarriers = new RootSigCarriers();
		Dictionary<string, string> platformOf = new Dictionary<string, string>();
		List<ShaderMap> list = new List<ShaderMap>();
		HashSet<string> hashSet = new HashSet<string>();
		Counts counts = new Counts();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		StringBuilder stringBuilder = new StringBuilder();
		foreach (string item in Zips(game))
		{
			ct.ThrowIfCancellationRequested();
			string relativePath = Path.GetRelativePath(game.InstallDir, item);
			using ZipArchive zipArchive = Open(item, counts, MaxZipEntries);
			if (zipArchive == null)
			{
				stringBuilder.Append(Stamp(relativePath, item));
				continue;
			}
			List<ZipArchiveEntry> list2 = ShaderEntries(zipArchive).ToList();
			if (list2.Count == 0)
			{
				continue;
			}
			num++;
			stringBuilder.Append(Stamp(relativePath, item));
			Dictionary<string, (string, string)> dictionary3 = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
			List<string> list3 = new List<string>();
			string key;
			foreach (ZipArchiveEntry item2 in list2)
			{
				ct.ThrowIfCancellationRequested();
				if (num2 >= MaxShaderEntries)
				{
					counts.Capped++;
					break;
				}
				byte[] array = Read(item2, counts);
				if (array == null)
				{
					continue;
				}
				num2++;
				string text = Convert.ToHexStringLower(SHA1.HashData(array));
				if (dictionary2.TryAdd(text, new Loc(item, item2.FullName)))
				{
					rootSigCarriers.See(array, text, Dxbc.Kind(array));
					try
					{
						ShaderInfo shaderInfo = ShaderContainer.Parse(array, text, new ResourceCounts(0, 0, 0, 0));
						if ((object)shaderInfo != null)
						{
							dictionary[text] = shaderInfo with
							{
								Counts = CarvedReader.Counts(shaderInfo.Bindings)
							};
							string text2 = CarvedReader.LanePlatform(Dxbc.WaveLanes(array));
							if (text2 != null)
							{
								platformOf[text] = text2;
							}
						}
					}
					catch (Exception ex) when ((ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
					{
						num5++;
					}
				}
				if (!dictionary.TryGetValue(text, out var value))
				{
					continue;
				}
				if (value.Stage == Stage.Compute)
				{
					if (hashSet.Add(text))
					{
						list.Add(new ShaderMap(CarvedReader.Sha1Hex(text), relativePath + ":" + item2.FullName, PlatformOf(platformOf, new string[1] { text }), new _003C_003Ez__ReadOnlySingleElementList<string>(text), IsPipeline: true));
					}
					continue;
				}
				Stage stage = value.Stage;
				bool flag = (uint)(stage - 1) <= 1u;
				if (flag && item2.FullName.EndsWith((value.Stage == Stage.Vertex) ? ".pcdxil.vso" : ".pcdxil.pso", StringComparison.OrdinalIgnoreCase))
				{
					key = item2.FullName;
					string key2 = key.Substring(0, key.Length - 4);
					(string, string) valueOrDefault = dictionary3.GetValueOrDefault(key2);
					dictionary3[key2] = ((value.Stage == Stage.Vertex) ? (text, valueOrDefault.Item2) : (valueOrDefault.Item1, text));
				}
				else
				{
					list3.Add(text);
				}
			}
			foreach (KeyValuePair<string, (string, string)> item3 in dictionary3)
			{
				item3.Deconstruct(out key, out var value2);
				(string, string) tuple = value2;
				string text3 = key;
				var (text4, text5) = tuple;
				if (text4 == null || text5 == null)
				{
					list3.Add(text4 ?? text5);
					continue;
				}
				if (NorthlightReader.Feeds(new _003C_003Ez__ReadOnlyArray<ShaderInfo>(new ShaderInfo[2]
				{
					dictionary[text4],
					dictionary[text5]
				})) != true)
				{
					num4++;
					list3.Add(text4);
					list3.Add(text5);
					continue;
				}
				num3++;
				string text6 = CarvedReader.Sha1Hex(text4 + "," + text5);
				if (hashSet.Add(text6))
				{
					list.Add(new ShaderMap(text6, relativePath + ":" + text3, PlatformOf(platformOf, new string[2] { text4, text5 }), new _003C_003Ez__ReadOnlyArray<string>(new string[2] { text4, text5 }), IsPipeline: true));
				}
			}
			foreach (IGrouping<string, string> item4 in from h in list3.Distinct()
				group h by platformOf.GetValueOrDefault(h, "D3D12"))
			{
				list.Add(new ShaderMap(CarvedReader.Sha1Hex((item4.Key == "D3D12") ? relativePath : (relativePath + "|" + item4.Key)), relativePath, item4.Key, item4.ToList()));
			}
			if (counts.Capped <= 0)
			{
				continue;
			}
			break;
		}
		rootSigCarriers.Apply(dictionary);
		int value3 = dictionary.Values.Count((ShaderInfo s) => s.Stage == Stage.Library);
		log?.Report($"{num} zips with shaders, {num2} shader entries -> {dictionary.Count} shaders (" + string.Join(", ", from s in dictionary.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select $"{g.Count()} {g.Key}") + "), " + $"{dictionary.Values.Select((ShaderInfo s) => s.RootSignature).OfType<string>().Distinct().Count()} embedded root signatures, " + $"{dictionary.Values.Count((ShaderInfo s) => s.Stage != Stage.Library && s.RootSignature == null)} shaders without one; {num3} VS+PS pairs by name, {list.Count} maps; " + $"{value3} ray tracing libraries without a root signature (they need a recording), {dictionary.Values.Count((ShaderInfo s) => s.InlineRayTracing)} tracing rays inline" + ((num4 > 0) ? $"; {num4} pairs pooled: their VS doesn't feed their PS" : "") + counts.Warnings + ((num5 > 0) ? $"; {num5} unparseable" : "") + $" ({stopwatch.Elapsed.TotalSeconds:F1}s)");
		if (dictionary.Count == 0)
		{
			throw new InvalidDataException("no shader in the zips of media" + counts.Warnings);
		}
		located[game.Id] = dictionary2;
		string contentHash = CarvedReader.Sha1Hex(stringBuilder.ToString());
		List<string> list4 = new List<string>();
		list4.Add("D3D12");
		list4.AddRange(platformOf.Values.Distinct().Order(StringComparer.Ordinal));
		return new ShaderIndex(contentHash, new _003C_003Ez__ReadOnlyList<string>(list4), dictionary, list, rootSigCarriers.Blobs);
	}

	private static string PlatformOf(Dictionary<string, string> platformOf, string[] shas)
	{
		return shas.Select((string h) => platformOf.GetValueOrDefault(h, "D3D12")).FirstOrDefault((string p) => p != "D3D12", "D3D12");
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		if (!located.TryGetValue(game.Id, out IReadOnlyDictionary<string, Loc> locs))
		{
			Index(game, engine, null, ct);
			locs = located[game.Id];
		}
		Counts counts = new Counts();
		foreach (IGrouping<string, (string, Loc)> item in from s in sha1s.Where(locs.ContainsKey)
			select (Sha: s, At: locs[s]) into x
			group x by x.At.Zip)
		{
			using ZipArchive zipArchive = Open(item.Key, counts, MaxZipEntries);
			if (zipArchive == null)
			{
				continue;
			}
			foreach (var (text, loc) in item)
			{
				ct.ThrowIfCancellationRequested();
				ZipArchiveEntry entry = zipArchive.GetEntry(loc.Entry);
				if (entry != null)
				{
					byte[] array = Read(entry, counts);
					if (array != null && Convert.ToHexStringLower(SHA1.HashData(array)) == text)
					{
						sink(text, array);
					}
				}
			}
		}
	}

	private static IEnumerable<string> Zips(Game game)
	{
		string path = Path.Combine(game.InstallDir, "media");
		try
		{
			return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.zip", new EnumerationOptions
			{
				RecurseSubdirectories = true,
				IgnoreInaccessible = true
			}).Order(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return Array.Empty<string>();
		}
	}

	private static IEnumerable<ZipArchiveEntry> ShaderEntries(ZipArchive a)
	{
		return a.Entries.Where((ZipArchiveEntry e) => ShaderName.IsMatch(e.FullName));
	}

	private static string Stamp(string rel, string path)
	{
		try
		{
			FileInfo fileInfo = new FileInfo(path);
			return $"{rel}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}\n";
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return rel + "|gone\n";
		}
	}

	private static ZipArchive? Open(string path, Counts counts, int maxEntries)
	{
		FileStream fileStream = null;
		try
		{
			fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (DeclaredEntries(fileStream) > maxEntries)
			{
				fileStream.Dispose();
				counts.Huge++;
				return null;
			}
			fileStream.Position = 0L;
			ZipArchive zipArchive = new ZipArchive(fileStream, ZipArchiveMode.Read);
			try
			{
				_ = zipArchive.Entries.Count;
			}
			catch
			{
				zipArchive.Dispose();
				throw;
			}
			return zipArchive;
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is OverflowException) ? true : false)
		{
			fileStream?.Dispose();
			counts.Broken++;
			if (counts.FirstBroken == null)
			{
				counts.FirstBroken = Path.GetFileName(path);
			}
			return null;
		}
	}

	internal static long DeclaredEntries(Stream s)
	{
		byte[] array = new byte[(int)Math.Min(s.Length, 65577L)];
		if (array.Length < 22)
		{
			return 0L;
		}
		s.Position = s.Length - array.Length;
		s.ReadExactly(array);
		int num = Math.Max(0, array.Length - 22 - 65535);
		int num2 = ((ReadOnlySpan<byte>)array.AsSpan(num, array.Length - 22 + 4 - num)).LastIndexOf((ReadOnlySpan<byte>)new byte[4] { 80, 75, 5, 6 });
		if (num2 < 0)
		{
			return 0L;
		}
		num2 += num;
		long num3 = BinaryPrimitives.ReadUInt16LittleEndian(array.AsSpan(num2 + 10));
		if (num2 < 20 || !((ReadOnlySpan<byte>)array.AsSpan(num2 - 20)).StartsWith((ReadOnlySpan<byte>)new byte[4] { 80, 75, 6, 7 }))
		{
			return num3;
		}
		ulong num4 = BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(num2 - 12));
		if (num4 > (ulong)Math.Max(0L, s.Length - 56))
		{
			return num3;
		}
		byte[] array2 = new byte[56];
		s.Position = (long)num4;
		s.ReadExactly(array2);
		if (!((ReadOnlySpan<byte>)array2.AsSpan()).StartsWith((ReadOnlySpan<byte>)new byte[4] { 80, 75, 6, 6 }))
		{
			return num3;
		}
		return Math.Max(num3, (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(array2.AsSpan(32)), 9223372036854775807uL));
	}

	private static byte[]? Read(ZipArchiveEntry e, Counts counts)
	{
		long length = e.Length;
		bool flag = ((length < 32 || length > 67108864) ? true : false);
		if (flag || e.IsEncrypted)
		{
			counts.Unreadable++;
			return null;
		}
		byte[] array = new byte[e.Length];
		try
		{
			using Stream stream = e.Open();
			stream.ReadExactly(array);
			if (stream.ReadByte() != -1)
			{
				counts.Unreadable++;
				return null;
			}
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is NotSupportedException || ex is ArgumentException || ex is OverflowException) ? true : false)
		{
			counts.Unreadable++;
			return null;
		}
		if (!Dxbc.Valid(array))
		{
			counts.NotOne++;
			return null;
		}
		return array;
	}
}
