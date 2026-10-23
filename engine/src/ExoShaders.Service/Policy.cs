// Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 The SCSKiller authors
// Copyright (C) 2026 Exo Shaders contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
// version, with the additional permission in LICENSE-EXCEPTION.txt. It is distributed WITHOUT ANY WARRANTY; see LICENSE.
//
// The settings policy: Exo Shaders has no settings. One fixed set of values, the same rules on every PC, is applied
// through the engine's own setters (so their side effects run: the driver-update task, recorder reconciliation, the
// re-evaluation of every game) after the first scan and again whenever the settings or a game change. It only
// decides options: it never queues or starts a compile. Nothing runs in --safe (it would write the data folder).

using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace ExoShaders.Service;

internal sealed partial class Host
{
    /// <summary>
    /// Compile threads: three quarters of the hardware threads (at least one), so a compile never takes the whole CPU.
    /// The engine's own default is every thread (AppStore.DefaultSettings), which is what Exo Shaders avoids.
    /// </summary>
    public static int PolicyThreads(int cpus) => Math.Max(1, cpus - Math.Max(1, cpus / 4));

    /// <summary>
    /// The compile's memory budget: a quarter of the RAM, at least 4 GB, and never less than
    /// the engine's Auto (which gives a quarter from 64 GB of RAM since 1.3.0). Auto gives
    /// 2 GB up to 20 GB of RAM, but the compiler needs about 1.3 GB just to load
    /// (ARCHITECTURE.md, Memory budget), so on a 16 GB PC a 2 GB budget kept releasing
    /// parked state objects and rebuilding them (Little Nightmares peaked at 2.4 GB).
    /// Over the budget the compiler drops workers rather than growing, so it stays a cap.
    /// </summary>
    public static int PolicyMemoryGB() => Math.Max(Math.Clamp(ScsKiller.RamGB() / 4, 4, 16), ScsKiller.AutoCompileMemoryGB());

    /// <summary>The fixed settings, from what the engine has now (fields the policy doesn't own are kept).</summary>
    public Settings PolicySettings(Settings s)
    {
        int cpus = Environment.ProcessorCount;
        int threads = PolicyThreads(cpus);
        Settings defaults = AppStore.DefaultSettings;
        return s with
        {
            Threads = threads,
            // The engine's default for idle compiles (8), never more than the foreground ones.
            BackgroundThreads = Math.Min(defaults.BackgroundThreads, threads),
            Priority = WarmPriority.BelowNormal,
            PauseWhileGaming = true,
            MaxCompileMemoryGB = PolicyMemoryGB(),
            MaximumPlans = false,        // Maximum is decided per game (PolicyGame)
            CompileRayTracing = true,
            HddMode = false,             // never every drive: HddDrives names the hard disks the games are on
            HddDrives = HardDiskDrives(s.HddDrives),
            // 1.4.0-alpha.3: games never compiled compile while the PC is idle (the games Add all ready takes), paused
            // while a game runs like every other compile; Exo has no "start it yourself" step to wait for.
            CompileNewGames = true,
            OnDriverUpdate = DriverUpdateMode.WhenIdle,
            OnNewShaders = NewShadersMode.Notify,
            ScanAtStart = true,
            RecordAllGames = true,
            RecordAntiCheatGames = false,
            RecordingLimitMB = defaults.RecordingLimitMB,
            KeepCachesAside = true,      // AMD only in effect (ScsKiller checks AmdAppCache); the engine default elsewhere
            ExperimentalIntel = CompileGpuIsIntel(),
            Gpu = null,                  // automatic: GpuBackends.Pick, the adapter with the most video memory
            MaxCompileConfirmed = true,
            // beta.2: the anonymous daily check (a server call) is off until asked; Exo Shaders never runs it and never
            // asks (ActiveCheck.Asks is false once it's answered), and its client isn't wired (Host.Start).
            ActiveCheck = false,
            ActiveCheckAsked = true,
        };
    }

