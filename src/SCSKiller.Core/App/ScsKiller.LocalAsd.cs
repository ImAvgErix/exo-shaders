using System.Text.Json;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.App;

/// <summary>
/// Exo as a local Advanced Shader Delivery installer. A compile's work folder becomes the game's state object database
/// (<see cref="Sodb"/>, games\&lt;id&gt;\asd\game.sodb, driver-independent), which after a complete warm is compiled at
/// idle priority by Microsoft's compiler with this driver's own plugin into a precompiled shader database, registered in
/// user scope as installer "Exo" for the game's real exe (<see cref="AsdInstaller"/>). D3D12 then loads the game's PSOs
/// from it: no driver-cache cap or eviction, and the exact identity the game asks for. A driver update (Windows' own
/// listener, and at start) rebuilds only the database from the kept SODB, when the PC is idle and nothing compiles.
/// Never over a store's database (<see cref="ShaderDelivery.Covers"/>, <see cref="AsdInstaller.OthersCover"/>); undone
/// per game (<see cref="UndoLocalAsd"/>, which also keeps it off for the game) and at Exo Shaders' uninstall
/// (<see cref="RemoveAllLocalAsd"/>).
/// </summary>
public sealed partial class ScsKiller
{
    /// <summary>games\&lt;id&gt;\asd\asd.json. <paramref name="Driver"/>: the driver its PSDB was built with (null: none
    /// built); <paramref name="Off"/>: the user undid it for this game; <paramref name="Source"/>: a store's SODB for the
    /// same game installed elsewhere, built from in place of one of Exo's own (<see cref="AdoptStoreSodbs"/>).</summary>
    public sealed record LocalAsdState(string Exe, string? Family = null, string? Driver = null, string? Psdb = null,
        int Stored = 0, int Compiled = 0, int Failed = 0, bool Registered = false, bool Off = false, string? Source = null);

    const string AsdFolder = "asd", AsdStateFile = "asd.json", SodbFile = "game.sodb";
    static readonly JsonSerializerOptions AsdJson = new() { WriteIndented = true };
    IDisposable? _asdListener;
    int _asdRefreshing;

    string AsdDir(string id) => Path.Combine(Store.GameDir(id), AsdFolder);

