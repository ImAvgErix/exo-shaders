using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Planning;

public static class Middleware
{
	private sealed record KeptImage(string Stamp, MiddlewareImage Image);

	public static readonly string[] SharedVendors = new string[2] { "amd", "intel" };

	private static readonly string[] ProxyNames = new string[9] { "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll", "dinput8.dll", "nvngx.dll" };

	private static readonly string[] OptiScalerBundle = new string[4] { "FSR4", "FidelityFX", "XeSS", "FSR3 frame generation" };

	private static readonly ConcurrentDictionary<(string, string), MiddlewareImage> scans = new ConcurrentDictionary<(string, string), MiddlewareImage>();

	private static readonly Dictionary<string, KeptImage> kept = new Dictionary<string, KeptImage>();

	private static long keptVersion;

	private static readonly Dictionary<string, long> keptSaved = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private static readonly Lock keptGate = new Lock();

	private static readonly JsonSerializerOptions KeptJson = new JsonSerializerOptions
	{
		IncludeFields = true
	};

	public static (string Vendor, string Name, bool Packable)? Classify(string fileName, string? exportName)
	{
		foreach (string item in new string[2] { exportName, fileName }.OfType<string>())
		{
			string text = item.ToLowerInvariant();
			if (text.EndsWith(".dll") || text.EndsWith(".asi"))
			{
				if (text.StartsWith("optiscaler"))
				{
					return ("optiscaler", "OptiScaler.dll", true);
				}
				if (text == "amdxcffx64.dll")
				{
					return ("amd", item, true);
				}
				if (text.StartsWith("amd_fidelityfx_") && text.Contains("dx12"))
				{
					return ("amd", item, true);
				}
				if (text.StartsWith("ffx_") && !text.Contains("vk"))
				{
					return ("amd", item, true);
				}
				if (text.StartsWith("dlssg_to_fsr3"))
				{
					return ("mod", item, true);
				}
				if (text.StartsWith("libxess"))
				{
					return ("intel", item, true);
				}
				if (text.StartsWith("nvngx_dlss"))
				{
					return ("nvidia", item, false);
				}
				if (text.StartsWith("sl.") && text != "sl.interposer.dll")
				{
					return ("nvidia", item, true);
				}
				if (text == "dstoragecore.dll")
				{
					return ("microsoft", item, true);
				}
			}
		}
		return null;
	}

	public static string Label(MiddlewareDll d)
	{
		string text = d.Name.ToLowerInvariant();
		if (!(d.Vendor == "optiscaler"))
		{
			if (!(text == "amdxcffx64.dll"))
			{
				if (!text.StartsWith("amd_fidelityfx_"))
				{
					if (!text.StartsWith("ffx_"))
					{
						if (!text.StartsWith("dlssg_to_fsr3"))
						{
							if (!text.StartsWith("libxess"))
							{
								if (!text.StartsWith("nvngx_dlss"))
								{
									if (!text.StartsWith("sl."))
									{
										if (!(text == "dstoragecore.dll"))
										{
											return Path.GetFileNameWithoutExtension(d.Name);
										}
										return "DirectStorage";
									}
									return "Streamline";
								}
								return "DLSS";
							}
							return "XeSS";
						}
						return "FSR3 frame generation";
					}
					return "FSR 2/3";
				}
				return "FidelityFX";
			}
			return "FSR4";
		}
		return "OptiScaler";
	}

