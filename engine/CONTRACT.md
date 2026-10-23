# Exo Shaders engine service: protocol contract

`ExoShaders.Service.exe` hosts one SCSKiller engine (`ScsKiller`, the 1.3.0 app model) the way the official
SCSKiller app does, with no window and no tray. A host (Exo) starts it as a child process and talks to it over
stdin/stdout. This file is the contract the UI is built against; the service implements exactly this.

Protocol version: **1** (reported in the `ready` event).

## Transport

- UTF-8, **newline-delimited JSON**: one JSON object per line, both directions. No BOM. Lines end with `\n`.
- **stdout carries only protocol lines.** Logs go to `<dataDir>\app.log` (the engine's log, shared with the official
  app's format, service lines prefixed `[exo]`) and, with `--log-stderr`, to stderr. The host should still drain
  stderr (or redirect it) because the refusal message is written there.
- All JSON is camelCase. Enums are camelCase strings. Absent/`null` optional fields are **omitted** from output.
- Dates are ISO 8601 strings with offset (`"2026-10-15T20:26:30+02:00"`). Durations are given both as display text
  (`"12 min"`, the app's own `Format.Duration`) and, where useful, as numbers of seconds (`...Seconds`).
- Byte sizes are numbers of bytes, with a display string next to them where the app shows one (`"4.2 GB"`).

### Requests and responses

```json
{"id": 7, "method": "game.compile", "params": {"id": "steam:2909400"}}
{"id": 7, "result": {"ok": true}}
{"id": 8, "method": "nope"}
{"id": 8, "error": "unknown method 'nope'"}
```

- `id` is any JSON number or string chosen by the host; it is echoed back unchanged. `params` may be omitted when a
  method takes none.
- Requests are handled **concurrently**; responses may arrive in a different order than the requests. A slow method
  (`scan`, `cache.inventory`, `game.setAesKey`, ...) never blocks others.
- `error` is a user-facing sentence (the same wording the official app shows in its error dialogs/info bars), suitable
  to show as-is.
- Methods that change something return `{"ok": true}` unless documented otherwise. The resulting state change also
  arrives as a `state` event.
- A request line that isn't valid JSON gets `{"id": null, "error": "..."}`.

### Events (no `id`)

| event | data | when |
|---|---|---|
| `ready` | `{protocol: 1, service: string, engine: string, dataDir: string, pid: number, safe: boolean}` | once, first line, after the engine is constructed (before the first scan finishes) |
| `state` | `Snapshot` (below) | whenever anything changes, **coalesced, at most every 250 ms** (~4/s). Always full. A snapshot identical to the last one sent (apart from `rev`) is not sent again. |
| `progress` | `{rev: number, queue: Queue, game?: {id, statusTitle, statusReason, percent?}}` | queue progress-only changes (pipelines done, rate, time left) between `state` events, also at most ~4/s; `game` = the running game's `Game.statusTitle/statusReason/percent`, which otherwise only change with the next `state`. A `state` event supersedes it. |

After `ui.visible {visible: false}` no `state` or `progress` events are sent (nothing would read them, and the host
relays each one); `ui.visible {visible: true}` sends a fresh `state` at once. A host that never sends `ui.visible` gets
every event. `notice`, `log`, `ready` and `exiting` are always sent.
| `notice` | `Notice` (below) | things the official app shows as a toast/notification |
| `log` | `{line: string}` | engine log lines, only after `logs.subscribe {on: true}` |
| `exiting` | `{reason: "shutdown" \| "stdinClosed" \| "signal" \| "conflict"}` | right before the process exits cleanly (`conflict`: the official app started on the same data folder, see Process) |

`rev` (in `state.data.rev` and `progress.data.rev`) increases with every event, so a UI can drop stale ones.

### Notices

```ts
type Notice =
  | { kind: "driverStale", driver: string, vendor: string, games: {id: string, name: string}[], title: string, text: string }
      // title "NVIDIA driver updated", text "Driver 617.42 cleared the shader cache. 2 games need rebuilding: A, B, about 20 min."
      // "NVIDIA driver updated" toast: offer gameIds via driverStale.compileNow / driverStale.whenIdle / driverStale.skip
  | { kind: "newShaders", compiling: boolean, games: {id: string, name: string, newPipelines: number}[], title: string, text: string }
      // compiling=true: they were queued to compile when idle (setting "Compile automatically and notify me")
  | { kind: "beforeAfterTimeUp", id: string, name: string }   // play time of a before/after run is up (sound setting is the UI's job)
  | { kind: "beforeAfterDone", id: string, name: string }     // report ready: call ba.report
  | { kind: "error", text: string }                           // background failure worth showing (e.g. scan failed, a recording session failed)
  | { kind: "conflict", text: string }                        // the official app started on the same data folder: the service is stopping
```

## Process

```
ExoShaders.Service.exe [--data-dir <dir>] [--safe] [--log-stderr]
ExoShaders.Service.exe --offline-cleanup <gameId>     (internal: the engine's cleanup helper, same as the app)
ExoShaders.Service.exe --asd-remove-all              (Exo Shaders' uninstall: takes out every precompiled shader database Exo registered with Windows)
```

- Data folder: `%LOCALAPPDATA%\SCSKiller` (the official app's, so existing compiles stay valid). `--data-dir` (or env
  `SCSKILLER_DATA_DIR`) points the whole engine at another folder.
- `--safe`: test/inspection mode. The engine does not manage recorders (no recorder install/remove in game folders),
  doesn't run the game watcher (no kept-cache moves, no play detection), doesn't auto-queue new shaders, doesn't apply
  the settings policy (`policy.preview` shows what it would change), and refuses the methods that would compile,
  record, delete, launch games or change driver settings (`error: "not available in safe mode"`).
- Exit codes: `0` clean exit, `2` bad arguments, `3` **refused to start**: the official SCSKiller app (or its CLI) is
  running on the same data folder, another Exo Shaders service holds the data folder, or an SCSKiller update is being
  installed. The reason is one line on stderr. `1` fatal error (details in `<dataDir>\crash.log` and on stderr).
- Single instance: named mutex `Local\ExoShaders.Service.<16 hex of SHA-256 of the lower-cased full data dir path>`.
  The official app is detected as a running `SCSKiller.exe`/`scskiller.exe` process (it uses a Windows App SDK
  instance key, not a mutex, so the service can't block it from starting: the host must not start the official app
  while the service runs). If it starts anyway on the same data folder, the service notices within ~3 s, sends a
  `conflict` notice and stops like a quit (`exiting` reason `conflict`). The service's own bundled `cli\scskiller.exe`
  doesn't count.
- Exit: on `shutdown`, when stdin closes, or on Ctrl+C/Ctrl+Break. Like the official app's Quit: if a compile is
  running, waiting items are removed from the queue and the compile is stopped so the graphics driver writes its
  shader cache, then the service waits for it to finish (up to `shutdown.params.waitSeconds`, default 120), stops the
  game watcher, flushes the log and exits 0.
- Reads from SCSKiller's servers (api.scskiller.com, api.scskiller.io as its fallback) only what the official app reads
  signed out, and sends nothing: after a scan, at most about daily, the engine-support updates (plan-rules.json, game
  verdicts, Maximum suggestions, news; each used only when it verifies with SCSKiller's pinned rules keys, stable
  channel) and the known-stutter, confirmed-engines and boot-precompile lists; the community manifest, the official
  recordings marked for everyone and the shared upscaler packs (plain downloads, no token). Never: the community
  database or Patreon/backer recordings (they need a sign-in), sharing, diagnostics upload, compatibility reports
  ("Rate this compile"), problem reports ("Report a problem": `ProblemReport`/`SendProblemAsync` aren't exposed),
  the anonymous daily check, Patreon sign-in or update checks. `--safe` reads nothing. Pinned third-party downloads
  the engine needs to read some games (Oodle/zlib codecs, FromSoftware/RE Engine key tables, public Unreal key lists
  from GitHub) still happen as in the app.

## Settings policy

Exo Shaders has no settings. The service sets every option itself (`Policy.cs`), through the same engine setters the
methods used (so their side effects run: the driver-update task, recorder reconciliation, re-evaluating the games),
once the first scan is done and again, debounced by 500 ms, after any settings or game change. It is idempotent and
never runs in `--safe` (it would write the data folder). It never queues, starts or stops a compile.

| setting | value |
|---|---|
| `threads` | three quarters of the hardware threads, at least 1 (`cpus - max(1, cpus / 4)`) |
| `backgroundThreads` | the engine default (8), at most `threads` |
| `priority` | `belowNormal` |
| `pauseWhileGaming` | `true`; the service also pauses a running compile while any library game plays and resumes it after (the engine's own pause covers idle-time compiles only). A pause or resume the user presses wins until the games close. |
| `maxCompileMemoryGB` | a quarter of the RAM, 4 to 16 GB, or the engine's Auto when that is more (a quarter of the RAM from 64 GB) |
| `maximumPlans` | `false` (Maximum is per game, below) |
| `compileRayTracing` | `true` |
| `hddMode` | `false` (the engine default) |
| `onDriverUpdate` | `whenIdle` |
| `onNewShaders` | `notify` (no automatic compile) |
| `scanAtStart`, `recordAllGames` | `true` |
| `recordAntiCheatGames` | `false` |
| `recordingLimitMB` | the engine default (1024) |
| `keepCachesAside` | `true` (only has an effect on AMD) |
| `experimentalIntel` | `true` only when the GPU the engine compiles for (the one with the most video memory) is Intel; takes effect at the next start |
| `gpu` | automatic |
| `activeCheck`, `activeCheckAsked` (beta.2) | `false`, `true`: the anonymous daily check stays off and is never asked about (its client isn't wired either) |
| `beforeAfter*`, `theme`, `startWithWindows`, `shareDiagnostics` | not touched |

Per game: API override cleared (Auto: a game that ships both DirectX 11 and 12 is planned for both), new-shaders mode
and ray tracing back to the global values, "left out of compile all ready" off, a recorder turned off goes back to the
default (anti-cheat games are never recorded), careful compile on whenever the engine offers it (AMD), Maximum on
exactly for the games the engine's list suggests it for on this GPU vendor.

The methods that set these options are refused with `"Exo Shaders sets this itself: there is no option for it"`:
`settings.set`, `game.setApi`, `game.setNewShaders`, `game.setCareful`, `game.setRayTracing`, `game.setMaximum`,
`game.setLeftOutOfAddAll`, `game.compileMax`, `queue.addWhenIdle`, `driverStale.whenIdle`, `library.addAllRecommended`,
`recorder.set`, `recorder.install`, `recorder.uninstall`, `recorder.alongside`, `recorder.offline`,
`recorder.startOffline`, `recorder.checkRisk`, `recorder.startRisk`, `driver.setAutoShader`.

## Snapshot

```ts
type Snapshot = {
  rev: number
  app: App
  games: Game[]          // the library, in the app's saved library sort order (see app.librarySort)
  hidden: {id: string, name: string, store: string}[]   // "Show hidden" list
  queue: Queue
  settings: Settings
  policy: Policy
  manualFolders: string[] // folders the library looks for games in ("Add a folder of games")
}

type Policy = {                 // what the settings policy does, for the page's "How it works"
  on: boolean                   // false in --safe (nothing is applied)
  summary: string               // "Balanced settings sized to this PC. Pauses while you play or run on battery."
  threads: number, processorCount: number, memoryGB: number, ramGB: number
  lines: string[]               // "Compiles with 9 of 12 CPU threads at below-normal priority, and up to 2 GB of memory", ...
}

type App = {
  status: "scanning" | "compiling" | "paused" | "checking" | "idle"
      // compiling/paused = the queue's running item; checking = only plan checks run; scanning = a library scan runs
  statusLine: string            // tray tooltip line: "Idle", "Compiling Cyberpunk 2077, 42%", "Paused: X", "Preparing X"
  scanning: boolean
  scanProgress?: string         // "Checking each game… 12 of 80"
  afterUpdateLine?: string      // "Checking your games after the update. This can take a few minutes."
  summary: string               // "80 games found in 4 stores · 12 ready to compile"
  readyCount: number            // "Add all ready (N)" (ready/stale, not queued, not left out of add-all)
  recommendedCount: number      // "Add all recommended (N)"
  waitingCount: number          // "Compile queue (N)"
  speedNote: string             // "Below-normal priority · about 450 pipelines/s measured"
  librarySort: "status" | "nameAscending" | "nameDescending" | "pipelinesDescending" | "pipelinesAscending" | "cacheDescending" | "cacheAscending"
  gpu: {
    name: string, vendor: "nvidia" | "amd" | "intel" | "qualcomm" | "other", vendorName: string, // "NVIDIA"
    driver: string,             // "617.42"
    driverLine: string,         // what the app shows under the GPU name: "Driver 617.42" or the restart note
    windowsCleanup?: {kept: boolean, canUndo: boolean, text: string} | null, // NVIDIA: whether Windows' automatic cleanup can delete D3DSCache (driver.setCleanupGuard)
    notice?: string             // unsupported/experimental GPU notice (dismiss with app.dismissGpuNotice)
  }
  cache?: {                     // driver shader cache; read in the background, absent until first read
    used: number, usedText: string, upperBound: boolean,     // "≤ 12.3 GB" when upperBound
    limit?: number,             // bytes; absent = unlimited or unknown (see limitText)
    limitText: string,          // "of 100 GB limit" | "no limit" | "limit unknown" | "· DirectX 12 x of y limit"
    limitCurrent: string,       // Settings page: "100 GB", "16 GB (driver default)", "Unlimited", "Disabled", "Unknown"
    isDriverDefault: boolean,
    percent: number,            // 0..100 bar
    warn?: string,              // "Driver default limit may evict games" | "The cache is nearly full"
    configurable: boolean,      // driver.setCacheLimit available (NVIDIA)
    largest: boolean,           // the limit is already the largest there is (no limit)
    notConfigurableText?: string,
    amdDx12?: {used: number, text: string, percent: number},  // AMD fixed 16 GB DirectX 12 cache
    path: string,
    free?: number, freeText?: string, freeDrive?: string      // "Free on C:"
    oldDriver: number,          // bytes the current driver no longer reads
    oldDriverNote?: string      // "3.1 GB the new driver no longer uses" (shown when >= 1 GB)
    autoShader?: string         // NVIDIA Auto Shader Compilation: "Now: Off" | "Now: On, Medium" | "Couldn't read the setting" | ...
    sizeIndex?: number          // which settings.meta.cacheSizes entry the current limit is (absent: driver default/other)
    intro: string               // Settings page text above the size picker
  }
  driverStale?: {               // games a driver update cleared (the "driver updated" toast)
    notify: boolean,            // true = the app would show the toast now (mode "ask" and not skipped)
    games: {id: string, name: string}[],
    text: string                // "Driver 617.42 cleared the shader cache. 3 games need rebuilding: A, B, C, about 20 min."
  }
  driverMode: string            // "Ask me first" | "When the PC is idle" | "Off"
  driverModeNote: string
  lastRebuilt?: string          // "Last rebuilt for driver 617.42"
  kept?: {summary: string, games: {id: string, name: string, sizeText: string}[]}  // AMD "Kept aside" (Settings)
  recording: {count: string, space: string, games: {id: string, name: string, size: string, note?: string}[]} // Settings › Recording
  launchers: {name: string, games: number}[]  // the stores the library's games came from, with how many each, in the
                                // order of Game.store below, then Exo's own labels by name ("Added by you" included)
  version: string               // the Exo Shaders service's version ("0.1.0"), as in `ready.service`
  engineVersion: string         // the SCSKiller engine's version ("1.3.0"), as in `ready.engine`
}
```

### Game

```ts
type Game = {
  id: string                    // "steam:2909400"; stable key for every per-game method
  name: string
  store: string                 // "Steam", "Epic Games", "Xbox / Game Pass", "EA app", "GOG", "Ubisoft Connect", "Battle.net",
                                // "Amazon Games", "PURPLE", "HoYoPlay", "Gaijin", "Riot Client", "Other", "Added by you", or
                                // the label Exo gave the game through exo.library ("itch.io", "Added in Exo", ...)
  manual: boolean               // added by the user (rename / game folder / remove = forget)
  exe: string, exeName: string, installDir: string
  sub: string                   // library line under the name: "UE 5.4 · Steam"
  engine: string                // "UE 5.4" / "Unknown engine" (+ " (guessed)")
  engineChip: string            // detail page: "Unreal Engine 5.4 · custom fork"
  engineFamily?: string
  api?: string                  // "DirectX 12" (when no API picker is shown)
  status: "warmed" | "ready" | "needsRecording" | "stale" | "unsupported"   // engine status
  statusText: string            // the app's status word: "Warmed", "Ready to compile", "Needs a 5-min recording",
                                // "Needs rebuilding", "Partly compiled", "Not supported yet", "Encrypted game files", ...
  tone: "accent" | "primary" | "muted" | "warn"   // the app's status colour (accent = done, primary = actionable)
  statusTitle: string           // detail header: statusText, or "In queue (position 2)", or the queue stage ("Compiling", "Paused: ...")
  statusReason: string          // detail line under it: progress while compiling, else the app's reason sentence
  rowNote?: string              // library row note ("Playing now · ...", "Compiled through ReShade", short note)
  tip: string                   // library row tooltip (full note)
  reason: string                // raw engine reason (the "Why?" dialog text for unsupported games)
  playing: boolean
  queued: boolean               // in the queue (not finished)
  compiling: boolean            // running now (any stage)
  compilingSoon: boolean        // running or next to run: Play is disabled
  queuePosition?: number        // 1-based, as "In queue (position N)"
  percent?: number              // 0..100 while warming; absent = indeterminate
  shaders?: number
  pipelines?: number            // plan pipelines (library "Pipelines" column)
  cacheBytes?: number           // measured driver cache, else estimate
  cacheText: string             // "1.2 GB" | "≈ 1.2 GB" (estimate) | "—"
  cacheEstimated: boolean
  compileTime?: string          // last compile time, else estimate ("12 min")
  compiledAt?: string           // ISO date of the last compile (WarmedAt)
  compiledDriver?: string
  recommended: boolean          // in the "Recommended to compile" group
  inAddAllReady: boolean
  tags: {text: string, tip: string}[]      // middleware/mod chips (max 3 + "+N")
  knownStutter?: {severity: "moderate" | "severe", reason: string, source: string, date: string, tip: string}
  noStutter?: string            // "no shader stutter" verdict text
  antiCheat: {kind: "none" | "easyAntiCheat" | "battlEye" | "other", name?: string,  // name (absent for none): ScsKiller.AntiCheatName(game), e.g. "EasyAntiCheat"
               chip: string, tip: string}
  recorder: {
    installed: boolean, effective: boolean,        // effective = what "Record while I play" shows on
    override: "default" | "on" | "off", foreign: boolean,
    skip?: string, note?: string, refused?: string,
    recordNote: string,         // the text under "Record while I play" (beta.2: crash notes, "can't start games from <store>", kept-session notes)
    dll: string,                // the file the recorder goes in as: "d3d12.dll" | "d3d11.dll" | "amd_ags_x64.dll" (AMD, games whose device AGS creates)
    noLaunch: boolean,          // anti-cheat game whose store SCSKiller can't start (HoYoPlay, Battle.net, Riot, EA app): no risk session
    canToggle: boolean,         // recorder.set / recorder.offline allowed now
    useDefaultText?: string,    // "Use default (on)" when an override is set
    mod?: string,               // a mod's d3d12.dll is in the recorder's place
    alongside?: {on: boolean, title: string, note: string},   // "Record alongside <mod>"
    recordingBytes: number, recordingText?: string, paused: boolean, pausedText?: string,
    canClear: boolean, clearTip: string,
    offline?: {                 // anti-cheat games: offline (EAC) or risk session
      risk: boolean,            // true = "Record a session (at your own risk)" (recorder.checkRisk + recorder.startRisk)
      on: boolean, running: boolean, canStart: boolean,
      buttonText: string, riskText: string
    }
    sessionNote?: string
    tipTitle?: string, tip?: string                 // "Record 5 minutes of play" callout
  }
  actions: {                    // which buttons the app shows/enables for this game
    compile: boolean,           // detail "Compile" enabled (CanCompile && CanAdd)
    compileText: string,        // "Compile" | "Compile carefully" | "Compile without ray tracing (partial)" | "In queue"
    compileTip?: string,
    add: boolean,               // library "Add to queue" shown (ready/stale) ...
    addEnabled: boolean,        // ... and enabled (not queued)
    record: boolean, recordEnabled: boolean,   // library "Record" button (recorder.install), enabled only with no anti-cheat (VAC included); never for a risk-session game
    details: boolean, why: boolean,
    play: boolean, playEnabled: boolean, playText: string, playTip: string,
    clearCache: boolean, clearCacheEnabled: boolean, clearCacheText: string, clearCacheTip: string,
    setAesKey: boolean,         // "Set AES key…" (encrypted game)
    rename: boolean, gameFolder: boolean,      // manual games
    remove: {text: string, tip: string, confirmTitle: string, confirmText: string}  // manual: forget; store: hide
  }
  kept?: {bytes: number, frees: number, sizeText: string, state: string}   // AMD kept-aside cache
  keptCacheNote?: string
  beforeAfter: {
    shown: boolean, enabled: boolean, text: string, note?: string, tip?: string,
    stage?: "beforeArmed" | "beforePlaying" | "compiling" | "afterReady" | "afterArmed" | "afterPlaying" | "done",
    minutes?: number, playedSeconds?: number, problem?: string, voided: boolean, clearPending: boolean,
    chip?: string, hasMenu: boolean, compileAgain: boolean
  }
  similar?: {text: string, warning: boolean}
  guessNote?: string, keyNote?: string /* "Key from the community's public list" when the AES key came from a public key list */, partlyCompiled: boolean, partlyCompiledWhy?: string, needsAesKey: boolean
  dxvk?: {compiled: boolean, text: string}  // beta.2: DXVK (its d3d11.dll/dxgi.dll) runs the game on Vulkan; compiled = NVIDIA compiles
                                // through DXVK's own d3d11.dll ("...the compile covers it on NVIDIA"), else "...the compile isn't used".
                                // Not repeated in statusReason.
  cacheLimitWarning?: string    // beta.2, NVIDIA: this game's estimated cache is more than the driver's shader cache limit (engine text,
                                // "...Raise the limit in Settings before you compile"); the queue's cacheWarning says the same for the queue
  error?: string                // last error of an action on this game (the app's red info bar on the game page); cleared by the next success
  frames?: {verdict: string, summary: string, stuttersLabel: string, stutters: string}  // last play; graph data: game.detail
}
```

### Queue

```ts
type Queue = {
  running: boolean              // the queue is started (not only idle-waiting items)
  compiling: boolean            // a compile (or plan check) process runs
  paused: boolean
  summary: string               // "3 games · about 25 min in total · not started" / "Nothing queued"
  planChecks?: string           // "Checking 2 games for more to compile (while idle)"
  cacheWarning?: string
  cpuSummary: string            // "CPU use: 6 of 16 threads, below-normal priority"
  pauseText: string             // "Pause" | "Resume"
  canStart: boolean, canClear: boolean
  current?: QueueItem           // the running item (number 1)
  waiting: QueueItem[]          // in order (numbers continue)
  finished: QueueItem[]         // done/failed/stopped, until removed or the queue starts again
}
type QueueItem = {
  id: string, name: string, number: number,
  stage: "waiting" | "indexing" | "planning" | "materializing" | "warming" | "paused" | "done" | "failed" | "stopped"
  stageText: string             // "Reading shaders", "Building plan", "Preparing files", "Compiling", "Paused: ..."
  detail?: string               // "Compiling · 42.0% · 12,000 of 28,000 pipelines · 450/s"
  progressText?: string         // "42.0% · 12,000 of 28,000 pipelines · 450/s · 3 failed"
  percent?: number              // absent = indeterminate
  done?: number, total?: number, failed?: number, skipped?: number, perSecond?: number
  time: string                  // "12 min left" | "12 min" | "—" (finished: last compile time)
  timeLeftSeconds?: number
  note: string                  // "starts when the PC is idle", "Failed: ...", "Stopped at x of y: add it again to continue", "Done · ..."
  cache: string                 // game's cache text
  whenIdle: boolean
  error?: string
}
```

### Settings

```ts
type Settings = {
  threads: number               // compile threads (Queue page slider), 1..processorCount
  priority: "belowNormal" | "idle"
  backgroundThreads: number     // threads for idle/background compiles
  pauseWhileGaming: boolean
  maxCompileMemoryGB: number    // 0 = Auto
  maximumPlans: boolean         // "Maximum mode" for every game
  compileRayTracing: boolean
  hddMode: boolean
  onDriverUpdate: "ask" | "whenIdle" | "off"   // registers/unregisters the "re-warm after a driver update" task
  onNewShaders: "off" | "notify" | "compileAndNotify" | "compileSilently"
  scanAtStart: boolean
  recordAllGames: boolean       // "Record while I play" default for every game
  recordAntiCheatGames: boolean // risk sessions for anti-cheat games
  recordingLimitMB: number      // one of meta.recordingLimits[].mb (0 = no limit)
  keepCachesAside: boolean      // AMD
  experimentalIntel: boolean
  gpu?: string                  // adapter LUID (16 hex) of the GPU to compile for; absent = automatic
  beforeAfter: boolean, beforeAfterSound: boolean, beforeAfterOverlay: boolean
  theme: "system" | "light" | "dark"   // stored for the official app; the service ignores it
  startWithWindows: boolean     // stored only: starting the service with Windows is the host's job
  shareDiagnostics: boolean     // stored only: the service never uploads
  meta: {                       // read-only helpers for the settings UI
    processorCount: number, autoCompileMemoryGB: number, ramGB: number, maxCompileMemoryGB: number,
    maxCompileMemoryText: string, maxCompileMemoryNote: string, priorityNote: string,
    recordingLimits: {mb: number, label: string}[],
    newShadersLabels: string[], // ["Off", "Notify me", "Compile automatically and notify me", "Compile automatically, silently"]
    gpuChoices: {luid: string, name: string, vendor: string}[],   // more than 1 = show the GPU picker
    hasIntelGpu: boolean,
    cacheSizes: {index: number, label: string}[]                  // for driver.setCacheLimit
  }
}
```

## Methods

All per-game methods take `{id: string}` (the game id) plus the listed params, and fail with
`"no game <id>"` if the game isn't in the library.

### General

| method | params | result |
|---|---|---|
| `snapshot` | – | `Snapshot` |
| `game.detail` | `{id}` | `GameDetail` (below) |
| `whatsNew` | `{all?: boolean}` | `{version: string, sections: {version: string, bullets: Span[][]}[]}`: the engine's embedded CHANGELOG as the app's "What's new" dialog shows it (each bullet's first sentence); the current version's section, or every released section with `all`. `Span = {text, kind: "text"\|"bold"\|"code"\|"link", url?}` |
| `whatsNew.atStart` | – | `{section?: {version, bullets: Span[][]}}`: the "What's new in X" dialog to show once after an update (marks it seen, like the app) |
| `logs.subscribe` | `{on: boolean}` | enables/disables `log` events |
| `ui.visible` | `{visible: boolean}` | tells the engine whether a UI is on screen (the app trims memory while hidden; `state`/`progress` events pause while hidden, see Events). `visible: true` also re-measures the games' caches, like the app on window activation, and sends a fresh `state`. |
| `policy.preview` | – | what the settings policy would change now, without changing anything (works in `--safe`): `{applies: boolean, settings: string, games: {id, name, api?, bothApis: boolean, changes: string[]}[]}` (games listed when something would change or they ship both DirectX 11 and 12) |
| `app.dismissGpuNotice` | – | hides `app.gpu.notice` for this GPU |
| `shutdown` | `{waitSeconds?: number}` | `{ok: true}` then a clean exit (see Process) |

### Library and scanning

| method | params | result / semantics |
|---|---|---|
| `scan` | `{force?: boolean}` | Refresh (the Library refresh button: `userRequested`). `force` = Shift+Refresh: re-read every game's engine and anti-cheat. Resolves when the scan ends: `{games: number}`. Errors as `"Scan failed: ..."`. |
| `game.refresh` | `{id}` | Re-evaluate one game (detail "Refresh") |
| `game.check` | `{id}` | Check an unchecked game ("Not checked": the detail page does this on open) |
| `cache.refreshSizes` | – | Re-measure every game's cache on disk (the app does this when its window is activated, at most every 10 s) |
| `library.setSort` | `{sort}` | `app.librarySort` values; saved like the app |
| `library.addAllReady` | – | Enqueue every game counted in `readyCount` |

### Compile queue

| method | params | semantics |
|---|---|---|
| `game.compile` | `{id}` | "Compile now": if the app offers "Compile carefully" (`actions.compileText`), turns careful compile on first; queues the game first among the waiting ones and starts the queue (a running compile finishes first). |
| `queue.add` | `{id}` or `{ids: string[]}` | "Add to queue" (waits for Start) |
| `queue.remove` | `{id}` | remove one (stops it if it's running) |
| `queue.removeMany` | `{ids?: string[]}` | bulk remove waiting/finished items (not the running one); no `ids` = "Clear queue" |
| `queue.clearFinished` | – | remove finished items |
| `queue.move` | `{id, index}` | move a waiting item to 0-based `index` among waiting items (0 = "Move to top") |
| `queue.start` | – | "Compile queue": idle-waiting items become normal and the queue starts |
| `queue.pause` / `queue.resume` | – | pause/resume the running compile (the user's choice wins over the pause while gaming until the games close) |
| `queue.stop` | – | stop the running compile (it keeps what's compiled; add it again to continue) |
| `driverStale.compileNow` | `{ids?: string[]}` | toast "Compile now" (default: the driver-stale games) |
| `driverStale.skip` | – | toast "Skip" (`DismissStale`) |

### Recorder

Recording follows the settings policy (every game that can be recorded, never anti-cheat games); the recorder
switches, offline and risk sessions are refused (see Settings policy).

| method | params | semantics |
|---|---|---|
| `recorder.clear` | `{id}` | "Clear recording". Error `"Nothing was cleared: no recording of this game was found."` when there is none. Confirm first with `CLEAR_RECORDING_CONFIRM`. |

### Per-game actions

The per-game options (API, ray tracing, new shaders, careful compile, Maximum, left out of compile all ready) belong
to the settings policy; their setters are refused.

| method | params |
|---|---|
| `game.setAesKey` | `{id, key: string}` → `{ok: true}` or error `"That key didn't work: It doesn't open this game's files."` (the key is "0x followed by 64 hex digits"; verified, kept locally). The game is rescanned after a good key. |
| `game.play` | `{id}` → `{ok: true}` or error: puts a kept-aside cache back, then starts the game through its store (or arms/launches the before/after run). Error e.g. `"Start X from its own launcher"`. |

### Game caches

| method | params | result |
|---|---|---|
| `game.caches` | `{id}` | `{parts: {name, bytes, size, files: number, own: boolean}[], title: string, text: string, ownText?: string, note: string, gap?: string}`: what the "Clear the shader cache of X?" dialog shows (`own` = the game's own cache, deleted only with `includeGameCache`) |
| `game.clearCache` | `{id, includeGameCache?: boolean}` | `{ok: true}` or error `"Nothing was cleared: no shader-cache files of this game were found."` |
| `game.asdUndo` | `{id, allow?: boolean}` | take out the game's precompiled shader database (built on this PC from its compile and registered with Windows for its exe as installer "Exo") and its files; kept off for the game until called with `allow: true` |
| `game.putBackKept` | `{id}` | put an AMD kept-aside cache back now |
| `game.dropKept` | `{id}` | Settings › Kept aside › Remove |

### Manual games

| method | params | result / flow |
|---|---|---|
| `games.previewExe` | `{exe}` | `{game: {id, name, exe, installDir, store}, existed: boolean, hidden: boolean, listed: boolean, message?: string, folderProblem?: string, title: string, text: string}` (`title`/`text`: the folder dialog's). Flow of the app's "Add a game": if `existed` the app shows `message` ("Already in your library", "Hidden from your library") and stops; otherwise it shows the **game folder dialog** (pre-filled with `game.installDir`, `GAME_FOLDER_TEXT`), validating every pick with `games.checkFolder`, and adds with `games.addExe`. |
| `games.checkFolder` | `{exe, folder}` | `{problem?: string}`: the dialog's error (the Add button is disabled while there is one) |
| `games.addExe` | `{exe, folder}` | adds the game with its confirmed folder (re-validated; error if the folder has a problem), then rescans. `{id}` |
| `games.setFolder` | `{id, folder}` | detail "Game folder…" for a manual game (same validation) |
| `games.addFolder` | `{path}` | "Add a folder of games…", then rescans |
| `games.removeFolder` | `{path}` | "Stop looking for games here", then rescans |
| `games.rename` | `{id, name}` | manual games only ("Only the name SCSKiller shows changes.") |
| `games.remove` | `{id}` | the detail page's Remove: manual game = forget it (its recorder leaves the folder); store game = hide it. Confirm texts in `actions.remove`. |
| `games.hide` / `games.show` | `{id}` | hide a game / bring a hidden one back |

### Exo's library

Where the engine finds games, in this order: Steam; Epic Games (the old launcher's `.item` manifests; the new 2026
launcher's `installations.jsonl`, in the folder `HKCU\Software\Epic Games\EpicGamesLauncher` `AppDataPath` names, by
default `%LOCALAPPDATA%\com.epicgames.eglv2`, newest line per installation, titled from its
`cache\prod\catalog-cache.json`; Legendary's `installed.json`, which Exo Launcher installs with, in
`LEGENDARY_CONFIG_PATH`, `XDG_CONFIG_HOME\legendary`, `%USERPROFILE%\.config\legendary`, `%APPDATA%\legendary`,
`%LOCALAPPDATA%\legendary`, each also under `heroic\legendaryConfig`, and `%LOCALAPPDATA%\ExoLauncher\(tools\)legendary`);
Xbox (`.GamingRoot` libraries, `XboxGames`, `ModifiableWindowsApps`); GOG (Galaxy's `GOG.com\Games` keys in HKLM and
HKCU, the gogdl installs Exo Launcher records in its `installed-games.json`, Heroic's `gog_store\installed.json`);
Ubisoft Connect; Battle.net; PURPLE; HoYoPlay; EA app; Gaijin; Riot Client; Amazon Games (the app's uninstall entries,
Nile's `installed.json`, `fuel.json` game folders); the games below; then the games and folders you added. One game per
install folder: the first source to find a folder keeps it. Epic ids are `epic:<AppName>` whichever record found the
game; Amazon ids `amazon:<product id>`, GOG ids `gog:<product id>`.

| method | params | result / semantics |
|---|---|---|
| `exo.library` | `{games: {id: string, title: string, store: string, installDir: string, exe?: string}[]}` | Exo's installed games, as a safety net. The list **replaces** the previous one (a game no longer in it leaves the library at the next scan) and is kept in `<dataDir>\exo-library.json`, so its games are there at the next start before Exo sends it again. The service rescans only when the list changed. Result `{ok: true, changed: boolean, games: number, refused: {id, title, problem}[]}`: `games` = entries kept; `refused` (worked out only when the list changed) = games whose folder no other source found and that Exo Shaders won't take, with the sentence `games.checkFolder` would give, e.g. `"C:\ is a whole drive: pick the game's own folder."`. |

Each game whose `installDir` isn't the folder of a game another source found (nor inside or around one) becomes a
discovered game: `id` = `"exo:" + Exo's id` (when that is 1 to 120 of `A-Z a-z 0-9 . _ : -`, else `"exo:"` + 16 hex of
the folder's hash), `store` = the given `store` (empty: `"Exo Launcher"`), `manual: false`, `exe` = the given `exe` when
it is an existing `.exe` inside `installDir` (absolute or relative to it), else the folder's game as an added folder is
read (the Unreal `-Shipping.exe` first, then the most likely game exe). Folders `games.checkFolder` would refuse (a whole
drive, a library folder like `Program Files`, a folder of several games) are left out. It then goes through the same
engine, anti-cheat and recorder checks as every game; `games.remove` hides it like any store game. Entries without an
`id` or a full `installDir` path are ignored; a second entry with the same id is dropped.

### Before and after measurement

Headless-feasible: the engine does everything; the UI shows the dialogs. See `Game.beforeAfter` for the button text
and state.

| method | params | semantics |
|---|---|---|
| `ba.blocked` | `{id}` | `{blocked?: string}` why it can't start now |
| `ba.start` | `{id, minutes: 5 \| 10, overlay?: boolean, sound?: boolean, over?: boolean}` | "Start the before run" (after the intro dialog, `BA_INTRO`): clears the game's caches, arms the recorder and launches the game (when launchable). `over: true` = "Start over" (ends the current one first). Saves `beforeAfterIntroSeen/Sound/Overlay` like the app. Returns `{ok: true, launchError?: string}`. |
| `ba.launch` | `{id}` | "Start <game>" for an armed run |
| `ba.continue` | `{id}` | "Continue with the after recording" / "Clear the cache and try again" / compile again |
| `ba.recordAfterAgain` | `{id}` | "Record the after run again" |
| `ba.cancel` | `{id, compile: boolean}` | "Cancel": `compile` = "Compile now" vs "Don't compile" |
| `ba.report` | `{id}` | `{report?: BeforeAfterResult}`: the engine's record (camelCase): `{before: SessionSnapshot, after: SessionSnapshot, compare: {before: RunStats, after: RunStats, perTenMinutes, alreadySmooth, notes, dx11}}`; snapshots carry `durationMs, startupMs, peaks[], hitches[{at, ms, cause}]` for the graph |

### Cache page

| method | params | result |
|---|---|---|
| `cache.inventory` | – | `Inventory` (below). Reading takes a few seconds. |
| `cache.delete` | `{keys: string[]}` or `{group: kind}` or `{all: true, withOthers?: boolean}` | deletes items of the **last** inventory read (re-read with `cache.inventory` first; unknown keys → error `"The cache list changed: refresh it and try again."`). Result `{ok: true, bytes, files, skipped: string[], text, warning: boolean}` (`warning` = some files were in use); errors `"Nothing was deleted: <reason>"` (e.g. while a compile runs). |
| `cache.freeOldDriver` | – | deletes the "Old driver" group (the "Free X" button) |

```ts
type Inventory = {
  total: string                 // "38.2 GB of shader caches"
  totalBytes: number
  noFolders: boolean
  driverInstalled?: string
  oldDriver?: {bytes: number, text: string, freeText: string}
  everythingWarning: {withOthers: string, withoutOthers: string}   // texts for "Clear everything"
  folders: {label: string, path: string, size: string, note?: string}[]
  groups: {
    kind: "installed" | "uninstalled" | "oldDriver" | "oldBuild" | "otherVendor" | "unattributed",
    title: string, caption: string, size: string, bytes: number, note?: string,
    deletable: boolean,         // per-item delete (not "unattributed" as a whole... see app)
    consequence: string,        // confirm text for deleting the whole group
    items: {key: string, name: string, size: string, bytes: number, note?: string, outside?: string, gameIds: string[], files: number, consequence: string}[]
  }[]
}
```

### Settings and driver

| method | params | result |
|---|---|---|
| `settings.get` | – | `Settings` (read-only: the settings policy sets them) |
| `driver.setCacheLimit` | `{index}` (`settings.meta.cacheSizes`) | NVIDIA shader cache size. Runs the bundled CLI elevated (Windows asks for administrator permission), as the app does. `{ok: true, text}` / `{ok: false, text}` (`"Cancelled."` when UAC is declined). Confirm first with `CACHE_LIMIT_CONFIRM`. |
| `driver.setCleanupGuard` | `{on: boolean}` | `on`: Windows' automatic cleanup (SilentCleanup, Storage Service) no longer runs the DirectX Shader Cache handler, which can empty D3DSCache, where NVIDIA from 621 keeps compiled pipelines; `off`: undo. Clears or sets bit 0 of that handler's `Autorun` value only. Runs the bundled CLI elevated (`cleanup-guard on\|off`). `{ok, text}` as `driver.setCacheLimit`. |

## GameDetail (`game.detail`)

`Game` plus the heavier detail-page content:

```ts
type GameDetail = Game & {
  coverage: {value: string, label: string, headline: string, low: boolean, good: boolean, percent?: number}
  sources: {label: string, value: string, note?: string}[]     // "Where the plan comes from"
  details: {label: string, value: string}[]                    // the Details expander
  left?: {title: string, text: string}                         // "Some shaders still compile while you play"
  compile: {title: string, text: string, skippedTitle?: string, skippedText?: string}
  session?: {header: string, verdict: string, label: string, stateObjects?: string, rayQuery?: string, fromLibrary: number, cacheHits: number, compiles: number}
  frames?: {durationMs: number, startupMs: number, peaks: number[], hitches: {atMs: number, ms: number, cause: string}[],
            verdict: string, summary: string, low1PctFps: number, avgFps: number}   // FrameGraph data of the last play (replaces Game.frames here)
  framesSummary?: Game["frames"]                             // the summary Game.frames carries in the snapshot
  hitchRows: {label: string, value: string}[]
  earlierSessions: {label: string, value: string}[]
  middleware?: {chip: string, tip: string}
  apiChip?: string
  dbNote?: string
}
```

## Appendix: confirmation texts the app shows

The service doesn't show dialogs; these are the official app's texts so the UI can match them.

- `RECORD_CONFIRM` (title `Record <name>?`, button `Add recorder`): "SCSKiller adds a small d3d12.dll next to the game
  that writes down every pipeline it creates. Play for about 5 minutes, close the game, then add it to the compile
  queue. You can remove it any time from the game's page."
- `OFFLINE_CONFIRM` (title `Start <name> offline without EasyAntiCheat?`, button `Record offline session`): "Starts the
  game offline without EasyAntiCheat. SCSKiller removes its files when the game exits (after a crash: at the next
  logon). If any are left when you play online, you could be banned. Steam must be running. Don't uninstall SCSKiller
  mid-session. Online play isn't possible in that session."
- Risk session: `recorder.checkRisk` returns the title/text.
- `CLEAR_RECORDING_CONFIRM` (title `Clear the recording of <name>?`, button `Clear recording`): "The recorded
  pipelines are removed. They're compiled again only if the game creates them again while recording, and the compile
  plan goes back to what the game's files give.\n\nThe recorder itself stays in, and the game's files aren't touched."
- Clear cache (title `Clear the shader cache of <name>?`, button `Clear cache`): `game.caches` → `text` + checkbox
  `ownText` when there are own caches.
- `MAX_COMPILE` (title `Max compile <name>?`): returned by `game.compileMax` as `needsConfirm`.
- `GAME_FOLDER_TEXT`: "Before it records, SCSKiller checks the whole game folder for anti-cheat. Pick the game's own
  folder, not one that holds other games." Labels: Game, "Program (the recorder goes next to it)", "Game folder".
- `CACHE_LIMIT_CONFIRM` (title `Set the shader cache limit to <label>?`, button `Change limit`): "This is a global
  <vendor> driver setting: it applies to every game on this PC, not only the ones SCSKiller compiles, and stays until
  you change it. Windows will ask for administrator permission.\n\nRaising it deletes nothing. Lowering it below what
  is used now makes the driver delete older entries, and those games stutter again. You can switch back to the driver
  default here at any time."
- `BA_INTRO` (title "Before and after recording", button "Start the before run"): warning "This also deletes the
  game's own shader cache, which isn't backed up and can be 2 GB. The game rebuilds it, which can take minutes.";
  steps 1–4 and bullets as in the app; choices "5 minutes"/"10 minutes", "Play a sound when the time is up", "Show the
  countdown in the game".
- Cancel before/after (title "Cancel the before and after recording?"): "Its shader caches were cleared, so the game
  stutters until it's compiled again." Buttons "Compile now" (`compile: true`), "Don't compile" (`compile: false`),
  "Keep going".