    public LocalAsdState? LoadLocalAsd(string id)
    {
        try { return JsonSerializer.Deserialize<LocalAsdState>(File.ReadAllText(Path.Combine(AsdDir(id), AsdStateFile))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    void SaveLocalAsd(string id, LocalAsdState s)
    {
        Directory.CreateDirectory(AsdDir(id));
        AppStore.WriteAtomic(Path.Combine(AsdDir(id), AsdStateFile), JsonSerializer.SerializeToUtf8Bytes(s, AsdJson));
    }

    /// <summary>On (the service, outside safe mode): Windows' driver-update listener, and the databases a driver update
    /// while Exo wasn't running left behind are rebuilt once the PC is idle.</summary>
    public bool LocalAsd
    {
        get => _asdListener != null;
        set
        {
            if (!value) { _asdListener?.Dispose(); _asdListener = null; return; }
            if (_asdListener != null || !OperatingSystem.IsWindows() || !AsdInstaller.Present()) return;
            try { _asdListener = AsdInstaller.Listen(() => _ = Task.Run(RefreshLocalAsd)); }
            catch (IOException e) { Log?.Report($"precompiled shaders: no driver-update listener ({e.Message})"); }
            _ = Task.Run(RefreshLocalAsd);
        }
    }

    /// <summary>The adapter of <paramref name="gpu"/> as the compiler lists it, when its driver has a compiler plugin; null
    /// otherwise (or the compiler can't be had).</summary>
    async Task<(string Tools, AsdTools.Adapter Adapter)?> AsdAdapter(GpuInfo gpu, CancellationToken ct)
    {
        string tools;
        try { tools = AsdTools.Ensure(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log?.Report($"precompiled shaders: no compiler ({e.Message})");
            return null;
        }
        var (_, list) = await AsdTools.Run(tools, ["list", "--adapters"], ct);
        var adapters = AsdTools.ParseAdapters(list);
        return adapters.FirstOrDefault(a => a.Family != null && a.Name == gpu.Name) is { } a ? (tools, a) : null;
    }

    /// <summary>After a compile's materialize (its whole work folder, before what was compiled is left out): the game's
    /// SODB. Never throws: a game without one is only warmed.</summary>
    void WriteSodb(Game game, GameRecord rec, string work, CancellationToken ct)
    {
        try
        {
            if (rec.Plan is not { } plan || plan.Platform == "vulkan" || !OperatingSystem.IsWindows() || !AsdInstaller.Present()) return;
            if (LoadLocalAsd(game.Id) is { Off: true }) return;
            var gpu = Snapshot().Gpu;
            if (ShaderDelivery.Covers(game.ExePath, gpu)) return;   // the store's database: nothing to add
            string tools;
            try { tools = AsdTools.Ensure(); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { Log?.Report($"{game.Name}: precompiled shaders: no compiler ({e.Message})"); return; }
            Directory.CreateDirectory(AsdDir(game.Id));
            var tmp = Path.Combine(AsdDir(game.Id), SodbFile + ".tmp");
            var r = Sodb.Write(Path.Combine(tools, AsdTools.Dll), tmp, Path.GetFileName(game.ExePath), AsdName(game),
                [Path.Combine(work, "scskiller.db"), Path.Combine(work, "scskiller_gen.db")], ct);
            if (r.Stored == 0) { File.Delete(tmp); return; }
            File.Move(tmp, Path.Combine(AsdDir(game.Id), SodbFile), overwrite: true);   // a registered PSDB was built from the old one: the next build replaces it
            var s = LoadLocalAsd(game.Id) ?? new LocalAsdState(game.ExePath);
            SaveLocalAsd(game.Id, s with { Exe = game.ExePath, Stored = r.Stored, Source = null });   // its own compile's, over a store's
            Log?.Report($"{game.Name}: precompiled shaders: {r.Stored:N0} PSOs written for Windows' compiler" + (r.Skipped > 0 ? $", {r.Skipped:N0} left out" : ""));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Log?.Report($"{game.Name}: precompiled shaders: no database written ({e.Message})"); }
    }

    /// <summary>The application name a game is registered under (its registry key under the installer's).</summary>
    static string AsdName(Game game) => game.Name.Replace('\\', '-').Trim() is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(game.ExePath);

    /// <summary>After a complete warm, and for a driver update: the game's SODB compiled with this driver's plugin at idle
    /// priority, and registered for its exe. Never throws: a cancel kills the compiler and leaves the last registration.</summary>
    async Task CompileLocalAsd(Game game, CancellationToken ct)
    {
        var id = game.Id;
        try
        {
            if (LoadLocalAsd(id) is not { Off: false } s) return;
            var sodb = s.Source ?? Path.Combine(AsdDir(id), SodbFile);
            if (!File.Exists(sodb) || !File.Exists(game.ExePath)) return;
            var gpu = Snapshot().Gpu;
            if (await AsdAdapter(gpu, ct) is not var (tools, adapter)) return;
            if (ShaderDelivery.Covers(game.ExePath, gpu) || AsdInstaller.OthersCover(game.ExePath, adapter.Family!))
            {
                if (s.Registered) UndoRegistration(game.Name, game.ExePath);
                SaveLocalAsd(id, s with { Registered = false });
                return;
            }
            // a new name each build: a running game holds the registered one open
            var psdb = Path.Combine(AsdDir(id), $"{adapter.Family}-{gpu.DriverVersion}-{DateTime.UtcNow:yyyyMMddHHmmss}.psdb");
            var tmp = psdb + ".tmp";
            File.Delete(tmp);
            var (exit, output) = await AsdTools.Run(tools, ["compile", "--adapter", adapter.Index.ToString(), "--priority", "idle",
                "--max-threads", Math.Max(1, Settings.BackgroundThreads).ToString(), "--psos", sodb, tmp], ct);
            var built = AsdTools.ParseCompiled(output);
            if (exit != 0 || built is not { Created: > 0 } b || !File.Exists(tmp))
            {
                File.Delete(tmp);
                Log?.Report($"{game.Name}: precompiled shaders: Windows' compiler built none (exit {exit})");
                return;
            }
            File.Move(tmp, psdb, overwrite: true);
            AsdInstaller.Register(game.ExePath, AsdName(game), sodb, adapter.Family!, psdb);
            if (s.Psdb is { } old && !old.Equals(psdb, StringComparison.OrdinalIgnoreCase)) TryDelete(old);   // a game running now keeps it open: left for the next build
            SaveLocalAsd(id, s with { Exe = game.ExePath, Family = adapter.Family, Driver = gpu.DriverVersion, Psdb = psdb, Compiled = b.Created, Failed = b.Failed, Registered = true });
            Log?.Report($"{game.Name}: precompiled shaders: {b.Created:N0} PSOs compiled for {adapter.Family} and registered" + (b.Failed > 0 ? $" ({b.Failed:N0} the driver's compiler refused)" : ""));
        }
        catch (OperationCanceledException) { Log?.Report($"{game.Name}: precompiled shaders: stopped"); }
        catch (Exception e) { Log?.Report($"{game.Name}: precompiled shaders: not built ({e.Message})"); }
    }

    /// <summary>The game's registration and databases taken out, and kept off for it until <paramref name="off"/> is false.</summary>
    public void UndoLocalAsd(string id, bool off = true)
    {
        var s = LoadLocalAsd(id);
        var exe = s?.Exe ?? Find(id).Game.ExePath;
        UndoRegistration(id, exe);
        foreach (var f in Directory.Exists(AsdDir(id)) ? Directory.GetFiles(AsdDir(id)) : []) if (!f.EndsWith(AsdStateFile, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
        SaveLocalAsd(id, new LocalAsdState(exe, Off: off));
    }

    void UndoRegistration(string who, string exe)
    {
        try { if (AsdInstaller.Present()) AsdInstaller.Remove(exe); }
        catch (IOException e) { Log?.Report($"{who}: precompiled shaders: registration not removed ({e.Message})"); }
    }

    /// <summary>Every registration Exo made (Exo Shaders' uninstall: <c>ExoShaders.Service.exe --asd-remove-all</c>).</summary>
    public static void RemoveAllLocalAsd()
    {
        if (OperatingSystem.IsWindows() && AsdInstaller.Present()) AsdInstaller.RemoveAll();
    }

    /// <summary>Once the PC is idle and nothing compiles: registrations of exes gone (an uninstalled game) taken out, and the
    /// databases another driver built rebuilt from their SODBs, one game at a time under its compile lock.</summary>
    async Task RefreshLocalAsd()
    {
        if (Interlocked.Exchange(ref _asdRefreshing, 1) == 1) return;
        try
        {
            while (Compiling || QueueRunning || !UserIdle || _playing.Count > 0) await Task.Delay(Poll);
            RedetectGpu();
            var gpu = Snapshot().Gpu;
            foreach (var r in AsdInstaller.List())
                if (!File.Exists(r.ExePath))
                {
                    AsdInstaller.Remove(r.ExePath);
                    Log?.Report($"precompiled shaders: {r.Name} unregistered, its exe is gone");
                }
            AdoptStoreSodbs();
            foreach (var game in Games.Select(g => g.Game))
            {
                // Built before for another driver, or a store's pipelines adopted and not built yet.
                if (LoadLocalAsd(game.Id) is not { Off: false } s || !(s.Registered || s.Source != null) || s.Driver == gpu.DriverVersion) continue;
                while (Compiling || QueueRunning || !UserIdle || _playing.Count > 0) await Task.Delay(Poll);
                using var own = CompileLock(game.Id);
                if (own == null) continue;   // a compile of it runs: its own build follows its warm
                await CompileLocalAsd(game, CancellationToken.None);
            }
        }
        catch (Exception e) { Log?.Report($"precompiled shaders: refresh stopped ({e.Message})"); }
        finally { Volatile.Write(ref _asdRefreshing, 0); }
    }

    /// <summary>
    /// A game a store delivered precompiled shaders for, installed here from another store (or as another copy): Windows
    /// has no database for this copy, but the store's SODB lists every pipeline the game builds, by content. It becomes
    /// this copy's source, and the pass after builds and registers a database from it for this driver, with no compile
    /// or recording of its own. Never over a database of the copy's own, Exo's own compile or a game turned off.
    /// </summary>
    void AdoptStoreSodbs()
    {
        var gpu = Snapshot().Gpu;
        foreach (var game in Games.Select(g => g.Game))
        {
            var s = LoadLocalAsd(game.Id);
            if (s is { Off: true } || s?.Source != null || File.Exists(Path.Combine(AsdDir(game.Id), SodbFile))) continue;
            if (!File.Exists(game.ExePath) || ShaderDelivery.Covers(game.ExePath, gpu)) continue;
            if (AsdInstaller.StoreSodbFor(game.ExePath, game.Name) is not { } sodb) continue;
            SaveLocalAsd(game.Id, (s ?? new LocalAsdState(game.ExePath)) with { Exe = game.ExePath, Source = sodb, Driver = null });
            Log?.Report($"{game.Name}: precompiled shaders: the store's pipelines for this game ({Path.GetFileName(sodb)}) will be built for this copy");
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
