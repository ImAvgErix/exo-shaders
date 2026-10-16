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

namespace SCSKiller.Core.FromSoft;

public sealed class FromSoftReader(string dataDir, Func<string, byte[]?>? download = null, string uxmSha256 = "e016cb66fed930d70a60d982b25fab9ec3729bd7fb1f48bb35c6bf059ca23441") : IEngineReader
{
	public sealed record Title(string Exe, string Name, string Api, bool Hash64, string[] Archives, string? Uxm = null);

	public const string Family = "FromSoftware";

	private readonly SoulsKeys keys = new SoulsKeys(dataDir, download, uxmSha256);

	public static readonly Title[] Titles = new Title[5]
	{
		new Title("DarkSoulsIII.exe", "Dark Souls III", "D3D11", Hash64: false, new string[7] { "Data1", "Data2", "Data3", "Data4", "Data5", "DLC1", "DLC2" }, "DarkSouls3Keys"),
		// Sekiro: DS3's BHD5 layout (32-bit path hashes, salted), Data1..Data5, KRAK DCX; its /shader/*.shaderbnd.dcx and
		// shaderbdle.shaderbdlebnd.dcx names are all in ShaderFiles. Keys: UXM Selective Unpack's SekiroKeys (same pinned file).
		new Title("sekiro.exe", "Sekiro: Shadows Die Twice", "D3D11", Hash64: false, new string[5] { "Data1", "Data2", "Data3", "Data4", "Data5" }, "SekiroKeys"),
		new Title("DarkSoulsRemastered.exe", "Dark Souls: Remastered", "D3D11", Hash64: false, Array.Empty<string>()),
		new Title("eldenring.exe", "Elden Ring", "D3D12", Hash64: true, new string[5] { "Data0", "Data1", "Data2", "Data3", "DLC" }, "EldenRingKeys"),
		new Title("nightreign.exe", "Elden Ring Nightreign", "D3D12", Hash64: true, new string[4] { "Data0", "Data1", "Data2", "Data3" })
	};

	public static readonly string[] ShaderFiles;

	private const int IndexVersion = 2;

	private const string D3D11 = "D3D11";

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, string>>();

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, byte[]>> created = new ConcurrentDictionary<string, IReadOnlyDictionary<string, byte[]>>();

	public static Title? TitleOf(Game game)
	{
		return Titles.FirstOrDefault((Title t) => t.Exe.Equals(Path.GetFileName(game.ExePath), StringComparison.OrdinalIgnoreCase));
	}

	public int ReaderVersion(EngineInfo engine)
	{
		return 2;
	}

	public EngineInfo? Detect(Game game)
	{
		string directoryName = Path.GetDirectoryName(game.ExePath);
		if (directoryName == null || !Directory.Exists(directoryName))
		{
			return null;
		}
		Title title = TitleOf(game);
		if (title == null)
		{
			if (!Directory.EnumerateFiles(directoryName, "*.bhd").Any((string path) => File.Exists(Path.ChangeExtension(path, ".bdt")) && IsEncryptedBhd(path)))
			{
				return null;
			}
			return new EngineInfo("FromSoftware", "-", null, "D3D11 or D3D12", Encrypted: false, "FromSoftware archives SCSKiller has no key for (a title it doesn't know yet)");
		}
		List<(FileInfo, string)> list = Archives(game, directoryName, title, null, out string why);
		if (list == null)
		{
			return new EngineInfo("FromSoftware", "-", title.Name, title.Api, Encrypted: false, why);
		}
		int programs = 0;
		int dxil = 0;
		int rts0 = 0;
		foreach (var (name, b) in ShaderFilesOf(directoryName, title, list, sample: true, null, CancellationToken.None))
		{
			Visit(name, b, (string _, byte[] c) =>
			{
				int num = Dxbc.Kind(c);
				if ((num >= 0 && num != 6) || 1 == 0)
				{
					programs++;
					if (!Dxbc.Part(c, "DXIL"u8).IsEmpty)
					{
						dxil++;
					}
					if (!Dxbc.Part(c, "RTS0"u8).IsEmpty)
					{
						rts0++;
					}
				}
			}, CancellationToken.None);
			if (programs > 0)
			{
				break;
			}
		}
		string version = ((programs == 0) ? "-" : (((dxil == programs) ? "DXIL" : ((dxil == 0) ? "DXBC" : "DXIL+DXBC")) + (((double)rts0 >= 0.9 * (double)programs) ? "+RTS0" : "")));
		string name2 = title.Name;
		string api = title.Api;
		string unsupported = ((programs == 0) ? "no shaders found in its shader files" : null);
		string note = why;
		return new EngineInfo("FromSoftware", version, name2, api, Encrypted: false, unsupported, NoRtPipelines: false, NoRayTracing: false, AftermathShaderDebug: false, VersionGuessed: false, ApiGuessed: false, null, null, note);
	}

