using System;
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
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Tank;

public sealed class TankReader(string? documents = null) : IEngineReader
{
	public sealed record Loc(string Path, long Offset);

	public const string Family = "Tank";

	public const string Platform = "PCD3D_SM5";

	public const string Product = "Prometheus";

	private static readonly byte[] Magic = new byte[8] { 255, 255, 255, 255, 255, 255, 255, 255 };

	private const int Readers = 4;

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	private string SettingsFile => Path.Combine(documents ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Overwatch", "Settings", "Settings_v0.ini");

	public EngineInfo? Detect(Game game)
	{
		Dictionary<string, string> dictionary = StaticContainer.ReadBuildConfig(game.InstallDir);
		if (dictionary.GetValueOrDefault("build-product") != "Prometheus" || !StaticContainer.Files(game.InstallDir).Any())
		{
			return null;
		}
		string text = dictionary.GetValueOrDefault("build-branch")?.Replace('_', '.');
		string version = ((text != null && text.Length > 0) ? text : (dictionary.GetValueOrDefault("build-number") ?? "-"));
		return new EngineInfo("Tank", version, null, Api(), Encrypted: false, null);
	}

	public string DetectStamp(Game game, EngineInfo? engine)
	{
		FileInfo fileInfo = new FileInfo(SettingsFile);
		if (fileInfo == null || !fileInfo.Exists)
		{
			return "-";
		}
		return $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
	}

	private string Api()
	{
		string text = Regex.Match(StaticContainer.SmallText(SettingsFile) ?? "", "^\\s*GraphicsAPI\\s*=\\s*\"?(\\w+)\"?", RegexOptions.Multiline).Groups[1].Value.ToLowerInvariant();
		if (!(text == "dx11"))
		{
			if (text == "dx12")
			{
				return "D3D12";
			}
			return "D3D11 or D3D12";
		}
		return "D3D11";
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		List<FileInfo> list = StaticContainer.Files(game.InstallDir).ToList();
		ConcurrentDictionary<string, ShaderInfo> shaders = new ConcurrentDictionary<string, ShaderInfo>();
		ConcurrentDictionary<string, Loc> locs = new ConcurrentDictionary<string, Loc>();
		ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> perFile = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
		long blobs = 0L;
		long assets = 0L;
		long dxil = 0L;
		long bad = 0L;
		ConcurrentBag<string> stops = new ConcurrentBag<string>();
		Parallel.ForEach(list, new ParallelOptions
		{
			TaskScheduler = TaskScheduler.Current,
			MaxDegreeOfParallelism = 4,
			CancellationToken = ct
		}, (FileInfo file) =>
		{
			ConcurrentDictionary<string, byte> mine = perFile.GetOrAdd(file.Name, (string _) => new ConcurrentDictionary<string, byte>());
			try
			{
				Walk(file.FullName, ct, (long at) =>
				{
					stops.Add($"{file.Name} at {at}");
				}, () =>
				{
					Interlocked.Increment(ref blobs);
				}, (long offset, List<byte[]> containers) =>
				{
					Interlocked.Increment(ref assets);
					foreach (byte[] container in containers)
					{
						if (!Dxbc.Part(container, "DXIL"u8).IsEmpty)
						{
							Interlocked.Increment(ref dxil);
						}
						else
						{
							string text = Convert.ToHexStringLower(SHA1.HashData(container));
							mine.TryAdd(text, 0);
							if (locs.TryAdd(text, new Loc(file.FullName, offset)))
							{
								try
								{
									ShaderInfo shaderInfo = ShaderContainer.Parse(container, text, new ResourceCounts(0, 0, 0, 0));
									if ((object)shaderInfo != null && Planner.IsD3D11(shaderInfo))
									{
										shaders[text] = shaderInfo with
										{
											Counts = CarvedReader.Counts(shaderInfo.Bindings),
											RootSignature = null
										};
									}
								}
								catch (Exception ex2) when ((ex2 is ArgumentException || ex2 is IndexOutOfRangeException) ? true : false)
								{
									Interlocked.Increment(ref bad);
								}
							}
						}
					}
				});
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				stops.Add(file.Name + " (" + ex.Message + ")");
			}
		});
		List<ShaderMap> list2 = (from p in (from p in perFile
				select (Key: p.Key, Shaders: p.Value.Keys.Where(shaders.ContainsKey).Order(StringComparer.Ordinal).ToList()) into p
				where p.Shaders.Count > 0
				select p).OrderBy(((string Key, List<string> Shaders) p) => p.Key, StringComparer.Ordinal)
			select new ShaderMap(CarvedReader.Sha1Hex($"{"data"}\\{p.Key}|{"PCD3D_SM5"}"), "data\\" + p.Key, "PCD3D_SM5", p.Shaders)).ToList();
		if (list2.Count == 0)
		{
			throw new InvalidDataException($"no DirectX 11 shader in {"data"}\\ ({blobs} blobs, {assets} shader assets): the storage layout may have changed");
		}
		located[game.Id] = new Dictionary<string, Loc>(locs.Where((KeyValuePair<string, Loc> l) => shaders.ContainsKey(l.Key)));
		log?.Report($"{shaders.Count} DirectX 11 shaders ({string.Join(", ", from s in shaders.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select $"{g.Count()} {g.Key}")}) in {assets} shader assets of {blobs} blobs; {dxil} DirectX 12 (DXIL) shaders not indexed: the game builds their root signatures at run time" + ((bad > 0) ? $"; {bad} unparseable" : "") + (stops.IsEmpty ? "" : ("; warning: stopped reading " + string.Join(", ", stops.Order()))) + $" ({stopwatch.Elapsed.TotalSeconds:F1}s)");
		return new ShaderIndex(ContentHash(game, list), new _003C_003Ez__ReadOnlySingleElementList<string>("PCD3D_SM5"), new Dictionary<string, ShaderInfo>(shaders), list2);
	}

	private static string ContentHash(Game game, IEnumerable<FileInfo> files)
	{
		StringBuilder stringBuilder = new StringBuilder(StaticContainer.BuildConfigText(game.InstallDir));
		foreach (FileInfo file in files)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(3, 3, stringBuilder2);
			handler.AppendLiteral("\n");
			handler.AppendFormatted(file.Name);
			handler.AppendLiteral("|");
			handler.AppendFormatted(file.Length);
			handler.AppendLiteral("|");
			handler.AppendFormatted(file.LastWriteTimeUtc.Ticks);
			stringBuilder2.Append(ref handler);
		}
		return CarvedReader.Sha1Hex(stringBuilder.ToString());
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		if (!located.TryGetValue(game.Id, out IReadOnlyDictionary<string, Loc> locs))
		{
			Index(game, engine, null, ct);
			locs = located[game.Id];
		}
		HashSet<string> hashSet = new HashSet<string>(sha1s);
		foreach (IGrouping<string, Loc> item in from l in (from s in sha1s.Where(locs.ContainsKey)
				select locs[s]).Distinct()
			group l by l.Path)
		{
			try
			{
				using SafeFileHandle safeFileHandle = StaticContainer.Open(item.Key);
				long length = RandomAccess.GetLength(safeFileHandle);
				foreach (long item2 in item.Select((Loc l) => l.Offset).Order())
				{
					ct.ThrowIfCancellationRequested();
					StaticContainer.Blob blob = StaticContainer.BlobAt(safeFileHandle, item2, length);
					if ((object)blob == null)
					{
						continue;
					}
					byte[] array = StaticContainer.Decode(safeFileHandle, blob);
					if (array == null)
					{
						continue;
					}
					foreach (byte[] item3 in Containers(array))
					{
						string text = Convert.ToHexStringLower(SHA1.HashData(item3));
						if (hashSet.Remove(text))
						{
							sink(text, item3);
						}
					}
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
		}
	}

	internal static void Walk(string path, CancellationToken ct, Action<long> stopped, Action counted, Action<long, List<byte[]>> asset)
	{
		using SafeFileHandle safeFileHandle = StaticContainer.Open(path);
		long length = RandomAccess.GetLength(safeFileHandle);
		Span<byte> span = stackalloc byte[8];
		int num = 0;
		foreach (StaticContainer.Blob item in StaticContainer.Blobs(safeFileHandle, length, stopped))
		{
			if (++num % 4096 == 0)
			{
				ct.ThrowIfCancellationRequested();
			}
			counted();
			if (StaticContainer.Peek(safeFileHandle, item, span) < 8 || !((ReadOnlySpan<byte>)span).SequenceEqual((ReadOnlySpan<byte>)Magic))
			{
				continue;
			}
			byte[] array = StaticContainer.Decode(safeFileHandle, item);
			if (array != null)
			{
				List<byte[]> list = Containers(array);
				if (list != null && list.Count > 0)
				{
					asset(item.Offset, list);
				}
			}
		}
	}

	internal static List<byte[]> Containers(byte[] asset)
	{
		if (asset.Length < 16 || !((ReadOnlySpan<byte>)asset.AsSpan(0, 8)).SequenceEqual((ReadOnlySpan<byte>)Magic))
		{
			return new List<byte[]>();
		}
		ReadOnlySpan<byte> value = new byte[3] { 31, 139, 8 };
		int i = 8;
		int num;
		for (int j = 0; j < 4; i += num + 1, j++)
		{
			num = ((ReadOnlySpan<byte>)asset.AsSpan(i)).IndexOf(value);
			if (num < 0)
			{
				break;
			}
			MemoryStream memoryStream = new MemoryStream();
			try
			{
				using GZipStream gZipStream = new GZipStream(new MemoryStream(asset, i + num, asset.Length - i - num), CompressionMode.Decompress);
				byte[] array = new byte[65536];
				int count;
				while (memoryStream.Length < 67108864 && (count = gZipStream.Read(array)) > 0)
				{
					memoryStream.Write(array, 0, count);
				}
			}
			catch (InvalidDataException)
			{
				continue;
			}
			return (from c in Dxbc.Containers(memoryStream.ToArray())
				select c.Container).ToList();
		}
		return new List<byte[]>();
	}
}
