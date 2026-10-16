using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Vendors;

public sealed class AmdAppCache(string dxcDir, string dxDir) : IAppCache
{
	public const string D3D12Prefix = "dxc:";

	public const string D3D11Prefix = "dx:";

	private static readonly (string Part, bool Substring, uint App)[] Profiles = new (string, bool, uint)[4]
	{
		("ff7rebirth", true, 1798294915u),
		("cyberpunk2077.exe", false, 2178836856u),
		("eldenring.exe", false, 3074122105u),
		("wonderlands.exe", false, 2244129509u)
	};

	public const long DxcCacheCap = 17179869184L;

	public string DxcDir { get; } = dxcDir;

	public string DxDir { get; } = dxDir;

	public static string? Key(string fileName, bool d3d12)
	{
		string[] array = fileName.Split('.');
		if (array.Length != 6 || !array[5].Equals("parc", StringComparison.OrdinalIgnoreCase) || !int.TryParse(array[4], out var result))
		{
			return null;
		}
		for (int i = 0; i < 4; i++)
		{
			result = array[i].Length;
			bool flag = ((result > 16 || result == 0) ? true : false);
			if (flag || !ulong.TryParse(array[i], NumberStyles.HexNumber, null, out var _))
			{
				return null;
			}
		}
		ulong num = ulong.Parse(array[0], NumberStyles.HexNumber);
		if (!d3d12)
		{
			return $"{"dx:"}{num:x16}";
		}
		if (num > uint.MaxValue)
		{
			return null;
		}
		return $"{"dxc:"}{num:x8}";
	}

	public static uint DxcAppHash(string exeFileName)
	{
		return Fnv1a(Path.GetFileName(exeFileName));
	}

	public static uint Fnv1a(string s)
	{
		uint num = 2166136261u;
		foreach (char c in s)
		{
			num = (num ^ (byte)c) * 16777619;
			num = (num ^ (byte)((int)c >> 8)) * 16777619;
		}
		return num;
	}

	public static string DxcKey(string exeFileName)
	{
		return $"{"dxc:"}{DxcAppHash(exeFileName):x8}";
	}

	public static string AppNameKey(string appName)
	{
		return $"{"dxc:"}{Fnv1a(appName):x8}";
	}

	private static IEnumerable<AgsRule> AgsRules()
	{
		return (RulesFile.Current.Rules.Any((PlanRule r) => r.Ags != null) ? RulesFile.Current : RulesFile.Embedded).Rules.Select((PlanRule r) => r.Ags).OfType<AgsRule>();
	}

	public static string AgsKey(string exeFileName, string appName)
	{
		return (from k in (from r in AgsRules()
				select r.Profiles?.GetValueOrDefault(appName)).OfType<string>()
			select "dxc:" + k.ToLowerInvariant()).FirstOrDefault() ?? ProfileKey(exeFileName) ?? AppNameKey(appName);
	}

	public static bool ProvenAgsApp(string appName)
	{
		return AgsRules().Any((AgsRule r) =>
		{
			string[]? proven = r.Proven;
			return proven != null && proven.Contains(appName, StringComparer.Ordinal);
		});
	}

	public static string? ProfileKey(string exeFileName)
	{
		string fileName = Path.GetFileName(exeFileName);
		(string, bool, uint)[] profiles = Profiles;
		for (int i = 0; i < profiles.Length; i++)
		{
			var (value, flag, value2) = profiles[i];
			if (flag ? fileName.Contains(value, StringComparison.OrdinalIgnoreCase) : fileName.Equals(value, StringComparison.OrdinalIgnoreCase))
			{
				return $"{"dxc:"}{value2:x8}";
			}
		}
		return null;
	}

	public static string HintKey(string exeFileName)
	{
		return ProfileKey(exeFileName) ?? DxcKey(exeFileName);
	}

	public static bool? IsNameHashed(IEnumerable<string> learnedKeys, string exeFileName)
	{
		List<string> list = learnedKeys.Where((string k) => k.StartsWith("dxc:", StringComparison.OrdinalIgnoreCase)).ToList();
		if (list.Count != 0)
		{
			return list.Contains(DxcKey(exeFileName), StringComparer.OrdinalIgnoreCase);
		}
		return null;
	}

	public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys)
	{
		HashSet<string> set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (set.Count != 0)
		{
			return (from f in All()
				where set.Contains(f.Key)
				select new FileInfo(f.Path)).ToList();
		}
		return new List<FileInfo>();
	}

	public long SizeOf(IEnumerable<string> keys)
	{
		return FilesOf(keys).Sum((FileInfo f) => (!f.Exists) ? 0 : f.Length);
	}

	public IReadOnlySet<string> KeysOpenBy(string exeFileName)
	{
		return AppCacheFiles.KeysOpenBy(exeFileName, All());
	}

	public int Delete(IEnumerable<string> keys)
	{
		return AppCacheFiles.DeleteAll(FilesOf(keys));
	}

	public IReadOnlySet<string> AllKeys()
	{
		return (from f in All()
			select f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	public HashSet<string> D3D12FileNames(IEnumerable<string> keys)
	{
		HashSet<string> set = keys.Where((string k) => k.StartsWith("dxc:", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (set.Count != 0 && AppCacheFiles.Readable(DxcDir))
		{
			return (from f in Directory.EnumerateFiles(DxcDir, "*.parc")
				select Path.GetFileName(f)).Where((string n) =>
			{
				string text = Key(n, d3d12: true);
				return text != null && set.Contains(text);
			}).ToHashSet(StringComparer.OrdinalIgnoreCase);
		}
		return new HashSet<string>();
	}

	public IReadOnlyList<string> Missing(IEnumerable<string> fileNames)
	{
		return fileNames.Where((string n) => !File.Exists(Path.Combine(DxcDir, n))).ToList();
	}

	public long DxcBytes()
	{
		return AmdBackend.Bytes(DxcDir);
	}

	public bool Full()
	{
		return DxcBytes() >= 15461882266L;
	}

	public static string? QueueWarning(long used, IReadOnlyList<(string Game, long Bytes)> queue, long cap = 17179869184L)
	{
		List<(string, long)> source = queue.Where(((string Game, long Bytes) q) => q.Bytes > 0).ToList();
		long num = source.Sum(((string Game, long Bytes) q) => q.Bytes);
		long num2 = Math.Max(0L, cap - used);
		if (num <= num2)
		{
			return null;
		}
		return $"This queue adds about {Format.Bytes(num)} to the shader cache ({string.Join(", ", source.Select(((string Game, long Bytes) q) => q.Game + " " + Format.Bytes(q.Bytes)))}), {Format.Bytes(num - num2)} more than the {Format.Bytes(num2)} free under the AMD driver's fixed {Format.Bytes(cap)} limit. " + "The driver then trims the least recently used caches, older games' included, and those games stutter until compiled again.";
	}

	private IEnumerable<(string Path, string Key)> All()
	{
		(string, bool)[] array = new (string, bool)[2]
		{
			(DxcDir, true),
			(DxDir, false)
		};
		for (int i = 0; i < array.Length; i++)
		{
			var (text, d3d12) = array[i];
			if (!AppCacheFiles.Readable(text))
			{
				continue;
			}
			foreach (string item in Directory.EnumerateFiles(text, "*.parc"))
			{
				string text2 = Key(Path.GetFileName(item), d3d12);
				if (text2 != null)
				{
					yield return (Path: item, Key: text2);
				}
			}
		}
	}
}
