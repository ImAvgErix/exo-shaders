// Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 The SCSKiller authors
// Copyright (C) 2026 Exo Shaders contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
// version, with the additional permission in LICENSE-EXCEPTION.txt. It is distributed WITHOUT ANY WARRANTY; see LICENSE.
//
// The `state` snapshot (CONTRACT.md): library (LibraryVm), queue (QueueVm), settings (SettingsVm), driver cache.

using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace ExoShaders.Service;

internal sealed partial class Host
{
    private LibrarySort? _sort;

    public LibrarySort Sort
    {
        get => _sort ??= _k.Store.LoadLibrarySort();
        set
        {
            _sort = value;
            _k.Store.SaveLibrarySort(value);
            MarkState();
        }
    }

    public object Snapshot() => Snapshot(null);

    /// <summary>With <paramref name="fixedRev"/> the snapshot carries that rev instead of taking the next one.</summary>
    public object Snapshot(long? fixedRev)
    {
        ScsKiller k = _k;
        IReadOnlyList<GameState> games = k.Games;
        IReadOnlyList<QueueItem> queue = k.Queue;
        ViewContext ctx = Context(games, queue);
        Settings settings = ctx.Settings;
        List<GameState> sorted = Library.Sorted(games, Sort).ToList();
        List<GameView> views = sorted.Select(g => new GameView(g, ctx)).ToList();
        List<QueueItem> jobs = queue.Where(q => !q.PlanCheck).ToList();
        HashSet<string> queued = jobs.Where(Fmt.Active).Select(q => q.GameId).ToHashSet();

        int readyStale = games.Count(g => g.Status is GameStatus.Ready or GameStatus.Stale);
        List<(string Name, int Games)> launchers = Fmt.OrderStores(views.Select(v => Fmt.StoreName(v.S.Game)))
            .Select(n => (n, views.Count(v => Fmt.StoreName(v.S.Game) == n))).ToList();
        List<string> stores = launchers.Select(l => l.Name).Where(n => n != Fmt.AddedByYou).ToList();
        string storeText = stores.Count > 2 ? $"{stores.Count} stores" : string.Join(" and ", stores);
        bool allUnreal = games.Count > 0 && games.All(g => g.Engine?.Family == "Unreal");
        string summary = Scanning && games.Count == 0
            ? "Looking for games…"
            : $"{games.Count} {(allUnreal ? "Unreal Engine " : "")}game{(games.Count == 1 ? "" : "s")} found{(storeText.Length > 0 ? " in " + storeText : "")} · {readyStale} ready to compile";
        double? rate = ScsKiller.MeasuredRate(games);
        string speed = (settings.Priority == WarmPriority.BelowNormal ? "Below-normal priority" : "Idle priority") + (rate is { } r ? $" · about {r:N0} pipelines/s measured" : "");

        QueueItem? running = queue.FirstOrDefault(Format.Running);
        string status = running is not null ? running.PlanCheck ? "checking" : running.Stage == QueueStage.Paused ? "paused" : "compiling"
            : Scanning ? "scanning" : "idle";
        string runningName = running is null ? "" : games.FirstOrDefault(g => g.Game.Id == running.GameId)?.Game.Name ?? running.GameId;

        IReadOnlyList<GameState> stale = SafeList(k.DriverStaleGames);
        bool notifyStale = stale.Count > 0 && SafeBool(k.ShouldNotifyStale);

        string? lastRebuilt = games.Where(g => g.WarmedAt.HasValue && g.WarmedDriverVersion != null).MaxBy(g => g.WarmedAt)?.WarmedDriverVersion;
        List<GameState> kept = games.Where(g => g.Kept != null).OrderByDescending(g => g.Kept!.Frees).ThenBy(g => g.Game.Name).ToList();
        List<GameState> recorded = games.Where(g => g.RecordingBytes > 0).OrderByDescending(g => g.RecordingBytes).ToList();

