using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.Warming;

/// <summary>One line of scskiller_warm.exe's stdout protocol (ARCHITECTURE.md). Unknown or non-JSON lines parse to null.
/// A "retry" event (the process's driver was poisoned by a fault or hang in ray tracing, <paramref name="Reason"/> "rt", its
/// device was removed, "removed", a pipeline's create raised an exception, "fault", creates kept hanging with the device
/// intact, "hang", or the process died without a last line, "died", planned by its parent from the item states it left)
/// says where a new process goes on: <paramref name="From"/> (--start), <paramref name="RtThreads"/>
/// (--rt-threads), <paramref name="FailedItem"/> (-1 or an item that faulted alone: --skip from then on),
/// <paramref name="Skip"/> (hung items, or one that died alone, at or after From: --skip from then on), <paramref name="Isolate"/> (--isolate).
/// <paramref name="Crashed"/> (retry and done): the record keys of the items that crash the driver, skipped or blamed by
/// that process (--skip-keys from then on). <paramref name="Stage"/> (the "stage" event, the first line): the process's own
/// staging folder, where its scskiller.log ends up. <paramref name="Workers"/> of <paramref name="Threads"/> (progress): the
/// threads its memory budget lets run now; 0 from a warm that doesn't say.
/// <paramref name="DriverSkipped"/> (progress, retry and done, counted like Failed): <see cref="WarmResult.DriverSkipped"/>.</summary>
public sealed record WarmEvent(string Event, long Done, long Total, long Failed, double Rate, double Seconds, bool Stopped, string? Message,
    long From = 0, int RtThreads = 0, long FailedItem = -1, string? Reason = null, IReadOnlyList<string>? Crashed = null, IReadOnlyList<long>? Isolate = null,
    string? Stage = null, int Workers = 0, int Threads = 0, long DriverSkipped = 0, IReadOnlyList<long>? Skip = null)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WarmEvent? Parse(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            if (JsonSerializer.Deserialize<WarmEvent>(line, Json) is not { Event: not null } e) return null;
            // null when empty: records compare lists by reference, and Pump folds equal progress lines
            return e with { Crashed = e.Crashed is { Count: > 0 } ? e.Crashed : null, Isolate = e.Isolate is { Count: > 0 } ? e.Isolate : null, Skip = e.Skip is { Count: > 0 } ? e.Skip : null };
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Drives scskiller_warm.exe (staged path: the vendor cache is keyed by exe name).</summary>
public sealed class Warmer(IGpuVendorBackend vendor, string? warmExe = null) : IWarmer
{
    /// <summary>After Stop: a warm that hasn't printed its done event by then is stuck (workers deadlocked in the driver
    /// ignore the stop event) and is ended. Replaceable for tests.</summary>
    public TimeSpan StuckAfter { get; init; } = TimeSpan.FromSeconds(30);
    public const string StuckError = "the compile didn't stop cleanly (the driver may be stuck), so it was ended";
    public const string NoDoneError = "scskiller_warm exited without a done event";
    public const string DiedError = "scskiller_warm died without an exit code";

    /// <summary>Removed devices, faults or hangs in a row whose retry blamed no new item before a warm gives up. Replaceable for tests.</summary>
    public int MaxRecoveries { get; init; } = 20;

    /// <summary>The error of a warm that stopped on removed devices, by the number of items it skipped for crashing the driver.</summary>
    public static string CrashedError(int skipped) => skipped == 0
        ? "the GPU driver kept crashing with no single pipeline to blame, so the compile stopped"
        : skipped == 1 ? "1 pipeline that crashes the GPU driver was skipped. Compiling again continues where it stopped"
        : $"{skipped} pipelines that crash the GPU driver were skipped. Compiling again continues where it stopped";

    public const string HungError = "the GPU driver kept hanging, so the compile stopped. Compiling again continues where it stopped";

    /// <summary>Added to scskiller_warm's environment (tests: SCSKILLER_WARM_* fault injection).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public IProgress<string>? Log { get; set; }

    /// <summary>Where the run's processes start (<see cref="WarmHost.For"/>), by game and work folder. Replaceable for tests.</summary>
    internal Func<Game, string, IWarmHost>? Host { get; init; }

    /// <summary>Steam's folder for the host (default: Steam's own).</summary>
    internal string? SteamRoot { get; init; }

    /// <summary>The game's AGS registration (<see cref="AmdAgs.Of"/>); null or returning null: a plain device.</summary>
    public Func<Game, AgsRegistration?>? Ags { get; set; }

    /// <summary>How the game starts NVIDIA Aftermath (<see cref="NvAftermath.Of"/>); null or returning null: the warm doesn't.</summary>
    public Func<Game, AftermathInit?>? Aftermath { get; set; }

    /// <summary>A folder holding a copy of the game's layer (ReShade's dll, its ini, the add-ons), staged next to the warm's
    /// exe (scskiller_warm --layer) so the layer changes the warm's pipelines as it changes the game's; null or returning
    /// null: none. Called with the run's work folder.</summary>
    public Func<Game, string, string?>? Layer { get; set; }

    /// <summary>The warm exe for a vendor, relative to native\: NVIDIA's runs on the segment heap, since on the NT heap its
    /// compiler threads queue on the process heap's lock. AMD's compiler is slower on the segment heap, so the others keep
    /// the NT heap. Both are named scskiller_warm.exe and stage themselves under the game's name.</summary>
    public static string ExeFor(GpuVendor vendor, bool vulkan = false) => vendor == GpuVendor.Nvidia ? Path.Combine("segheap", "scskiller_warm.exe") : "scskiller_warm.exe";

    /// <summary>Why the game can't be compiled on this PC now, one line; null = it can.</summary>
    public string? Refusal(Game game, bool vulkan = false) => WarmHost.Refusal(game, SteamRoot, vulkan);

    /// <summary>A Vulkan warm's environment: the game's cache variables (<see cref="WarmOptions.CacheEnv"/>, so the driver
    /// writes where the game's cache is), then <paramref name="env"/> over them.</summary>
    internal static IReadOnlyDictionary<string, string>? VulkanEnv(IReadOnlyDictionary<string, string>? env, IReadOnlyDictionary<string, string>? cache)
    {
        if (cache is not { Count: > 0 }) return env;
        var all = new Dictionary<string, string>(cache, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) all[k] = v;
        return all;
    }

    public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress) =>
        Start(game, workDir, options, progress, vendor.Gpu);

    /// <summary><paramref name="gpu"/>: the adapter the run launches on (its LUID), every process of it, as the caller
    /// records the run for.</summary>
    public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress, GpuInfo gpu)
    {
        var host = Host?.Invoke(game, workDir) ?? WarmHost.For(game, workDir, gpu, Log, SteamRoot, options.Vulkan);
        try
        {
            var exe = warmExe ?? NativeTools.Find(ExeFor(vendor.Vendor, options.Vulkan))
                      ?? throw new FileNotFoundException($"{ExeFor(vendor.Vendor, options.Vulkan)} not found next to the app or in {Path.Combine("proxy", "build", "Release")}");
            // a Vulkan game's pipelines replay from its .foz, under its own cache variables: no staged layout, AGS or layers
            if (options.Vulkan)
                return new WarmRun(host, vendor, gpu, exe, game, workDir, options, progress, StuckAfter, null, MaxRecoveries, VulkanEnv(Environment, options.CacheEnv), ["--vulkan"], Log);
            string? stagePath = null;
            if (Games.XboxSource.AppUserModelId(game) == null && (stagePath = StagePath(game, workDir, out var why)) == null)
                Log?.Report($"{game.Name}: the compile runs without the install's folder layout: {why}");
            var reg = vendor.Vendor == GpuVendor.Amd ? Ags?.Invoke(game) : null;
            var agsDll = reg == null ? null : AmdAgs.DllFor(game, NativeTools.Find(AmdAgs.DllName));
            var ags = AgsArgs(vendor.Vendor, reg, agsDll == null ? null : host.WarmPath(agsDll), out var agsWhy);
            if (agsWhy != null) Log?.Report($"{game.Name}: {agsWhy}");
            if (Layer?.Invoke(game, workDir) is { } layer) ags = [.. ags, "--layer", host.WarmPath(layer)];
            if (AgilityDir(game.ExePath) is { } d3d12) ags = [.. ags, "--d3d12", host.WarmPath(d3d12)];
            if (DxvkDll(vendor.Vendor, game.ExePath) is { } d3d11) ags = [.. ags, "--d3d11", host.WarmPath(d3d11)];
            if (vendor.Vendor == GpuVendor.Nvidia && Aftermath?.Invoke(game) is { } am)
                ags = [.. ags, .. AftermathArgs(vendor.Vendor, am, NvAftermath.DllFor(game) is { } dll ? host.WarmPath(dll) : null)];
            return new WarmRun(host, vendor, gpu, exe, game, workDir, options, progress, StuckAfter, stagePath, MaxRecoveries, Environment, ags, Log);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>scskiller_warm's --ags arguments: on AMD, for a game that registers with AGS (an Xbox app game holds its
    /// app name's key too, measured), the child creates its device through <paramref name="agsDll"/> under the
    /// game's app and engine names, so the driver keys its cache like the game's. Empty otherwise, with the reason when a
    /// registration can't be passed.</summary>
    public static string[] AgsArgs(GpuVendor vendor, AgsRegistration? reg, string? agsDll, out string? why)
    {
        why = null;
        if (vendor != GpuVendor.Amd || reg == null) return [];
        if (agsDll == null)
        {
            why = $"{AmdAgs.DllName} not found next to the app: the compile fills the exe name's cache, not the one of the game's AGS app name {reg.App}";
            return [];
        }
        return ["--ags", agsDll, "--ags-app", reg.App, "--ags-engine", reg.Engine];
    }

    /// <summary>scskiller_warm's --aftermath arguments: on NVIDIA, for a game that starts Aftermath in a way that changes the
    /// driver's key, the child starts it the same way through the game's own <paramref name="dll"/>. Empty otherwise.</summary>
    public static string[] AftermathArgs(GpuVendor vendor, AftermathInit? init, string? dll) =>
        vendor == GpuVendor.Nvidia && init is { ChangesKey: true } && dll != null ? ["--aftermath", dll, "--aftermath-init", init.Arg] : [];

    /// <summary>scskiller_warm's --d3d11: the game's d3d11.dll when it is DXVK's, on NVIDIA, whose Vulkan cache is keyed on
    /// the exe name (AMD's on the full path, which the staged warm doesn't share); null otherwise.</summary>
    public static string? DxvkDll(GpuVendor vendor, string exePath)
    {
        try
        {
            var dll = Games.GameFiles.Find(Path.GetDirectoryName(Path.GetFullPath(exePath))!, App.ScsKiller.Dll11);
            return vendor == GpuVendor.Nvidia && Games.ReShade.IsDxvk(dll) ? dll : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    /// <summary>scskiller_warm's --d3d12: the folder of the Agility SDK runtime the game's exe asks for (its D3D12SDKPath
    /// export, relative to the exe, else its D3D12 or D3D12\x64 folder), when it holds a D3D12Core.dll; null otherwise.
    /// The warm then runs on that runtime, as the game does: where the system's is older (Windows 10), it rejects what the
    /// game recorded on the newer one.</summary>
    public static string? AgilityDir(string exePath)
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
            string? sdkPath = null;
            try
            {
                using var pe = Carved.PeFile.Open(exePath);
                sdkPath = Carved.PeFile.ExportedString(pe, "D3D12SDKPath");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException) { }
            // a protected or packed exe's exports can't be read from the file: the folder the Agility SDK installs to, or
            // its x64 subfolder (Fortnite's)
            var d3d12 = Games.GameFiles.Find(exeDir, "D3D12");
            var dir = !string.IsNullOrEmpty(sdkPath) ? AgilityFolder(exeDir, sdkPath)
                : File.Exists(Games.GameFiles.Find(d3d12, "D3D12Core.dll")) ? d3d12 : Games.GameFiles.Find(d3d12, "x64");
            return dir != null && File.Exists(Games.GameFiles.Find(dir, "D3D12Core.dll")) ? dir : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    /// <summary>The folder a D3D12SDKPath export names: a relative path that stays inside the exe's folder, as the D3D12
    /// loader takes it; null for anything else (an absolute path, a drive, a ".." out of it).</summary>
    public static string? AgilityFolder(string exeDir, string? sdkPath)
    {
        if (string.IsNullOrEmpty(sdkPath) || Path.IsPathRooted(sdkPath) || sdkPath.Contains(':')) return null;
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Games.GameFiles.Find(exeDir, sdkPath)));
        var rel = Path.GetRelativePath(exeDir, dir);
        return rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(rel) ? null : dir;
    }

    /// <summary>The D3D12SDKVersion the Agility SDK runtime in <paramref name="dir"/> exports; 0 when it can't be read.</summary>
    public static int AgilitySdk(string dir)
    {
        try
        {
            using var pe = Carved.PeFile.Open(Path.Combine(dir, "D3D12Core.dll"));
            return Carved.PeFile.ExportData(pe, "D3D12SDKVersion") is { RemainingBytes: >= 4 } data ? data.ReadInt32() : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException) { return 0; }
    }

    /// <summary>The highest Shader Model DirectX 12 compiles on Windows 10 (builds 19041 to 21999), with the game's Agility SDK
    /// runtime of that version (null = none: the system's); null where it isn't known to fall short (Windows 11, a newer SDK).</summary>
    public static Version? RuntimeShaderModel(int osBuild, int? agilitySdk) => osBuild is < 19041 or >= 22000 ? null : agilitySdk switch
    {
        null => new(6, 5),
        4 or (>= 600 and <= 605) => new(6, 6),
        >= 606 and <= 612 => new(6, 7),
        >= 613 and <= 616 => new(6, 8),
        _ => null,
    };

    /// <summary>Why pipelines of a plan whose shaders reach <paramref name="planShaderModel"/> will fail on this runtime, one
    /// line; null when they won't (<see cref="RuntimeShaderModel"/>).</summary>
    public static string? RuntimeWarning(string? planShaderModel, int osBuild, int? agilitySdk) =>
        Version.TryParse(planShaderModel, out var needed) && RuntimeShaderModel(osBuild, agilitySdk) is { } runs && needed > runs
            ? agilitySdk == null
                ? $"Windows 10's DirectX 12 can't compile this game's Shader Model {needed} shaders, so pipelines that use them will fail. Updating to Windows 11 can fix it."
                : $"The game's own DirectX 12 runtime can't compile its Shader Model {needed} shaders, so pipelines that use them will fail. Updating the game or Windows can fix it."
            : null;

    /// <summary><see cref="RuntimeWarning(string?, int, int?)"/> for the game's exe: with the Agility SDK it ships
    /// (<see cref="AgilityDir"/>), read only when the system's runtime would fall short.</summary>
    public static string? RuntimeWarning(string? planShaderModel, string exePath, int osBuild) =>
        RuntimeWarning(planShaderModel, osBuild, null) == null ? null
            : RuntimeWarning(planShaderModel, osBuild, AgilityDir(exePath) is { } dir ? AgilitySdk(dir) : null);

    const int MaxPath = 260;
    const string LongestStagedName = "scskiller_warm_times.csv";
    const string LongestStageDir = "stage-4294967295-99";   // scskiller_warm's staging folder: stage-<pid>-<n>

    /// <summary>scskiller_warm's --stage-path: the install folder's name, the exe's folder inside the install, the exe's
    /// file name (AMD app profiles such as Hogwarts Legacy's match the launched path's tail, e.g.
    /// "Hogwarts Legacy\Phoenix\Binaries\Win64\HogwartsLegacy.exe"). Null, with the reason, for an exe outside the install
    /// or a staged path that would reach MAX_PATH (scskiller_warm then stages the exe alone).</summary>
    public static string? StagePath(Game game, string workDir, out string? why)
    {
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.InstallDir));
        var rel = Path.GetRelativePath(install, Path.GetFullPath(game.ExePath));
        why = Path.GetFileName(install).Length == 0 ? $"the install folder {game.InstallDir} is a drive root"
            : Path.IsPathRooted(rel) || rel == "." || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? $"{game.ExePath} is outside {game.InstallDir}"
            : null;
        if (why != null) return null;
        var path = Path.Combine(Path.GetFileName(install), rel);
        var dir = Path.GetFullPath(Path.Combine(workDir, LongestStageDir, Path.GetDirectoryName(path)!));
        if (dir.Length + 1 + Math.Max(Path.GetFileName(path).Length, LongestStagedName.Length) < MaxPath) return path;
        why = $"the staged path under {workDir} would be longer than {MaxPath - 1} characters";
        return null;
    }
}

