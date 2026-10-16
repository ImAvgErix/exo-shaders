// Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 The SCSKiller authors
// Copyright (C) 2026 Exo Shaders contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
// version, with the additional permission in LICENSE-EXCEPTION.txt. It is distributed WITHOUT ANY WARRANTY; see LICENSE.
//
// Display helpers ported from the SCSKiller 1.3.0-beta.1 app (SCSKiller.App.Fmt, QueueVm), so the wording the
// service sends matches what the official app shows.

using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace ExoShaders.Service;

internal static class Fmt
{
    public static readonly string[] Stores =
    [
        "Steam", "Epic Games", "Xbox / Game Pass", "EA app", "GOG", "Ubisoft Connect", "Battle.net", "Amazon Games", "PURPLE", "HoYoPlay",
        "Gaijin", "Riot Client", "Other", "Added by you"
    ];

    public const string AddedByYou = "Added by you";
    public const string CompilingTip = "Compiling: play when it finishes";
    public const string IdleNote = "starts when the PC is idle";

    public static readonly string[] NewShadersLabels = ["Off", "Notify me", "Compile automatically and notify me", "Compile automatically, silently"];

    public static string N(long? n) => n?.ToString("N0") ?? "—";

    public static string When(DateTimeOffset at)
    {
        DateTime local = at.LocalDateTime;
        if (local.Date == DateTime.Today) return $"today at {local:t}";
        if (local.Date == DateTime.Today.AddDays(-1.0)) return $"yesterday at {local:t}";
        return $"on {local:d}";
    }

    public static string KeptSize(KeptCache k) =>
        Format.Bytes(k.Bytes) + " · " + (k.Frees > 0 ? "frees " + Format.Bytes(k.Frees) : "takes no extra space");

    public static string Vendor(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        _ => "GPU",
    };

    public static string VendorKey(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "nvidia",
        GpuVendor.Amd => "amd",
        GpuVendor.Intel => "intel",
        GpuVendor.Qualcomm => "qualcomm",
        _ => "other",
    };

    public static string Engine(EngineInfo? e) =>
        e is null ? "Unknown engine" : Format.Engine(e, e.Family == "Unreal" ? "UE" : null) + Guessed(e);

    public static string Guessed(EngineInfo e) => e.VersionGuessed ? " (guessed)" : "";

    public static string AntiCheatName(AntiCheat a) => a switch
    {
        AntiCheat.EasyAntiCheat => "EasyAntiCheat",
        AntiCheat.BattlEye => "BattlEye",
        _ => "anti-cheat",
    };

    public static string AntiCheatKey(AntiCheat a) => a switch
    {
        AntiCheat.None => "none",
        AntiCheat.EasyAntiCheat => "easyAntiCheat",
        AntiCheat.BattlEye => "battlEye",
        _ => "other",
    };

    public static string? StutterTip(GameState s)
    {
        KnownStutter? k = s.KnownStutter;
        if (k is null) return null;
        return $"{(k.Severity == StutterSeverity.Severe ? "Severe" : "Moderate")} shader-compilation stutter: {k.Reason}.\nSource: {k.Source} (checked {k.Date})";
    }

    /// <summary>Store labels in the app's order, then any other label (Exo's, from exo.library) by name.</summary>
    public static IEnumerable<string> OrderStores(IEnumerable<string> labels) =>
        labels.Distinct().OrderBy(n => Array.IndexOf(Stores, n) is var i and >= 0 ? i : Stores.Length).ThenBy(n => n, StringComparer.OrdinalIgnoreCase);

    public static string StoreName(Game g) => g.Store switch
    {
        Store.Steam => "Steam",
        Store.Epic => "Epic Games",
        Store.Xbox => "Xbox / Game Pass",
        Store.EA => "EA app",
        Store.Manual => AddedByYou,
        _ => g.Id[..Math.Max(0, g.Id.IndexOf(':'))] switch
        {
            "gog" => "GOG",
            "ubisoft" => "Ubisoft Connect",
            "battlenet" => "Battle.net",
            "purple" => "PURPLE",
            "hoyoplay" => "HoYoPlay",
            "gaijin" => "Gaijin",
            "riot" => "Riot Client",
            "amazon" => "Amazon Games",
            "exo" => ExoLibrarySource.LabelOf(g.Id) ?? "Exo Launcher",
            _ => "Other",
        },
    };

    public static string Cache(GameState s)
    {
        if (s.CacheOnDisk is long onDisk) return Format.Bytes(onDisk);
        if (s.EstimatedCacheBytes is long est) return "≈ " + Format.Bytes(est);
        return "—";
    }

    public static TimeSpan? CompileTime(GameState s) => s.LastWarmTime ?? s.EstimatedWarmTime;