	public static List<MiddlewareTag> Tags(IEnumerable<MiddlewareDll> dlls, Func<MiddlewareDll, int> pipelines)
	{
		List<MiddlewareTag> list = (from g in dlls.GroupBy(Label)
			select new MiddlewareTag(g.Key, g.Select((MiddlewareDll d) => d.Name).ToList(), g.Sum(pipelines))).Where((MiddlewareTag t) =>
		{
			string label2 = t.Label;
			return (!(label2 == "DirectStorage") && !(label2 == "Streamline")) || 1 == 0 || t.Pipelines > 0;
		}).ToList();
		MiddlewareTag middlewareTag = list.Find((MiddlewareTag t) => t.Label == "OptiScaler");
		if ((object)middlewareTag != null)
		{
			List<MiddlewareTag> list2 = list.Where((MiddlewareTag t) => OptiScalerBundle.Contains(t.Label)).ToList();
			List<string> list3 = (from t in list2
				where t.Pipelines > 0
				orderby t.Pipelines descending
				select t.Label).ToList();
			int index = list.IndexOf(middlewareTag);
			string label = ((list3.Count > 0) ? ("OptiScaler (" + string.Join(", ", list3) + ")") : "OptiScaler");
			List<string> list4 = new List<string>();
			list4.AddRange(middlewareTag.Dlls);
			list4.AddRange(list2.SelectMany((MiddlewareTag t) => t.Dlls));
			list[index] = new MiddlewareTag(label, new _003C_003Ez__ReadOnlyList<string>(list4), middlewareTag.Pipelines + list2.Sum((MiddlewareTag t) => t.Pipelines));
			list.RemoveAll(list2.Contains);
		}
		return list.OrderBy((MiddlewareTag t) => (t.Pipelines <= 0) ? 1 : 0).ToList();
	}

	public static List<MiddlewareDll> Detect(Game game)
	{
		if (GameFiles.DetectAntiCheat(game) == AntiCheat.None)
		{
			string directoryName = Path.GetDirectoryName(game.ExePath);
			if (directoryName != null)
			{
				return Detect(directoryName);
			}
		}
		return new List<MiddlewareDll>();
	}

	public static List<MiddlewareDll> Detect(string exeDir)
	{
		List<MiddlewareDll> list = new List<MiddlewareDll>();
		if (!Directory.Exists(exeDir))
		{
			return list;
		}
		foreach (string item in Directory.EnumerateFiles(exeDir, "*", new EnumerationOptions
		{
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None
		}).Order(StringComparer.OrdinalIgnoreCase))
		{
			string fileName = Path.GetFileName(item);
			if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			(string, string, bool)? tuple = Classify(fileName, null);
			string exportName = null;
			if (tuple.HasValue || ProxyNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
			{
				try
				{
					exportName = ExportName(item);
				}
				catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
				}
				(string, string, bool)? tuple2 = Classify(fileName, exportName);
				if (tuple2.HasValue)
				{
					(string, string, bool) valueOrDefault = tuple2.GetValueOrDefault();
					list.Add(new MiddlewareDll(valueOrDefault.Item1, valueOrDefault.Item2, item, valueOrDefault.Item3));
				}
			}
		}
		return (from g in list.GroupBy((MiddlewareDll d) => d.Name, StringComparer.OrdinalIgnoreCase)
			select g.OrderBy((MiddlewareDll d) => (!Path.GetFileName(d.Path).Equals(d.Name, StringComparison.OrdinalIgnoreCase)) ? 1 : 0).First()).ToList();
	}

	public static string? ExportName(string path)
	{
		try
		{
			using PEReader pe = PeFile.Open(path);
			return PeFile.ExportName(pe);
		}
		catch (Exception ex) when ((ex is BadImageFormatException || ex is InvalidOperationException) ? true : false)
		{
			return null;
		}
	}