/// <summary>One warm, possibly several scskiller_warm processes: a process whose driver was poisoned by a fault or hang in
/// ray tracing ends with a "retry" event, and a new one goes on from its first unfinished item with the same ray tracing
/// threads, the state object that faulted or hung skipped (a quarter of the threads when that item is unknown, or after
/// <see cref="WarmRun.MaxKnownSkips"/> such skips). Failed counts add up across them; the progress note says what the retry
/// did meanwhile. A process whose device was removed, or whose replay of a pipeline faulted, ends with a "removed" or "fault"
/// retry: the next one creates the items that were in flight alone first and skips the ones that crash the driver (by record
/// key). A process whose creates kept hanging ends with a "hang" retry: the next one skips the hung items for this compile
/// only. Both go on until MaxRecoveries such retries in a row blame no new item (per pass of a careful warm). A work folder
/// with a pass file (<see cref="WarmPasses"/>) runs one process per pass, in order,
/// with the crash keys found so far; Done and StartAt then count the whole warm's items
/// (<see cref="WarmPasses.Overall"/>), so a stopped careful warm resumes in its pass. A process started with --aftermath that
/// fails or dies before its first item (the game's Aftermath may fault or hang there) is run again without it, once, from the
/// warm's first item (<see cref="WarmResult.AftermathDropped"/>). The processes start through an <see cref="IWarmHost"/>,
/// which may end one at a segment's end (a new one goes on from where it stopped) or halt the run.</summary>
sealed class WarmRun : IWarmRun
{
    static readonly TimeSpan CacheSampleInterval = TimeSpan.FromSeconds(5), ReportEvery = TimeSpan.FromMilliseconds(250);
    // Up to two processes per item that removes the device (the removal, then the one that blames it alone): ~127 such items.
    const int MaxProcesses = 256;
    // Known state objects skipped in one compile before the retries lower the ray tracing threads too (a crash storm).
    const int MaxKnownSkips = 8;
    IWarmProcess _p = null!;
    readonly IWarmHost _host;
    readonly object _lock = new();
    readonly TimeSpan _stuckAfter;
    volatile TaskCompletionSource _doneSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);   // the final done event
    bool _paused, _stopping;
    volatile string? _ended;   // why Stop had to end the process

    public Task<WarmResult> Completion { get; }

    /// <summary><paramref name="host"/> starts the processes and is disposed when the run ends.</summary>
    public WarmRun(IWarmHost host, IGpuVendorBackend vendor, GpuInfo gpu, string exe, Game game, string workDir, WarmOptions o, IProgress<WarmProgress>? progress, TimeSpan stuckAfter, string? stagePath,
        int maxRecoveries, IReadOnlyDictionary<string, string>? env, string[]? extra = null, IProgress<string>? log = null)
    {
        _host = host;
        _stuckAfter = stuckAfter;
        // a Vulkan game's cache variables may point the driver at a folder of the game's own: its growth is measured there
        var redirect = o.Vulkan && Vulkan.VulkanPlan.CachePath(env, vendor.Vendor) is { } cache && Directory.Exists(cache) ? cache : null;
        long before = Usage();
        var clock = Stopwatch.StartNew();
        var stderr = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var skipKeys = new HashSet<string>(o.SkipKeys ?? [], StringComparer.OrdinalIgnoreCase);
        var passes = WarmPasses.Read(workDir);
        var (pass, from) = passes?.Locate(o.StartAt) ?? (0, o.StartAt);
        var rt = 0;   // the last process's --rt-threads
        long Whole(long done) => passes?.Overall(pass, done) ?? done;
        long Usage()
        {
            if (redirect == null) return vendor.GetCacheUsage().BytesOnDisk;
            try { return new DirectoryInfo(redirect).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        }
        IWarmProcess Launch(long start, int rtThreads, List<long> skip, IReadOnlyList<long> isolate)
        {
            (from, rt) = (start, rtThreads);
            var threads = passes != null && !passes.IsFast(pass) && o.CarefulThreads > 0 ? o.CarefulThreads : o.Threads;
            List<string> args = [host.WarmPath(workDir), Path.GetFileName(game.ExePath), "--threads", threads.ToString(),
                "--priority", o.Priority == WarmPriority.Idle ? "idle" : "below", "--start", start.ToString(), .. host.StopArgs, .. host.AdapterArgs(gpu)];
            if (o.MemoryMB > 0) args.AddRange(["--memory-mb", o.MemoryMB.ToString()]);
            if (rtThreads > 0) args.AddRange(["--rt-threads", rtThreads.ToString()]);
            if (skip.Count > 0) args.AddRange(["--skip", string.Join(',', skip)]);
            if (stagePath != null) args.AddRange(["--stage-path", stagePath]);
            args.AddRange(extra ?? []);
            if (vendor.Caps.PackageKeyed && !o.Vulkan && Games.XboxSource.AppUserModelId(game) is { } app) args.AddRange(["--package", app]);
            if (skipKeys.Count > 0)
            {
                var keys = string.Join(',', skipKeys.Order(StringComparer.Ordinal));
                if (keys.Length > 8000)   // a command line holds 32,767 characters: a long list goes through a file
                {
                    var file = Path.GetFullPath(Path.Combine(workDir, "skip-keys.txt"));
                    File.WriteAllText(file, keys);
                    keys = "@" + host.WarmPath(file);
                }
                args.AddRange(["--skip-keys", keys]);
            }
            if (isolate.Count > 0) args.AddRange(["--isolate", string.Join(',', isolate)]);
            if (passes != null) args.AddRange(["--pass", passes.Number(pass).ToString()]);
            return host.Start(exe, args, env ?? new Dictionary<string, string>(), l => { stderr.Enqueue(l); log?.Report($"{game.Name}: compile: {l}"); });
        }
        _p = Launch(from, 0, [], []);
        _ = Task.Run(() => GuardMemory(log, game.Name));
        Completion = Task.Run(async () =>
        {
            try { return await Run(); }
            catch
            {
                IWarmProcess p;
                lock (_lock) p = _p;
                try { p.Kill(); }
                finally { _host.Dispose(); }
                throw;
            }
        });

        async Task<WarmResult> Run()
        {
            Stopwatch? sampled = null;
            long growth = 0;
            long Growth()   // a directory walk for NVIDIA: not on every report
            {
                if (sampled == null || sampled.Elapsed >= CacheSampleInterval) (growth, sampled) = (Math.Max(0, Usage() - before), Stopwatch.StartNew());
                return growth;
            }
            long carried = 0, carriedSkips = 0;   // failures and skips the earlier processes counted (before their retry point, or in earlier passes)
            var lastCarried = false;   // the last process's failures are in carried already
            string? note = null;
            var skip = new List<long>();
            var crashed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var added = new HashSet<string>(StringComparer.Ordinal);   // cache keys the host saw the processes add
            int removals = 0, knownSkips = 0;
            WarmEvent? last, done;
            string? error;
            int? code;
            bool betweenPasses = false;   // stopped after a pass ended: the next one resumes from its first item
            bool resumable = false;       // gave up on device removals or hangs at a retry point
            bool dropped = false;         // run again without --aftermath
            string? stage = null;         // the last process's staging folder (its stage event)
            for (var n = 1; ; n++)
            {
                var passNote = passes == null ? null : passes.IsFast(pass) ? "careful compile: the plan's other pipelines at full speed"
                    : $"careful compile: recorded pipelines, pass {passes.Number(pass)} of {passes.CarefulCount}";
                var report = progress == null ? null : new Adjusted(progress, carried, carriedSkips, note ?? passNote, passes?.Total);
                var lastPass = passes == null || pass == passes.Count - 1;
                stage = null;   // this process's, never an earlier one's
                (last, done, error) = await WarmOutput.Pump(_p.Output, report, Growth, ReportEvery,
                    () => { if (lastPass || _stopping) _doneSeen.TrySetResult(); }, Whole, s => stage = host.HostPath(s));
                code = await _p.Exited;
                var end = await host.AfterProcess(doing => progress?.Report(new WarmProgress(Whole(last?.Done ?? from), passes?.Total ?? last?.Total ?? 0,
                    carried + (last?.Failed ?? 0), 0, Growth(), Note: doing, DriverSkipped: carriedSkips + (last?.DriverSkipped ?? 0))));
                added.UnionWith(end.Added);
                lastCarried = false;
                crashed.UnionWith(done?.Crashed ?? []);
                bool stopping;
                lock (_lock) stopping = _stopping;
                var clean = error == null && (done is { Event: "retry" } || code == 0 && done != null);
                // the host can't go on (a pass that ended cleanly as the last one still counts)
                if (end.Halt is { } halt && !stopping && !(clean && done is { Event: "done", Stopped: false } && lastPass))
                {
                    (error, resumable) = (error ?? halt, clean);
                    break;
                }
                // the host ended the process at a segment's end: a new one goes on from where it stopped
                if (end.Segment && clean && done is { Event: "done", Stopped: true } segment)
                {
                    lock (_lock)
                    {
                        if (_stopping) break;
                        (carried, carriedSkips, lastCarried, note) = (carried + segment.Failed, carriedSkips + segment.DriverSkipped, true, null);
                        _doneSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _p = Launch(segment.Done, rt, skip, []);
                    }
                    continue;
                }
                if (error == null && code == 0 && done is { Event: "done", Stopped: false } && !lastPass)
                {
                    (carried, carriedSkips, lastCarried) = (carried + done.Failed, carriedSkips + done.DriverSkipped, true);
                    lock (_lock)
                    {
                        if (_stopping) { betweenPasses = true; break; }
                        (pass, n, note, removals) = (pass + 1, 0, null, 0);   // each pass gets its own recoveries
                        skip.Clear();
                        skipKeys.UnionWith(crashed);
                        _p = Launch(0, 0, skip, []);
                    }
                    continue;
                }
                var am = Array.IndexOf(extra ?? [], "--aftermath");
                if (am >= 0 && !_stopping && (error != null || done is { Event: "retry", Reason: "died", Isolate: null, Skip: null }) && (last?.Done ?? from) <= from)
                {
                    log?.Report($"{game.Name}: the compile stopped while starting NVIDIA Aftermath ({error ?? "the warm process died"}): compiling without it");
                    lock (_lock)
                    {
                        (extra, dropped, error) = ([.. extra![..am], .. extra[(am + 4)..]], true, null);   // AftermathArgs: the option, its DLL, --aftermath-init, its values
                        // what ran before filled the Aftermath key only: the plain key starts from the first item
                        (pass, carried, carriedSkips, lastCarried, removals, knownSkips, note) = (0, 0, 0, false, 0, 0, null);
                        skip.Clear();
                        _p = Launch(0, 0, skip, []);
                    }
                    continue;
                }
                if (done is not { Event: "retry" } r || error != null) break;
                (carried, carriedSkips, lastCarried) = (carried + r.Failed, carriedSkips + r.DriverSkipped, true);
                if (r.FailedItem >= 0) skip.Add(r.FailedItem);
                skip.AddRange(r.Skip ?? []);
                var blamed = skipKeys.Count;
                skipKeys.UnionWith(r.Crashed ?? []);   // a retry also lists the known keys it skipped
                var removed = r.Reason is "removed" or "fault" or "died";
                var hung = r.Reason == "hang";
                var rtThreads = r.RtThreads;
                if (r.FailedItem >= 0 && ++knownSkips > MaxKnownSkips) rtThreads = Math.Max(1, rtThreads / 4);
                lock (_lock)
                {
                    // an item that died alone is skipped for this run only: no key, yet progress all the same
                    if (removed || hung) removals = skipKeys.Count > blamed || r.Reason == "died" && r.Skip is { Count: > 0 } ? 0 : removals + 1;
                    if (_stopping || n >= MaxProcesses || removals > maxRecoveries)
                    {
                        error = _stopping ? null
                            : removed ? Warmer.CrashedError(crashed.Count)
                            : hung ? Warmer.HungError
                            : $"gave up after {n} processes (the driver keeps faulting)";
                        resumable = (removed || hung) && !_stopping;
                        break;
                    }
                    note = removed ? "recovering from a GPU driver crash"
                        : hung ? "skipping pipelines that hung the GPU driver"
                        : r.FailedItem >= 0 ? $"skipping a ray tracing state object that crashed ({rtThreads} {(rtThreads == 1 ? "thread" : "threads")})"
                        : $"retrying ray tracing with fewer threads ({rtThreads})";
                    progress?.Report(new WarmProgress(Whole(r.Done), passes?.Total ?? r.Total, carried, 0, Growth(), Note: note, DriverSkipped: carriedSkips));
                    _p = Launch(r.From, rtThreads, skip, removed ? r.Isolate ?? [] : []);
                }
            }
            host.Dispose();
            growth = Math.Max(0, Usage() - before);   // after exit: the driver writes its cache then
            // stopped between processes: resumes from the retry point, or from the next pass
            var stopped = done is { Event: "retry" } && _stopping || betweenPasses;
            var outcome = error == null && (stopped || code == 0 && done != null) ? stopped || done!.Stopped ? WarmOutcome.Stopped : WarmOutcome.Completed : WarmOutcome.Failed;
            if (_ended != null) (outcome, error) = (WarmOutcome.Failed, _ended);
            if (outcome == WarmOutcome.Failed)
            {
                var said = stderr.IsEmpty ? "" : ": " + string.Join(" | ", stderr.TakeLast(3));
                error ??= code == null ? Warmer.DiedError + said
                    : code != 0 ? $"scskiller_warm exited with code {code}{said}"
                    : Warmer.NoDoneError;
            }
            var failed = carried + (lastCarried ? 0 : last?.Failed ?? 0);
            var skips = carriedSkips + (lastCarried ? 0 : last?.DriverSkipped ?? 0);
            return new WarmResult(outcome, Whole(last?.Done ?? from), passes?.Total ?? last?.Total ?? 0, failed, clock.Elapsed, growth,
                Path.Combine(stage ?? Path.Combine(workDir, "stage"), "scskiller.log"), outcome == WarmOutcome.Failed ? error : null,
                Crashed: crashed.Count > 0 ? crashed : null, DriverSkipped: skips, Resumable: resumable && outcome == WarmOutcome.Failed && _ended == null,
                AftermathDropped: dropped, CacheKeysAdded: added.Count > 0 ? added : null);
        }
    }

    /// <summary>A later process's progress, as the whole warm's: its failures and skips plus the earlier ones', with the retry
    /// or pass note; a careful warm's total is every pass's items.</summary>
    sealed class Adjusted(IProgress<WarmProgress> inner, long carried, long carriedSkips, string? note, long? total) : IProgress<WarmProgress>
    {
        public void Report(WarmProgress p) => inner.Report(p with { Total = total ?? p.Total, Failed = p.Failed + carried, DriverSkipped = p.DriverSkipped + carriedSkips, Note = note });
    }

    /// <summary>Keeps the PC's RAM for the PC while a compile runs. The driver maps its shader cache file into the compile and
    /// keeps every page it wrote resident: Marvel Rivals' compile held 7-11 GB (a 32 GB cache) and the PC had 0.3-2 GB left
    /// for hours. So each compile process is held, from its start, to its own memory and 1 GB more (<see cref="ProcessTree.Cap"/>,
    /// a hard working-set limit, following its own memory as it grows): Windows takes its oldest pages, the cache already
    /// written, which stay in Windows' standby cache (free for anything else, faulted back without a read if touched), and
    /// never what it works with. Measured on Rivals: emptying the whole working set brought it all back within 15 s, and a
    /// limit only while RAM was short refilled 3 GB within two minutes of each lift and dipped the PC to 0.6 GB.</summary>
    async Task GuardMemory(IProgress<string>? log, string name)
    {
        int capped = 0;   // the process the limit is on
        var said = false;
        while (true)
        {
            await Task.Delay(MemoryCheckEvery);
            IWarmProcess p;
            lock (_lock)
            {
                if (_stopping || Completion?.IsCompleted == true) return;
                if (_paused) continue;
                p = _p;
            }
            try
            {
                if (p.HasExited) continue;   // between processes: the next one is _p soon
                var (ws, mapped) = ProcessTree.Resident(p.Id);
                if (!ProcessTree.Cap(p.Id, CapHeadroom))
                {
                    if (!said) log?.Report($"{name}: compile: Windows refused the compile a memory limit (it holds {ws >> 20:N0} MB)");
                    said = true;
                    continue;
                }
                if (capped != p.Id && mapped > CapHeadroom)
                    log?.Report($"{name}: compile: holds its own memory and {CapHeadroom >> 20:N0} MB of the driver cache's pages: {(mapped - CapHeadroom) >> 20:N0} MB go to Windows' cache");
                capped = p.Id;
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }   // exited meanwhile
        }
    }

    static readonly TimeSpan MemoryCheckEvery = TimeSpan.FromSeconds(3);
    const long CapHeadroom = 1L << 30;

    public void Pause()
    {
        lock (_lock)
        {
            if (_paused || _stopping || _p.HasExited) return;
            _p.Suspend(true);
            _paused = true;
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (!_paused) return;
            _p.Suspend(false);
            _paused = false;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_stopping || Completion.IsCompleted) return;
            _stopping = true;
        }
        Resume();   // a suspended process can't see the event
        try { _host.Stop(); } catch (ObjectDisposedException) { return; }
        // No done event soon after the stop = stuck (the stop event is every process's, so a relaunch sees it too, and none
        // starts after a stop). After the done event the process may be writing the driver cache for minutes (NVIDIA writes
        // at exit): scskiller_warm's own exit limit (600 s) bounds that, not us. An ended run is Failed, so its resume point
        // stays at the last clean one: what it compiled may not be on disk, and the next warm redoes it.
        _ = Task.Run(async () =>
        {
            await Task.WhenAny(_doneSeen.Task, Completion, Task.Delay(_stuckAfter));
            if (_doneSeen.Task.IsCompleted || Completion.IsCompleted) return;
            IWarmProcess p;
            lock (_lock) p = _p;
            _ended = Warmer.StuckError;
            p.Kill();
        });
    }
}

