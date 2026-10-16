using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace SCSKiller.Core.Warming;

internal static class ProcessTree
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ProcessEntry32
	{
		public int Size;

		public int Usage;

		public int Pid;

		public nint DefaultHeapId;

		public int ModuleId;

		public int Threads;

		public int ParentPid;

		public int PriClassBase;

		public int Flags;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string ExeFile;
	}

	private static readonly Lock SnapshotLock = new Lock();

	private static List<(int, int, string)>? _snapshot;

	private static long _snapshotAt;

	public static void Suspend(int rootPid, bool suspend)
	{
		foreach (int item in WithDescendants(rootPid))
		{
			nint num = OpenProcess(2048u, inherit: false, item);
			if (num != 0)
			{
				if (!suspend)
				{
					NtResumeProcess(num);
				}
				else
				{
					NtSuspendProcess(num);
				}
				CloseHandle(num);
			}
		}
	}

	public static List<(int Pid, int Parent, string Exe)> Snapshot()
	{
		List<(int, int, string)> list = new List<(int, int, string)>();
		nint num = CreateToolhelp32Snapshot(2u, 0);
		if (num == -1)
		{
			throw new Win32Exception();
		}
		try
		{
			ProcessEntry32 e = new ProcessEntry32
			{
				Size = Marshal.SizeOf<ProcessEntry32>()
			};
			bool flag = Process32FirstW(num, ref e);
			while (flag)
			{
				list.Add((e.Pid, e.ParentPid, e.ExeFile));
				flag = Process32NextW(num, ref e);
			}
			return list;
		}
		finally
		{
			CloseHandle(num);
		}
	}

	public static List<(int Pid, int Parent, string Exe)> RecentSnapshot()
	{
		using (SnapshotLock.EnterScope())
		{
			if (_snapshot == null || Environment.TickCount64 - _snapshotAt > 1000)
			{
				List<(int Pid, int Parent, string Exe)> snapshot = Snapshot();
				long tickCount = Environment.TickCount64;
				_snapshot = snapshot;
				_snapshotAt = tickCount;
			}
			return _snapshot;
		}
	}

	internal static List<int> WithDescendants(int root)
	{
		List<(int, int, string)> list = Snapshot();
		HashSet<int> linked = new HashSet<int> { root };
		bool flag = true;
		while (flag)
		{
			flag = false;
			foreach (var item in list)
			{
				flag |= linked.Contains(item.Item2) && linked.Add(item.Item1);
			}
		}
		return Tree(root, list.Where(((int Pid, int Parent, string Exe) p) => linked.Contains(p.Pid)).Select(((int Pid, int Parent, string Exe) p) => (Pid: p.Pid, Parent: p.Parent, Created(p.Pid))));
	}

	internal static List<int> Tree(int root, IEnumerable<(int Pid, int Parent, long? Created)> procs)
	{
		List<(int, int, long?)> list = procs.ToList();
		long? item = list.FirstOrDefault(((int Pid, int Parent, long? Created) p) => p.Pid == root).Item3;
		if (item.HasValue)
		{
			long valueOrDefault = item.GetValueOrDefault();
			List<(int, long)> list2 = new List<(int, long)> { (root, valueOrDefault) };
			HashSet<int> hashSet = new HashSet<int> { root };
			for (int num = 0; num < list2.Count; num++)
			{
				foreach (var item2 in list)
				{
					if (item2.Item2 != list2[num].Item1)
					{
						continue;
					}
					item = item2.Item3;
					if (item.HasValue)
					{
						long valueOrDefault2 = item.GetValueOrDefault();
						if (valueOrDefault2 >= list2[num].Item2 && hashSet.Add(item2.Item1))
						{
							list2.Add((item2.Item1, valueOrDefault2));
						}
					}
				}
			}
			return list2.Select(((int Pid, long Created) t) => t.Pid).ToList();
		}
		int num2 = 1;
		List<int> list3 = new List<int>(num2);
		CollectionsMarshal.SetCount(list3, num2);
		CollectionsMarshal.AsSpan(list3)[0] = root;
		return list3;
	}

	public unsafe static string? ImagePath(int pid)
	{
		char[] array = new char[32768];
		fixed (char* ptr = array)
		{
			byte* ptr2 = stackalloc byte[24];
			*(IntPtr*)ptr2 = (IntPtr)pid;
			((short*)ptr2)[4] = 0;
			((short*)ptr2)[5] = (short)(ushort)(array.Length * 2 - 2);
			*(char**)(ptr2 + 16) = ptr;
			if (NtQuerySystemInformation(88, ptr2, 24, out var _) < 0)
			{
				return null;
			}
			return DosPath(new string(ptr, 0, ((ushort*)ptr2)[4] / 2), DosDevices());
		}
	}

	internal static string? DosPath(string nt, IEnumerable<(string Drive, string Device)> devices)
	{
		if (nt.Length > 7 && nt.StartsWith("\\??\\", StringComparison.Ordinal) && char.IsAsciiLetter(nt[4]) && nt[5] == ':' && nt[6] == '\\')
		{
			return nt.Substring(4);
		}
		foreach (var (text, text2) in devices)
		{
			if (nt.StartsWith(text2 + "\\", StringComparison.OrdinalIgnoreCase))
			{
				return text + nt.Substring(text2.Length);
			}
		}
		return null;
	}

	private static IEnumerable<(string Drive, string Device)> DosDevices()
	{
		char[] target = new char[1024];
		string[] logicalDrives = Environment.GetLogicalDrives();
		for (int i = 0; i < logicalDrives.Length; i++)
		{
			string text = logicalDrives[i].TrimEnd(Path.DirectorySeparatorChar);
			if (QueryDosDeviceW(text, target, target.Length) > 0)
			{
				yield return (Drive: text, Device: new string(target).Split('\0')[0]);
			}
		}
	}

	private static long? Created(int pid)
	{
		nint num = OpenProcess(4096u, inherit: false, pid);
		if (num == 0)
		{
			return null;
		}
		try
		{
			long created;
			long exited;
			long kernel;
			long user;
			return GetProcessTimes(num, out created, out exited, out kernel, out user) ? new long?(created) : ((long?)null);
		}
		finally
		{
			CloseHandle(num);
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint CreateToolhelp32Snapshot(uint flags, int pid);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern bool Process32FirstW(nint snap, ref ProcessEntry32 e);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern bool Process32NextW(nint snap, ref ProcessEntry32 e);

	[DllImport("kernel32.dll")]
	private static extern nint OpenProcess(uint access, bool inherit, int pid);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);

	[DllImport("ntdll.dll")]
	private unsafe static extern int NtQuerySystemInformation(int infoClass, void* info, int length, out int returned);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int QueryDosDeviceW(string device, char[] target, int max);

	[DllImport("kernel32.dll")]
	private static extern bool GetProcessTimes(nint h, out long created, out long exited, out long kernel, out long user);

	[DllImport("ntdll.dll")]
	private static extern int NtSuspendProcess(nint h);

	[DllImport("ntdll.dll")]
	private static extern int NtResumeProcess(nint h);
}
