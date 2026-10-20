// Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 The SCSKiller authors
// Copyright (C) 2026 Exo Shaders contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
// version, with the additional permission in LICENSE-EXCEPTION.txt. It is distributed WITHOUT ANY WARRANTY; see LICENSE.
//
// One game as the official app shows it: a port of SCSKiller.App.GameRow (library row) and DetailVm (game page) of
// 1.3.0-beta.1, without XAML. Community-database (Patreon) teasers are left out: this fork never offers them.

using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;

namespace ExoShaders.Service;

/// <summary>What a view of a game needs besides the game: read once per snapshot.</summary>
internal sealed class ViewContext
{
    public required Settings Settings { get; init; }
    public required IGpuVendorBackend Vendor { get; init; }
    public required IReadOnlyList<QueueItem> Queue { get; init; }
    public required bool QueueRunning { get; init; }
    public required IReadOnlyList<GameState> Games { get; init; }
    public required Func<string, string?> Blocked { get; init; }        // ScsKiller.BeforeAfterBlocked, cached
    public required Func<string, long> OtherCacheBytes { get; init; }   // non-driver caches seen by game.caches
    public required Func<string, string?> Error { get; init; }          // last error of a per-game action
    public required ISet<string> Recommended { get; init; }
    public Func<string, string?> KeySource { get; init; } = _ => null;  // "public list" when the stored AES key was found by Exo Shaders, else null
    public long? CacheLimitBytes { get; init; }                          // the NVIDIA shader cache limit, when known (null = unlimited or unread)

    public bool AmdVendor => Vendor.Vendor == GpuVendor.Amd;
}

internal sealed class GameView(GameState state, ViewContext ctx)
{
    // Exo Shaders never offers the community database (a Patreon feature): the engine still reads the public manifest (for
    // the official recordings for everyone), so its "in the community database" facts are left out of every view, as the
    // app leaves them out for a user it shows the Patreon teaser instead.
    private readonly GameState s = state with { InCommunityDb = null, CommunityDbPsos = 0 };

    private enum LeftKind { AntiCheat, EngineSlots, UnknownSlots, NotSeen, Recording, PlayedClean, PlayedCompiles, NothingKnown, PlayOnly }

    public const string PartlyCompiledWhy = "SCSKiller compiled this game, but the graphics driver refused a large part of it. This happens when the driver or Windows can't build pipelines the way the game does. The refused ones are built by the game the first time it needs them, which is when they can stutter. SCSKiller compiles them again after a driver update.";
    public const string MaxCompileWhy = "Compiles every combination of this game's shaders, not just each one once. It takes longer and uses more disk space, but potentially fewer shaders compile while you play. Seems to help most on AMD.";
    public const string OfflineRisk = "Starts the game offline without EasyAntiCheat. SCSKiller removes its files when the game exits (after a crash: at the next logon). If any are left when you play online, you could be banned. Steam must be running. Don't uninstall SCSKiller mid-session.";
    private const string BaBusyTip = "A before and after recording is in progress";

    public GameState S => s;
    private string Id => s.Game.Id;
    private string Name => s.Game.Name;

    // ---------------------------------------------------------------- queue place

    private (QueueItem? Item, int Number) Place
    {
        get
        {
            var (running, waiting) = Fmt.RunOrder(ctx.Queue);
            if (running?.GameId == Id) return (running, 1);
            int i = waiting.FindIndex(q => q.GameId == Id);
            return i >= 0 ? (waiting[i], i + (running is null ? 1 : 2)) : (null, 0);
        }
    }

    private QueueItem? Running => Place.Item is { } q && Format.Running(q) ? q : null;
    private bool Queued => ctx.Queue.Any(q => q.GameId == Id && Fmt.Active(q) && !q.PlanCheck);
    private bool CompilingSoon => Fmt.CompilingSoon(ctx.Queue, ctx.QueueRunning, Id);

    // ---------------------------------------------------------------- GameRow

    private bool Partly => ScsKiller.IsPartlyWarmed(s);
    private bool PartlyCompiled => ScsKiller.IsPartlyCompiled(s);
    private bool Shared => Collections.RecordedBy(Id) != null;

    /// <summary>
    /// An encrypted game whose key the engine couldn't read from its files (its exe is
    /// protected, as Wuthering Waves' is): Exo Shaders calls it not supported instead of
    /// asking for a key. Reading the key from the running game would mean going around its
    /// anti-cheat, which Exo Shaders never does. Engine.Encrypted is only left set once the stored key, the exe scan
    /// and the community's public key list (UnrealKeys.Get) all failed to open the game's files.
    /// </summary>
    private bool KeyLocked => ScsKiller.NeedsAesKey(s);

    /// <summary>The game page's note when the AES key came from a public key list rather than the game's own files.</summary>
    public string? KeyNote => !KeyLocked && s.Engine is { Family: "Unreal" } && ctx.KeySource(Id) == SCSKiller.Core.Unreal.PublicKeys.SourceName
        ? "Key from the community's public list" : null;

    private const string KeyLockedReason = "Its game files are protected, so the key needed to read its shaders can't be found";

    public string StatusText
    {
        get
        {
            if (KeyLocked) return "Not supported";
            if (s.CompileUnreached) return "The compile didn't help";
            if (PartlyCompiled) return "Partly compiled";
            switch (s.Status)
            {
                case GameStatus.Warmed:
                    if (Partly) return "Partly warmed";
                    return s.Plan is { RecordedOnly: true } ? "Warmed (recorded pipelines only)" : "Warmed";
                case GameStatus.Ready:
                    return "Ready to compile";
            }
            if (ScsKiller.NeedsOfflineSession(s)) return "Needs an offline session";
            switch (s.Status)
            {
                case GameStatus.NeedsRecording:
                    if (ScsKiller.NeverRecorded(s)) return "Played, nothing recorded";
                    return ScsKiller.RecordedEnough(s) ? "Needs a recording" : "Needs a 5-min recording";
                case GameStatus.Stale:
                    return "Needs rebuilding";
            }
            if (s.StatusReason == "checking its files") return "Checking…";
            if (s.StatusReason == "not checked yet: open it to check") return "Not checked";
            if (s.StatusReason == "the compile can't reach this game: it reads its shaders from a different driver cache than the one SCSKiller fills. Clear its cache to free the space") return "Not compatible";
            if (s.ShaderModBlocks) return "Not compiled";
            if (s.Engine is { Encrypted: true }) return "Encrypted game files";
            return s.NotPlanned ? "Not supported" : "Not supported yet";
        }
    }

    public string FullNote
    {
        get
        {
            if (s.CompileUnreached || PartlyCompiled) return NoDbNote(s.StatusReason);
            if (s.Status == GameStatus.Warmed)
            {
                if (Partly) return $"driver {s.WarmedDriverVersion} · {s.Careful!.LaunchCompiled * 100.0:0}% still compiled at its first launch";
                string extra = ScsKiller.IsPartial(s.Plan) ? " · a recording compiles the rest"
                    : ScsKiller.RtAfterRecording(s) ? " · ray tracing needs a 5-min recording" : "";
                return "driver " + s.WarmedDriverVersion + extra;
            }
            if (!ScsKiller.NeedsOfflineSession(s) && s.Status == GameStatus.NeedsRecording)
            {
                if (s.AnyAntiCheat && !s.RiskEligible) return ScsKiller.AntiCheatName(s) + " blocks recording";
                if (s.RecordingPaused) return ScsKiller.PausedNote(ctx.Settings);
                if (ScsKiller.NeverRecorded(s)) return s.RecorderRefused ?? NoDbNote(s.StatusReason);
                if (s.RecorderInstalled && !ScsKiller.RecordedEnough(s)) return Format.RecorderOnNote(s);
            }
            return NoDbNote(s.StatusReason);
        }
    }