/// <summary>Reads scskiller_warm's stdout and reports its progress without ever making the child wait.</summary>
public static class WarmOutput
{
    static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(10);

    /// <summary>The reader only parses lines and keeps the latest progress; another loop reports it every
    /// <paramref name="every"/> (and every 10 s unchanged, so a silent warm still shows as stalled). A slow listener then
    /// sees the current line, not a backlog: a listener that took seconds per line (cache attribution over a 42 GB driver
    /// cache) once filled the pipe, blocked the child's progress loop on its stdout, and the queue showed numbers 10 minutes
    /// old. The rate reported is the recent one (<see cref="RecentRate"/>), not the child's average since it started.
    /// <paramref name="whole"/> maps a line's done to the reported one (a pass of a careful warm: <see cref="WarmPasses.Overall"/>).
    /// <paramref name="onStage"/> gets the stage event's staging folder.</summary>
    public static async Task<(WarmEvent? Last, WarmEvent? Done, string? Error)> Pump(TextReader stdout, IProgress<WarmProgress>? progress,
        Func<long> cacheGrowth, TimeSpan every, Action? onDone = null, Func<long, long>? whole = null, Action<string>? onStage = null)
    {
        whole ??= d => d;
        WarmEvent? last = null, done = null, latest = null;
        string? error = null;
        var reader = Task.Run(async () =>
        {
            while (await stdout.ReadLineAsync() is { } line)
            {
                if (WarmEvent.Parse(line) is not { } e) continue;
                if (e.Event == "error") error ??= e.Message ?? "unknown error";
                else if (e.Event is "done" or "retry") done = e;   // retry: the process ends, a new one goes on (WarmRun)
                if (e.Event is "done" or "stopping") onDone?.Invoke();   // stopping: the stop was seen, the done event follows
                if (e is { Event: "stage", Stage: { Length: > 0 } st }) onStage?.Invoke(st);
                if (e.Event is "start" or "progress" or "done" or "retry") last = e;
                if (e.Event is "progress" or "done") Volatile.Write(ref latest, e);
            }
        });
        var clock = Stopwatch.StartNew();
        var rate = new RecentRate();
        WarmEvent? reported = null;
        var since = Stopwatch.StartNew();
        void Report(bool final)
        {
            if (Volatile.Read(ref latest) is not { } e) return;
            var d = whole(e.Done);
            rate.Add(clock.Elapsed, d);
            if (progress == null || (e == reported && !final && since.Elapsed < KeepAlive)) return;
            (reported, since) = (e, Stopwatch.StartNew());
            progress.Report(new WarmProgress(d, e.Total, e.Failed, rate.PerSecond ?? e.Rate, cacheGrowth(), Workers: e.Workers, Threads: e.Threads, DriverSkipped: e.DriverSkipped,
                SteadyPerSecond: rate.Steady ?? 0));
        }
        while (await Task.WhenAny(reader, Task.Delay(every)) != reader) Report(false);
        await reader;
        if (Volatile.Read(ref latest) != reported) Report(true);   // the last line (the done event's counts)
        return (last, done, error);
    }
}

