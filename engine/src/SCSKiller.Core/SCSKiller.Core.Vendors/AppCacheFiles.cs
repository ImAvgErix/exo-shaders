using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Games;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.Vendors;

public static class AppCacheFiles
{
	private struct IoStatusBlock
	{
		public nint Status;

		public nint Information;
	}

	internal static Func<List<(int Pid, int Parent, string Exe)>> Processes { get; set; } = ProcessTree.Snapshot;

	public static IReadOnlySet<string> KeysOpenBy(string exeFileName, IEnumerable<(string Path, string Key)> files)
	{
		HashSet<nuint> hashSet = Processes().Where(((int Pid, int Parent, string Exe) p) => p.Exe.Equals(exeFileName, StringComparison.OrdinalIgnoreCase)).Select(((int Pid, int Parent, string Exe) p) => (nuint)p.Pid).ToHashSet();
		HashSet<string> hashSet2 = new HashSet<string>();
		if (hashSet.Count == 0)
		{
			return hashSet2;
		}
		foreach (var (path, item) in files)
		{
			if (!hashSet2.Contains(item) && ProcessesUsing(path).Overlaps(hashSet))
			{
				hashSet2.Add(item);
			}
		}
		return hashSet2;
	}

	public static int DeleteAll(IReadOnlyList<FileInfo> files)
	{
		List<FileStream> list = new List<FileStream>();
		try
		{
			foreach (FileInfo file in files)
			{
				try
				{
					list.Add(new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Delete));
				}
				catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
					List<string> list2 = ProcessesUsing(file.FullName).Select(Describe).ToList();
					throw new InvalidOperationException($"files in use by {((list2.Count > 0) ? string.Join(", ", list2) : "another process")} ({file.Name})", ex);
				}
			}
			foreach (FileInfo file2 in files)
			{
				if (file2.IsReadOnly)
				{
					file2.IsReadOnly = false;
				}
			}
			foreach (FileInfo file3 in files)
			{
				file3.Delete();
			}
			return files.Count;
		}
		finally
		{
			foreach (FileStream item in list)
			{
				item.Dispose();
			}
		}
	}

	public static bool Readable(string dir)
	{
		if (Directory.Exists(dir))
		{
			return GameFiles.FinalPath(dir) != null;
		}
		return false;
	}

	public static string? MissingTarget(IAppCache? cache)
	{
		string[] source;
		if (cache is NvidiaAppCache nvidiaAppCache)
		{
			source = new string[1] { nvidiaAppCache.Dir };
		}
		else
		{
			source = ((!(cache is AmdAppCache amdAppCache)) ? Array.Empty<string>() : new string[2] { amdAppCache.DxcDir, amdAppCache.DxDir });
		}
		return source.FirstOrDefault((string d) => Directory.Exists(d) && !Readable(d));
	}

	private static string Describe(nuint pid)
	{
		string item = ProcessTree.Snapshot().FirstOrDefault(((int Pid, int Parent, string Exe) p) => (nuint)p.Pid == pid).Exe;
		if (item != null)
		{
			return $"{item} (pid {pid})";
		}
		return $"pid {pid}";
	}

	public static HashSet<nuint> ProcessesUsing(string path)
	{
		HashSet<nuint> hashSet = new HashSet<nuint>();
		using SafeFileHandle safeFileHandle = CreateFileW(path, 128u, 7u, 0, 3u, 0u, 0);
		if (safeFileHandle.IsInvalid)
		{
			return hashSet;
		}
		nuint[] array = new nuint[64];
		int num;
		while (true)
		{
			num = NtQueryInformationFile(safeFileHandle, out var _, array, array.Length * IntPtr.Size, 47);
			if (num != -1073741820 || array.Length >= 65536)
			{
				break;
			}
			array = new nuint[array.Length * 4];
		}
		if (num < 0)
		{
			return hashSet;
		}
		for (int i = 0; i < (int)array[0]; i++)
		{
			hashSet.Add(array[1 + i]);
		}
		return hashSet;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

	[DllImport("ntdll.dll")]
	private static extern int NtQueryInformationFile(SafeFileHandle h, out IoStatusBlock iosb, [Out] nuint[] info, int length, int infoClass);
}
