using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Unity;

public sealed class UnityReader : IEngineReader
{
	public sealed record Loc(string Path, string? Node, long Start, long Size, int ClassId);

	public sealed record Source(string Path, string? Node, UnityFiles.SerializedFile File, UnityFiles.ReadAt Read, UnityFiles.Bundle? In, IDisposable? Owner) : IDisposable
	{
		public void Dispose()
		{
			Owner?.Dispose();
		}
	}

	public const string Family = "Unity";

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	private static readonly Regex LogApi = new Regex("^\\s*Version:\\s+(Direct3D 11|Direct3D 12|Vulkan)", RegexOptions.Multiline);

	private readonly ConcurrentDictionary<string, string> dataDirs = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private static readonly string[] Managers = new string[3] { "globalgamemanagers", "globalgamemanagers.assets", "mainData" };

	public string LocalLow { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

	public static (string Player, string Data)? Find(Game game)
	{
		EnumerationOptions enumerationOptions = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			MaxRecursionDepth = 1,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None
		};
		IEnumerable<string> enumerable = new _003C_003Ez__ReadOnlyArray<string>(new string[2]
		{
			Path.GetDirectoryName(game.ExePath) ?? "",
			game.InstallDir
		});
		if (Directory.Exists(game.InstallDir))
		{
			enumerable = enumerable.Concat(Directory.EnumerateDirectories(game.InstallDir, "*", enumerationOptions));
		}
		foreach (string item in enumerable.Where((string d) => d.Length > 0 && File.Exists(Path.Combine(d, "UnityPlayer.dll"))))
		{
			string own = Path.Combine(item, Path.GetFileNameWithoutExtension(game.ExePath) + "_Data");
			string text = (from d in Directory.EnumerateDirectories(item, "*_Data")
				orderby !d.Equals(own, StringComparison.OrdinalIgnoreCase)
				select d).FirstOrDefault((string d) => File.Exists(Path.Combine(d, "globalgamemanagers")) || File.Exists(Path.Combine(d, "data.unity3d")));
			if (text != null)
			{
				return (item, text);
			}
		}
		return null;
	}

	public EngineInfo? Detect(Game game)
	{
		(string, string)? tuple = Find(game);
		if (tuple.HasValue)
		{
			(string, string) valueOrDefault = tuple.GetValueOrDefault();
			string item = valueOrDefault.Item1;
			string item2 = valueOrDefault.Item2;
			string text = null;
			int[] array = null;
			int num = 0;
			int num2 = 0;
			foreach (Source item3 in Sources(item2, onlyManagers: true))
			{
				using (item3)
				{
					if (text == null)
					{
						text = item3.File.UnityVersion;
					}
					UnityFiles.Obj obj = item3.File.Objects.FirstOrDefault((UnityFiles.Obj o) => o.ClassId == 141);
					if ((object)obj != null && array == null)
					{
						array = UnityFiles.GraphicsApis(item3.Read(obj.Start, (int)obj.Size));
					}
					foreach (UnityFiles.Obj item4 in item3.File.Objects.Where((UnityFiles.Obj o) => o.ClassId == 48).Take(3 - num))
					{
						num++;
						try
						{
							if (UnityFiles.ShaderBlob(item3.Read(item4.Start, (int)item4.Size)) != null)
							{
								num2++;
							}
						}
						catch (Exception ex) when ((ex is IOException || ex is InvalidDataException) ? true : false)
						{
						}
					}
				}
			}
			if (string.IsNullOrEmpty(text))
			{
				FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(Path.Combine(item, "UnityPlayer.dll"));
				text = ((versionInfo != null && versionInfo.FileMajorPart > 0) ? $"{versionInfo.FileMajorPart}.{versionInfo.FileMinorPart}.{versionInfo.FileBuildPart}" : "?");
			}
			string lastRun = LastRun(PlayerLog(item2));
			return new EngineInfo("Unity", text, null, Api(array, lastRun), Encrypted: false, (num > 0 && num2 == 0) ? $"Unity {text}: none of {num} built-in shaders has DirectX (d3d11) code this reader can read" : null, NoRtPipelines: false, NoRayTracing: false, AftermathShaderDebug: false, VersionGuessed: false, ApiGuessed(array, lastRun));
		}
		return null;
	}