/// <summary>Pipelines per second over the last 30 s of (time, done) samples; null until they span 5 s. The child's own
/// rate is its average since it started, which stays high long after a warm slows down (the cached start is fast).</summary>
public sealed class RecentRate
{
    static readonly TimeSpan Window = TimeSpan.FromSeconds(30), MinSpan = TimeSpan.FromSeconds(5), SteadyWindow = TimeSpan.FromMinutes(5),
        SteadyEvery = TimeSpan.FromSeconds(5);
    readonly Queue<(TimeSpan At, long Done)> _samples = new(), _steady = new();

    public void Add(TimeSpan at, long done)
    {
        _samples.Enqueue((at, done));
        while (_samples.Count > 2 && at - _samples.Peek().At > Window) _samples.Dequeue();
        if (_steady.Count == 0 || at - _steady.Last().At >= SteadyEvery) _steady.Enqueue((at, done));
        while (_steady.Count > 2 && at - _steady.Peek().At > SteadyWindow) _steady.Dequeue();
    }

    /// <summary>Pipelines per second over the last 5 minutes, once they span a minute: what time left is reckoned from. A big
    /// game's rate swings 3x from one 30 s to the next (Marvel Rivals: 65 to 220 a second), and time left with it.</summary>
    public double? Steady
    {
        get
        {
            if (_steady.Count < 2) return null;
            var (t0, d0) = _steady.Peek();
            var (t1, d1) = _steady.Last();
            return t1 - t0 >= TimeSpan.FromMinutes(1) ? Math.Max(0, d1 - d0) / (t1 - t0).TotalSeconds : null;
        }
    }