        long rev = fixedRev ?? Interlocked.Increment(ref _rev);
        return new
        {
            rev,
            app = new
            {
                status,
                statusLine = Format.TrayStatus(running, running is null ? null : runningName, Quitting).Line,
                scanning = Scanning,
                scanProgress = k.ScanProgress,
                afterUpdateLine = k.AfterUpdateLine,
                summary,
                readyCount = views.Count(v => ScsKiller.InAddAllReady(v.S) && !queued.Contains(v.S.Game.Id)),
                recommendedCount = views.Count(v => ctx.Recommended.Contains(v.S.Game.Id) && v.IsAdd && !queued.Contains(v.S.Game.Id)),
                waitingCount = jobs.Count(q => q.Stage == QueueStage.Waiting),
                speedNote = speed,
                librarySort = Fmt.Camel(Sort.ToString()),
                gpu = new
                {
                    name = k.Vendor.Gpu.Name,
                    vendor = Fmt.VendorKey(k.Vendor.Vendor),
                    vendorName = Fmt.Vendor(k.Vendor.Vendor),
                    driver = k.Vendor.Gpu.DriverVersion,
                    driverLine = k.GpuRestartNote ?? "Driver " + k.Vendor.Gpu.DriverVersion,
                    notice = Format.GpuNotice(k.Vendor.Gpu, settings.GpuNoticeDismissed, k.Vendor is IntelBackend),
                },
                cache = _cacheInfo,
                launchers = launchers.Select(l => new { name = l.Name, games = l.Games }),
                version = Program.ServiceVersion,
                engineVersion = AppVersion.Current.ToString(),
                driverStale = stale.Count > 0 ? new
                {
                    notify = notifyStale,
                    games = stale.Select(g => new { id = g.Game.Id, name = g.Game.Name }),
                    text = DriverStaleText(stale),
                } : null,
                driverMode = settings.OnDriverUpdate switch
                {
                    DriverUpdateMode.Ask => "Ask me first",
                    DriverUpdateMode.WhenIdle => "When the PC is idle",
                    _ => "Off",
                },
                driverModeNote = settings.OnDriverUpdate switch
                {
                    DriverUpdateMode.Ask => "A notification offers compile now, when idle, or skip",
                    DriverUpdateMode.WhenIdle => "Rebuilds on its own while you're away from the PC",
                    _ => "Rebuild games yourself from the Library",
                },
                lastRebuilt = lastRebuilt != null ? "Last rebuilt for driver " + lastRebuilt : null,
                kept = settings.KeepCachesAside && kept.Count > 0 ? new
                {
                    summary = $"{kept.Count} game{(kept.Count == 1 ? "" : "s")}, {Format.Bytes(kept.Sum(g => g.Kept!.Bytes))} kept aside · frees {Format.Bytes(kept.Sum(g => g.Kept!.Frees))}",
                    games = kept.Select(g => new { id = g.Game.Id, name = g.Game.Name, sizeText = Fmt.KeptSize(g.Kept!) }),
                } : null,
                recording = new
                {
                    count = RecordingCount(games),
                    space = recorded.Count == 0 ? "No recordings on disk" : $"Recordings use {Format.Bytes(recorded.Sum(g => g.RecordingBytes))} in {recorded.Count} game{(recorded.Count == 1 ? "" : "s")}",
                    games = recorded.Select(g => new { id = g.Game.Id, name = g.Game.Name, size = Format.Bytes(g.RecordingBytes), note = g.RecordingPaused ? ScsKiller.PausedNote(settings) : null }),
                },
            },
            games = views.Select(v => v.ToDto()),
            hidden = k.HiddenGames.Select(g => new { id = g.Id, name = g.Name, store = Fmt.StoreName(g) }),
            queue = QueueDto(games, queue),
            settings = SettingsDto(),
            policy = PolicyDto(),
            manualFolders = k.Manual?.Folders ?? [],
        };
    }

    private static IReadOnlyList<GameState> SafeList(Func<IReadOnlyList<GameState>> f)
    {
        try { return f(); } catch { return []; }
    }

    private static bool SafeBool(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }

    private static readonly HashSet<string> CountedSkips =
    [
        "anti-cheat", "an HDR mod changes every pipeline", "the game folder needs administrator", "a mod's DLL is in the recorder's place",
        "DLSS on NVIDIA loads DirectX 12 before the recorder can", "a mod's DLL is in the recorder's place and stops working renamed",
        "a DLL in the game's package folder loads instead of the recorder",
        "vkd3d-proton or DXVK runs the game on Vulkan, whose pipelines a DirectX warm doesn't compile",
    ];

    private static string RecordingCount(IReadOnlyList<GameState> games)
    {
        IEnumerable<string> skipped = games.Where(g => g.RecorderSkip is { } s && CountedSkips.Contains(s)).GroupBy(g => g.RecorderSkip)
            .OrderByDescending(x => x.Count()).Select(x => $" · {x.Count()} skipped: {x.Key}");
        int n = games.Count(g => g.RecorderInstalled);
        return $"Recording in {n} game{(n == 1 ? "" : "s")}" + string.Concat(skipped);
    }

    // =================================================================== queue (QueueVm)

