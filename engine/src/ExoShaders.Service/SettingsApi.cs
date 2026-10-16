// Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 The SCSKiller authors
// Copyright (C) 2026 Exo Shaders contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
// version, with the additional permission in LICENSE-EXCEPTION.txt. It is distributed WITHOUT ANY WARRANTY; see LICENSE.
//
// settings.get / settings.set: the Settings fields the app's Settings and Queue pages expose (SettingsVm, QueueVm),
// with the same side effects. Server/Patreon settings (community database, sharing, update channel, active check)
// aren't exposed.

using System.Globalization;
using System.Text.Json;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace ExoShaders.Service;

internal sealed partial class Host
{
    public object SettingsDto()
    {
        Settings s = _k.Settings;
        IReadOnlyList<GpuChoice> choices = _k.GpuChoices;
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new
        {
            threads = s.Threads,
            priority = Fmt.Camel(s.Priority.ToString()),
            backgroundThreads = s.BackgroundThreads,
            pauseWhileGaming = s.PauseWhileGaming,
            maxCompileMemoryGB = s.MaxCompileMemoryGB,
            maximumPlans = s.MaximumPlans,
            compileRayTracing = s.CompileRayTracing,
            hddMode = s.HddMode,
            onDriverUpdate = Fmt.Camel(s.OnDriverUpdate.ToString()),
            onNewShaders = Fmt.Camel(NewShaders.Global(s).ToString()),
            scanAtStart = s.ScanAtStart,
            recordAllGames = s.RecordAllGames,
            recordAntiCheatGames = s.RecordAntiCheatGames,
            recordingLimitMB = s.RecordingLimitMB,
            keepCachesAside = s.KeepCachesAside,
            experimentalIntel = s.ExperimentalIntel,
            gpu = s.Gpu is { } g ? g.AdapterLuid.ToString("X16") : null,
            beforeAfter = s.BeforeAfter,
            beforeAfterSound = s.BeforeAfterSound,
            beforeAfterOverlay = s.BeforeAfterOverlay,
            theme = s.Theme switch { AppTheme.Light => "light", AppTheme.Dark => "dark", _ => "system" },
            startWithWindows = s.StartWithWindows,
            shareDiagnostics = s.ShareDiagnostics,
            meta = new
            {
                processorCount = Environment.ProcessorCount,
                autoCompileMemoryGB = ScsKiller.AutoCompileMemoryGB(),
                ramGB = ScsKiller.RamGB(),
                maxCompileMemoryGB = ScsKiller.MaxCompileMemoryGB(total),
                maxCompileMemoryText = s.MaxCompileMemoryGB > 0 ? $"{s.MaxCompileMemoryGB} GB" : $"Auto ({ScsKiller.AutoCompileMemoryGB()} GB)",
                maxCompileMemoryNote = $"Auto picks the best limit for this PC's memory ({ScsKiller.AutoCompileMemoryGB()} GB here, with {ScsKiller.RamGB()} GB of RAM). Set it lower to keep more memory free while compiling, higher to compile faster.",
                priorityNote = (s.Priority == WarmPriority.BelowNormal ? "Below-normal priority." : "Idle priority.") + " Fewer threads = slower compile, quieter PC.",
                recordingLimits = ScsKiller.RecordingLimits.Select(mb => new { mb, label = ScsKiller.LimitText(mb) }),
                newShadersLabels = Fmt.NewShadersLabels,
                gpuChoices = choices.Select(c => new { luid = c.AdapterLuid.ToString("X16"), name = c.Name, vendor = Fmt.VendorKey(c.Vendor) }),
                hasIntelGpu = _k.HasIntelGpu,
                cacheSizes = Sizes.Select((x, i) => new { index = i, label = x.Label }),
            },
        };
    }

    private static readonly HashSet<string> SettingKeys =
    [
        "threads", "priority", "backgroundThreads", "pauseWhileGaming", "maxCompileMemoryGB", "maximumPlans", "compileRayTracing",
        "hddMode", "onDriverUpdate", "onNewShaders", "scanAtStart", "recordAllGames", "recordAntiCheatGames", "recordingLimitMB",
        "keepCachesAside", "experimentalIntel", "gpu", "beforeAfter", "beforeAfterSound", "beforeAfterOverlay", "theme",
        "startWithWindows", "shareDiagnostics",
    ];

    private readonly SemaphoreSlim _settingsGate = new(1, 1);