    public string? Note => Format.ShortNote(s) is { } n ? NoDbNote(n) : null;

    private string? ListNote => s.ShaderModLayer ? (s.Status == GameStatus.Warmed ? "Compiled" : "Compiles") + " through " + s.ShaderMod : Note;

    public string RowNote => s.Playing ? "Playing now" + (ListNote is { } l ? " · " + l : "") : ListNote ?? "";

    public string Tone
    {
        get
        {
            if (KeyLocked) return "muted";
            if (s.CompileUnreached || PartlyCompiled) return "warn";
            return s.Status switch
            {
                GameStatus.Warmed => Partly ? "warn" : "accent",
                GameStatus.Ready => "primary",
                GameStatus.Unsupported => "muted",
                _ => "warn",
            };
        }
    }

    public bool IsAdd => s.Status is GameStatus.Ready or GameStatus.Stale && !s.CompileUnreached;
    public bool IsRecord => s.Status == GameStatus.NeedsRecording && !s.RecorderInstalled && s.NoStutter == null && !ScsKiller.NeedsOfflineSession(s) && !s.RiskEligible && !Shared;
    public bool IsDetails =>
        s.Status == GameStatus.Warmed || ScsKiller.IsUnchecked(s) || s.CompileUnreached || ScsKiller.NeedsOfflineSession(s)
        || (s.Status == GameStatus.NeedsRecording && (s.RecorderInstalled || s.NoStutter != null || s.RiskEligible || Shared));
    public bool IsWhy => s.Status == GameStatus.Unsupported && !ScsKiller.IsUnchecked(s);
    public bool CanLaunch => s.AntiCheat == AntiCheat.None && !ScsKiller.IsUnchecked(s) && StoreLaunch.Supported(s.Game);

    public string Sub => Fmt.Engine(s.Engine) + " · " + Fmt.StoreName(s.Game);

    public List<object> Tags
    {
        get
        {
            IReadOnlyList<MiddlewareTag> mw = s.Middleware ?? [];
            (string Text, string Tip)? mod = ModChip(s);
            var all = new List<(string Text, string Tip)>();
            if (mod is { } m) all.Add(m);
            all.AddRange(mw.Select(t => (t.Label, Fmt.TagTip(t))));
            if (s.KnownStutter == null && s.NoStutter == null)
            {
                if (all.Count > 3)
                {
                    var cut = all.Take(3).ToList();
                    cut.Add(($"+{all.Count - 3}", string.Join("\n", all.Skip(3).Select(t => t.Tip))));
                    all = cut;
                }
            }
            else if (all.Count > 1)
            {
                all = [mod ?? ($"{mw.Count} upscalers", string.Join("\n", mw.Select(Fmt.TagTip)))];
            }
            return all.Select(t => (object)new { text = t.Text, tip = t.Tip }).ToList();
        }
    }

    internal static (string Text, string Tip)? ModChip(GameState s)
    {
        if (s.ShaderMod is { } mod)
            return (mod, s.ShaderModBlocks ? "Stops this game compiling" : s.ShaderModLayer ? "Compiled through your mod" : "A few modded shaders aren't precompiled");
        return s.ReShade ? ("ReShade", "Changes no pipelines") : null;
    }

    // ---------------------------------------------------------------- DetailVm: status

    private bool Partial => ScsKiller.IsPartial(s.Plan);
    private bool RtPartial => s.Status == GameStatus.NeedsRecording && ScsKiller.NeedsRtRecording(s);
    private bool RtAfter => ScsKiller.RtAfterRecording(s);
    private bool NoAntiCheat => !s.AnyAntiCheat;
    private bool CanRecord => NoAntiCheat && !s.RootUnconfirmed && !s.StreamlineFirst;

    public string StatusTitle
    {
        get
        {
            if (Running is { } r) return Fmt.Stage(r);
            var place = Place;
            return place.Item is not null ? $"In queue (position {place.Number})" : StatusText;
        }
    }

    public string StatusReason
    {
        get
        {
            if (KeyLocked) return KeyLockedReason;
            if (Running is { } r && Fmt.ProgressText(r) is { } text)
            {
                string left = r.Progress is { PerSecond: > 0.0 } && Fmt.Left(r, s) is { } t ? " · " + Format.Duration(t) + " left" : "";
                return text + left;
            }
            return GameReason;
        }
    }

    private string GameReason
    {
        get
        {
            string t;
            if (s.CompileUnreached)
                t = s.StatusReason + (s.WarmedAt.HasValue ? ". " + Fmt.Sentence("its compiled cache isn't used, clear it to free the space") : "");
            else if (PartlyCompiled)
                t = ScsKiller.RefusedNote(s) + ". An SCSKiller update could fix it";
            else
            {
                switch (s.Status)
                {
                    case GameStatus.Warmed:
                        if (Partly) { t = s.StatusReason; break; }
                        t = "compiled for " + (ScsKiller.BothApis(s.Plan) ? "DirectX 11 and DirectX 12, for " : "") + "driver " + s.WarmedDriverVersion
                            + (s.WarmedAt is { } at ? $" on {at.LocalDateTime:d}" : "")
                            + (RtAfter ? ". " + Fmt.Sentence("ray tracing needs a 5-min recording") : "");
                        break;
                    case GameStatus.Ready:
                        string head = s.CacheOnDisk > 0 ? "not fully compiled yet: " + Format.Bytes(s.CacheOnDisk) + " of it is in the driver cache"
                            : !s.LastWarmTime.HasValue ? "not compiled yet" : "its shader cache is empty: compile it again";
                        t = head + (Note != null ? ". " + Fmt.Sentence(NoDbNote(s.StatusReason)) : "");
                        break;
                    case GameStatus.NeedsRecording:
                        t = NoDbNote(FullNote);
                        break;
                    default:
                        t = s.Engine is { Encrypted: true } ? "needs the game's AES key" : NoDbNote(s.StatusReason);
                        break;
                }
            }
            // The DXVK line of the engine's note goes out on its own (dxvk), not in the status text.
            string mod = Format.ModNote(s);
            if (DxvkNote is { } dn) mod = mod.Replace("\n" + dn, "");
            return Fmt.Sentence(t) + (PartlyCompiled || s.CompileUnreached ? "." : "") + mod;
        }
    }

    /// <summary>The engine's reason without its community database clause (ScsKiller.DbNote, RtNote).</summary>
    private static string NoDbNote(string r) =>
        r.Replace("; " + ScsKiller.InDbSupportersNote, "").Replace("; " + ScsKiller.InDbNote, "").Replace("; not in the community database yet", "")
            .Replace(", or a community recording for this version", "");

    public bool CanCompile => s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale || RtPartial;

    private bool BaInProgressAny => s.BeforeAfter is { } b && b.Stage != BeforeAfterStage.Done;

    public bool CanAdd => !Queued && !BaInProgressAny;

    public bool OffersCareful => ScsKiller.IsPartlyWarmed(s) && s.Careful is { On: false };

    public string AddText => Queued ? "In queue" : OffersCareful ? "Compile carefully" : RtPartial ? "Compile without ray tracing (partial)" : "Compile";

    public string? GuessNote => s.Engine is { } e && s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale ? ScsKiller.GuessNote(e) : null;

    // ---------------------------------------------------------------- careful, kept, options