    public object QueueDto(IReadOnlyList<GameState> games, IReadOnlyList<QueueItem> queue)
    {
        ScsKiller k = _k;
        Settings settings = k.Settings;
        List<QueueItem> list = queue.Where(q => !q.PlanCheck).ToList();
        Dictionary<string, GameState> byId = games.GroupBy(g => g.Game.Id).ToDictionary(g => g.Key, g => g.First());
        var (cur, waiting) = Fmt.RunOrder(list);
        TimeSpan? Left(QueueItem q) => Fmt.Left(q, byId.GetValueOrDefault(q.GameId));

        object Row(QueueItem q, int number)
        {
            GameState? g = byId.GetValueOrDefault(q.GameId);
            TimeSpan? left = q.Stage != QueueStage.Done ? Left(q) : null;
            string time = q.Stage == QueueStage.Done ? Format.Duration(g?.LastWarmTime)
                : left is { } l ? Format.Duration(l) + (q.Progress is { PerSecond: > 0.0 } ? " left" : "") : "—";
            WarmProgress? p = q.Progress;
            string? progressText = Fmt.ProgressText(q);
            return new
            {
                id = q.GameId,
                name = g?.Game.Name ?? q.GameId,
                number,
                stage = Fmt.Camel(q.Stage.ToString()),
                stageText = Fmt.Stage(q),
                detail = Format.Running(q) ? Fmt.Stage(q) + (progressText != null ? " · " + progressText : "") : null,
                progressText,
                percent = !Fmt.Indeterminate(q) && p != null ? Math.Round(Fmt.Percent(q), 1) : (double?)null,
                done = p?.Done,
                total = p?.Total,
                failed = p?.Failed,
                skipped = p?.Skipped,
                perSecond = p is { PerSecond: > 0.0 } ? Math.Round(p.PerSecond, 1) : (double?)null,
                time,
                timeLeftSeconds = left is { } ls && q.Progress is { PerSecond: > 0.0 } ? Math.Round(ls.TotalSeconds) : (double?)null,
                note = NoteOf(q, g),
                cache = g is null ? "—" : Fmt.Cache(g),
                whenIdle = q.Note == Fmt.IdleNote,
                error = q.Error,
            };
        }

        int first = cur is null ? 1 : 2;
        List<QueueItem> active = list.Where(Fmt.Active).ToList();
        TimeSpan total = TimeSpan.FromTicks(active.Sum(q => Left(q)?.Ticks ?? 0));
        bool plusPlans = active.Any(q => !Left(q).HasValue && !ScsKiller.Stalled(q));
        string summary = active.Count == 0 ? "Nothing queued"
            : $"{active.Count} game{(active.Count == 1 ? "" : "s")} · "
              + (total > TimeSpan.Zero ? "about " + Format.Duration(total) + " in total" + (plusPlans ? ", plus plans to build" : "") : "time known once the plans are built")
              + (cur is null && !k.QueueRunning ? " · not started" : "");

        List<GameState> queuedGames = active.Select(q => byId.GetValueOrDefault(q.GameId)).OfType<GameState>().ToList();
        string? cacheWarning = null;
        if (_cacheRaw is { } raw)
        {
            List<(long, long)> sizes = queuedGames.Select(g => (ScsKiller.CacheGrowth(g), g.EstimatedCacheBytes.GetValueOrDefault())).ToList();
            long planned = queuedGames.Sum(g => g.EstimatedCacheBytes.GetValueOrDefault());
            cacheWarning = raw.Capped is { } capped
                ? ScsKiller.LargeCompileWarning(sizes, k.Vendor.Vendor) ?? AmdAppCache.QueueWarning(capped, queuedGames.Select(g => (g.Game.Name, ScsKiller.CacheGrowth(g))).ToList())
                : ScsKiller.QueueCacheWarning(sizes, raw.Usage.BytesOnDisk, planned, raw.Limit?.Bytes, k.Vendor.Vendor);
        }
        bool paused = cur is { Stage: QueueStage.Paused };
        return new
        {
            running = k.QueueRunning,
            compiling = k.Compiling,
            paused,
            summary,
            planChecks = ScsKiller.PlanCheckLine(queue),
            cacheWarning = cacheWarning is { Length: > 0 } ? cacheWarning : null,
            cpuSummary = $"CPU use: {settings.Threads} of {Environment.ProcessorCount} threads, {(settings.Priority == WarmPriority.BelowNormal ? "below-normal" : "idle")} priority",
            pauseText = paused ? "Resume" : "Pause",
            canStart = !k.QueueRunning && waiting.Count > 0,
            canClear = waiting.Count > 0 || list.Any(q => !Fmt.Active(q)),
            current = cur is null ? null : Row(cur, 1),
            waiting = waiting.Select((q, i) => Row(q, first + i)).ToList(),
            finished = list.Where(q => !Fmt.Active(q)).Select(q => Row(q, 0)).ToList(),
        };
    }

