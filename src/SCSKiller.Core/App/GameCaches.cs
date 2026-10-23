using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.App;

/// <summary><c>%LOCALAPPDATA%\D3DSCache</c>: one folder per exe path of SQLite dbs (<c>*.dxcache</c>, WAL) with an
/// <c>app_id(exe_path)</c> table. Opened immutable (no lock, no -shm/-wal write); rows only in the -wal are found by scanning it.</summary>
public static partial class D3DSCache
{
    /// <summary>Folders whose every exe path is the game's; skipped on doubt (an unreadable db, no path, another path).</summary>
    public static IReadOnlyList<string> FoldersOf(string root, Game g)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        foreach (var dir in Directory.EnumerateDirectories(root))
            if (IsGames(dir, g) == true) found.Add(dir);
        return found;
    }

    /// <summary>True: every exe path the folder names is the game's; false: a db names another exe (its WAL never read);
    /// null on doubt (an unreadable file, no path).</summary>
    public static bool? IsGames(string dir, Game g)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles();
            var dbs = files.Where(f => f.Name.EndsWith(".dxcache", StringComparison.OrdinalIgnoreCase)).ToList();
            var stamp = string.Join('|', dbs.Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));
            if (!DbPaths.TryGetValue(dir, out var known) || known.Stamp != stamp)
            {
                var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!dbs.All(f => ReadAppIds(f.FullName, read))) return null;
                DbPaths[dir] = known = (stamp, read);
            }
            // measured: reading every folder's WAL allocated 13 GB for 14,000 folders
            if (!known.Paths.All(p => IsGameExe(p, g))) return false;
            var paths = new HashSet<string>(known.Paths, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files.Where(f => f.Name.EndsWith(".dxcache-wal", StringComparison.OrdinalIgnoreCase))) ReadWal(f.FullName, paths);
            return paths.Count == 0 ? null : paths.All(p => IsGameExe(p, g));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // folder -> its dbs' names, sizes and write times, and the exe paths they hold; the WAL isn't kept
    // ponytail: never pruned, a deleted folder's entry stays until the app restarts (a few MB at 14,000 folders)
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Stamp, HashSet<string> Paths)> DbPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders in <paramref name="root"/> created since <paramref name="since"/> whose dbs name only exes under
    /// <paramref name="dir"/>; one in doubt (an unreadable db, no path, a creation time not known) is never one. Only those new
    /// folders' dbs are read, none of the folders named in <paramref name="skip"/>. <paramref name="doubt"/> gets the new folders
    /// with a db that couldn't be read.</summary>
    public static IReadOnlyList<string> Made(string root, string dir, DateTime since, IReadOnlySet<string>? skip = null, ICollection<string>? doubt = null) =>
        Made(root, [dir], since, skip, doubt);

    /// <summary><see cref="Made(string, string, DateTime, IReadOnlySet{string}?, ICollection{string}?)"/> with exes under any of <paramref name="dirs"/>.
    /// With <paramref name="game"/>, also one whose dbs name only the game's exe in a folder the runtime didn't name for it: a
    /// staged db named for the game whose move into the game's folder didn't follow.</summary>
    public static IReadOnlyList<string> Made(string root, IReadOnlyList<string> dirs, DateTime since, IReadOnlySet<string>? skip = null, ICollection<string>? doubt = null,
        Game? game = null)
    {
        var unders = dirs.Select(dir => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar).ToList();
        bool Under(string p) => unders.Any(u => p.StartsWith(u, StringComparison.OrdinalIgnoreCase));
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        foreach (var d in new DirectoryInfo(root).EnumerateDirectories().Where(d => FileTimes.CreatedUtc(d) >= since.ToUniversalTime().AddSeconds(-2) && skip?.Contains(d.Name) != true))
            try
            {
                var paths = ExePaths(d.FullName);
                // a damaged db's rows can't be read, its bytes may still name the exe: then the fold finds it damaged
                if (paths == null && doubt != null && RawPaths(d.FullName) is { } raw)
                {
                    // the runtime names a folder by its process's exe path: one of ours, and nothing else's in it
                    if (raw.All(Under) && raw.Any(p => FolderName(p) == d.Name)) found.Add(d.FullName);
                    else doubt.Add(d.FullName);
                }
                else if (paths == null) doubt?.Add(d.FullName);
                else if (paths.Count > 0 && paths.All(p => Under(p) || game != null && IsGameExe(p, game) && FolderName(p) != d.Name)) found.Add(d.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { doubt?.Add(d.FullName); }
        return found;
    }

    /// <summary>Null when a db can't be read.</summary>
    public static HashSet<string>? ExePaths(string dir)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir))
            if (f.EndsWith(".dxcache", StringComparison.OrdinalIgnoreCase))
            {
                if (!ReadAppIds(f, paths)) return null;
            }
            else if (f.EndsWith(".dxcache-wal", StringComparison.OrdinalIgnoreCase)) ReadWal(f, paths);
        return paths;
    }

    /// <summary>The exe paths in the bytes of the folder's dbs and WALs; null when none.</summary>
    static HashSet<string>? RawPaths(string dir)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir))
            if (f.EndsWith(".dxcache", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dxcache-wal", StringComparison.OrdinalIgnoreCase)) ReadWal(f, paths);
        return paths.Count > 0 ? paths : null;
    }

    static void ReadWal(string file, HashSet<string> paths)
    {
        using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        var b = bytes.AsSpan();
        for (int at = 1, i; at < b.Length && (i = b[at..].IndexOf(@":\"u8)) >= 0; at += i + 2)
            if (char.IsAsciiLetter((char)b[at + i - 1]) && RowPath(b, at + i - 1) is { } path) paths.Add(path);
    }

    /// <summary>The exe path an app_id record holds at <paramref name="at"/>: its header ends right there, the id (a rowid alias)
    /// stored as nothing, then exe_path's text type, whose byte count is where the path ends (a folder name may hold ".exe").</summary>
    static string? RowPath(ReadOnlySpan<byte> b, int at)
    {
        for (var h = 3; h <= Math.Min(at, 64); h++)
        {
            if (b[at - h] != h) continue;   // the header's size, a one-byte varint here
            var types = new List<long>();
            for (var i = at - h + 1; i < at;) types.Add(Varint(b, ref i, at));
            if (types.Count < 2 || types.Contains(-1) || types[0] is not (0 or 8 or 9) || types[1] < 13 || types[1] % 2 == 0) continue;
            var n = (int)Math.Min((types[1] - 13) / 2, int.MaxValue);
            if (n > b.Length - at || !System.Text.Unicode.Utf8.IsValid(b.Slice(at, n))) continue;
            var path = Encoding.UTF8.GetString(b.Slice(at, n));   // SQLite text is UTF-8
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !path.Any(char.IsControl)) return path;
        }
        return null;
    }

    /// <summary>SQLite's big-endian varint at <paramref name="i"/>; -1 when it runs to <paramref name="end"/>.</summary>
    static long Varint(ReadOnlySpan<byte> b, ref int i, int end)
    {
        long v = 0;
        for (var k = 0; k < 9; k++)
        {
            if (i >= end) return -1;
            var x = b[i++];
            if (k == 8) return v << 8 | x;
            v = v << 7 | (long)(x & 0x7F);
            if (x < 0x80) return v;
        }
        return v;
    }

    /// <summary>The runtime's files in a cache folder, nothing else it may hold: <c>&lt;GUID&gt;[_VEN_..&amp;DEV_..&amp;SUBSYS_..&amp;REV_..]</c>
    /// with .dxcache (and its -shm, -wal) or the older .idx, .val, .lock.</summary>
    public static IEnumerable<FileInfo> CacheFiles(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles().Where(f => CacheFile.IsMatch(f.Name));

    static readonly Regex CacheFile = new(@"^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}(_VEN_[0-9A-F]+&DEV_[0-9A-F]+&SUBSYS_[0-9A-F]+&REV_[0-9A-F]+)?\.(dxcache(-shm|-wal)?|idx|val|lock)$",
        RegexOptions.IgnoreCase);

    /// <summary>An Xbox game's exe runs from <c>...\WindowsApps\&lt;Name&gt;_&lt;Version&gt;_&lt;Arch&gt;_&lt;ResourceId&gt;_&lt;PublisherId&gt;\&lt;path in Content&gt;</c>:
    /// Name_PublisherId is its package family, any version matches. The folder is named by the path as launched (measured on
    /// 621): through a junction, a subst drive or a mounted folder the same exe gets another one, so the same file counts too.</summary>
    public static bool IsGameExe(string path, Game g)
    {
        if (path.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
        if (Path.GetFileName(path).Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase)
            && Games.GameFiles.Identity(path)?.Id is { } id && Games.GameFiles.Identity(g.ExePath)?.Id is { } own && id.AsSpan().SequenceEqual(own)) return true;
        if (g.Store != Store.Xbox || !g.Id.StartsWith("xbox:", StringComparison.Ordinal)) return false;
        var parts = path.Split('\\');
        var i = Array.FindIndex(parts, p => p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 2 >= parts.Length) return false;
        var pkg = parts[i + 1].Split('_');   // package names have no '_'
        return pkg.Length == 5 && $"{pkg[0]}_{pkg[4]}".Equals(g.Id["xbox:".Length..], StringComparison.OrdinalIgnoreCase)
            && string.Join('\\', parts[(i + 2)..]).Equals(Path.GetRelativePath(g.InstallDir, g.ExePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>False when not a readable SQLite db; one without the table yet (rows still in the WAL) adds nothing.</summary>
    static bool ReadAppIds(string file, HashSet<string> paths)
    {
        var rc = sqlite3_open_v2(Immutable(file), out var db, 0x01 /* READONLY */ | 0x40 /* URI */, 0);
        try
        {
            if (rc != 0) return false;
            long tables = 0;
            if (!Rows(db, "select count(*) from sqlite_master where type='table' and name='app_id'", st => tables = sqlite3_column_int64(st, 0))) return false;
            return tables == 0 || Rows(db, "select exe_path from app_id", st =>
            {
                if (Marshal.PtrToStringUTF8(sqlite3_column_text(st, 0)) is { Length: > 0 } p) paths.Add(p);
            });
        }
        finally { sqlite3_close_v2(db); }
    }

    /// <summary>The folder name the runtime opens for a process started from <paramref name="exePath"/>, exactly as the
    /// process sees it (GetModuleFileNameW, case kept): its CRC-64 over UTF-16LE in lowercase hex, no leading zeros.</summary>
    public static string FolderName(string exePath)
    {
        // D3DSCache.dll GetFilenameInAppDataStorageForCurrentApp: reflected poly 0x92C64265D32139A4, this init, no final xor
        var crc = 0x012345678ABCDEF0UL;
        foreach (var b in MemoryMarshal.AsBytes(exePath.AsSpan()))
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? crc >> 1 ^ 0x92C64265D32139A4UL : crc >> 1;
        }
        return crc.ToString("x");
    }

    /// <summary>The game's folder a compile's dbs go to, with the one exe path its dbs name, which must still exist (an Xbox
    /// game's earlier versions each have a folder too): the one written last.</summary>
    public static (string Dir, string Exe)? GameFolder(string root, Game g)
    {
        foreach (var d in FoldersOf(root, g).Select(d => new DirectoryInfo(d)).Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                     .OrderByDescending(d => d.LastWriteTimeUtc))
            // a staged db named for the game but not moved yet sits in a folder the runtime named for another exe
            if (ExePaths(d.FullName) is { Count: 1 } exe && File.Exists(exe.Single()) && FolderName(exe.Single()) == d.Name) return (d.FullName, exe.Single());
        return null;
    }

    /// <summary>Puts every db of a staged exe's folder into the game's: NVIDIA from 621 keeps compiled pipelines there, keyed by
    /// content, so they hit under any exe path. A missing db is copied in naming <paramref name="exe"/>; an existing one gains
    /// the missing rows in one transaction, only with the same schema and compiler. False on doubt, that db unchanged then: a
    /// db held open by any process, another compiler, a db that fails SQLite's check, a link. <paramref name="stop"/> true (the
    /// game started) ends it early: the game's db is rolled back and released, the rest left as it is. <paramref name="refused"/>
    /// gets why the first db stayed out. The adapter dbs take the identity <see cref="Decide"/> gives (an Xbox game's
    /// <paramref name="package"/> family), which <paramref name="stamped"/> gets when an adapter db went in or all stayed out for it.</summary>
    public static bool MergeInto(string from, string to, string exe, Action<long>? added = null, Func<bool>? stop = null, Action<string>? refused = null,
        string? package = null, Action<IdentityStamp>? stamped = null)
    {
        var ok = true;
        Lookup(exe, out var read);
        var decided = Decide(read, exe, package, Planning.RulesFile.Current);
        if (decided.Source == IdentityStamp.Refused)
        {
            stamped?.Invoke(decided);
            refused?.Invoke("the game's shader delivery registrations differ");
            return false;
        }
        var into = false;
        foreach (var f in Directory.GetFiles(from, "*.dxcache"))
        {
            string? why;
            try { why = stop?.Invoke() == true ? GameStarted : MergeDb(f, Path.Combine(to, Path.GetFileName(f)), exe, added, stop, decided.Stamped,
                decided.Source == IdentityStamp.ByRule); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = e.Message; }
            into |= why == null && Path.GetFileName(f).Contains("_VEN_", StringComparison.OrdinalIgnoreCase);
            if (why == null) continue;
            if (ok) refused?.Invoke(why);
            ok = false;
        }
        if (into) stamped?.Invoke(decided);
        return ok;
    }

    /// <summary>A db of the folder fails SQLite's check: what the compile wrote there is lost. False on doubt (held, unopenable).</summary>
    public static bool Damaged(string dir)
    {
        foreach (var f in Directory.GetFiles(dir, "*.dxcache"))
        {
            if (Held(f)) continue;
            if (sqlite3_open_v2(f, out var db, 0x01 /* READONLY */, 0) != 0) { sqlite3_close_v2(db); continue; }
            try { if (Text(db, "pragma quick_check") != "ok") return true; }
            finally { sqlite3_close_v2(db); }
        }
        return false;
    }

    /// <summary>True when an adapter db both folders hold names another compiler in each; false on doubt.</summary>
    public static bool OtherCompiler(string a, string b)
    {
        foreach (var f in Directory.GetFiles(a, "*_VEN_*.dxcache"))
            if (Path.Combine(b, Path.GetFileName(f)) is var other && File.Exists(other)
                && !LinkedDb(f) && !LinkedDb(other) && Compiler(f) is { Length: > 0 } x && Compiler(other) is { Length: > 0 } y && x != y) return true;
        return false;
    }

    static string? Compiler(string file)
    {
        if (sqlite3_open_v2(file, out var db, 0x01 /* READONLY */, 0) != 0) { sqlite3_close_v2(db); return null; }
        try { return Text(db, string.Format(CompilerSql, "main")); }
        finally { sqlite3_close_v2(db); }
    }

    const string SchemaSql = "select group_concat(name || ':' || sql, '|') from (select name, sql from {0}.sqlite_master order by name)";
    const string CompilerSql = "select group_concat(abi || ':' || version || ':' || family, '|') from (select * from {0}.compiler_id order by id)";

    const string GameStarted = "the game started";

    /// <summary>Null when merged, else why not.</summary>
    static string? MergeDb(string from, string to, string exe, Action<long>? added, Func<bool>? stop, AppIdentity? asd = null, bool byRule = false)
    {
        // the runtime stamps a registered exe's identity into its adapter db only, and empties an adapter db without it
        var adapter = Path.GetFileName(from).Contains("_VEN_", StringComparison.OrdinalIgnoreCase);
        var stamp = adapter ? asd : null;
        if (!CacheFile.IsMatch(Path.GetFileName(from))) return "not a runtime cache file";
        if (LinkedDb(from) || LinkedDb(to)) return "a db is a link";
        if (Held(from) || Held(from + "-wal")) return "the compile's db is open in another process";
        var copy = !File.Exists(to);
        if (!Open(from, out var src)) return "the compile's db can't be opened";
        long rows = 0;
        try
        {
            long busy = -1;
            // the copy takes the main file only: every WAL frame must be in it
            if (copy && (!Rows(src, "pragma wal_checkpoint(TRUNCATE)", st => busy = sqlite3_column_int64(st, 0)) || busy != 0)) return "the compile's db WAL can't be checkpointed";
            if (Text(src, "pragma quick_check") != "ok" || Text(src, string.Format(SchemaSql, "main")) is not { } schema
                || !schema.Contains("keys:") || !schema.Contains("vals:") || !schema.Contains("app_id:")) return "the compile's db fails SQLite's check";
            Rows(src, "select count(*) from keys", st => rows = sqlite3_column_int64(st, 0));
        }
        finally { sqlite3_close_v2(src); }
        if (copy)
        {
            if (File.Exists(to + "-wal") || File.Exists(from + "-wal") && new FileInfo(from + "-wal").Length > 0) return "a WAL is left beside a db";   // a WAL would replay into the copy
            if (string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
            {
                // one volume: the staged db (ours) is named for the game where it is, then renamed into the game's folder
                if (stop?.Invoke() == true) return GameStarted;
                if (!Open(from, out var own)) return "the compile's db can't be opened";
                try
                {
                    if (!NameFor(own, exe)) return "the compile's db can't be named for the game";
                    // a staged db left pending carries the stamp of the fold before: each copy takes this one's, none included
                    if (adapter && !Stamp(own, stamp ?? AppIdentity.None)) return "the compile's db can't take the game's shader delivery identity";
                }
                finally { sqlite3_close_v2(own); }   // the last connection checkpoints into the file
                TestPause();
                if (File.Exists(from + "-wal") || Held(from)) return "the compile's db is open in another process";
                if (stop?.Invoke() == true) return GameStarted;
                File.Move(from, to);   // fails when the runtime made one meanwhile: merged next time
                File.Delete(from + "-shm");
                added?.Invoke(rows);
                return null;
            }
            var tmp = to + "." + Guid.NewGuid().ToString("N") + ".scskiller-tmp";
            try
            {
                // no other writer while the bytes are read: the checkpointed file is the whole db
                using (var s = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var d = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (File.Exists(from + "-wal") && new FileInfo(from + "-wal").Length > 0) return "a WAL is left beside a db";
                    s.CopyTo(d);
                }
                if (!Open(tmp, out var db)) return "the copied db can't be opened";
                try
                {
                    if (!NameFor(db, exe)) return "the compile's db can't be named for the game";
                    if (adapter && !Stamp(db, stamp ?? AppIdentity.None)) return "the compile's db can't take the game's shader delivery identity";
                }
                finally { sqlite3_close_v2(db); }   // the last connection checkpoints into tmp
                if (File.Exists(tmp + "-wal")) return "a WAL is left beside a db";
                if (stop?.Invoke() == true) return GameStarted;
                File.Move(tmp, to);   // fails when the runtime made one meanwhile: merged next time
                added?.Invoke(rows);
                return null;
            }
            finally
            {
                File.Delete(tmp);
                File.Delete(tmp + "-wal");
                File.Delete(tmp + "-shm");
            }
        }
        if (Held(to) || Held(to + "-wal")) return "the game's db is open in another process";
        if (!Open(to, out var t)) return "the game's db can't be opened";
        // the game started: the next step fails with SQLITE_INTERRUPT, which rolls the transaction back and frees the db
        // an exception can't cross SQLite's native frames: a check that throws lets the merge go on
        Progress? check = stop == null ? null : _ =>
        {
            try { return stop() ? 1 : 0; }
            catch (Exception) { return 0; }
        };
        if (check != null) sqlite3_progress_handler(t, 10000, check, 0);
        try
        {
            // the db's EXCLUSIVE lock, taken at the first read, fails while any other connection holds its SHARED lock (all of
            // them do in WAL mode): a game that opened it after Held keeps it, and none reads or writes it until this one closes.
            // No busy timeout: the dbs wait for the next try.
            TestPause();
            if (!Exec(t, "pragma main.locking_mode=exclusive") || !Exec(t, "select count(*) from main.sqlite_master")) return "the game's db is in use by another process";
            if (!Exec(t, $"attach database {Quote(from)} as s")) return "the compile's db can't be attached";
            var schema = Text(t, string.Format(SchemaSql, "main"));
            if (schema == null || schema != Text(t, string.Format(SchemaSql, "s"))) return "the game's db has another schema";
            if (schema.Contains("compiler_id:") && (Text(t, string.Format(CompilerSql, "main")) is not { Length: > 0 } mine || mine != Text(t, string.Format(CompilerSql, "s")))) return "the game's db names another compiler";
            if (stop?.Invoke() == true) return GameStarted;
            if (!Exec(t, "begin immediate")) return "the game's db is locked";
            var refused = Text(t, "pragma main.quick_check") != "ok" ? "the game's db fails SQLite's check"
                : Text(t, $"select count(*) from main.app_id where exe_path is not {Quote(exe)}") != "0" ? "the game's db names another exe"
                // made under an earlier registration: the game's next start empties it, the rows go in after that
                // a rule never changes a db the game's runtime wrote: its rows go in only where it holds the rule's identity
                : stamp != null && byRule && Text(t, $"select count(*) from main.app_id where not ({stamp.Is})") != "0" ? "the game's db holds another identity than its rule's"
                : stamp != null && !byRule && Text(t, $"select count(*) from main.app_id where not (app_name is null and engine_name is null) and not ({stamp.Is})") != "0" ? "the game's db is from before its shader delivery"
                : stamp != null && !byRule && !Stamp(t, stamp) ? "the game's db can't take its shader delivery identity" : null;
            if (refused != null)
            {
                Rollback(t);
                return refused;
            }
            const string fresh = "s.keys where key not in (select key from main.keys)";
            rows = 0;
            Rows(t, $"select count(*) from {fresh}", st => rows = sqlite3_column_int64(st, 0));
            // groups stay out: no driver writes them yet
            if (Exec(t, $"insert into main.keys(key, last_accessed) select key, last_accessed from {fresh}")
                && Exec(t, "insert into main.vals(keyid, type, value) select m.keyid, v.type, v.value from s.keys k join s.vals v on v.keyid = k.keyid "
                    + "join main.keys m on m.key = k.key where not exists (select 1 from main.vals x where x.keyid = m.keyid and x.type = v.type)")
                && stop?.Invoke() != true && Exec(t, "commit"))
            {
                added?.Invoke(rows);
                return null;
            }
            Rollback(t);
            return stop?.Invoke() == true ? GameStarted : "the rows couldn't be written";
        }
        finally
        {
            sqlite3_close_v2(t);
            GC.KeepAlive(check);
        }
    }

    static void Rollback(nint db)
    {
        sqlite3_progress_handler(db, 0, null, 0);
        Exec(db, "rollback");
    }

    delegate int Progress(nint arg);

    /// <summary>Every app_id row names <paramref name="exe"/>, and there is one.</summary>
    static bool NameFor(nint db, string exe) => Exec(db, $"update app_id set exe_path = {Quote(exe)}")
        && Text(db, $"select count(*) from app_id where exe_path is not {Quote(exe)}") == "0" && Text(db, "select count(*) from app_id") is { } n && n != "0";

    /// <summary>An exe's Advanced Shader Delivery registration (a store's or installer's, under DirectX\ShaderCache): the identity
    /// the runtime writes into the exe's adapter db, and the precompiled shader dbs delivered for it (their size is shown).</summary>
    public sealed record Delivery(string Name, string Engine, long Version, long EngineVersion, IReadOnlyList<string> Psdbs, IReadOnlyList<string>? Families = null)
    {
        public AppIdentity Identity => new(Name, Engine, Version, EngineVersion);

        public long Bytes => Psdbs.Sum(p =>
        {
            try { return new FileInfo(p).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }   // replaced by its store meanwhile
        });
    }

    /// <summary>The registration the runtime applies to <paramref name="exe"/> (HKLM and HKCU): its ExePath is the exe's final
    /// path. Null when none, unreadable, or two differ.</summary>
    public static Delivery? Delivered(string exe) => Lookup(exe, out _);

    /// <summary><see cref="Delivered"/>, with what the read found (<paramref name="read"/>): registrations that differ, whose one the
    /// runtime takes isn't known; a key that can't be read, logged once, after which nothing is stamped: a copy goes in as it
    /// is, and the worst the runtime does with it is empty it (a cold start, as without a compile).</summary>
    static Delivery? Lookup(string exe, out RegistryRead read)
    {
        read = new(RegistryRead.None, []);
        if (!OperatingSystem.IsWindows()) return null;
        var matches = new List<Delivery>();
        var unread = false;
        T? Read<T>(string key, Func<T?> f) where T : class
        {
            try { return f(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                unread = true;
                if (Unreadable.TryAdd(key, 0)) Warn?.Invoke($"the shader delivery registration {key} can't be read: {e.Message}");
                return null;
            }
        }
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            var at = hive.Name + @"\Software\Microsoft\DirectX\ShaderCache";
            using var root = Read(at, () => hive.OpenSubKey(@"Software\Microsoft\DirectX\ShaderCache"));
            foreach (var installer in Read(at, () => root?.GetSubKeyNames()) ?? [])
            {
                var ip = at + "\\" + installer;
                using var ik = Read(ip, () => root!.OpenSubKey(installer));
                foreach (var app in Read(ip, () => ik?.GetSubKeyNames()) ?? [])
                {
                    var ap = ip + "\\" + app;
                    using var ak = Read(ap, () => ik!.OpenSubKey(app));
                    if (ak == null || Read(ap, () => ak.GetValue("ExePath")) is not string path || !SameExe(path, exe)) continue;
                    var (psdbs, families) = (new List<string>(), new List<string>());
                    using var comps = Read(ap, () => ak.OpenSubKey("Components"));
                    foreach (var c in Read(ap, () => comps?.GetSubKeyNames()) ?? [])
                    {
                        using var fams = Read(ap, () => comps!.OpenSubKey(c + @"\AdapterFamilies"));
                        foreach (var f in Read(ap, () => fams?.GetSubKeyNames()) ?? [])
                        {
                            using var fk = Read(ap, () => fams!.OpenSubKey(f));
                            if (Read(ap, () => fk?.GetValue("PSDBPath")) is string { Length: > 0 } p) { psdbs.Add(p); families.Add(f); }
                        }
                    }
                    if (Read(ap, () => (object)new Delivery(ak.GetValue("Name") as string ?? "", ak.GetValue("EngineName") as string ?? "",
                        Num(ak.GetValue("Version")), Num(ak.GetValue("EngineVersion")), psdbs, families)) is Delivery found) matches.Add(found);
                }
            }
        }
        AppIdentity[] ids = [.. matches.Select(m => m.Identity).Distinct()];
        var doubt = ids.Length > 1;
        // measured: with a key it can't read in the hive the runtime writes no identity either, so nothing is stamped
        read = new(unread ? RegistryRead.Unreadable : doubt ? RegistryRead.Differ : ids.Length == 1 ? RegistryRead.One : RegistryRead.None, ids);
        return doubt || unread || matches.Count == 0 ? null : matches[0] with
        {
            Psdbs = [.. PsdbsOf(matches).Select(p => p.Psdb)], Families = [.. PsdbsOf(matches).Select(p => p.Family)],
        };
    }

    /// <summary>The registrations' delivered dbs, each once, with the adapter family it was registered for.</summary>
    static IEnumerable<(string Psdb, string Family)> PsdbsOf(List<Delivery> matches) =>
        matches.SelectMany(m => m.Psdbs.Zip(m.Families ?? [])).Select(p => (Psdb: p.First, Family: p.Second)).DistinctBy(p => p.Psdb, StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the line naming a registration key that can't be read, once per key and session.</summary>
    public static Action<string>? Warn;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Unreadable = new(StringComparer.OrdinalIgnoreCase);

    static long Num(object? o) => o switch { long l => l, int i => (uint)i, _ => 0 };   // REG_DWORD is unsigned

    // measured: the runtime compares the registered path as written with the launched exe's final path (junctions followed),
    // so a registration at the real path holds through a junction, one at a junction path never does
    static bool SameExe(string registered, string exe) =>
        Games.GameFiles.Identity(exe)?.Final is { } final && final.Equals(registered, StringComparison.OrdinalIgnoreCase);

    // measured on NVIDIA 621: the runtime writes the registration's values unchanged and keeps a db stamped with them
    static bool Stamp(nint db, AppIdentity a) => Exec(db, $"update app_id set {a.Set}");

    /// <summary>The db or its -wal or -shm is a link: SQLite would follow it out of the folder.</summary>
    static bool LinkedDb(string file) => new[] { file, file + "-wal", file + "-shm" }.Any(f => File.Exists(f) && new FileInfo(f).Attributes.HasFlag(FileAttributes.ReparsePoint));

    // SCSKILLER_TEST_FOLD_PAUSE_MS: a pause before the merge locks the game's db and before a renamed staged db moves (E2E),
    // at most 5 s so a stray value can't hold a fold
    static void TestPause()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("SCSKILLER_TEST_FOLD_PAUSE_MS"), out var ms) && ms > 0) Thread.Sleep(Math.Min(ms, 5000));
    }

    /// <summary>Another handle on the file (the game's runtime, a warm): no exclusive open.</summary>
    static bool Held(string file)
    {
        if (!File.Exists(file)) return false;
        try { using var _ = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    static bool Open(string file, out nint db)
    {
        if (sqlite3_open_v2(file, out db, 0x02 /* READWRITE */, 0) == 0) return true;
        sqlite3_close_v2(db);
        return false;
    }

    static string Immutable(string file) =>
        "file:" + file.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23").Replace("?", "%3f") + "?immutable=1";

    static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    static bool Exec(nint db, string sql) => Rows(db, sql, _ => { });

    static string? Text(nint db, string sql)
    {
        string? text = null;
        return Rows(db, sql, st => text = Marshal.PtrToStringUTF8(sqlite3_column_text(st, 0)) ?? "") ? text : null;
    }

    /// <summary>Runs <paramref name="sql"/> on the SQLite db <paramref name="file"/>, read only and as it is on disk (immutable); false
    /// when it isn't a readable db or the query fails.</summary>
    internal static bool Query(string file, string sql, Action<nint> row)
    {
        var uri = "file:" + file.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23").Replace("?", "%3f") + "?immutable=1";
        var rc = sqlite3_open_v2(uri, out var db, 0x01 /* READONLY */ | 0x40 /* URI */, 0);
        try { return rc == 0 && Rows(db, sql, row); }
        finally { sqlite3_close_v2(db); }
    }

    static bool Rows(nint db, string sql, Action<nint> row)
    {
        if (sqlite3_prepare_v2(db, sql, -1, out var st, 0) != 0) return false;
        try
        {
            int rc;
            while ((rc = sqlite3_step(st)) == 100 /* ROW */) row(st);
            return rc == 101;   // DONE
        }
        finally { sqlite3_finalize(st); }
    }

    [DllImport(Sqlite)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out nint db, int flags, nint vfs);
    [DllImport(Sqlite)] static extern int sqlite3_close_v2(nint db);
    [DllImport(Sqlite)] static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint stmt, nint tail);
    [DllImport(Sqlite)] static extern int sqlite3_step(nint stmt);
    [DllImport(Sqlite)] internal static extern nint sqlite3_column_text(nint stmt, int col);
    [DllImport(Sqlite)] internal static extern long sqlite3_column_int64(nint stmt, int col);
    [DllImport(Sqlite)] static extern int sqlite3_finalize(nint stmt);
    [DllImport(Sqlite)] static extern void sqlite3_progress_handler(nint db, int ops, Progress? check, nint arg);
}

/// <summary>Unreal's user pipeline cache, <c>&lt;Saved&gt;\&lt;Project&gt;_&lt;ShaderPlatform&gt;.upipelinecache</c>. Saved's place varies
/// by game (e.g. <c>%LOCALAPPDATA%\Iris\Saved_Steam_&lt;id&gt;</c>), so it's found by file name in any Saved / Saved_* folder one
/// level under the roots; never inside the install (the shipped caches).</summary>
public static class UnrealUserCache
{
    /// <summary>The folder holding <c>Binaries\&lt;platform&gt;\</c> the exe is in; null when not laid out like that.</summary>
    public static string? Project(string exePath)
    {
        var bin = Path.GetDirectoryName(Path.GetDirectoryName(exePath));
        return bin != null && Path.GetFileName(bin).Equals("Binaries", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(Path.GetDirectoryName(bin)) is { Length: > 0 } project ? project : null;
    }

    public static IReadOnlyList<string> FilesOf(Game g, IEnumerable<string> roots)
    {
        if (Project(g.ExePath) is not { } project) return [];
        var name = new Regex($@"^{Regex.Escape(project)}_(PCD3D|SF_VULKAN|VULKAN)_[A-Z0-9_]+(\.stable)?\.upipelinecache$", RegexOptions.IgnoreCase);
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir)) + Path.DirectorySeparatorChar;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var files = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var app in Directory.EnumerateDirectories(root, "*", options))
                try
                {
                    foreach (var saved in Directory.EnumerateDirectories(app, "Saved*", options))
                        if (Path.GetFileName(saved) is var s && (s.Equals("Saved", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Saved_", StringComparison.OrdinalIgnoreCase)))
                            files.AddRange(Directory.EnumerateFiles(saved, "*.upipelinecache", options)
                                .Where(f => name.IsMatch(Path.GetFileName(f)) && !Path.GetFullPath(f).StartsWith(install, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return files;
    }

    /// <summary>The folder names a game's own caches go under in %LOCALAPPDATA%, My Games or ProgramData: its name, its
    /// install folder's, its Unreal project's (Tiny Tina's Wonderlands: <c>%LOCALAPPDATA%\Tiny Tina's Wonderlands\Saved</c>).</summary>
    public static IEnumerable<string> Folders(Game g) =>
        new[] { g.Name, Path.GetFileName(Path.TrimEndingDirectorySeparator(g.InstallDir)), Project(g.ExePath) }
            .OfType<string>().Select(n => n.Trim()).Where(n => n.Length > 0 && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <c>*.ushaderprecache</c> files (a shader and pipeline cache some Unreal games write themselves, and rebuild
    /// at their next start) in <c>&lt;root&gt;\&lt;folder&gt;</c> and two levels below (its Saved folder); never inside the install.</summary>
    public static IReadOnlyList<string> PrecacheFilesOf(Game g, IEnumerable<string> roots, IEnumerable<string> folders)
    {
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir)) + Path.DirectorySeparatorChar;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = true, MaxRecursionDepth = 2 };
        var names = folders.ToList();
        return roots.Where(Directory.Exists).SelectMany(r => names.Select(n => Path.Combine(r, n))).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.ushaderprecache", options))
            .Where(f => !Path.GetFullPath(f).StartsWith(install, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Pipeline caches a game writes and rebuilds when they're gone, never a file it ships, each tied to its game's engine:
///   - Dawn (Guardians of the Galaxy): <c>bin\pipelineLibrary.cache</c> beside the shipped <c>bin\raw*.store2</c>
///     (<see cref="Carved.DawnStores"/>), a serialized ID3D12PipelineLibrary, which only loads on the GPU and driver that
///     wrote it;
///   - RE Engine: <c>shader.cache2</c> and <c>piplinelist.bin</c> beside the exe, written at the first start after an
///     install (RE Engine);
///   - GTA V Enhanced (RAGE, <c>GTA5_Enhanced.exe</c>): <c>*.d3d12PipelineCacheWindows</c> in
///     <c>Documents\Rockstar Games\GTAV Enhanced\Settings</c>, a pipeline library;
///   - HELLDIVERS 2 (Stingray, <c>helldivers2.exe</c>): <c>%APPDATA%\Arrowhead\Helldivers2\shader_cache\pipeline_library.cache</c>,
///     a pipeline library.
/// Folders outside the install are another game's for every other game of the engine, so those go by the exe too. A list of
/// the pipelines play created (War Thunder's dx12.cache, GTA V's warmup cache, Unreal's .upipelinecache) is never one:
/// only playing again refills it.</summary>
public static class OwnPipelineCaches
{
    static readonly string[] DawnFiles = ["rawshader.store2", "rawroot.store2", "rawpso.store2"], ReFiles = ["shader.cache2", "piplinelist.bin"];

    public static IReadOnlyList<string> FilesOf(Game g, EngineInfo? engine, string documents, string roamingAppData)
    {
        var bin = Games.GameFiles.Find(g.InstallDir, "bin");
        var files = new List<string>();
        if (DawnFiles.All(f => File.Exists(Games.GameFiles.Find(bin, f)))) files.Add(Games.GameFiles.Find(bin, "pipelineLibrary.cache"));
        if (engine?.Family == ReEngine.ReEngineReader.Family) files.AddRange(ReFiles.Select(f => Games.GameFiles.Find(Path.GetDirectoryName(g.ExePath)!, f)));
        var exe = Path.GetFileName(g.ExePath);
        if (engine?.Family == Rage.RageReader.Family && exe.Equals("GTA5_Enhanced.exe", StringComparison.OrdinalIgnoreCase)
            && Path.Combine(documents, "Rockstar Games", "GTAV Enhanced", "Settings") is var settings && Directory.Exists(settings))
            files.AddRange(Directory.EnumerateFiles(settings, "*.d3d12PipelineCacheWindows", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }));
        if (engine is { Family: Nixxes.NixxesReader.Family, Fork: Nixxes.NixxesReader.Stingray } && exe.Equals("helldivers2.exe", StringComparison.OrdinalIgnoreCase))
            files.Add(Path.Combine(roamingAppData, "Arrowhead", "Helldivers2", "shader_cache", "pipeline_library.cache"));
        return files.Where(File.Exists).ToList();
    }
}