    public double? PerSecond
    {
        get
        {
            if (_samples.Count < 2) return null;
            var (t0, d0) = _samples.Peek();
            var (t1, d1) = _samples.Last();
            return t1 - t0 >= MinSpan ? Math.Max(0, d1 - d0) / (t1 - t0).TotalSeconds : null;
        }
    }
}

/// <summary>NtSuspendProcess / NtResumeProcess on a process and all its descendants (scskiller_warm runs the staged
/// copy named like the game as a child).</summary>
static class ProcessTree
{
    /// <summary>A suspended tree also gives its memory back (<see cref="Trim"/>): a paused compile, or one paused while a game
    /// plays, held 7-11 GB (Marvel Rivals: 4 GB of its own and the driver cache's pages it had written) and the game ran short.
    /// Resumed, it faults back the pages it touches.</summary>
    public static void Suspend(int rootPid, bool suspend)
    {
        foreach (var pid in WithDescendants(rootPid))
        {
            var h = OpenProcess(0x0800 /* PROCESS_SUSPEND_RESUME */ | (suspend ? TrimAccess : 0), false, pid);
            if (h == 0 && suspend) h = OpenProcess(0x0800, false, pid);
            if (h == 0) continue;   // exited meanwhile
            if (suspend) { _ = NtSuspendProcess(h); _ = K32EmptyWorkingSet(h); }
            else _ = NtResumeProcess(h);
            CloseHandle(h);
        }
    }

