using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace SCSKiller.Core.Warming;

public sealed class WarmJob : IDisposable
{
	private struct ExtendedLimitInformation
	{
		public long PerProcessUserTimeLimit;

		public long PerJobUserTimeLimit;

		public uint LimitFlags;

		public nuint MinimumWorkingSetSize;

		public nuint MaximumWorkingSetSize;

		public uint ActiveProcessLimit;

		public nuint Affinity;

		public uint PriorityClass;

		public uint SchedulingClass;

		public ulong ReadOperationCount;

		public ulong WriteOperationCount;

		public ulong OtherOperationCount;

		public ulong ReadTransferCount;

		public ulong WriteTransferCount;

		public ulong OtherTransferCount;

		public nuint ProcessMemoryLimit;

		public nuint JobMemoryLimit;

		public nuint PeakProcessMemoryUsed;

		public nuint PeakJobMemoryUsed;
	}

	private readonly nint _job;

	public static WarmJob Current { get; } = new WarmJob();

	public WarmJob()
	{
		_job = CreateJobObjectW(0, null);
		if (_job == 0)
		{
			throw new Win32Exception();
		}
		ExtendedLimitInformation info = new ExtendedLimitInformation
		{
			LimitFlags = 8192u
		};
		if (!SetInformationJobObject(_job, 9, ref info, Marshal.SizeOf<ExtendedLimitInformation>()))
		{
			throw new Win32Exception();
		}
	}

	public void Add(int pid)
	{
		if (!Assign(pid) && !Contains(pid) && Running(pid))
		{
			throw new Win32Exception();
		}
		for (int i = 0; i < 5; i++)
		{
			if (ProcessTree.WithDescendants(pid).Skip(1).Count(Assign) <= 0)
			{
				break;
			}
		}
	}

	private bool Assign(int pid)
	{
		nint num = OpenProcess(4353u, inherit: false, pid);
		if (num == 0)
		{
			return false;
		}
		try
		{
			bool result;
			return !(IsProcessInJob(num, _job, out result) & result) && AssignProcessToJobObject(_job, num);
		}
		finally
		{
			CloseHandle(num);
		}
	}

	private static bool Running(int pid)
	{
		nint num = OpenProcess(4096u, inherit: false, pid);
		if (num == 0)
		{
			return false;
		}
		try
		{
			uint code;
			return GetExitCodeProcess(num, out code) && code == 259;
		}
		finally
		{
			CloseHandle(num);
		}
	}

	public bool Contains(int pid)
	{
		nint num = OpenProcess(5120u, inherit: false, pid);
		if (num == 0)
		{
			return false;
		}
		try
		{
			bool result;
			return IsProcessInJob(num, _job, out result) & result;
		}
		finally
		{
			CloseHandle(num);
		}
	}

	public void Dispose()
	{
		CloseHandle(_job);
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint CreateJobObjectW(nint security, string? name);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, int length);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AssignProcessToJobObject(nint job, nint process);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool IsProcessInJob(nint process, nint job, out bool result);

	[DllImport("kernel32.dll")]
	private static extern nint OpenProcess(uint access, bool inherit, int pid);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);

	[DllImport("kernel32.dll")]
	private static extern bool GetExitCodeProcess(nint h, out uint code);
}
