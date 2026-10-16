using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SCSKiller.Core.Vendors;

public sealed class NvidiaAppCache(string dir) : IAppCache
{
	public string Dir { get; } = dir;

	public static string? Key(string fileName)
	{
		if (fileName.Length != 21 || !fileName.EndsWith(".nvph", StringComparison.OrdinalIgnoreCase) || !ulong.TryParse(fileName.AsSpan(0, 16), NumberStyles.HexNumber, null, out var _))
		{
			return null;
		}
		return fileName.Substring(8, 8).ToLowerInvariant();
	}

	public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys)
	{
		HashSet<string> set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (set.Count != 0 && AppCacheFiles.Readable(Dir))
		{
			return new DirectoryInfo(Dir).EnumerateFiles("*.nvph").Where((FileInfo f) =>
			{
				string text = Key(f.Name);
				return text != null && set.Contains(text);
			}).ToList();
		}
		return new List<FileInfo>();
	}

	public long SizeOf(IEnumerable<string> keys)
	{
		return FilesOf(keys).Sum((FileInfo f) => f.Length);
	}

	public IReadOnlySet<string> KeysOpenBy(string exeFileName)
	{
		if (AppCacheFiles.Readable(Dir))
		{
			return AppCacheFiles.KeysOpenBy(exeFileName, from f in Directory.EnumerateFiles(Dir, "*.nvph")
				select (Path: f, Key: Key(Path.GetFileName(f))) into f
				where f.Key != null
				select f);
		}
		return new HashSet<string>();
	}

	public int Delete(IEnumerable<string> keys)
	{
		return AppCacheFiles.DeleteAll(FilesOf(keys));
	}

	public IReadOnlySet<string> AllKeys()
	{
		if (Directory.Exists(Dir))
		{
			return (from f in Directory.EnumerateFiles(Dir, "*.nvph")
				select Key(Path.GetFileName(f))).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
		}
		return new HashSet<string>();
	}
}
