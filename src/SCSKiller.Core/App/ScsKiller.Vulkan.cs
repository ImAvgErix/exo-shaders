using System.Security;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Vulkan;

namespace SCSKiller.Core.App;

// Vulkan games (Steam only for now): the recorder is a Vulkan layer (VkLayer_scskiller.dll) in the data folder, registered
// for the game's app id alone (VulkanLayer), with the same signed scskiller.ini next to the exe as the DirectX recorder's.
// What it records lands in the recorder folder's inbox for the exe (VulkanInboxDir) and is imported into the game's
// scskiller.foz; a plan replays it with fossilize-replay. Steam's own Fossilize pre-caching of the game is a source too.
public sealed partial class ScsKiller
{
    /// <summary>The Vulkan recorder's file name, next to the app's proxy and in each game's layer folder.</summary>
    public const string DllVulkan = "VkLayer_scskiller.dll";
    public const string VulkanRecorderMissing = "this version of SCSKiller can't record Vulkan games yet";
    /// <summary>The recorder skip of a Vulkan game outside Steam: the layer is registered for a Steam app id.</summary>
    public const string SkipVulkanSteamOnly = "Vulkan games are recorded only from Steam for now";
    /// <summary>The game page's recorder line for a Vulkan game.</summary>
    public const string VulkanRecorderNote = "Adds a Vulkan layer for this game and a small scskiller.ini next to it to catch anything the plan missed.";
    /// <summary>A Vulkan compile's pause while Steam's fossilize-replay runs (<see cref="SteamReplaying"/>).</summary>
    public const string SteamPrecachingNote = "stopped while Steam processes this game's shaders: it goes on after";
    public const string VulkanOtherNote = "another SCSKiller copy's Vulkan recorder is registered for this game, so this one isn't installed";

    /// <summary>The app's Vulkan layer, next to its proxy; null without one.</summary>
    string? VulkanLayerSource => _proxyDll is { } proxy ? Path.Combine(Path.GetDirectoryName(proxy)!, DllVulkan) : null;

    /// <summary>Steam's folder (<see cref="SteamSource.Root"/>), for its Fossilize filters; null: no Steam.</summary>
    internal string? SteamRoot { get; set; }

    /// <summary>The game's recorder is the Vulkan layer: a Steam game on Vulkan, on a driver whose Vulkan cache is known.</summary>
    bool VulkanRoute(Game g, EngineInfo? e) => VulkanLayer.AppId(g) != null && e != null && VulkanPlan.IsVulkan(e) && Vendor.Caps.VulkanKey != null;

    /// <summary>The record's Vulkan layer was installed for the game's exe folder.</summary>
    static bool VulkanIn(Game g, GameRecord rec) =>
        rec.VulkanLayer != null && rec.RecorderExe is { } exe && SameDir(Path.GetDirectoryName(exe)!, Path.GetDirectoryName(g.ExePath)!);

    /// <summary>This copy's layer manifest for a Steam app id, under the data folder.</summary>
    string VulkanManifest(string appId) => Path.Combine(Store.DataDir, "vulkan-layer", appId, VulkanLayer.ManifestName(appId));