	public static string Api(int[]? apis, string? lastRun)
	{
		if (lastRun != null)
		{
			return lastRun + " (last run)";
		}
		return (apis == null || apis.Length < 1) ? "D3D11 or D3D12" : (apis[0] switch
		{
			2 => "D3D11", 
			18 => "D3D12", 
			21 => "Vulkan", 
			_ => "OpenGL", 
		});
	}

	public static bool ApiGuessed(int[]? apis, string? lastRun)
	{
		bool flag = lastRun == null;
		bool flag2;
		if (flag)
		{
			if (apis != null && apis.Length >= 1)
			{
				int num = apis[0];
				if (num == 2 || num == 18)
				{
					flag2 = true;
					goto IL_0024;
				}
			}
			flag2 = false;
			goto IL_0024;
		}
		goto IL_0026;
		IL_0024:
		flag = flag2;
		goto IL_0026;
		IL_0026:
		if (flag && apis.Contains(2))
		{
			return apis.Contains(18);
		}
		return false;
	}

	private string? PlayerLog(string data)
	{
		try
		{
			string[] array = File.ReadAllLines(Path.Combine(data, "app.info"));
			object result;
			if (array != null && array.Length >= 2)
			{
				string path = array[0];
				string path2 = array[1];
				result = Path.Combine(LocalLow, path, path2, "Player.log");
			}
			else
			{
				result = null;
			}
			return (string?)result;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public string DetectStamp(Game game, EngineInfo? engine)
	{
		if (engine != null && engine.Family != "Unity")
		{
			return "";
		}
		if (!dataDirs.TryGetValue(game.ExePath, out string value))
		{
			(string, string)? tuple = Find(game);
			if (tuple.HasValue)
			{
				string item = tuple.GetValueOrDefault().Item2;
				value = (dataDirs[game.ExePath] = item);
			}
		}
		object obj;
		if (value != null)
		{
			string text2 = PlayerLog(value);
			obj = ((text2 != null) ? new FileInfo(text2) : null);
		}
		else
		{
			obj = null;
		}
		FileInfo fileInfo = (FileInfo)obj;
		if (fileInfo == null || !fileInfo.Exists)
		{
			return "";
		}
		return $"{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
	}

	private static string? LastRun(string? log)
	{
		try
		{
			if (log == null || !File.Exists(log))
			{
				return null;
			}
			using StreamReader streamReader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
			char[] array = new char[262144];
			Match match = LogApi.Match(new string(array, 0, streamReader.ReadBlock(array)));
			string result;
			if (!match.Success)
			{
				result = null;
			}
			else
			{
				string value = match.Groups[1].Value;
				string text;
				if (value == "Direct3D 11")
				{
					text = "D3D11";
				}
				else
				{
					text = ((!(value == "Direct3D 12")) ? "Vulkan" : "D3D12");
				}
				result = text;
			}
			return result;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		string text = Find(game)?.Data ?? throw new DirectoryNotFoundException(game.InstallDir + ": no Unity data folder");
		Dictionary<string, Loc> dictionary = new Dictionary<string, Loc>();
		Dictionary<string, ShaderInfo> dictionary2 = new Dictionary<string, ShaderInfo>();
		List<ShaderMap> list = new List<ShaderMap>();
		HashSet<string> hashSet = new HashSet<string>();
		HashSet<UnityFiles.Bundle> hashSet2 = new HashSet<UnityFiles.Bundle>();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		int num6 = 0;
		foreach (Source item in Sources(text, onlyManagers: false))
		{
			using (item)
			{
				ct.ThrowIfCancellationRequested();
				num++;
				if (item.In != null)
				{
					hashSet2.Add(item.In);
				}
				string library = Path.GetRelativePath(text, item.Path) + ((item.Node != null) ? ("|" + item.Node) : "");
				foreach (UnityFiles.Obj item2 in item.File.Objects.Where((UnityFiles.Obj o) =>
				{
					int classId = o.ClassId;
					return (classId == 48 || classId == 72) ? true : false;
				}))
				{
					if (item2.ClassId == 48)
					{
						num2++;
					}
					else
					{
						num3++;
					}
					List<byte[]> list2;
					try
					{
						list2 = Programs(item.Read(item2.Start, (int)item2.Size), item2.ClassId)?.ToList();
					}
					catch (Exception ex) when ((ex is IOException || ex is InvalidDataException) ? true : false)
					{
						num5++;
						continue;
					}
					if (list2 == null)
					{
						num4++;
						continue;
					}
					List<string> list3 = new List<string>();
					bool flag = false;
					foreach (byte[] item3 in list2)
					{
						string text2 = Convert.ToHexStringLower(SHA1.HashData(item3));
						if (dictionary.TryAdd(text2, new Loc(item.Path, item.Node, item2.Start, item2.Size, item2.ClassId)))
						{
							try
							{
								if (Dxbc.Kind(item3) >= 0)
								{
									ShaderInfo shaderInfo = ShaderContainer.Parse(item3, text2, new ResourceCounts(0, 0, 0, 0));
									if ((object)shaderInfo != null)
									{
										dictionary2[text2] = shaderInfo with
										{
											Counts = CarvedReader.Counts(shaderInfo.Bindings)
										};
									}
								}
							}
							catch (Exception ex2) when ((ex2 is ArgumentException || ex2 is IndexOutOfRangeException) ? true : false)
							{
								num6++;
							}
						}
						if (dictionary2.ContainsKey(text2))
						{
							list3.Add(text2);
							flag |= !Dxbc.Part(item3, "DXIL"u8).IsEmpty;
						}
					}
					list3 = list3.Distinct().ToList();
					string text3 = CarvedReader.Sha1Hex(string.Join(',', list3.Order(StringComparer.Ordinal)));
					if (list3.Count > 0 && hashSet.Add(text3))
					{
						list.Add(new ShaderMap(text3, library, flag ? "PCD3D_SM6" : "PCD3D_SM5", list3));
					}
				}
			}
		}
		located[game.Id] = dictionary;
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
		foreach (string item4 in dictionary.Values.Select((Loc l) => l.Path).Distinct().Order(StringComparer.OrdinalIgnoreCase))
		{
			FileInfo fileInfo = new FileInfo(item4);
			incrementalHash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(text, item4)}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}\n"));
		}
		log?.Report($"{num} serialized files ({hashSet2.Count} bundles, {hashSet2.Sum((UnityFiles.Bundle b) => b.BlocksDecompressed)} blocks decompressed): {num2} Shader objects{((num4 > 0) ? $" ({num4} without d3d11 code)" : "")}{((num5 > 0) ? $" ({num5} unreadable)" : "")}, {num3} ComputeShaders -> {dictionary2.Count} shaders (" + string.Join(", ", from s in dictionary2.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select $"{g.Count()} {g.Key}") + $"){((num6 > 0) ? $", {num6} unparseable" : "")}, {list.Count} maps ({stopwatch.Elapsed.TotalSeconds:F1}s)");
		List<string> platforms = list.Select((ShaderMap m) => m.Platform).Distinct().Order(StringComparer.Ordinal)
			.ToList();
		return new ShaderIndex(Convert.ToHexStringLower(incrementalHash.GetHashAndReset()), platforms, dictionary2, list);
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		if (!located.TryGetValue(game.Id, out IReadOnlyDictionary<string, Loc> locs))
		{
			Index(game, engine, null, ct);
			locs = located[game.Id];
		}
		HashSet<string> hashSet = new HashSet<string>();
		foreach (IGrouping<(string, string), Loc> item in from l in (from s in sha1s.Where(locs.ContainsKey)
				select locs[s]).Distinct()
			group l by (Path: l.Path, Node: l.Node))
		{
			using Source source = Open(item.Key.Item1, item.Key.Item2);
			if (source == null)
			{
				continue;
			}
			foreach (Loc item2 in item.OrderBy((Loc l) => l.Start))
			{
				ct.ThrowIfCancellationRequested();
				List<byte[]> list;
				try
				{
					list = Programs(source.Read(item2.Start, (int)item2.Size), item2.ClassId)?.ToList() ?? new List<byte[]>();
				}
				catch (Exception ex) when ((ex is IOException || ex is InvalidDataException) ? true : false)
				{
					continue;
				}
				foreach (byte[] item3 in list)
				{
					string text = Convert.ToHexStringLower(SHA1.HashData(item3));
					if (sha1s.Contains(text) && hashSet.Add(text))
					{
						sink(text, item3);
					}
				}
			}
		}
	}

	private static IEnumerable<byte[]>? Programs(byte[] obj, int cls)
	{
		if (cls != 72)
		{
			return UnityFiles.ShaderBlob(obj)?.SelectMany((byte[] b) => from c in Dxbc.Containers(b)
				select c.Container);
		}
		return from c in Dxbc.Containers(obj)
			select c.Container;
	}

	private static bool IsResource(string name)
	{
		if (!name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase))
		{
			return name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static IEnumerable<Source> Sources(string data, bool onlyManagers)
	{
		EnumerationOptions enumerationOptions = new EnumerationOptions
		{
			RecurseSubdirectories = !onlyManagers,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None,
			MaxRecursionDepth = 8
		};
		IOrderedEnumerable<string> orderedEnumerable = (from p in Directory.EnumerateFiles(data, "*", enumerationOptions)
			where !IsResource(p)
			select p).Order(StringComparer.OrdinalIgnoreCase);
		IEnumerable<string> enumerable2;
		if (!onlyManagers)
		{
			IEnumerable<string> enumerable = orderedEnumerable;
			enumerable2 = enumerable;
		}
		else
		{
			enumerable2 = orderedEnumerable.Where((string p) => Managers.Append("data.unity3d").Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase));
		}
		foreach (string path in enumerable2)
		{
			Source source = Open(path, null);
			if ((object)source != null)
			{
				yield return source;
				continue;
			}
			UnityFiles.Bundle b;
			try
			{
				b = UnityFiles.Bundle.Open(path);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException) ? true : false)
			{
				continue;
			}
			if (b == null)
			{
				continue;
			}
			using (b)
			{
				foreach (UnityFiles.Node item in b.Nodes.Where((UnityFiles.Node n) => !IsResource(n.Path) && (!onlyManagers || Managers.Contains(n.Path, StringComparer.OrdinalIgnoreCase))))
				{
					UnityFiles.SerializedFile serializedFile;
					try
					{
						serializedFile = UnityFiles.ReadSerialized(b.Reader(item), item.Size);
					}
					catch (InvalidDataException)
					{
						serializedFile = null;
					}
					if (serializedFile != null)
					{
						yield return new Source(path, item.Path, serializedFile, b.Reader(item), b, null);
					}
				}
			}
		}
	}

