using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.Vendors;

/// <summary>What the <see cref="IAppCache"/> implementations share: which processes have a file open, and an
/// all-or-nothing delete. Only file attributes and handles are touched, never content.</summary>
public static class AppCacheFiles
{
    /// <summary>Keys of <paramref name="files"/> (path, key) opened by a running process with this exe file name (from the
    /// process snapshot: none opened).</summary>
    public static IReadOnlySet<string> KeysOpenBy(string exeFileName, IEnumerable<(string Path, string Key)> files)
    {
        var pids = Processes().Where(p => p.Exe.Equals(exeFileName, StringComparison.OrdinalIgnoreCase))
            .Select(p => (nuint)p.Pid).ToHashSet();
        var keys = new HashSet<string>();
        if (pids.Count == 0) return keys;
        foreach (var (path, key) in files)
            if (!keys.Contains(key) && ProcessesUsing(path).Overlaps(pids)) keys.Add(key);
        return keys;
    }

    /// <summary>The processes whose open files count. Tests: their own process tree, so another test run on the PC with
    /// the same process names never counts.</summary>
    internal static Func<List<(int Pid, int Parent, string Exe)>> Processes { get; set; } = Warming.ProcessTree.Snapshot;

    /// <summary>Deletes all <paramref name="files"/> or none: InvalidOperationException ("files in use by ...") when one
    /// is open (the driver holds a running app's files). All files are opened exclusively first, so nothing can open them
    /// between the check and the delete. Returns how many were deleted. Steam's own shader cache files (<see cref="SteamOwn"/>)
    /// are refused, all of them kept, unless <paramref name="steams"/> names them.</summary>
    public static int DeleteAll(IReadOnlyList<FileInfo> files, IReadOnlySet<string>? steams = null)
    {
        if (files.FirstOrDefault(f => SteamOwn(f.Name) && steams?.Contains(f.FullName) != true) is { } steam)
            throw new InvalidOperationException($"{steam.Name} is Steam's shader cache, not deleted");
        var held = new List<FileStream>();
        try
        {
            foreach (var f in files)
                try { held.Add(new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Delete)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    var users = ProcessesUsing(f.FullName).Select(Describe).ToList();
                    throw new InvalidOperationException(users.Count > 0 ? $"files in use by {string.Join(", ", users)} ({f.Name})" : Unheld(f, e), e);
                }
            foreach (var f in files) if (f.IsReadOnly) f.IsReadOnly = false;   // Delete refuses a read-only file: none is deleted before all can be
            foreach (var f in files) f.Delete();   // gone once our handles close
            return files.Count;
        }
        finally { foreach (var h in held) h.Dispose(); }
    }

    /// <summary>A file Steam's shader pre-caching writes in the driver's cache folder, not the driver's.</summary>
    internal static bool SteamOwn(string fileName) =>
        fileName.StartsWith("steam_shader_cache", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("steamapp_merged_shader_cache", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a file no process holds open couldn't be opened, with the error's code; AMD's cache folders say how
    /// AMD Software removes it.</summary>
    static string Unheld(FileInfo f, Exception e) =>
        $"{(e is UnauthorizedAccessException ? "access denied to" : "no process found holding")} {f.Name} (0x{e.HResult:X8})"
        + (f.Directory is { Name: "DxcCache" or "DxCache", Parent.Name: "AMD" } ? ". Reset Shader Cache in AMD Software removes it" : "");

    /// <summary>The folder exists and its files can be listed: false for a link (a junction to another drive) whose target is
    /// gone, which Directory.Exists still reports as there.</summary>
    public static bool Readable(string dir) => Directory.Exists(dir) && Games.GameFiles.FinalPath(dir) != null;

    /// <summary>A cache folder of <paramref name="cache"/> that is a link whose target is missing; null when none is.</summary>
    public static string? MissingTarget(IAppCache? cache) =>
        (cache switch { NvidiaAppCache nv => [nv.Dir], AmdAppCache amd => new[] { amd.DxcDir, amd.DxDir }, _ => [] })
            .FirstOrDefault(d => Directory.Exists(d) && !Readable(d));

    static string Describe(nuint pid) =>
        Warming.ProcessTree.Snapshot().FirstOrDefault(p => (nuint)p.Pid == pid).Exe is { } exe ? $"{exe} (pid {pid})" : $"pid {pid}";   // exited meanwhile

    public static HashSet<nuint> ProcessesUsing(string path)
    {
        var pids = new HashSet<nuint>();
        using var h = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7 /* share all */, 0, 3 /* OPEN_EXISTING */, 0, 0);
        if (h.IsInvalid) return pids;
        var buf = new nuint[64];   // FILE_PROCESS_IDS_USING_FILE_INFORMATION: count, pad, pids
        for (int status; ; buf = new nuint[buf.Length * 4])
        {
            status = NtQueryInformationFile(h, out _, buf, buf.Length * nint.Size, 47 /* FileProcessIdsUsingFileInformation */);
            if (status == unchecked((int)0xC0000004) /* STATUS_INFO_LENGTH_MISMATCH */ && buf.Length < 1 << 16) continue;
            if (status < 0) return pids;
            break;
        }
        for (int i = 0; i < (int)(uint)buf[0]; i++) pids.Add(buf[1 + i]);
        return pids;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationFile(SafeFileHandle h, out IoStatusBlock iosb, [Out] nuint[] info, int length, int infoClass);
    struct IoStatusBlock { public nint Status, Information; }
}
