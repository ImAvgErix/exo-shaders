// Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 The SCSKiller authors
// Copyright (C) 2026 Exo Shaders contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
// version, with the additional permission in LICENSE-EXCEPTION.txt. It is distributed WITHOUT ANY WARRANTY; see LICENSE.
//
// Hosts one ScsKiller the way the SCSKiller 1.3.0-beta.1 app does (SCSKiller.App.App.Launch / QuitAsync), minus the
// window, the tray, the updater and every SCSKiller server feature that needs an account or sends anything (community
// database, sharing, diagnostics, problem reports, the active check, the Patreon account): see Start.

using System.Collections.Concurrent;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace ExoShaders.Service;

internal sealed record Options(bool Safe, bool LogStderr);

internal sealed partial class Host
{
    public const int ProtocolVersion = 1;

    private readonly Protocol _io;
    private readonly Options _opt;
    private ScsKiller _k = null!;
    private TeeLog _log = null!;

    private readonly CancellationTokenSource _stopWatching = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task _watcher = Task.CompletedTask;
    private readonly TaskCompletionSource<string> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _quitting;

    // ---- coalesced events
    private readonly SemaphoreSlim _signal = new(0, 1);
    private int _stateDirty, _progressDirty;
    private long _rev;
    private string _queueShape = "";
    private readonly Lock _shapeGate = new();

    // ---- per-game UI state
    private readonly ConcurrentDictionary<string, string> _errors = new();
    private readonly ConcurrentDictionary<string, long> _otherCaches = new();
    private readonly ConcurrentDictionary<string, (GameState State, string? Current, string? Blocked)> _blocked = new();
    private int _scans;
    private volatile bool _uiVisible;
    private volatile bool _logEvents;

    public Host(Protocol io, Options opt)
    {
        _io = io;
        _opt = opt;
    }

    public ScsKiller K => _k;
    public bool Safe => _opt.Safe;

    // =================================================================== startup

    public async Task<int> RunAsync()
    {
        Start();
        Task firstScan = FirstScan();   // counts as scanning from here on, so the first state already says so
        _io.Event("ready", new
        {
            protocol = ProtocolVersion,
            service = Program.ServiceVersion,
            engine = AppVersion.Current.ToString(),
            dataDir = _k.Store.DataDir,
            pid = Environment.ProcessId,
            safe = _opt.Safe,
        });
        MarkState();

        Task reader = Task.Run(ReadLoop);
        Task emitter = Task.Run(EmitLoop);
        if (Program.OfficialDataDir(_k.Store.DataDir)) _ = Task.Run(WatchConflict);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _ = QuitAsync("signal", null);
        };

