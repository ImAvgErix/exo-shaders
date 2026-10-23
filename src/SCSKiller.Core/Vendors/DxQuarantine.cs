using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Vendors;

/// <summary>The DirectX 11 cache files of AMD's DxCache (*.parc) or, with <paramref name="nvidia"/>, NVIDIA's DXCache
/// (*.nvph) set aside for a before run whose game's key isn't known: the driver doesn't always hold that file open, so no
/// clear or check can find the game's old cache among the others. Every file of a key not kept is renamed into a folder
/// beside the cache (same volume: a rename, never a copy) and, once the run is over, put back, except the files of the key
/// the game made during the run, which are its old cache. A journal of the moves is written before the first one, so a run
/// cut off by a crash is put back at the next start. A file the run's own replaces is kept in before-run-caches beside the
/// journal, never deleted. A cache folder that is a link, or a journal entry outside the two folders, is never touched.
/// With <paramref name="glCache"/>, <paramref name="dxDir"/> is a game's Vulkan cache folder in Steam's shader cache, set
/// aside whole (<see cref="SetAsideTree"/>) and put back whole once nothing changed it.</summary>
public sealed class DxQuarantine(string dxDir, string journalPath, bool nvidia = false, bool glCache = false) : IBeforeRunAside
{
    /// <summary>A file renamed from <see cref="From"/> to <see cref="To"/>; a Vulkan cache file's <see cref="Length"/> and
    /// <see cref="Sha256"/> as it was set aside (-1 and null for a driver cache file).</summary>
    public sealed record Move(string From, string To, long Length = -1, string? Sha256 = null);
    /// <param name="Dir">a Vulkan cache folder set aside whole: the folder; null = a driver cache's files</param>
    /// <param name="Archived">the Vulkan files the run made were moved to before-run-caches: what's left is the put-back</param>
    public sealed record Journal(string GameId, List<Move> Moves, List<string> Skipped, string? Dir = null, bool Archived = false);

    /// <summary>How long Steam's merged cache files stay untouched before a put-back: Steam may still be writing them.</summary>
    static readonly TimeSpan MergeWait = TimeSpan.FromMinutes(2);

