// Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 The SCSKiller authors
// Copyright (C) 2026 Exo Shaders contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
// version, with the additional permission in LICENSE-EXCEPTION.txt. It is distributed WITHOUT ANY WARRANTY; see LICENSE.
//
// Request methods (CONTRACT.md). Each does what the corresponding button/dialog of the SCSKiller 1.3.0-beta.1 app does
// (LibraryPage, DetailPage, QueuePage, CachePage, SettingsPage, BeforeAfterUi, App toasts).

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace ExoShaders.Service;

internal sealed partial class Host
{
    private static readonly object Ok = new { ok = true };

    private static readonly HashSet<string> Unsafe =
    [
        "game.compile", "game.compileMax", "queue.add", "queue.addWhenIdle", "queue.start", "queue.resume",
        "driverStale.compileNow", "driverStale.whenIdle", "library.addAllReady", "library.addAllRecommended",
        "recorder.install", "recorder.uninstall", "recorder.set", "recorder.alongside", "recorder.offline", "recorder.startOffline",
        "recorder.startRisk", "recorder.clear", "game.clearCache", "game.putBackKept", "game.dropKept", "game.play",
        "games.remove", "games.hide", "cache.delete", "cache.freeOldDriver", "driver.setCacheLimit", "driver.setAutoShader",
        "ba.start", "ba.launch", "ba.continue", "ba.recordAfterAgain", "ba.cancel",
    ];

    /// <summary>Options the settings policy (Policy.cs) owns: Exo Shaders has no settings, so these are refused.</summary>
    private static readonly HashSet<string> PolicyOwned =
    [
        "settings.set", "game.setApi", "game.setNewShaders", "game.setCareful", "game.setRayTracing", "game.setMaximum",
        "game.setLeftOutOfAddAll", "game.compileMax", "queue.addWhenIdle", "driverStale.whenIdle", "library.addAllRecommended",
        "recorder.set", "recorder.install", "recorder.uninstall", "recorder.alongside", "recorder.offline", "recorder.startOffline",
        "recorder.checkRisk", "recorder.startRisk", "driver.setAutoShader",
    ];

