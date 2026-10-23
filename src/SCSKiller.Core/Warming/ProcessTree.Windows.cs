using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SCSKiller.Core.Warming;

static partial class ProcessTree
{
    /// <summary>A suspended tree also gives its memory back (K32EmptyWorkingSet): a paused compile, or one paused while a game
    /// plays, held 7-11 GB (Marvel Rivals: 4 GB of its own and the driver cache's pages it had written) and the game ran short.
    /// Resumed, it faults back the pages it touches.</summary>
    public static partial void Suspend(int rootPid, bool suspend)
    {
        foreach (var pid in WithDescendants(rootPid))
        {
            var h = OpenProcess(0x0800 /* PROCESS_SUSPEND_RESUME */ | (suspend ? TrimAccess : 0), false, pid);
            if (h == 0 && suspend) h = OpenProcess(0x0800, false, pid);
            if (h == 0) continue;   // exited meanwhile
            if (suspend) { _ = NtSuspendProcess(h); _ = K32EmptyWorkingSet(h); }
            else _ = NtResumeProcess(h);
            CloseHandle(h);
        }
    }

    const uint TrimAccess = 0x0400 /* PROCESS_QUERY_INFORMATION */ | 0x0100 /* PROCESS_SET_QUOTA */;

    /// <summary>The tree's resident memory, and what of it isn't its own (pages of files it maps: the driver's shader cache).</summary>
    public static (long WorkingSet, long Mapped) Resident(int rootPid)
    {
        long ws = 0, mapped = 0;
        foreach (var pid in WithDescendants(rootPid))
        {
            var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == 0) continue;
            var c = new MemoryCounters { Cb = Marshal.SizeOf<MemoryCounters>() };
            if (K32GetProcessMemoryInfo(h, ref c, c.Cb)) { ws += (long)c.WorkingSetSize; mapped += Math.Max(0, (long)c.WorkingSetSize - (long)c.PagefileUsage); }
            CloseHandle(h);
        }
        return (ws, mapped);
    }

    /// <summary>Holds each process of the tree to its own committed memory plus <paramref name="headroom"/> (a hard working-set
    /// maximum: Windows trims its least recently used pages to stay under it), or lifts that limit (null). False when Windows
    /// refused every process the limit.</summary>
    public static bool Cap(int rootPid, long? headroom)
    {
        var any = false;
        foreach (var pid in WithDescendants(rootPid))
        {
            var h = OpenProcess(TrimAccess, false, pid);
            if (h == 0) continue;
            var c = new MemoryCounters { Cb = Marshal.SizeOf<MemoryCounters>() };
            if (K32GetProcessMemoryInfo(h, ref c, c.Cb))
            {
                var max = (long)c.PagefileUsage + (headroom ?? 0) + (64L << 20);
                any |= SetProcessWorkingSetSizeEx(h, 4 << 20, (nint)max, headroom != null ? 0x4u /* QUOTA_LIMITS_HARDWS_MAX_ENABLE */ | 0x2u /* MIN_DISABLE */
                    : 0x8u /* QUOTA_LIMITS_HARDWS_MAX_DISABLE */ | 0x2u);
            }
            CloseHandle(h);
        }
        return any;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryCounters
    {
        public int Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
            QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("kernel32.dll")] static extern bool K32EmptyWorkingSet(nint h);
    [DllImport("kernel32.dll")] static extern bool K32GetProcessMemoryInfo(nint h, ref MemoryCounters c, int cb);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSizeEx(nint h, nint min, nint max, uint flags);

    public static partial List<(int Pid, int Parent, string Exe)> Snapshot()
    {
        var all = new List<(int, int, string)>();
        var snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == -1) throw new Win32Exception();
        try
        {
            var e = new ProcessEntry32 { Size = Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e)) all.Add((e.Pid, e.ParentPid, e.ExeFile));
        }
        finally { CloseHandle(snap); }
        return all;
    }

    // NtQuerySystemInformation(SystemProcessIdInformation) gives its NT path: null when no drive letter maps its volume
    public static partial string? ImagePath(int pid)
    {
        unsafe
        {
            const int SystemProcessIdInformation = 88;
            var name = new char[32768];
            fixed (char* buffer = name)
            {
                // SYSTEM_PROCESS_ID_INFORMATION (x64): ProcessId, then UNICODE_STRING { u16 Length, u16 MaximumLength, PWSTR Buffer at 8 }
                var info = stackalloc byte[24];
                *(nint*)info = pid;
                *(ushort*)(info + 8) = 0;
                *(ushort*)(info + 10) = (ushort)(name.Length * 2 - 2);
                *(char**)(info + 16) = buffer;
                if (NtQuerySystemInformation(SystemProcessIdInformation, info, 24, out _) < 0) return null;
                return DosPath(new string(buffer, 0, *(ushort*)(info + 8) / 2), DosDevices());
            }
        }
    }

    static IEnumerable<(string Drive, string Device)> DosDevices()
    {
        var target = new char[1024];
        foreach (var root in Environment.GetLogicalDrives())
        {
            var drive = root.TrimEnd(Path.DirectorySeparatorChar);
            if (QueryDosDeviceW(drive, target, target.Length) > 0) yield return (drive, new string(target).Split((char)0)[0]);
        }
    }

    // FILETIME
    internal static partial long? Created(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try { return GetProcessTimes(h, out var created, out _, out _, out _) ? created : null; }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public int Size, Usage, Pid;
        public nint DefaultHeapId;
        public int ModuleId, Threads, ParentPid, PriClassBase, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(nint snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(nint snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("ntdll.dll")] static extern unsafe int NtQuerySystemInformation(int infoClass, void* info, int length, out int returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int QueryDosDeviceW(string device, [Out] char[] target, int max);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(nint h, out long created, out long exited, out long kernel, out long user);
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(nint h);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(nint h);
}