    public static string TagTip(MiddlewareTag t) => Format.Middleware(t) + "\n    " + string.Join(", ", t.Dlls);

    /// <summary>Not finished (done, failed or stopped).</summary>
    public static bool Active(QueueItem q) => !(q.Stage is QueueStage.Done or QueueStage.Failed or QueueStage.Stopped);

    public static (QueueItem? Running, List<QueueItem> Waiting) RunOrder(IEnumerable<QueueItem> all)
    {
        List<QueueItem> list = all.Where(q => !q.PlanCheck).ToList();
        return (list.FirstOrDefault(Format.Running), list.Where(q => q.Stage == QueueStage.Waiting).ToList());
    }

    public static string Stage(QueueItem q)
    {
        switch (q.Stage)
        {
            case QueueStage.Indexing: return "Reading shaders";
            case QueueStage.Planning: return "Building plan";
            case QueueStage.Materializing: return "Preparing files";
            case QueueStage.Paused: return q.Note is { } n ? "Paused: " + n : "Paused";
            case QueueStage.Warming when q.Note is { } note: return "Compiling: " + note;
        }
        return "Compiling";
    }

    public static string? ProgressText(QueueItem q)
    {
        WarmProgress? p = q.Progress;
        if (p is null || p.Total <= 0) return null;
        string mem = ScsKiller.MemoryLimitNote(p) is { } m ? " · " + m : "";
        return $"{Percent(q):0.0}% · {p.Done:N0} of {p.Total:N0} {(q.Stage == QueueStage.Indexing ? "packages" : "pipelines")}"
            + (p.PerSecond > 0.0 && !ScsKiller.Stalled(q) ? $" · {p.PerSecond:N0}/s" : "")
            + (p.Failed > 0 ? $" · {p.Failed:N0} failed" : "")
            + (p.Skipped > 0 ? $" · {p.Skipped:N0} skipped" : "")
            + mem;
    }

    public static double Percent(QueueItem? q)
    {
        WarmProgress? p = q?.Progress;
        if (p is null || p.Total <= 0) return 0.0;
        return 100.0 * p.Done / p.Total;
    }

    public static bool Indeterminate(QueueItem? q) => q is not null && (q.Progress is null || q.Progress.Total <= 0);

    public static TimeSpan? Left(QueueItem q, GameState? g)
    {
        if (ScsKiller.Stalled(q)) return null;
        WarmProgress? p = q.Progress;
        if (p is null || !(p.PerSecond > 0.0)) return g is null ? null : g.EstimatedWarmTime ?? g.LastWarmTime;
        return TimeSpan.FromSeconds((p.Total - p.Done) / p.PerSecond);
    }

    public static bool CompilingSoon(IEnumerable<QueueItem> queue, bool queueRunning, string gameId)
    {
        List<QueueItem> list = queue.Where(q => !q.PlanCheck).ToList();
        if (list.Any(q => q.GameId == gameId && Format.Running(q))) return true;
        return queueRunning && list.FirstOrDefault(q => q.Stage == QueueStage.Waiting && q.Note != IdleNote)?.GameId == gameId;
    }

    public static string? Play(GameState s)
    {
        Game game = s.Game;
        if (StoreLaunch.OwnLauncherOnly(s)) return "Start " + game.Name + " from its own launcher";
        try
        {
            ProcessStartInfo? psi = StoreLaunch.Command(game);
            if (psi is null) return $"Couldn't find how {StoreName(game)} starts {game.Name}: refresh the library.";
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Couldn't start {game.Name}{(game.Store == Store.Manual ? "" : " through " + StoreName(game))}: {ex.Message}";
        }
    }

    public static string PlayVia(Game g)
    {
        if (g.Store == Store.Manual) return "Start " + Path.GetFileName(g.ExePath);
        if (Collections.Member(g.Id).HasValue)
            return $"Play through {StoreName(g)}, then pick {g.Name} in the game's menu if another game opens";
        return "Play through " + StoreName(g);
    }

    public static string Sentence(string t) => t.Length <= 0 ? t : char.ToUpperInvariant(t[0]) + t[1..];

    public static string Describe(Exception e)
    {
        while (e is AggregateException { InnerExceptions.Count: 1 } a) e = a.InnerExceptions[0];
        if (e is System.Reflection.TargetInvocationException { InnerException: { } inner }) e = inner;
        if (string.IsNullOrWhiteSpace(e.Message))
            return e.GetType().Name + (e.InnerException is { } i ? ": " + Describe(i) : $" (0x{e.HResult:X8})");
        return e.Message;
    }

    public static string Iso(DateTimeOffset d) => d.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);

    public static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