    public async Task<object> SetSettings(Params p)
    {
        foreach (string name in p.Names())
            if (!SettingKeys.Contains(name)) throw new ServiceError($"unknown or read-only setting '{name}'");
        await _settingsGate.WaitAsync();
        try
        {
            Settings was = _k.Settings;
            Settings s = was;
            int cpus = Environment.ProcessorCount;
            if (p.Int("threads") is { } t) s = s with { Threads = Math.Clamp(t, 1, cpus) };
            if (p.Int("backgroundThreads") is { } bt) s = s with { BackgroundThreads = Math.Clamp(bt, 1, cpus) };
            if (p.Str("priority") is { } pr) s = s with { Priority = Enum<WarmPriority>(pr, "priority") };
            if (p.Bool("pauseWhileGaming") is { } pwg) s = s with { PauseWhileGaming = pwg };
            if (p.Int("maxCompileMemoryGB") is { } mem)
            {
                int max = ScsKiller.MaxCompileMemoryGB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
                if (mem < 0 || mem > max) throw new ServiceError($"maxCompileMemoryGB must be 0 (Auto) to {max}");
                s = s with { MaxCompileMemoryGB = mem == 1 ? 2 : mem };
            }
            if (p.Bool("maximumPlans") is { } mp) s = s with { MaximumPlans = mp };
            if (p.Bool("compileRayTracing") is { } crt) s = s with { CompileRayTracing = crt };
            if (p.Bool("hddMode") is { } hdd) s = s with { HddMode = hdd };
            if (p.Str("onDriverUpdate") is { } du) s = s with { OnDriverUpdate = Enum<DriverUpdateMode>(du, "onDriverUpdate") };
            if (p.Str("onNewShaders") is { } ns) s = s with { OnNewShaders = Enum<NewShadersMode>(ns, "onNewShaders") };
            if (p.Bool("scanAtStart") is { } sas) s = s with { ScanAtStart = sas };
            if (p.Bool("recordAllGames") is { } rag) s = s with { RecordAllGames = rag };
            if (p.Bool("recordAntiCheatGames") is { } rac) s = s with { RecordAntiCheatGames = rac };
            if (p.Int("recordingLimitMB") is { } lim)
            {
                if (!ScsKiller.RecordingLimits.Contains(lim)) throw new ServiceError("recordingLimitMB must be one of " + string.Join(", ", ScsKiller.RecordingLimits));
                s = s with { RecordingLimitMB = lim };
            }
            if (p.Bool("keepCachesAside") is { } kca) s = s with { KeepCachesAside = kca };
            if (p.Bool("experimentalIntel") is { } ei) s = s with { ExperimentalIntel = ei };
            if (p.Has("gpu"))
            {
                string? luid = p.Str("gpu");
                if (luid is null or "" or "auto") s = s with { Gpu = null };
                else
                {
                    GpuChoice c = _k.GpuChoices.FirstOrDefault(c => c.AdapterLuid.ToString("X16").Equals(luid, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ServiceError("no GPU with adapter LUID " + luid);
                    s = s with { Gpu = c };
                }
            }
            if (p.Bool("beforeAfter") is { } ba) s = s with { BeforeAfter = ba };
            if (p.Bool("beforeAfterSound") is { } bas) s = s with { BeforeAfterSound = bas };
            if (p.Bool("beforeAfterOverlay") is { } bao) s = s with { BeforeAfterOverlay = bao };
            if (p.Str("theme") is { } th)
                s = s with { Theme = th switch { "light" => AppTheme.Light, "dark" => AppTheme.Dark, "system" => null, _ => throw new ServiceError("theme must be system, light or dark") } };
            if (p.Bool("startWithWindows") is { } sww) s = s with { StartWithWindows = sww };
            if (p.Bool("shareDiagnostics") is { } sd) s = Diagnostics.Answered(s, sd);

            if (s == was) return SettingsDto();
            if (_opt.Safe && (s.RecordAllGames != was.RecordAllGames || s.RecordingLimitMB != was.RecordingLimitMB || s.OnDriverUpdate != was.OnDriverUpdate || s.RecordAntiCheatGames != was.RecordAntiCheatGames || s.KeepCachesAside != was.KeepCachesAside))
                throw new ServiceError("not available in safe mode");
            _k.Settings = s;   // the engine's setter re-evaluates games, reconciles recorders, re-detects the GPU
            _blocked.Clear();
            string? problem = null;
            if (s.OnDriverUpdate != was.OnDriverUpdate)
            {
                try { _k.ApplyDriverUpdateMode(); }
                catch (Exception e) { problem = "Couldn't update the sign-in check: " + e.Message; }
            }
            if (NewShaders.Global(s) != NewShaders.Global(was) && !_opt.Safe) RequestNewShadersCheck();
            MarkState();
            RequestCacheInfo(force: true);
            if (problem != null) throw new ServiceError(problem);
            return SettingsDto();
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private static T Enum<T>(string value, string name) where T : struct, System.Enum
    {
        if (System.Enum.TryParse(value, ignoreCase: true, out T r) && System.Enum.IsDefined(r) && !int.TryParse(value, out _)) return r;
        throw new ServiceError($"'{value}' isn't a valid {name}: " + string.Join(", ", System.Enum.GetNames<T>().Select(Fmt.Camel)));
    }
}