    public string CarefulNote
    {
        get
        {
            string text;
            if (s.Careful is { Recorded: 0L })
                text = "Compiles the pipelines recorded in the game in passes on few threads, so the driver keeps more of them. This game has no recording yet, so there is nothing to compile carefully.";
            else
            {
                text = $"Compiles the pipelines recorded in the game in passes on {ScsKiller.AmdCarefulThreads} threads, so the driver keeps more of them. The rest compile at full speed.";
                if (s.Careful?.Estimate is { } est)
                    text += " About " + Format.Duration(est) + (!s.Careful.On && Fmt.CompileTime(s) is { } ct ? " instead of " + Format.Duration(ct) + "." : ".");
            }
            string launch = s.Careful?.LaunchCompiled is { } lc ? $" After the last compile, the game's first launch still compiled {lc * 100.0:0}% of its pipelines." : "";
            return "Only for games whose compile fails. " + text + launch;
        }
    }

    public bool HasKept => s.Kept != null && ctx.AmdVendor && ctx.Settings.KeepCachesAside;

    public string KeptState
    {
        get
        {
            KeptCache? kept = s.Kept;
            if (kept is null) return "";
            if (kept.Unkept > 0)
            {
                int u = kept.Unkept;
                return $"{u} file{(u == 1 ? " was" : "s were")} in use by {kept.HeldBy ?? "another app"} and {(u == 1 ? "isn't" : "aren't")} kept aside.";
            }
            if (kept.Frees <= 0)
            {
                if (kept.PutBackAt is { } at)
                    return kept.Late ? "Put back " + Fmt.When(at) + ", after the game had started. Some shaders may have compiled again." : "Put back " + Fmt.When(at) + ", before the game started.";
                return "In the driver's cache now.";
            }
            return s.AntiCheat == AntiCheat.None ? "The driver removed part of it. Put back when you press Play." : "Put back within a few seconds after the game starts. Keep SCSKiller running.";
        }
    }

    public List<string> NewShadersChoices =>
        ["Use the global setting (" + Fmt.NewShadersLabels[(int)NewShaders.Global(ctx.Settings)] + ")", .. Fmt.NewShadersLabels];

    public int NewShadersIndex => s.NewShadersOverride is { } m ? (int)m + 1 : 0;

    public List<string> RayTracingChoices => ["Default (" + (ctx.Settings.CompileRayTracing ? "on" : "off") + ")", "On", "Off"];

    public int RayTracingIndex => s.RayTracingOverride switch { true => 1, false => 2, null => 0 };

    private long OtherBytes => ctx.OtherCacheBytes(Id);

    public bool HasCache => s.CacheOnDisk.HasValue || OtherBytes > 0 || s.Status is GameStatus.Warmed or GameStatus.Stale;

    public bool CanClearCache => HasCache && !BaInProgressAny;

    public string? KeptCacheNote =>
        (s.Status == GameStatus.Unsupported || s.NoStutter != null) && s.WarmedAt.HasValue && s.CacheOnDisk > 0
            ? $"SCSKiller compiled {DiskValue} of cache for this game before it was marked {(s.NoStutter != null ? "as having no shader stutter" : "not supported")}."
            : null;

    private bool MaximumSuggested => s.MaximumSuggestion != null;
    public bool HasMaximumLine => MaximumSuggested || (s.MaximumForGame == true && !ctx.Settings.MaximumPlans);
    public string MaximumLine => MaximumSuggested
        ? "Maximum mode helps this game: it also compiles every pairing of its shaders, so fewer compile while you play."
        : "Maximum mode is on for this game. Its compiles take longer and use more disk space.";
    public string? MaximumNote
    {
        get
        {
            string? a = s.MaximumSuggestion is { Length: > 0 } m ? m : null;
            string? b = MaximumSuggested && s.MaximumAddsBytes is { } adds
                ? $"On AMD it adds up to {Format.Bytes(adds)} to the driver's shared {Format.Bytes(17179869184L)} cache." : null;
            string text = string.Join(" ", new[] { a, b }.OfType<string>());
            return text.Length > 0 ? text : null;
        }
    }
    public string MaximumAction => MaximumSuggested ? "Turn on for this game" : "Turn off for this game";

    public bool ShowClearCache => HasCache || CanCompile;

    public string ClearCacheText => s.CacheOnDisk.HasValue ? "Clear cache (" + DiskValue + ")" : "Clear cache";

    public string ClearCacheTip => BaInProgressAny ? BaBusyTip
        : HasCache ? "Deletes this game's shader caches (the driver's and Windows', and the game's own if you choose) so its next run starts cold"
        : "SCSKiller learns this game's driver cache files the first time it compiles it or sees it running";

    public string EngineChip => s.Engine is { } e
        ? Format.Engine(e, e.Family == "Unreal" ? "Unreal Engine" : null) + Fmt.Guessed(e) + (e.Fork != null ? " · custom fork" : "")
        : "Unknown engine";

    public string ApiChip => s.Engine?.GraphicsApi.Replace("D3D", "DirectX ") ?? "";

    public bool ShowApiPicker => (s.ApiDetected != null || s.ApiOverride.HasValue) && s.Engine is { Unsupported: null, Encrypted: false };

    public bool CanPickApi => !ctx.Queue.Any(q => q.GameId == Id && Format.Running(q));

    public int ApiIndex => s.ApiOverride switch { DxApi.D3D11 => 1, DxApi.D3D12 => 2, _ => 0 };

    public string[] ApiItems
    {
        get
        {
            string auto = s.Engine is { } e && e.GraphicsApi.EndsWith(" (recording)", StringComparison.Ordinal)
                ? "Auto (" + Dx(e.GraphicsApi) + " from your recording)"
                : s.ApiDetected == "D3D11 or D3D12" ? "Auto (DirectX 11 and 12)"
                : s.ApiDetected is { } d ? "Auto (" + Dx(d) + ")" : "Auto";
            return [auto, "DirectX 11", "DirectX 12"];
        }
    }

    private static string Dx(string api) => api.Contains("D3D12") ? "DirectX 12" : "DirectX 11";

    private string RecorderDll => s.AgsHost ? "amd_ags_x64.dll" : s.Records11 ? "d3d11.dll" : "d3d12.dll";

    /// <summary>beta.2: a game with DXVK as its d3d11.dll or dxgi.dll runs on Vulkan; on NVIDIA the compile goes through DXVK's own d3d11.dll.</summary>
    public string? DxvkNote => s.Dxvk && s.Status != GameStatus.Unsupported ? (s.DxvkCompiled ? ScsKiller.DxvkCompiledNote : ScsKiller.DxvkNote) : null;

    /// <summary>beta.2: on NVIDIA, the compile warns when this game's shaders are more than the driver's shader cache limit.</summary>
    public string? CacheLimitWarning =>
        ctx.Vendor.Vendor == GpuVendor.Nvidia && ctx.CacheLimitBytes is { } limit && s.EstimatedCacheBytes is { } est && est > limit && s.Status != GameStatus.Unsupported
            ? ScsKiller.QueueCacheWarning([(ScsKiller.CacheGrowth(s), est)], 0L, 0L, limit, GpuVendor.Nvidia)
            : null;

    public string AntiCheatChip => NoAntiCheat ? "No anti-cheat" : ScsKiller.AntiCheatName(s);

    public string AntiCheatTip => NoAntiCheat ? "No anti-cheat found: this game can be recorded"
        : s.RiskEligible ? "This game contains " + ScsKiller.AntiCheatName(s) + ". The recorder runs only in a session you confirm, at your own risk."
        : ScsKiller.AntiCheatName(s) + " treats an extra " + RecorderDll + " as tampering: this game is never recorded";

    public static string RiskText(GameState g) =>
        "This game contains " + ScsKiller.AntiCheatName(g) + ". If you enable the recorder, it's at your own risk. SCSKiller isn't responsible if your account gets banned.";

    // ---------------------------------------------------------------- recorder

    private bool PairFramesOnly => s.Records11 && ScsKiller.Frames11(s.Engine, ctx.Vendor.Caps);

    public bool ShowOffline => s.OfflineEligible || s.RiskEligible;
    private bool Risk => s.RiskEligible;

