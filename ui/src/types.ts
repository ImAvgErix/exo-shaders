// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// The engine service's shapes, as engine/CONTRACT.md (protocol 1) defines them.

export type LibrarySort = 'status' | 'nameAscending' | 'nameDescending' | 'pipelinesDescending' | 'pipelinesAscending' | 'cacheDescending' | 'cacheAscending'

export type CacheInfo = {
  used: number; usedText: string; upperBound: boolean
  limit?: number; limitText: string; limitCurrent: string; isDriverDefault: boolean
  percent: number; warn?: string; configurable: boolean; notConfigurableText?: string
  amdDx12?: { used: number; text: string; percent: number }
  path: string; free?: number; freeText?: string; freeDrive?: string
  oldDriver: number; oldDriverNote?: string; autoShader?: string; sizeIndex?: number; intro: string
  /** The limit is already the largest there is (no limit). */
  largest: boolean
}

export type App = {
  status: 'scanning' | 'compiling' | 'paused' | 'checking' | 'idle'
  statusLine: string
  scanning: boolean
  scanProgress?: string
  afterUpdateLine?: string
  summary: string
  readyCount: number
  recommendedCount: number
  waitingCount: number
  speedNote: string
  librarySort: LibrarySort
  gpu: { name: string; vendor: 'nvidia' | 'amd' | 'intel' | 'qualcomm' | 'other'; vendorName: string; driver: string; driverLine: string; shaderDelivery?: { works: boolean; text: string }; windowsCleanup?: { kept: boolean; canUndo: boolean; text: string } | null; notice?: string }
  cache?: CacheInfo
  driverStale?: { notify: boolean; games: { id: string; name: string }[]; text: string }
  driverMode: string
  driverModeNote: string
  lastRebuilt?: string
  kept?: { summary: string; games: { id: string; name: string; sizeText: string }[] }
  recording: { count: string; space: string; games: { id: string; name: string; size: string; note?: string }[] }
  /** The stores the library's games came from, with how many each. */
  launchers?: { name: string; games: number }[]
  /** The Exo Shaders service's version and the SCSKiller engine's. */
  version?: string
  engineVersion?: string
}

export type Tone = 'accent' | 'primary' | 'muted' | 'warn'

export type BeforeAfterStage = 'beforeArmed' | 'beforePlaying' | 'compiling' | 'afterReady' | 'afterArmed' | 'afterPlaying' | 'done'

export type FramesSummary = { verdict: string; summary: string; stuttersLabel: string; stutters: string }