    private async Task<object?> Dispatch(string method, Params p)
    {
        if (Quitting && method != "shutdown" && method != "snapshot") throw new ServiceError("the service is stopping");
        if (PolicyOwned.Contains(method)) throw new ServiceError("Exo Shaders sets this itself: there is no option for it");
        if (_opt.Safe && Unsafe.Contains(method)) throw new ServiceError("not available in safe mode");
        ScsKiller k = _k;
        switch (method)
        {
            // ------------------------------------------------------------ general
            case "snapshot":
                return Snapshot();
            case "game.detail":
                return await Detail(p.ReqStr("id"));
            case "whatsNew":
                return WhatsNewDto(p.Bool("all") ?? false);
            case "whatsNew.atStart":
                return new { section = WhatsNew.AtStart(k.Store, firstRun: !k.Settings.WelcomeSeen, AppVersion.Current) is { } sec ? SectionDto(sec) : null };
            case "logs.subscribe":
                LogEvents(p.ReqBool("on"));
                return Ok;
            case "ui.visible":
                UiVisible(p.ReqBool("visible"));
                if (p.ReqBool("visible")) { k.RefreshCacheSizes(); RequestCacheInfo(force: false); }
                return Ok;
            case "app.dismissGpuNotice":
                k.Settings = k.Settings with { GpuNoticeDismissed = k.Vendor.Gpu.Name };
                MarkState();
                return Ok;
            case "shutdown":
                return Ok;   // the clean exit starts once this answer is out (ReadLoop)

            // ------------------------------------------------------------ library
            case "scan":
            {
                try
                {
                    IReadOnlyList<GameState> games = await ScanAsync(p.Bool("force") ?? false, userRequested: true);
                    if (!_opt.Safe) RequestNewShadersCheck();
                    return new { games = games.Count };
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new ServiceError("Scan failed: " + Fmt.Describe(e));
                }
            }
            case "game.refresh":
            {
                string id = p.ReqStr("id");
                Game(id);
                await Act(id, () => k.RefreshGame(id));
                ReadOtherCaches(id);
                return Ok;
            }
            case "game.check":
            {
                string id = p.ReqStr("id");
                Game(id);
                await Task.Run(() => k.CheckGame(id));
                return Ok;
            }
            case "cache.refreshSizes":
                await Task.Run(k.RefreshCacheSizes);
                RequestCacheInfo(force: true, readAuto: true);
                return Ok;
            case "library.setSort":
            {
                string sort = p.ReqStr("sort");
                Sort = Enum<LibrarySort>(sort, "sort");
                return Ok;
            }
            case "library.addAllReady":
            {
                HashSet<string> queued = Queued();
                foreach (GameState g in k.Games.Where(g => ScsKiller.InAddAllReady(g) && !queued.Contains(g.Game.Id)).ToList()) k.Enqueue(g.Game.Id);
                return Ok;
            }
            case "library.addAllRecommended":
            {
                HashSet<string> queued = Queued();
                ViewContext ctx = Context();
                foreach (GameState g in StutterList.Recommended(ctx.Games).Where(g => new GameView(g, ctx).IsAdd && !queued.Contains(g.Game.Id)).ToList())
                    k.Enqueue(g.Game.Id);
                return Ok;
            }

            // ------------------------------------------------------------ queue
            case "game.compile":
            {
                string id = p.ReqStr("id");
                GameView v = new(Game(id), Context());
                await Act(id, () =>
                {
                    if (v.OffersCareful) k.SetCarefulCompile(id, on: true);
                    k.Compile(id);
                    k.MoveInQueue(id, 0);   // Compile now: first of the waiting games (a running one finishes first)
                });
                return Ok;
            }
            case "game.compileMax":
            {
                string id = p.ReqStr("id");
                GameState g = Game(id);
                if (!k.Settings.MaxCompileConfirmed)
                {
                    if (p.Bool("confirmed") != true)
                        return new { needsConfirm = true, title = "Max compile " + g.Game.Name + "?", text = GameView.MaxCompileWhy, confirmText = "Max compile" };
                    k.Settings = k.Settings with { MaxCompileConfirmed = true };
                }
                await Act(id, () => k.CompileMax(id));
                return Ok;
            }
            case "queue.add":
                foreach (string id in p.Ids()) { Game(id); k.Enqueue(id); }
                return Ok;
            case "queue.addWhenIdle":
                foreach (string id in p.Ids()) { Game(id); k.EnqueueWhenIdle(id); }
                return Ok;
            case "queue.remove":
                k.Remove(p.ReqStr("id"));
                return Ok;
            case "queue.removeMany":
            {
                List<string>? ids = p.Strs("ids");
                foreach (string id in ScsKiller.BulkRemovable(k.Queue, ids?.ToHashSet())) k.Remove(id);
                return Ok;
            }
            case "queue.clearFinished":
                foreach (QueueItem q in k.Queue.Where(q => !q.PlanCheck && !Fmt.Active(q)).ToList()) k.Remove(q.GameId);
                return Ok;
            case "queue.move":
                k.MoveInQueue(p.ReqStr("id"), p.ReqInt("index"));
                return Ok;
            case "queue.start":
                CompileQueue();
                return Ok;
            case "queue.pause":
                UserPausedOrResumed(resumed: false);
                k.PauseQueue();
                return Ok;
            case "queue.resume":
                UserPausedOrResumed(resumed: true);
                k.ResumeQueue();
                return Ok;
            case "queue.stop":
                k.StopQueue();
                return Ok;
            case "driverStale.compileNow":
                await AfterScan();
                foreach (string id in StaleIds(p)) k.Enqueue(id);
                k.StartQueue();
                return Ok;
            case "driverStale.whenIdle":
                await AfterScan();
                foreach (string id in StaleIds(p)) k.EnqueueWhenIdle(id);
                return Ok;
            case "driverStale.skip":
                await AfterScan();
                k.DismissStale();
                MarkState();
                return Ok;

            // ------------------------------------------------------------ recorder
            case "recorder.install":
            {
                string id = p.ReqStr("id");
                Game(id);
                try { await Act(id, () => k.InstallRecorder(id)); }
                catch (Exception e) { throw new ServiceError("Couldn't add the recorder: " + Fmt.Describe(e)); }
                return Ok;
            }
            case "recorder.uninstall":
            {
                string id = p.ReqStr("id");
                Game(id);
                await Act(id, () => k.UninstallRecorder(id));
                return Ok;
            }
            case "recorder.set":
            {
                string id = p.ReqStr("id");
                Game(id);
                RecorderOverride mode = p.ReqStr("mode") switch
                {
                    "on" => RecorderOverride.On,
                    "off" => RecorderOverride.Off,
                    "default" => RecorderOverride.Default,
                    var m => throw new ServiceError($"'{m}' isn't a valid mode: on, off, default"),
                };
                await Act(id, () => k.SetRecorderOverride(id, mode));
                return Ok;
            }
            case "recorder.alongside":
            {
                string id = p.ReqStr("id");
                bool on = p.ReqBool("on");
                Game(id);
                await Act(id, () => k.SetRecordAlongsideMod(id, on));
                return Ok;
            }
            case "recorder.offline":
            {
                string id = p.ReqStr("id");
                bool on = p.ReqBool("on");
                Game(id);
                await Act(id, () => k.SetOfflineRecording(id, on));
                return Ok;
            }
            case "recorder.startOffline":
            {
                string id = p.ReqStr("id");
                Game(id);
                if (p.Bool("confirmed") != true) throw new ServiceError("an offline session starts only after the user confirms it (confirmed: true)");
                await StartSession(id, () => k.StartOfflineSession(id, confirmed: true));
                return Ok;
            }
            case "recorder.checkRisk":
            {
                string id = p.ReqStr("id");
                GameState g = Game(id);
                AntiCheat shown;
                try { shown = await k.CheckAntiCheatAsync(id); }
                catch (Exception e) { SetError(id, Fmt.Describe(e)); throw; }
                GameState with = g with { AntiCheat = shown };
                _riskShown[id] = shown;
                return new
                {
                    antiCheat = Fmt.AntiCheatKey(shown),
                    name = ScsKiller.AntiCheatName(with),
                    title = "Record " + g.Game.Name + " at your own risk?",
                    text = GameView.RiskText(with) + " SCSKiller removes its files when the game exits, or after a crash at your next logon.",
                    confirmText = "Record session",
                };
            }
            case "recorder.startRisk":
            {
                string id = p.ReqStr("id");
                string said = p.ReqStr("antiCheat");
                GameState g = Game(id);
                if (!_riskShown.TryGetValue(id, out AntiCheat shown))
                    throw new ServiceError("check the anti-cheat and confirm first (recorder.checkRisk)");
                string name = ScsKiller.AntiCheatName(g with { AntiCheat = shown });
                if (!said.Equals(name, StringComparison.OrdinalIgnoreCase) && !said.Equals(Fmt.AntiCheatKey(shown), StringComparison.OrdinalIgnoreCase))
                    throw new ServiceError($"the confirmation was for {said}, but the game has {name}: confirm again");
                _riskShown.TryRemove(id, out _);
                await StartSession(id, () => k.StartRiskSession(id, confirmed: true, shown));
                return Ok;
            }
            case "recorder.clear":
            {
                string id = p.ReqStr("id");
                Game(id);
                bool cleared = false;
                await Act(id, () => cleared = k.ClearRecording(id));
                if (!cleared) { SetError(id, "Nothing was cleared: no recording of this game was found."); throw new ServiceError("Nothing was cleared: no recording of this game was found."); }
                return Ok;
            }

            // ------------------------------------------------------------ per-game options
            case "game.setCareful":
            {
                string id = p.ReqStr("id");
                bool on = p.ReqBool("on");
                Game(id);
                await Act(id, () => k.SetCarefulCompile(id, on));
                return Ok;
            }
            case "game.setNewShaders":
            {
                string id = p.ReqStr("id");
                Game(id);
                NewShadersMode? mode = p.Str("mode") is { } m ? Enum<NewShadersMode>(m, "mode") : p.Has("mode") ? null : throw new ServiceError("missing parameter 'mode'");
                await Act(id, () => k.SetNewShadersMode(id, mode));
                if (!_opt.Safe) RequestNewShadersCheck();
                return Ok;
            }
            case "game.setApi":
            {
                string id = p.ReqStr("id");
                Game(id);
                DxApi? api = p.ReqStr("api") switch
                {
                    "auto" => null,
                    "dx11" => DxApi.D3D11,
                    "dx12" => DxApi.D3D12,
                    var a => throw new ServiceError($"'{a}' isn't a valid api: auto, dx11, dx12"),
                };
                if (!new GameView(Game(id), Context()).CanPickApi) throw new ServiceError("Wait for the compile to finish");
                await Act(id, () => k.SetApiOverride(id, api));
                return Ok;
            }
            case "game.setRayTracing":
            {
                string id = p.ReqStr("id");
                Game(id);
                bool? on = p.ReqStr("value") switch
                {
                    "default" => null,
                    "on" => true,
                    "off" => false,
                    var v => throw new ServiceError($"'{v}' isn't a valid value: default, on, off"),
                };
                await Act(id, () => k.SetGameRayTracing(id, on));
                return Ok;
            }
            case "game.setMaximum":
            {
                string id = p.ReqStr("id");
                bool on = p.ReqBool("on");
                Game(id);
                await Act(id, () => k.SetGameMaximum(id, on));
                return Ok;
            }
            case "game.setLeftOutOfAddAll":
            {
                string id = p.ReqStr("id");
                bool on = p.ReqBool("on");
                Game(id);
                await Act(id, () => k.SetGameLeftOutOfAddAll(id, on));
                return Ok;
            }
            case "game.setAesKey":
            {
                string id = p.ReqStr("id");
                string key = p.ReqStr("key").Trim();
                Game(id);
                bool ok;
                string? problem = null;
                try { ok = await Task.Run(() => k.SetEncryptionKey(id, key)); }
                catch (Exception e) { ok = false; problem = Fmt.Describe(e); }
                if (!ok) throw new ServiceError("That key didn't work: " + (problem ?? "It doesn't open this game's files."));
                SetError(id, null);
                _ = Task.Run(() => { try { k.RefreshGame(id); } catch (Exception e) { Log("refreshing after the key: " + e.Message); } });
                return Ok;
            }
            case "game.play":
            {
                string id = p.ReqStr("id");
                string? error = await PlayAsync(Game(id));
                SetError(id, error);
                if (error != null) throw new ServiceError(error);
                return Ok;
            }

            // ------------------------------------------------------------ game caches
            case "game.caches":
                return await GameCachesDto(p.ReqStr("id"));
            case "game.clearCache":
            {
                string id = p.ReqStr("id");
                bool withOwn = p.Bool("includeGameCache") ?? false;
                Game(id);
                bool cleared = false;
                await Act(id, () => cleared = k.ClearGameCache(id, withOwn));
                ReadOtherCaches(id);
                RequestCacheInfo(force: true);
                if (!cleared) { SetError(id, "Nothing was cleared: no shader-cache files of this game were found."); throw new ServiceError("Nothing was cleared: no shader-cache files of this game were found."); }
                return Ok;
            }
            case "game.putBackKept":
            {
                string id = p.ReqStr("id");
                Game(id);
                await Act(id, () => k.PutBackKept(id));
                return Ok;
            }
            case "game.dropKept":
            {
                string id = p.ReqStr("id");
                Game(id);
                await Act(id, () => k.DropKeptCache(id));
                RequestCacheInfo(force: true);
                return Ok;
            }

            // ------------------------------------------------------------ manual games
            case "games.previewExe":
            {
                string exe = p.ReqStr("exe");
                ManualAdd added;
                try { added = await Task.Run(() => k.PreviewManualGame(exe)); }
                catch (Exception e) { throw new ServiceError("Couldn't add this game: " + Fmt.Describe(e)); }
                bool hidden = k.HiddenGames.Any(g => g.Id == added.Game.Id);
                bool listed = k.Games.Any(g => g.Game.Id == added.Game.Id);
                string where = added.Game.Store == Store.Manual ? "you added it already" : "SCSKiller found it in " + Fmt.StoreName(added.Game);
                string? message = !added.Existed ? null
                    : hidden ? "Hidden from your library: You removed " + added.Game.Name + ". Show hidden brings it back."
                    : listed ? "Already in your library: " + added.Game.Name + " is in the list: " + where + "."
                    : "Already in your library: " + added.Game.Name + ": " + where + ". Refresh the library to see it.";
                string? folderProblem = added.Existed ? null : await Task.Run(() => k.ManualFolderProblem(exe, added.Game.InstallDir));
                return new
                {
                    game = new { id = added.Game.Id, name = added.Game.Name, exe = added.Game.ExePath, installDir = added.Game.InstallDir, store = Fmt.StoreName(added.Game) },
                    existed = added.Existed, hidden, listed, message, folderProblem,
                    title = "Add " + added.Game.Name,
                    text = "Before it records, SCSKiller checks the whole game folder for anti-cheat. Pick the game's own folder, not one that holds other games.",
                };
            }
            case "games.checkFolder":
            {
                string exe = p.ReqStr("exe"), folder = p.ReqStr("folder");
                return new { problem = await Task.Run(() => k.ManualFolderProblem(exe, folder)) };
            }
            case "games.addExe":
            {
                string exe = p.ReqStr("exe"), folder = p.ReqStr("folder");
                if (await Task.Run(() => k.ManualFolderProblem(exe, folder)) is { } problem) throw new ServiceError(problem);
                ManualAdd added;
                try { added = await Task.Run(() => k.AddManualGame(exe, folder)); }
                catch (Exception e) { throw new ServiceError("Couldn't add this game: " + Fmt.Describe(e)); }
                _ = BackgroundScan();
                return new { id = added.Game.Id };
            }
            case "games.setFolder":
            {
                string id = p.ReqStr("id"), folder = p.ReqStr("folder");
                GameState g = Game(id);
                if (g.Game.Store != Store.Manual) throw new ServiceError("only for games you added");
                if (await Task.Run(() => k.ManualFolderProblem(g.Game.ExePath, folder)) is { } problem) throw new ServiceError(problem);
                try { await Task.Run(() => k.AddManualGame(g.Game.ExePath, folder)); }
                catch (Exception e) { throw new ServiceError("Couldn't set the game folder: " + Fmt.Describe(e)); }
                MarkState();
                return Ok;
            }
            case "games.addFolder":
            {
                string path = p.ReqStr("path");
                var manual = k.Manual ?? throw new ServiceError("adding games isn't available");
                try { await Task.Run(() => manual.AddFolder(path)); }
                catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { throw new ServiceError("Couldn't add this folder: " + e.Message); }
                _ = BackgroundScan();
                return Ok;
            }
            case "exo.library":
            {
                // Exo Launcher's installed games: a safety net for stores the engine can't read. The list replaces the last one.
                if (p.Element("games") is not { ValueKind: JsonValueKind.Array } list) throw new ServiceError("parameter 'games' must be a list");
                var source = k.ExoLibrary ?? throw new ServiceError("the Exo library isn't available");
                List<ExoLibraryEntry> entries = [];
                foreach (JsonElement e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) throw new ServiceError("each game must be an object");
                    string? S(string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    if (S("id") is not { Length: > 0 } id || S("installDir") is not { Length: > 0 } dir) continue;
                    entries.Add(new ExoLibraryEntry(id, S("title") ?? "", S("store") ?? "", dir, S("exe")));
                }
                bool changed;
                try { changed = await Task.Run(() => source.Replace(entries)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ServiceError("Couldn't save Exo's games: " + e.Message); }
                List<object> refused = [];
                if (changed)
                {
                    // Folders another source already found: those Exo games are covered, not refused.
                    List<string> listed = k.Games.Where(g => g.Game.Store != Store.Manual && !g.Game.Id.StartsWith("exo:", StringComparison.Ordinal) && g.Game.InstallDir.Length > 0)
                        .Select(g => GameFiles.DirKey(g.Game.InstallDir)).ToList();
                    await Task.Run(() =>
                    {
                        foreach (ExoLibraryEntry e in source.Entries)
                        {
                            string dir = GameFiles.DirKey(e.InstallDir);
                            if (listed.Any(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase) || GameFiles.Inside(d, dir) || GameFiles.Inside(dir, d))) continue;
                            if (ExoLibrarySource.Problem(e) is { } problem) refused.Add(new { id = e.Id, title = e.Title, problem });
                        }
                    });
                    _log.Report($"[exo] Exo's library: {source.Entries.Count} games{(refused.Count > 0 ? $", {refused.Count} refused" : "")}, rescanning");
                    _ = BackgroundScan();
                }
                return new { ok = true, changed, games = source.Entries.Count, refused };
            }
            case "games.removeFolder":
            {
                string path = p.ReqStr("path");
                var manual = k.Manual ?? throw new ServiceError("adding games isn't available");
                await Task.Run(() => manual.RemoveFolder(path));
                _ = BackgroundScan();
                return Ok;
            }
            case "games.rename":
            {
                string id = p.ReqStr("id");
                string name = p.ReqStr("name");
                if (name.Trim().Length == 0) throw new ServiceError("the name can't be empty");
                if (Game(id).Game.Store != Store.Manual) throw new ServiceError("only games you added can be renamed");
                try { await Task.Run(() => k.RenameManualGame(id, name)); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { throw new ServiceError("Couldn't rename the game: " + e.Message); }
                MarkState();
                return Ok;
            }
            case "games.remove":
            {
                string id = p.ReqStr("id");
                bool manual = Game(id).Game.Store == Store.Manual;
                try { await Task.Run(() => { if (manual) k.RemoveManualGame(id); else k.HideGame(id); }); }
                catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { throw new ServiceError("Couldn't remove the game: " + e.Message); }
                MarkState();
                return Ok;
            }
            case "games.hide":
            {
                string id = p.ReqStr("id");
                Game(id);
                try { await Task.Run(() => k.HideGame(id)); }
                catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { throw new ServiceError("Couldn't remove the game: " + e.Message); }
                MarkState();
                return Ok;
            }
            case "games.show":
            {
                string id = p.ReqStr("id");
                try { await Task.Run(() => k.ShowGame(id)); }
                catch (ArgumentException) { }
                MarkState();
                return Ok;
            }

            // ------------------------------------------------------------ before and after
            case "ba.blocked":
            {
                string id = p.ReqStr("id");
                Game(id);
                _blocked.TryRemove(id, out _);
                return new { blocked = Blocked(id) };
            }
            case "ba.start":
            {
                string id = p.ReqStr("id");
                Game(id);
                int minutes = p.Int("minutes") ?? 5;
                if (minutes is not (5 or 10)) throw new ServiceError("minutes must be 5 or 10");
                bool overlay = p.Bool("overlay") ?? k.Settings.BeforeAfterOverlay;
                bool sound = p.Bool("sound") ?? k.Settings.BeforeAfterSound;
                bool over = p.Bool("over") ?? false;
                k.Settings = k.Settings with { BeforeAfterIntroSeen = true, BeforeAfterSound = sound, BeforeAfterOverlay = overlay };
                await Act(id, () =>
                {
                    if (over) k.EndBeforeAfter(id, compile: false);
                    k.StartBeforeAfter(id, minutes, overlay);
                });
                _blocked.TryRemove(id, out _);
                string? launchError = await LaunchAsync(id);
                SetError(id, launchError);
                return new { ok = true, launchError };
            }
            case "ba.launch":
            {
                string id = p.ReqStr("id");
                Game(id);
                string? error = await LaunchAsync(id);
                SetError(id, error);
                if (error != null) throw new ServiceError(error);
                return Ok;
            }
            case "ba.continue":
            {
                string id = p.ReqStr("id");
                Game(id);
                await Act(id, () => k.ContinueBeforeAfter(id));
                if (Game(id).BeforeAfter is { } b && (b.Stage == BeforeAfterStage.AfterArmed || (b.Stage == BeforeAfterStage.BeforeArmed && !b.ClearPending)))
                {
                    string? error = await LaunchAsync(id);
                    SetError(id, error);
                    if (error != null) throw new ServiceError(error);
                }
                return Ok;
            }
            case "ba.recordAfterAgain":
            {
                string id = p.ReqStr("id");
                Game(id);
                await Act(id, () => k.RecordAfterAgain(id));
                return Ok;
            }
            case "ba.cancel":
            {
                string id = p.ReqStr("id");
                bool compile = p.ReqBool("compile");
                Game(id);
                await Act(id, () => k.EndBeforeAfter(id, compile));
                _blocked.TryRemove(id, out _);
                return Ok;
            }
            case "ba.report":
            {
                string id = p.ReqStr("id");
                Game(id);
                BeforeAfterResult? report = await Task.Run(() => k.BeforeAfterReport(id));
                return new { report };
            }

            // ------------------------------------------------------------ cache page
            case "cache.inventory":
                return await InventoryDto();
            case "cache.delete":
                return await DeleteCaches(p);
            case "cache.freeOldDriver":
            {
                CacheInventory inv = _inventory ?? await Task.Run(k.CacheInventory);
                _inventory = inv;
                return await DeleteItems(inv.Groups.Where(g => g.Kind == CacheGroupKind.OldDriver).SelectMany(g => g.Items).ToList());
            }

            // ------------------------------------------------------------ settings and driver
            case "settings.get":
                return SettingsDto();
            case "policy.preview":
                return PolicyPreview();
            case "settings.set":
                return await SetSettings(p);
            case "driver.setCacheLimit":
                return await SetCacheLimit(p.ReqInt("index"));
            case "driver.setAutoShader":
                return await SetAutoShader(p.ReqStr("level"));
        }
        throw new ServiceError($"unknown method '{method}'");
    }

    // =================================================================== helpers

    private readonly ConcurrentDictionary<string, AntiCheat> _riskShown = new();
    private CacheInventory? _inventory;

    private HashSet<string> Queued() => _k.Queue.Where(q => !q.PlanCheck && Fmt.Active(q)).Select(q => q.GameId).ToHashSet();

    /// <summary>Runs a per-game action off the request thread; its error shows on the game (DetailVm.Error) too.</summary>
    private async Task Act(string id, Action action)
    {
        try
        {
            await Task.Run(action);
            SetError(id, null);
        }
        catch (Exception e)
        {
            SetError(id, Fmt.Describe(e));
            throw;
        }
        finally
        {
            MarkState();
        }
    }

    private async Task StartSession(string id, Func<Task> start)
    {
        Task session;
        try
        {
            session = await Task.Factory.StartNew(start, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            SetError(id, null);
        }
        catch (Exception e)
        {
            SetError(id, Fmt.Describe(e));
            throw;
        }
        _ = session.ContinueWith(t =>
        {
            if (t.Exception is { } ex)
            {
                string text = Fmt.Describe(ex);
                SetError(id, text);
                _io.Event("notice", new { kind = "error", text });
            }
            MarkState();
        }, TaskScheduler.Default);
    }

    /// <summary>App.CompileQueue: idle-waiting items become normal, then the queue starts.</summary>
    private void CompileQueue()
    {
        foreach (QueueItem q in _k.Queue.Where(q => q.Stage == QueueStage.Waiting && q.Note == Fmt.IdleNote && !q.PlanCheck).ToList())
            _k.Enqueue(q.GameId);
        _k.StartQueue();
    }

    private async Task AfterScan()
    {
        if (_k.Games.Count == 0) await ScanAsync(force: false, userRequested: false);
    }

    private IEnumerable<string> StaleIds(Params p) => p.Strs("ids") ?? _k.DriverStaleGames().Select(g => g.Game.Id).ToList();

    private async Task BackgroundScan()
    {
        try { await ScanAsync(force: false, userRequested: false); }
        catch (Exception e) { _io.Event("notice", new { kind = "error", text = "Scan failed: " + Fmt.Describe(e) }); }
    }

    private void ReadOtherCaches(string id)
    {
        try { NoteOtherCaches(id, _k.GameCaches(id)); MarkState(); }
        catch (Exception) { }
    }

    /// <summary>BeforeAfterUi.PlayAsync: a kept-aside cache goes back first; an armed before/after run launches.</summary>
    private async Task<string?> PlayAsync(GameState g)
    {
        await Task.Run(() => _k.PutBackKept(g.Game.Id));
        if (g.BeforeAfter is { Stage: BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed, ClearPending: false })
            return await LaunchAsync(g.Game.Id);
        return Fmt.Play(g);
    }

    /// <summary>BeforeAfterUi.LaunchAsync.</summary>
    private async Task<string?> LaunchAsync(string id)
    {
        try
        {
            await Task.Run(() => _k.BeforeAfterRecorderReady(id));
        }
        catch (Exception e)
        {
            return Fmt.Describe(e);
        }
        finally
        {
            MarkState();
        }
        GameState g = Game(id);
        return new GameView(g, Context()).CanLaunch ? Fmt.Play(g) : null;
    }

    // =================================================================== game detail

    private async Task<object> Detail(string id)
    {
        Game(id);
        await Task.Run(() => ReadOtherCaches(id));
        GameState g = Game(id);
        var view = new GameView(g, Context());
        JsonObject node = (JsonObject)JsonSerializer.SerializeToNode(view.ToDto(), Protocol.Json)!;
        void Put(string name, object? value)
        {
            if (value is not null) node[name] = JsonSerializer.SerializeToNode(value, Protocol.Json);
        }
        Put("coverage", view.Coverage);
        Put("sources", view.Sources);
        Put("details", view.Details);
        Put("left", view.Left);
        Put("compile", view.Compile);
        Put("session", view.Session);
        Put("frames", view.FrameGraph);
        Put("framesSummary", view.FramesSummary);
        Put("hitchRows", view.HitchRows);
        long? lastLaunch = g.LastFrames?.LaunchUnixMs;
        Put("earlierSessions", _k.FrameHistory(id).Where(e => e.LaunchUnixMs != lastLaunch).Select(e => new
        {
            label = $"{DateTimeOffset.FromUnixTimeMilliseconds(e.LaunchUnixMs).LocalDateTime:g} · {Format.Duration(e.Duration)}",
            value = Format.Session(e),
        }).ToList());
        if (g.Middleware is { Count: > 0 }) Put("middleware", new { chip = view.MiddlewareChip, tip = view.MiddlewareTip });
        if (view.ApiChip.Length > 0) Put("apiChip", view.ApiChip);
        return node;
    }

    private static bool Own(CachePart p) => p.Name is "Game's pipeline cache" or "Game's shader precache";

    private async Task<object> GameCachesDto(string id)
    {
        GameState g = Game(id);
        IReadOnlyList<CachePart> parts = await Task.Run(() => _k.GameCaches(id, gamePrecache: true));
        _otherCaches[id] = parts.Where(x => !Own(x) && x.Name != "Driver cache").Sum(x => x.Bytes);
        MarkState();
        List<CachePart> notOwn = parts.Where(x => !Own(x)).ToList();
        List<CachePart> own = parts.Where(Own).ToList();
        string note = "The game will compile shaders during play again until you re-warm it." + (g.AntiCheat != AntiCheat.None ? " Anti-cheat game: only the driver cache is cleared." : "");
        return new
        {
            parts = parts.Select(x => new { name = x.Name, bytes = x.Bytes, size = Format.Bytes(x.Bytes), files = x.Files.Count, own = Own(x) }),
            title = "Clear the shader cache of " + g.Game.Name + "?",
            text = (notOwn.Count > 0 ? string.Join(" · ", notOwn.Select(x => x.Name + " " + Format.Bytes(x.Bytes))) : "No cache files on disk now") + ".\n\n" + note,
            ownText = own.Count > 0 ? "Also delete the game's own shader cache (" + Format.Bytes(own.Sum(x => x.Bytes)) + "). It rebuilds it at its next start, which then takes longer." : null,
            note,
            gap = _k.DriverCacheGap(id),
        };
    }

    // =================================================================== cache page (CacheVm)

    private static (string Title, string Caption) Texts(CacheGroupKind kind, DateTime? installed) => kind switch
    {
        CacheGroupKind.Installed => ("Installed games", "Games in your library, by the cache keys SCSKiller learned for them."),
        CacheGroupKind.Uninstalled => ("Uninstalled games", "Games SCSKiller compiled or saw running that aren't in your library now. Named by their exe."),
        CacheGroupKind.OldDriver => ("Old driver", $"Written before the current driver was installed on {installed?.ToLocalTime():d}. It doesn't read them."),
        CacheGroupKind.OldBuild => ("Earlier SCSKiller compiles", "Left by earlier compiles. The games don't read them."),
        CacheGroupKind.OtherVendor => ("Other GPU vendor", "Caches for another brand of GPU than the one SCSKiller compiles for."),
        _ => ("Other apps and games", "Desktop apps, and games SCSKiller hasn't compiled or seen running. A game moves to its group once SCSKiller sees it running."),
    };

    private static string Consequence(CacheGroupKind kind, IReadOnlyList<CacheItem> items)
    {
        string text = kind switch
        {
            CacheGroupKind.Installed or CacheGroupKind.Uninstalled => "These games compile their shaders again on their next compile, and stutter until then: " + string.Join(", ", items.Select(i => i.Name)) + ".",
            CacheGroupKind.OldDriver => "The current driver doesn't read these files. Nothing needs to compile again.",
            CacheGroupKind.OldBuild => "The games don't read these files. Nothing needs to compile again.",
            CacheGroupKind.OtherVendor => "Apps that run on that GPU build their cache again as they run.",
            _ => "These apps and games build their cache again as they run, and may stutter until they have.",
        };
        return text + string.Concat(items.Select(i => i.Note).OfType<string>().Select(n => " " + n)) + " Files in use are skipped.";
    }

    private static string ItemName(CacheGroupKind kind, CacheItem i) =>
        kind == CacheGroupKind.Unattributed && i.Name.Length == 8 && i.Name.All(char.IsAsciiHexDigit) ? "Unknown app " + i.Name : i.Name;

    private static string? Outside(CacheInventory inv, IReadOnlyList<CacheFile> files)
    {
        if (files.Count == 0) return null;
        string? dir = Path.GetDirectoryName(files[0].Path);
        if (dir is null) return null;
        while (!files.All(f => SCSKiller.Core.Games.GameFiles.Inside(dir, f.Path)))
        {
            string? up = Path.GetDirectoryName(dir);
            if (up is null) break;
            dir = up;
        }
        return inv.Folders.Any(f => string.Equals(SCSKiller.Core.Games.GameFiles.DirKey(f.Path), SCSKiller.Core.Games.GameFiles.DirKey(dir), StringComparison.OrdinalIgnoreCase)) ? null : dir;
    }

    private async Task<object> InventoryDto()
    {
        CacheInventory inv = await Task.Run(_k.CacheInventory);
        _inventory = inv;
        long old = inv.Bytes(CacheGroupKind.OldDriver);
        return new
        {
            total = Format.Bytes(inv.Groups.Sum(g => g.Bytes)) + " of shader caches",
            totalBytes = inv.Groups.Sum(g => g.Bytes),
            driverInstalled = inv.DriverInstalled is { } di ? Fmt.Iso(new DateTimeOffset(di.ToLocalTime())) : null,
            oldDriver = old > 0 ? new
            {
                bytes = old,
                text = $"{Format.Bytes(old)} of cache files are from before the current driver was installed on {inv.DriverInstalled?.ToLocalTime():d}. It doesn't read them.",
                freeText = "Free " + Format.Bytes(old),
            } : null,
            everythingWarning = new { withOthers = ScsKiller.EverythingWarning(inv, withOthers: true), withoutOthers = ScsKiller.EverythingWarning(inv, withOthers: false) },
            noFolders = inv.Folders.Count == 0,
            folders = inv.Folders.Select(f => new
            {
                label = f.Label,
                path = f.Path,
                size = f.Cap is { } cap ? Format.Bytes(f.Bytes) + " of " + Format.Bytes(cap) : Format.Bytes(f.Bytes),
                note = f.TargetMissing ? "This folder is a link whose target is missing"
                    : f.Cap is { } c && f.Bytes >= c * 9 / 10 ? "Nearly full: the driver removes the least recently used games' caches when a game starts"
                    : f.Final != null ? "Stored in " + f.Final + " through a link"
                    : !f.Exists ? "Not created yet" : null,
            }),
            groups = inv.Groups.Select((g, gi) =>
            {
                var (title, caption) = Texts(g.Kind, inv.DriverInstalled);
                return new
                {
                    kind = Fmt.Camel(g.Kind.ToString()),
                    title,
                    caption,
                    size = Format.Bytes(g.Bytes),
                    bytes = g.Bytes,
                    note = g.Note,
                    deletable = g.Kind != CacheGroupKind.Unattributed,
                    consequence = Consequence(g.Kind, g.Items),
                    items = g.Items.Select((i, ii) => new
                    {
                        key = $"{gi}.{ii}",
                        name = ItemName(g.Kind, i),
                        size = Format.Bytes(i.Bytes),
                        bytes = i.Bytes,
                        note = i.Note,
                        outside = Outside(inv, i.Files),
                        gameIds = i.GameIds,
                        files = i.Files.Count,
                        consequence = Consequence(g.Kind, [i]),
                    }),
                };
            }),
        };
    }

    private async Task<object> DeleteCaches(Params p)
    {
        CacheInventory inv = _inventory ?? throw new ServiceError("The cache list changed: refresh it and try again.");
        List<CacheItem> items;
        if (p.Strs("keys") is { } keys)
        {
            items = [];
            foreach (string key in keys)
            {
                string[] parts = key.Split('.');
                if (parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out int gi) || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out int ii)
                    || gi < 0 || gi >= inv.Groups.Count || ii < 0 || ii >= inv.Groups[gi].Items.Count)
                    throw new ServiceError("The cache list changed: refresh it and try again.");
                items.Add(inv.Groups[gi].Items[ii]);
            }
        }
        else if (p.Str("group") is { } group)
        {
            CacheGroupKind kind = Enum<CacheGroupKind>(group, "group");
            items = inv.Groups.Where(g => g.Kind == kind).SelectMany(g => g.Items).ToList();
        }
        else if (p.Bool("all") == true)
            items = inv.Everything(p.Bool("withOthers") ?? false).ToList();
        else throw new ServiceError("give keys, group or all");
        return await DeleteItems(items);
    }

