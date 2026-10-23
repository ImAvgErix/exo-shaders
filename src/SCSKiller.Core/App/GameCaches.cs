using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.App;

/// <summary><c>%LOCALAPPDATA%\D3DSCache</c>: one folder per exe path of SQLite dbs (<c>*.dxcache</c>, WAL) with an
/// <c>app_id(exe_path)</c> table. Opened immutable (no lock, no -shm/-wal write); rows only in the -wal are found by scanning it.</summary>
public static class D3DSCache
{
    internal const string Sqlite = "winsqlite3.dll";

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
    /// <paramref name="dir"/>; one in doubt (an unreadable db, no path) is never one. Only those new folders' dbs are read.</summary>
    public static IReadOnlyList<string> Made(string root, string dir, DateTime since)
    {
        var under = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar;
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        foreach (var d in new DirectoryInfo(root).EnumerateDirectories().Where(d => FileTimes.CreatedUtc(d) >= since.ToUniversalTime().AddSeconds(-2)))
            try
            {
                if (ExePaths(d.FullName) is { Count: > 0 } paths && paths.All(p => p.StartsWith(under, StringComparison.OrdinalIgnoreCase)))
                    found.Add(d.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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

    static void ReadWal(string file, HashSet<string> paths)
    {
        using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        foreach (Match m in WalExe.Matches(Encoding.UTF8.GetString(bytes))) paths.Add(m.Value);   // SQLite text is UTF-8
    }

    static readonly Regex WalExe = new(@"[A-Za-z]:\\[^\x00-\x1F\x7F�]{3,}?\.[Ee][Xx][Ee]");   // U+FFFD: a byte that isn't UTF-8 text ends a path

    /// <summary>The runtime's files in a cache folder, nothing else it may hold: <c>&lt;GUID&gt;[_VEN_..&amp;DEV_..&amp;SUBSYS_..&amp;REV_..]</c>
    /// with .dxcache (and its -shm, -wal) or the older .idx, .val, .lock.</summary>
    public static IEnumerable<FileInfo> CacheFiles(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles().Where(f => CacheFile.IsMatch(f.Name));

    static readonly Regex CacheFile = new(@"^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}(_VEN_[0-9A-F]+&DEV_[0-9A-F]+&SUBSYS_[0-9A-F]+&REV_[0-9A-F]+)?\.(dxcache(-shm|-wal)?|idx|val|lock)$",
        RegexOptions.IgnoreCase);

    /// <summary>An Xbox game's exe runs from <c>...\WindowsApps\&lt;Name&gt;_&lt;Version&gt;_&lt;Arch&gt;_&lt;ResourceId&gt;_&lt;PublisherId&gt;\&lt;path in Content&gt;</c>:
    /// Name_PublisherId is its package family, any version matches.</summary>
    public static bool IsGameExe(string path, Game g)
    {
        if (path.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
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
            if (ExePaths(d.FullName) is { Count: 1 } exe && File.Exists(exe.Single())) return (d.FullName, exe.Single());
        return null;
    }

    /// <summary>Puts every db of a staged exe's folder into the game's: NVIDIA from 621 keeps compiled pipelines there, keyed by
    /// content, so they hit under any exe path. A missing db is copied in naming <paramref name="exe"/>; an existing one gains
    /// the missing rows in one transaction, only with the same schema and compiler. False on doubt, that db unchanged then: a
    /// db held open by any process, another compiler, a db that fails SQLite's check, a link.</summary>
    public static bool MergeInto(string from, string to, string exe)
    {
        var ok = true;
        foreach (var f in Directory.GetFiles(from, "*.dxcache"))
            try { ok &= MergeDb(f, Path.Combine(to, Path.GetFileName(f)), exe); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ok = false; }
        return ok;
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

    static bool MergeDb(string from, string to, string exe)
    {
        if (!CacheFile.IsMatch(Path.GetFileName(from)) || LinkedDb(from) || LinkedDb(to) || Held(from) || Held(from + "-wal")) return false;
        var copy = !File.Exists(to);
        if (!Open(from, out var src)) return false;
        try
        {
            long busy = -1;
            // the copy takes the main file only: every WAL frame must be in it
            if (copy && (!Rows(src, "pragma wal_checkpoint(TRUNCATE)", st => busy = sqlite3_column_int64(st, 0)) || busy != 0)) return false;
            if (Text(src, "pragma quick_check") != "ok" || Text(src, string.Format(SchemaSql, "main")) is not { } schema
                || !schema.Contains("keys:") || !schema.Contains("vals:") || !schema.Contains("app_id:")) return false;
        }
        finally { sqlite3_close_v2(src); }
        if (copy)
        {
            if (File.Exists(to + "-wal") || File.Exists(from + "-wal") && new FileInfo(from + "-wal").Length > 0) return false;   // a WAL would replay into the copy
            if (string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
            {
                // one volume: the staged db (ours) is named for the game where it is, then renamed into the game's folder
                if (!Open(from, out var own)) return false;
                try { if (!NameFor(own, exe)) return false; }
                finally { sqlite3_close_v2(own); }   // the last connection checkpoints into the file
                if (File.Exists(from + "-wal") || Held(from)) return false;
                File.Move(from, to);   // fails when the runtime made one meanwhile: merged next time
                File.Delete(from + "-shm");
                return true;
            }
            var tmp = to + "." + Guid.NewGuid().ToString("N") + ".scskiller-tmp";
            try
            {
                // no other writer while the bytes are read: the checkpointed file is the whole db
                using (var s = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var d = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (File.Exists(from + "-wal") && new FileInfo(from + "-wal").Length > 0) return false;
                    s.CopyTo(d);
                }
                if (!Open(tmp, out var db)) return false;
                try { if (!NameFor(db, exe)) return false; }
                finally { sqlite3_close_v2(db); }   // the last connection checkpoints into tmp
                if (File.Exists(tmp + "-wal")) return false;
                File.Move(tmp, to);   // fails when the runtime made one meanwhile: merged next time
                return true;
            }
            finally
            {
                File.Delete(tmp);
                File.Delete(tmp + "-wal");
                File.Delete(tmp + "-shm");
            }
        }
        if (Held(to) || Held(to + "-wal") || !Open(to, out var t)) return false;
        try
        {
            // no busy timeout: a game that opened it meanwhile keeps it, the dbs wait for the next try
            if (!Exec(t, $"attach database {Quote(from)} as s")) return false;
            var schema = Text(t, string.Format(SchemaSql, "main"));
            if (schema == null || schema != Text(t, string.Format(SchemaSql, "s"))) return false;
            if (schema.Contains("compiler_id:") && (Text(t, string.Format(CompilerSql, "main")) is not { Length: > 0 } mine || mine != Text(t, string.Format(CompilerSql, "s")))) return false;
            if (!Exec(t, "begin immediate")) return false;
            if (Text(t, "pragma main.quick_check") != "ok" || Text(t, $"select count(*) from main.app_id where exe_path is not {Quote(exe)}") != "0")
            {
                Exec(t, "rollback");
                return false;
            }
            // groups stay out: no driver writes them yet
            if (Exec(t, "insert into main.keys(key, last_accessed) select key, last_accessed from s.keys where key not in (select key from main.keys)")
                && Exec(t, "insert into main.vals(keyid, type, value) select m.keyid, v.type, v.value from s.keys k join s.vals v on v.keyid = k.keyid "
                    + "join main.keys m on m.key = k.key where not exists (select 1 from main.vals x where x.keyid = m.keyid and x.type = v.type)")
                && Exec(t, "commit")) return true;
            Exec(t, "rollback");
            return false;
        }
        finally { sqlite3_close_v2(t); }
    }

    /// <summary>Every app_id row names <paramref name="exe"/>, and there is one.</summary>
    static bool NameFor(nint db, string exe) => Exec(db, $"update app_id set exe_path = {Quote(exe)}")
        && Text(db, $"select count(*) from app_id where exe_path is not {Quote(exe)}") == "0" && Text(db, "select count(*) from app_id") is { } n && n != "0";

    /// <summary>The db or its -wal or -shm is a link: SQLite would follow it out of the folder.</summary>
    static bool LinkedDb(string file) => new[] { file, file + "-wal", file + "-shm" }.Any(f => File.Exists(f) && new FileInfo(f).Attributes.HasFlag(FileAttributes.ReparsePoint));

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
    [DllImport(Sqlite)] static extern nint sqlite3_column_text(nint stmt, int col);
    [DllImport(Sqlite)] static extern long sqlite3_column_int64(nint stmt, int col);
    [DllImport(Sqlite)] static extern int sqlite3_finalize(nint stmt);
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
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir)) + '\\';
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
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir)) + '\\';
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
        var bin = GameFiles.Find(g.InstallDir, "bin");
        var files = new List<string>();
        if (DawnFiles.All(f => File.Exists(GameFiles.Find(bin, f)))) files.Add(GameFiles.Find(bin, "pipelineLibrary.cache"));
        if (engine?.Family == ReEngine.ReEngineReader.Family) files.AddRange(ReFiles.Select(f => GameFiles.Find(Path.GetDirectoryName(g.ExePath)!, f)));
        var exe = Path.GetFileName(g.ExePath);
        if (engine?.Family == Rage.RageReader.Family && exe.Equals("GTA5_Enhanced.exe", StringComparison.OrdinalIgnoreCase)
            && Path.Combine(documents, "Rockstar Games", "GTAV Enhanced", "Settings") is var settings && Directory.Exists(settings))
            files.AddRange(Directory.EnumerateFiles(settings, "*.d3d12PipelineCacheWindows", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }));
        if (engine is { Family: Nixxes.NixxesReader.Family, Fork: Nixxes.NixxesReader.Stingray } && exe.Equals("helldivers2.exe", StringComparison.OrdinalIgnoreCase))
            files.Add(Path.Combine(roamingAppData, "Arrowhead", "Helldivers2", "shader_cache", "pipeline_library.cache"));
        return files.Where(File.Exists).ToList();
    }
}