export type Game = {
  id: string
  name: string
  store: string
  manual: boolean
  exe: string; exeName: string; installDir: string
  sub: string
  engine: string
  engineChip: string
  engineFamily?: string
  api?: string
  status: 'warmed' | 'ready' | 'needsRecording' | 'stale' | 'unsupported'
  statusText: string
  tone: Tone
  statusTitle: string
  statusReason: string
  rowNote?: string
  tip: string
  reason: string
  playing: boolean
  queued: boolean
  compiling: boolean
  compilingSoon: boolean
  queuePosition?: number
  percent?: number
  shaders?: number
  pipelines?: number
  cacheBytes?: number
  cacheText: string
  cacheEstimated: boolean
  compileTime?: string
  compiledAt?: string
  compiledDriver?: string
  recommended: boolean
  inAddAllReady: boolean
  tags: { text: string; tip: string }[]
  knownStutter?: { severity: 'moderate' | 'severe'; reason: string; source: string; date: string; tip: string }
  noStutter?: string
  antiCheat: { kind: 'none' | 'easyAntiCheat' | 'battlEye' | 'other'; name?: string; chip: string; tip: string }
  recorder: {
    installed: boolean; effective: boolean
    override: 'default' | 'on' | 'off'; foreign: boolean
    skip?: string; note?: string; refused?: string
    recordNote: string
    dll?: string                // the file the recorder goes in as (d3d12.dll, d3d11.dll, amd_ags_x64.dll)
    noLaunch?: boolean
    canToggle: boolean
    useDefaultText?: string
    mod?: string
    alongside?: { on: boolean; title: string; note: string }
    recordingBytes: number; recordingText?: string; paused: boolean; pausedText?: string
    canClear: boolean; clearTip: string
    offline?: { risk: boolean; on: boolean; running: boolean; canStart: boolean; buttonText: string; riskText: string }
    sessionNote?: string
    tipTitle?: string; tip?: string
  }
  actions: {
    compile: boolean; compileText: string; compileTip?: string
    add: boolean; addEnabled: boolean
    record: boolean; recordEnabled: boolean
    details: boolean; why: boolean
    play: boolean; playEnabled: boolean; playText: string; playTip: string
    clearCache: boolean; clearCacheEnabled: boolean; clearCacheText: string; clearCacheTip: string
    setAesKey: boolean
    rename: boolean; gameFolder: boolean
    remove: { text: string; tip: string; confirmTitle: string; confirmText: string }
  }

  kept?: { bytes: number; frees: number; sizeText: string; state: string }
  keptCacheNote?: string
  beforeAfter: {
    shown: boolean; enabled: boolean; text: string; note?: string; tip?: string
    stage?: BeforeAfterStage
    minutes?: number; playedSeconds?: number; problem?: string; voided: boolean; clearPending: boolean
    chip?: string; hasMenu: boolean; compileAgain: boolean
  }
  similar?: { text: string; warning: boolean }
  guessNote?: string
  keyNote?: string
  partlyCompiled: boolean
  partlyCompiledWhy?: string
  needsAesKey: boolean
  dxvk?: { compiled: boolean; text: string }
  cacheLimitWarning?: string
  error?: string
  frames?: FramesSummary
}

export type Hitch = { atMs?: number; at?: string | number; ms: number; cause: string }

export type FrameData = {
  durationMs: number; startupMs: number; peaks: number[]; hitches: Hitch[]
  verdict?: string; summary?: string; low1PctFps?: number; avgFps?: number
}

export type GameDetail = Omit<Game, 'frames'> & {
  coverage: { value: string; label: string; headline: string; low: boolean; good: boolean; percent?: number }
  sources: { label: string; value: string; note?: string }[]
  details: { label: string; value: string }[]
  left?: { title: string; text: string }
  compile: { title: string; text: string; skippedTitle?: string; skippedText?: string }
  session?: { header: string; verdict: string; label: string; stateObjects?: string; rayQuery?: string; fromLibrary: number; cacheHits: number; compiles: number }
  /** D3D12's own count of the last play: "92% of pipelines came from the cache". */
  cacheUse?: { verdict: string; detail: string; percent: number; pipelines: number }
  frames?: FrameData & { verdict: string; summary: string; low1PctFps: number; avgFps: number }
  framesSummary?: FramesSummary
  hitchRows: { label: string; value: string }[]
  earlierSessions: { label: string; value: string }[]
  middleware?: { chip: string; tip: string }
  apiChip?: string
  dbNote?: string
}

export type QueueItem = {
  id: string; name: string; number: number
  stage: 'waiting' | 'indexing' | 'planning' | 'materializing' | 'warming' | 'paused' | 'done' | 'failed' | 'stopped'
  stageText: string
  detail?: string
  progressText?: string
  percent?: number
  done?: number; total?: number; failed?: number; skipped?: number; perSecond?: number
  time: string
  timeLeftSeconds?: number
  note: string
  cache: string
  whenIdle: boolean
  error?: string
}

export type Queue = {
  running: boolean
  compiling: boolean
  paused: boolean
  summary: string
  planChecks?: string
  cacheWarning?: string
  cpuSummary: string
  pauseText: string
  canStart: boolean; canClear: boolean
  current?: QueueItem
  waiting: QueueItem[]
  finished: QueueItem[]
}

export type NewShadersMode = 'off' | 'notify' | 'compileAndNotify' | 'compileSilently'