    private async Task<object> DeleteItems(IReadOnlyList<CacheItem> items)
    {
        CacheDeleteResult r;
        try
        {
            r = await Task.Run(() => _k.DeleteCaches(items));
        }
        catch (Exception e)
        {
            throw new ServiceError("Nothing was deleted: " + Fmt.Describe(e));
        }
        finally
        {
            _inventory = null;
            RequestCacheInfo(force: true);
            MarkState();
        }
        string text = $"Deleted {Format.Bytes(r.Bytes)} in {r.Files} file{(r.Files == 1 ? "" : "s")}." + (r.Skipped.Count > 0 ? $" Skipped {r.Skipped.Count}:\n" + string.Join("\n", r.Skipped) : "");
        return new { ok = true, bytes = r.Bytes, files = r.Files, skipped = r.Skipped, text, warning = r.Skipped.Count > 0 };
    }

    // =================================================================== driver settings (SettingsPage, elevated)

    private string[] RunningGpu => ["--adapter-luid", _k.Vendor.Gpu.AdapterLuid.ToString("X16")];

    private Task<Elevated.Result?> AsAdminAsync(Func<string> inProcess, string[] cliArgs) => Task.Run(() =>
    {
        Interlocked.Increment(ref _elevated);
        try
        {
            if (!Elevated.IsAdmin) return Elevated.Run(cliArgs);
            return new Elevated.Result(true, inProcess());
        }
        catch (Exception ex)
        {
            return new Elevated.Result(false, ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _elevated);
        }
    });