    private static string NoteOf(QueueItem q, GameState? g)
    {
        switch (q.Stage)
        {
            case QueueStage.Failed:
                return "Failed: " + q.Error;
            case QueueStage.Stopped:
                if (q.Progress is { } p) return $"Stopped at {p.Done:N0} of {p.Total:N0}: add it again to continue" + (q.Note is { } n ? " · " + n : "");
                return "Stopped";
            case QueueStage.Done:
                return q.Note is { } d ? "Done · " + d : "Done";
            default:
                if (q.Note != null) return q.Note;
                return g?.Plan is { } plan
                    ? $"{plan.Recorded + plan.Generated + plan.MiddlewareItems:N0} pipelines{(plan.D3D11Shaders > 0 ? $" + {plan.D3D11Shaders:N0} DirectX 11 shaders" : "")}"
                    : "plan is built first";
        }
    }

    // =================================================================== driver cache (LibraryVm.ShowCache, SettingsVm)

    private sealed record CacheRaw(CacheUsage Usage, CacheLimit? Limit, long? Capped, long OldDriver, long? Free, string Drive, AutoShaderState? Auto, bool AutoRead);

    private CacheRaw? _cacheRaw;
    private object? _cacheInfo;
    private int _cacheReading, _cacheAgain, _cacheForce;
    private DateTime _cacheAt = DateTime.MinValue;

    public static readonly (string Label, CacheLimit Limit)[] Sizes =
    [
        ("16 GB (driver default)", new CacheLimit(17179869184L, IsDriverDefault: true)),
        ("32 GB", new CacheLimit(34359738368L, IsDriverDefault: false)),
        ("100 GB", new CacheLimit(107374182400L, IsDriverDefault: false)),
        ("Unlimited", new CacheLimit(null, IsDriverDefault: false)),
    ];