    const uint TrimAccess = 0x0400 /* PROCESS_QUERY_INFORMATION */ | 0x0100 /* PROCESS_SET_QUOTA */;

    /// <summary>The tree's resident memory, and what of it isn't its own (pages of files it maps: the driver's shader cache).</summary>
    public static (long WorkingSet, long Mapped) Resident(int rootPid)
    {
        long ws = 0, mapped = 0;
        foreach (var pid in WithDescendants(rootPid))
        {
            var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == 0) continue;
            var c = new MemoryCounters { Cb = Marshal.SizeOf<MemoryCounters>() };
            if (K32GetProcessMemoryInfo(h, ref c, c.Cb)) { ws += (long)c.WorkingSetSize; mapped += Math.Max(0, (long)c.WorkingSetSize - (long)c.PagefileUsage); }
            CloseHandle(h);
        }
        return (ws, mapped);
    }

    /// <summary>Holds each process of the tree to its own committed memory plus <paramref name="headroom"/> (a hard working-set
    /// maximum: Windows trims its least recently used pages to stay under it), or lifts that limit (null). False when Windows
    /// refused every process the limit.</summary>
    public static bool Cap(int rootPid, long? headroom)
    {
        var any = false;
        foreach (var pid in WithDescendants(rootPid))
        {
            var h = OpenProcess(TrimAccess, false, pid);
            if (h == 0) continue;
            var c = new MemoryCounters { Cb = Marshal.SizeOf<MemoryCounters>() };
            if (K32GetProcessMemoryInfo(h, ref c, c.Cb))
            {
                var max = (long)c.PagefileUsage + (headroom ?? 0) + (64L << 20);
                any |= SetProcessWorkingSetSizeEx(h, 4 << 20, (nint)max, headroom != null ? 0x4u /* QUOTA_LIMITS_HARDWS_MAX_ENABLE */ | 0x2u /* MIN_DISABLE */
                    : 0x8u /* QUOTA_LIMITS_HARDWS_MAX_DISABLE */ | 0x2u);
            }
            CloseHandle(h);
        }
        return any;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryCounters
    {
        public int Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
            QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("kernel32.dll")] static extern bool K32EmptyWorkingSet(nint h);
    [DllImport("kernel32.dll")] static extern bool K32GetProcessMemoryInfo(nint h, ref MemoryCounters c, int cb);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSizeEx(nint h, nint min, nint max, uint flags);

    /// <summary>One toolhelp snapshot: no process is opened; Exe has its extension.</summary>
    public static List<(int Pid, int Parent, string Exe)> Snapshot()
    {
        var all = new List<(int, int, string)>();
        var snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == -1) throw new Win32Exception();
        try
        {
            var e = new ProcessEntry32 { Size = Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e)) all.Add((e.Pid, e.ParentPid, e.ExeFile));
        }
        finally { CloseHandle(snap); }
        return all;
    }

    /// <summary><see cref="Snapshot"/>, at most a second old.</summary>
    public static List<(int Pid, int Parent, string Exe)> RecentSnapshot()
    {
        lock (SnapshotLock)
        {
            if (_snapshot == null || Environment.TickCount64 - _snapshotAt > 1000) (_snapshot, _snapshotAt) = (Snapshot(), Environment.TickCount64);
            return _snapshot;
        }
    }

    static readonly Lock SnapshotLock = new();
    static List<(int, int, string)>? _snapshot;
    static long _snapshotAt;

    internal static List<int> WithDescendants(int root)
    {
        var all = Snapshot();
        var linked = new HashSet<int> { root };   // by the parent links alone: only these are opened for their creation times
        for (var more = true; more;)
        {
            more = false;
            foreach (var p in all) more |= linked.Contains(p.Parent) && linked.Add(p.Pid);
        }
        return Tree(root, all.Where(p => linked.Contains(p.Pid)).Select(p => (p.Pid, p.Parent, Created(p.Pid))));
    }

    /// <summary><paramref name="root"/> and its descendants among <paramref name="procs"/>. A child counts only if it was
    /// created at or after its parent: Windows reuses an exited process's pid, so an older process whose parent exited names
    /// a pid that may now be ours. A process without a creation time (it couldn't be opened) is left out with its
    /// descendants; a root without one, or not listed, is the whole tree.</summary>
    internal static List<int> Tree(int root, IEnumerable<(int Pid, int Parent, long? Created)> procs)
    {
        var all = procs.ToList();
        if (all.FirstOrDefault(p => p.Pid == root).Created is not { } rootCreated) return [root];
        var tree = new List<(int Pid, long Created)> { (root, rootCreated) };
        var seen = new HashSet<int> { root };
        for (int i = 0; i < tree.Count; i++)
            foreach (var p in all)
                if (p.Parent == tree[i].Pid && p.Created is { } c && c >= tree[i].Created && seen.Add(p.Pid)) tree.Add((p.Pid, c));
        return tree.Select(t => t.Pid).ToList();
    }

    /// <summary>The process's image path, without opening the process (no handle to it: nothing an anti-cheat driver's handle
    /// callbacks see): NtQuerySystemInformation(SystemProcessIdInformation) gives its NT path, mapped to a drive letter.
    /// Null when it has exited or its volume has no drive letter.</summary>
    public static unsafe string? ImagePath(int pid)
    {
        const int SystemProcessIdInformation = 88;
        var name = new char[32768];
        fixed (char* buffer = name)
        {
            // SYSTEM_PROCESS_ID_INFORMATION (x64): ProcessId, then UNICODE_STRING { u16 Length, u16 MaximumLength, PWSTR Buffer at 8 }
            var info = stackalloc byte[24];
            *(nint*)info = pid;
            *(ushort*)(info + 8) = 0;
            *(ushort*)(info + 10) = (ushort)(name.Length * 2 - 2);
            *(char**)(info + 16) = buffer;
            if (NtQuerySystemInformation(SystemProcessIdInformation, info, 24, out _) < 0) return null;
            return DosPath(new string(buffer, 0, *(ushort*)(info + 8) / 2), DosDevices());
        }
    }

    /// <summary>An NT path (\Device\HarddiskVolume3\...) on a drive letter's device, as a DOS path; null when no drive maps it.</summary>
    internal static string? DosPath(string nt, IEnumerable<(string Drive, string Device)> devices)
    {
        // Wine names images in the DOS devices namespace (\??\C:\...)
        if (nt.Length > 7 && nt.StartsWith(@"\??\", StringComparison.Ordinal) && char.IsAsciiLetter(nt[4]) && nt[5] == ':' && nt[6] == '\\') return nt[4..];
        foreach (var (drive, device) in devices)
            if (nt.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase)) return drive + nt[device.Length..];
        return null;
    }

    static IEnumerable<(string Drive, string Device)> DosDevices()
    {
        var target = new char[1024];
        foreach (var root in Environment.GetLogicalDrives())
        {
            var drive = root.TrimEnd(Path.DirectorySeparatorChar);
            if (QueryDosDeviceW(drive, target, target.Length) > 0) yield return (drive, new string(target).Split((char)0)[0]);
        }
    }

    /// <summary>The process's creation time (FILETIME); null when it can't be opened or has exited.</summary>
    internal static long? Created(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try { return GetProcessTimes(h, out var created, out _, out _, out _) ? created : null; }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public int Size, Usage, Pid;
        public nint DefaultHeapId;
        public int ModuleId, Threads, ParentPid, PriClassBase, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(nint snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(nint snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("ntdll.dll")] static extern unsafe int NtQuerySystemInformation(int infoClass, void* info, int length, out int returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int QueryDosDeviceW(string device, [Out] char[] target, int max);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(nint h, out long created, out long exited, out long kernel, out long user);
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(nint h);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(nint h);
}

/// <summary>Finds the native binaries (scskiller_warm.exe, the proxy d3d12.dll): next to the app (native\ first),
/// else, for dev builds, the nearest proxy\build\Release above the app folder.</summary>
public static class NativeTools
{
    public static string? Find(string fileName)
    {
        var app = AppContext.BaseDirectory;   // the published CLI lives in cli\ next to the app's native\
        foreach (var p in new[] { Path.Combine(app, "native", fileName), Path.Combine(app, fileName), Path.GetFullPath(Path.Combine(app, "..", "native", fileName)) })
            if (File.Exists(p)) return p;
        for (var d = new DirectoryInfo(app); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "proxy", "build", "Release", fileName);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