	private static Source? Open(string path, string? node)
	{
		try
		{
			if (node != null)
			{
				UnityFiles.Bundle bundle = UnityFiles.Bundle.Open(path);
				UnityFiles.Node node2 = bundle?.Nodes.FirstOrDefault((UnityFiles.Node n) => n.Path == node);
				if ((object)node2 != null)
				{
					UnityFiles.SerializedFile serializedFile = UnityFiles.ReadSerialized(bundle.Reader(node2), node2.Size);
					if ((object)serializedFile != null)
					{
						return new Source(path, node, serializedFile, bundle.Reader(node2), bundle, bundle);
					}
				}
				bundle?.Dispose();
				return null;
			}
			SafeFileHandle h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
			long len = RandomAccess.GetLength(h);
			UnityFiles.SerializedFile serializedFile2 = UnityFiles.ReadSerialized(Read, len);
			if ((object)serializedFile2 != null)
			{
				return new Source(path, null, serializedFile2, Read, null, h);
			}
			h.Dispose();
			return null;
			byte[] Read(long off, int count)
			{
				byte[] array = new byte[Math.Max(0L, Math.Min(count, len - off))];
				if (RandomAccess.Read(h, array, off) != array.Length)
				{
					return array[..0];
				}
				return array;
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) ? true : false)
		{
			return null;
		}
	}
}
