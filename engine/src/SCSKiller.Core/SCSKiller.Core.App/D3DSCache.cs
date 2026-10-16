using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.App;

public static class D3DSCache
{
	private static readonly ConcurrentDictionary<string, (string Stamp, HashSet<string> Paths)> DbPaths = new ConcurrentDictionary<string, (string, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);

	private static readonly Regex WalExe = new Regex("[A-Za-z]:\\\\[^\\x00-\\x1F\\x7F\ufffd]{3,}?\\.[Ee][Xx][Ee]");

	private static readonly Regex CacheFile = new Regex("^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}(_VEN_[0-9A-F]+&DEV_[0-9A-F]+&SUBSYS_[0-9A-F]+&REV_[0-9A-F]+)?\\.(dxcache(-shm|-wal)?|idx|val|lock)$", RegexOptions.IgnoreCase);

	public static IReadOnlyList<string> FoldersOf(string root, Game g)
	{
		List<string> list = new List<string>();
		if (!Directory.Exists(root))
		{
			return list;
		}
		foreach (string item in Directory.EnumerateDirectories(root))
		{
			if (IsGames(item, g) == true)
			{
				list.Add(item);
			}
		}
		return list;
	}

	public static bool? IsGames(string dir, Game g)
	{
		try
		{
			FileInfo[] files = new DirectoryInfo(dir).GetFiles();
			List<FileInfo> source = files.Where((FileInfo f) => f.Name.EndsWith(".dxcache", StringComparison.OrdinalIgnoreCase)).ToList();
			string text = string.Join('|', source.Select((FileInfo f) => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));
			if (!DbPaths.TryGetValue(dir, out (string, HashSet<string>) value) || value.Item1 != text)
			{
				HashSet<string> read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				if (!source.All((FileInfo f) => ReadAppIds(f.FullName, read)))
				{
					return null;
				}
				value = (DbPaths[dir] = (text, read));
			}
			if (!value.Item2.All((string p) => IsGameExe(p, g)))
			{
				return false;
			}
			HashSet<string> hashSet = new HashSet<string>(value.Item2, StringComparer.OrdinalIgnoreCase);
			foreach (FileInfo item in files.Where((FileInfo f) => f.Name.EndsWith(".dxcache-wal", StringComparison.OrdinalIgnoreCase)))
			{
				ReadWal(item.FullName, hashSet);
			}
			return (hashSet.Count == 0) ? ((bool?)null) : new bool?(hashSet.All((string p) => IsGameExe(p, g)));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public static IReadOnlyList<string> Made(string root, string dir, DateTime since)
	{
		string under = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar;
		List<string> list = new List<string>();
		if (!Directory.Exists(root))
		{
			return list;
		}
		foreach (DirectoryInfo item in from d in new DirectoryInfo(root).EnumerateDirectories()
			where d.CreationTimeUtc >= since.ToUniversalTime().AddSeconds(-2.0)
			select d)
		{
			try
			{
				HashSet<string> hashSet = ExePaths(item.FullName);
				if (hashSet != null && hashSet.Count > 0 && hashSet.All((string p) => p.StartsWith(under, StringComparison.OrdinalIgnoreCase)))
				{
					list.Add(item.FullName);
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
		}
		return list;
	}

	public static HashSet<string>? ExePaths(string dir)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string item in Directory.EnumerateFiles(dir))
		{
			if (item.EndsWith(".dxcache", StringComparison.OrdinalIgnoreCase))
			{
				if (!ReadAppIds(item, hashSet))
				{
					return null;
				}
			}
			else if (item.EndsWith(".dxcache-wal", StringComparison.OrdinalIgnoreCase))
			{
				ReadWal(item, hashSet);
			}
		}
		return hashSet;
	}

	private static void ReadWal(string file, HashSet<string> paths)
	{
		using FileStream fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		byte[] array = new byte[fileStream.Length];
		fileStream.ReadExactly(array);
		foreach (Match item in WalExe.Matches(Encoding.UTF8.GetString(array)))
		{
			paths.Add(item.Value);
		}
	}

	public static IEnumerable<FileInfo> CacheFiles(string dir)
	{
		return from f in new DirectoryInfo(dir).EnumerateFiles()
			where CacheFile.IsMatch(f.Name)
			select f;
	}

	public static bool IsGameExe(string path, Game g)
	{
		if (path.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (g.Store != Store.Xbox || !g.Id.StartsWith("xbox:", StringComparison.Ordinal))
		{
			return false;
		}
		string[] array = path.Split('\\');
		int num = Array.FindIndex(array, (string p) => p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
		if (num < 0 || num + 2 >= array.Length)
		{
			return false;
		}
		string[] array2 = array[num + 1].Split('_');
		if (array2.Length == 5 && (array2[0] + "_" + array2[4]).Equals(g.Id.Substring("xbox:".Length), StringComparison.OrdinalIgnoreCase))
		{
			return string.Join('\\', array[(num + 2)..]).Equals(Path.GetRelativePath(g.InstallDir, g.ExePath), StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static bool ReadAppIds(string file, HashSet<string> paths)
	{
		int num = sqlite3_open_v2("file:" + file.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23")
			.Replace("?", "%3f") + "?immutable=1", out var db, 65, 0);
		try
		{
			if (num != 0)
			{
				return false;
			}
			long tables = 0L;
			if (!Rows(db, "select count(*) from sqlite_master where type='table' and name='app_id'", (nint st) =>
			{
				tables = sqlite3_column_int64(st, 0);
			}))
			{
				return false;
			}
			return tables == 0L || Rows(db, "select exe_path from app_id", (nint st) =>
			{
				string text = Marshal.PtrToStringUTF8(sqlite3_column_text(st, 0));
				if (text != null && text.Length > 0)
				{
					paths.Add(text);
				}
			});
		}
		finally
		{
			sqlite3_close_v2(db);
		}
	}

	private static bool Rows(nint db, string sql, Action<nint> row)
	{
		if (sqlite3_prepare_v2(db, sql, -1, out var stmt, 0) != 0)
		{
			return false;
		}
		try
		{
			int num;
			while ((num = sqlite3_step(stmt)) == 100)
			{
				row(stmt);
			}
			return num == 101;
		}
		finally
		{
			sqlite3_finalize(stmt);
		}
	}

	[DllImport("winsqlite3.dll")]
	private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out nint db, int flags, nint vfs);

	[DllImport("winsqlite3.dll")]
	private static extern int sqlite3_close_v2(nint db);

	[DllImport("winsqlite3.dll")]
	private static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint stmt, nint tail);

	[DllImport("winsqlite3.dll")]
	private static extern int sqlite3_step(nint stmt);

	[DllImport("winsqlite3.dll")]
	private static extern nint sqlite3_column_text(nint stmt, int col);

	[DllImport("winsqlite3.dll")]
	private static extern long sqlite3_column_int64(nint stmt, int col);

	[DllImport("winsqlite3.dll")]
	private static extern int sqlite3_finalize(nint stmt);
}