    /// <summary>The recorder folder's inbox the layer writes the exe's recordings to; null when the exe's key can't be read.</summary>
    static string? VulkanInboxDir(Game g)
    {
        try { return VulkanLayer.Inbox(RecorderDir, FileKey(g.ExePath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Every inbox the game's layer wrote to: the ones on record (an earlier exe) and the current one.</summary>
    static List<string> VulkanInboxDirs(Game g, GameRecord rec) =>
        rec.VulkanInboxes.Append(VulkanInboxDir(g)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static IEnumerable<string> VulkanInboxes(Game g, GameRecord rec) =>
        VulkanInboxDirs(g, rec).Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "scskiller.*.foz"));

    /// <summary>The layer's recordings not imported yet and its session files: what a recording reset deletes too.</summary>
    IEnumerable<string> VulkanFiles(Game g)
    {
        var rec = Store.LoadGame(g.Id);
        return [.. VulkanInboxes(g, rec), .. VulkanInboxDirs(g, rec).SelectMany(d => SessionFiles.Select(f => Path.Combine(d, f))).Where(File.Exists)];
    }

    /// <summary>Takes the layer's recordings into the game's scskiller.foz and the last launch's cache redirection and device
    /// into its record; an inbox of an earlier exe that is empty goes. True when pipelines were recorded.</summary>
    bool ImportVulkan(Game g, GameRecord rec, CancellationToken ct)
    {
        if (Collections.RecordedBy(g.Id) != null || Foreign(g)) return false;
        var now = VulkanInboxDir(g);
        var dirs = VulkanInboxDirs(g, rec);
        var csv = dirs.Select(d => Path.Combine(d, "scskiller_creates.csv")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        Dictionary<string, string>? env = null;
        string? device = null;
        try
        {
            if (csv != null) (env, device) = (VulkanLayer.CacheEnv(csv), VulkanLayer.Device(csv) ?? rec.VulkanDevice);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { csv = null; }
        if (csv != null && (!SameEnv(env, rec.VulkanCacheEnv) || device != rec.VulkanDevice))
        {
            var moved = !SameEnv(env, rec.VulkanCacheEnv);
            (rec.VulkanCacheEnv, rec.VulkanDevice) = (env, device);
            if (Vendor.Caps.VulkanKey == "device") rec.RecordingImportedAt = DateTimeOffset.Now;   // the plan is for the device: checked again
            Store.SaveGame(g.Id, rec);
            if (moved)
                Log?.Report($"{g.Name}: " + (env == null ? "the last launch's Vulkan cache isn't redirected" : "the last launch's Vulkan cache is redirected (Steam's shader pre-caching)"));
        }
        var added = new List<string>();
        if (dirs.Any(d => Directory.Exists(d) && Directory.EnumerateFiles(d, "scskiller.*.foz").Any()))
            using (Recordings.Lock(RecordingPath(g.Id), null, ct))
                foreach (var d in dirs) added.AddRange(VulkanPlan.Import(d, FozPath(g.Id), Log));
        foreach (var d in dirs.Where(d => !d.Equals(now, StringComparison.OrdinalIgnoreCase)))
            try
            {
                if (Directory.Exists(d) && Directory.EnumerateFiles(d, "scskiller.*.foz").Any()) continue;
                if (Directory.Exists(d)) Directory.Delete(d, true);
                rec.VulkanInboxes.Remove(d);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // tried again at the next import
        if (added.Count == 0) return false;
        rec.RecordingImportedAt = DateTimeOffset.Now;
        Log?.Report($"{g.Name}: {added.Count:N0} Vulkan pipelines recorded");
        return true;
    }

    static bool SameEnv(Dictionary<string, string>? a, Dictionary<string, string>? b) =>
        a == null ? b == null : b != null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Steam's fossilize-replay runs: Steam processes a game's shaders, and a Vulkan compile or set-aside waits for it.</summary>
    bool SteamReplaying() => Processes(true).Any(p => Path.GetFileName(p.Exe) is var name
        && (name.Equals("fossilize-replay64.exe", StringComparison.OrdinalIgnoreCase) || name.Equals(Replayer, StringComparison.OrdinalIgnoreCase)));

    const string Replayer = "fossilize-replay.exe";

    /// <summary><see cref="Reconcile"/> for a game whose recorder is or was the Vulkan layer: installed, updated or taken out as
    /// the game's switch and state say, never while it runs, and after a full anti-cheat check of the folder. A DirectX
    /// recorder in its folder goes first. True when something changed.</summary>
    bool ReconcileVulkan(GameState s, GameRecord rec, bool changed)
    {
        var (g, id) = (s.Game, s.Game.Id);
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var appId = VulkanLayer.AppId(g);
        if (rec.VulkanLayer != null && !OursUnder(rec.VulkanLayer, Store))   // not this copy's: never touched
        {
            rec.VulkanLayer = null;
            Store.SaveGame(id, rec);
        }
        var manifest = rec.VulkanLayer ?? (appId == null ? "" : VulkanManifest(appId));
        var unread = ProxyNames.Select(n => Path.Combine(dir, n))
            .Concat(rec.VulkanLayer != null ? [manifest, Path.Combine(Path.GetDirectoryName(manifest)!, DllVulkan)] : [])
            .FirstOrDefault(CantRead);
        string? stamp = null, note = null;
        long gen = 0;
        var clean = false;
        if (s.AntiCheat == AntiCheat.None && (rec.VulkanLayer != null || rec.RecorderExe != null || unread != null))
        {
            (stamp, gen) = (Stamp(ArmTarget(g).ExePath), RemovalGen(id));   // before the check, as Attest needs
            if (FullCheck(g) is not AntiCheat.None and var found)
            {
                AntiCheatFound(g, found ?? AntiCheat.Other, unfinished: found == null);
                Refresh(g);
                return true;
            }
            clean = true;
        }
        if (unread != null)   // a file held by another process: nothing is judged from it
        {
            note = $"waits until {unread} can be read";
            if (note != _recorderNotes.GetValueOrDefault(id))
            {
                _recorderNotes[id] = note;
                changed = true;
                RecorderLog($"{g.Name}: recorder {note}");
            }
            if (changed) Refresh(g);
            return changed;
        }
        var want = VulkanRoute(g, s.Engine) && RecorderWanted(rec.Recorder ?? RecorderOverride.Default, Settings.RecordAllGames && s.NoStutter == null, RecorderSkip(s, null), false);
        if (want && VulkanLayer.Others(appId!, manifest).Count > 0)
            (want, note) = (false, VulkanOtherNote);
        var have = rec.VulkanLayer != null;
        var stale = want && have && (VulkanLayerSource is { } source && File.Exists(source) && !SameBytes(Path.Combine(Path.GetDirectoryName(manifest)!, DllVulkan), source)
            || !File.Exists(manifest) || !VulkanLayer.Registered(manifest) || !VulkanIn(g, rec));
        var directX = rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || OurProxyIn(dir);
        if (want != have || stale || directX || !want && rec.RecorderExe != null)
        {
            if (GameRunning(g)) note ??= stale ? "updates when the game exits" : want ? "installs when the game exits" : "removed when the game exits";
            else
                try
                {
                    if (directX && want && !Uninstall(g, rec)) throw new InvalidOperationException("the DirectX recorder in its folder couldn't be taken out yet");
                    if (want)
                    {
                        (stamp, gen) = (Stamp(ArmTarget(g).ExePath), RemovalGen(id));
                        InstallVulkan(g, rec, manifest);
                        if (!Attest(ArmTarget(g), rec, stamp, gen)) note = ForeignIni(dir, rec);
                    }
                    else Uninstall(g, rec);
                    _recorderSkips.TryRemove(id, out _);
                    RecorderLog($"{g.Name}: Vulkan recorder {(stale ? "updated" : want ? "installed" : "removed")} ({dir})");
                    changed = true;
                }
                catch (UnauthorizedAccessException)
                {
                    if (_recorderSkips.GetValueOrDefault(id) != SkipNeedsAdmin)
                    {
                        _recorderSkips[id] = SkipNeedsAdmin;
                        RecorderLog($"{g.Name}: Vulkan recorder not installed: {SkipNeedsAdmin} ({dir})");
                        changed = true;
                    }
                }
                catch (RemovedFromAllException e) { note = e.Message; }
                catch (Exception e) when (e is IOException or InvalidOperationException or SecurityException)
                {
                    note = (want ? "couldn't install: " : "couldn't remove: ") + e.Message;
                    RecorderLog($"{g.Name}: Vulkan recorder {note}");
                }
        }
        else if (want && !GameRunning(g) && _removedFromAll) note = RemovedFromAllNote;
        else if (want && !GameRunning(g))
        {
            if (clean && !File.Exists(Path.Combine(dir, "scskiller.ini"))) UpdateRecorderIni(g, rec, "");   // an ini the user deleted
            if (clean && !Attest(ArmTarget(g), rec, stamp, gen)) note = ForeignIni(dir, rec);
            else UpdateRecorderIni(g, rec);
        }
        if (want && VulkanInboxDir(g) is { } inbox && !rec.VulkanInboxes.Contains(inbox, StringComparer.OrdinalIgnoreCase))
        {
            rec.VulkanInboxes.Add(inbox);
            Store.SaveGame(id, rec);
        }
        if (note != _recorderNotes.GetValueOrDefault(id))
        {
            if (note == null) _recorderNotes.TryRemove(id, out _);
            else _recorderNotes[id] = note;
            changed = true;
        }
        if (changed) Refresh(g);
        return changed;
    }

    /// <summary>Copies the layer next to its manifest in the data folder, registers it for the game's app id and writes the
    /// signed scskiller.ini next to the exe; the record names them before the registration. Refused in an anti-cheat folder,
    /// before and after (a check that found one takes it all out again).</summary>
    void InstallVulkan(Game g, GameRecord rec, string manifest)
    {
        if (_removedFromAll) throw new RemovedFromAllException();
        if (!RecorderInstallAllowed(Store.DataDir, AppStore.Moved, Environment.GetEnvironmentVariable("SCSKILLER_TEST_RECORDER")))
            throw new InvalidOperationException("the data folder is moved (SCSKILLER_DATA_DIR), which SCSKiller's uninstall doesn't know: set SCSKILLER_TEST_RECORDER=1 to install a recorder anyway, and take it out yourself");
        RefuseAntiCheat(g, rec);
        if (!GameFolderWrite(g)) throw new InvalidOperationException($"{g.Name} is running");
        if (VulkanLayerSource is not { } source || !File.Exists(source)) throw new FileNotFoundException($"the Vulkan recorder ({DllVulkan}) was not found next to the app");
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var ini = Path.Combine(dir, "scskiller.ini");
        var (state, judged) = JudgeIni(dir, Store, null, g.InstallDir);
        if (state == IniState.Unknown) throw new IOException($"{ini} can't be read: not installing until it can");
        if (state is not (IniState.Absent or IniState.Ours)) throw new InvalidOperationException($"{ini} is not SCSKiller's: the Vulkan recorder needs its own. Not installing.");
        (rec.RecorderExe, rec.RecorderInstallDir) = (g.ExePath, g.InstallDir);
        StampLedger(dir);
        var layerDir = Directory.CreateDirectory(Path.GetDirectoryName(manifest)!).FullName;
        var dll = Path.Combine(layerDir, DllVulkan);
        Place(dll, CopyOf(source), replace: true);
        Place(manifest, Text(VulkanLayer.Manifest(VulkanLayer.AppId(g)!, dll)), replace: true);
        (rec.VulkanLayer, rec.VulkanLayerSha) = (manifest, Sha256(dll));
        Store.SaveGame(g.Id, rec);   // on record before the registration: an uninstall finds it
        InstallStep?.Invoke("vulkan");
        VulkanLayer.Register(manifest);
        var late = false;
        var found = GameFiles.DetectAntiCheat(g, quick: true, late: () => late = true);
        if (found == AntiCheat.None && !WriteIni(dir, IniText(g, rec), judged, null, g.InstallDir, () => GameFolderWrite(g), step => { if (step == "saved") InstallStep?.Invoke("ini"); }, install: true)
            && !GameFolderWrite(g))
        {
            TakeOut(g, rec, "the game started while the Vulkan recorder was installed");
            throw new InvalidOperationException($"{g.Name} started while the recorder was installed");
        }
        if (found == AntiCheat.None) found = GameFiles.DetectAntiCheat(g, quick: false, late: () => late = true);
        if (found != AntiCheat.None)
        {
            AntiCheatFound(g, found, installing: rec, unfinished: late);
            throw AntiCheatRefusal(g, found, late);
        }
        lock (_scanLock) _scan?.Remove(g.Id);   // evaluated again with the layer in
    }

    /// <summary>Unregisters the record's layer and deletes its manifest and DLL when they are still the ones placed. A layer
    /// outside this copy's data folder isn't ours: only forgotten. False when the registration can't be taken out yet.</summary>
    static bool UnregisterVulkan(GameRecord rec, AppStore store, string name, Action<string> log)
    {
        if (rec.VulkanLayer is not { } manifest) return true;
        if (!OursUnder(manifest, store))
        {
            rec.VulkanLayer = null;
            return true;
        }
        try { VulkanLayer.Unregister(manifest); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            log($"{name}: couldn't unregister the Vulkan recorder ({e.Message}): tried again later");
            return false;
        }
        var (dir, sha) = (Path.GetDirectoryName(manifest)!, rec.VulkanLayerSha);
        (rec.VulkanLayer, rec.VulkanLayerSha) = (null, null);
        try
        {
            var dll = Path.Combine(dir, DllVulkan);
            if (File.Exists(dll) && sha != null && Sha256(dll) == sha) File.Delete(dll);
            var appId = Path.GetFileNameWithoutExtension(manifest)["VkLayer_scskiller_".Length..];
            if (File.Exists(manifest) && File.ReadAllText(manifest) == VulkanLayer.Manifest(appId, dll)) File.Delete(manifest);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }   // unregistered: what's left loads nowhere
        return true;
    }

    /// <summary>Every registration of this copy's layers: one no record names, or whose game folder is gone, is taken out; a
    /// game not listed (or not read yet) gets its VAC and anti-cheat check here, which takes its layer out on a find.</summary>
    void SweepVulkan()
    {
        var unlisted = new List<Game>();
        lock (_recorderLock)
        {
            List<string> manifests;
            try { manifests = VulkanLayer.Under(Path.Combine(Store.DataDir, "vulkan-layer")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
            {
                RecorderLog($"the Vulkan recorders' registrations can't be read: {e.Message}");
                return;
            }
            foreach (var manifest in manifests)
                try
                {
                    var id = "steam:" + VulkanLayer.ManifestAppId(manifest);
                    if (OfflineLive(id)) continue;
                    var rec = Store.LoadGame(id);
                    if (rec.OfflineSession != null) continue;   // its cleanup's
                    var listed = Games.FirstOrDefault(s => s.Game.Id == id);
                    var name = listed?.Game.Name ?? id;
                    if (rec.VulkanLayer is not { } named || !named.Equals(manifest, StringComparison.OrdinalIgnoreCase))
                    {
                        if (UnregisterVulkan(new GameRecord { VulkanLayer = manifest }, Store, name, RecorderLog))
                            RecorderLog($"{name}: Vulkan recorder unregistered: no game record names it ({manifest})");
                        continue;
                    }
                    var dir = rec.RecorderExe is { } exe ? Path.GetDirectoryName(exe) : null;
                    if (dir == null || !Directory.Exists(dir))
                    {
                        var why = dir == null ? "its record names no game folder" : Gone(dir) ? $"its game folder {dir} is gone" : $"its game folder {dir} can't be reached";
                        if (dir == null) UnregisterVulkan(rec, Store, name, RecorderLog);
                        else RemoveRecorder(dir, rec, Store, name, RecorderLog, rec.RecorderInstallDir);
                        Store.SaveGame(id, rec);
                        if (rec.VulkanLayer == null) RecorderLog($"{name}: Vulkan recorder unregistered: {why}");
                    }
                    else if (listed == null || IsUnchecked(listed))
                        unlisted.Add(listed?.Game ?? new Game(id, name, Core.Store.Steam, rec.RecorderInstallDir ?? dir, rec.RecorderExe!));
                }
                catch (Exception e) { RecorderLog($"{manifest}: couldn't sweep the Vulkan recorder's registration: {e.Message}"); }
        }
        foreach (var g in unlisted)   // outside the lock: a full check reads the whole install
            try
            {
                if (VacCheck(g)) TakeOutNow(g, "VAC found");
                else if (FullCheck(g) is not AntiCheat.None and var found) AntiCheatFound(g, found ?? AntiCheat.Other, unfinished: found == null);
            }
            catch (Exception e) { RecorderLog($"{g.Name}: checking the Vulkan recorder's game for anti-cheat failed: {e.Message}"); }
    }

    /// <summary>A file that is there but can't be opened (held by another process).</summary>
    static bool CantRead(string path)
    {
        try
        {
            using (File.OpenRead(path)) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return File.Exists(path); }
    }

    /// <summary>A layer manifest under this copy's data folder: one SCSKiller placed and may take out.</summary>
    static bool OursUnder(string manifest, AppStore store)
    {
        try
        {
            return Path.GetFullPath(manifest).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(store.DataDir, "vulkan-layer"))) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>The environment a Vulkan warm runs in: the cache redirection the game's last launch had (Steam's pre-caching),
    /// the device on a driver keyed by it (AMD), or on NVIDIA, with no recording yet, Steam's own Vulkan cache of the game so
    /// the compile fills what Steam's launch reads. Null: the driver's default cache.</summary>
    Dictionary<string, string>? VulkanWarmEnv(Game g, GameRecord rec)
    {
        if (Vendor.Caps.VulkanKey == "device" && rec.VulkanDevice is { } device)
            return new(rec.VulkanCacheEnv ?? [], StringComparer.OrdinalIgnoreCase) { ["SCSKILLER_REPLAY_DEVICE"] = VulkanPlan.UnderSteamLayer(device) };
        if (rec.VulkanCacheEnv == null && OperatingSystem.IsWindows() && Vendor.Vendor == GpuVendor.Nvidia && !File.Exists(FozPath(g.Id))
            && SteamGlCache(g) is { } cache && Path.GetDirectoryName(cache) is { } dir && Directory.Exists(dir))
        {
            Log?.Report($"{g.Name}: the compile writes Steam's Vulkan cache of the game in {dir}");
            return new(StringComparer.OrdinalIgnoreCase)
            {
                ["__GL_SHADER_DISK_CACHE_PATH"] = dir,
                ["__GL_SHADER_DISK_CACHE_APP_NAME"] = SteamGlAppName,
                ["__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"] = "steam_shader_cache;steamapp_merged_shader_cache",
                ["__GL_SHADER_DISK_CACHE_SKIP_CLEANUP"] = "1",
            };
        }
        return rec.VulkanCacheEnv;
    }

    /// <summary>The game's cache on disk: its driver cache keys' files, and for a Vulkan game Steam's shader cache of it.</summary>
    long? CacheOnDiskOf(Game g, EngineInfo? engine, GameRecord rec)
    {
        var driver = rec.CacheKeys.Count > 0 && AppCache != null ? AppCache.SizeOf(rec.CacheKeys) : (long?)null;
        return SteamGlFiles(g, engine) is { Count: > 0 } steam ? (driver ?? 0) + steam.Sum(f => f.Length) : driver;
    }

    /// <summary>The files of Steam's Vulkan cache of a Vulkan game the driver writes (and Steam's own in it): never through a link.</summary>
    List<FileInfo> SteamGlFiles(Game g, EngineInfo? engine)
    {
        if (!VulkanRoute(g, engine) || SteamGlCache(g) is not { } dir || GameFiles.FinalPath(dir) is not { } final || !SamePath(final, dir)) return [];
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(f => f.Name.StartsWith(SteamGlAppName, StringComparison.OrdinalIgnoreCase) || AppCacheFiles.SteamOwn(f.Name)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    const string SteamGlAppName = "steamapp_shader_cache";

    /// <summary>The compile lock a clear of Steam's cache holds: none while the game's cache is set aside for its before
    /// and after recording, or a compile of it runs.</summary>
    FileStream SteamGlHeld(GameState s)
    {
        if (PairAsides().Any(a => a.PendingGame == s.Game.Id))
            throw new InvalidOperationException($"{s.Game.Name}'s cache is set aside for its before and after recording, end it first");
        return CompileLock(s.Game.Id) ?? throw new InvalidOperationException($"A compile of {s.Game.Name} is in progress");
    }

    /// <summary>Steam's Fossilize folder of a Steam game in its library (steamapps\shadercache\&lt;app id&gt;\fozpipelinesv6) and
    /// Steam's own folder; null for another store's game or without Steam.</summary>
    (string Dir, string Root)? SteamCache(Game g)
    {
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir)) + Path.DirectorySeparatorChar;
        var at = install.IndexOf(SteamSource.CommonPart, StringComparison.OrdinalIgnoreCase);
        return SteamRoot is { } root && SteamSource.AppId(g.Id) is { } appId && at >= 0 ? (SteamPipelineCache.Dir(install[..at], appId), root) : null;
    }
}