    private async Task<object> SetCacheLimit(int index)
    {
        if (index < 0 || index >= Sizes.Length) throw new ServiceError("index must be 0 to " + (Sizes.Length - 1));
        if (!_k.Vendor.Caps.CacheSizeConfigurable) throw new ServiceError("this GPU's cache size is not configurable");
        var (label, limit) = Sizes[index];
        string arg = limit.IsDriverDefault ? "default" : limit.Bytes is { } b ? (b / 1073741824.0).ToString(CultureInfo.InvariantCulture) : "unlimited";
        Elevated.Result? r = await AsAdminAsync(() => { _k.Vendor.SetCacheLimit(limit); return ""; }, ["cache", "set", arg, .. RunningGpu]);
        RequestCacheInfo(force: true);
        return r is null ? new { ok = false, text = "Cancelled." }
            : r.Ok ? new { ok = true, text = "Shader cache limit set to " + label + "." }
            : new { ok = false, text = "The limit was not changed: " + r.Message };
    }

    private async Task<object> SetAutoShader(string levelText)
    {
        if (_k.Vendor.Vendor != GpuVendor.Nvidia) throw new ServiceError("Auto Shader Compilation is an NVIDIA feature");
        AutoShaderCompilation level = Enum<AutoShaderCompilation>(levelText, "level");
        AutoShaderState? now = null;
        try { now = await Task.Run(_k.Vendor.GetAutoShaderCompilation); } catch { }
        if (now != null && now.Is(level)) return new { ok = true, text = SCSKiller.Core.Vendors.NvidiaBackend.AutoShaderOutcome(level, changed: false) };
        Elevated.Result? r = await AsAdminAsync(() => _k.Vendor.SetAutoShaderCompilation(level),
            ["nvidia-auto-shader", level.ToString().ToLowerInvariant(), "--for-user", Elevated.CurrentUserSid, .. RunningGpu]);
        if (r is { Ok: false }) Log($"Auto Shader Compilation {level}: {r.Message}");
        RequestCacheInfo(force: true, readAuto: true);
        return r is null ? new { ok = false, text = "Cancelled." }
            : r.Ok ? new { ok = true, text = r.Message is { Length: > 0 } m ? m : SCSKiller.Core.Vendors.NvidiaBackend.AutoShaderOutcome(level, changed: true) }
            : new { ok = false, text = "Couldn't apply: " + r.Message.Split('\n')[0] };
    }

    // =================================================================== what's new

    private static object SectionDto(WhatsNewSection s) => new
    {
        version = s.Version,
        bullets = s.Bullets.Select(b => WhatsNew.Spans(b).Select(x => new { text = x.Text, kind = x.Kind == SpanKind.Plain ? "text" : Fmt.Camel(x.Kind.ToString()), url = x.Url })),
    };

    private static object WhatsNewDto(bool all)
    {
        string log = WhatsNew.Changelog;
        List<string> versions = log.Split('\n').Select(l => Regex.Match(l, @"^## \[([^\]]+)\]")).Where(m => m.Success)
            .Select(m => m.Groups[1].Value).Where(v => !v.Equals("Unreleased", StringComparison.OrdinalIgnoreCase)).ToList();
        if (!all)
        {
            WhatsNewSection? current = WhatsNew.ForAbout(AppVersion.Current);
            return new { version = AppVersion.Current.ToString(), sections = current is null ? [] : new[] { SectionDto(current) } };
        }
        return new
        {
            version = AppVersion.Current.ToString(),
            sections = versions.Select(v => WhatsNew.Section(log, v)).OfType<WhatsNewSection>().Select(SectionDto).ToList(),
        };
    }
}