    public string Folder => Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dxDir))!, Path.GetFileName(Path.TrimEndingDirectorySeparator(dxDir)) + ".scskiller-quarantine");

    public string? PendingGame => Pending?.GameId;

    /// <summary>A driver cache's run end reads nothing of the cache to learn the game's key: it comes from the files the run
    /// made. A Vulkan cache folder's is read to archive what the run made.</summary>
    public bool AfterRunReads => glCache;

    /// <summary>A Vulkan cache folder's put-back waits while this is true (Steam processing shaders).</summary>
    public Func<bool>? Busy { get; init; }

    public Journal? Pending => Read(journalPath);

    /// <summary>Where files put back over are kept, one folder per restore.</summary>
    string KeepDir => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(journalPath))!, "before-run-caches");

    public static Journal? Read(string journalPath)
    {
        try { return File.Exists(journalPath) ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    Journal New(string gameId, List<Move> moves, List<string> skipped) => new(gameId, moves, skipped, glCache ? dxDir : null);

    /// <summary>The key of a cache file of this driver's; null for anything else.</summary>
    string? KeyOf(string fileName) => nvidia
        ? NvidiaAppCache.Key(fileName) is { } key && fileName.AsSpan(4, 4).Equals("a91d", StringComparison.OrdinalIgnoreCase) ? key : null
        : AmdAppCache.Key(fileName, d3d12: false);

    /// <summary>The cache folder or the set-aside folder is a junction or symlink, or, for a Vulkan cache, a folder in the set-aside one.</summary>
    bool Linked() => new[] { dxDir, Folder }.Any(d => Directory.Exists(d) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
        || glCache && Directory.Exists(Folder) && !NoLinks(new DirectoryInfo(Folder));

    static bool NoLinks(DirectoryInfo d)
    {
        try { return d.EnumerateDirectories().All(s => (s.Attributes & FileAttributes.ReparsePoint) == 0 && NoLinks(s)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The bytes a cache file holds: NVIDIA pre-sizes its files, its header has what's used.</summary>
    long Holds(string path) => (nvidia ? NvidiaAppCache.UsedBytes(path) : null) ?? new FileInfo(path).Length;

    /// <summary>Sets aside every cache file whose key isn't <paramref name="keep"/>'s, for <paramref name="gameId"/>'s run, a
    /// key's files together. A key with a file a process holds open stays and is listed as skipped, as is every file while a
    /// journal is pending or a folder is a link; a file that doesn't rename too. Returns the journal, written even with
    /// nothing moved (the run's end learns the key the game made from it), unless one was pending. While another process
    /// sets aside or restores, nothing moves and the cache folder is listed as skipped. A Vulkan cache folder is set aside
    /// whole (<paramref name="keep"/> unused), never while <paramref name="held"/>.</summary>
    public Journal SetAside(string gameId, Func<string, bool> keep, bool held = false)
    {
        using var locked = Lock();
        if (locked == null) return New(gameId, [], [dxDir]);
        return glCache ? SetAsideTree(gameId, held) : SetAsideKeys(gameId, keep);
    }

    Journal SetAsideKeys(string gameId, Func<string, bool> keep)
    {
        var plan = new List<Move>();
        var skipped = new List<string>();
        var pending = File.Exists(journalPath);
        var refuse = pending || Linked();
        if (Directory.Exists(dxDir))
            foreach (var files in new DirectoryInfo(dxDir).EnumerateFiles(nvidia ? "*.nvph" : "*.parc")
                         .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && KeyOf(f.Name) is { } key && !keep(key))
                         .GroupBy(f => KeyOf(f.Name), StringComparer.OrdinalIgnoreCase))
                if (refuse || files.Any(f => AppCacheFiles.ProcessesUsing(f.FullName).Count > 0)) skipped.AddRange(files.Select(f => f.FullName));
                else plan.AddRange(files.Select(f => new Move(f.FullName, Path.Combine(Folder, f.Name))));
        if (plan.Count == 0)
        {
            var none = new Journal(gameId, [], skipped);
            if (!pending) Write(none, replace: false);
            return none;
        }
        Directory.CreateDirectory(Folder);
        Write(new(gameId, plan, skipped), replace: false);   // before the first move
        var moved = new List<Move>();
        foreach (var m in plan)
            if (Rename(m.From, m.To)) moved.Add(m);
            else skipped.Add(m.From);
        var j = new Journal(gameId, moved, skipped);
        Write(j);
        return j;
    }

    /// <summary>One set-aside or restore at a time across processes; null while another holds it.</summary>
    FileStream? Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
        try { return new FileStream(journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when (IoErrors.HeldElsewhere(e)) { return null; }
    }

    /// <summary>Puts the set-aside files back, except <paramref name="old"/>'s (the game's old cache), which are deleted. A
    /// file made again at its place meanwhile waits while it is in use; then the one holding more stays and the other is
    /// kept in before-run-caches. Returns the files that couldn't be put back (all of them while a folder is a link); the
    /// journal keeps them, and those waiting, for the next try. Journal entries that don't move a cache file between the
    /// two folders are left alone.</summary>
    public List<string> Restore(string? old = null)
    {
        using var held = Lock();
        if (held == null || Pending is not { } j) return [];
        if (Linked()) return j.Moves.Select(m => m.To).ToList();
        if (glCache) return RestoreTree(j, Busy?.Invoke() ?? false);
        var left = new List<Move>();
        var waiting = new List<Move>();
        var keep = Path.Combine(KeepDir, $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(dxDir))}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}");
        foreach (var m in j.Moves.Where(Ours))
        {
            try
            {
                if (!Exists(m.To)) continue;
                if (old != null && string.Equals(KeyOf(Path.GetFileName(m.To)), old, StringComparison.OrdinalIgnoreCase)) { File.Delete(m.To); continue; }
                if (File.Exists(m.From))
                {
                    if (AppCacheFiles.ProcessesUsing(m.From).Count > 0) { waiting.Add(m); continue; }
                    if (Holds(m.From) >= Holds(m.To))
                    {
                        if (!Keep(m.To, Path.Combine(keep, Path.GetFileName(m.To)))) left.Add(m);
                        continue;
                    }
                    if (!Keep(m.From, Path.Combine(keep, Path.GetFileName(m.From)))) { left.Add(m); continue; }
                }
                if (!Rename(m.To, m.From)) left.Add(m);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(m); }
        }
        if (left.Count + waiting.Count > 0) { Write(j with { Moves = [.. left, .. waiting] }); return left.Select(m => m.To).ToList(); }
        File.Delete(journalPath);
        try { Directory.Delete(Folder); }
        catch (IOException) { }   // not empty: something else put a file there
        return [];
    }

    /// <summary>Anything is at the path, a broken link too.</summary>
    static bool Exists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    /// <summary>The move is a cache file of the cache folder to the same name in the set-aside folder (for a Vulkan cache,
    /// the same place three levels below each): a journal edited or damaged moves nothing else.</summary>
    bool Ours(Move m)
    {
        try
        {
            var from = Path.GetFullPath(m.From);
            var to = Path.GetFullPath(m.To);
            if (glCache) return Below(dxDir, from) is { } rel && rel == Below(Folder, to);
            return KeyOf(Path.GetFileName(from)) != null && Path.GetFileName(from) == Path.GetFileName(to)
                && string.Equals(Path.GetDirectoryName(from), Path.GetFullPath(Path.TrimEndingDirectorySeparator(dxDir)), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(to), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Written whole and flushed, then renamed over the journal; <paramref name="replace"/> false: IOException when
    /// a journal is already there.</summary>
    void Write(Journal j, bool replace = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var tmp = journalPath + ".tmp";
        using (var s = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(s, j);
            s.Flush(flushToDisk: true);
        }
        File.Move(tmp, journalPath, replace);
    }

    /// <summary>Sets the Vulkan cache folder's files aside whole, each held open and hashed first so a put-back knows it
    /// unchanged. Nothing moves while a journal is pending, <paramref name="held"/>, the folder isn't the driver's plain
    /// two-level layout, or a file is in use; then everything is listed as skipped. A move that fails puts back the ones before it.</summary>
    Journal SetAsideTree(string gameId, bool held)
    {
        var pending = File.Exists(journalPath);
        var files = Layout();
        var plan = new List<Move>();
        var refuse = pending || held || files == null || Linked();
        var hold = new List<FileStream>();
        try
        {
            foreach (var f in refuse ? [] : files!)
            {
                if (Open(f.FullName, hold) is not { } has) { refuse = true; break; }
                plan.Add(new Move(f.FullName, Path.Combine(Folder, Path.GetRelativePath(dxDir, f.FullName)), has.Length, has.Sha256));
            }
            if (refuse || plan.Count == 0)
            {
                var none = New(gameId, [], !refuse ? [] : files is { Count: > 0 } ? files.Select(f => f.FullName).ToList() : [dxDir]);
                if (!pending) Write(none, replace: false);
                return none;
            }
            Write(New(gameId, plan, []), replace: false);   // before the first move
            var moved = new List<Move>();
            foreach (var m in plan)
            {
                if (Put(m.From, m.To)) { moved.Add(m); continue; }
                var back = New(gameId, moved.Where(x => !Put(x.To, x.From)).ToList(), plan.Select(p => p.From).ToList());
                Write(back);
                return back;
            }
            return New(gameId, moved, []);
        }
        finally { foreach (var h in hold) h.Dispose(); }
    }

    /// <summary>Puts a Vulkan cache folder back: what the run made there is archived in before-run-caches first, and only
    /// set-aside files unchanged since (length and SHA-256) go back. Waits (returns nothing, journal kept) while
    /// <paramref name="held"/>, a file is in use, or Steam's merged files changed in the last <see cref="MergeWait"/>.
    /// Returns the files that couldn't be put back.</summary>
    List<string> RestoreTree(Journal j, bool held)
    {
        var ours = j.Moves.Where(Ours).ToList();
        var moved = ours.Where(m => Exists(m.To)).ToList();
        var unmoved = ours.Except(moved).Select(m => Path.GetFullPath(m.From)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var made = j.Skipped.Count > 0 || j.Archived ? [] : Layout()?.Where(f => !unmoved.Contains(f.FullName)).ToList();
        if (made == null) return moved.Select(m => m.To).ToList();
        if (made.Any(f => f.Name.StartsWith("steamapp_shader_cache", StringComparison.OrdinalIgnoreCase))
            && made.Max(f => f.LastWriteTimeUtc) > DateTime.UtcNow - MergeWait) return [];
        var hold = new List<FileStream>();
        try
        {
            if (held || made.Any(f => Open(f.FullName, hold, hash: false) == null)) return [];
            var back = moved.Select(m => (Move: m, Has: Open(m.To, hold))).ToList();
            if (back.Any(p => p.Has == null)) return [];
            static bool Changed((Move Move, (long Length, string? Sha256)? Has) p) => p.Has!.Value.Length != p.Move.Length || p.Has.Value.Sha256 != p.Move.Sha256;
            if (back.Where(Changed).Select(p => p.Move.To).ToList() is { Count: > 0 } changed) return changed;
            if (made.Count > 0)
            {
                var steam = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dxDir))!;
                var archive = Path.Combine(KeepDir, $"{Path.GetFileName(Path.GetDirectoryName(steam))}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}",
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(dxDir)));
                // made's files were opened first, so hold[i] is made[i]'s handle
                if (made.Select((f, i) => (f, i)).Any(x => !Keep(x.f.FullName, Path.Combine(archive, Path.GetRelativePath(dxDir, x.f.FullName)), hold[x.i])))
                    return moved.Select(m => m.To).ToList();
                Write(j with { Archived = true });
            }
            var left = back.Where(p => Changed(p) || Exists(p.Move.From) || !Put(p.Move.To, p.Move.From)).Select(p => p.Move).ToList();
            if (left.Count > 0)
            {
                Write(j with { Archived = true });
                return left.Select(m => m.To).ToList();
            }
        }
        finally { foreach (var h in hold) h.Dispose(); }
        File.Delete(journalPath);
        foreach (var d in Directory.Exists(Folder) ? Directory.EnumerateDirectories(Folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(Folder) : [])
        {
            try { Directory.Delete(d); }
            catch (IOException) { }   // not empty: something else put a file there
        }
        return [];
    }

    /// <summary>The Vulkan cache folder's files, two plain folders deep (the driver's layout); empty when it isn't there,
    /// null when anything else is in it (a file at another depth, a link) or it can't be listed.</summary>
    List<FileInfo>? Layout()
    {
        var files = new List<FileInfo>();
        if (!Directory.Exists(dxDir)) return files;
        static bool Plain(FileSystemInfo i) => (i.Attributes & FileAttributes.ReparsePoint) == 0;
        try
        {
            foreach (var a in new DirectoryInfo(dxDir).EnumerateFileSystemInfos())
            {
                if (a is not DirectoryInfo da || !Plain(a)) return null;
                foreach (var b in da.EnumerateFileSystemInfos())
                {
                    if (b is not DirectoryInfo db || !Plain(b)) return null;
                    foreach (var c in db.EnumerateFileSystemInfos())
                        if (c is FileInfo f && Plain(f)) files.Add(f);
                        else return null;
                }
            }
            return files;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary><paramref name="full"/>'s path three levels below <paramref name="root"/>; null for anything else.</summary>
    static string? Below(string root, string full) =>
        Path.GetRelativePath(Path.GetFullPath(root), full) is var rel && !Path.IsPathRooted(rel)
        && rel.Split(Path.DirectorySeparatorChar) is { Length: 3 } parts && !parts.Any(p => p is "" or "." or "..") ? rel : null;

    /// <summary>Opens a file no process uses, held in <paramref name="hold"/> (deletes and renames still allowed), with its
    /// length and, with <paramref name="hash"/>, its SHA-256; null when it is in use or can't be opened.</summary>
    static (long Length, string? Sha256)? Open(string path, List<FileStream> hold, bool hash = true)
    {
        if (AppCacheFiles.ProcessesUsing(path).Count > 0) return null;
        try
        {
            var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
            hold.Add(s);
            return (s.Length, hash ? Convert.ToHexString(SHA256.HashData(s)) : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A rename into a folder made if needed; false when it couldn't.</summary>
    static bool Put(string from, string to)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            return Rename(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/>: a rename, else, on another volume, a copy checked by
    /// SHA-256 before <paramref name="from"/> is deleted. False when it couldn't (nothing is lost). <paramref name="open"/>:
    /// a handle already open on <paramref name="from"/> to copy from.</summary>
    internal static bool Keep(string from, string to, FileStream? open = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (Rename(from, to)) return true;
            if (Marshal.GetLastPInvokeError() != 17 /* ERROR_NOT_SAME_DEVICE */ || File.Exists(to)) return false;
            var source = open ?? new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                var partial = to + ".partial";
                source.Position = 0;
                string copied, written;
                using (var dest = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buffer = new byte[1 << 20];
                        for (int n; (n = source.Read(buffer)) > 0;)
                        {
                            hash.AppendData(buffer, 0, n);
                            dest.Write(buffer, 0, n);
                        }
                        copied = Convert.ToHexString(hash.GetHashAndReset());
                    }
                    dest.Flush(flushToDisk: true);
                    dest.Position = 0;
                    written = dest.Length == source.Length ? Convert.ToHexString(SHA256.HashData(dest)) : "";
                }
                if (written != copied)
                {
                    File.Delete(partial);
                    return false;
                }
                File.Move(partial, to);
                File.Delete(from);
            }
            finally { source.Dispose(); }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A rename only: MoveFileEx without MOVEFILE_COPY_ALLOWED fails across volumes instead of copying.</summary>
    static bool Rename(string from, string to) => MoveFileExW(from, to, 0);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool MoveFileExW(string existing, string newName, uint flags);
}