    public bool RecordOn => ShowOffline ? s.OfflineRecord : !PairFramesOnly && s.RecorderEffective;

    public bool CanToggleRecord => ShowOffline ? !s.OfflineRunning : NoAntiCheat && s.RecorderSkip == null && !PairFramesOnly;

    public bool ShowUseDefault => s.RecorderOverride != RecorderOverride.Default && s.RecorderSkip == null && !PairFramesOnly && !s.RecorderForeign;

    public string UseDefaultText => "Use default (" + (ctx.Settings.RecordAllGames && s.NoStutter == null ? "on" : "off") + ")";

    public string RecordNote
    {
        get
        {
            if (ScsKiller.CrashNote(s.Game) is { } crash) return crash;
            string kept = ScsKiller.SessionKeptNote(s) is { } k ? " " + k : "";
            if (Risk) return "Each session is confirmed before it starts." + kept;
            if (ShowOffline) return ScsKiller.AntiCheatName(s) + " blocks the recorder, but you can enable it at your own risk with an offline session." + kept;
            if (s.RiskNoLaunch) return ScsKiller.NoLaunchNote(Fmt.StoreName(s.Game));
            if (!NoAntiCheat) return $"Not available: {ScsKiller.AntiCheatName(s)} treats an extra {RecorderDll} as tampering, so this game is compiled from its files only.";
            if (PairFramesOnly) return "Frame times only, for the before and after recording.";
            switch (s.RecorderSkip)
            {
                case "game folder not confirmed":
                    return "Not available until you confirm the game's folder (Game folder… above): SCSKiller checks all of it for anti-cheat before it records.";
                case "a mod's DLL is in the recorder's place and stops working renamed":
                    return "Not available: " + ScsKiller.NotChainableReason(s.RecorderMod, RecorderDll) + ".";
                case { } skip:
                    return "Not available: " + skip + ".";
            }
            return "Adds a small " + RecorderDll + " next to the game to catch anything the plan missed and time each frame, so this page shows what stuttered. Remove any time. If a game has anti-cheat SCSKiller didn't detect, don't turn this on."
                + (s.RecorderNote is { } n ? " (" + Fmt.Sentence(n) + ")" : "")
                + (s.RecorderRefused is { } r && (s.Status != GameStatus.NeedsRecording || !ScsKiller.NeverRecorded(s)) ? " " + r : "");
        }
    }

    public bool HasMod
    {
        get
        {
            if (s.RecorderMod == null) return false;
            return s.RecorderSkip is not ("not supported yet" or "not DirectX 12" or "anti-cheat" or "an HDR mod changes every pipeline"
                or "game folder not confirmed" or "DLSS on NVIDIA loads DirectX 12 before the recorder can" or "the recorder crashes this game, so it's off for now");
        }
    }

    public bool HasRecordTip => CanRecord && (s.Status == GameStatus.NeedsRecording || RtAfter) && !RecordOn && s.NoStutter == null;

    private bool Enough => ScsKiller.RecordedEnough(s);

    public string RecordTipTitle => Enough ? "Record more play" : "Record 5 minutes of play";

    public string RecordTip
    {
        get
        {
            if (!RtPartial && !RtAfter)
                return Enough ? Fmt.Sentence(NoDbNote(s.StatusReason)) + "." : TurnOnAndPlay + " as usual for about 5 minutes. SCSKiller learns this game's shader layout from it, then it can compile.";
            return TurnOnAndPlay + " with ray tracing on" + (Enough ? "" : " for about 5 minutes") + ". " + ScsKiller.RtWhy(ctx.Vendor.Caps, s.Engine)
                + (RtAfter ? " Everything else is compiled." : " Everything else compiles from the game files already.");
        }
    }

    private string TurnOnAndPlay => Collections.RecordedBy(Id) is { } by && Collections.Member(by) is { } m
        ? "Turn on recording on " + m.Game.Name + "'s page and play this game"
        : "Turn on recording and play";

    public string OfflineButtonText => Risk ? (s.OfflineRunning ? "Session running…" : "Record a session (at your own risk)")
        : s.OfflineRunning ? "Offline session running…" : "Record offline without EasyAntiCheat (at your own risk)";

    public string OfflineRiskText => Risk ? RiskText(s) : OfflineRisk;

    public bool CanStartOffline => s.OfflineRecord && !s.OfflineRunning && !s.Playing;

    // ---------------------------------------------------------------- before and after

    private BeforeAfterInfo? Ba => s.BeforeAfter;
    private string? Blocked => ctx.Settings.BeforeAfter ? ctx.Blocked(Id) : null;
    private (bool Shown, bool Enabled, string Note) BaStart => BeforeAfter.StartButton(ctx.Settings.BeforeAfter, Blocked);
    public bool BaVoided => Voided(Ba);
    public bool ShowBa => Ba != null ? ctx.Settings.BeforeAfter : BaStart.Shown;

    public static bool Voided(BeforeAfterInfo? i) =>
        i?.Problem is "The driver changed since the before recording, start over" or "The game was updated since the before recording, start over";

    public string BaText
    {
        get
        {
            BeforeAfterInfo? ba = Ba;
            if (ba is null) return "Before and after";
            if (BaVoided) return "Start over";
            switch (ba.Stage)
            {
                case BeforeAfterStage.BeforeArmed when ba.ClearPending:
                    return "Clear the cache and try again";
                case BeforeAfterStage.BeforeArmed:
                case BeforeAfterStage.AfterArmed:
                    return CanLaunch ? "Start " + Name : "Start " + Name + " as usual";
                case BeforeAfterStage.BeforePlaying:
                case BeforeAfterStage.AfterPlaying:
                    double left = ba.Minutes * 60 - ba.PlayedSeconds;
                    return left > 0.0 ? $"Before and after: playing, {(int)left / 60}:{(int)left % 60:00} left" : "Before and after: time's up, close the game";
                case BeforeAfterStage.Compiling:
                case BeforeAfterStage.AfterReady:
                    return "Continue with the after recording";
                default:
                    return "View before and after";
            }
        }
    }

    public bool BaEnabled
    {
        get
        {
            if (Ba is not { } ba) return BaStart.Enabled;
            if (BaVoided || ba.ClearPending) return !s.Playing;
            return ba.Stage switch
            {
                BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed => CanLaunch && !s.Playing,
                BeforeAfterStage.AfterReady => !s.Playing,
                BeforeAfterStage.Done => true,
                _ => false,
            };
        }
    }

    public string? BaTip
    {
        get
        {
            if (Ba is null) return Blocked ?? "Measure this game cold, compile it, measure it again, and get a report you can share";
            return Ba.Stage is BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed && !CanLaunch ? "Start " + Name + " as usual: SCSKiller sees it start" : null;
        }
    }

    public string BaNote
    {
        get
        {
            if (Ba is not { } ba) return BaStart.Note;
            if (ba.Problem is { } p) return p;
            return ba.Stage switch
            {
                BeforeAfterStage.BeforeArmed => $"Play {ba.Minutes} minutes from a save you can load again, then close the game",
                BeforeAfterStage.AfterArmed => $"Load the same save and play the same {ba.Minutes} minutes",
                BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying => "Close the game when the time is up",
                BeforeAfterStage.Compiling => "Waiting for the compile",
                BeforeAfterStage.AfterReady => "Compiled: next, the after run",
                _ => "",
            };
        }
    }

    public string BaChip
    {
        get
        {
            if (BaVoided) return "";
            return s.BeforeAfter?.Stage switch
            {
                BeforeAfterStage.AfterReady => "After run ready",
                BeforeAfterStage.Compiling => "Before and after: compiling",
                BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying => "Before and after: playing",
                _ => "",
            };
        }
    }