    public void RequestCacheInfo(bool force, bool readAuto = false)
    {
        if (!force && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(10)) return;
        if (readAuto) Interlocked.Exchange(ref _cacheForce, 1);
        if (Interlocked.Exchange(ref _cacheReading, 1) == 1)
        {
            Interlocked.Exchange(ref _cacheAgain, 1);
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _cacheAgain, 0);
                    bool auto = Interlocked.Exchange(ref _cacheForce, 0) == 1 || _cacheRaw is not { AutoRead: true };
                    ReadCache(auto);
                    _cacheAt = DateTime.UtcNow;
                    MarkState();
                }
                while (Interlocked.Exchange(ref _cacheAgain, 0) == 1);
            }
            catch (Exception e)
            {
                _log.Report("[exo] reading the driver cache failed: " + e.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _cacheReading, 0);
            }
        });
    }

    private void ReadCache(bool readAuto)
    {
        IGpuVendorBackend v = _k.Vendor;
        CacheUsage usage = v.GetCacheUsage();
        CacheLimit? limit = Try(v.GetCacheLimit);
        long? capped = (v as AmdBackend)?.AppCache.DxcBytes();
        long old = 0;
        try { old = _k.OldDriverBytes(); } catch (Exception e) { _log.Report("[exo] old driver caches: " + e.Message); }
        string drive = DriveOf(usage);
        long? free = null;
        try { free = new DriveInfo(drive).AvailableFreeSpace; } catch { }
        AutoShaderState? auto = _cacheRaw?.Auto;
        bool autoRead = _cacheRaw?.AutoRead ?? false;
        if (readAuto && v.Vendor == GpuVendor.Nvidia)
        {
            auto = Try(v.GetAutoShaderCompilation);
            autoRead = true;
        }
        var raw = new CacheRaw(usage, limit, capped, old, free, drive, auto, autoRead);
        _cacheRaw = raw;
        _cacheInfo = CacheDto(raw);
    }

    private static T? Try<T>(Func<T?> f) where T : class
    {
        try { return f(); } catch { return null; }
    }

    private static string DriveOf(CacheUsage? u)
    {
        string? path = u?.Path;
        string? root = Path.GetPathRoot(path is { Length: > 0 } ? GameFiles.FinalPath(path) ?? path : null);
        return root is { Length: > 0 } ? root : Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!;
    }

    private object CacheDto(CacheRaw c)
    {
        IGpuVendorBackend v = _k.Vendor;
        bool keptOn = v.Vendor == GpuVendor.Amd && _k.Settings.KeepCachesAside;
        string limitText = c.Limit is null ? "limit unknown"
            : c.Limit.Bytes is { } b ? c.Capped is { } cap ? $"· DirectX 12 {Format.Bytes(cap)} of {Format.Bytes(b)} limit" : "of " + Format.Bytes(b) + " limit"
            : "no limit";
        double percent = c.Limit?.Bytes is > 0 and var lb ? Math.Min(100.0, 100.0 * (c.Capped ?? c.Usage.BytesOnDisk) / lb) : 0.0;
        bool warn = c.Limit?.Bytes.HasValue == true && (c.Limit.IsDriverDefault || percent > 75.0);
        string limitCurrent = c.Limit is null ? "Unknown"
            : c.Limit.Bytes is { } lbytes ? lbytes != 0L ? Format.Bytes(lbytes) + (c.Limit.IsDriverDefault ? " (driver default)" : "") : "Disabled"
            : "Unlimited";
        string? notConfigurable = v.Caps.CacheSizeConfigurable ? null
            : v.Vendor == GpuVendor.Amd ? "The AMD driver's DirectX 12 shader cache is fixed at " + Format.Bytes(17179869184L) + ", with no size setting. Past it, the driver removes the least recently used games' caches when a game starts" + (keptOn ? ". Kept games are put back." : ", and SCSKiller shows those games as needing a compile again.")
            : v.Vendor == GpuVendor.Intel ? "The Intel driver keeps up to " + Format.Bytes(536870912L) + " of shaders per game, with no size setting. Past it, the driver drops that game's older shaders, so SCSKiller compiles the most likely ones first."
            : "SCSKiller can't change the cache size of " + Fmt.Vendor(v.Vendor) + " drivers yet, so it only shows what is used. If games get evicted, raise the limit in your GPU vendor's control panel.";
        string? auto = v.Vendor != GpuVendor.Nvidia || !c.AutoRead ? null
            : c.Auto is null ? "Couldn't read the setting"
            : c.Auto.Level == AutoShaderCompilation.Off ? "Now: Off"
            : c.Auto.TaskReady ? $"Now: On, {c.Auto.Level}" : "Setting on but the NVIDIA task isn't set up: click Apply to repair";
        return new
        {
            used = c.Usage.BytesOnDisk,
            usedText = (c.Usage.UpperBound ? "≤ " : "") + Format.Bytes(c.Usage.BytesOnDisk),
            upperBound = c.Usage.UpperBound,
            limit = c.Limit?.Bytes,
            limitText,
            limitCurrent,
            isDriverDefault = c.Limit?.IsDriverDefault ?? false,
            percent = Math.Round(percent, 1),
            warn = warn ? c.Limit!.IsDriverDefault ? "Driver default limit may evict games" : "The cache is nearly full" : null,
            configurable = v.Caps.CacheSizeConfigurable,
            largest = c.Limit is { Bytes: null },
            sizeIndex = Array.FindIndex(Sizes, s => !s.Limit.IsDriverDefault && s.Limit == c.Limit) is var i && i >= 0 ? i : (int?)null,
            notConfigurableText = notConfigurable,
            intro = (v.Vendor == GpuVendor.Nvidia ? "Global driver setting, the same one as NVIDIA App › Graphics › Shader Cache Size. " : "Global driver setting. ")
                + (keptOn ? "When the cache is full the driver deletes older entries. Kept games are put back." : "When the cache is full the driver deletes older entries, and those games stutter again."),
            amdDx12 = c.Capped is { } used ? new
            {
                used,
                text = "DirectX 12 cache: " + Format.Bytes(used) + " of " + Format.Bytes(17179869184L) + (used >= 15461882265L ? ". Nearly full: the driver removes the least recently used games' caches when a game starts." : ""),
                percent = Math.Round(Math.Min(100.0, 100.0 * used / 17179869184.0), 1),
            } : null,
            path = c.Usage.Path,
            free = c.Free,
            freeText = c.Free is { } f ? Format.Bytes(f) : "—",
            freeDrive = "Free on " + c.Drive.TrimEnd('\\'),
            oldDriver = c.OldDriver,
            oldDriverNote = c.OldDriver >= 1073741824L ? Format.Bytes(c.OldDriver) + " the new driver no longer uses" : null,
            autoShader = auto,
        };
    }
}