	public string DetectStamp(Game game, EngineInfo? engine)
	{
		return IndexStamp(game);
	}

	public string IndexStamp(Game game)
	{
		Title title = TitleOf(game);
		if ((object)title != null)
		{
			string[] archives = title.Archives;
			if (archives != null && archives.Length > 0)
			{
				string directoryName = Path.GetDirectoryName(game.ExePath);
				if (directoryName != null)
				{
					return string.Join(',', keys.Unstored(game, from f in SourceFiles(directoryName, title)
						select Path.GetFileNameWithoutExtension(f.Name)));
				}
			}
		}
		return "";
	}

	private static bool IsEncryptedBhd(string path)
	{
		try
		{
			using FileStream fileStream = File.OpenRead(path);
			Span<byte> span = stackalloc byte[4];
			return fileStream.Length >= 256 && fileStream.Length % 256 == 0L && fileStream.Read(span) == 4 && !((ReadOnlySpan<byte>)span).SequenceEqual("BHD5"u8);
		}
		catch (IOException)
		{
			return false;
		}
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Title title = TitleOf(game) ?? throw new NotSupportedException(game.ExePath + ": not a FromSoftware title this reader knows");
		Stopwatch stopwatch = Stopwatch.StartNew();
		string directoryName = Path.GetDirectoryName(game.ExePath);
		List<(FileInfo, string)> list = Archives(game, directoryName, title, log, out string why) ?? throw new InvalidDataException(why);
		Dictionary<string, string> fileOf = new Dictionary<string, string>();
		Dictionary<string, ShaderInfo> shaders = new Dictionary<string, ShaderInfo>();
		Dictionary<string, List<string>> pools = new Dictionary<string, List<string>>();
		Dictionary<string, string> partOf = new Dictionary<string, string>();
		Dictionary<string, (string Sha, byte[] Blob)?> rootSigs = new Dictionary<string, (string, byte[])?>();
		Dictionary<string, string> platformOf = new Dictionary<string, string>();
		int bad = 0;
		int num = 0;
		foreach (var item2 in ShaderFilesOf(directoryName, title, list, sample: false, null, ct))
		{
			string name = item2.Name;
			byte[] item = item2.Bytes;
			num++;
			Visit(name, item, (string at, byte[] c) =>
			{
				string text3 = Convert.ToHexStringLower(SHA1.HashData(c));
				if (fileOf.TryAdd(text3, name))
				{
					if (Dxbc.Kind(c) < 0)
					{
						return;
					}
					try
					{
						ShaderInfo shaderInfo = ShaderContainer.Parse(c, text3, new ResourceCounts(0, 0, 0, 0));
						if ((object)shaderInfo == null)
						{
							return;
						}
						shaders[text3] = shaderInfo with
						{
							Counts = CarvedReader.Counts(shaderInfo.Bindings)
						};
						platformOf[text3] = CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) ?? (Dxbc.Part(c, "DXIL"u8).IsEmpty ? "D3D11" : "D3D12");
						ReadOnlySpan<byte> source = Dxbc.Part(c, "RTS0"u8);
						if (!source.IsEmpty)
						{
							string text4 = Convert.ToHexStringLower(SHA1.HashData(source));
							partOf[text3] = text4;
							if (!rootSigs.ContainsKey(text4))
							{
								try
								{
									byte[] array = RootSig.AsVersion10(c);
									rootSigs[text4] = (Convert.ToHexStringLower(SHA1.HashData(array)), array);
								}
								catch (RootSig.SerializeException)
								{
									rootSigs[text4] = null;
								}
							}
						}
					}
					catch (Exception ex2) when ((ex2 is ArgumentException || ex2 is IndexOutOfRangeException) ? true : false)
					{
						int num2 = bad;
						bad = num2 + 1;
						return;
					}
				}
				if (shaders.ContainsKey(text3))
				{
					string text5 = Pool(at);
					if (shaders[text3].Stage == Stage.Library && text5.EndsWith("_[RT].shaderbdle", StringComparison.OrdinalIgnoreCase))
					{
						text5 += $"|p{RtCollections.OwnPayload(c)}";
					}
					if (!pools.TryGetValue(text5, out List<string> value))
					{
						value = (pools[text5] = new List<string>());
					}
					value.Add(text3);
				}
			}, ct);
		}
		foreach (var (key, key2) in partOf)
		{
			(string, byte[])? tuple = rootSigs[key2];
			if (tuple.HasValue)
			{
				(string, byte[]) valueOrDefault = tuple.GetValueOrDefault();
				shaders[key] = shaders[key]with
				{
					RootSignature = valueOrDefault.Item1
				};
			}
		}
		List<ShaderMap> list2 = pools.SelectMany((KeyValuePair<string, List<string>> p) => from s in p.Value.Distinct()
			group s by platformOf[s] into g
			select new ShaderMap(CarvedReader.Sha1Hex(p.Key + "|" + g.Key), p.Key, g.Key, g.ToList())).ToList();
		created[game.Id] = rootSigs.Values.OfType<(string, byte[])>().DistinctBy(((string Sha, byte[] Blob) r) => r.Sha).ToDictionary(((string Sha, byte[] Blob) r) => r.Sha, ((string Sha, byte[] Blob) r) => r.Blob);
		located[game.Id] = fileOf;
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
		incrementalHash.AppendData(Encoding.UTF8.GetBytes($"{"FromSoftware"} {2}\n"));
		IEnumerable<FileInfo> enumerable;
		if (title.Archives.Length != 0)
		{
			enumerable = list.Select(((FileInfo Bhd, string Key) a) => a.Bhd);
		}
		else
		{
			IEnumerable<FileInfo> enumerable2 = SourceFiles(directoryName, title);
			enumerable = enumerable2;
		}
		foreach (FileInfo item3 in enumerable)
		{
			incrementalHash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(directoryName, item3.FullName)}|{item3.Length}|{item3.LastWriteTimeUtc.Ticks}\n"));
		}
		List<string> list3 = shaders.Keys.Select((string s) => platformOf[s]).Distinct().OrderBy((string p) =>
		{
			bool flag = ((p == "D3D12" || p == "D3D11") ? true : false);
			return (!flag) ? 1 : 0;
		})
			.ThenBy((string p) => p, StringComparer.Ordinal)
			.ToList();
		log?.Report($"{title.Name}: {num} shader files, {shaders.Count} shaders (" + string.Join(", ", from s in shaders.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select $"{g.Count()} {g.Key}") + $"){((bad > 0) ? $", {bad} unparseable" : "")}, {shaders.Values.Select((ShaderInfo s) => s.RootSignature).OfType<string>().Distinct().Count()} embedded root signatures, " + $"{list2.Count} maps ({string.Join(", ", list3)}) ({stopwatch.Elapsed.TotalSeconds:F1}s)");
		return new ShaderIndex(Convert.ToHexStringLower(incrementalHash.GetHashAndReset()), list3, shaders, list2, created[game.Id]);
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		Title title = TitleOf(game) ?? throw new NotSupportedException(game.ExePath + ": not a FromSoftware title this reader knows");
		if (!located.TryGetValue(game.Id, out IReadOnlyDictionary<string, string> fileOf))
		{
			Index(game, engine, null, ct);
			fileOf = located[game.Id];
		}
		HashSet<string> only = (from s in sha1s.Where(fileOf.ContainsKey)
			select fileOf[s]).ToHashSet();
		HashSet<string> sent = new HashSet<string>();
		foreach (string sha in sha1s)
		{
			if (created[game.Id].TryGetValue(sha, out byte[] value) && sent.Add(sha))
			{
				sink(sha, value);
			}
		}
		string directoryName = Path.GetDirectoryName(game.ExePath);
		List<(FileInfo, string)> archives = Archives(game, directoryName, title, null, out string why, fetch: false) ?? throw new InvalidDataException(why);
		foreach (var (name, b) in ShaderFilesOf(directoryName, title, archives, sample: false, only, ct))
		{
			Visit(name, b, (string _, byte[] c) =>
			{
				string text = Convert.ToHexStringLower(SHA1.HashData(c));
				if (sha1s.Contains(text) && sent.Add(text))
				{
					sink(text, c);
				}
			}, ct);
		}
	}

	private static string Pool(string at)
	{
		string[] array = at.Split('|');
		return string.Join('|', ((array.Length > 1) ? array[..^1] : array).Select((string p, int i) => (i != 0) ? Path.GetFileName(p) : p));
	}

	private static IEnumerable<FileInfo> SourceFiles(string dir, Title title)
	{
		if (title.Archives.Length != 0)
		{
			return from a in title.Archives
				select new FileInfo(Path.Combine(dir, a + ".bhd")) into f
				where f.Exists && File.Exists(Path.ChangeExtension(f.FullName, ".bdt"))
				select f;
		}
		return new DirectoryInfo(Path.Combine(dir, "shader")).EnumerateFiles("*.dcx").OrderBy((FileInfo f) => f.Name, StringComparer.OrdinalIgnoreCase);
	}

	private List<(FileInfo Bhd, string Key)>? Archives(Game game, string dir, Title title, IProgress<string>? log, out string? why, bool fetch = true)
	{
		why = null;
		if (title.Archives.Length == 0)
		{
			return new List<(FileInfo, string)>();
		}
		List<FileInfo> source = SourceFiles(dir, title).ToList();
		Dictionary<string, string> found = keys.Get(game, source.ToDictionary((FileInfo f) => Path.GetFileNameWithoutExtension(f.Name), (FileInfo f) => f.FullName), fetch ? title.Uxm : null, out List<string> missing, log);
		List<(FileInfo, string)> list = (from f in source
			where found.ContainsKey(Path.GetFileNameWithoutExtension(f.Name))
			select (f: f, found[Path.GetFileNameWithoutExtension(f.Name)])).ToList();
		if (missing.Count == 0)
		{
			return list;
		}
		log?.Report(title.Name + ": no key for its archives " + string.Join(", ", missing) + ": the game's exe doesn't carry them in the clear" + ((title.Uxm != null) ? " and none that opens them could be downloaded (UXM Selective Unpack's)" : "") + ". Put one \"<archive> <key>\" line per archive (the body of its \"RSA PUBLIC KEY\" PEM) in " + keys.KeyFile(game));
		if (list.Count == 0)
		{
			why = "no key found for its archives" + ((title.Uxm != null) ? ", and none could be downloaded" : "");
			return null;
		}
		why = ((missing != null && missing.Count == 1 && missing[0] == "DLC") ? "Its DLC shaders are left out." : ("Shaders in " + string.Join(", ", missing) + " are left out."));
		return list;
	}

	private static IEnumerable<(string Name, byte[] Bytes)> ShaderFilesOf(string dir, Title title, IEnumerable<(FileInfo Bhd, string Key)> archives, bool sample, IReadOnlySet<string>? only, CancellationToken ct)
	{
		if (title.Archives.Length == 0)
		{
			IEnumerable<(string, FileInfo)> enumerable = from f in SourceFiles(dir, title)
				select (Name: "/shader/" + f.Name, File: f) into f
				where only?.Contains(f.Name) ?? true
				select f;
			IEnumerable<(string, FileInfo)> enumerable2;
			if (!sample)
			{
				enumerable2 = enumerable;
			}
			else
			{
				IEnumerable<(string, FileInfo)> enumerable3 = enumerable.OrderBy(((string Name, FileInfo File) f) => f.File.Length);
				enumerable2 = enumerable3;
			}
			foreach (var (item, fileInfo) in enumerable2)
			{
				ct.ThrowIfCancellationRequested();
				yield return (Name: item, Bytes: File.ReadAllBytes(fileInfo.FullName));
			}
			yield break;
		}
		Dictionary<ulong, string> byHash = ShaderFiles.Where((string n) => only?.Contains(n) ?? true).ToDictionary((string n) => Souls.NameHash(n, title.Hash64), (string n) => n);
		IEnumerable<(FileInfo, string)> enumerable4;
		if (!sample)
		{
			enumerable4 = archives;
		}
		else
		{
			IEnumerable<(FileInfo, string)> enumerable5 = archives.OrderBy(((FileInfo Bhd, string Key) a) => a.Bhd.Length);
			enumerable4 = enumerable5;
		}
		foreach (var (fileInfo2, key) in enumerable4)
		{
			if (byHash.Count == 0)
			{
				yield break;
			}
			List<Souls.Entry> source = (from e in Souls.ParseBhd(Souls.DecryptBhd(File.ReadAllBytes(fileInfo2.FullName), key), title.Hash64)
				where byHash.ContainsKey(e.Hash)
				select e).ToList();
			using SafeFileHandle bdt = File.OpenHandle(Path.ChangeExtension(fileInfo2.FullName, ".bdt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
			foreach (Souls.Entry item3 in sample ? source.OrderBy((Souls.Entry e) => e.Size) : source.OrderBy((Souls.Entry e) => e.Offset))
			{
				ct.ThrowIfCancellationRequested();
				string item2 = byHash[item3.Hash];
				byHash.Remove(item3.Hash);
				yield return (Name: item2, Bytes: Souls.Read(bdt, item3));
			}
		}
	}

	private static void Visit(string name, byte[] b, Action<string, byte[]> found, CancellationToken ct, int depth = 0)
	{
		ct.ThrowIfCancellationRequested();
		if (depth > 8)
		{
			throw new InvalidDataException(name + ": binders nested deeper than 8");
		}
		if (Souls.IsDcx(b))
		{
			Visit(name, Souls.Dcx(b), found, ct, depth + 1);
			return;
		}
		if (Souls.IsBnd4(b))
		{
			foreach (var (text, b2) in Souls.Bnd4(b))
			{
				Visit(name + "|" + text, b2, found, ct, depth + 1);
			}
			return;
		}
		foreach (var item2 in Dxbc.Containers(b))
		{
			byte[] item = item2.Container;
			found(name, item);
		}
	}

	static FromSoftReader()
	{
		List<string> list = new List<string>();
		list.AddRange(new string[12]
		{
			"debugfont", "guishader", "gxdecal", "gxffxshader", "gxflvershader", "gxgui", "gxposteffect", "gxrenderershader", "gxshader", "grass",
			"title", "gxraytracing"
		}.Select((string n) => "/shader/" + n + ".shaderbnd.dcx"));
		list.AddRange(new string[8] { "shaderbdle", "shaderbdle_[rt]", "shaderbdle_dlc01", "shaderbdle_dlc01_[rt]", "shaderbdle_dlc02", "shaderbdle_dlc02_[rt]", "speedtree", "speedtree_[rt]" }.SelectMany((string n) => new string[2]
		{
			"/shader/" + n + ".shaderbdlebnd.dcx",
			"/shader/" + n + ".shaderbdlebnd.devpatch.dcx"
		}));
		ShaderFiles = list.ToArray();
	}
}