    public bool BaCompileAgain => Ba is { Stage: BeforeAfterStage.Compiling, Problem: not null } && !BaVoided && !Queued;

    public string HideNote => "It stays out of the list and out of automatic compiles. Its files, saves and compiled cache stay. Show hidden on the Library page brings it back."
        + (BaInProgressAny ? " The before and after recording in progress is cancelled." : "")
        + (s.RecorderInstalled ? " The recorder is turned off and leaves the game folder first." : "");

    // ---------------------------------------------------------------- coverage, compile card (detail)

    private PlanStats? P => s.Plan;
    private int? Pct => DriverChanged ? null : ScsKiller.ShownPercent(s);
    private bool DriverChanged => s.Status == GameStatus.Stale && s.StatusReason.StartsWith("driver changed:", StringComparison.Ordinal);
    private bool Dx11Only => P is { StageSets: 0L, Generated: 0L } p && p.D3D11Shaders > 0;
    private bool Compiled => s.WarmedAt.HasValue && s.Status != GameStatus.Stale;
    private bool LowCoverage => !s.CompileUnreached && (Pct is { } pct ? pct < 90 : Partial);

    public object Coverage
    {
        get
        {
            string headline;
            if (P is null) headline = "Known once its first compile starts";
            else if (s.CompileUnreached) headline = "Its compile isn't used by this game";
            else if (DriverChanged) headline = "Its last compile was for driver " + s.WarmedDriverVersion;
            else if (Dx11Only && (Pct is null or 100))
                headline = (Compiled ? "Covers" : "Will cover") + " every DirectX 11 shader in the game files"
                    + (Planner.D3D11CoversVs(ctx.Vendor.Caps) || P.D3D11Draws > 0 ? "" : " but the vertex shaders");
            else if (Pct is { } pct)
                headline = (Compiled || s.NewOnly ? "Covers" : s.WarmedAt.HasValue ? "Its last compile covered" : "Will cover")
                    + (pct == 100 ? " all the shader combinations found in this game" : $" about {pct}% of the shader combinations found in this game");
            else headline = "Measured again on the next compile";
            string value = Pct is { } v ? $"{v}%" : Dx11Only && !s.CompileUnreached ? "All" : "—";
            string label = s.CompileUnreached ? "not used" : Pct.HasValue || Dx11Only ? "covered" : "not yet";
            return new { value, label, headline, low = LowCoverage, good = Pct.HasValue && !LowCoverage, percent = Pct };
        }
    }

    public List<object> Sources
    {
        get
        {
            var rows = new List<object>();
            PlanStats? p = P;
            if (p is null) return rows;
            void Row(string label, string value, string? note = null) => rows.Add(new { label, value, note });
            if (p.Generated > 0) Row(ScsKiller.BothApis(p) ? "DirectX 12, from the game files" : "Found in the game files", Fmt.N(p.Generated));
            if (p.Recorded > 0) Row(RecordingSource, Fmt.N(p.Recorded), CommunityLine);
            if (p.CacheBudgetLeftOut is > 0 and var lo)
                Row(ScsKiller.CacheBudgetTitle(ctx.Vendor.Caps), Fmt.N(lo), ScsKiller.CacheBudgetNote(ctx.Vendor.Caps));
            if (s.Official is { } official)
                Row("Official recording", $"{official.Psos:N0} pipelines", $"Published by SCSKiller for this version, downloaded {official.DownloadedAt.LocalDateTime:d}");
            if (p.MiddlewareItems > 0)
                Row(p.MiddlewareSharedItems == 0L ? "Its upscalers, learned from recordings" : p.MiddlewareSharedItems == p.MiddlewareItems ? "Its upscalers, from shared packs" : "Its upscalers, from recordings and shared packs", Fmt.N(p.MiddlewareItems));
            if (p.D3D11Shaders > 0) Row(ScsKiller.BothApis(p) ? "DirectX 11, from the game files" : "DirectX 11 shaders", Fmt.N(p.D3D11Shaders));
            if (p.RtLibraries > 0 && s.RtSkipped) Row("Ray tracing", "skipped (your setting)");
            else if (p.RtLibraries > 0)
            {
                string value = s.Engine is { NoRayTracing: true } ? "off in this game"
                    : s.Engine is { NoRtPipelines: true } ? "inline, from the game files"
                    : p.RtUncovered == 0L ? "covered"
                    : s.RtToPlan ? "being checked"
                    : s.RtUnseen ? "not seen while recording"
                    : ScsKiller.RtInlineCovers(p) ? "inline ones covered"
                    : ScsKiller.NeedsRtRecording(s) ? (CanRecord ? "need a recording" : "not compiled")
                    : "mostly covered";
                Row("Ray-traced effects", value);
            }
            return rows;
        }
    }

    private CommunityInfo? UsedCommunity => s.Community is { OtherVersion: false } c ? c : null;