        string reason = await _exit.Task;
        _stopping.Cancel();
        _io.Event("exiting", new { reason });
        return 0;
    }

    private void Start()
    {
        ScsKiller k = ScsKiller.CreateDefault();
        _k = k;
        _log = new TeeLog(new AppLog(k.Store.DataDir), _opt.LogStderr, line => { if (_logEvents) _io.Event("log", new { line }); });
        k.Log = _log;
        Codecs.Log = _log;
        _log.Report($"[exo] Exo Shaders service {Program.ServiceVersion} (SCSKiller {AppVersion.Current} engine) started on Windows {Environment.OSVersion.Version}, {k.Vendor.Gpu.Name}, driver {k.Vendor.Gpu.DriverVersion}{(_opt.Safe ? ", safe mode" : "")}");

        // The app wires Community, Sharing, Diagnostics, ContentRoutes, ChannelAccess, UserFetch and ActiveCheck to
        // SCSKiller's servers and the Patreon account here. Exo Shaders reads what SCSKiller's app reads signed out, and
        // sends nothing:
        // - ContentRoutes: after a scan, at most about daily, the engine-support updates (plan-rules.json, game verdicts,
        //   Maximum suggestions and news, each used only when signed with SCSKiller's pinned rules keys, ContentTrust)
        //   and the known-stutter, confirmed-engines and boot-precompile lists. Stable channel only (no ChannelAccess).
        // - Community, with no token: the manifest, the official recordings marked for everyone and the shared upscaler
        //   packs, all plain downloads. Patreon and backer recordings and the community database need a token: never.
        // Sharing (uploads), Diagnostics, ActiveCheck (the daily check), UserFetch (the account and the app's updater)
        // and Report a problem stay unset. No Patreon entitlement.
        if (!_opt.Safe)
        {
            k.ContentRoutes = RouteFailover.Default;
            k.Community = new Community(AppStore.DefaultDir, (_, _) => Task.FromResult<string?>(null));
        }
        k.DbEntitled = () => false;
        k.ManageRecorders = !_opt.Safe;
        k.CleanupHelper = _opt.Safe ? null : Environment.ProcessPath;
        k.CheckPlans = !_opt.Safe;
        k.UseChannels(null);   // what the app does while signed out
        k.Unseen = () => !_uiVisible;
        k.IdleWatchInterval = TimeSpan.FromSeconds(8);   // 3 s while a game plays, is followed or compiles

        k.GameChanged += OnGameChanged;
        k.QueueChanged += OnQueueChanged;
        k.ScanProgressed += MarkState;
        k.GpuChanged += OnGpuChanged;

        foreach (GameState g in k.Games) _baSeen[g.Game.Id] = (g.BeforeAfter?.Stage, false);

        if (!_opt.Safe) _watcher = k.WatchGames(_stopWatching.Token);
        // The driver-update task names the program it runs; the official app registers
        // it again at every start, so after a move (or SCSKiller's uninstall, which
        // removes it) it points at this engine's command line.
        if (!_opt.Safe && k.Settings.OnDriverUpdate != DriverUpdateMode.Off)
            _ = Task.Run(() =>
            {
                try { k.ApplyDriverUpdateMode(); }
                catch (Exception e) { _log.Report("[exo] driver-update task not registered: " + e.Message); }
            });
        RequestCacheInfo(force: true);
    }

    private async Task FirstScan()
    {
        // The Library page scans when it loads (LibraryVm.Load); a --tray start scans too.
        try
        {
            await ScanAsync(force: false, userRequested: false);
        }
        catch (Exception e)
        {
            _io.Event("notice", new { kind = "error", text = "Scan failed: " + Fmt.Describe(e) });
        }
        ShowDriverNotice();
        if (!_opt.Safe) RequestNewShadersCheck();
        StartPolicy();
    }

    public async Task<IReadOnlyList<GameState>> ScanAsync(bool force, bool userRequested)
    {
        Interlocked.Increment(ref _scans);
        MarkState();
        try
        {
            return await (force ? _k.RescanAsync(_stopping.Token, userRequested) : _k.ScanAsync(_stopping.Token, userRequested));
        }
        finally
        {
            Interlocked.Decrement(ref _scans);
            MarkState();
            RequestCacheInfo(force: true);
        }
    }

    public bool Scanning => Volatile.Read(ref _scans) > 0 || _k.ScanProgress != null;

    /// <summary>
    /// The official app can't see the service (its single-instance key is its own), so if it starts on the same data
    /// folder anyway the service steps aside: it stops like a quit, so the two engines never work on the same data.
    /// </summary>
    private async Task WatchConflict()
    {
        CancellationToken ct = _stopping.Token;
        while (!ct.IsCancellationRequested && !Quitting)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { return; }
            if (Volatile.Read(ref _elevated) > 0) continue;   // our own CLI, elevated, can't be told apart by its path
            string? other;
            try { other = Program.OfficialRunning(); }
            catch (Exception) { continue; }
            if (other is null) continue;
            string text = $"The official SCSKiller app started ({other}) on the same data folder: Exo Shaders stopped its engine.";
            _log.Report("[exo] " + text);
            _io.Event("notice", new { kind = "conflict", text });
            await QuitAsync("conflict", null);
            return;
        }
    }

    private int _elevated;

    // =================================================================== requests

    private async Task ReadLoop()
    {
        await foreach (string line in Protocol.Lines())
        {
            System.Text.Json.JsonElement? id = null;
            string? method;
            System.Text.Json.JsonElement? p;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                System.Text.Json.JsonElement root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) throw new ServiceError("a request must be a JSON object");
                if (root.TryGetProperty("id", out var idEl)) id = idEl.Clone();
                method = root.TryGetProperty("method", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString() : null;
                p = root.TryGetProperty("params", out var pe) ? pe.Clone() : null;
                if (method is null) throw new ServiceError("missing 'method'");
            }
            catch (Exception e)
            {
                _io.Error(id, e is ServiceError ? e.Message : "not a valid request: " + e.Message);
                continue;
            }
            System.Text.Json.JsonElement? reqId = id;
            string name = method;
            Params args = new(p);
            _ = Task.Run(async () =>
            {
                try
                {
                    object? result = await Dispatch(name, args);
                    // The changelog is SCSKiller's own history and keeps its name.
                    _io.Result(reqId, result, brand: !name.StartsWith("whatsNew", StringComparison.Ordinal));
                    if (name == "shutdown") _ = QuitAsync("shutdown", args.Int("waitSeconds"));
                }
                catch (Exception e)
                {
                    if (e is not ServiceError) _log.Report($"[exo] {name} failed: {e}");
                    _io.Error(reqId, Fmt.Describe(e));
                }
            });
        }
        await QuitAsync("stdinClosed", null);
    }

    // =================================================================== events

    private void OnGameChanged(GameState g)
    {
        MarkState();
        if (!_opt.Safe) RequestNewShadersCheck();
        WatchBeforeAfter(g);
        RequestPolicy();
        GamingPause();
    }

    private void OnQueueChanged(QueueItem q)
    {
        string shape = QueueShape(_k.Queue);
        bool structural;
        using (_shapeGate.EnterScope())
        {
            structural = shape != _queueShape;
            _queueShape = shape;
        }
        if (structural)
        {
            MarkState();
            if (q.Stage is QueueStage.Done or QueueStage.Failed or QueueStage.Stopped) RequestCacheInfo(force: false);
            GamingPause();
        }
        else MarkProgress();
    }

    private static string QueueShape(IEnumerable<QueueItem> queue) =>
        string.Join("|", queue.Select(q => $"{q.GameId}:{q.Stage}:{q.Note}:{q.Error}:{q.PlanCheck}"));

    private void OnGpuChanged()
    {
        MarkState();
        RequestCacheInfo(force: true);
        if (_k.GpuRestartNote == null) _ = NotifyDriverUpdateAsync();
    }

    public void MarkState()
    {
        Interlocked.Exchange(ref _stateDirty, 1);
        Signal();
    }

    private void MarkProgress()
    {
        Interlocked.Exchange(ref _progressDirty, 1);
        Signal();
    }

    private void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task EmitLoop()
    {
        CancellationToken ct = _stopping.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct);
                // The page said it's hidden (ui.visible false): state and progress wait, still marked, until it's
                // shown again; nothing would read them, and each one costs Exo a parse, a copy and a relay.
                if (_uiHidden) continue;
                bool state = Interlocked.Exchange(ref _stateDirty, 0) == 1;
                bool progress = Interlocked.Exchange(ref _progressDirty, 0) == 1;
                if (state)
                {
                    // Built with a placeholder rev; an unchanged state (a scan re-evaluating every game, a
                    // refresh that changed nothing) isn't sent again.
                    byte[] body = Protocol.Brand(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Snapshot(fixedRev: 0), Protocol.Json));
                    if (_lastState is null || !body.AsSpan().SequenceEqual(_lastState))
                    {
                        _lastState = body;
                        long rev = Interlocked.Increment(ref _rev);
                        _io.EventRaw("state", WithRev(body, rev));
                    }
                    else Interlocked.Increment(ref _statesSkipped);
                }
                else if (progress)
                {
                    long rev = Interlocked.Increment(ref _rev);
                    IReadOnlyList<GameState> games = _k.Games;
                    IReadOnlyList<QueueItem> queue = _k.Queue;
                    // the running game's progress lines, so a game page can update without a full state
                    object? game = queue.FirstOrDefault(q => Format.Running(q) && !q.PlanCheck) is { } run
                        && games.FirstOrDefault(g => g.Game.Id == run.GameId) is { } rg
                        && new GameView(rg, Context(games, queue)) is var v
                            ? new { id = rg.Game.Id, statusTitle = v.StatusTitle, statusReason = v.StatusReason, percent = Fmt.Indeterminate(run) ? (double?)null : Math.Round(Fmt.Percent(run), 1) }
                            : null;
                    _io.Event("progress", new { rev, queue = QueueDto(games, queue), game });
                }
                await Task.Delay(250, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _log.Report("[exo] building the state failed: " + e);
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public void SetError(string id, string? error)
    {
        if (error is null) _errors.TryRemove(id, out _);
        else _errors[id] = error;
        MarkState();
    }

    public void UiVisible(bool visible)
    {
        _uiVisible = visible;
        _uiHidden = !visible;
        if (visible)
        {
            _lastState = null;   // the page may have missed states while hidden: the next one goes out in any case
            MarkState();
        }
    }

    // Until the page says otherwise it's treated as shown, so a host that never sends ui.visible gets every state.
    private volatile bool _uiHidden;
    private byte[]? _lastState;
    private long _statesSkipped;

    /// <summary>`{"rev":0,...}` (the placeholder Snapshot(fixedRev: 0) writes first) with the real rev.</summary>
    private static byte[] WithRev(byte[] body, long rev)
    {
        ReadOnlySpan<byte> placeholder = "{\"rev\":0,"u8;
        if (!body.AsSpan().StartsWith(placeholder)) throw new InvalidOperationException("the state doesn't start with its rev");
        byte[] head = System.Text.Encoding.UTF8.GetBytes("{\"rev\":" + rev.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",");
        byte[] r = new byte[head.Length + body.Length - placeholder.Length];
        head.CopyTo(r, 0);
        body.AsSpan(placeholder.Length).CopyTo(r.AsSpan(head.Length));
        return r;
    }

    public void LogEvents(bool on) => _logEvents = on;

    // =================================================================== driver update notice (App.ShowDriverToast)

    private bool _checkingDriver, _checkAgain;
    private string? _toldDriver;
    private readonly Lock _driverGate = new();

    private async Task NotifyDriverUpdateAsync()
    {
        using (_driverGate.EnterScope())
        {
            if (_checkingDriver) { _checkAgain = true; return; }
            _checkingDriver = true;
        }
        while (true)
        {
            try { await ScanAsync(force: false, userRequested: false); }
            catch (Exception e) { _log.Report("[exo] scan after a driver change failed: " + e.Message); }
            using (_driverGate.EnterScope())
            {
                if (!_checkAgain) { _checkingDriver = false; break; }
                _checkAgain = false;
            }
        }
        ShowDriverNotice();
    }

    private void ShowDriverNotice()
    {
        try
        {
            if (!_k.ShouldNotifyStale()) return;
            IReadOnlyList<GameState> stale = _k.DriverStaleGames();
            string key = _k.DriverId + "|" + string.Join('|', stale.Select(g => g.Game.Id).Order());
            if (key != _toldDriver)
            {
                _io.Event("notice", new
                {
                    kind = "driverStale",
                    driver = _k.Vendor.Gpu.DriverVersion,
                    vendor = Fmt.Vendor(_k.Vendor.Vendor),
                    games = stale.Select(g => new { id = g.Game.Id, name = g.Game.Name }),
                    title = Fmt.Vendor(_k.Vendor.Vendor) + " driver updated",
                    text = DriverStaleText(stale),
                });
            }
            _toldDriver = key;
        }
        catch (Exception e)
        {
            _log.Report("[exo] driver update check failed: " + e.Message);
        }
    }

    public string DriverStaleText(IReadOnlyList<GameState> stale)
    {
        TimeSpan total = TimeSpan.FromTicks(stale.Sum(g => (g.EstimatedWarmTime ?? TimeSpan.Zero).Ticks));
        string need = stale.Count == 1 ? "1 game needs" : $"{stale.Count} games need";
        return $"Driver {_k.Vendor.Gpu.DriverVersion} cleared the shader cache. {need} rebuilding: {string.Join(", ", stale.Select(g => g.Game.Name))}"
            + (total > TimeSpan.Zero ? ", about " + Format.Duration(total) + "." : ".");
    }

    // =================================================================== new shaders (App.NotifyNewShaders)

    private readonly HashSet<string> _autoQueued = [];
    private readonly DateTimeOffset _session = DateTimeOffset.UtcNow;   // NewShaders.Plan: what an earlier service queued was lost with it
    private Dictionary<string, string>? _told;
    private int _newShadersPending;
    private readonly SemaphoreSlim _newShadersGate = new(1, 1);

    public void RequestNewShadersCheck()
    {
        if (Interlocked.Exchange(ref _newShadersPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await _newShadersGate.WaitAsync();
            try
            {
                Interlocked.Exchange(ref _newShadersPending, 0);
                NotifyNewShaders();
            }
            catch (Exception e)
            {
                _log.Report("[exo] new shaders check failed: " + e.Message);
            }
            finally
            {
                _newShadersGate.Release();
            }
        });
    }

    private void NotifyNewShaders()
    {
        ScsKiller k = _k;
        Settings settings = k.Settings;
        var (withdraw, forget) = NewShaders.Withdrawn(_autoQueued, k.Queue, k.Games, settings);
        foreach (string id in forget) _autoQueued.Remove(id);
        foreach (string id in withdraw)
        {
            k.Remove(id);
            _autoQueued.Remove(id);
        }
        _told ??= k.Store.LoadNotified();
        // 1.3.0: forget (left the queue) and the session start let a game waiting for idle be queued again after a restart,
        // and one another compile held back be queued once that compile ends
        var (notify, compile, notified) = NewShaders.Plan(k.Games, k.Queue, _told,
            k.DriverStaleGames().Select(s => s.Game.Id).ToHashSet(), DateTimeOffset.UtcNow, s => NewShaders.ModeOf(s, k.Settings), k.Compiling,
            forget.ToHashSet(), _session);
        if (notify.Count == 0 && notified.Count == _told.Count && notified.All(e => _told.GetValueOrDefault(e.Key) == e.Value)) return;
        k.Store.SaveNotified(_told = notified);
        foreach (GameState g in compile)
        {
            k.EnqueueWhenIdle(g.Game.Id);
            _autoQueued.Add(g.Game.Id);
        }
        List<GameState> compiling = notify.Where(compile.Contains).ToList();
        if (compiling.Count > 0) _io.Event("notice", NewShadersNotice(compiling, compiling: true));
        List<GameState> rest = notify.Except(compiling).ToList();
        if (rest.Count > 0) _io.Event("notice", NewShadersNotice(rest, compiling: false));
    }

    private static object NewShadersNotice(IReadOnlyList<GameState> games, bool compiling)
    {
        long n = NewShaders.Count(games[0]);
        string title, text;
        if (compiling)
        {
            title = "Compiling new shaders";
            text = games.Count == 1 ? $"{games[0].Game.Name} has {n:N0} new pipeline{(n == 1 ? "" : "s")}. They compile when the PC is idle." : $"{games.Count} games have new shaders. They compile when the PC is idle.";
        }
        else
        {
            title = "New shaders to compile";
            text = games.Count == 1 ? $"{games[0].Game.Name} has {n:N0} new pipeline{(n == 1 ? "" : "s")}. Compile now so they don't stutter." : $"{games.Count} games have new shaders to compile";
        }
        return new
        {
            kind = "newShaders",
            compiling,
            games = games.Select(g => new { id = g.Game.Id, name = g.Game.Name, newPipelines = NewShaders.Count(g) }),
            title,
            text,
        };
    }

    // =================================================================== before and after (BeforeAfterUi.Watch)

    private readonly ConcurrentDictionary<string, (BeforeAfterStage? Stage, bool Sounded)> _baSeen = new();

    private void WatchBeforeAfter(GameState g)
    {
        string id = g.Game.Id;
        BeforeAfterInfo? ba = g.BeforeAfter;
        bool had = _baSeen.TryGetValue(id, out var was);
        bool sounded = had && was.Stage == ba?.Stage && was.Sounded;
        if (ba is { Stage: BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying } playing && !sounded && playing.PlayedSeconds >= playing.Minutes * 60)
        {
            sounded = true;
            if (_k.Settings.BeforeAfterSound) Native.MessageBeep(64u);
            _io.Event("notice", new { kind = "beforeAfterTimeUp", id, name = g.Game.Name });
        }
        _baSeen[id] = (ba?.Stage, sounded);
        if (had && was.Stage is { } st && st != BeforeAfterStage.Done && ba is { Stage: BeforeAfterStage.Done })
            _io.Event("notice", new { kind = "beforeAfterDone", id, name = g.Game.Name });
    }

    // =================================================================== shutdown (App.QuitAsync)

    public Task QuitAsync(string reason, int? waitSeconds)
    {
        if (Interlocked.Exchange(ref _quitting, 1) == 1) return _exit.Task;
        return Task.Run(async () =>
        {
            try
            {
                _log.Report($"[exo] stopping ({reason})");
                if (_k.Compiling)
                {
                    StopAll();
                    TimeSpan wait = TimeSpan.FromSeconds(waitSeconds ?? 120);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (_k.Compiling && sw.Elapsed < wait) await Task.Delay(250);
                    if (_k.Compiling) _log.Report("[exo] the compile didn't stop in time: quitting anyway (the driver may lose part of its cache)");
                }
                _stopWatching.Cancel();
                await Task.WhenAny(_watcher, Task.Delay(1000));
                await _log.FlushAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception e)
            {
                try { CrashLog.Write(_k.Store.DataDir, e); } catch { }
            }
            finally
            {
                _exit.TrySetResult(reason);
            }
        });
    }

    public bool Quitting => Volatile.Read(ref _quitting) == 1;

    /// <summary>App.StopAll: waiting items leave the queue, the running compile stops so the driver writes its cache.</summary>
    private void StopAll()
    {
        foreach (QueueItem q in _k.Queue.Where(q => q.Stage == QueueStage.Waiting)) _k.Remove(q.GameId);
        _k.StopQueue();
    }

    // =================================================================== helpers for methods

    public GameState Game(string id) =>
        _k.Games.FirstOrDefault(g => g.Game.Id == id) ?? throw new ServiceError("no game " + id);

    public string? Blocked(string id)
    {
        GameState? g = _k.Games.FirstOrDefault(x => x.Game.Id == id);
        if (g is null) return null;
        string? current = _k.Queue.FirstOrDefault(Format.Running)?.GameId;
        if (_blocked.TryGetValue(id, out var c) && ReferenceEquals(c.State, g) && c.Current == current) return c.Blocked;
        string? blocked;
        try
        {
            string? text = _k.BeforeAfterBlocked(id);
            blocked = text != null && text == g.StatusReason ? Fmt.Sentence(text) : text;
        }
        catch (Exception e)
        {
            blocked = Fmt.Describe(e);
        }
        _blocked[id] = (g, current, blocked);
        return blocked;
    }

    public void NoteOtherCaches(string id, IReadOnlyList<CachePart> parts) =>
        _otherCaches[id] = parts.Where(p => p.Name != "Driver cache").Sum(p => p.Bytes);

    public ViewContext Context(IReadOnlyList<GameState>? games = null, IReadOnlyList<QueueItem>? queue = null)
    {
        games ??= _k.Games;
        return new ViewContext
        {
            Settings = _k.Settings,
            Vendor = _k.Vendor,
            Queue = queue ?? _k.Queue,
            QueueRunning = _k.QueueRunning,
            Games = games,
            Blocked = Blocked,
            OtherCacheBytes = id => _otherCaches.GetValueOrDefault(id),
            Error = id => _errors.GetValueOrDefault(id),
            KeySource = id => new SCSKiller.Core.Unreal.UnrealKeys(_k.Store.DataDir).AutoSource(id),
            Recommended = StutterList.Recommended(games).Select(g => g.Game.Id).ToHashSet(),
            CacheLimitBytes = _cacheRaw is { Capped: null } raw ? raw.Limit?.Bytes : null,
        };
    }

    public void Log(string line) => _log.Report("[exo] " + line);
}

/// <summary>The engine log: app.log in the data folder (like the app), optionally stderr, optionally `log` events.</summary>
internal sealed class TeeLog(AppLog file, bool stderr, Action<string> events) : IProgress<string>
{
    public void Report(string value)
    {
        file.Report(value);
        if (stderr)
        {
            try { Console.Error.WriteLine(value); } catch (IOException) { }
        }
        try { events(value); } catch { }
    }

    public Task FlushAsync(TimeSpan wait) => file.FlushAsync(wait);
}

internal static partial class Native
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static partial bool MessageBeep(uint type);
}