	public static void LoadImages(string file)
	{
		try
		{
			if (!File.Exists(file))
			{
				return;
			}
			Dictionary<string, KeptImage> dictionary = JsonSerializer.Deserialize<Dictionary<string, KeptImage>>(File.ReadAllBytes(file), KeptJson);
			if (dictionary == null)
			{
				return;
			}
			using (keptGate.EnterScope())
			{
				foreach (var (key, keptImage2) in dictionary)
				{
					if (keptImage2?.Image.Lanes != null)
					{
						kept.TryAdd(key, keptImage2);
					}
				}
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
		{
		}
	}

	public static void SaveImages(string file)
	{
		using (keptGate.EnterScope())
		{
			if (keptSaved.GetValueOrDefault(file) != keptVersion)
			{
				AppStore.WriteAtomic(file, JsonSerializer.SerializeToUtf8Bytes(kept, KeptJson));
				keptSaved[file] = keptVersion;
			}
		}
	}

	public static void ForgetScans()
	{
		scans.Clear();
		using (keptGate.EnterScope())
		{
			kept.Clear();
		}
	}

	public static MiddlewareImage Scan(string path)
	{
		FileInfo fileInfo = new FileInfo(path);
		(string, string) key = (fileInfo.FullName.ToLowerInvariant(), KeyFiles.Stamp(fileInfo.FullName) ?? "");
		if (scans.TryGetValue(key, out MiddlewareImage value))
		{
			return value;
		}
		using (keptGate.EnterScope())
		{
			KeptImage valueOrDefault = kept.GetValueOrDefault(key.Item1);
			if ((object)valueOrDefault != null && valueOrDefault.Stamp == key.Item2)
			{
				return scans[key] = valueOrDefault.Image;
			}
		}
		byte[] array;
		using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		{
			array = new byte[fileStream.Length];
			fileStream.ReadExactly(array);
		}
		Dictionary<string, (long, int)> dictionary = new Dictionary<string, (long, int)>();
		Dictionary<string, (uint, uint)> dictionary2 = new Dictionary<string, (uint, uint)>();
		Span<byte> span = array.AsSpan();
		int num;
		for (num = 0; num < span.Length; num += 4)
		{
			int num2 = ((ReadOnlySpan<byte>)span.Slice(num)).IndexOf("DXBC"u8);
			if (num2 < 0)
			{
				break;
			}
			num += num2;
			int num3 = Dxbc.HeaderSize(span.Slice(num));
			if (num3 > 0 && (long)num + (long)num3 <= span.Length && Dxbc.Valid(span.Slice(num, num3)))
			{
				string key2 = PsoDb.Hex(SHA1.HashData(span.Slice(num, num3)));
				if (dictionary.TryAdd(key2, (num, num3)))
				{
					(uint, uint)? tuple = Dxbc.WaveLanes(span.Slice(num, num3));
					if (tuple.HasValue)
					{
						(uint, uint) valueOrDefault2 = tuple.GetValueOrDefault();
						dictionary2[key2] = valueOrDefault2;
					}
				}
			}
		}
		MiddlewareImage middlewareImage = new MiddlewareImage(fileInfo.FullName, PsoDb.Hex(SHA1.HashData(array)), array.Length, dictionary)
		{
			Lanes = dictionary2
		};
		if (scans.Count > 64)
		{
			scans.Clear();
		}
		scans[key] = middlewareImage;
		using (keptGate.EnterScope())
		{
			if (key.Item2.Length > 0)
			{
				kept[key.Item1] = new KeptImage(key.Item2, middlewareImage);
				keptVersion++;
			}
		}
		return middlewareImage;
	}

	public static IEnumerable<(string Sha1, byte[] Bytes)> Blobs(Game game, IReadOnlySet<string> sha1s)
	{
		if (sha1s.Count == 0)
		{
			yield break;
		}
		HashSet<string> left = sha1s.ToHashSet();
		foreach (MiddlewareDll item in from d in Detect(game)
			where d.Packable
			select d)
		{
			if (left.Count == 0)
			{
				yield break;
			}
			MiddlewareImage image = TryScan(item.Path);
			if ((object)image == null)
			{
				continue;
			}
			foreach (string item2 in left.Where(image.Containers.ContainsKey).ToList())
			{
				byte[] array = image.Read(item2);
				if (array != null)
				{
					left.Remove(item2);
					yield return (Sha1: item2, Bytes: array);
				}
			}
		}
	}

	internal static MiddlewareImage? TryScan(string path)
	{
		try
		{
			return Scan(path);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}
}