    private string RecordingSource
    {
        get
        {
            if (UsedCommunity == null && s.Official == null) return "Your play recordings";
            var parts = new List<string>();
            if ((UsedCommunity ?? s.Official)!.WithLocalRecording) parts.Add("your recording");
            if (UsedCommunity != null) parts.Add("the community database");
            if (s.Official != null) parts.Add("the official recording");
            string text = parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1];
            return char.ToUpperInvariant(text[0]) + text[1..];
        }
    }

    private string? CommunityLine
    {
        get
        {
            if (UsedCommunity is not { } c)
                return s.RecordingSharedAt is { } at ? $"Shared with the community database {at.LocalDateTime:d}" : null;
            return $"Community recording downloaded {c.DownloadedAt.LocalDateTime:d}" + (s.RecordingSharedAt is { } y ? $" · yours shared {y.LocalDateTime:d}" : "");
        }
    }

    public List<object> Details
    {
        get
        {
            var rows = new List<object>();
            void Row(string label, string value) => rows.Add(new { label, value });
            PlanStats? p = P;
            if (p != null) Row("Pipelines in the plan", Fmt.N(p.Recorded + p.Generated + p.MiddlewareItems));
            if (s.ShaderCount.HasValue) Row("Shaders in the game files", Fmt.N(s.ShaderCount));
            if (p is { StageSets: > 0 }) Row("Shader combinations found", Fmt.N(p.StageSets));
            if (p is { LeftOut: > 0 }) Row("Left out", ScsKiller.LeftOutValue(p));
            if (p is { StageSets: 0L, Uncovered: > 0 }) Row("Left out for shader slots SCSKiller can't rebuild", Fmt.N(p.Uncovered));
            if (p != null) { Row("Recorded pipelines", Fmt.N(p.Recorded)); Row("Built from the game files", Fmt.N(p.Generated)); }
            if (p is { MiddlewareItems: > 0 }) Row("Upscaler pipelines", Fmt.N(p.MiddlewareItems));
            if (p is { D3D11Shaders: > 0 }) Row("DirectX 11 shaders", Fmt.N(p.D3D11Shaders));
            if (p is { RtLibraries: > 0 })
                Row("Ray tracing shader libraries", $"{p.RtLibraries:N0}" + (s.Engine is { NoRtPipelines: true } ? " (not used by the game)" : s.RtSkipped ? " (skipped, your setting)" : p.RtUncovered > 0 ? $" ({p.RtUncovered:N0} not compiled)" : ""));
            if (p?.RtInline is > 0) Row("Shaders that trace rays inline", Fmt.N(p.RtInline));
            if (s.Community is { } c) Row("Downloaded community recording", c.OtherVersion ? "doesn't match your game's files" : $"{c.Psos:N0} pipelines");
            if (s.Official is { } o) Row("Official recording", $"{o.Psos:N0} pipelines");
            if (p != null) { Row("Synthesized pipeline templates", Fmt.N(p.SynthesizedTemplates)); Row("Shader layouts", $"{p.RootSignatures:N0} · {RootSigSource(p)}"); }
            if (p != null && p.ExactUnits + p.InferredUnits + p.GuessedUnits > 0) Row("Stage units exact / inferred / guessed", $"{p.ExactUnits:N0} / {p.InferredUnits:N0} / {p.GuessedUnits:N0}");
            if (p is { LayoutCoverage: > 0.0 }) Row("Vertex layouts from a recording", $"{p.LayoutCoverage:P1}");
            if (s.CacheOnDisk.HasValue || s.EstimatedCacheBytes.HasValue) Row("Driver cache", Fmt.Cache(s) + (s.CacheOnDisk.HasValue ? " measured" : " estimated"));
            return rows;
        }
    }

    private string RootSigSource(PlanStats p)
    {
        if (p.RootSigRuleVerified) return "tested on this engine version";
        if (p.RootSignatures == 0L) return "not needed (DirectX 11)";
        if (s.Engine?.Family == "Unreal") return p.Recorded <= 0 ? "not tested on this engine version yet" : "learned from recording";
        return p.Recorded <= 0 ? "shipped with the shaders" : "learned from recording";
    }

    private long Missing => P is { } p ? Math.Max(p.LeftOut, p.Uncovered) : 0L;
    private SessionStats? L => s.LastSession;
    private static long PlayCompiles(SessionStats l) => l.Compiles + l.StateObjectsCompiled;
    private bool Rt => ScsKiller.NeedsRtRecording(s);
    private bool RtInline => ScsKiller.RtInlineCovers(P);
    private bool GuessedStates => P is { } p && p.GuessedUnits > 0.1 * (p.ExactUnits + p.InferredUnits + p.GuessedUnits);

    private LeftKind LeftCase
    {
        get
        {
            if (!CanRecord) return LeftKind.AntiCheat;
            if (Missing > 0)
            {
                if (P!.Uncovered * 2 < Missing) return LeftKind.NotSeen;
                return P.Recorded != 0L ? LeftKind.UnknownSlots : LeftKind.EngineSlots;
            }
            if (RecordOn) return LeftKind.Recording;
            if (L is { } l) return PlayCompiles(l) != 0L ? LeftKind.PlayedCompiles : LeftKind.PlayedClean;
            return P is { Recorded: > 0 } ? LeftKind.NothingKnown : LeftKind.PlayOnly;
        }
    }

    private bool ShowLeftRecord =>
        !RecordOn && CanToggleRecord && !HasRecordTip && s.NoStutter == null
        && (LeftCase is LeftKind.EngineSlots or LeftKind.NotSeen or LeftKind.PlayedCompiles or LeftKind.PlayOnly || GuessedStates || Rt || s.RtUnseen || RtInline);

    // 1.3.0: with recording on, what's left from slots or stage sets no recording showed yet still says so (and how it ends)
    public object? Left
    {
        get
        {
            if (P is null || !(ShowLeftRecord || RecordOn && s.NoStutter == null && LeftCase is LeftKind.NotSeen or LeftKind.EngineSlots)) return null;
            string title = !Rt && !s.RtUnseen ? "Some shaders still compile while you play" : "Ray-traced effects aren't compiled yet";
            return new { title, text = LeftSentences() };
        }
    }

    private string LeftSentences()
    {
        string? rt;
        if (s.RtUnseen) rt = Fmt.Sentence(ScsKiller.RtUnseenNote) + ".";
        else if (RtInline) rt = Fmt.Sentence(ScsKiller.RtInlineNote) + ".";
        else if (!Rt) rt = null;
        else rt = CanRecord ? ScsKiller.RtNeedsRecording + "."
            : "Ray-traced effects aren't compiled: they need a recording, " + (s.RiskEligible ? ScsKiller.RiskNoRecording : !NoAntiCheat ? "which " + ScsKiller.AntiCheatName(s) + " blocks" : s.RootUnconfirmed ? ScsKiller.ManualNoRecording : ScsKiller.StreamlineNoRecording) + ".";
        LeftKind left = LeftCase;
        string? text = left switch
        {
            LeftKind.AntiCheat => s.RiskEligible ? "Anything else compiles while you play: a session on this page can record it, at your own risk."
                : !NoAntiCheat ? "Anything else compiles while you play: " + ScsKiller.AntiCheatName(s) + " blocks the recording that would find it."
                : s.RootUnconfirmed ? "Anything else compiles while you play: confirm the game's folder (Game folder… above) to record what's missing."
                : "Anything else compiles while you play: DLSS on NVIDIA blocks the recording that would find it.",
            LeftKind.EngineSlots => ScsKiller.EngineSlotsNote(RecordOn),
            LeftKind.UnknownSlots => "The rest use shader slots SCSKiller can't rebuild yet, so they still compile while you play.",
            LeftKind.NotSeen => ScsKiller.NotSeenNote(RecordOn),
            LeftKind.Recording => "Recording is on: anything new you play is added the next time it compiles.",
            LeftKind.PlayedCompiles => $"Last time you played, {PlayCompiles(L!):N0} still had to compile. Recording picks them up.",
            _ when Rt || s.RtUnseen || RtInline => null,
            LeftKind.PlayedClean => "Nothing was missing last time you played.",
            LeftKind.NothingKnown => "Nothing SCSKiller knows of is missing.",
            _ => "A few effects are only put together while you play. A short recording picks them up.",
        };
        return string.Join(" ", new[] { rt, text, GuessedStates ? "Some shader states are guesses. A longer recording makes them exact." : null }.OfType<string>());
    }

    private long? OnDisk => s.CacheOnDisk + (s.Kept?.Frees ?? 0);
    private string DiskValue => Format.Bytes(OnDisk ?? s.EstimatedCacheBytes);

    public object Compile
    {
        get
        {
            string title = s.LastWarmTime.HasValue ? "Last compile" : "First compile";
            string text;
            if (s.WarmedAt is { } at)
            {
                text = "Compiled " + Fmt.When(at) + (s.LastWarmTime is { } t ? " in " + Format.Duration(t) : "") + ", for driver " + s.WarmedDriverVersion + "."
                    + (ScsKiller.NothingFailed(s) ? " Nothing failed." : "")
                    + (ScsKiller.RefusedLine(s) is { } r ? " " + r : "")
                    + (OnDisk is { } d ? " It uses " + Format.Bytes(d) + " of disk space." : "")
                    + " The game may still run its own shader step when it starts.";
            }
            else if (s.LastWarmTime is { } last)
                text = "Its shader cache was cleared. The last compile took " + Format.Duration(last) + ".";
            else
                text = (s.EstimatedWarmTime is { } est ? "It takes about " + Format.Duration(est) + " and runs in the background, so you can keep using the PC." : "It runs in the background, so you can keep using the PC.")
                    + (s.EstimatedCacheBytes is { } eb ? " It adds about " + Format.Bytes(eb) + " to the driver's shader cache." : "");
            long failed = s.LastWarmFailed ?? 0, skipped = s.LastWarmSkipped ?? 0, crashed = s.LastWarmCrashed ?? 0, driverSkipped = s.LastWarmDriverSkipped ?? 0, unbuilt = s.LastWarmUnbuilt ?? 0;
            _ = failed;
            long all = skipped + crashed + driverSkipped + unbuilt;
            string? skippedText = null;
            if (s.WarmedAt.HasValue && all != 0L)
            {
                long needsRec = Math.Min(skipped, s.LastWarmNeedsRecording ?? 0);
                long notInGame = skipped - needsRec;
                skippedText = string.Join(" ", new[]
                {
                    notInGame > 0 ? $"{notInGame:N0} {(notInGame == 1 ? "was" : "were")} skipped: {(notInGame == 1 ? "its shader isn't" : "their shaders aren't")} in this version of the game. Nothing to do." : null,
                    crashed > 0 ? $"{crashed:N0} {(crashed == 1 ? "was" : "were")} skipped: {(crashed == 1 ? "it crashes" : "they crash")} the GPU driver. If the game uses {(crashed == 1 ? "it" : "them")}, it compiles {(crashed == 1 ? "it" : "them")} itself." : null,
                    driverSkipped > 0 ? $"{driverSkipped:N0} {(driverSkipped == 1 ? "was" : "were")} skipped: the GPU driver crashed or hung on {(driverSkipped == 1 ? "it or on what it builds" : "them or on what they build")} on." : null,
                    unbuilt > 0 ? $"{unbuilt:N0} {(unbuilt == 1 ? "was" : "were")} skipped: SCSKiller can't build {(unbuilt == 1 ? "it" : "them")} yet." : null,
                    needsRec > 0 ? $"{needsRec:N0} need{(needsRec == 1 ? "s" : "")} a recording on this PC: the game or a mod builds {(needsRec == 1 ? "its shaders" : "their shaders")} while it runs, so they're in no game file. Play with \"Record while I play\" on to catch them." : null,
                }.OfType<string>());
            }
            return new { title, text, skippedTitle = skippedText != null ? $"{all:N0} skipped" : null, skippedText };
        }
    }

    public object? Session
    {
        get
        {
            if (L is not { } l) return null;
            long play = PlayCompiles(l);
            long startup = l.StartupCompiles + l.StateObjectsStartupCompiled;
            long ready = l.FromGameLibrary + l.CacheHits + l.StateObjectsReady;
            string verdict;
            if (play + startup == 0L)
                verdict = ready <= 0 ? "Nothing had to compile while you played." : $"Nothing had to compile while you played: all {ready:N0} shader combinations the game used were ready.";
            else
            {
                string worst = l.StateObjectsCompiled > 0 ? "" : play == 1 ? $" (it took {l.WorstCompileMs:0} ms)" : $" (the longest took {l.WorstCompileMs:0} ms)";
                verdict = (play == 0L ? "Nothing had to compile while you played." : $"{play:N0} had to compile while you played{worst}.")
                    + (startup > 0 ? $" {startup:N0} compiled while the game started or loaded." : "")
                    + (ready > 0 ? $" The other {ready:N0} were ready." : "");
            }
            string label = $"{l.Requests:N0} requests: {l.FromGameLibrary:N0} from the game's own library, {l.CacheHits:N0} already in the driver cache, {l.Compiles:N0} compiled during play"
                + (l.StartupCompiles > 0 ? $", {l.StartupCompiles:N0} while the game started or loaded" : "")
                + (l.RayQueryRecompiles > 0 ? $", {l.RayQueryRecompiles:N0} ray-traced pipelines the driver partly recompiles every launch" : "")
                + (l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0 ? $". Ray tracing pipelines: {l.StateObjectsReady:N0} ready, {l.StateObjectsCompiled:N0} compiled during play" : "")
                + (l.StateObjectsStartupCompiled > 0 ? $", {l.StateObjectsStartupCompiled:N0} while the game started or loaded" : "");
            string? stateObjects = l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0
                ? $"Ray tracing pipelines: {l.StateObjectsReady:N0} ready, {l.StateObjectsCompiled:N0} compiled while you played" + (l.StateObjectsStartupCompiled > 0 ? $", {l.StateObjectsStartupCompiled:N0} while the game started or loaded." : ".")
                : null;
            string? rayQuery = l.RayQueryRecompiles > 0 ? $"{l.RayQueryRecompiles:N0} ray-traced pipeline{(l.RayQueryRecompiles == 1 ? "" : "s")} the driver partly recompiles every launch." : null;
            return new
            {
                header = "Last time you played · " + Format.Duration(s.LastFrames?.Duration ?? l.Duration),
                verdict, label, stateObjects, rayQuery,
                fromLibrary = l.FromGameLibrary, cacheHits = l.CacheHits, compiles = l.Compiles,
            };
        }
    }

    private List<Hitch> ShaderHitches => s.LastFrames?.Hitches.Where(h => h.Cause == HitchCause.Shader).ToList() ?? [];

    private long? StutterCount
    {
        get
        {
            if (s.LastFrames is { } f) return Format.NoPlay(f.Low1PctFps) ? null : ShaderHitches.Count;
            return L is { } l ? PlayCompiles(l) : null;
        }
    }

    public object? FramesSummary
    {
        get
        {
            FrameReport? f = s.LastFrames;
            long? count = StutterCount;
            if (f is null && count is null) return null;
            return new
            {
                verdict = f is null ? null : Format.Frames(f, ShaderHitches),
                summary = f is null ? null : FramesText(f),
                stuttersLabel = f != null ? "Stutters last time you played" : "Compiles while you played",
                stutters = count is { } c ? c != 0L ? Fmt.N(c) : "None" : null,
            };
        }
    }

    private static string FramesText(FrameReport f)
    {
        var inPlay = f.Hitches.Where(h => FrameLog.InPlay(h.At.TotalMilliseconds, h.Ms, f.Startup.TotalMilliseconds) && h.Cause != HitchCause.Quitting).ToList();
        return $"Startup (the game's own shader precompile and first load): {Format.Duration(f.Startup)}. In play: {inPlay.Count(h => h.Ms >= 50.0):N0} frames of 50 ms or more, {inPlay.Count(h => h.Ms >= 100.0):N0} of 100 ms or more. Frame times stay on this PC.";
    }

    public static string Cause(HitchCause c) => c switch
    {
        HitchCause.Shader => "shader compile",
        HitchCause.Other => "other hitch",
        HitchCause.Quitting => "quitting",
        HitchCause.LoadingShaders => "loading, compiling shaders",
        _ => "loading",
    };

    private static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    public object? FrameGraph
    {
        get
        {
            if (s.LastFrames is not { } f) return null;
            return new
            {
                durationMs = f.Duration.TotalMilliseconds,
                startupMs = f.Startup.TotalMilliseconds,
                peaks = f.Peaks,
                hitches = f.Hitches.Select(h => new { atMs = h.At.TotalMilliseconds, ms = h.Ms, cause = Fmt.Camel(h.Cause.ToString()) }),
                verdict = Format.Frames(f, ShaderHitches),
                summary = FramesText(f),
                low1PctFps = f.Low1PctFps,
                avgFps = f.AvgFps,
            };
        }
    }

    public List<object> HitchRows => s.LastFrames?.Hitches.Select(h => (object)new { label = Clock(h.At) + "  " + Cause(h.Cause), value = Format.Ms(h.Ms) }).ToList() ?? [];

    public string MiddlewareChip
    {
        get
        {
            IReadOnlyList<MiddlewareTag> mw = s.Middleware ?? [];
            var compiled = mw.Where(t => t.Pipelines > 0).Select(t => t.Label).Concat(mw.Any(t => t.Label == "DLSS") ? ["DLSS (by the driver)"] : Array.Empty<string>()).ToList();
            return compiled.Count > 0 ? "Also compiles: " + string.Join(" · ", compiled) : "Detected: " + string.Join(" · ", mw.Select(t => t.Label));
        }
    }

    public string MiddlewareTip
    {
        get
        {
            IReadOnlyList<MiddlewareTag> mw = s.Middleware ?? [];
            return "Upscaler DLLs next to the game. Their pipelines are compiled with it once a recording of any game has seen that DLL version: on this PC, or for FSR and XeSS on any PC that shares them"
                + (P is { MiddlewareItems: > 0 } p ? " (this plan: " + Fmt.N(p.MiddlewareItems) + " of theirs)" : "") + ".\n\n" + string.Join("\n", mw.Select(Fmt.TagTip));
        }
    }

    public string? SimilarText => s.Similar switch
    {
        SimilarSystems.Helped => "Users with a similar system reported that compiling removed or reduced the stutter in this game.",
        SimilarSystems.NoHelp => "Users with a similar system reported that compiling did not help in this game.",
        SimilarSystems.Warning => "Users with a similar system reported that " + Name + " ran worse after compiling.",
        _ => null,
    };

    // ---------------------------------------------------------------- DTOs

    public object ToDto()
    {
        var place = Place;
        QueueItem? running = Running;
        bool compilingNow = running != null;
        bool manual = s.Game.Store == Store.Manual;
        bool canRecordToggle = CanToggleRecord;
        return new
        {
            id = Id,
            name = Name,
            store = Fmt.StoreName(s.Game),
            manual,
            exe = s.Game.ExePath,
            exeName = Path.GetFileName(s.Game.ExePath),
            installDir = s.Game.InstallDir,
            sub = Sub,
            engine = Fmt.Engine(s.Engine),
            engineChip = EngineChip,
            engineFamily = s.Engine?.Family,
            api = ApiChip.Length > 0 && !ShowApiPicker ? ApiChip : null,
            status = KeyLocked ? "unsupported" : Fmt.Camel(s.Status.ToString()),
            statusText = StatusText,
            tone = Tone,
            statusTitle = StatusTitle,
            statusReason = StatusReason,
            rowNote = RowNote.Length > 0 ? RowNote : null,
            tip = (s.Playing ? "Playing now · " : "") + FullNote,
            reason = KeyLocked ? KeyLockedReason : NoDbNote(s.StatusReason),
            playing = s.Playing,
            queued = Queued,
            compiling = compilingNow,
            compilingSoon = CompilingSoon,
            queuePosition = place.Item != null ? place.Number : (int?)null,
            percent = running is { } r && !Fmt.Indeterminate(r) ? Math.Round(Fmt.Percent(r), 1) : (double?)null,
            shaders = s.ShaderCount,
            pipelines = ScsKiller.PlanPipelines(s),
            cacheBytes = Library.CacheBytes(s),
            cacheText = Fmt.Cache(s),
            cacheEstimated = !s.CacheOnDisk.HasValue && s.EstimatedCacheBytes.HasValue,
            compileTime = Fmt.CompileTime(s) is { } ct ? Format.Duration(ct) : null,
            compiledAt = s.WarmedAt is { } wa ? Fmt.Iso(wa) : null,
            compiledDriver = s.WarmedDriverVersion,
            recommended = ctx.Recommended.Contains(Id),
            inAddAllReady = ScsKiller.InAddAllReady(s),
            tags = Tags,
            knownStutter = s.KnownStutter is { } ks ? new { severity = Fmt.Camel(ks.Severity.ToString()), reason = ks.Reason, source = ks.Source, date = ks.Date, tip = Fmt.StutterTip(s) } : null,
            noStutter = s.NoStutter,
            antiCheat = new { kind = Fmt.AntiCheatKey(s.AntiCheat), name = NoAntiCheat ? null : ScsKiller.AntiCheatName(s), chip = AntiCheatChip, tip = AntiCheatTip },
            recorder = new
            {
                installed = s.RecorderInstalled,
                effective = RecordOn,
                @override = Fmt.Camel(s.RecorderOverride.ToString()),
                foreign = s.RecorderForeign,
                skip = s.RecorderSkip,
                note = s.RecorderNote,
                refused = s.RecorderRefused,
                recordNote = RecordNote,
                dll = RecorderDll,
                noLaunch = s.RiskNoLaunch,
                canToggle = canRecordToggle,
                useDefaultText = ShowUseDefault ? UseDefaultText : null,
                mod = s.RecorderMod,
                alongside = HasMod ? new { on = s.RecordAlongsideMod, title = "Record alongside " + s.RecorderMod, note = s.RecorderMod + "'s d3d12.dll is renamed while the recorder is in, and restored exactly as it was when it's removed." } : null,
                recordingBytes = s.RecordingBytes,
                recordingText = s.RecordingBytes > 0 ? "The recording uses " + Format.Bytes(s.RecordingBytes) + " (in the game folder and SCSKiller's copy)" : null,
                paused = s.RecordingPaused,
                pausedText = s.RecordingPaused ? ScsKiller.PausedNote(ctx.Settings) + ". Raise the limit in Settings, or clear the recording." : null,
                canClear = !s.Playing,
                clearTip = s.Playing ? "Close the game first" : "Deletes the recorded pipelines. The recorder stays in",
                offline = ShowOffline ? new { risk = Risk, on = s.OfflineRecord, running = s.OfflineRunning, canStart = CanStartOffline, buttonText = OfflineButtonText, riskText = OfflineRiskText } : null,
                sessionNote = s.SessionNote,
                tipTitle = HasRecordTip ? RecordTipTitle : null,
                tip = HasRecordTip ? RecordTip : null,
            },
            actions = new
            {
                compile = CanCompile && CanAdd,
                compileText = AddText,
                compileTip = BaInProgressAny ? BaBusyTip : null,
                add = IsAdd,
                addEnabled = IsAdd && !Queued,
                record = IsRecord,
                recordEnabled = IsRecord && !s.AnyAntiCheat,
                details = IsDetails,
                why = IsWhy,
                play = CanLaunch,
                playEnabled = CanLaunch && !s.Playing && !CompilingSoon,
                playText = s.Playing ? "Running" : "Play",
                playTip = s.Playing ? Name + " is running" : CompilingSoon ? Fmt.CompilingTip : Fmt.PlayVia(s.Game),
                clearCache = ShowClearCache,
                clearCacheEnabled = CanClearCache,
                clearCacheText = ClearCacheText,
                clearCacheTip = ClearCacheTip,
                setAesKey = false,
                rename = manual,
                gameFolder = manual,
                remove = new
                {
                    text = "Remove",
                    tip = manual ? "Forget this game. Its recorder leaves the game folder, and the game's own files stay." : "Hide this game. Its files, saves and compiled cache stay.",
                    confirmTitle = "Remove " + Name + " from the library?",
                    confirmText = manual ? "SCSKiller forgets the game and takes its recorder out of the game's folder. The game's own files stay, and you can add it again any time." : HideNote,
                },
            },
            kept = HasKept && s.Kept is { } k ? new { bytes = k.Bytes, frees = k.Frees, sizeText = Fmt.KeptSize(k), state = KeptState } : null,
            keptCacheNote = KeptCacheNote,
            beforeAfter = new
            {
                shown = ShowBa,
                enabled = BaEnabled,
                text = BaText,
                note = ShowBa && BaNote.Length > 0 ? BaNote : null,
                tip = BaTip,
                stage = Ba is { } b ? Fmt.Camel(b.Stage.ToString()) : null,
                minutes = Ba?.Minutes,
                playedSeconds = Ba?.PlayedSeconds,
                problem = Ba?.Problem,
                voided = BaVoided,
                clearPending = Ba?.ClearPending ?? false,
                chip = BaChip.Length > 0 ? BaChip : null,
                hasMenu = ShowBa && Ba != null,
                compileAgain = BaCompileAgain,
            },
            similar = SimilarText is { } st ? new { text = st, warning = s.Similar == SimilarSystems.Warning } : null,
            guessNote = GuessNote,
            keyNote = KeyNote,
            partlyCompiled = PartlyCompiled,
            partlyCompiledWhy = PartlyCompiled ? PartlyCompiledWhy : null,
            needsAesKey = false,
            dxvk = DxvkNote is { } dxvkNote ? new { compiled = s.DxvkCompiled, text = dxvkNote } : null,
            cacheLimitWarning = CacheLimitWarning,
            error = ctx.Error(Id),
            frames = FramesSummary,
        };
    }
}