    /// <summary>
    /// HDD mode for exactly the drives the games are on that are hard disks (Windows says they incur a seek penalty):
    /// there a game's files are read when it compiles instead of at every scan, while games on an SSD are still
    /// checked at the scan. The same list as before keeps its instance, so the settings compare equal.
    /// </summary>
    private IReadOnlyList<string>? HardDiskDrives(IReadOnlyList<string>? had)
    {
        var roots = GameFiles.DriveRoots();
        List<string> drives = [.. _k.Games.Select(g => GameFiles.DriveOf(g.Game.InstallDir, roots)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(IsHardDisk).Order(StringComparer.OrdinalIgnoreCase)];
        if (drives.Count == 0) return had is { Count: > 0 } ? null : had;
        return had != null && had.SequenceEqual(drives, StringComparer.OrdinalIgnoreCase) ? had : drives;
    }

    /// <summary>A local drive ("D:\") on a disk that incurs a seek penalty (a spinning hard disk); SSDs, network shares
    /// and mount points that can't be asked read as not.</summary>
    private static bool IsHardDisk(string root)
    {
        if (root.Length != 3 || root[1] != ':') return false;
        using var volume = CreateFileW(@"\\.\" + root[..2], 0, 3, 0, 3, 0, 0);
        if (volume.IsInvalid) return false;
        var query = new StoragePropertyQuery { PropertyId = 7 };   // StorageDeviceSeekPenaltyProperty, PropertyStandardQuery
        return DeviceIoControl(volume, 0x2D1400, ref query, System.Runtime.InteropServices.Marshal.SizeOf<StoragePropertyQuery>(),
            out SeekPenalty answer, System.Runtime.InteropServices.Marshal.SizeOf<SeekPenalty>(), out _, 0) && answer.IncursSeekPenalty != 0;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct StoragePropertyQuery { public int PropertyId, QueryType; public byte Additional; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SeekPenalty { public int Version, Size; public byte IncursSeekPenalty; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, ref StoragePropertyQuery input, int inputSize,
        out SeekPenalty output, int outputSize, out int returned, nint overlapped);

    /// <summary>With no GPU chosen the engine compiles for the adapter with the most video memory; Intel needs the flag.</summary>
    private bool CompileGpuIsIntel()
    {
        try
        {
            IReadOnlyList<DxgiAdapter> adapters = _k.Adapters?.Invoke() ?? [];
            return GpuBackends.Pick(adapters, null)?.Gpu.Vendor == GpuVendor.Intel;
        }
        catch (Exception)
        {
            return _k.Settings.ExperimentalIntel;
        }
    }

    // =================================================================== applying it

    private int _policyPending;
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    private volatile bool _policyReady;

    /// <summary>Coalesced: a burst of game changes applies it once, half a second after the last request.</summary>
    public void RequestPolicy()
    {
        if (_opt.Safe || !_policyReady || Quitting) return;
        if (Interlocked.Exchange(ref _policyPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(500, _stopping.Token); }
            catch (OperationCanceledException) { return; }
            await _policyGate.WaitAsync();
            try
            {
                Interlocked.Exchange(ref _policyPending, 0);
                ApplyPolicy();
            }
            catch (Exception e)
            {
                _log.Report("[exo] applying the settings policy failed: " + e.Message);
            }
            finally
            {
                _policyGate.Release();
            }
        });
    }

    /// <summary>Called once the first scan is done; from then on every settings or game change re-applies it.</summary>
    private void StartPolicy()
    {
        if (_opt.Safe) return;
        _policyReady = true;
        RequestPolicy();
    }

    private void ApplyPolicy()
    {
        ScsKiller k = _k;
        Settings was = k.Settings;
        Settings s = PolicySettings(was);
        if (s != was)
        {
            _settingsGate.Wait();
            try
            {
                k.Settings = s;   // re-evaluates the games, reconciles recorders, re-detects the GPU as needed
                _blocked.Clear();
                if (s.OnDriverUpdate != was.OnDriverUpdate)
                {
                    try { k.ApplyDriverUpdateMode(); }
                    catch (Exception e) { _log.Report("[exo] driver-update task not registered: " + e.Message); }
                }
                if (NewShaders.Global(s) != NewShaders.Global(was)) RequestNewShadersCheck();
                _log.Report("[exo] settings policy applied: " + Changed(was, s));
            }
            finally
            {
                _settingsGate.Release();
            }
            MarkState();
            RequestCacheInfo(force: true);
        }

        string? current = k.Queue.FirstOrDefault(Format.Running)?.GameId;
        bool changed = false;
        foreach (GameState g in k.Games)
        {
            if (Quitting) return;
            if (g.Game.Id == current) continue;   // the engine refuses option changes during that game's compile
            foreach (var (what, run) in PolicyGame(g))
            {
                try { run(); changed = true; _log.Report($"[exo] settings policy, {g.Game.Name}: {what}"); }
                catch (Exception e) { _log.Report($"[exo] settings policy, {g.Game.Name}: {what} failed: {e.Message}"); }
            }
        }
        if (changed) MarkState();
    }

    /// <summary>One game's changes: what each is, and the engine call that makes it (each setter refreshes the game).</summary>
    private List<(string What, Action Do)> PolicyGame(GameState g)
    {
        ScsKiller k = _k;
        string id = g.Game.Id;
        var todo = new List<(string, Action)>();
        // Auto: the engine's detected API. A game that ships both ("D3D11 or D3D12") is planned for both
        // (PlanBuilder.Decide, Planner.Check); a DirectX 11 or 12 choice would plan one only.
        if (g.ApiOverride != null) todo.Add(($"API {g.ApiOverride} -> auto", () => k.SetApiOverride(id, null)));
        if (g.NewShadersOverride != null) todo.Add(($"new shaders {g.NewShadersOverride} -> global", () => k.SetNewShadersMode(id, null)));
        if (g.LeftOutOfAddAll) todo.Add(("left out of compile all ready -> no", () => k.SetGameLeftOutOfAddAll(id, false)));
        // Every shader the game can use: no compile size limit (1.4.0-alpha.3's per-game cap).
        if (g.CacheCapGB != null) todo.Add(("compile size limit -> none", () => k.SetGameCacheCap(id, null)));
        if (g.RayTracingOverride != null) todo.Add(($"ray tracing {g.RayTracingOverride} -> default (on)", () => k.SetGameRayTracing(id, null)));
        // A game whose recording was turned off records again (RecordAllGames); an explicit "on" stays. Anti-cheat
        // games are never recorded: the engine skips them (RecorderSkip "anti-cheat") and RecordAntiCheatGames is off.
        if (g.RecorderOverride == RecorderOverride.Off && !g.Playing && g.AntiCheat == AntiCheat.None
            && k.Store.LoadGame(id).Recorder == RecorderOverride.Off)   // the setter doesn't compare: don't repeat it
            todo.Add(("recorder off -> default", () => k.SetRecorderOverride(id, RecorderOverride.Default)));
        // AMD: the engine offers the careful compile when a launch still compiled many shaders (IsPartlyWarmed).
        if (ScsKiller.CarefulThreads(k.Vendor.Vendor).HasValue && ScsKiller.IsPartlyWarmed(g) && g.Careful is { On: false })
            todo.Add(("careful compile -> on", () => k.SetCarefulCompile(id, on: true)));
        // Maximum where the engine's list suggests it for this GPU vendor ("Turn on for this game"), off elsewhere.
        bool maximum = MaximumMode.Current.For(g.Game, ScsKiller.PackGpu(k.Vendor.Vendor)) != null;
        if ((g.MaximumForGame == true) != maximum) todo.Add(($"maximum -> {(maximum ? "on" : "off")}", () => k.SetGameMaximum(id, maximum)));
        return todo;
    }

    /// <summary>policy.preview: what the policy would change now, without changing anything (works in --safe).</summary>
    public object PolicyPreview()
    {
        Settings was = _k.Settings;
        Settings s = PolicySettings(was);
        return new
        {
            applies = !_opt.Safe,
            settings = s == was ? "" : Changed(was, s),
            games = _k.Games.Select(g => new { id = g.Game.Id, name = g.Game.Name, api = g.Engine?.GraphicsApi, bothApis = ScsKiller.ApiKey(g.Engine?.GraphicsApi) == "11+12", changes = PolicyGame(g).Select(c => c.What).ToList() })
                .Where(x => x.changes.Count > 0 || x.bothApis).ToList(),
        };
    }

    // =================================================================== pause while gaming (or on battery)

    // The engine's PauseWhileGaming pauses only idle-time and background compiles (ScsKiller.Watch: `Background ||
    // _whenIdle`); a compile the user started keeps running while another game plays. Exo Shaders pauses every
    // compile while a library game runs, and resumes it when the last one exits. A pause or resume the user presses
    // wins until the games close.
    private readonly Lock _gamingGate = new();
    private bool _gamingPaused, _userResumed;

    private int _gamingPending;

    private Timer? _powerCheck;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>A laptop running on its battery (AC line 0). Desktops and unknown states read as plugged in.</summary>
    private static bool OnBattery() => GetSystemPowerStatus(out var p) && p.AcLineStatus == 0;

    /// <summary>Off the engine's event thread, coalesced: scans raise a GameChanged per game. Power is checked every 30 s.</summary>
    public void GamingPause()
    {
        if (_opt.Safe) return;
        // The 30 s power check only matters while a compile runs or waits on the charger.
        _powerCheck ??= new Timer(_ => { if (_k.Compiling || _gamingPaused) GamingPause(); }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        if (Interlocked.Exchange(ref _gamingPending, 1) == 1) return;
        _ = Task.Run(() =>
        {
            Interlocked.Exchange(ref _gamingPending, 0);
            try { GamingPauseNow(); }
            catch (Exception e) { _log.Report("[exo] pause while gaming: " + e.Message); }
        });
    }

    private void GamingPauseNow()
    {
        ScsKiller k = _k;
        bool playing = k.Games.Any(g => g.Playing);
        // A compile on battery drains it and heats a laptop: it waits for the charger like it waits for a game.
        bool battery = OnBattery();
        bool hold = playing || battery;
        bool pause = false, resume = false;
        using (_gamingGate.EnterScope())
        {
            if (!hold)
            {
                resume = _gamingPaused;
                _gamingPaused = _userResumed = false;
            }
            else if (!_gamingPaused && !_userResumed && k.Compiling && !Paused())
            {
                pause = _gamingPaused = true;
            }
        }
        if (pause)
        {
            k.PauseQueue();
            _log.Report(playing ? "[exo] compile paused while a game runs" : "[exo] compile paused: on battery");
        }
        else if (resume && Paused())
        {
            k.ResumeQueue();
            _log.Report("[exo] compile resumed: no game runs, plugged in");
        }
    }

    private bool Paused() => _k.Queue.FirstOrDefault(Format.Running) is { Stage: QueueStage.Paused };

    /// <summary>queue.pause / queue.resume from the page: the user's choice stands until the games close.</summary>
    public void UserPausedOrResumed(bool resumed)
    {
        using (_gamingGate.EnterScope())
        {
            if (resumed && _gamingPaused) _userResumed = true;
            _gamingPaused = false;
        }
    }

    private static string Changed(Settings a, Settings b)
    {
        var parts = new List<string>();
        foreach (var prop in typeof(Settings).GetProperties())
        {
            object? x = prop.GetValue(a), y = prop.GetValue(b);
            if (!Equals(x, y)) parts.Add($"{prop.Name} {x ?? "null"} -> {y ?? "null"}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>The page's read-only "How it works" facts.</summary>
    public object PolicyDto()
    {
        Settings s = _k.Settings;
        int cpus = Environment.ProcessorCount;
        int threads = PolicyThreads(cpus);
        return new
        {
            on = !_opt.Safe,
            summary = "Balanced settings sized to this PC. Pauses while you play or run on battery.",
            threads,
            processorCount = cpus,
            memoryGB = PolicyMemoryGB(),
            ramGB = ScsKiller.RamGB(),
            lines = new[]
            {
                $"Compiles with {threads} of {cpus} CPU threads at below-normal priority, and up to {PolicyMemoryGB()} GB of memory",
                "Pauses while you play or run on battery",
                "Games with both DirectX 11 and 12 are compiled for both",
                "Ray tracing shaders are included",
                "Rebuilds after a driver update when the PC is idle",
                "Tells you when a game update brings new shaders",
                "Records DirectX 12 games while you play, never anti-cheat games",
            },
        };
    }
}