export type Settings = {
  threads: number
  priority: 'belowNormal' | 'idle'
  backgroundThreads: number
  pauseWhileGaming: boolean
  maxCompileMemoryGB: number
  maximumPlans: boolean
  compileRayTracing: boolean
  hddMode: boolean
  onDriverUpdate: 'ask' | 'whenIdle' | 'off'
  onNewShaders: NewShadersMode
  scanAtStart: boolean
  recordAllGames: boolean
  recordAntiCheatGames: boolean
  recordingLimitMB: number
  keepCachesAside: boolean
  experimentalIntel: boolean
  gpu?: string
  beforeAfter: boolean; beforeAfterSound: boolean; beforeAfterOverlay: boolean
  theme: 'system' | 'light' | 'dark'
  startWithWindows: boolean
  shareDiagnostics: boolean
  meta: {
    processorCount: number; autoCompileMemoryGB: number; ramGB: number; maxCompileMemoryGB: number
    maxCompileMemoryText: string; maxCompileMemoryNote: string; priorityNote: string
    recordingLimits: { mb: number; label: string }[]
    newShadersLabels: string[]
    gpuChoices: { luid: string; name: string; vendor: string }[]
    hasIntelGpu: boolean
    cacheSizes: { index: number; label: string }[]
  }
}

/** The settings policy: Exo Shaders sets every option itself (engine Policy.cs); this says what it does. */
export type Policy = {
  on: boolean
  summary: string
  threads: number
  processorCount: number
  memoryGB: number
  ramGB: number
  lines: string[]
}

export type Snapshot = {
  rev: number
  app: App
  games: Game[]
  hidden: { id: string; name: string; store: string }[]
  queue: Queue
  settings: Settings
  policy: Policy
  manualFolders: string[]
}

export type Progress = { rev: number; queue: Queue; game?: { id: string; statusTitle: string; statusReason: string; percent?: number } }

export type Notice =
  | { kind: 'driverStale'; driver: string; vendor: string; games: { id: string; name: string }[]; title: string; text: string }
  | { kind: 'newShaders'; compiling: boolean; games: { id: string; name: string; newPipelines: number }[]; title: string; text: string }
  | { kind: 'beforeAfterTimeUp'; id: string; name: string }
  | { kind: 'beforeAfterDone'; id: string; name: string }
  | { kind: 'error'; text: string }
  | { kind: 'conflict'; text: string }

export type InventoryGroupKind = 'installed' | 'uninstalled' | 'oldDriver' | 'oldBuild' | 'otherVendor' | 'unattributed'

export type Inventory = {
  total: string
  totalBytes: number
  noFolders: boolean
  driverInstalled?: string
  oldDriver?: { bytes: number; text: string; freeText: string }
  everythingWarning: { withOthers: string; withoutOthers: string }
  folders: { label: string; path: string; size: string; note?: string }[]
  groups: {
    kind: InventoryGroupKind
    title: string; caption: string; size: string; bytes: number; note?: string
    deletable: boolean
    consequence: string
    items: { key: string; name: string; size: string; bytes: number; note?: string; outside?: string; gameIds: string[]; files: number; consequence: string }[]
  }[]
}

export type Span = { text: string; kind: 'text' | 'bold' | 'code' | 'link'; url?: string }
export type WhatsNew = { version: string; sections: { version: string; bullets: Span[][] }[] }

export type GameCaches = { parts: { name: string; bytes: number; size: string; files: number; own: boolean }[]; title: string; text: string; ownText?: string; note: string; gap?: string }

export type RunStats = {
  playSeconds: number; shaderStutters: number; frozenSeconds: number; worstStutterMs: number; readyShare?: number
  playCompiles: number; low1PctFps: number; avgFps: number; otherHitches: number; hitches?: number; hitchSeconds?: number; worstHitchMs?: number
}
export type SessionSnapshot = { durationMs: number; startupMs: number; peaks: number[]; hitches: Hitch[]; low1PctFps?: number; avgFps?: number; playSeconds?: number }
export type BeforeAfterReport = {
  before: SessionSnapshot; after: SessionSnapshot
  compare: { before: RunStats; after: RunStats; perTenMinutes: boolean; alreadySmooth: boolean; notes: string[]; dx11?: boolean }
}

export type ExoGame = { id: string; title: string; path: string | null; cover: string | null }
