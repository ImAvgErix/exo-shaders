using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Dagor;
using SCSKiller.Core.Decima;
using SCSKiller.Core.ForzaTech;
using SCSKiller.Core.FromSoft;
using SCSKiller.Core.Games;
using SCSKiller.Core.Luminous;
using SCSKiller.Core.NaughtyDog;
using SCSKiller.Core.Nixxes;
using SCSKiller.Core.Northlight;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Rage;
using SCSKiller.Core.ReEngine;
using SCSKiller.Core.RedEngine;
using SCSKiller.Core.SantaMonica;
using SCSKiller.Core.Source2;
using SCSKiller.Core.Tank;
using SCSKiller.Core.Unity;
using SCSKiller.Core.Unreal;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.App;

public sealed class ScsKiller : IScsKiller
{
	private sealed record OldBefore(string? DriverId, DateTime? At);

	private sealed record GpuSnapshot(GpuInfo Gpu, string? Id, bool Complete, bool Moved, string? Dxgi, string? Fallback);

	public enum ServerCheck
	{
		Done,
		TooSoon,
		Unreachable
	}

	private readonly record struct Pending(long Recorded, long? Planned, bool Unknown = false, long Upscaler = 0L);

	private sealed class BelowNormalScheduler : TaskScheduler
	{
		public static readonly BelowNormalScheduler Instance = new BelowNormalScheduler(Environment.ProcessorCount);

		[ThreadStatic]
		private static bool ours;

		private readonly BlockingCollection<Task> tasks = new BlockingCollection<Task>();

		private readonly int threads;

		public override int MaximumConcurrencyLevel => threads;

		private BelowNormalScheduler(int threads)
		{
			this.threads = threads;
			for (int i = 0; i < threads; i++)
			{
				Thread thread = new Thread(() =>
				{
					SetThreadPriority(GetCurrentThread(), 65536);
					Thread currentThread = Thread.CurrentThread;
					ours = true;
					currentThread.Priority = ThreadPriority.BelowNormal;
					foreach (Task item in tasks.GetConsumingEnumerable())
					{
						TryExecuteTask(item);
					}
				});
				thread.IsBackground = true;
				thread.Name = "SCSKiller background index";
				thread.Start();
			}
		}

		protected override void QueueTask(Task task)
		{
			tasks.Add(task);
		}

		protected override bool TryExecuteTaskInline(Task task, bool queued)
		{
			if (ours)
			{
				return TryExecuteTask(task);
			}
			return false;
		}

		protected override IEnumerable<Task> GetScheduledTasks()
		{
			return tasks.ToArray();
		}
	}

	private enum IniState
	{
		Absent,
		Ours,
		NotOurs,
		Unknown
	}

	private sealed class NotReadyException : InvalidOperationException
	{
		public NotReadyException(string message)
			: base(message)
		{
		}
	}

	private struct LastInputInfo
	{
		public uint Size;

		public uint Time;
	}

	private sealed class Reporter<T>(Action<T> report) : IProgress<T>
	{
		public void Report(T value)
		{
			report(value);
		}
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct StartupInfo
	{
		public int Size;

		public string? Reserved;

		public string? Desktop;

		public string? Title;

		public int X;

		public int Y;

		public int XSize;

		public int YSize;

		public int XCountChars;

		public int YCountChars;

		public int FillAttribute;

		public int Flags;

		public short ShowWindow;

		public short Reserved2Size;

		public nint Reserved2;

		public nint StdInput;

		public nint StdOutput;

		public nint StdError;
	}

	private struct ProcessInformation
	{
		public nint Process;

		public nint Thread;

		public int ProcessId;

		public int ThreadId;
	}

	public const string NoStartNote = "Didn't see the game start";

	public const string LostNote = "The game started again before SCSKiller read the run, so it's lost";

	public const string DriverVoidNote = "The driver changed since the before recording, start over";

	public const string BuildVoidNote = "The game was updated since the before recording, start over";

	public const string NotCompiledNote = "The compile didn't finish, compile again";

	public const string StoppedNote = "The compile was stopped";

	public const string DirtyNote = "The game ran after the compile, Continue compiles again first";

	public const string DriverRedoNote = "The driver was updated, play the before run again";

	public const string BuildRedoNote = "The game was updated, play the before run again";

	public const string RecorderNotReadyNote = "The recorder isn't ready, so the run wouldn't be measured: ";

	public const string OldCacheNote = "Old driver cache files were in use: close the game and play the before run again";

	public const string OldCacheRedoNote = "Old driver cache files were in use, play the before run again";

	public const string UnseenRedoNote = "SCSKiller wasn't running during the before run, so it couldn't check the driver cache: play it again";

	public const string FramesStoppedNote = "Frame times stopped when frame generation started, so this run can't be measured. Turn frame generation off for the before and after recording";

	public const string RestoreFailedNote = "Couldn't put back {0} of AMD's DirectX 11 cache files: they're in {1}";

	private static readonly TimeSpan NoStartAfter = TimeSpan.FromMinutes(5L);

	private static readonly TimeSpan PlayReadEvery = TimeSpan.FromSeconds(2L);

	private readonly Dictionary<string, DateTimeOffset> _pairPolled = new Dictionary<string, DateTimeOffset>();

	private readonly ConcurrentDictionary<string, double> _played = new ConcurrentDictionary<string, double>();

	private readonly ConcurrentDictionary<string, Task> _learning = new ConcurrentDictionary<string, Task>();

	private readonly ConcurrentDictionary<string, byte> _pairInstall = new ConcurrentDictionary<string, byte>();

	private readonly object _setAsideLock = new object();

	private static readonly TimeSpan LaunchFileMargin = TimeSpan.FromSeconds(30L);

	private static readonly TimeSpan LearnWait = TimeSpan.FromSeconds(60L);

	public const string OtherApps = "Other apps and games";

	public const string NoAppKey = "Files without an app key";

	public const string CompileLeftovers = "Windows shader cache of earlier compiles";

	private static readonly (GpuVendor Vendor, string Label, string Folder)[] VendorFolders = new (GpuVendor, string, string)[6]
	{
		(GpuVendor.Nvidia, "NVIDIA DirectX", "NVIDIA\\DXCache"),
		(GpuVendor.Nvidia, "NVIDIA OpenGL and Vulkan", "NVIDIA\\GLCache"),
		(GpuVendor.Amd, "AMD DirectX 12", "AMD\\DxcCache"),
		(GpuVendor.Amd, "AMD DirectX 11", "AMD\\DxCache"),
		(GpuVendor.Amd, "AMD Vulkan", "AMD\\VkCache"),
		(GpuVendor.Amd, "AMD OpenGL", "AMD\\GLCache")
	};

	private OldBefore? _oldBefore;

	public const double DefaultBytesPerPso = 24576.0;

	public const double DefaultPsoPerSecond = 450.0;

	public const long LargeCompile = 17179869184L;

	private const string LargeCacheCost = "Very large caches can make the game start slower and use more memory.";

	private static readonly byte[] ProxyMarker = "SCSKiller_StartWarm"u8.ToArray();

	private const string RecorderIni = "[scskiller]\r\n; written by SCSKiller: record the pipelines this game creates. Removed by 'uninstall recorder'.\r\nmode=record\r\n";

	private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500L);

	private static readonly TimeSpan AttributionInterval = TimeSpan.FromSeconds(3L);

	internal static readonly string CoreBuild = BuildOf(typeof(ScsKiller).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "", typeof(ScsKiller).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault((AssemblyMetadataAttribute m) => m.Key == "SourceRevisionId")?.Value);

	private readonly IReadOnlyList<IGameSource> _sources;

	private readonly IEngineReader _reader;

	private readonly IPlanner _planner;

	private readonly IWarmer _warmer;

	private readonly string? _proxyDll;

	private readonly object _lock = new object();

	private readonly object _scanLock = new object();

	private readonly List<QueueItem> _queue = new List<QueueItem>();

	private readonly HashSet<string> _whenIdle = new HashSet<string>();

	private readonly HashSet<string> _planOnly = new HashSet<string>();

	private readonly HashSet<string> _maxNext = new HashSet<string>();

	private readonly HashSet<string> _prompt = new HashSet<string>();

	private readonly ManualResetEventSlim _go = new ManualResetEventSlim(initialState: true);

	private List<GameState> _games = new List<GameState>();

	private Dictionary<string, Evaluation>? _scan;

	private readonly Dictionary<string, long> _scanStarted = new Dictionary<string, long>();

	private long _evaluations;

	private Settings? _settings;

	private Task? _worker;

	private bool _running;

	private string? _current;

	private QueueStage _stage;

	private IWarmRun? _run;

	private CancellationTokenSource? _itemCts;

	private volatile string? _pauseWhy;

	private readonly ConcurrentDictionary<string, long> _ownGameSeen = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, LaunchedExe> _launched = new ConcurrentDictionary<string, LaunchedExe>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, bool> _driverStale = new Dictionary<string, bool>();

	public const string AdapterArg = "--adapter-luid";

	private DxgiAdapter? _known;

	private GpuChoice? _startGpu;

	private bool _resolved;

	private DateTimeOffset? _goneSince;

	private readonly object _gpuLock = new object();

	public static readonly TimeSpan GpuGoneAfter = TimeSpan.FromMinutes(15L);

	private string? _id;

	private string? _completeId;

	private readonly object _refreshLock = new object();

	private readonly object _scanLoop = new object();

	public static readonly TimeSpan UserFetchEvery = TimeSpan.FromMinutes(10L);

	private DateTime _userFetched;

	private List<Game>? _freshGames;

	private int _updateScans;

	public const string AfterUpdateText = "Checking your games after the update. This can take a few minutes.";

	private readonly object _keepLock = new object();

	private CompatFeed? _compat;

	private DateTime _stutterTried;

	private readonly object _channelLock = new object();

	private (bool Known, IReadOnlyCollection<string>? Ent) _channelEnt;

	private string[]? _rulesRead;

	private string[]? _newsRead;

	private readonly ConcurrentDictionary<string, bool> _unread = new ConcurrentDictionary<string, bool>();

	public const string CheckingReason = "checking its files";

	public const string NotCheckedReason = "not checked yet: open it to check";

	private readonly ConcurrentDictionary<string, bool> _asked = new ConcurrentDictionary<string, bool>();

	private static readonly string[] RecorderDataFiles = new string[4] { "scskiller.db", "scskiller_creates.csv", "scskiller.log", "scskiller_frames.bin" };

	private readonly ConcurrentDictionary<string, (long Bin, DateTime Written, long Csv, PlayWindow? Played, FrameReport? Report)> _frames = new ConcurrentDictionary<string, (long, DateTime, long, PlayWindow, FrameReport)>();

	private readonly ConcurrentDictionary<string, string?> _trimmedLogged = new ConcurrentDictionary<string, string>();

	private readonly ConcurrentDictionary<string, FrameSession> _noted = new ConcurrentDictionary<string, FrameSession>();

	public static readonly int[] RecordingLimits = new int[6] { 32, 128, 256, 512, 1024, 0 };

	public const string RtInlineNote = "ray tracing that uses separate pipelines (such as path tracing) is compiled only from a recording";

	public static readonly TimeSpan EnoughRecording = TimeSpan.FromMinutes(5L);

	public const string NothingRecordedTitle = "Played, nothing recorded";

	public const string NeverSawNote = "The recorder never loaded in the last launch. Please report it with the game's name.";

	public const string NeverSawDx11Note = "The recorder didn't load in the last launch. The game may have run on DirectX 11.";

	public const string NeverSawDx12Note = "The recorder didn't load in the last launch. The game may have run on DirectX 12.";

	public const string GameChangedNote = "The game changed since the recorder was set up. It is set up again: play again.";

	public const string CollectionSwitchNote = "The last launch wasn't recorded: the recorder was set up for the game played before. It is set up for this game now: play again.";

	private const string CollectionSwitchRefusal = "stamped for another game of its collection";

	private const string RanOtherExe = "The last launch ran ";

	public const string RtSkippedNote = "Ray tracing skipped (your setting)";

	public const string RtToCompileReason = "ray tracing shaders are still to compile";

	public const string RtUnseenNote = "no ray tracing seen while recording: turn it on in the game and play to cover it";

	public const string RtNeedsRecording = "Ray-traced effects need one short recording";

	public const string InDbNote = "in the community database";

	public const string InDbSupportersNote = "in the community database for Patreon supporters";

	public const string RtAfterRecordingNote = "ray tracing needs a 5-min recording";

	public const string RecordingSuffix = " (recording)";

	public const string ChoiceSuffix = " (your choice)";

	public const string ManualNoRecording = "once you confirm its game folder";

	public const string StreamlineNoRecording = "which DLSS on NVIDIA blocks";

	public const string RiskNoRecording = "from a session on the game page, at your own risk";

	public const string OfflineSessionNote = "EasyAntiCheat blocks the recorder, but you can record at your own risk with an offline session.";

	private CommunityManifest? _manifest;

	private string? _manifestStamp;

	public const double PartlyCompiledShare = 0.1;

	public const int AmdCarefulThreads = 4;

	public const double DefaultCarefulPsoPerSecond = 67.0;

	public const double PartlyWarmedShare = 0.2;

	public const long MinJudgedCreates = 100L;

	public const double DefaultAmdPsoPerSecond = 160.0;

	private double? _warmRate;

	private readonly ConcurrentDictionary<string, Game> _redetect = new ConcurrentDictionary<string, Game>();

	private Task _redetecting = Task.CompletedTask;

	public const string ReadFailed = "couldn't read the game files";

	private static readonly EnumerationOptions AllEntries = new EnumerationOptions
	{
		IgnoreInaccessible = false,
		AttributesToSkip = FileAttributes.None
	};

	private const string IndexChangedReason = "game shaders changed since the warm";

	internal const string NotThroughLayerReason = "the last compile didn't run through the game's HDR mod";

	public const string MissesGameReason = "the compile didn't reach this game's cache: the game uses another driver-cache key";

	public const string CantReachReason = "the compile can't reach this game: it reads its shaders from a different driver cache than the one SCSKiller fills. Clear its cache to free the space";

	public const string UnreachedReason = "the compile didn't help this game on NVIDIA, it needs a custom loader";

	public const string ClearForGameNote = "its compiled cache isn't used, clear it to free the space";

	public const double UnreachedMedianMs = 20.0;

	public const string TrimmedPartReason = "part of this game's compiled shader cache is gone, compile again";

	public const string TrimmedAllReason = "this game's compiled shader cache is gone, compile again";

	public const string OverCacheLimitReason = "this game's shaders don't fit the driver's shader cache, raise its size";

	public const string CacheFullReason = "the driver's shader cache is full, so it removed older games' files";

	public const string DriverPart = "Driver cache";

	public const string WindowsPart = "Windows shader cache";

	public const string PipelinePart = "Game's pipeline cache";

	public const string PrecachePart = "Game's shader precache";

	private readonly ConcurrentDictionary<string, AntiCheat> _verdicts = new ConcurrentDictionary<string, AntiCheat>();

	private long _tickets;

	private readonly Dictionary<string, long> _evaluatedAt = new Dictionary<string, long>();

	private long _scans;

	private long _listed;

	private readonly ConcurrentDictionary<string, (long, DateTime)> _drawsChecked = new ConcurrentDictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> _restateFailed = new HashSet<string>();

	internal Action? RecordingsRead;

	internal Action? CacheSampled;

	internal Action? InboxRotating;

	internal Action? MigrationLoaded;

	private readonly ConcurrentDictionary<string, bool> _indexRetry = new ConcurrentDictionary<string, bool>();

	private long _indexRetried;

	private readonly ConcurrentDictionary<string, CancellationTokenSource> _recorderIndexes = new ConcurrentDictionary<string, CancellationTokenSource>();

	private readonly ConcurrentDictionary<string, byte> _parkedIndexes = new ConcurrentDictionary<string, byte>();

	private int _readingIndexes;

	public const string SkipAntiCheat = "anti-cheat";

	public const string SkipForeignDll = "a mod's DLL is in the recorder's place";

	public const string SkipModNotChainable = "a mod's DLL is in the recorder's place and stops working renamed";

	public const string SkipVulkanMod = "vkd3d-proton or DXVK runs the game on Vulkan, whose pipelines a DirectX warm doesn't compile";

	public const string SkipNeedsAdmin = "the game folder needs administrator";

	public const string SkipNotDx12 = "not DirectX 12";

	public const string SkipUnsupported = "not supported yet";

	public const string SkipPackageD3D12 = "a DLL in the game's package folder loads instead of the recorder";

	public const string SkipShaderMod = "an HDR mod changes every pipeline";

	public const string SkipStreamline = "DLSS on NVIDIA loads DirectX 12 before the recorder can";

	public const string SkipManual = "game folder not confirmed";

	public const string SkipCrashes = "the recorder crashes this game, so it's off for now";

	private static readonly HashSet<string> NoRecorderExes = new HashSet<string>(new _003C_003Ez__ReadOnlyArray<string>(new string[5] { "ACShadows", "ACMirage", "ACBlackFlag", "ACValhalla", "SCPSL" }), StringComparer.OrdinalIgnoreCase);

	public const string ChainName = "d3d12.scskiller-next.dll";

	public const string ChainName11 = "d3d11.scskiller-next.dll";

	public const string Dll12 = "d3d12.dll";

	public const string Dll11 = "d3d11.dll";

	public const string AgsName = "amd_ags_x64.dll";

	public const string AgsRealName = "amd_ags_real.dll";

	private static readonly string[] ProxyNames = new string[3] { "d3d12.dll", "d3d11.dll", "amd_ags_x64.dll" };

	public const string DxvkNote = "DXVK runs this game on Vulkan, so the compile isn't used";

	public const string DxvkCompiledNote = "DXVK runs this game on Vulkan, and the compile covers it on NVIDIA";

	public const string ModRemovedNote = "a mod SCSKiller recorded alongside was removed, so recording is off for this game";

	private readonly object _recorderLock = new object();

	private readonly ConcurrentDictionary<string, string> _recorderSkips = new ConcurrentDictionary<string, string>();

	private readonly ConcurrentDictionary<string, string> _recorderNotes = new ConcurrentDictionary<string, string>();

	private readonly HashSet<string> _removed = new HashSet<string>();

	private Dictionary<string, Game> _hidden = new Dictionary<string, Game>();

	private long _hideChanges;

	public const string ForeignNote = "placed by another SCSKiller copy, so it may still record. Turn it on here to take it over, then off to remove it";

	private static readonly string[] SessionFiles = new string[3] { "scskiller_creates.csv", "scskiller.log", "scskiller_frames.bin" };

	private string? _proxySha;

	private string? _agsSha;

	private readonly ConcurrentDictionary<string, Game> _moving = new ConcurrentDictionary<string, Game>();

	private readonly ConcurrentDictionary<string, bool> _rollbacks = new ConcurrentDictionary<string, bool>();

	private static readonly string[] IniComments = new string[5] { "; the recording limit per game (Settings): no new records once scskiller.db has this many bytes", "; the game's own d3d12.dll (a mod), renamed by SCSKiller and put back when the recorder is removed", "; the game's install folder: the recorder checks its entries for anti-cheat too", "; the game's exe when SCSKiller last found no anti-cheat in its folders: the recorder records only while it is unchanged", "; frame times for a before and after recording, nothing else is recorded" };

	private static readonly TimeSpan UninstallBudget = TimeSpan.FromSeconds(20L);

	internal static readonly TimeSpan GoneFor = TimeSpan.FromHours(1);

	private static readonly EnumerationOptions InstallTree = new EnumerationOptions
	{
		RecurseSubdirectories = true,
		IgnoreInaccessible = false,
		AttributesToSkip = FileAttributes.None
	};

	public const string WhenIdleNote = "starts when the PC is idle";

	public const string AnotherCompileNote = "waits for another SCSKiller compile of this game";

	private static readonly Regex FailTally = new Regex("warm: failed x(\\d+): (.*)", RegexOptions.CultureInvariant);

	private static readonly Regex FailTally11 = new Regex("warm11: (\\S+) failed: (.*) x(\\d+)\\s*$", RegexOptions.CultureInvariant);

	public const string LastWarmLog = "warm-last.log";

	public const int LastWarmLogBytes = 4194304;

	private const int MaxReplans = 2;

	private volatile HashSet<string> _playing = new HashSet<string>();

	private readonly Dictionary<string, int> _absent = new Dictionary<string, int>();

	private readonly HashSet<string> _toFollow = new HashSet<string>();

	private readonly Dictionary<string, PlayWindow> _runs = new Dictionary<string, PlayWindow>();

	private DateTimeOffset? _lastPoll;

	public const int ExitPolls = 2;

	public static readonly TimeSpan GpuCheckInterval = TimeSpan.FromMinutes(5L);

	public static readonly TimeSpan RecorderCheckInterval = TimeSpan.FromMinutes(2L);

	public static readonly TimeSpan KeyCacheIdle = TimeSpan.FromMinutes(1L);

	public static readonly TimeSpan CollectAfterIdle = TimeSpan.FromSeconds(30L);

	private volatile bool _collect;

	private readonly ConcurrentDictionary<string, Stopwatch> _removalTried = new ConcurrentDictionary<string, Stopwatch>();

	internal static readonly HashSet<string> RecorderOwnFiles;

	private static readonly HashSet<string> DataTypes;

	public const string ArmedFile = "scskiller.armed";

	private static string? _ledgerDir;

	public const string LedgerStamp = "scskiller.ledger";

	private static string? _recorderDir;

	private readonly ConcurrentDictionary<string, string> _collectionArmed = new ConcurrentDictionary<string, string>();

	private readonly ConcurrentDictionary<string, DateTimeOffset> _endedAt = new ConcurrentDictionary<string, DateTimeOffset>();

	private static readonly HashSet<string> StampKeys;

	private static readonly HashSet<string> SignedLineKeys;

	private readonly ConcurrentDictionary<string, long> _removalGen = new ConcurrentDictionary<string, long>();

	internal const string NoteForeignIni = "records nothing: the scskiller.ini in its folder isn't SCSKiller's, so it's left as it is";

	private readonly ConcurrentDictionary<string, string> _unstamped = new ConcurrentDictionary<string, string>();

	private static readonly object _revokedLock;

	private const string KeptFolder = "kept";

	private readonly object _keptLock = new object();

	public const string SteamAppIdFile = "steam_appid.txt";

	public const string CleanupArg = "--offline-cleanup";

	public const string SteamFirst = "Start Steam first (offline mode is fine).";

	private const string TempSuffix = ".scskiller-new";

	private readonly ConcurrentDictionary<string, SafeProcessHandle> _offline = new ConcurrentDictionary<string, SafeProcessHandle>();

	private readonly ConcurrentDictionary<string, string> _sessionNotes = new ConcurrentDictionary<string, string>();

	public const string UnboundSessionNote = "This launch started before SCSKiller could attach: nothing was recorded. Try again.";

	private ConcurrentDictionary<string, bool>? _offlineLeft;

	private readonly object _updateGate = new object();

	private int _updating;

	private int _offlineStarting;

	public const string UpdateInstalling = "An update is being installed. Try again after it.";

	internal const string RiskLine = "risk=1\r\n";

	internal const string RiskLogLine = "risk=1: recording at the user's own risk";

	private const uint CreateSuspended = 4u;

	private const uint Terminate = 1u;

	private const uint Synchronize = 1048576u;

	private const uint QueryLimitedInformation = 4096u;

	private const uint WaitTimeout = 258u;

	public Func<DateTime?> DriverInstalledAt { get; set; } = NvidiaBackend.InstalledAt;

	public Func<bool>? CompileRunning { get; set; }

	public static string DeletingName => "Local\\SCSKiller.CacheDelete" + Busy.Scope;

	internal string? AgsDll { get; set; }

	public IGpuVendorBackend Vendor { get; }

	public AppStore Store { get; }

	public Func<IReadOnlyList<DxgiAdapter>>? Adapters { get; set; }

	public string? GpuRestartNote { get; private set; }

	public string? DriverId => _id;

	public IReadOnlyList<GpuChoice> GpuChoices
	{
		get
		{
			IReadOnlyList<DxgiAdapter> adapters = Adapters?.Invoke() ?? Array.Empty<DxgiAdapter>();
			List<DxgiAdapter> list = GpuBackends.Usable(adapters, Settings.ExperimentalIntel).ToList();
			if (list == null || list.Count <= 1)
			{
				return Array.Empty<GpuChoice>();
			}
			List<GpuChoice> list2 = new List<GpuChoice>();
			list2.AddRange(list.Select(GpuBackends.ChoiceOf));
			return new _003C_003Ez__ReadOnlyList<GpuChoice>(list2);
		}
	}

	public bool HasIntelGpu => (Adapters?.Invoke() ?? Array.Empty<DxgiAdapter>()).Any((DxgiAdapter a) => a.Gpu.Vendor == GpuVendor.Intel);

	public bool Background { get; set; }

	public int? ThreadsOverride { get; set; }

	public IProgress<string>? Log { get; set; }

	public bool CheckPlans { get; set; }

	internal long? NewMinimum { get; init; }

	public bool ManageRecorders { get; set; }

	public Func<IReadOnlySet<string>> ProcessNames { get; set; } = RunningProcessNames;

	internal Func<bool, List<(int Pid, int Parent, string Exe)>> Processes { get; set; } = (bool fresh) => (!fresh) ? ProcessTree.RecentSnapshot() : ProcessTree.Snapshot();

	internal Func<int, string?> ProcessPath { get; set; } = ProcessTree.ImagePath;

	public IAppCache? AppCache { get; set; }

	public string LocalAppData { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

	public string MyGames { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "My Games");

	public string Documents { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Personal);

	public string RoamingAppData { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

	public string ProgramData { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

	public Community? Community { get; set; }

	public Func<bool> DbEntitled { get; set; } = () => false;

	public Task CommunitySync { get; private set; } = Task.CompletedTask;

	public Sharing? Sharing { get; set; }

	public Task SharingPass { get; private set; } = Task.CompletedTask;

	public Diagnostics? Diagnostics { get; set; }

	public Task DiagnosticsSent { get; private set; } = Task.CompletedTask;

	public ActiveCheck? ActiveCheck { get; set; }

	public Task ActiveCheckSent { get; private set; } = Task.CompletedTask;

	public RouteFailover? ContentRoutes { get; set; }

	public Func<Task<(string? Token, IReadOnlyCollection<string>? Ent)>>? ChannelAccess { get; set; }

	public Task StutterUpdate { get; private set; } = Task.CompletedTask;

	public Func<TimeSpan> IdleTime { get; set; } = UserIdleTime;

	public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(2L);

	public TimeSpan StallAfter { get; set; } = TimeSpan.FromSeconds(60L);

	public TimeSpan GameClosedFor { get; set; } = TimeSpan.FromMinutes(2L);

	public Settings Settings
	{
		get
		{
			Settings settings = _settings;
			Settings settings2;
			if ((object)settings == null)
			{
				Settings s = Store.LoadSettings();
				if (s.RecordingLimitMB != 256 || s.RecordingLimitChosen)
				{
					if (s.RecordingLimitMB > 0)
					{
						int num = RecordingLimits.FirstOrDefault((int l) => l >= s.RecordingLimitMB);
						if (num != s.RecordingLimitMB)
						{
							settings2 = s with
							{
								RecordingLimitMB = num,
								RecordingLimitChosen = true
							};
							goto IL_00b5;
						}
					}
					settings2 = s;
				}
				else
				{
					settings2 = s with
					{
						RecordingLimitMB = AppStore.DefaultSettings.RecordingLimitMB
					};
				}
				goto IL_00b5;
			}
			goto IL_00bd;
			IL_00bd:
			return settings;
			IL_00b5:
			Settings settings3 = settings2;
			_settings = settings2;
			settings = settings3;
			goto IL_00bd;
		}
		set
		{
			if (value.RecordingLimitMB != Settings.RecordingLimitMB)
			{
				value = value with
				{
					RecordingLimitChosen = true
				};
			}
			bool flag = value.MaximumPlans != Settings.MaximumPlans;
			bool flag2 = value.UseCommunityDb && !Settings.UseCommunityDb;
			bool flag3 = value.ShareRecordings && !Settings.ShareRecordings;
			bool flag4 = SCSKiller.Core.App.ActiveCheck.Allowed(value) && !SCSKiller.Core.App.ActiveCheck.Allowed(Settings);
			bool flag5 = value.RecordingLimitMB != Settings.RecordingLimitMB;
			bool flag6 = (value.RecordAllGames != Settings.RecordAllGames) | flag5;
			bool flag7 = value.UpdateChannel != Settings.UpdateChannel;
			bool flag8 = value.Gpu != Settings.Gpu;
			bool riskOff = !value.RecordAntiCheatGames && Settings.RecordAntiCheatGames;
			bool keptOff = !value.KeepCachesAside && Settings.KeepCachesAside;
			bool num = flag | (value.KeepCachesAside != Settings.KeepCachesAside) | (value.UseCommunityDb != Settings.UseCommunityDb) | (value.RecordAntiCheatGames != Settings.RecordAntiCheatGames) | (value.CompileRayTracing != Settings.CompileRayTracing) | (value.HddMode != Settings.HddMode);
			Store.SaveSettings(value);
			_settings = value;
			if (num | flag5)
			{
				SettingsRefresh = Task.Run(() =>
				{
					lock (_refreshLock)
					{
						try
						{
							if (riskOff)
							{
								RiskOff();
							}
							if (keptOff)
							{
								DropStaleKept();
							}
							foreach (GameState game in Games)
							{
								Refresh(game.Game);
							}
						}
						catch (Exception ex)
						{
							Log?.Report("re-evaluating games for the new settings failed: " + ex.Message);
						}
					}
				});
			}
			if (flag7)
			{
				lock (_channelLock)
				{
					ApplyChannels(always: false);
				}
			}
			if (flag8)
			{
				Task.Run((Func<bool>)RedetectGpu);
			}
			if (flag2)
			{
				StartCommunitySync();
			}
			if (flag3)
			{
				StartSharing();
			}
			if (flag4)
			{
				ActiveCheck active = ActiveCheck;
				if (active != null)
				{
					ActiveCheckSent = Task.Run(() => active.SendAsync());
				}
			}
			if (!flag6 || !ManageRecorders)
			{
				return;
			}
			Task.Run(() =>
			{
				try
				{
					ReconcileRecorders();
				}
				catch (Exception ex)
				{
					Log?.Report("reconciling recorders failed: " + ex.Message);
				}
			});
		}
	}

	public Task SettingsRefresh { get; private set; } = Task.CompletedTask;

	public IReadOnlyList<GameState> Games
	{
		get
		{
			lock (_lock)
			{
				return _games.ToList();
			}
		}
	}

	public IReadOnlyList<QueueItem> Queue
	{
		get
		{
			lock (_lock)
			{
				return _queue.ToList();
			}
		}
	}

	private string UserFetchFile => Path.Combine(Store.DataDir, "user-fetch.txt");

	public Func<Task>? UserFetch { get; set; }

	public Task<ServerCheck> ServerRefresh { get; private set; } = Task.FromResult(ServerCheck.Done);

	public string? AfterUpdateLine
	{
		get
		{
			lock (_lock)
			{
				return (Volatile.Read(in _updateScans) > 0 || _queue.Any((QueueItem q) => q.PlanCheck && !Finished(q.Stage) && _prompt.Contains(q.GameId))) ? "Checking your games after the update. This can take a few minutes." : null;
			}
		}
	}

	public string? ScanProgress { get; private set; }

	internal Func<string, string, string>? ShippingLookup { get; set; }

	private string StutterFile => Path.Combine(Store.DataDir, "known-stutter.json");

	private string ImagesFile => Path.Combine(Store.DataDir, "middleware.json");

	private string ProbesFile => Path.Combine(Store.DataDir, "reshade.json");

	private string ConfirmedFile => Path.Combine(Store.DataDir, "confirmed-engines.json");

	private string PrecompilesFile => Path.Combine(Store.DataDir, "precompiles-at-boot.json");

	private string VerdictsFile => Path.Combine(Store.DataDir, "game-verdicts.json");

	private string MaximumFile => Path.Combine(Store.DataDir, "maximum-mode.json");

	private string RulesPath => Path.Combine(Store.DataDir, "plan-rules.json");

	private string NewsPath => Path.Combine(Store.DataDir, "news.json");

	private ContentTrust RulesTrust => new ContentTrust(Store, ContentKeys);

	public IReadOnlyDictionary<string, string> ContentKeys { get; set; } = ContentTrust.RulesKeys;

	public IReadOnlyDictionary<string, string> CompatKeys
	{
		get;
		set
		{
			field = value;
			_compat = CompatFeed.Cached(CompatTrust, CompatFile);
		}
	} = ContentTrust.CompatKeys;

	private string CompatFile => Path.Combine(Store.DataDir, "compat.json");

	private ContentTrust CompatTrust => new ContentTrust(Store, CompatKeys, "compat", 1048576);

	public News News { get; private set; }

	private bool FullEntries => Vendor.Vendor == GpuVendor.Nvidia;

	private double WarmRate
	{
		get
		{
			double valueOrDefault = _warmRate.GetValueOrDefault();
			double num;
			if (!_warmRate.HasValue)
			{
				valueOrDefault = Store.LoadWarmRates().GetValueOrDefault(Vendor.Vendor);
				_warmRate = valueOrDefault;
				num = valueOrDefault;
			}
			else
			{
				num = valueOrDefault;
			}
			double num2 = num;
			if (!(num2 > 0.0))
			{
				return DefaultWarmRate(Vendor.Vendor);
			}
			return num2;
		}
	}

	private bool CaseSensitiveCache => Vendor.Vendor != GpuVendor.Nvidia;

	private bool OnAmd => Vendor.Caps.Profile.StartsWith("amd");

	internal bool HoldsRecorderLock => Monitor.IsEntered(_recorderLock);

	public TimeSpan IndexRetryInterval { get; set; } = TimeSpan.FromMinutes(1L);

	public Task RecorderIndexing { get; private set; } = Task.CompletedTask;

	internal Func<bool> OtherCompile { get; set; } = () => Busy.IsHeld();

	public Task RecordingMigration { get; private set; } = Task.CompletedTask;

	public ManualSource? Manual => _sources.OfType<ManualSource>().FirstOrDefault();

	public ExoLibrarySource? ExoLibrary => _sources.OfType<ExoLibrarySource>().FirstOrDefault();

	public IReadOnlyList<Game> HiddenGames
	{
		get
		{
			lock (_lock)
			{
				List<Game> list = new List<Game>();
				list.AddRange(_hidden.Values.OrderBy((Game g) => g.Name, StringComparer.OrdinalIgnoreCase));
				return new _003C_003Ez__ReadOnlyList<Game>(list);
			}
		}
	}

	internal Action? FolderChanging { get; set; }

	internal Func<int, bool> OwnWarm { get; set; } = (int pid) => WarmJob.Current.Contains(pid);

	public Action<string>? InstallStep { get; set; }

	public Action<string>? EvaluateStep { get; set; }

	public bool QueueRunning
	{
		get
		{
			lock (_lock)
			{
				return _running;
			}
		}
	}

	public bool Compiling
	{
		get
		{
			lock (_lock)
			{
				return _current != null;
			}
		}
	}

	private bool UserIdle => IdleTime() >= IdleAfter;

	public Func<IReadOnlySet<string>> RunningGameExes { get; set; }

	public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

	public TimeSpan WatchInterval { get; set; } = TimeSpan.FromSeconds(3L);

	// Exo Shaders: the watch slows to this while no game is playing, being
	// followed or compiling; a game start is then seen within it.
	public TimeSpan IdleWatchInterval { get; set; } = TimeSpan.FromSeconds(8L);

	public Func<bool> Unseen { get; set; } = () => true;

	internal bool CollectPending => _collect;

	public Action TrimWorkingSet { get; set; } = ProcessMemory.Trim;

	public TimeSpan RecorderCheckTimeout { get; set; } = GameFiles.Budget + TimeSpan.FromSeconds(5L);

	public TimeSpan RemovalRetryInterval { get; set; } = TimeSpan.FromSeconds(30L);

	internal Func<Game, AntiCheat> FullAntiCheatCheck { get; set; } = (Game g) => GameFiles.DetectAntiCheat(g);

	internal Func<Game, bool> VacCheck { get; set; } = (Game g) => Vac.Is(g);

	internal Func<string?> VacReadError { get; set; } = () => Vac.ReadError();

	internal static string LedgerDir
	{
		get
		{
			return _ledgerDir ?? (_ledgerDir = DataLedger);
		}
		set
		{
			_ledgerDir = value;
		}
	}

	internal static string DataLedger => Path.Combine(AppStore.DefaultDir, "armed");

	internal static string DefaultLedger { get; set; }

	private static bool DefaultCopy => SameDir(LedgerDir, DefaultLedger);

	internal static string RecorderDir
	{
		get
		{
			return _recorderDir ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(LedgerDir)));
		}
		set
		{
			_recorderDir = value;
		}
	}

	internal Action<string>? AttestStep { get; set; }

	internal Action? ClaimStep { get; set; }

	internal static string Revoked => Path.Combine(RecorderDir, "recorder.revoked");

	public bool OfflineBlocksUpdate
	{
		get
		{
			if (_offlineStarting <= 0 && _offline.IsEmpty)
			{
				return OfflineLeft().Keys.Any((string id) =>
				{
					if (!HelperRuns(id))
					{
						OfflineSession offlineSession = Store.LoadGame(id).OfflineSession;
						if ((object)offlineSession != null)
						{
							return Alive(offlineSession);
						}
						return false;
					}
					return true;
				});
			}
			return true;
		}
	}

	internal Action<string, string> AddRunOnce { get; set; } = (string gameId, string exe) =>
	{
		RunOnce(gameId, exe);
	};

	internal string OfflineArguments { get; set; } = "";

	internal Func<Game, ProcessStartInfo?> LaunchCommand { get; set; } = (Game g) => StoreLaunch.Command(g);

	public string? CleanupHelper { get; set; }

	internal Func<OfflineSession, bool> OfflineRuns { get; set; }

	internal Func<Game, bool> StoreLaunches { get; set; } = StoreLaunch.Supported;

	public event Action? GpuChanged;

	public event Action<GameState>? GameChanged;

	public event Action<QueueItem>? QueueChanged;

	public event Action? ScanProgressed;

	public event Action? NewsChanged;

	public string? BeforeAfterBlocked(string gameId)
	{
		GameState gameState = Find(gameId);
		GameRecord gameRecord = Store.LoadGame(gameId);
		bool flag;
		lock (_lock)
		{
			flag = _current == gameId;
		}
		bool flag2 = Frames11(gameState.Engine, Vendor.Caps);
		string text = (flag2 ? PairRecorderSkip(gameState, gameRecord) : gameState.RecorderSkip);
		if (!Settings.BeforeAfter)
		{
			return "Before and after recording is off in Settings";
		}
		string text2 = gameState.Engine?.GraphicsApi;
		string result;
		if (text2 == null || (!text2.Contains("D3D12") && !text2.StartsWith("D3D11")))
		{
			result = "Only for DirectX 12 and DirectX 11 games";
			goto IL_027e;
		}
		string text3;
		if (gameState.AnyAntiCheat)
		{
			text3 = "Not for games with anti-cheat";
			goto IL_027a;
		}
		string text4;
		if (gameState.NoStutter != null)
		{
			text4 = "Not for games without shader compilation stutter";
			goto IL_0276;
		}
		string text5;
		if (!(flag2 ? RecorderWanted(gameState.RecorderOverride, Settings.RecordAllGames, text, frames11: true) : gameState.RecorderEffective))
		{
			text5 = ((text != null) ? ("The recorder can't run in this game: " + text) : "Turn the recorder on for this game first");
			goto IL_0272;
		}
		string recorderRefused = gameState.RecorderRefused;
		string text6;
		if (recorderRefused != null && recorderRefused != "The game changed since the recorder was set up. It is set up again: play again." && !recorderRefused.StartsWith("The last launch ran "))
		{
			text6 = recorderRefused;
			goto IL_026e;
		}
		GameStatus status = gameState.Status;
		object obj;
		if ((uint)(status - 1) <= 3u)
		{
			if (!gameState.Playing && !GameRunning(gameState.Game))
			{
				if (!flag)
				{
					BeforeAfterRecord beforeAfter = gameRecord.BeforeAfter;
					if (beforeAfter == null || beforeAfter.Stage == BeforeAfterStage.Done)
					{
						if (AppCache != null)
						{
							HashSet<string> item = DriverKeys(gameState.Game, gameRecord).Keys;
							if (item != null && item.Count > 0)
							{
								List<(string, List<string>)> list = SharedWith(gameId, item);
								if (list != null && list.Count > 0)
								{
									obj = "Its driver cache is shared with " + string.Join(", ", list.Select(((string Name, List<string> Keys) x) => x.Name));
									goto IL_026c;
								}
							}
						}
						obj = null;
					}
					else
					{
						obj = "A before and after recording is already in progress";
					}
				}
				else
				{
					obj = "Wait for the compile to finish";
				}
			}
			else
			{
				obj = gameState.Game.Name + " is running";
			}
		}
		else
		{
			obj = gameState.StatusReason;
		}
		goto IL_026c;
		IL_026c:
		text6 = (string)obj;
		goto IL_026e;
		IL_027a:
		result = text3;
		goto IL_027e;
		IL_0276:
		text3 = text4;
		goto IL_027a;
		IL_0272:
		text4 = text5;
		goto IL_0276;
		IL_027e:
		return result;
		IL_026e:
		text5 = text6;
		goto IL_0272;
	}

	public void StartBeforeAfter(string gameId, int minutes, bool overlay = true)
	{
		string text = BeforeAfterBlocked(gameId);
		if (text != null)
		{
			throw new InvalidOperationException(text);
		}
		Game game = Find(gameId).Game;
		if (!Frames11(Find(gameId).Engine, Vendor.Caps))
		{
			BeforeAfterRecorderReady(gameId);
		}
		Remove(gameId);
		BeforeAfterRecord beforeAfter = Store.LoadGame(gameId).BeforeAfter;
		if (beforeAfter != null)
		{
			Unpin(gameId, beforeAfter);
		}
		lock (_setAsideLock)
		{
			(HashSet<string>? Survivors, bool Gap) tuple = ClearBeforeRun(gameId);
			HashSet<string> item = tuple.Survivors;
			bool item2 = tuple.Gap;
			GameRecord gameRecord = Store.LoadGame(gameId);
			gameRecord.BeforeAfter = new BeforeAfterRecord
			{
				Stage = BeforeAfterStage.BeforeArmed,
				Minutes = minutes,
				ArmedAt = Clock(),
				DriverId = DriverId,
				Build = BuildNow(game),
				Overlay = overlay,
				Survivors = item,
				ClearGap = item2
			};
			_played.TryRemove(gameId, out var _);
			Changed(game, gameRecord);
		}
	}

	public void BeforeAfterRecorderReady(string gameId)
	{
		if (!ManageRecorders)
		{
			return;
		}
		_pairInstall[gameId] = 0;
		try
		{
			ReconcileRecorders(gameId);
		}
		finally
		{
			_pairInstall.TryRemove(gameId, out var _);
		}
		GameState gameState = Find(gameId);
		GameRecord gameRecord = Store.LoadGame(gameId);
		string directoryName = Path.GetDirectoryName(gameState.Game.ExePath);
		string path = directoryName;
		string path2;
		if (gameState.AgsHost)
		{
			path2 = "amd_ags_x64.dll";
		}
		else
		{
			path2 = (As11(gameState.Engine, gameRecord, Vendor.Caps) ? "d3d11.dll" : "d3d12.dll");
		}
		string text = Path.Combine(path, path2);
		bool flag;
		bool flag2;
		bool flag3;
		try
		{
			flag = IsOurProxy(text);
			int num;
			if (flag)
			{
				string text2 = ProxySha(Path.GetFileName(text));
				num = ((text2 == null || Sha256(text) == text2) ? 1 : 0);
			}
			else
			{
				num = 0;
			}
			flag2 = (byte)num != 0;
			string text3 = RunIni(gameState.Game, gameRecord.BeforeAfter);
			flag3 = text3.Length == 0 || (File.Exists(Path.Combine(directoryName, "scskiller.ini")) && File.ReadAllText(Path.Combine(directoryName, "scskiller.ini")).Contains(text3, StringComparison.Ordinal));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			flag = (flag2 = (flag3 = false));
		}
		if ((flag & flag2 & flag3) && Attested(gameState.Game))
		{
			return;
		}
		throw new InvalidOperationException("The recorder isn't ready, so the run wouldn't be measured: " + (gameState.RecorderSkip ?? ((gameState.RecorderNote == "records nothing: the scskiller.ini in its folder isn't SCSKiller's, so it's left as it is") ? "its scskiller.ini isn't SCSKiller's" : gameState.RecorderNote) ?? _unstamped.GetValueOrDefault(gameId) ?? ((!gameState.RecorderEffective) ? "it's off for this game" : ((flag && !flag2) ? "another SCSKiller's recorder is in the game and couldn't be replaced" : ((flag && !flag3) ? "its scskiller.ini isn't SCSKiller's" : (flag ? "the game's folder is still being checked, try again in a moment" : "it couldn't be installed"))))));
	}

	public void ContinueBeforeAfter(string gameId)
	{
		GameState gameState = Find(gameId);
		GameRecord gameRecord = Store.LoadGame(gameId);
		BeforeAfterRecord beforeAfter = gameRecord.BeforeAfter;
		BeforeAfterRecord beforeAfterRecord2;
		BeforeAfterRecord beforeAfterRecord;
		DateTimeOffset armedAt;
		if (beforeAfter != null && beforeAfter.Stage == BeforeAfterStage.BeforeArmed && beforeAfter.ClearPending)
		{
			Idle(gameState.Game);
			lock (_setAsideLock)
			{
				try
				{
					beforeAfterRecord = beforeAfter;
					(HashSet<string>, bool) tuple = ClearBeforeRun(gameId);
					bool? clearGap = tuple.Item2;
					beforeAfterRecord.Survivors = tuple.Item1;
					beforeAfter.ClearGap = clearGap;
				}
				catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
					throw new InvalidOperationException("Couldn't clear the cache: " + ex.Message, ex);
				}
				beforeAfterRecord = beforeAfter;
				beforeAfterRecord2 = beforeAfter;
				armedAt = Clock();
				beforeAfterRecord.ArmedAt = armedAt;
				beforeAfterRecord2.ClearPending = false;
				beforeAfter.Problem = null;
				Changed(gameState.Game, gameRecord);
				return;
			}
		}
		BeforeAfterRecord beforeAfter2 = gameRecord.BeforeAfter;
		bool flag;
		if (beforeAfter2 != null)
		{
			BeforeAfterStage stage = beforeAfter2.Stage;
			if ((uint)(stage - 2) <= 1u)
			{
				flag = true;
				goto IL_013b;
			}
		}
		flag = false;
		goto IL_013b;
		IL_013b:
		if (!flag)
		{
			throw new InvalidOperationException("There's no after recording to continue");
		}
		string text = Voided(gameState.Game, beforeAfter2);
		if (text != null)
		{
			throw new InvalidOperationException(text);
		}
		Idle(gameState.Game);
		beforeAfter2.Dirty |= LaunchesAfter(gameState.Game, gameRecord, beforeAfter2.ArmedAt).After.Count > 0;
		if (beforeAfter2.Stage == BeforeAfterStage.Compiling || beforeAfter2.Dirty || gameState.Status != GameStatus.Warmed)
		{
			Recompile(gameState.Game, gameRecord, null);
			return;
		}
		beforeAfterRecord2 = beforeAfter2;
		beforeAfterRecord = beforeAfter2;
		armedAt = Clock();
		beforeAfter2.Stage = BeforeAfterStage.AfterArmed;
		beforeAfterRecord2.ArmedAt = armedAt;
		beforeAfterRecord.Problem = null;
		Changed(gameState.Game, gameRecord);
	}

	public void RecordAfterAgain(string gameId)
	{
		Game game = Find(gameId).Game;
		GameRecord gameRecord = Store.LoadGame(gameId);
		BeforeAfterRecord beforeAfter = gameRecord.BeforeAfter;
		if (beforeAfter == null || beforeAfter.Stage != BeforeAfterStage.Done)
		{
			throw new InvalidOperationException("There's no finished before and after recording");
		}
		string text = Voided(game, beforeAfter);
		if (text != null)
		{
			throw new InvalidOperationException(text);
		}
		Idle(game);
		long? after = beforeAfter.After;
		if (after.HasValue)
		{
			long valueOrDefault = after.GetValueOrDefault();
			Snapshots.Pin(Store, gameId, valueOrDefault, null);
		}
		beforeAfter.After = null;
		Recompile(game, gameRecord, null);
	}

	public void EndBeforeAfter(string gameId, bool compile)
	{
		Game game = Find(gameId).Game;
		GameRecord gameRecord = Store.LoadGame(gameId);
		BeforeAfterRecord beforeAfter = gameRecord.BeforeAfter;
		if (beforeAfter != null)
		{
			Unpin(gameId, beforeAfter);
		}
		gameRecord.BeforeAfter = null;
		_played.TryRemove(gameId, out var _);
		Changed(game, gameRecord);
		if (compile)
		{
			Compile(gameId);
		}
	}

	private void EndPair(Game g, GameRecord? installing)
	{
		BeforeAfterRecord ended = null;
		try
		{
			PairChange(g.Id, (GameRecord r, BeforeAfterRecord ba) =>
			{
				if (ba.Stage == BeforeAfterStage.Done)
				{
					return false;
				}
				ended = ba;
				r.BeforeAfter = null;
				return true;
			});
			if (ended != null)
			{
				Unpin(g.Id, ended);
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			RecorderLog(g.Name + ": couldn't end its before and after recording: " + ex.Message);
		}
		BeforeAfterRecord beforeAfterRecord = installing?.BeforeAfter;
		if (beforeAfterRecord != null && beforeAfterRecord.Stage != BeforeAfterStage.Done)
		{
			installing.BeforeAfter = null;
		}
		_played.TryRemove(g.Id, out var _);
		lock (_lock)
		{
			int num = _games.FindIndex((GameState x) => x.Game.Id == g.Id);
			if (num >= 0)
			{
				BeforeAfterInfo beforeAfter = _games[num].BeforeAfter;
				if ((object)beforeAfter != null && beforeAfter.Stage != BeforeAfterStage.Done)
				{
					_games[num] = _games[num]with
					{
						BeforeAfter = null
					};
				}
			}
		}
	}

	public BeforeAfterResult? BeforeAfterReport(string gameId)
	{
		BeforeAfterRecord beforeAfter = Store.LoadGame(gameId).BeforeAfter;
		if (beforeAfter != null && beforeAfter.Stage == BeforeAfterStage.Done)
		{
			long? before = beforeAfter.Before;
			if (before.HasValue)
			{
				long valueOrDefault = before.GetValueOrDefault();
				long? after = beforeAfter.After;
				if (after.HasValue)
				{
					long valueOrDefault2 = after.GetValueOrDefault();
					SessionSnapshot? sessionSnapshot = Snapshots.Read(Store, gameId, valueOrDefault);
					SessionSnapshot sessionSnapshot2 = Snapshots.Read(Store, gameId, valueOrDefault2);
					SessionSnapshot sessionSnapshot3 = sessionSnapshot;
					if (!(sessionSnapshot3 == null) && !(sessionSnapshot2 == null))
					{
						return new BeforeAfterResult(sessionSnapshot3, sessionSnapshot2, BeforeAfter.Compare(sessionSnapshot3, sessionSnapshot2, AppCache is AmdAppCache && sessionSnapshot3.D3D11 && beforeAfter.ClearGap != false, AppCache is AmdAppCache && sessionSnapshot3.D3D11 && beforeAfter.DxShared));
					}
					return null;
				}
			}
		}
		return null;
	}

	private void DeleteSessionSnapshots(string gameId)
	{
		try
		{
			Directory.Delete(Path.Combine(Store.GameDir(gameId), "sessions"), recursive: true);
		}
		catch (DirectoryNotFoundException)
		{
		}
		catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
		{
			Log?.Report(gameId + ": couldn't delete its session snapshots: " + ex2.Message);
		}
	}

	private string? PairHoldsCache(string gameId, bool compiling = false)
	{
		switch (Store.LoadGame(gameId).BeforeAfter?.Stage)
		{
		case BeforeAfterStage.BeforeArmed:
		case BeforeAfterStage.BeforePlaying:
			if (compiling)
			{
				return "Its before recording is still to come, end the before and after recording to compile now";
			}
			break;
		case BeforeAfterStage.Compiling:
		case BeforeAfterStage.AfterReady:
		case BeforeAfterStage.AfterArmed:
		case BeforeAfterStage.AfterPlaying:
			if (!compiling)
			{
				return "Its before and after recording needs this cache, end it first";
			}
			break;
		}
		return null;
	}

	private void Idle(Game g)
	{
		if (IsPlaying(g.Id) || GameRunning(g))
		{
			throw new InvalidOperationException(g.Name + " is running");
		}
		QueueItem queueItem = QueueItemOf(g.Id);
		if ((object)queueItem != null && !Finished(queueItem.Stage))
		{
			throw new InvalidOperationException("Wait for the compile to finish");
		}
	}

	private void Unpin(string gameId, BeforeAfterRecord ba)
	{
		foreach (long item in new long?[2] { ba.Before, ba.After }.OfType<long>())
		{
			Snapshots.Pin(Store, gameId, item, null);
		}
	}

	private QueueItem? QueueItemOf(string gameId)
	{
		lock (_lock)
		{
			return _queue.FirstOrDefault((QueueItem q) => q.GameId == gameId);
		}
	}

	private static string BuildNow(Game g)
	{
		return g.Version ?? ExeStamp(g);
	}

	private string? Voided(Game g, BeforeAfterRecord ba)
	{
		return IdentityChange(ba, DriverId, BuildNow(g));
	}

	private static string? IdentityChange(BeforeAfterRecord ba, string? driverId, string? build)
	{
		if (ba.DriverId == null || driverId == null || !(ba.DriverId != driverId))
		{
			if (ba.Build == null || build == null || !(ba.Build != build))
			{
				return null;
			}
			return "The game was updated since the before recording, start over";
		}
		return "The driver changed since the before recording, start over";
	}

	private void Changed(Game g, GameRecord rec)
	{
		Store.SaveGame(g.Id, rec);
		UpdateRecorderIni(g, rec);
		Refresh(g);
		if (ManageRecorders && Frames11(Games.FirstOrDefault((GameState s) => s.Game.Id == g.Id)?.Engine, Vendor.Caps))
		{
			try
			{
				ReconcileRecorders(g.Id);
			}
			catch (Exception ex)
			{
				Log?.Report(g.Name + ": reconciling the recorder failed: " + ex.Message);
			}
		}
	}

	private void Recompile(Game g, GameRecord rec, string? problem)
	{
		BeforeAfterRecord beforeAfter = rec.BeforeAfter;
		BeforeAfterRecord beforeAfterRecord = beforeAfter;
		BeforeAfterRecord beforeAfterRecord2 = beforeAfter;
		BeforeAfterRecord beforeAfterRecord3 = beforeAfter;
		DateTimeOffset armedAt = Clock();
		beforeAfterRecord.Stage = BeforeAfterStage.Compiling;
		beforeAfterRecord2.Dirty = false;
		beforeAfterRecord3.ArmedAt = armedAt;
		beforeAfter.Problem = problem;
		try
		{
			ClearCache(g.Id, gamePrecache: true);
		}
		catch (Exception ex) when ((ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			beforeAfter.Problem = "Couldn't clear the cache: " + ex.Message;
			QueueItem queueItem = QueueItemOf(g.Id);
			if ((object)queueItem != null && Finished(queueItem.Stage))
			{
				Remove(g.Id);
			}
			Changed(g, rec);
			return;
		}
		Compile(g.Id, pair: true);
		MoveInQueue(g.Id, 0);
		Changed(g, rec);
	}

	private FrameReport? FramesNow(Game g, GameRecord rec, double? playCapMs = null)
	{
		return Frames(SessionDir(g), Path.GetFileName(g.ExePath), (Vendor.Vendor == GpuVendor.Nvidia) ? SessionLog.ReadRayQueryKeys(RayQueryKeysPath(g.Id)) : null, rec.LastPlay, playCapMs);
	}

	private (List<long> After, long Last) LaunchesAfter(Game g, GameRecord rec, DateTimeOffset since)
	{
		long from = since.ToUnixTimeMilliseconds();
		long num = FramesNow(g, rec)?.LaunchUnixMs ?? 0;
		string path = Path.Combine(SessionDir(g), "scskiller_creates.csv");
		IEnumerable<long> enumerable2;
		if (!File.Exists(path))
		{
			IEnumerable<long> enumerable = Array.Empty<long>();
			enumerable2 = enumerable;
		}
		else
		{
			enumerable2 = from l in SessionLog.Launches(path, 0L)
				where l.StartT.HasValue && !SessionLog.OtherExe(l.Exe, Path.GetFileName(g.ExePath))
				select l.Start.GetValueOrDefault();
		}
		IEnumerable<long> second = enumerable2;
		return (After: (from t in (from s in Snapshots.List(Store, g.Id)
				select s.LaunchUnixMs).Concat(second).Append(num)
			where t > @from
			select t).Distinct().Order().ToList(), Last: num);
	}

	private static double? PairCapMs(GameRecord rec, FrameReport frames)
	{
		BeforeAfterRecord beforeAfter = rec.BeforeAfter;
		bool flag = beforeAfter != null;
		if (flag)
		{
			bool flag2 = frames.LaunchUnixMs == beforeAfter.Before || frames.LaunchUnixMs == beforeAfter.After;
			if (!flag2)
			{
				BeforeAfterStage stage = beforeAfter.Stage;
				bool flag3 = (((uint)stage <= 1u || (uint)(stage - 4) <= 1u) ? true : false);
				flag2 = flag3 && frames.LaunchUnixMs > beforeAfter.ArmedAt.ToUnixTimeMilliseconds();
			}
			flag = flag2;
		}
		if (!flag)
		{
			return null;
		}
		return (double)beforeAfter.Minutes * 60000.0;
	}

	private (SessionSnapshot? Run, string? Lost) RunOf(Game g, GameRecord rec, BeforeAfterRecord ba)
	{
		var (list, num) = LaunchesAfter(g, rec, ba.ArmedAt);
		if (list.Count == 0)
		{
			return (Run: null, Lost: null);
		}
		if (num <= list[0])
		{
			return (Run: Snapshots.Read(Store, g.Id, list[0]), Lost: null);
		}
		return (Run: null, Lost: "The game started again before SCSKiller read the run, so it's lost");
	}

	private BeforeAfterInfo? BeforeAfterStarted(string gameId)
	{
		BeforeAfterInfo shown = null;
		PairChange(gameId, (GameRecord r, BeforeAfterRecord ba) =>
		{
			switch (ba.Stage)
			{
			case BeforeAfterStage.BeforeArmed:
			case BeforeAfterStage.AfterArmed:
				if (!ba.ClearPending)
				{
					BeforeAfterStage stage = ((ba.Stage == BeforeAfterStage.BeforeArmed) ? BeforeAfterStage.BeforePlaying : BeforeAfterStage.AfterPlaying);
					ba.Stage = stage;
					ba.Problem = null;
					_played.TryRemove(gameId, out var _);
					shown = new BeforeAfterInfo(ba.Stage, ba.Minutes, null, 0.0);
					return true;
				}
				break;
			case BeforeAfterStage.Compiling:
			case BeforeAfterStage.AfterReady:
				if (!ba.Dirty)
				{
					return ba.Dirty = true;
				}
				break;
			}
			return false;
		});
		return shown;
	}

	private GameRecord? PairChange(string gameId, Func<GameRecord, BeforeAfterRecord, bool> change)
	{
		GameRecord rec = null;
		AppStore.Locked(Path.Combine(Store.GameDir(gameId), "state.json"), () =>
		{
			rec = Store.LoadGame(gameId);
			BeforeAfterRecord beforeAfter = rec.BeforeAfter;
			if (beforeAfter != null && change(rec, beforeAfter))
			{
				Store.SaveGame(gameId, rec);
			}
		});
		return rec;
	}

	private void BeforeAfterTick(IReadOnlyList<GameState> games, HashSet<string> running, DateTimeOffset at)
	{
		foreach (GameState game in games)
		{
			string id = game.Game.Id;
			BeforeAfterInfo beforeAfter = game.BeforeAfter;
			bool flag;
			BeforeAfterStage stage;
			if ((object)beforeAfter != null)
			{
				stage = beforeAfter.Stage;
				if ((uint)stage <= 1u || (uint)(stage - 4) <= 1u)
				{
					flag = true;
					goto IL_006e;
				}
			}
			flag = false;
			goto IL_006e;
			IL_006e:
			if (!flag || (_pairPolled.TryGetValue(id, out var value) && at - value < PlayReadEvery))
			{
				continue;
			}
			_pairPolled[id] = at;
			GameRecord gameRecord = PairChange(id, (GameRecord r, BeforeAfterRecord ba) =>
			{
				BeforeAfterStage stage2 = ba.Stage;
				bool flag2 = ((stage2 == BeforeAfterStage.BeforeArmed || stage2 == BeforeAfterStage.AfterArmed) ? true : false);
				if (!flag2 || ba.ClearPending || running.Contains(id) || ba.Problem == "Didn't see the game start" || at - ba.ArmedAt < NoStartAfter)
				{
					return false;
				}
				ba.Problem = "Didn't see the game start";
				return true;
			});
			BeforeAfterRecord beforeAfterRecord = gameRecord?.BeforeAfter;
			if (beforeAfterRecord == null)
			{
				continue;
			}
			stage = beforeAfterRecord.Stage;
			if ((stage == BeforeAfterStage.BeforeArmed || stage == BeforeAfterStage.AfterArmed) ? true : false)
			{
				if (beforeAfterRecord.Problem == "Didn't see the game start" && beforeAfter.Problem != "Didn't see the game start")
				{
					ShowPair(id, beforeAfter with
					{
						Stage = beforeAfterRecord.Stage,
						Problem = "Didn't see the game start"
					});
				}
			}
			else if (running.Contains(id) && beforeAfter.Problem != "Frame times stopped when frame generation started, so this run can't be measured. Turn frame generation off for the before and after recording" && FramesStopped(game.Game, beforeAfterRecord))
			{
				FramesStoppedWhilePlaying(game.Game, beforeAfter);
			}
			else if (running.Contains(id))
			{
				FrameReport frameReport = FramesNow(game.Game, gameRecord, (double)beforeAfterRecord.Minutes * 60000.0);
				if ((object)frameReport != null && frameReport.LaunchUnixMs > beforeAfterRecord.ArmedAt.ToUnixTimeMilliseconds() && frameReport.PlaySeconds != beforeAfter.PlayedSeconds)
				{
					_played[id] = frameReport.PlaySeconds;
					ShowPair(id, beforeAfter with
					{
						PlayedSeconds = frameReport.PlaySeconds
					});
				}
			}
		}
	}

	private bool FramesStopped(Game g, BeforeAfterRecord ba)
	{
		string path = Path.Combine(SessionDir(g), "scskiller_creates.csv");
		long from = ba.ArmedAt.ToUnixTimeMilliseconds();
		if (File.Exists(path))
		{
			return SessionLog.Launches(path, SessionLog.LastSessionAt(path)).Any((SessionLog.CsvLaunch l) => l.FramesStopped && l.Start > from);
		}
		return false;
	}

	private void FramesStoppedWhilePlaying(Game g, BeforeAfterInfo shown)
	{
		bool changed = false;
		PairChange(g.Id, (GameRecord r, BeforeAfterRecord ba) =>
		{
			BeforeAfterStage stage = ba.Stage;
			bool flag = ((stage == BeforeAfterStage.BeforePlaying || stage == BeforeAfterStage.AfterPlaying) ? true : false);
			if (!flag || ba.Problem == "Frame times stopped when frame generation started, so this run can't be measured. Turn frame generation off for the before and after recording")
			{
				return false;
			}
			ba.Problem = "Frame times stopped when frame generation started, so this run can't be measured. Turn frame generation off for the before and after recording";
			return changed = true;
		});
		if (changed)
		{
			Log?.Report(g.Name + ": frame times stopped when frame generation started, the before and after run can't be measured");
			ShowPair(g.Id, shown with
			{
				Problem = "Frame times stopped when frame generation started, so this run can't be measured. Turn frame generation off for the before and after recording"
			});
		}
	}

	private void ShowPair(string gameId, BeforeAfterInfo info)
	{
		GameState gameState = null;
		lock (_lock)
		{
			int num = _games.FindIndex((GameState x) => x.Game.Id == gameId);
			if (num >= 0)
			{
				List<GameState> games = _games;
				GameState gameState2 = _games[num]with
				{
					BeforeAfter = info
				};
				gameState = gameState2;
				games[num] = gameState2;
			}
		}
		if (gameState != null)
		{
			GameChanged?.Invoke(gameState);
		}
	}

	private void BeforeAfterExited(Game g, bool watched = true)
	{
		try
		{
			bool flag = true;
			if (_learning.TryRemove(g.Id, out Task value))
			{
				try
				{
					flag = value.Wait(LearnWait);
				}
				catch (AggregateException)
				{
					flag = false;
				}
			}
			GameRecord gameRecord = Store.LoadGame(g.Id);
			BeforeAfterRecord beforeAfter = gameRecord.BeforeAfter;
			BeforeAfterStage stage;
			bool flag2;
			if (beforeAfter != null && !beforeAfter.ClearPending)
			{
				stage = beforeAfter.Stage;
				if ((uint)stage <= 1u || (uint)(stage - 4) <= 1u)
				{
					flag2 = true;
					goto IL_006a;
				}
			}
			flag2 = false;
			goto IL_006a;
			IL_006a:
			if (!flag2)
			{
				return;
			}
			_played.TryRemove(g.Id, out var _);
			stage = beforeAfter.Stage;
			flag2 = (uint)stage <= 1u;
			bool flag3 = flag2;
			var (sessionSnapshot, text) = RunOf(g, gameRecord, beforeAfter);
			if (text == null && FramesStopped(g, beforeAfter))
			{
				text = "Frame times stopped when frame generation started, so this run can't be measured. Turn frame generation off for the before and after recording";
			}
			if (text == null)
			{
				text = BeforeAfter.RunProblem(sessionSnapshot, flag3);
			}
			string text2 = (flag3 ? EndSetAside(g, gameRecord, beforeAfter, sessionSnapshot != null && text == null, (!(watched & flag)) ? ((long?)null) : sessionSnapshot?.LaunchUnixMs) : null);
			if (flag3 && sessionSnapshot != null && OldCache(gameRecord, beforeAfter))
			{
				text = "Old driver cache files were in use, play the before run again";
			}
			else if (flag3 && sessionSnapshot != null && (!watched || !flag) && beforeAfter.ClearGap != false && text == null)
			{
				text = "SCSKiller wasn't running during the before run, so it couldn't check the driver cache: play it again";
			}
			string? obj = sessionSnapshot?.DriverId ?? DriverId;
			string text3 = sessionSnapshot?.Build ?? BuildNow(g);
			string text4 = obj;
			BeforeAfterRecord beforeAfterRecord2;
			string problem;
			if (!flag3)
			{
				string text5 = IdentityChange(beforeAfter, text4, text3) ?? Voided(g, beforeAfter);
				if (text5 != null)
				{
					BeforeAfterRecord beforeAfterRecord = beforeAfter;
					problem = text5;
					beforeAfter.Stage = BeforeAfterStage.AfterReady;
					beforeAfterRecord.Problem = problem;
				}
				else
				{
					if (text != null)
					{
						Recompile(g, gameRecord, text);
						return;
					}
					Snapshots.Pin(Store, g.Id, sessionSnapshot.LaunchUnixMs, "after");
					BeforeAfterRecord beforeAfterRecord = beforeAfter;
					beforeAfterRecord2 = beforeAfter;
					long? after = sessionSnapshot.LaunchUnixMs;
					beforeAfter.After = after;
					beforeAfterRecord.Stage = BeforeAfterStage.Done;
					beforeAfterRecord2.Problem = null;
				}
				Changed(g, gameRecord);
				return;
			}
			if (text == null && !watched)
			{
				string text6 = IdentityChange(beforeAfter, text4, text3);
				if (text6 != null)
				{
					text = ((text6 == "The driver changed since the before recording, start over") ? "The driver was updated, play the before run again" : "The game was updated, play the before run again");
				}
			}
			beforeAfterRecord2 = beforeAfter;
			problem = text4;
			string build = text3;
			beforeAfter.DriverId = problem;
			beforeAfterRecord2.Build = build;
			if (text != null)
			{
				Rearm(g, gameRecord, text2 ?? text);
				return;
			}
			Snapshots.Pin(Store, g.Id, sessionSnapshot.LaunchUnixMs, "before");
			beforeAfter.Before = sessionSnapshot.LaunchUnixMs;
			Recompile(g, gameRecord, text2);
		}
		catch (Exception ex2)
		{
			Log?.Report(g.Name + ": before and after recording: " + ex2.Message);
		}
	}

	private HashSet<string>? Survivors()
	{
		return AppCache?.AllKeys().ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	private DxQuarantine? SetAside()
	{
		if (!(AppCache is AmdAppCache amdAppCache))
		{
			return null;
		}
		return new DxQuarantine(amdAppCache.DxDir, Path.Combine(Store.DataDir, "dxcache-set-aside.json"));
	}

	private (HashSet<string>? Survivors, bool Gap) ClearBeforeRun(string gameId)
	{
		ClearCache(gameId, gamePrecache: true);
		GameState gameState = Find(gameId);
		GameRecord rec = Store.LoadGame(gameId);
		bool flag = ClearGap(gameState, rec);
		if (flag && Dx11Only(gameState))
		{
			DxQuarantine dxQuarantine = SetAside();
			if (dxQuarantine != null)
			{
				lock (_setAsideLock)
				{
					DxQuarantine.Journal pending = dxQuarantine.Pending;
					if ((object)pending != null && pending.GameId == gameId)
					{
						List<string> list = dxQuarantine.Restore();
						if (list != null && list.Count > 0)
						{
							throw new IOException(F("Couldn't put back {0} of AMD's DirectX 11 cache files: they're in {1}", list.Count, dxQuarantine.Folder));
						}
					}
					if (dxQuarantine.Pending == null)
					{
						HashSet<string> hashSet = Games.Where((GameState o) => o.Game.Id != gameId).SelectMany((GameState o) => Store.LoadGame(o.Game.Id).CacheKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
						DxQuarantine.Journal journal = dxQuarantine.SetAside(gameId, hashSet.Contains);
						Log?.Report($"{gameState.Game.Name}: {journal.Moves.Count} DirectX 11 cache files of other apps set aside for the before run" + ((journal.Skipped.Count > 0) ? $", {journal.Skipped.Count} left in use" : ""));
						flag = false;
					}
				}
			}
		}
		return (Survivors: Survivors(), Gap: flag);
	}

	private static bool Held(IReadOnlyList<FileInfo> files)
	{
		if (files.Any((FileInfo f) => AppCacheFiles.ProcessesUsing(f.FullName).Count > 0))
		{
			return true;
		}
		List<FileStream> list = new List<FileStream>();
		try
		{
			foreach (FileInfo file in files)
			{
				list.Add(new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None));
			}
			return false;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return true;
		}
		finally
		{
			foreach (FileStream item in list)
			{
				item.Dispose();
			}
		}
	}

	private static bool Dx11Only(GameState s)
	{
		string text = s.Engine?.GraphicsApi;
		if (text != null && text.StartsWith("D3D11"))
		{
			return !text.Contains("D3D12");
		}
		return false;
	}

	private static string F(string format, params object[] args)
	{
		return string.Format(CultureInfo.InvariantCulture, format, args);
	}

	private string? EndSetAside(Game g, GameRecord rec, BeforeAfterRecord ba, bool learn, long? launchUnixMs)
	{
		DxQuarantine dxQuarantine = SetAside();
		if (dxQuarantine == null)
		{
			return null;
		}
		DxQuarantine.Journal j;
		lock (_setAsideLock)
		{
			j = dxQuarantine.Pending;
			if ((object)j == null || j.GameId != g.Id)
			{
				return null;
			}
			string text = null;
			string exe = Path.GetFileName(g.ExePath);
			bool flag = Games.Any((GameState o) => o.Game.Id != g.Id && Path.GetFileName(o.Game.ExePath).Equals(exe, StringComparison.OrdinalIgnoreCase));
			try
			{
				HashSet<string> now = Survivors() ?? new HashSet<string>();
				List<string> list = now.Where((string k) => k.StartsWith("dx:", StringComparison.Ordinal) && New(k)).ToList();
				List<string> list2 = list.Where(rec.GameKeys.Contains).ToList();
				AmdAppCache amd = (AmdAppCache)AppCache;
				DateTime dateTime;
				if (launchUnixMs.HasValue)
				{
					long valueOrDefault = launchUnixMs.GetValueOrDefault();
					DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(valueOrDefault);
					PlayWindow lastPlay = rec.LastPlay;
					dateTime = (((object)lastPlay != null && lastPlay.From <= dateTimeOffset && dateTimeOffset <= lastPlay.To) ? lastPlay.From : dateTimeOffset).UtcDateTime - LaunchFileMargin;
				}
				else
				{
					dateTime = DateTime.MaxValue;
				}
				DateTime since = dateTime;
				Dictionary<string, IReadOnlyList<FileInfo>> files = list.ToDictionary((string k) => k, (string k) => amd.FilesOf(new _003C_003Ez__ReadOnlySingleElementList<string>(k)));
				if (list2.Count == 0 && !flag && list.All((string k) => files[k].Count > 0 && !files[k].Any(Tunneled)))
				{
					List<string> list3 = list.Where((string k) => files[k].All((FileInfo f) => f.CreationTimeUtc >= since)).ToList();
					if (list3 != null && list3.Count == 1)
					{
						string text2 = list3[0];
						if (!now.Any((string k) => k.StartsWith("dxc:", StringComparison.Ordinal) && New(k) && (rec.GameKeys.Contains(k) || k == AmdAppCache.HintKey(WarmExeName(g, rec)))) && !Held(files[text2]))
						{
							int num = 1;
							List<string> list4 = new List<string>(num);
							CollectionsMarshal.SetCount(list4, num);
							CollectionsMarshal.AsSpan(list4)[0] = text2;
							list2 = list4;
						}
					}
				}
				ba.DxShared = learn && list.Count > 0 && list2.Count != 1;
				if (learn && list2.Count == 1)
				{
					GameRecord gameRecord = rec;
					GameRecord gameRecord2 = rec;
					HashSet<string> hashSet = new HashSet<string>();
					foreach (string cacheKey in rec.CacheKeys)
					{
						hashSet.Add(cacheKey);
					}
					hashSet.Add(list2[0]);
					HashSet<string> cacheKeys = hashSet;
					HashSet<string> hashSet2 = new HashSet<string>();
					foreach (string gameKey in rec.GameKeys)
					{
						hashSet2.Add(gameKey);
					}
					hashSet2.Add(list2[0]);
					HashSet<string> gameKeys = hashSet2;
					gameRecord.CacheKeys = cacheKeys;
					gameRecord2.GameKeys = gameKeys;
					Store.SaveGame(g.Id, rec);
					text = list2[0];
					Log?.Report(g.Name + ": its DirectX 11 cache key " + text + " learned from the before run");
				}
				else if (ba.DxShared)
				{
					Log?.Report($"{g.Name}: {list.Count} DirectX 11 cache keys made during the before run, none taken as the game's");
				}
				bool New(string k)
				{
					HashSet<string>? survivors = ba.Survivors;
					if (survivors == null || !survivors.Contains(k))
					{
						return now.Contains(k);
					}
					return false;
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				Log?.Report(g.Name + ": the DirectX 11 cache key wasn't learned: " + ex.Message);
			}
			List<string> list5 = dxQuarantine.Restore(flag ? null : text);
			if (list5.Count == 0)
			{
				return null;
			}
			Log?.Report($"{g.Name}: {list5.Count} DirectX 11 cache files couldn't be put back from {dxQuarantine.Folder}");
			return F("Couldn't put back {0} of AMD's DirectX 11 cache files: they're in {1}", list5.Count, dxQuarantine.Folder);
		}
		bool Tunneled(FileInfo f)
		{
			return j.Moves.Any((DxQuarantine.Move m) => m.From.Equals(f.FullName, StringComparison.OrdinalIgnoreCase) && File.Exists(m.To) && File.GetCreationTimeUtc(m.To) == f.CreationTimeUtc);
		}
	}

	private void PutBackSetAside()
	{
		DxQuarantine dxQuarantine = SetAside();
		if (dxQuarantine == null)
		{
			return;
		}
		lock (_setAsideLock)
		{
			DxQuarantine.Journal j = dxQuarantine.Pending;
			if ((object)j == null)
			{
				return;
			}
			GameState gameState = Games.FirstOrDefault((GameState s) => s.Game.Id == j.GameId);
			BeforeAfterRecord beforeAfterRecord = ((gameState == null) ? null : Store.LoadGame(j.GameId).BeforeAfter);
			if ((beforeAfterRecord != null && beforeAfterRecord.Stage == BeforeAfterStage.BeforeArmed && !beforeAfterRecord.ClearPending) || (beforeAfterRecord != null && beforeAfterRecord.Stage == BeforeAfterStage.BeforePlaying && IsPlaying(j.GameId)))
			{
				return;
			}
			List<string> list = dxQuarantine.Restore();
			if (list != null && list.Count > 0)
			{
				Log?.Report($"{list.Count} DirectX 11 cache files couldn't be put back from {dxQuarantine.Folder}");
				if (gameState != null && beforeAfterRecord != null)
				{
					ShowPair(gameState.Game.Id, new BeforeAfterInfo(beforeAfterRecord.Stage, beforeAfterRecord.Minutes, F("Couldn't put back {0} of AMD's DirectX 11 cache files: they're in {1}", list.Count, dxQuarantine.Folder), _played.GetValueOrDefault(gameState.Game.Id)));
				}
			}
		}
	}

	private bool ClearGap(GameState s, GameRecord rec)
	{
		if (AppCache is AmdAppCache)
		{
			string text = s.Engine?.GraphicsApi;
			if (text != null && text.StartsWith("D3D11") && !text.Contains("D3D12"))
			{
				return !rec.CacheKeys.Any((string k) => k.StartsWith("dx:", StringComparison.Ordinal));
			}
		}
		return DriverKeys(s.Game, rec).Gap != null;
	}

	private bool OldCache(GameRecord rec, BeforeAfterRecord ba)
	{
		HashSet<string> old = ba.Survivors;
		if (old == null)
		{
			return AppCache != null;
		}
		return rec.GameKeys.Any((string k) => old.Contains(k, StringComparer.OrdinalIgnoreCase));
	}

	private void OldCacheWhilePlaying(Game g)
	{
		BeforeAfterInfo shown = null;
		PairChange(g.Id, (GameRecord r, BeforeAfterRecord ba) =>
		{
			if (ba == null || ba.Stage != BeforeAfterStage.BeforePlaying || ba.Problem == "Old driver cache files were in use: close the game and play the before run again" || !OldCache(r, ba))
			{
				return false;
			}
			ba.Problem = "Old driver cache files were in use: close the game and play the before run again";
			shown = new BeforeAfterInfo(ba.Stage, ba.Minutes, "Old driver cache files were in use: close the game and play the before run again", _played.GetValueOrDefault(g.Id));
			return true;
		});
		if (shown != null)
		{
			Log?.Report(g.Name + ": the before run uses driver cache files from before its clear: it's played again");
			ShowPair(g.Id, shown);
		}
	}

	private void Rearm(Game g, GameRecord rec, string problem)
	{
		BeforeAfterRecord beforeAfter = rec.BeforeAfter;
		beforeAfter.Stage = BeforeAfterStage.BeforeArmed;
		lock (_setAsideLock)
		{
			try
			{
				(HashSet<string>? Survivors, bool Gap) tuple = ClearBeforeRun(g.Id);
				HashSet<string> item = tuple.Survivors;
				bool item2 = tuple.Gap;
				BeforeAfterRecord beforeAfterRecord = beforeAfter;
				BeforeAfterRecord beforeAfterRecord2 = beforeAfter;
				BeforeAfterRecord beforeAfterRecord3 = beforeAfter;
				BeforeAfterRecord beforeAfterRecord4 = beforeAfter;
				BeforeAfterRecord beforeAfterRecord5 = beforeAfter;
				DateTimeOffset armedAt = Clock();
				string problem2 = problem;
				HashSet<string> survivors = item;
				bool? clearGap = item2;
				beforeAfterRecord.ArmedAt = armedAt;
				beforeAfterRecord2.ClearPending = false;
				beforeAfterRecord3.Problem = problem2;
				beforeAfterRecord4.Survivors = survivors;
				beforeAfterRecord5.ClearGap = clearGap;
				beforeAfter.DxShared = false;
			}
			catch (Exception ex) when ((ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				BeforeAfterRecord beforeAfterRecord5 = beforeAfter;
				string problem2 = "Couldn't clear the cache: " + ex.Message;
				beforeAfterRecord5.ClearPending = true;
				beforeAfter.Problem = problem2;
			}
			Changed(g, rec);
		}
	}

	private void BeforeAfterCatchUp(IEnumerable<GameState> states)
	{
		foreach (GameState state in states)
		{
			try
			{
				GameRecord gameRecord = Store.LoadGame(state.Game.Id);
				bool flag = IsPlaying(state.Game.Id) || GameRunning(state.Game);
				BeforeAfterRecord beforeAfter = gameRecord.BeforeAfter;
				if (beforeAfter == null)
				{
					goto IL_0182;
				}
				if (!beforeAfter.ClearPending)
				{
					switch (beforeAfter.Stage)
					{
					case BeforeAfterStage.Compiling:
					case BeforeAfterStage.AfterReady:
						break;
					case BeforeAfterStage.BeforePlaying:
					case BeforeAfterStage.AfterPlaying:
						if (!flag)
						{
							goto IL_00e8;
						}
						goto IL_0182;
					case BeforeAfterStage.BeforeArmed:
					case BeforeAfterStage.AfterArmed:
						if (!flag && LaunchesAfter(state.Game, gameRecord, beforeAfter.ArmedAt).After.Count > 0)
						{
							goto IL_00e8;
						}
						goto IL_0182;
					default:
						goto IL_0182;
						IL_00e8:
						Refresh(state.Game);
						BeforeAfterExited(state.Game, watched: false);
						goto end_IL_0013;
					}
				}
				else
				{
					BeforeAfterStage stage = beforeAfter.Stage;
					if (stage != BeforeAfterStage.Compiling && stage != BeforeAfterStage.AfterReady)
					{
						goto IL_0182;
					}
				}
				if (beforeAfter.Dirty)
				{
					goto IL_0182;
				}
				BeforeAfterRecord beforeAfterRecord = beforeAfter;
				if (LaunchesAfter(state.Game, gameRecord, beforeAfterRecord.ArmedAt).After.Count <= 0)
				{
					goto IL_0182;
				}
				PairChange(state.Game.Id, (GameRecord _, BeforeAfterRecord now) =>
				{
					BeforeAfterStage stage2 = now.Stage;
					return (uint)(stage2 - 2) <= 1u && (now.Dirty = true);
				});
				Refresh(state.Game);
				goto end_IL_0013;
				IL_0182:
				if (beforeAfter?.Stage != state.BeforeAfter?.Stage)
				{
					Refresh(state.Game);
				}
				end_IL_0013:;
			}
			catch (Exception ex)
			{
				Log?.Report(state.Game.Name + ": before and after recording: " + ex.Message);
			}
		}
	}

	private void BeforeAfterQueued(QueueItem item)
	{
		if (!Finished(item.Stage))
		{
			return;
		}
		try
		{
			BeforeAfterRecord beforeAfter = Store.LoadGame(item.GameId).BeforeAfter;
			if (beforeAfter != null && beforeAfter.Stage == BeforeAfterStage.Compiling)
			{
				GameState gameState = Games.FirstOrDefault((GameState s) => s.Game.Id == item.GameId);
				if ((object)gameState != null)
				{
					Refresh(gameState.Game);
				}
			}
		}
		catch (Exception ex)
		{
			Log?.Report(item.GameId + ": before and after recording: " + ex.Message);
		}
	}

	private GameState BeforeAfterNow(GameState s, GameRecord evaluated)
	{
		if (evaluated.BeforeAfter == null)
		{
			return s;
		}
		bool moved = false;
		GameRecord gameRecord;
		try
		{
			gameRecord = PairChange(s.Game.Id, (GameRecord r, BeforeAfterRecord ba) =>
			{
				if (ba.Stage == BeforeAfterStage.Compiling)
				{
					(BeforeAfterStage, string) tuple = CompileEnded(s, r, ba);
					(BeforeAfterStage, string) tuple2 = tuple;
					BeforeAfterStage stage3 = ba.Stage;
					string problem2 = ba.Problem;
					if (tuple2.Item1 != stage3 || !(tuple2.Item2 == problem2))
					{
						(ba.Stage, ba.Problem) = tuple;
						return moved = true;
					}
				}
				return false;
			});
			if (moved)
			{
				UpdateRecorderIni(s.Game, gameRecord);
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			Log?.Report(s.Game.Name + ": couldn't save its record: " + ex.Message);
			gameRecord = Store.LoadGame(s.Game.Id);
		}
		BeforeAfterRecord beforeAfterRecord = gameRecord?.BeforeAfter;
		if (beforeAfterRecord == null)
		{
			return s;
		}
		BeforeAfterStage stage = beforeAfterRecord.Stage;
		string text2;
		if (((uint)stage > 1u && stage != BeforeAfterStage.Done) || 1 == 0)
		{
			string text = Voided(s.Game, beforeAfterRecord);
			if (text != null)
			{
				text2 = text;
				goto IL_01df;
			}
		}
		string text3 = beforeAfterRecord.Problem;
		if (text3 == null)
		{
			string text4 = ((beforeAfterRecord.Stage == BeforeAfterStage.AfterReady && beforeAfterRecord.Dirty) ? "The game ran after the compile, Continue compiles again first" : null);
			if (text4 == null)
			{
				string text5 = ((beforeAfterRecord.Stage == BeforeAfterStage.AfterReady && s.Status != GameStatus.Warmed) ? s.StatusReason : null);
				if (text5 == null)
				{
					BeforeAfterStage stage2 = beforeAfterRecord.Stage;
					bool flag = (uint)stage2 <= 1u;
					text5 = ((flag && s.RecordingPaused) ? PausedNote(Settings) : null);
				}
				text4 = text5;
			}
			text3 = text4;
		}
		text2 = text3;
		goto IL_01df;
		IL_01df:
		string problem = text2;
		return s with
		{
			BeforeAfter = new BeforeAfterInfo(beforeAfterRecord.Stage, beforeAfterRecord.Minutes, problem, _played.GetValueOrDefault(s.Game.Id), beforeAfterRecord.ClearPending)
		};
	}

	private (BeforeAfterStage, string?) CompileEnded(GameState s, GameRecord rec, BeforeAfterRecord ba)
	{
		QueueItem queueItem = QueueItemOf(s.Game.Id);
		if ((object)queueItem != null)
		{
			switch (queueItem.Stage)
			{
			case QueueStage.Failed:
			case QueueStage.Stopped:
				return (BeforeAfterStage.Compiling, queueItem.Error ?? "The compile was stopped");
			default:
				return (BeforeAfterStage.Compiling, ba.Problem);
			case QueueStage.Done:
				break;
			}
		}
		QueueItem queueItem2 = queueItem;
		if (s.Status == GameStatus.Warmed && (queueItem2 != null || rec.WarmedAt >= ba.ArmedAt))
		{
			return (BeforeAfterStage.AfterReady, null);
		}
		return (BeforeAfterStage.Compiling, (s.Status == GameStatus.Stale) ? s.StatusReason : (ba.Problem ?? "The compile didn't finish, compile again"));
	}

	private IDisposable? HoldForCompile()
	{
		IDisposable disposable = Busy.TryHold(Store.DataDir, Clock());
		if (disposable == null || !Busy.IsHeld(DeletingName))
		{
			return disposable;
		}
		disposable.Dispose();
		return null;
	}

	public CacheInventory CacheInventory()
	{
		IAppCache appCache = AppCache;
		(GpuVendor, List<(string, string, bool)>) tuple;
		if (!(appCache is NvidiaAppCache nvidiaAppCache))
		{
			if (appCache is AmdAppCache amdAppCache)
			{
				int num = 2;
				List<(string, string, bool)> list = new List<(string, string, bool)>(num);
				CollectionsMarshal.SetCount(list, num);
				Span<(string, string, bool)> span = CollectionsMarshal.AsSpan(list);
				span[0] = ("DirectX 12", amdAppCache.DxcDir, true);
				span[1] = ("DirectX 11", amdAppCache.DxDir, false);
				tuple = (GpuVendor.Amd, list);
			}
			else
			{
				tuple = (GpuVendor.Unknown, new List<(string, string, bool)>());
			}
		}
		else
		{
			tuple = (GpuVendor.Nvidia, new List<(string, string, bool)> { ("DirectX 12 and 11", nvidiaAppCache.Dir, true) });
		}
		(GpuVendor, List<(string, string, bool)>) tuple2 = tuple;
		GpuVendor own = tuple2.Item1;
		List<(string, string, bool)> item = tuple2.Item2;
		Dictionary<string, string> notes = new Dictionary<string, string>();
		Dictionary<string, List<(CacheGroupKind, string, string)>> claims = Claims(own, notes);
		Dictionary<(CacheGroupKind Kind, string Name), (List<FileInfo> Files, HashSet<string> Ids)> items = new Dictionary<(CacheGroupKind, string), (List<FileInfo>, HashSet<string>)>();
		DateTime? dateTime = OldDriverBefore(fresh: true);
		List<CacheFolder> list2 = new List<CacheFolder>();
		foreach (var (label, text, d3d) in item)
		{
			list2.Add(Read(text, out List<FileInfo> files)with
			{
				Label = label,
				Cap = ((AppCache is AmdAppCache amdAppCache2 && text == amdAppCache2.DxcDir) ? new long?(17179869184L) : ((long?)null))
			});
			foreach (FileInfo item6 in files)
			{
				string key = KeyOf(item6, d3d);
				var (kind, text2, readOnlyList) = Owner(claims, key);
				if (item6.LastWriteTimeUtc < dateTime)
				{
					Add(CacheGroupKind.OldDriver, (readOnlyList != null) ? text2 : "Other apps and games", new _003C_003Ez__ReadOnlySingleElementList<FileInfo>(item6));
				}
				else
				{
					Add(kind, text2, new _003C_003Ez__ReadOnlySingleElementList<FileInfo>(item6), readOnlyList);
				}
			}
		}
		string text3 = Path.Combine(Store.DataDir, "games");
		if (Directory.Exists(text3))
		{
			Add(CacheGroupKind.OldBuild, "Windows shader cache of earlier compiles", D3DSCache.Made(Path.Combine(LocalAppData, "D3DSCache"), text3, Directory.GetCreationTimeUtc(text3)).SelectMany(D3DSCache.CacheFiles));
		}
		HashSet<string> read = item.Select(((string, string, bool) f) => GameFiles.DirKey(f.Item2)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var item7 in VendorFolders.Where(((GpuVendor Vendor, string Label, string Folder) f) => f.Vendor == own && !read.Contains(GameFiles.DirKey(Path.Combine(LocalAppData, f.Folder)))))
		{
			string item2 = item7.Label;
			string item3 = item7.Folder;
			Read(Path.Combine(LocalAppData, item3), out List<FileInfo> files2, recurse: true);
			Add(CacheGroupKind.Unattributed, item2, files2);
		}
		string note = null;
		HashSet<GpuVendor> hashSet = (from a in (Adapters ?? new Func<IReadOnlyList<DxgiAdapter>>(GpuBackends.Adapters))()
			select a.Gpu.Vendor).ToHashSet();
		foreach (GpuVendor vendor in new GpuVendor[2]
		{
			GpuVendor.Nvidia,
			GpuVendor.Amd
		}.Where((GpuVendor v) => v != own))
		{
			foreach (var item8 in VendorFolders.Where(((GpuVendor Vendor, string Label, string Folder) f) => f.Vendor == vendor))
			{
				string item4 = item8.Label;
				string item5 = item8.Folder;
				Read(Path.Combine(LocalAppData, item5), out List<FileInfo> files3, recurse: true);
				Add(CacheGroupKind.OtherVendor, item4, files3);
			}
			if (hashSet.Contains(vendor))
			{
				note = "This PC also has an " + ((vendor == GpuVendor.Nvidia) ? "NVIDIA" : "AMD") + " GPU. Apps that run on it use this cache.";
			}
		}
		Dictionary<(string, string), List<string>> listed = (from f in items.Values.SelectMany(((List<FileInfo> Files, HashSet<string> Ids) i) => i.Files)
			group f by (Path.GetDirectoryName(f.FullName), UnitKey(f.FullName))).ToDictionary((IGrouping<(string, string), FileInfo> g) => g.Key, (IGrouping<(string, string), FileInfo> g) => g.Select((FileInfo f) => f.FullName).ToList());
		List<CacheGroup> groups = (from i in items
			where i.Value.Files.Count > 0
			group i by i.Key.Kind into g
			orderby g.Key
			select new CacheGroup(g.Key, (from i in g
				select new CacheItem(i.Key.Name, g.Key, i.Value.Files.Select((FileInfo f) => new CacheFile(f.FullName, f.Length, f.LastWriteTimeUtc)).ToList(), i.Value.Ids.Order().ToList(), Seen(i.Value.Files), (g.Key == CacheGroupKind.Uninstalled) ? notes.GetValueOrDefault(i.Key.Name) : null) into p
				orderby p.Bytes descending
				select p).ToList(), (g.Key == CacheGroupKind.OtherVendor) ? note : null)).ToList();
		return new CacheInventory(list2, groups, dateTime);
		void Add(CacheGroupKind cacheGroupKind, string name, IEnumerable<FileInfo> collection, IEnumerable<string>? ids = null)
		{
			if (!items.TryGetValue((cacheGroupKind, name), out (List<FileInfo>, HashSet<string>) value))
			{
				value = (items[(cacheGroupKind, name)] = (new List<FileInfo>(), new HashSet<string>()));
			}
			value.Item1.AddRange(collection);
			bool flag = (uint)cacheGroupKind <= 1u;
			if (flag && ids != null)
			{
				value.Item2.UnionWith(ids);
			}
		}
		string? KeyOf(FileInfo f, bool d3d2)
		{
			if (own != GpuVendor.Nvidia)
			{
				return AmdAppCache.Key(f.Name, d3d2);
			}
			return NvidiaAppCache.Key(f.Name);
		}
		IReadOnlyCollection<string> Seen(List<FileInfo> source)
		{
			return source.Select((FileInfo f) => (Path.GetDirectoryName(f.FullName), UnitKey(f.FullName))).Distinct().SelectMany(((string, string) u) => listed[u])
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
		}
	}

	private Dictionary<string, List<(CacheGroupKind Kind, string Name, string Id)>> Claims(GpuVendor own, Dictionary<string, string>? notes = null)
	{
		Dictionary<string, List<(CacheGroupKind Kind, string Name, string Id)>> claims = new Dictionary<string, List<(CacheGroupKind, string, string)>>(StringComparer.OrdinalIgnoreCase);
		List<(HashSet<string> Exes, CacheGroupKind Kind, string Name, string Id)> byExe = new List<(HashSet<string>, CacheGroupKind, string, string)>();
		IReadOnlyList<GameState> games = Games;
		foreach (Game item4 in games.Select((GameState s) => s.Game).Concat(HiddenGames))
		{
			GameRecord gameRecord = Store.LoadGame(item4.Id);
			Claim(gameRecord, new _003C_003Ez__ReadOnlyArray<string>(new string[3]
			{
				Path.GetFileName(item4.ExePath),
				gameRecord.LaunchedExeName,
				gameRecord.WarmedExeName
			}), CacheGroupKind.Installed, item4.Name, item4.Id);
		}
		foreach (var item5 in UninstalledRecords(games))
		{
			string item = item5.Id;
			GameRecord item2 = item5.Rec;
			string text = UninstalledName(item, item2);
			if (notes != null)
			{
				string recorderInstallDir = item2.RecorderInstallDir;
				if (recorderInstallDir != null && Directory.Exists(recorderInstallDir))
				{
					notes[text] = "Its folder " + recorderInstallDir + " is still there. It may be on a drive or in a store SCSKiller didn't find this time.";
				}
			}
			Claim(item2, new _003C_003Ez__ReadOnlyArray<string>(new string[3]
			{
				UninstalledExe(item2),
				item2.LaunchedExeName,
				Path.GetFileName(item2.RecorderExe)
			}), CacheGroupKind.Uninstalled, text, item);
		}
		if (own == GpuVendor.Nvidia)
		{
			foreach (List<(CacheGroupKind, string, string)> list in claims.Values)
			{
				foreach (var item6 in list.ToList())
				{
					var (cacheGroupKind, _, id) = item6;
					foreach (var item7 in byExe.Where(((HashSet<string> Exes, CacheGroupKind Kind, string Name, string Id) o) => o.Id != id && byExe.Any(((HashSet<string> Exes, CacheGroupKind Kind, string Name, string Id) m) => m.Id == id && m.Exes.Overlaps(o.Exes)) && !list.Any(((CacheGroupKind Kind, string Name, string Id) x) => x.Id == o.Id)))
					{
						list.Add(((cacheGroupKind == CacheGroupKind.OldBuild) ? cacheGroupKind : item7.Kind, item7.Name, item7.Id));
					}
				}
			}
		}
		return claims;
		void Claim(GameRecord rec, IEnumerable<string?> exeNames, CacheGroupKind kind, string name, string text2)
		{
			HashSet<string> hashSet;
			if (rec.GameKeys.Count > 0)
			{
				HashSet<string> warmedKeys = rec.WarmedKeys;
				if (warmedKeys != null)
				{
					hashSet = rec.CacheKeys.Except(rec.GameKeys).Except(warmedKeys).ToHashSet();
					goto IL_0064;
				}
			}
			hashSet = new HashSet<string>();
			goto IL_0064;
			IL_0064:
			HashSet<string> unused = hashSet;
			List<(string, CacheGroupKind)> list2 = rec.CacheKeys.Select((string k) => (k: k, unused.Contains(k) ? CacheGroupKind.OldBuild : kind)).ToList();
			if (own == GpuVendor.Amd)
			{
				list2.AddRange(from h in exeNames.OfType<string>().Select(AmdAppCache.HintKey)
					where !rec.CacheKeys.Contains(h)
					select (h: h, kind: kind));
			}
			foreach (var (key, item3) in list2)
			{
				List<(CacheGroupKind, string, string)> list5;
				if (!claims.TryGetValue(key, out List<(CacheGroupKind, string, string)> value))
				{
					List<(CacheGroupKind, string, string)> list3 = (claims[key] = new List<(CacheGroupKind, string, string)>());
					list5 = list3;
				}
				else
				{
					list5 = value;
				}
				list5.Add((item3, name, text2));
			}
			byExe.Add((exeNames.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase), kind, name, text2));
		}
	}

	private static (CacheGroupKind Kind, string Name, IReadOnlyList<string>? Ids) Owner(Dictionary<string, List<(CacheGroupKind Kind, string Name, string Id)>> claims, string? key)
	{
		if (key == null || !claims.TryGetValue(key, out List<(CacheGroupKind, string, string)> value))
		{
			return (Kind: CacheGroupKind.Unattributed, Name: key ?? "Files without an app key", Ids: null);
		}
		CacheGroupKind cacheGroupKind = value.Min(((CacheGroupKind Kind, string Name, string Id) x) => x.Kind);
		CacheGroupKind item = cacheGroupKind;
		string item2 = string.Join(", ", value.Select(((CacheGroupKind Kind, string Name, string Id) x) => x.Name).Distinct());
		bool flag = (uint)cacheGroupKind <= 1u;
		return (Kind: item, Name: item2, Ids: flag ? value.Select(((CacheGroupKind Kind, string Name, string Id) x) => x.Id).Distinct().Order()
			.ToList() : new List<string>());
	}

	private IEnumerable<(string Id, GameRecord Rec)> UninstalledRecords(IReadOnlyList<GameState> games)
	{
		string path = Path.Combine(Store.DataDir, "games");
		HashSet<string> listed = (from g in games.Select((GameState s) => s.Game).Concat(HiddenGames)
			select Store.GameDir(g.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		IEnumerable<string> enumerable2;
		if (!Directory.Exists(path))
		{
			IEnumerable<string> enumerable = Array.Empty<string>();
			enumerable2 = enumerable;
		}
		else
		{
			enumerable2 = Directory.EnumerateDirectories(path);
		}
		foreach (string item2 in enumerable2)
		{
			if (!listed.Contains(item2))
			{
				string text = AppStore.GameId(Path.GetFileName(item2));
				GameRecord item;
				try
				{
					item = Store.LoadGame(text);
				}
				catch (ArgumentException)
				{
					continue;
				}
				yield return (Id: text, Rec: item);
			}
		}
	}

	private static string? UninstalledExe(GameRecord rec)
	{
		return rec.WarmedExeName ?? rec.LaunchedExeName ?? Path.GetFileName(rec.RecorderExe);
	}

	private string UninstalledName(string id, GameRecord rec)
	{
		string fileName = Path.GetFileName(Store.GameDir(id));
		string text = UninstalledExe(rec);
		if (text == null || text.Length <= 0)
		{
			return fileName;
		}
		return text + " (" + fileName + ")";
	}

	private DateTime? OldDriverBefore(bool fresh)
	{
		if (!(AppCache is NvidiaAppCache))
		{
			return null;
		}
		string driverId = DriverId;
		if (!fresh)
		{
			OldBefore oldBefore = _oldBefore;
			if ((object)oldBefore != null && oldBefore.DriverId == driverId)
			{
				return oldBefore.At;
			}
		}
		DateTime? dateTime = DriverInstalledAt();
		foreach (GameRecord item in Games.Select((GameState s) => Store.LoadGame(s.Game.Id)).Concat(from r in UninstalledRecords(Games)
			select r.Rec))
		{
			if (item.WarmedDriverId != null && item.WarmedDriverId == driverId)
			{
				DateTimeOffset? warmedAt = item.WarmedAt;
				if (warmedAt.HasValue && warmedAt.GetValueOrDefault().UtcDateTime < dateTime)
				{
					dateTime = item.WarmedAt.Value.UtcDateTime;
				}
			}
		}
		_oldBefore = new OldBefore(driverId, dateTime);
		return dateTime;
	}

	public long OldDriverBytes()
	{
		DateTime? dateTime = OldDriverBefore(fresh: false);
		if (dateTime.HasValue)
		{
			DateTime before = dateTime.GetValueOrDefault();
			if (AppCache is NvidiaAppCache nvidiaAppCache && AppCacheFiles.Readable(nvidiaAppCache.Dir))
			{
				return (from f in new DirectoryInfo(nvidiaAppCache.Dir).EnumerateFiles("*", new EnumerationOptions
					{
						IgnoreInaccessible = true
					})
					where f.LastWriteTimeUtc < before
					select f).Sum((FileInfo f) => f.Length);
			}
		}
		return 0L;
	}

	private static CacheFolder Read(string dir, out List<FileInfo> files, bool recurse = false)
	{
		string text = GameFiles.FinalPath(dir);
		bool flag = Directory.Exists(dir);
		int num;
		if (text != null)
		{
			string directoryName = Path.GetDirectoryName(dir);
			if (directoryName != null)
			{
				string text2 = GameFiles.FinalPath(directoryName);
				if (text2 != null)
				{
					num = ((!string.Equals(text, Path.Combine(text2, Path.GetFileName(dir)), StringComparison.OrdinalIgnoreCase)) ? 1 : 0);
					goto IL_0041;
				}
			}
		}
		num = 0;
		goto IL_0041;
		IL_0041:
		bool flag2 = (byte)num != 0;
		files = new List<FileInfo>();
		if (text != null)
		{
			try
			{
				files = new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions
				{
					RecurseSubdirectories = recurse,
					IgnoreInaccessible = true,
					AttributesToSkip = (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)
				}).ToList();
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
		}
		return new CacheFolder("", dir, flag, flag2 ? text : null, flag && text == null, files.Sum((FileInfo f) => f.Length));
	}

	private IEnumerable<string> CacheRoots()
	{
		IAppCache appCache = AppCache;
		string[] first;
		if (appCache is NvidiaAppCache nvidiaAppCache)
		{
			first = new string[1] { nvidiaAppCache.Dir };
		}
		else
		{
			first = ((!(appCache is AmdAppCache amdAppCache)) ? Array.Empty<string>() : new string[2] { amdAppCache.DxcDir, amdAppCache.DxDir });
		}
		return first.Concat(VendorFolders.Select(((GpuVendor Vendor, string Label, string Folder) f) => Path.Combine(LocalAppData, f.Folder))).Append(Path.Combine(LocalAppData, "D3DSCache")).Distinct(StringComparer.OrdinalIgnoreCase);
	}

	public CacheDeleteResult DeleteCaches(IReadOnlyList<CacheItem> items)
	{
		using (Busy.Hold(DeletingName))
		{
			if ((CompileRunning ?? ((Func<bool>)(() => Compiling || Busy.IsHeld())))())
			{
				throw new InvalidOperationException("a compile is in progress: delete caches once it has finished");
			}
			List<string> list = new List<string>();
			long num = 0L;
			int num2 = 0;
			Dictionary<string, GameState> games = Games.ToDictionary((GameState s) => s.Game.Id);
			IReadOnlySet<string> names = ProcessNames();
			List<(string, string)> roots = (from r in CacheRoots()
				select (Root: r, Final: GameFiles.FinalPath(r)) into r
				where r.Final != null
				select r).ToList();
			IAppCache appCache = AppCache;
			GpuVendor gpuVendor = ((appCache is NvidiaAppCache) ? GpuVendor.Nvidia : ((appCache is AmdAppCache) ? GpuVendor.Amd : GpuVendor.Unknown));
			GpuVendor own = gpuVendor;
			Dictionary<string, List<(CacheGroupKind, string, string)>> claims = Claims(own);
			Dictionary<string, FileStream?> held = new Dictionary<string, FileStream>();
			try
			{
				foreach (CacheItem item2 in items)
				{
					string text = item2.GameIds.Select((string id) =>
					{
						if (!games.TryGetValue(id, out var value2))
						{
							string text4 = UninstalledExe(Store.LoadGame(id));
							if (text4 == null || !names.Contains(Path.GetFileNameWithoutExtension(text4)))
							{
								return (string)null;
							}
							return text4;
						}
						return (!GameRunning(value2.Game)) ? null : value2.Game.Name;
					}).FirstOrDefault((string n) => n != null);
					if (text != null)
					{
						list.Add(item2.Name + ": " + text + " is running");
						continue;
					}
					foreach (string gameId in item2.GameIds)
					{
						if (!held.ContainsKey(gameId))
						{
							held[gameId] = CompileLock(gameId);
						}
					}
					if (item2.GameIds.Any((string id) => held[id] == null))
					{
						list.Add(item2.Name + ": a compile of it is in progress");
						continue;
					}
					bool flag = false;
					foreach (IGrouping<(string, string), CacheFile> item3 in from f in item2.Files
						group f by (Dir: Path.GetDirectoryName(f.Path), Key: UnitKey(f.Path)))
					{
						string text2 = OwnKey(item3.First().Path);
						(CacheGroupKind, string, IReadOnlyList<string>) tuple = ((text2 != null && item2.Kind != CacheGroupKind.OldDriver) ? Owner(claims, text2) : default((CacheGroupKind, string, IReadOnlyList<string>)));
						object obj = Unsafe(item3.ToList(), roots);
						if (obj == null)
						{
							obj = Incomplete(item3.Key.Item1, item3.Key.Item2, item2.Seen.Concat(item3.Select((CacheFile f) => f.Path)));
							if (obj == null)
							{
								IReadOnlyList<string> item = tuple.Item3;
								obj = ((item != null && (tuple.Item1 != item2.Kind || item.Except(item2.GameIds).Any())) ? ("these files are now " + tuple.Item2 + "'s") : null);
							}
						}
						string text3 = (string)obj;
						if (text3 != null)
						{
							list.Add(item2.Name + ": " + text3);
							continue;
						}
						try
						{
							AppCacheFiles.DeleteAll(item3.Select((CacheFile f) => new FileInfo(f.Path)).ToList());
						}
						catch (Exception ex) when ((ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
						{
							list.Add(item2.Name + ": " + ex.Message);
							continue;
						}
						long num3 = num + item3.Sum((CacheFile f) => f.Length);
						int num4 = num2 + item3.Count();
						flag = true;
						num2 = num4;
						num = num3;
						if (GameFiles.Inside(Path.Combine(LocalAppData, "D3DSCache"), item3.Key.Item1))
						{
							try
							{
								Directory.Delete(item3.Key.Item1);
							}
							catch (IOException)
							{
							}
						}
					}
					if (!flag)
					{
						continue;
					}
					foreach (string gameId2 in item2.GameIds)
					{
						ForgetWarm(gameId2, Store.LoadGame(gameId2));
						if (games.TryGetValue(gameId2, out var value))
						{
							Refresh(value.Game);
						}
					}
				}
			}
			finally
			{
				foreach (FileStream value3 in held.Values)
				{
					value3?.Dispose();
				}
			}
			return new CacheDeleteResult(num, num2, list);
		}
	}

	internal static string? UnitKey(string path)
	{
		return NvidiaAppCache.Key(Path.GetFileName(path)) ?? AmdAppCache.Key(Path.GetFileName(path), d3d12: false);
	}

	private string? OwnKey(string path)
	{
		string dir = Path.GetDirectoryName(path);
		IAppCache appCache = AppCache;
		if (!(appCache is NvidiaAppCache nvidiaAppCache))
		{
			if (appCache is AmdAppCache amdAppCache)
			{
				if (In(amdAppCache.DxcDir))
				{
					return AmdAppCache.Key(Path.GetFileName(path), d3d12: true);
				}
				if (In(amdAppCache.DxDir))
				{
					return AmdAppCache.Key(Path.GetFileName(path), d3d12: false);
				}
			}
		}
		else if (In(nvidiaAppCache.Dir))
		{
			return NvidiaAppCache.Key(Path.GetFileName(path));
		}
		return null;
		bool In(string d)
		{
			return string.Equals(GameFiles.DirKey(d), GameFiles.DirKey(dir), StringComparison.OrdinalIgnoreCase);
		}
	}

	private static string? Incomplete(string dir, string? key, IEnumerable<string> seen)
	{
		HashSet<string> hashSet = seen.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
		try
		{
			foreach (FileInfo item in from f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions
				{
					AttributesToSkip = FileAttributes.None
				})
				where UnitKey(f.FullName) == key
				select f)
			{
				if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
				{
					return item.Name + " is a link";
				}
				if (!hashSet.Contains(item.FullName))
				{
					return item.Name + " was added since the page read it";
				}
			}
			return null;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return Path.GetFileName(dir) + " can't be read: " + ex.Message;
		}
	}

	private static string? Unsafe(IReadOnlyList<CacheFile> unit, List<(string Root, string? Final)> roots)
	{
		foreach (CacheFile item2 in unit)
		{
			string path = Path.GetFullPath(item2.Path);
			FileInfo fileInfo = new FileInfo(path);
			if (!fileInfo.Exists || fileInfo.Length != item2.Length || fileInfo.LastWriteTimeUtc != item2.WrittenUtc)
			{
				return fileInfo.Name + " changed since the page read it";
			}
			(string, string) tuple = roots.FirstOrDefault(((string Root, string Final) r) => GameFiles.Inside(r.Root, path));
			var (text, _) = tuple;
			if (text != null)
			{
				string item = tuple.Item2;
				if (item != null)
				{
					if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0 || !string.Equals(GameFiles.Identity(path)?.Final, Path.Combine(item, Path.GetRelativePath(text, path)), StringComparison.OrdinalIgnoreCase))
					{
						return fileInfo.Name + " is reached through a link inside the cache folder";
					}
					continue;
				}
			}
			return fileInfo.Name + " is outside the driver's cache folders";
		}
		return null;
	}

	public static string EverythingWarning(CacheInventory inv, bool withOthers)
	{
		return "Deletes " + Format.Bytes(inv.Everything(withOthers).Sum((CacheItem i) => i.Bytes)) + ". Every game compiles its shaders again. Games SCSKiller compiled stutter until you compile them again, and the rest build their cache as you play. " + (withOthers ? $"The {"Other apps and games".ToLowerInvariant()} group ({Format.Bytes(inv.Bytes(CacheGroupKind.Unattributed))}) is included: apps and games outside SCSKiller build their caches again too." : $"The {"Other apps and games".ToLowerInvariant()} group ({Format.Bytes(inv.Bytes(CacheGroupKind.Unattributed))}) is kept unless you tick it.");
	}

	public static double DefaultBytesPerPsoOf(EngineInfo? e)
	{
		if (!(e?.Fork == "GAME_WutheringWaves"))
		{
			return 24576.0;
		}
		return 99492.27964145443;
	}

	public static long CacheGrowth(GameState s)
	{
		return Math.Max(0L, s.EstimatedCacheBytes.GetValueOrDefault() - s.CacheOnDisk.GetValueOrDefault());
	}

	public static bool InAddAllReady(GameState s)
	{
		GameStatus status = s.Status;
		bool flag = ((status == GameStatus.Ready || status == GameStatus.Stale) ? true : false);
		if (flag && !s.CompileUnreached && s.NoStutter == null)
		{
			return !s.LeftOutOfAddAll;
		}
		return false;
	}

	public static long? PlanPipelines(GameState s)
	{
		PlanStats plan = s.Plan;
		if ((object)plan == null)
		{
			return null;
		}
		return Math.Max(0L, plan.Recorded + plan.Generated + plan.MiddlewareItems - s.LastWarmCrashed.GetValueOrDefault());
	}

	public static string? LargeCompileWarning(IEnumerable<(long Growth, long Estimate)> queue, GpuVendor vendor)
	{
		double? num = LargeCompileGb(queue);
		if (num.HasValue)
		{
			double valueOrDefault = num.GetValueOrDefault();
			return $"This compile adds about {valueOrDefault:0} GB. {"Very large caches can make the game start slower and use more memory."}" + ((vendor == GpuVendor.Amd) ? (" It's more than the AMD driver's " + Format.Bytes(17179869184L) + " cache can hold.") : "");
		}
		return null;
	}

	private static double? LargeCompileGb(IEnumerable<(long Growth, long Estimate)> queue)
	{
		(long, long) tuple = queue.DefaultIfEmpty().MaxBy(((long Growth, long Estimate) q) => q.Growth);
		if (tuple.Item1 > 17179869184L)
		{
			long item = tuple.Item2;
			return Math.Round((double)item / 1073741824.0, MidpointRounding.AwayFromZero);
		}
		return null;
	}

	public static string? QueueCacheWarning(IReadOnlyCollection<(long Growth, long Estimate)> queue, long usage, long planned, long? limit, GpuVendor vendor)
	{
		if (limit.HasValue)
		{
			long valueOrDefault = limit.GetValueOrDefault();
			if (vendor == GpuVendor.Nvidia)
			{
				long num = queue.Select(((long Growth, long Estimate) q) => q.Estimate).DefaultIfEmpty().Max();
				if (num > valueOrDefault)
				{
					return $"This compile adds about {Math.Round((double)num / 1073741824.0, MidpointRounding.AwayFromZero):0} GB, more than your {Format.Bytes(valueOrDefault)} NVIDIA shader cache limit. The driver would throw most of it away. Raise the limit in Settings before you compile. {"Very large caches can make the game start slower and use more memory."}";
				}
			}
			double? num2 = LargeCompileGb(queue);
			if (num2.HasValue)
			{
				double valueOrDefault2 = num2.GetValueOrDefault();
				if (usage + planned > valueOrDefault)
				{
					return $"This compile adds about {valueOrDefault2:0} GB and takes the cache past your {Format.Bytes(valueOrDefault)} limit, so older games may be evicted. {"Very large caches can make the game start slower and use more memory."}";
				}
			}
			string text = LargeCompileWarning(queue, vendor);
			if (text == null)
			{
				if (planned <= 0 || !((double)(usage + planned) > 0.9 * (double)valueOrDefault))
				{
					return null;
				}
				text = "After this queue the cache is close to its " + Format.Bytes(valueOrDefault) + " limit, older games may be evicted.";
			}
			return text;
		}
		return LargeCompileWarning(queue, vendor);
	}

	internal static string BuildOf(string version, string? commit)
	{
		if (!string.IsNullOrEmpty(commit) && !version.EndsWith("+" + commit, StringComparison.Ordinal))
		{
			return version + "+" + commit;
		}
		return version;
	}

	public ScsKiller(IEnumerable<IGameSource> sources, IGpuVendorBackend vendor, IEngineReader reader, IPlanner planner, IWarmer warmer, string dataDir, string? proxyDll)
	{
		_sources = sources.ToList();
		Vendor = vendor;
		_reader = reader;
		UnrealReader unrealReader = (reader as UnrealReader) ?? (reader as EngineReaders)?.Get<UnrealReader>();
		if (unrealReader != null)
		{
			unrealReader.UserCaches = (Game g) => UnrealUserCache.FilesOf(g, SavedRoots(g));
		}
		_planner = planner;
		_warmer = warmer;
		Store = new AppStore(dataDir);
		DeleteLedger();
		try
		{
			MigrateInis(Store);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			RecordersLog(Store.DataDir, "the records' scskiller.ini entries weren't moved yet: " + ex.Message);
		}
		Middleware.LoadImages(ImagesFile);
		ReShade.LoadProbes(ProbesFile);
		StutterList stutterList = StutterList.Cached(StutterFile);
		if (stutterList != null)
		{
			StutterList.Current = stutterList;
		}
		ConfirmedEngines confirmedEngines = ConfirmedEngines.Cached(ConfirmedFile);
		if (confirmedEngines != null)
		{
			ConfirmedEngines.Current = confirmedEngines;
		}
		BootPrecompiles bootPrecompiles = BootPrecompiles.Cached(PrecompilesFile);
		if (bootPrecompiles != null)
		{
			BootPrecompiles.Current = bootPrecompiles;
		}
		GameVerdicts gameVerdicts = RulesTrust.Cached("game-verdicts", GameVerdicts.TryParse, VerdictsFile, null);
		if (gameVerdicts != null)
		{
			GameVerdicts.Current = gameVerdicts;
		}
		MaximumMode maximumMode = RulesTrust.Cached("maximum-mode", MaximumMode.TryParse, MaximumFile, null);
		if (maximumMode != null)
		{
			MaximumMode.Current = maximumMode;
		}
		RulesFile rulesFile = RulesFile.Cached(RulesPath, RulesTrust, null);
		if (rulesFile != null)
		{
			RulesFile.Current = RulesFile.WithServer(rulesFile);
		}
		News = SCSKiller.Core.App.News.Cached(NewsPath, RulesTrust, null) ?? SCSKiller.Core.App.News.Empty;
		_compat = CompatFeed.Cached(CompatTrust, CompatFile);
		_proxyDll = proxyDll;
		AppCache = vendor.AppCache;
		RunningGameExes = DiscoveredGamesRunning;
		OfflineRuns = (OfflineSession s) => OthersRun(s, Processes(arg: true));
		_startGpu = Settings.Gpu;
	}

	public static ScsKiller CreateDefault()
	{
		IGpuVendorBackend gpuVendorBackend = DetectGpu();
		ScsKiller scsKiller = new ScsKiller(new _003C_003Ez__ReadOnlyArray<IGameSource>(new IGameSource[14]
		{
			new SteamSource(),
			new EpicSource(),
			new XboxSource(),
			new GogSource(),
			new UbisoftSource(),
			new BattleNetSource(),
			new PurpleSource(),
			new HoYoPlaySource(),
			new EaSource(),
			new GaijinSource(),
			new RiotSource(),
			new AmazonSource(),
			new ExoLibrarySource(AppStore.DefaultDir),
			new ManualSource(new AppStore(AppStore.DefaultDir))
		}), gpuVendorBackend, DefaultReaders(), new Planner(Path.Combine(AppStore.DefaultDir, "packs"), SharedPackDir(AppStore.DefaultDir, gpuVendorBackend.Vendor)), new Warmer(gpuVendorBackend), AppStore.DefaultDir, NativeTools.Find("d3d12.dll"));
		scsKiller.Adapters = GpuBackends.Adapters;
		scsKiller.AgsDll = NativeTools.Find("scskiller_ags.dll");
		scsKiller.RedetectGpu();
		return scsKiller;
	}

	public static IGpuVendorBackend DetectGpu(GpuChoice? choice = null)
	{
		Settings settings = new AppStore(AppStore.DefaultDir).LoadSettings();
		return GpuBackends.Detect(choice ?? settings.Gpu, settings.ExperimentalIntel);
	}

	public static string? PackGpu(GpuVendor v)
	{
		return v switch
		{
			GpuVendor.Nvidia => "nvidia", 
			GpuVendor.Amd => "amd", 
			_ => null, 
		};
	}

	public static string ShareVendor(GpuVendor v)
	{
		return v.ToString().ToLowerInvariant();
	}

	public static string? SharedPackDir(string dataDir, GpuVendor v)
	{
		string text = PackGpu(v);
		if (text == null)
		{
			return null;
		}
		return Path.Combine(dataDir, "community", "packs", text);
	}

	public static IEngineReader DefaultReaders(string? dataDir = null)
	{
		if (dataDir == null)
		{
			dataDir = AppStore.DefaultDir;
		}
		CarvedReader carved = new CarvedReader();
		List<(string, IEngineReader)> list = new List<(string, IEngineReader)>();
		list.Add(("Unreal", new UnrealReader(dataDir)));
		list.Add(("FromSoftware", new FromSoftReader(dataDir)));
		list.Add(("Unity", new UnityReader()));
		list.Add(("RE Engine", new ReEngineReader(dataDir)));
		list.Add(("REDengine 3", new RedEngineReader()));
		list.Add(("Dagor", new DagorReader()));
		list.Add(("Northlight", new NorthlightReader()));
		list.Add(("Source 2", new Source2Reader()));
		list.Add(("RAGE", new RageReader()));
		list.Add(("Naughty Dog", new NaughtyDogReader()));
		list.Add(("Santa Monica", new SantaMonicaReader()));
		list.Add(("Nixxes", new NixxesReader()));
		list.Add(("Tank", new TankReader()));
		list.Add(("Decima", new DecimaReader(dataDir)));
		list.Add(("Luminous", new LuminousReader()));
		list.Add(("ForzaTech", new ForzaTechReader()));
		list.AddRange(CarvedReader.Families.Select((string f) => ((string f, IEngineReader))(f: f, carved)));
		return new EngineReaders(list.ToArray());
	}

	public bool RedetectGpu()
	{
		GpuInfo gpuInfo;
		lock (_gpuLock)
		{
			try
			{
				IReadOnlyList<DxgiAdapter> readOnlyList = Adapters?.Invoke();
				if (readOnlyList == null)
				{
					return false;
				}
				GpuInfo gpu = Vendor.Gpu;
				string? gpuRestartNote = GpuRestartNote;
				string driverId = DriverId;
				string text = gpuRestartNote;
				gpuInfo = gpu;
				DxgiAdapter dxgiAdapter = Followed(readOnlyList);
				object gpuRestartNote2;
				if ((object)dxgiAdapter != null)
				{
					_goneSince = null;
					if (Settings.Gpu != null || _startGpu != null)
					{
						DxgiAdapter dxgiAdapter2 = GpuBackends.Pick(readOnlyList, Settings.Gpu);
						if ((object)dxgiAdapter2 != null && dxgiAdapter2.Gpu.AdapterLuid != dxgiAdapter.Gpu.AdapterLuid)
						{
							gpuRestartNote2 = "Restart SCSKiller to use " + dxgiAdapter2.Gpu.Name;
							goto IL_00d3;
						}
					}
					gpuRestartNote2 = null;
					goto IL_00d3;
				}
				DateTimeOffset dateTimeOffset = Clock();
				DateTimeOffset valueOrDefault = _goneSince.GetValueOrDefault();
				DateTimeOffset dateTimeOffset2;
				if (!_goneSince.HasValue)
				{
					valueOrDefault = Clock();
					_goneSince = valueOrDefault;
					dateTimeOffset2 = valueOrDefault;
				}
				else
				{
					dateTimeOffset2 = valueOrDefault;
				}
				if (dateTimeOffset - dateTimeOffset2 >= GpuGoneAfter)
				{
					DxgiAdapter dxgiAdapter3 = GpuBackends.Pick(readOnlyList, Settings.Gpu);
					if ((object)dxgiAdapter3 != null)
					{
						GpuRestartNote = "Restart SCSKiller to use " + dxgiAdapter3.Gpu.Name;
					}
				}
				goto IL_0279;
				IL_0279:
				if (GpuRestartNote == text && Vendor.Gpu == gpuInfo && DriverId == driverId)
				{
					return false;
				}
				goto end_IL_0011;
				IL_00d3:
				GpuRestartNote = (string?)gpuRestartNote2;
				if (dxgiAdapter != _known || !_resolved)
				{
					bool resolved = !(Vendor is IRefreshableGpu refreshableGpu) || refreshableGpu.Refresh(dxgiAdapter.Gpu);
					DxgiAdapter known = dxgiAdapter;
					_resolved = resolved;
					_known = known;
				}
				if (dxgiAdapter.Gpu.DriverVersion.Length > 0)
				{
					_id = $"{(int)dxgiAdapter.Gpu.Vendor:x4}:{dxgiAdapter.DeviceId:x4}:{dxgiAdapter.SubSysId:x8}:{dxgiAdapter.Gpu.DriverVersion}";
				}
				if (_resolved && _id != null)
				{
					_completeId = _id;
				}
				goto IL_0279;
				end_IL_0011:;
			}
			catch (Exception ex)
			{
				Log?.Report("re-detecting the GPU failed: " + ex.Message);
				return false;
			}
		}
		Log?.Report(GpuRestartNote ?? $"GPU: {gpuInfo.Name}, driver {gpuInfo.DriverVersion} -> {Vendor.Gpu.Name}, driver {Vendor.Gpu.DriverVersion}");
		DropStaleKept();
		GpuChanged?.Invoke();
		return true;
	}

	private GpuSnapshot Snapshot()
	{
		lock (_gpuLock)
		{
			string text = _known?.Gpu.DriverVersion;
			string text2 = ((text != null && text.Length > 0) ? text : null);
			return new GpuSnapshot(Vendor.Gpu, _id, _known == null || _resolved, _id != _completeId, text2, (text2 != null && Vendor is IRefreshableGpu refreshableGpu) ? refreshableGpu.FallbackVersion(text2) : null);
		}
	}

	private static bool CurrentDriver(GpuSnapshot s, string? id, string? version, bool adopting = false)
	{
		if (s.Id != null)
		{
			if (id == null)
			{
				if (version != null)
				{
					if (!(version == s.Dxgi) && !(version == s.Fallback))
					{
						if (version == s.Gpu.DriverVersion)
						{
							if (!s.Complete)
							{
								if (!adopting)
								{
									return !s.Moved;
								}
								return false;
							}
							return true;
						}
						return false;
					}
					return true;
				}
				return false;
			}
			return id == s.Id;
		}
		return true;
	}

	private static bool AdoptDriverId(GameRecord r, GpuSnapshot s)
	{
		if (s.Id == null)
		{
			return false;
		}
		bool num = r.WarmedAt.HasValue && r.WarmedDriverId == null && CurrentDriver(s, null, r.WarmedDriverVersion, adopting: true);
		bool flag = r.CrashKeysDriver != null && r.CrashKeysDriver != s.Id && CurrentDriver(s, null, r.CrashKeysDriver, adopting: true);
		if (num)
		{
			r.WarmedDriverId = s.Id;
		}
		if (flag)
		{
			r.CrashKeysDriver = s.Id;
		}
		return num | flag;
	}

	private void AdoptDismissals(GpuSnapshot s)
	{
		if (s.Id == null)
		{
			return;
		}
		Dictionary<string, string> dictionary = Store.LoadDismissed();
		bool flag = false;
		foreach (KeyValuePair<string, string> item in dictionary.ToList())
		{
			item.Deconstruct(out var key, out var value);
			string text = key;
			string[] array = value.Split('|', 2);
			if (array != null && array.Length == 2)
			{
				string text2 = array[0];
				string text3 = array[1];
				if (text2 != s.Id && CurrentDriver(s, null, text2, adopting: true))
				{
					value = text;
					key = (dictionary[value] = s.Id + "|" + text3);
					flag = true;
				}
			}
		}
		if (flag)
		{
			Store.SaveDismissed(dictionary);
		}
	}

	private DxgiAdapter? Followed(IReadOnlyList<DxgiAdapter> all)
	{
		return all.FirstOrDefault((DxgiAdapter a) => a.Gpu.AdapterLuid == Vendor.Gpu.AdapterLuid) ?? all.FirstOrDefault((DxgiAdapter a) =>
		{
			DxgiAdapter known = _known;
			GpuVendor vendor2;
			if ((object)known == null)
			{
				GpuVendor vendor = a.Gpu.Vendor;
				string name = a.Gpu.Name;
				vendor2 = Vendor.Vendor;
				string name2 = Vendor.Gpu.Name;
				if (vendor == vendor2)
				{
					return name == name2;
				}
				return false;
			}
			GpuVendor vendor3 = a.Gpu.Vendor;
			uint deviceId = a.DeviceId;
			uint subSysId = a.SubSysId;
			vendor2 = known.Gpu.Vendor;
			uint deviceId2 = known.DeviceId;
			uint subSysId2 = known.SubSysId;
			return vendor3 == vendor2 && deviceId == deviceId2 && subSysId == subSysId2;
		});
	}

	public Task WhenQueueIdle()
	{
		lock (_lock)
		{
			return _worker ?? Task.CompletedTask;
		}
	}

	public Task<IReadOnlyList<GameState>> ScanAsync(CancellationToken ct, bool userRequested = false)
	{
		return Scan(force: false, userRequested, ct);
	}

	public Task<IReadOnlyList<GameState>> RescanAsync(CancellationToken ct, bool userRequested = false)
	{
		return Scan(force: true, userRequested, ct);
	}

	private bool TakeUserFetch()
	{
		lock (_scanLock)
		{
			DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(UserFetchFile);
			TimeSpan timeSpan = DateTime.UtcNow - ((lastWriteTimeUtc > _userFetched) ? lastWriteTimeUtc : _userFetched);
			if (timeSpan >= TimeSpan.Zero && timeSpan < UserFetchEvery)
			{
				return false;
			}
			_userFetched = DateTime.UtcNow;
			try
			{
				File.WriteAllText(UserFetchFile, "");
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
			return true;
		}
	}

	private async Task<ServerCheck> Fetched(Task<bool> lists, Task community)
	{
		Func<Task> userFetch = UserFetch;
		Task task = ((userFetch != null) ? Task.Run(userFetch) : Task.CompletedTask);
		try
		{
			InlineArray3<Task> buffer = default;
			buffer[0] = lists;
			buffer[1] = community;
			buffer[2] = task;
			await Task.WhenAll(buffer);
		}
		catch (Exception ex)
		{
			Log?.Report("refreshing the server's data failed: " + ex.Message);
		}
		return (lists.IsCompletedSuccessfully && !lists.Result) ? ServerCheck.Unreachable : ServerCheck.Done;
	}

	private Task<IReadOnlyList<GameState>> Scan(bool force, bool userRequested, CancellationToken ct)
	{
		return Task.Run(() =>
		{
			KeptList keptList = Store.LoadList();
			bool flag = (object)keptList != null && keptList.Build != CoreBuild;
			if (flag)
			{
				Interlocked.Increment(ref _updateScans);
			}
			try
			{
				IReadOnlyList<GameState> result;
				if (!userRequested)
				{
					result = LowIo(() => Scanned(force, userRequested, ct));
				}
				else
				{
					IReadOnlyList<GameState> readOnlyList = Scanned(force, userRequested, ct);
					result = readOnlyList;
				}
				return result;
			}
			finally
			{
				if (flag)
				{
					Interlocked.Decrement(ref _updateScans);
				}
				Progress(null);
			}
		}, ct);
	}

	private void Progress(string? text)
	{
		ScanProgress = text;
		ScanProgressed?.Invoke();
	}

	private static string SourceName(IGameSource s)
	{
		if (!(s is SteamSource))
		{
			if (!(s is EpicSource))
			{
				if (!(s is XboxSource))
				{
					if (!(s is EaSource))
					{
						if (!(s is GogSource))
						{
							if (!(s is UbisoftSource))
							{
								if (!(s is BattleNetSource))
								{
									if (!(s is PurpleSource))
									{
										if (!(s is HoYoPlaySource))
										{
											if (!(s is GaijinSource))
											{
												if (!(s is RiotSource))
												{
													if (s is ManualSource)
													{
														return "your games and folders";
													}
													if (s is AmazonSource)
													{
														return "Amazon Games";
													}
													if (s is ExoLibrarySource)
													{
														return "Exo Launcher";
													}
													return s.Store.ToString();
												}
												return "Riot Client";
											}
											return "Gaijin";
										}
										return "HoYoPlay";
									}
									return "PURPLE";
								}
								return "Battle.net";
							}
							return "Ubisoft Connect";
						}
						return "GOG";
					}
					return "EA app";
				}
				return "Xbox";
			}
			return "Epic Games";
		}
		return "Steam";
	}

	private IReadOnlyList<GameState> Scanned(bool force, bool userRequested, CancellationToken ct)
	{
		long started = Interlocked.Increment(ref _scans);
		long discovering = Ticket();
		CleanOfflineSessions();
		RedetectGpu();
		AdoptDismissals(Snapshot());
		if (userRequested)
		{
			Middleware.ForgetScans();
			ReShade.ForgetProbes();
		}
		if (!force && !userRequested && !Settings.ScanAtStart)
		{
			KeptList keptList = Store.LoadList();
			if ((object)keptList != null && keptList.Build == CoreBuild && keptList.Driver == DriverId)
			{
				List<Game> list = Store.LoadDiscovered(CoreBuild);
				if (list != null && list.Count > 0)
				{
					return Listed(started, discovering, keptList.Games, list);
				}
			}
		}
		if (Empty())
		{
			KeptList keptList2 = Store.LoadList();
			if ((object)keptList2 != null && keptList2.Build == CoreBuild && keptList2.Driver == DriverId)
			{
				bool flag;
				lock (_lock)
				{
					if (flag = _listed == 0L && _games.Count == 0)
					{
						_games = (from x in keptList2.Games
							where !Unlisted(x.Game.Id)
							select x with
							{
								Playing = IsPlaying(x.Game.Id)
							}).ToList();
					}
				}
				if (flag)
				{
					Progress(ScanProgress);
				}
			}
		}
		(List<Game> Found, Dictionary<string, Exception> Unresolved, bool Failed) tuple = Discover(Store.LoadDiscovered(CoreBuild), (IGameSource s) => s.Discover(), userRequested);
		List<Game> item = tuple.Found;
		Dictionary<string, Exception> item2 = tuple.Unresolved;
		bool item3 = tuple.Failed;
		List<GameState> list2 = new List<GameState>();
		List<long> list3 = new List<long>();
		List<bool> list4 = new List<bool>();
		bool flag2 = false;
		List<Game> list5 = Visible(item).DistinctBy((Game game) => game.Id).OrderBy((Game game) => game.Name, StringComparer.OrdinalIgnoreCase).ToList();
		lock (_lock)
		{
			Publish(started, discovering, (from game in list5
				where !Unlisted(game.Id)
				select _games.Find((GameState x) => x.Game.Id == game.Id) ?? Unchecked(game, "checking its files")).ToList());
		}
		Progress(ScanProgress);
		lock (_scanLoop)
		{
			foreach (Game g in list5)
			{
				ct.ThrowIfCancellationRequested();
				Progress($"Checking each game… {list2.Count + 1} of {list5.Count}");
				long num = Ticket();
				list3.Add(num);
				bool fresh = false;
				bool driverStale = false;
				GameState gameState = (item2.TryGetValue(g.Id, out var value) ? Unread(g, value, out driverStale) : Evaluate(g, force, out fresh, out driverStale));
				list2.Add(gameState);
				list4.Add(driverStale);
				flag2 |= fresh;
				GameState gameState2 = null;
				lock (_lock)
				{
					if (!Unlisted(g.Id) && !StaleCopy(g) && _evaluatedAt.GetValueOrDefault(g.Id) < num && (!IsUnchecked(gameState) || !_evaluatedAt.ContainsKey(g.Id)))
					{
						int num2 = _games.FindIndex((GameState x) => x.Game.Id == g.Id);
						if (num2 >= 0)
						{
							if (!IsUnchecked(gameState))
							{
								Dictionary<string, long> evaluatedAt = _evaluatedAt;
								string id = g.Id;
								Dictionary<string, bool> driverStale2 = _driverStale;
								string id2 = g.Id;
								long value2 = num;
								bool value3 = driverStale;
								evaluatedAt[id] = value2;
								driverStale2[id2] = value3;
							}
							gameState2 = (_games[num2] = WithVerdict(gameState));
						}
					}
				}
				if (gameState2 != null)
				{
					GameChanged?.Invoke(gameState2);
				}
			}
		}
		lock (_lock)
		{
			Publish(started, discovering, Newest(list2, list3, list4));
		}
		if (list2.Any((GameState s) => s.Vac))
		{
			string text = VacReadError();
			if (text != null)
			{
				Log?.Report("Steam's " + text + ". Every Steam game counts as VAC until it reads.");
			}
		}
		IReadOnlySet<string> running = Running();
		HashSet<string> hashSet = LearnKeysOfRunning(running);
		for (int num3 = 0; num3 < list2.Count; num3++)
		{
			if (!item2.ContainsKey(list2[num3].Game.Id) && (_launched.ContainsKey(Path.GetFileName(list2[num3].Game.ExePath)) || hashSet.Contains(list2[num3].Game.Id)))
			{
				list3[num3] = Ticket();
				list2[num3] = Evaluate(list2[num3].Game, force: false, out var _, out var driverStale3);
				list4[num3] = driverStale3;
			}
		}
		lock (_lock)
		{
			Publish(started, discovering, Newest(list2, list3, list4));
			list2 = list2.Where((GameState s) => !Unlisted(s.Game.Id)).ToList();
		}
		foreach (GameState item4 in list2)
		{
			GameChanged?.Invoke(item4);
		}
		BeforeAfterCatchUp(list2);
		if (CheckPlans && !Settings.HddMode)
		{
			foreach (GameState item5 in list2)
			{
				if (item5.Engine != null && item5.Status != GameStatus.Unsupported)
				{
					GameRecord r = Store.LoadGame(item5.Game.Id);
					if (NeedsPlanCheck(item5.Game, r, item5.Engine))
					{
						CheckPlan(item5.Game.Id, OlderPlanner(item5.Game, r, item5.Engine));
					}
				}
			}
		}
		if (flag2)
		{
			ReleaseMemory();
		}
		if (ManageRecorders)
		{
			ReconcileRecorders();
			if (!item3)
			{
				CleanUninstalled(item.Select((Game game) => Store.GameDir(game.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase));
			}
			list2 = Games.ToList();
		}
		lock (_scanLock)
		{
			bool flag3 = userRequested && TakeUserFetch();
			StartCommunitySync(list2, flag3);
			Task<bool> lists = StartStutterUpdate(flag3);
			if (userRequested)
			{
				ScsKiller scsKiller = this;
				Task<ServerCheck> serverRefresh;
				if (flag3)
				{
					serverRefresh = Fetched(lists, CommunitySync);
				}
				else
				{
					serverRefresh = (ServerRefresh.IsCompleted ? Task.FromResult(ServerCheck.TooSoon) : ServerRefresh);
				}
				scsKiller.ServerRefresh = serverRefresh;
			}
		}
		StartSharing(list2.Select((GameState s) => s.Game));
		StartMigration(list2);
		ActiveCheck active = ActiveCheck;
		if (active != null)
		{
			ActiveCheckSent = Task.Run(() => active.SendAsync());
		}
		RedetectInBackground();
		SaveDllReads();
		KeepList();
		RequestCollect();
		return list2;
		bool Empty()
		{
			lock (_lock)
			{
				return _listed == 0L && _games.Count == 0;
			}
		}
	}

	private IReadOnlyList<GameState> Listed(long started, long discovering, List<GameState> kept, List<Game> before)
	{
		(List<Game> Found, Dictionary<string, Exception> Unresolved, bool Failed) tuple = Discover(before, (IGameSource gameSource) =>
		{
			Store store = gameSource.Store;
			return ((store != SCSKiller.Core.Store.Steam && store != SCSKiller.Core.Store.Xbox && store != SCSKiller.Core.Store.Manual) || 1 == 0) ? before.Where((Game g) => g.Store == gameSource.Store).ToList() : gameSource.Discover();
		});
		List<Game> item = tuple.Found;
		Dictionary<string, Exception> item2 = tuple.Unresolved;
		Dictionary<string, GameState> dictionary = kept.DistinctBy((GameState gameState) => gameState.Game.Id).ToDictionary((GameState gameState) => gameState.Game.Id);
		GpuSnapshot s = Snapshot();
		List<GameState> list = new List<GameState>();
		List<long> list2 = new List<long>();
		List<bool> list3 = new List<bool>();
		List<long> list4 = list2;
		List<GameState> list5 = list;
		foreach (Game item3 in Visible(item).DistinctBy((Game g) => g.Id).OrderBy((Game g) => g.Name, StringComparer.OrdinalIgnoreCase))
		{
			list4.Add(Ticket());
			if (item2.TryGetValue(item3.Id, out var value))
			{
				list5.Add(Unread(item3, value, out var driverStale));
				list3.Add(driverStale);
				continue;
			}
			GameState valueOrDefault = dictionary.GetValueOrDefault(item3.Id);
			if ((object)valueOrDefault != null && valueOrDefault.Game == item3 && !IsUnchecked(valueOrDefault))
			{
				GameRecord gameRecord = Store.LoadGame(item3.Id);
				list5.Add(valueOrDefault with
				{
					Playing = IsPlaying(item3.Id),
					NewShadersOverride = gameRecord.NewShaders,
					RtSkipped = !RayTracing(gameRecord),
					RayTracingOverride = gameRecord.RayTracing,
					LeftOutOfAddAll = gameRecord.LeftOutOfAddAll
				});
				list3.Add(gameRecord.WarmedAt.HasValue && !CurrentDriver(s, gameRecord.WarmedDriverId, gameRecord.WarmedDriverVersion));
			}
			else
			{
				list5.Add(Evaluate(item3, force: false, out var _, out var driverStale2));
				list3.Add(driverStale2);
			}
		}
		lock (_lock)
		{
			Publish(started, discovering, Newest(list5, list4, list3));
			list5 = list5.Where((GameState gameState) => !Unlisted(gameState.Game.Id)).ToList();
		}
		foreach (GameState item4 in list5)
		{
			GameChanged?.Invoke(item4);
		}
		BeforeAfterCatchUp(list5);
		if (ManageRecorders)
		{
			foreach (GameState item5 in list5.Where((GameState gameState) => Frames11(gameState.Engine, Vendor.Caps)))
			{
				try
				{
					ReconcileRecorders(item5.Game.Id);
				}
				catch (Exception ex)
				{
					Log?.Report(item5.Game.Name + ": reconciling the recorder failed: " + ex.Message);
				}
			}
		}
		StartStutterUpdate(force: false);
		ActiveCheck active = ActiveCheck;
		if (active != null)
		{
			ActiveCheckSent = Task.Run(() => active.SendAsync());
		}
		KeepList();
		return list5;
	}

	private void KeepList()
	{
		lock (_keepLock)
		{
			try
			{
				Store.SaveList(new KeptList(CoreBuild, DriverId, Games.ToList()));
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				Log?.Report("couldn't save the list of games: " + ex.Message);
			}
		}
	}

	private (List<Game> Found, Dictionary<string, Exception> Unresolved, bool Failed) Discover(List<Game> before, Func<IGameSource, IReadOnlyList<Game>> list, bool fresh = false)
	{
		Dictionary<string, ShippingPick> picks = (fresh ? new Dictionary<string, ShippingPick>() : Store.LoadShippingPicks(CoreBuild));
		Dictionary<string, ShippingPick> picked = new Dictionary<string, ShippingPick>();
		List<Game> found = new List<Game>();
		bool item = false;
		HashSet<string> claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, Game> known = before.DistinctBy((Game game2) => game2.Id).ToDictionary((Game game2) => game2.Id);
		foreach (IGameSource source in _sources)
		{
			if (source is SteamSource steamSource)
			{
				SteamSource steamSource2 = steamSource;
				IReadOnlyDictionary<string, Game> known2 = known;
				IProgress<string> log = Log;
				steamSource2.Known = known2;
				steamSource.Log = log;
			}
			else if (source is XboxSource xboxSource)
			{
				xboxSource.Known = known;
			}
			else if (source is ManualSource manualSource)
			{
				manualSource.Known = known;
			}
		}
		foreach (IGameSource source2 in _sources)
		{
			try
			{
				Progress($"Scanning {SourceName(source2)}… {found.Count} games found");
				if (source2 is ExoLibrarySource exoLibrarySource)
				{
					exoLibrarySource.Covered = found.ToList();
				}
				IReadOnlyList<Game> readOnlyList = list(source2);
				foreach (Game g in readOnlyList)
				{
					if (g.Store != SCSKiller.Core.Store.Manual || (source2 is ManualSource manualSource2 && manualSource2.InFolder(g.Id)))
					{
						continue;
					}
					Game game = found.FirstOrDefault((Game f) => GameFiles.Inside(f.InstallDir, g.ExePath));
					if ((object)game == null)
					{
						continue;
					}
					foreach (Game item2 in new Game[2]
					{
						g,
						Shipping(g)
					}.Distinct())
					{
						AdoptManual(item2, game);
					}
				}
				List<Game> list2 = (from game2 in readOnlyList.Select(Shipping).ToList()
					where !Claims(game2).Any(claimed.Contains) && (game2.Store != SCSKiller.Core.Store.Manual || !found.Any((Game f) => GameFiles.Inside(f.InstallDir, game2.ExePath)))
					select game2).ToList();
				found.AddRange(list2);
				claimed.UnionWith(list2.SelectMany(Claims));
			}
			catch (Exception ex)
			{
				item = true;
				Log?.Report($"{source2.Store}: discovery failed: {ex.Message}");
			}
		}
		if (found.Count != known.Count || found.Any((Game game2) => known.GetValueOrDefault(game2.Id) != game2) || picked.Count != picks.Count || picked.Any((KeyValuePair<string, ShippingPick> p) => picks.GetValueOrDefault(p.Key) != p.Value))
		{
			try
			{
				Store.SaveDiscovered(CoreBuild, found, picked);
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
			{
				Log?.Report("couldn't save the games found: " + ex2.Message);
			}
		}
		Dictionary<string, Exception> unresolved = new Dictionary<string, Exception>();
		found = found.Select((Game game2) =>
		{
			try
			{
				return Following(game2);
			}
			catch (Exception ex3) when (!(ex3 is OperationCanceledException))
			{
				unresolved[game2.Id] = ex3;
				string runsExe = Store.LoadGame(game2.Id).RunsExe;
				return (runsExe != null && !LeavesUnreal(game2.ExePath, runsExe)) ? game2 with
				{
					ExePath = runsExe
				} : game2;
			}
		}).ToList();
		return (Found: found, Unresolved: unresolved, Failed: item);
		static IEnumerable<string> Claims(Game game2)
		{
			return new string[2]
			{
				game2.InstallDir,
				Collections.AppDir(game2)
			}.Select(GameFiles.DirKey);
		}
		Game Shipping(Game game2)
		{
			if (GameFiles.IsShipping(game2.ExePath))
			{
				return game2;
			}
			string text = RootStamp(game2);
			ShippingPick shippingPick;
			if (text != null)
			{
				shippingPick = picks.GetValueOrDefault(game2.Id);
				if ((object)shippingPick != null && !(shippingPick.Named != game2.ExePath) && !(shippingPick.Stamp != text) && (!(shippingPick.Exe != game2.ExePath) || File.Exists(shippingPick.Exe)))
				{
					goto IL_0093;
				}
			}
			shippingPick = new ShippingPick(game2.ExePath, text, ShippingExe(game2.InstallDir, game2.ExePath));
			goto IL_0093;
			IL_0093:
			picked[game2.Id] = shippingPick;
			if (!(shippingPick.Exe != game2.ExePath))
			{
				return game2;
			}
			return game2 with
			{
				ExePath = shippingPick.Exe
			};
		}
	}

	private Game Unreal(Game g)
	{
		string text = ShippingExe(g.InstallDir, g.ExePath);
		if (!(text != g.ExePath))
		{
			return g;
		}
		return g with
		{
			ExePath = text
		};
	}

	private string ShippingExe(string installDir, string exe)
	{
		return ShippingLookup?.Invoke(installDir, exe) ?? GameFiles.GameExe(installDir, exe, (string m) =>
		{
			Log?.Report(m);
		});
	}

	private static string? RootStamp(Game g)
	{
		try
		{
			string text = Stamp(g.Version, GameFiles.DirKey(g.InstallDir));
			return (text != null) ? (text + GameFiles.EacSettingsStamp(g.ExePath)) : null;
		}
		catch (ArgumentOutOfRangeException)
		{
			return null;
		}
	}

	private void SaveDllReads()
	{
		try
		{
			Middleware.SaveImages(ImagesFile);
			ReShade.SaveProbes(ProbesFile);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			Log?.Report("couldn't save what was read of the games' DLLs: " + ex.Message);
		}
	}

	private Task<bool> StartStutterUpdate(bool force)
	{
		RouteFailover routes = ContentRoutes;
		if (routes == null)
		{
			return Task.FromResult(result: true);
		}
		lock (_scanLock)
		{
			DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(StutterFile);
			DateTime dateTime = ((lastWriteTimeUtc > _stutterTried) ? lastWriteTimeUtc : _stutterTried);
			if (!force && (!StutterUpdate.IsCompleted || DateTime.UtcNow - dateTime < TimeSpan.FromHours(20)))
			{
				return Task.FromResult(result: true);
			}
			_stutterTried = DateTime.UtcNow;
			return (Task<bool>)(StutterUpdate = StutterUpdate.ContinueWith(async (Task _) =>
			{
				StutterList list = await StutterList.FetchAsync(StutterFile, routes);
				if (list != null)
				{
					UseStutterList(list);
				}
				ConfirmedEngines confirmedEngines = await ConfirmedEngines.FetchAsync(ConfirmedFile, routes);
				if (confirmedEngines != null)
				{
					UseConfirmedEngines(confirmedEngines);
				}
				BootPrecompiles bootPrecompiles = await BootPrecompiles.FetchAsync(PrecompilesFile, routes);
				if (bootPrecompiles != null)
				{
					UseBootPrecompiles(bootPrecompiles);
				}
				(string? Token, IReadOnlyCollection<string>? Ent)? access = null;
				try
				{
					(string, IReadOnlyCollection<string>) value = ((ChannelAccess != null) ? (await ChannelAccess()) : (null, null));
					access = value;
				}
				catch (Exception)
				{
				}
				(string[] Rules, string[] News)? read;
				lock (_channelLock)
				{
					if (access.HasValue)
					{
						(string, IReadOnlyCollection<string>) valueOrDefault = access.GetValueOrDefault();
						_channelEnt = (Known: true, Ent: valueOrDefault.Item2);
					}
					read = ChannelsRead();
				}
				GameVerdicts gameVerdicts = await RulesTrust.FetchAsync("game-verdicts", GameVerdicts.TryParse, VerdictsFile, read?.News, access?.Token, routes);
				UseVerdicts(gameVerdicts ?? GameVerdicts.Embedded);
				MaximumMode maximumMode = await RulesTrust.FetchAsync("maximum-mode", MaximumMode.TryParse, MaximumFile, read?.News, access?.Token, routes);
				UseMaximum(maximumMode ?? MaximumMode.Embedded);
				if (CompatKeys.Count > 0)
				{
					CompatFeed feed = await CompatFeed.FetchAsync(CompatTrust, CompatFile, routes);
					UseCompat(feed);
				}
				RulesFile rules = await RulesFile.FetchAsync(RulesPath, RulesTrust, routes, access?.Token, read?.Rules);
				News news = await SCSKiller.Core.App.News.FetchAsync(NewsPath, RulesTrust, routes, access?.Token, read?.News);
				lock (_channelLock)
				{
					if (_channelEnt.Known)
					{
						ApplyChannels(always: true);
					}
					else
					{
						UseRules(rules);
						UseNews(news);
					}
				}
				return list != null;
			}, TaskScheduler.Default).Unwrap());
		}
	}

	private void UseConfirmedEngines(ConfirmedEngines list)
	{
		ConfirmedEngines.Current = list;
		lock (_refreshLock)
		{
			foreach (GameState game in Games)
			{
				if (game.StatusReason.Contains("not tested on this engine version yet") && RootSig.Verified(game.Engine))
				{
					Refresh(game.Game);
				}
			}
		}
	}

	private void UseBootPrecompiles(BootPrecompiles list)
	{
		BootPrecompiles current = BootPrecompiles.Current;
		BootPrecompiles.Current = list;
		lock (_refreshLock)
		{
			foreach (GameState game in Games)
			{
				if (list.Has(game.Game) != current.Has(game.Game))
				{
					Refresh(game.Game);
				}
			}
		}
	}

	private void UseVerdicts(GameVerdicts list)
	{
		GameVerdicts was = GameVerdicts.Current;
		GameVerdicts.Current = list;
		List<string> changed;
		lock (_refreshLock)
		{
			changed = (from s in Games
				where list.NoStutter(s.Game, s.Engine) != was.NoStutter(s.Game, s.Engine) || list.Unsupported(s.Game, s.Engine) != was.Unsupported(s.Game, s.Engine)
				select s.Game.Id).ToList();
			foreach (GameState item in Games.Where((GameState s) => changed.Contains(s.Game.Id)))
			{
				Refresh(item.Game);
			}
		}
		if (ManageRecorders)
		{
			foreach (string item2 in changed)
			{
				ReconcileRecorders(item2);
			}
		}
		if (changed.Count > 0)
		{
			KeepList();
		}
	}

	private void UseMaximum(MaximumMode list)
	{
		MaximumMode was = MaximumMode.Current;
		MaximumMode.Current = list;
		string vendor = PackGpu(Vendor.Vendor);
		List<GameState> list2;
		lock (_refreshLock)
		{
			list2 = Games.Where((GameState s) => list.For(s.Game, vendor) != was.For(s.Game, vendor)).ToList();
			foreach (GameState item in list2)
			{
				Refresh(item.Game);
			}
		}
		if (list2.Count > 0)
		{
			KeepList();
		}
	}

	private void UseCompat(CompatFeed? feed)
	{
		_compat = feed;
		List<GameState> list;
		lock (_refreshLock)
		{
			list = Games.Where((GameState s) => SimilarOf(s.Game) != s.Similar).ToList();
			foreach (GameState item in list)
			{
				Refresh(item.Game);
			}
		}
		if (list.Count > 0)
		{
			KeepList();
		}
	}

	private SimilarSystems? SimilarOf(Game g)
	{
		return _compat?.For(g, PackGpu(Vendor.Vendor), GpuModel());
	}

	private static string? NoStutterOf(Game g, EngineInfo? engine, KnownStutter? known)
	{
		if (!(known == null))
		{
			return null;
		}
		return GameVerdicts.Current.NoStutter(g, engine);
	}

	public void UseChannels(IReadOnlyCollection<string>? ent)
	{
		lock (_channelLock)
		{
			_channelEnt = (Known: true, Ent: ent);
			ApplyChannels(always: false);
		}
	}

	private (string[] Rules, string[] News)? ChannelsRead()
	{
		(bool, IReadOnlyCollection<string>) channelEnt = _channelEnt;
		if (channelEnt.Item1)
		{
			IReadOnlyCollection<string> item = channelEnt.Item2;
			return (RulesFile.Channels(item), SCSKiller.Core.App.News.Channels(UpdateChannels.Effective(Settings.UpdateChannel, AppVersion.Current.Channel, item)));
		}
		return null;
	}

	private void ApplyChannels(bool always)
	{
		(string[], string[])? tuple = ChannelsRead();
		if (tuple.HasValue)
		{
			(string[], string[]) valueOrDefault = tuple.GetValueOrDefault();
			if (always || _rulesRead == null || !valueOrDefault.Item1.SequenceEqual(_rulesRead))
			{
				UseRules(RulesFile.Cached(RulesPath, RulesTrust, _rulesRead = valueOrDefault.Item1));
			}
			if (always || _newsRead == null || !valueOrDefault.Item2.SequenceEqual(_newsRead))
			{
				UseNews(SCSKiller.Core.App.News.Cached(NewsPath, RulesTrust, _newsRead = valueOrDefault.Item2));
				UseVerdicts(RulesTrust.Cached("game-verdicts", GameVerdicts.TryParse, VerdictsFile, valueOrDefault.Item2) ?? GameVerdicts.Embedded);
				UseMaximum(RulesTrust.Cached("maximum-mode", MaximumMode.TryParse, MaximumFile, valueOrDefault.Item2) ?? MaximumMode.Embedded);
			}
		}
	}

	private void UseNews(News? news)
	{
		News = news ?? SCSKiller.Core.App.News.Empty;
		NewsChanged?.Invoke();
	}

	private void UseRules(RulesFile? server)
	{
		Dictionary<string, string> dictionary = Games.Where((GameState s) => s.Engine != null).ToDictionary((GameState s) => s.Game.Id, (GameState s) => RulesFile.Current.Hash(s.Engine));
		RulesFile.Current = ((server == null) ? RulesFile.Embedded : RulesFile.WithServer(server));
		lock (_refreshLock)
		{
			foreach (GameState game in Games)
			{
				if (dictionary.TryGetValue(game.Game.Id, out var value) && value != RulesFile.Current.Hash(game.Engine))
				{
					Refresh(game.Game);
					if (CheckPlans && !Settings.HddMode && game.Status != GameStatus.Unsupported && NeedsPlanCheck(game.Game, Store.LoadGame(game.Game.Id), game.Engine))
					{
						CheckPlan(game.Game.Id);
					}
				}
			}
		}
	}

	private void UseStutterList(StutterList list)
	{
		List<GameState> list2 = new List<GameState>();
		lock (_lock)
		{
			StutterList.Current = list;
			for (int i = 0; i < _games.Count; i++)
			{
				KnownStutter knownStutter = list.Find(_games[i].Game);
				if (knownStutter != _games[i].KnownStutter)
				{
					List<GameState> games = _games;
					int index = i;
					GameState gameState = _games[i]with
					{
						KnownStutter = knownStutter
					};
					GameState item = gameState;
					games[index] = gameState;
					list2.Add(item);
				}
			}
		}
		foreach (GameState item2 in list2)
		{
			GameChanged?.Invoke(item2);
		}
		List<Game> list3 = (from s in list2
			where NoStutterOf(s.Game, s.Engine, s.KnownStutter) != s.NoStutter
			select s.Game).ToList();
		lock (_refreshLock)
		{
			foreach (Game item3 in list3)
			{
				Refresh(item3);
			}
		}
		if (ManageRecorders)
		{
			foreach (Game item4 in list3)
			{
				ReconcileRecorders(item4.Id);
			}
		}
		if (list2.Count > 0)
		{
			KeepList();
		}
	}

	private GameState Evaluate(Game g, bool force, out bool fresh, out bool driverStale, CancellationToken ct = default(CancellationToken))
	{
		if (Settings.HddMode && !_asked.ContainsKey(g.Id) && !Checked(g))
		{
			fresh = false;
			driverStale = false;
			return Unchecked(g, "not checked yet: open it to check");
		}
		try
		{
			GameState result = EvaluateGame(g, force, out fresh, out driverStale, ct);
			_unread.TryRemove(g.Id, out var _);
			return result;
		}
		catch (Exception ex) when (!(ex is OperationCanceledException))
		{
			fresh = false;
			return Unread(g, ex, out driverStale);
		}
	}

	private GameState Unread(Game g, Exception e, out bool driverStale)
	{
		string text = Codecs.Reason(e);
		Log?.Report(g.Name + ": couldn't be read: " + ((text == null) ? e.Message : e.ToString()));
		_unread[g.Id] = true;
		string statusReason = text ?? ("couldn't read its files: " + e.Message);
		GameState gameState2;
		lock (_lock)
		{
			GameState? gameState = _games.FirstOrDefault((GameState s) => s.Game.Id == g.Id);
			bool valueOrDefault = _driverStale.GetValueOrDefault(g.Id);
			gameState2 = gameState;
			driverStale = valueOrDefault;
		}
		if (!(gameState2 != null))
		{
			return new GameState(g, null, AntiCheat.None, GameStatus.Unsupported, statusReason, null, null, null, null, null, null, null, RecorderInstalled: false, null, null, null, null, null, null, null, null, null, null, null, Playing: false, RecorderOverride.Default, RecorderEffective: false, RecorderForeign: false, null, null, null, null, RecordAlongsideMod: false, null, null, null, null, 0L, RecordingPaused: false, 0L)
			{
				RecorderSkip = "not supported yet"
			};
		}
		return gameState2 with
		{
			Game = g,
			Status = GameStatus.Unsupported,
			StatusReason = statusReason,
			NotPlanned = false
		};
	}

	public static bool IsUnchecked(GameState s)
	{
		string statusReason = s.StatusReason;
		if (statusReason == "checking its files" || statusReason == "not checked yet: open it to check")
		{
			return true;
		}
		return false;
	}

	private bool Checked(Game g)
	{
		lock (_scanLock)
		{
			if ((_scan ?? (_scan = Store.LoadScan())).ContainsKey(g.Id))
			{
				return true;
			}
		}
		return RecorderOnRecord(Store.LoadGame(g.Id));
	}

	private static bool RecorderOnRecord(GameRecord rec)
	{
		if (rec.RecorderFiles.Count <= 0 && !(rec.RecorderChained != null) && rec.RecorderExe == null)
		{
			return rec.RecorderMoveFrom != null;
		}
		return true;
	}

	private GameState Unchecked(Game g, string reason)
	{
		KnownStutter knownStutter = StutterList.Current.Find(g);
		bool playing = IsPlaying(g.Id);
		return new GameState(g, null, AntiCheat.None, GameStatus.Unsupported, reason, null, null, null, null, null, null, null, RecorderInstalled: false, null, null, null, null, null, null, knownStutter, null, null, null, null, playing, RecorderOverride.Default, RecorderEffective: false, RecorderForeign: false, null, null, null, null, RecordAlongsideMod: false, null, null, null, null, 0L, RecordingPaused: false, 0L)
		{
			RecorderSkip = "not supported yet"
		};
	}

	public void CheckGame(string gameId)
	{
		GameState gameState = Games.FirstOrDefault((GameState s) => s.Game.Id == gameId);
		if ((object)gameState != null && (IsUnchecked(gameState) || Settings.HddMode))
		{
			RefreshAsked(gameState.Game);
		}
	}

	private void RefreshAsked(Game g)
	{
		_asked[g.Id] = true;
		try
		{
			Refresh(g, default, force: false, prompt: true);
		}
		finally
		{
			_asked.TryRemove(g.Id, out var _);
		}
	}

	private GameState EvaluateGame(Game g, bool force, out bool fresh, out bool driverStale, CancellationToken ct)
	{
		GameRecord gameRecord = Store.LoadGame(g.Id);
		GpuSnapshot gpuSnapshot = Snapshot();
		string exeDir = Path.GetDirectoryName(g.ExePath);
		if (gameRecord.IniIsUsers && (gameRecord.RecorderExe ?? gameRecord.RecorderMoveFrom ?? gameRecord.OfflineSession?.Exe) == null && Directory.Exists(exeDir))
		{
			SetIni(Store, exeDir, (IniOwner? e) => e ?? new IniOwner(exeDir, null, Users: true));
			gameRecord.IniIsUsers = false;
			Store.SaveGame(g.Id, gameRecord);
		}
		int? num = CarefulThreads(Vendor.Vendor);
		LaunchCheck firstLaunch = gameRecord.FirstLaunch;
		int num2;
		if ((object)firstLaunch != null)
		{
			DateTimeOffset at = firstLaunch.At;
			DateTimeOffset? warmedAt = gameRecord.WarmedAt;
			num2 = ((at < warmedAt) ? 1 : 0);
		}
		else
		{
			num2 = 0;
		}
		bool flag = (byte)num2 != 0;
		if (flag)
		{
			gameRecord.FirstLaunch = null;
		}
		bool flag2 = Vendor.Vendor == GpuVendor.Nvidia;
		DateTimeOffset? firstAfter = (((num.HasValue | flag2) && gameRecord.FirstLaunch == null) ? gameRecord.WarmedAt : ((DateTimeOffset?)null));
		IReadOnlySet<string> rayQuery = (flag2 ? SessionLog.ReadRayQueryKeys(RayQueryKeysPath(g.Id)) : null);
		string text = SessionDir(g);
		FrameReport frameReport = Frames(text, Path.GetFileName(g.ExePath), rayQuery, gameRecord.LastPlay);
		var (sessionStats, marker, launchCheck) = SessionLog.Read(Path.Combine(text, "scskiller_creates.csv"), out bool framesMatch, out AgsRegistration ags, Path.GetFileName(g.ExePath), firstAfter, 100L, rayQuery, gameRecord.LastPlay, frameReport);
		if (sessionStats != null && !framesMatch)
		{
			frameReport = null;
		}
		if (frameReport != null)
		{
			try
			{
				double? num3 = PairCapMs(gameRecord, frameReport);
				FrameReport frameReport2;
				if (num3.HasValue)
				{
					double valueOrDefault = num3.GetValueOrDefault();
					frameReport2 = FrameLog.Read(Path.Combine(text, "scskiller_frames.bin"), Path.Combine(text, "scskiller_creates.csv"), Path.GetFileName(g.ExePath), rayQuery, gameRecord.LastPlay, valueOrDefault);
				}
				else
				{
					frameReport2 = frameReport;
				}
				FrameReport frameReport3 = frameReport2;
				if (frameReport3?.LaunchUnixMs == frameReport.LaunchUnixMs)
				{
					Snapshots.Write(Store, g.Id, Snapshots.Of(frameReport3, gameRecord.LastPlay, gpuSnapshot.Id, gpuSnapshot.Gpu.DriverVersion, g.Version ?? ExeStamp(g), gameRecord.WarmedAt));
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
			{
				Log?.Report(g.Name + ": couldn't save the session snapshot: " + ex.Message);
			}
		}
		if (frameReport != null)
		{
			NoteFrames(g, frameReport);
		}
		if (launchCheck != null && !flag2)
		{
			gameRecord.FirstLaunch = launchCheck;
		}
		bool rtUnseen = gameRecord.RtUnseen;
		bool recordedLong = gameRecord.RecordedLong;
		bool flag3 = rtUnseen;
		gameRecord.RtUnseen = RtUnseen(sessionStats, gameRecord.RtUnseen);
		gameRecord.RecordedLong |= sessionStats?.Duration >= EnoughRecording;
		if (launchCheck != null && AgsLaunchMissed(gameRecord))
		{
			gameRecord.AgsMissed = true;
			Log?.Report(g.Name + ": " + AgsMissedReason(gameRecord));
		}
		string keysIndexHash = gameRecord.KeysIndexHash;
		bool keysPending = gameRecord.KeysPending;
		bool flag4 = false;
		bool flag5 = keysPending;
		bool flag6 = false;
		try
		{
			if (flag5 || (keysIndexHash != null && !IndexIsInstalled(g, gameRecord)))
			{
				WriteKeys(g, gameRecord, ct);
			}
			flag6 = ImportRecording(g, gameRecord, migrate: false, ct);
			flag4 = true;
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
		}
		catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException || ex3 is InvalidDataException) ? true : false)
		{
			Log?.Report(g.Name + ": the recording's upkeep failed: " + ex3.Message);
		}
		if (((launchCheck != null) & flag2) && !flag4)
		{
			launchCheck = null;
		}
		else if ((launchCheck != null) & flag2)
		{
			gameRecord.FirstLaunch = launchCheck;
			(int, double)? tuple2 = WarmedCreates(g, gameRecord, launchCheck, Path.Combine(text, "scskiller_creates.csv"), rayQuery, gpuSnapshot);
			if (tuple2.HasValue)
			{
				(int, double) valueOrDefault2 = tuple2.GetValueOrDefault();
				if (Unreached(valueOrDefault2))
				{
					gameRecord.UnreachedWarm = gameRecord.WarmedAt;
					Log?.Report($"{g.Name}: {"the compile didn't help this game on NVIDIA, it needs a custom loader"} ({valueOrDefault2.Item1:N0} warmed pipelines at its first launch, median {valueOrDefault2.Item2:0} ms)");
				}
			}
		}
		try
		{
			if (flag6 | MergeLaunched(g, gameRecord, marker) | MergeAgsSeen(g, gameRecord, ags) | AdoptDriverId(gameRecord, gpuSnapshot) | (launchCheck != null) | flag | (gameRecord.KeysIndexHash != keysIndexHash) | (gameRecord.KeysPending != flag5) | (gameRecord.RtUnseen != flag3) | (gameRecord.RecordedLong != recordedLong))
			{
				Store.SaveGame(g.Id, gameRecord);
			}
		}
		catch (Exception ex4) when ((ex4 is IOException || ex4 is UnauthorizedAccessException) ? true : false)
		{
			Log?.Report(g.Name + ": couldn't save its record: " + ex4.Message);
		}
		long? num4 = PlanItems(gameRecord.Plan?.Stats);
		long? recorded = gameRecord.Plan?.Stats.Recorded;
		TimeSpan? timeSpan;
		if (num4.HasValue)
		{
			long valueOrDefault3 = num4.GetValueOrDefault();
			timeSpan = TimeSpan.FromSeconds((double)valueOrDefault3 / (gameRecord.PsoPerSecond ?? WarmRate));
		}
		else
		{
			timeSpan = null;
		}
		TimeSpan? timeSpan2 = timeSpan;
		TimeSpan? timeSpan3;
		if (num.HasValue && num4.HasValue)
		{
			long valueOrDefault4 = num4.GetValueOrDefault();
			if (recorded.HasValue)
			{
				long valueOrDefault5 = recorded.GetValueOrDefault();
				timeSpan3 = TimeSpan.FromSeconds((double)valueOrDefault5 / 67.0 + (double)(valueOrDefault4 - valueOrDefault5) / (gameRecord.PsoPerSecond ?? WarmRate));
				goto IL_085d;
			}
		}
		timeSpan3 = null;
		goto IL_085d;
		IL_1436:
		(GameStatus, string) tuple4;
		(GameStatus, string) tuple3 = tuple4;
		GameStatus item = tuple3.Item1;
		string item2 = tuple3.Item2;
		bool flag7 = IsPlaying(g.Id);
		GameState gameState = null;
		if (flag7)
		{
			lock (_lock)
			{
				gameState = _games.Find((GameState x) => x.Game.Id == g.Id);
			}
		}
		Game game = g;
		int? shaderCount = ((Collections.Member(g.Id).HasValue && !Indexed(g, gameRecord)) ? ((int?)null) : gameRecord.ShaderCount);
		PlanStats plan = gameRecord.Plan?.Stats;
		EngineInfo engineInfo;
		long? estimatedCacheBytes = num4 * (long)(gameRecord.BytesPerPso ?? DefaultBytesPerPsoOf(engineInfo));
		TimeSpan? timeSpan4;
		TimeSpan? estimatedWarmTime = ((gameRecord.Careful && timeSpan4.HasValue) ? timeSpan4 : timeSpan2);
		string? warmedDriverVersion = gameRecord.WarmedDriverVersion;
		DateTimeOffset? warmedAt2 = gameRecord.WarmedAt;
		TimeSpan? lastWarmTime = gameRecord.LastWarmTime;
		bool flag8;
		bool recorderInstalled = flag8;
		SessionStats? lastSession = ((!flag7) ? sessionStats : gameState?.LastSession);
		long? cacheOnDisk = ((gameRecord.CacheKeys.Count > 0 && AppCache != null) ? new long?(AppCache.SizeOf(gameRecord.CacheKeys)) : ((long?)null));
		string? launchedExeName = gameRecord.LaunchedExeName;
		AntiCheat antiCheat;
		List<MiddlewareTag> middleware = ((antiCheat == AntiCheat.None) ? Middleware.Tags(Middleware.Detect(exeDir), (MiddlewareDll d) => SeedsPacks(g) ? ((_planner as Planner)?.PackPipelines(d, OnAmd) ?? 0) : 0) : null);
		long? lastWarmFailed = gameRecord.LastWarmFailed;
		long? lastWarmSkipped = gameRecord.LastWarmSkipped;
		KnownStutter? knownStutter = StutterList.Current.Find(g);
		CommunityDownload communityDownload = CommunityKept(g.Id);
		CommunityInfo community = (((object)communityDownload != null) ? new CommunityInfo(communityDownload.Psos, communityDownload.DownloadedAt, File.Exists(RecordingPath(g.Id)), OtherVersion(communityDownload, g.Id, gameRecord)) : null);
		DateTimeOffset? recordingSharedAt = SCSKiller.Core.App.Sharing.Shared(Store.GameDir(g.Id))?.At;
		Pending value;
		long? planned = value.Planned;
		bool playing = flag7;
		long recordingBytes = 0L;
		long recordedSinceWarm = 0L;
		long? lastWarmUnbuilt = null;
		bool? flag9;
		bool flag10;
		string apiDetected;
		CommunityEntry communityEntry;
		ReShadeInstall reShadeInstall;
		ReShadeAddon reShadeAddon;
		bool flag11;
		bool flag12;
		bool flag13;
		bool flag14;
		bool flag15;
		PlanCheck planCheck;
		bool flag16;
		bool flag17;
		GameState gameState2 = new GameState(game, engineInfo, antiCheat, item, item2, shaderCount, plan, estimatedCacheBytes, estimatedWarmTime, warmedDriverVersion, warmedAt2, lastWarmTime, recorderInstalled, lastSession, cacheOnDisk, launchedExeName, middleware, lastWarmFailed, lastWarmSkipped, knownStutter, community, recordingSharedAt, flag9, planned, playing, RecorderOverride.Default, RecorderEffective: false, RecorderForeign: false, null, null, null, null, RecordAlongsideMod: false, null, null, null, null, recordingBytes, RecordingPaused: false, recordedSinceWarm, 0, null, null, ReShade: false, null, ShaderModBlocks: false, ShaderModLayer: false, ShaderModAsD3D12: false, RootUnconfirmed: false, StreamlineFirst: false, AgsHost: false, Vac: false, Records11: false, RtUnseen: false, RtToPlan: false, RtSkipped: false, null, LeftOutOfAddAll: false, RecordedEnough: false, OfflineEligible: false, OfflineRecord: false, OfflineRunning: false, null, null, CompileUnreached: false, NotPlanned: false, NewOnly: false, null, null, null, null, null, null, null, null, CompileFailed: false, RiskEligible: false, RiskNoLaunch: false, null, null, null, lastWarmUnbuilt) with
		{
			Kept = KeptState(g.Id),
			LastWarmNeedsRecording = gameRecord.LastWarmNeedsRecording,
			LastWarmCrashed = gameRecord.LastWarmCrashed,
			LastWarmDriverSkipped = gameRecord.LastWarmDriverSkipped,
			LastWarmUnbuilt = gameRecord.LastWarmUnbuilt,
			Careful = (num.HasValue ? new CarefulCompile(gameRecord.Careful, gameRecord.FirstLaunch?.Compiled, timeSpan4, recorded) : null),
			NewShadersOverride = gameRecord.NewShaders,
			RtSkipped = flag10,
			RayTracingOverride = gameRecord.RayTracing,
			LeftOutOfAddAll = gameRecord.LeftOutOfAddAll,
			ApiOverride = gameRecord.ApiOverride,
			ApiDetected = apiDetected,
			RecordedSinceWarm = value.Recorded,
			NewOnly = (item == GameStatus.Stale && !value.Unknown && WarmChanged(g, gameRecord, engineInfo, gpuSnapshot) == null),
			CommunityDbPsos = (communityEntry?.Psos ?? 0),
			PsoPerSecond = gameRecord.PsoPerSecond,
			Official = ((OfficialInUse(g.Id) is { } communityDownload2) ? new CommunityInfo(communityDownload2.Psos, communityDownload2.DownloadedAt, File.Exists(RecordingPath(g.Id))) : null),
			LastFrames = ((!flag7) ? frameReport : gameState?.LastFrames),
			ReShade = reShadeInstall != null,
			ShaderMod = reShadeAddon?.Mod,
			ShaderModBlocks = reShadeInstall?.Blocks ?? false,
			ShaderModLayer = reShadeInstall?.Layered ?? false,
			ShaderModAsD3D12 = (object)reShadeInstall != null && reShadeInstall.Layered && reShadeInstall.AsD3D12,
			RtUnseen = flag11,
			RtToPlan = flag12,
			RecordedEnough = gameRecord.RecordedLong,
			RootUnconfirmed = flag13,
			Vac = flag14,
			Records11 = As11(engineInfo, gameRecord, Vendor.Caps),
			StreamlineFirst = flag15 && engineInfo != null && planCheck.Readiness != Readiness.Unsupported,
			AgsHost = flag16,
			Dxvk = antiCheat == AntiCheat.None && ReShade.Dxvk(exeDir),
			DxvkCompiled = antiCheat == AntiCheat.None && Warmer.DxvkDll(Vendor.Vendor, g.ExePath) != null,
			NoStutter = NoStutterOf(g, engineInfo, StutterList.Current.Find(g)),
			CompileUnreached = flag17 && item != GameStatus.Unsupported,
			MaximumSuggestion = ((MaximumSuggestion(g, gameRecord) is { } suggestion) ? (suggestion.Note ?? "") : null),
			MaximumForGame = gameRecord.Maximum,
			MaximumAddsBytes = (Maximum(gameRecord) ? ((long?)null) : MaximumAdds(gameRecord.Plan?.Stats)),
			PlayedAt = ((gameRecord.WarmedGameVersion != null && gameRecord.WarmedGameVersion != g.Version) ? ((DateTimeOffset?)null) : new DateTimeOffset?[2]
			{
				gameRecord.LastPlay?.From,
				gameRecord.FirstLaunch?.At
			}.Max()),
			Similar = SimilarOf(g),
			CompileFailed = gameRecord.CompileFailedBuild != null && gameRecord.CompileFailedBuild == (g.Version ?? ""),
			NotPlanned = ((GameVerdicts.Current.Unsupported(g, engineInfo) is { } verdict) ? (verdict.Kind == "unsupported") : (item == GameStatus.Unsupported && ((object)reShadeInstall == null || !reShadeInstall.Blocks) && antiCheat != AntiCheat.None && planCheck.Readiness == Readiness.NeedsRecording))
		};
		string text2;
		bool flag18;
		return BeforeAfterNow(WithRecorder(gameState2, gameRecord, flag8, exeDir, text2, flag18), gameRecord);
		IL_13ee:
		string text3 = PartialNote(gameRecord.Plan?.Stats);
		bool rt;
		string text4;
		string text5;
		string text6;
		tuple4 = (GameStatus.Warmed, RtBlocked(rt, text4, text5 + ((text3 != null) ? ("; " + text3) : "")) + text6);
		goto IL_1436;
		IL_085d:
		timeSpan4 = timeSpan3;
		bool flag20;
		(_, engineInfo, antiCheat, planCheck, _, flag20, _, _, apiDetected, _, _) = Evaluated(g, gameRecord, force, out fresh);
		driverStale = gameRecord.WarmedAt.HasValue && !CurrentDriver(gpuSnapshot, gameRecord.WarmedDriverId, gameRecord.WarmedDriverVersion);
		PendingCount pending = gameRecord.Pending;
		value = PendingOf(g, gameRecord, engineInfo);
		if (gameRecord.Pending != pending)
		{
			try
			{
				Store.SaveGame(g.Id, gameRecord);
			}
			catch (Exception ex5) when ((ex5 is IOException || ex5 is UnauthorizedAccessException) ? true : false)
			{
				Log?.Report(g.Name + ": couldn't save its record: " + ex5.Message);
			}
		}
		flag14 = VacCheck(g);
		if (flag14 && (gameRecord.RecorderFiles.Count > 0 || gameRecord.RecorderChained != null || ProxyOnDisk(g)))
		{
			TakeOutNow(g, "VAC found");
			gameRecord = Store.LoadGame(g.Id);
		}
		if (((antiCheat != AntiCheat.None) | flag14) && !gameRecord.AgsIneligible && Vendor.Vendor == GpuVendor.Amd)
		{
			gameRecord.AgsIneligible = true;
			Log?.Report(g.Name + ": " + ((antiCheat != AntiCheat.None) ? "anti-cheat" : "VAC") + " found; the AGS app-name hook is off for it");
			try
			{
				Store.SaveGame(g.Id, gameRecord);
			}
			catch (Exception ex6) when ((ex6 is IOException || ex6 is UnauthorizedAccessException) ? true : false)
			{
				Log?.Report(g.Name + ": couldn't save its record: " + ex6.Message);
			}
		}
		if (engineInfo != null && planCheck.Reason.Contains("not tested on this engine version yet: playing with recording on improves it") && RootSig.Verified(engineInfo))
		{
			planCheck = planCheck with
			{
				Reason = planCheck.Reason.Replace("not tested on this engine version yet: playing with recording on improves it", "no recording needed")
			};
		}
		else if ((antiCheat != AntiCheat.None) | flag14)
		{
			planCheck = planCheck with
			{
				Reason = planCheck.Reason.Replace("not tested on this engine version yet: playing with recording on improves it", "not tested on this engine version yet")
			};
		}
		CommunityManifest communityManifest = LocalManifest();
		communityEntry = ((communityManifest != null) ? DbEntry(communityManifest, g, gameRecord, FullEntries) : null);
		flag9 = ((communityManifest != null) ? new bool?(communityEntry != null) : ((bool?)null));
		bool entitled = DbEntitled();
		flag12 = RtPlanCheck(gameRecord, engineInfo);
		flag10 = !RayTracing(gameRecord);
		flag11 = NeedsRtRecording(gameRecord.Plan?.Stats) && !flag12 && gameRecord.RtUnseen && !flag10;
		bool flag21 = NeedsRtRecording(gameRecord.Plan?.Stats) && !flag12 && !gameRecord.RtUnseen && !flag10;
		if (flag11)
		{
			text6 = "; no ray tracing seen while recording: turn it on in the game and play to cover it";
		}
		else
		{
			text6 = ((RtInlineCovers(gameRecord.Plan?.Stats) && !flag10) ? "; ray tracing that uses separate pipelines (such as path tracing) is compiled only from a recording" : "");
		}
		reShadeInstall = ((antiCheat == AntiCheat.None) ? ReShade.Detect(g) : null);
		reShadeAddon = reShadeInstall?.ShaderMod;
		if ((object)reShadeInstall != null && reShadeInstall.Blocks)
		{
			TakeOutNow(g, ShaderModReason(reShadeInstall), null, anyCopys: false);
			gameRecord = Store.LoadGame(g.Id);
		}
		flag16 = Vendor.Vendor == GpuVendor.Amd && antiCheat == AntiCheat.None && GameFiles.AgsMakesTheDevice(g.ExePath);
		text2 = RecorderDll(gameRecord, engineInfo, Vendor.Caps, flag16);
		string dll = Path.Combine(exeDir, text2);
		string text13 = Collections.RecordedBy(g.Id);
		flag18 = false;
		try
		{
			flag8 = IsOurProxy(dll) && !Foreign(exeDir);
		}
		catch (Exception ex7) when ((ex7 is IOException || ex7 is UnauthorizedAccessException) ? true : false)
		{
			flag8 = ((text13 != null) ? Store.LoadGame(text13) : gameRecord).RecorderFiles.ContainsKey(text2) && !Foreign(exeDir);
		}
		if (text13 != null)
		{
			bool flag22 = flag8;
			flag8 = false;
			flag18 = flag22;
		}
		flag13 = Unconfirmed(g);
		flag15 = flag20 && Vendor.Vendor == GpuVendor.Nvidia && text2 == "d3d12.dll";
		bool flag23 = ((antiCheat != AntiCheat.None) | flag14) && OfflineEntry(g, antiCheat, engineInfo) == null && RiskOffered(g, engineInfo, anyAntiCheat: true);
		object obj;
		if (!flag23)
		{
			if (antiCheat == AntiCheat.None)
			{
				if (flag14)
				{
					obj = "which VAC blocks";
				}
				else if (flag13)
				{
					obj = "once you confirm its game folder";
				}
				else
				{
					obj = (flag15 ? "which DLSS on NVIDIA blocks" : null);
				}
			}
			else
			{
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(13, 1);
				defaultInterpolatedStringHandler.AppendLiteral("which ");
				defaultInterpolatedStringHandler.AppendFormatted((antiCheat == AntiCheat.Other) ? "its anti-cheat" : ((object)antiCheat));
				defaultInterpolatedStringHandler.AppendLiteral(" blocks");
				obj = defaultInterpolatedStringHandler.ToStringAndClear() + ((OfflineEntry(g, antiCheat, engineInfo) != null) ? " unless you record an offline session (game page)" : "");
			}
		}
		else
		{
			obj = "from a session on the game page, at your own risk";
		}
		text4 = (string)obj;
		bool flag24 = ((antiCheat != AntiCheat.None) | flag14) && RiskDll(engineInfo, Vendor.Caps) == "d3d11.dll" && (OfflineEntry(g, antiCheat, engineInfo) != null || RiskOffered(g, engineInfo, anyAntiCheat: true));
		if (text4 != null && !flag24 && planCheck.Reason.EndsWith("; a 5-min recording covers those"))
		{
			string reason = planCheck.Reason;
			int num5 = "a 5-min recording covers those".Length + 2;
			planCheck = planCheck with
			{
				Reason = reason.Substring(0, reason.Length - num5)
			};
		}
		if (text4 != null)
		{
			planCheck = planCheck with
			{
				Reason = planCheck.Reason.Replace("for DirectX 12, turn on recording and play for about 5 minutes", "for DirectX 12, needs a recording, " + text4)
			};
		}
		long? rejected = Rejected(gameRecord.LastWarmFailed, gameRecord.LastWarmCrashed);
		bool flag25 = IsPartlyCompiled(rejected, num4);
		flag17 = (IsUnreached(gameRecord) & flag2) && !flag25;
		string text14 = (gameRecord.WarmedAt.HasValue ? Trimmed(g.Id, gameRecord) : null);
		if (text14 != _trimmedLogged.GetValueOrDefault(g.Id))
		{
			_trimmedLogged[g.Id] = text14;
			if (text14 != null)
			{
				IProgress<string>? log = Log;
				if (log != null)
				{
					string[] array = new string[5]
					{
						g.Name,
						": ",
						(text14 == "this game's compiled shader cache is gone, compile again") ? "its" : "part of its",
						" compiled shader cache is gone",
						null
					};
					string text15 = KeptReason(gameRecord);
					array[4] = ((text15 != null) ? (", " + text15) : "");
					log.Report(string.Concat(array));
				}
			}
		}
		Readiness readiness = planCheck.Readiness;
		GameVerdicts.Verdict verdict2 = GameVerdicts.Current.Unsupported(g, engineInfo);
		if ((object)verdict2 != null)
		{
			tuple4 = (GameStatus.Unsupported, verdict2.Text);
		}
		else if (BootPrecompiles.Current.Has(g))
		{
			tuple4 = (GameStatus.Unsupported, "builds its shaders at the main menu; in-game stutter isn't from shader compilation");
		}
		else if ((object)reShadeInstall == null || !reShadeInstall.Blocks)
		{
			if (readiness == Readiness.Unsupported)
			{
				tuple4 = (GameStatus.Unsupported, planCheck.Reason);
			}
			else if (!CantReach(g, gameRecord, gpuSnapshot))
			{
				if (readiness == Readiness.NeedsRecording)
				{
					if (OfflineEntry(g, antiCheat, engineInfo) != null && !File.Exists(RecordingPath(g.Id)) && CommunityInUse(g.Id) == null)
					{
						tuple4 = (GameStatus.NeedsRecording, "EasyAntiCheat blocks the recorder, but you can record at your own risk with an offline session.");
					}
					else if (text4 != null)
					{
						tuple4 = (flag23 ? GameStatus.NeedsRecording : GameStatus.Unsupported, "needs a recording, " + text4 + DbNote(flag9, entitled));
					}
					else if (text13 != null)
					{
						string name = Collections.Member(text13).Value.Game.Name;
						tuple4 = (GameStatus.NeedsRecording, planCheck.Reason.Replace("turn on recording and play for about 5 minutes", flag18 ? SharedOn(name) : RecordOnFirst(name)) + DbNote(flag9, entitled));
					}
					else
					{
						tuple4 = (GameStatus.NeedsRecording, planCheck.Reason + DbNote(flag9, entitled));
					}
				}
				else if (flag17)
				{
					tuple4 = ((!gameRecord.WarmedAt.HasValue) ? GameStatus.Ready : GameStatus.Warmed, "the compile didn't help this game on NVIDIA, it needs a custom loader");
				}
				else if (flag21 && text4 == null && !gameRecord.WarmedAt.HasValue)
				{
					tuple4 = (GameStatus.NeedsRecording, RtNote(flag9, entitled));
				}
				else if (!gameRecord.WarmedAt.HasValue)
				{
					tuple4 = (GameStatus.Ready, RtBlocked(flag21, text4, PartialNote(gameRecord.Plan?.Stats) ?? planCheck.Reason) + text6);
				}
				else
				{
					string text16 = StaleReason(g, gameRecord, engineInfo, value, gpuSnapshot, status: true);
					if (text16 == null)
					{
						if (text14 != null)
						{
							string text17 = KeptReason(gameRecord);
							if (text17 != null)
							{
								tuple4 = (GameStatus.Warmed, text17);
								goto IL_1436;
							}
						}
						rt = flag21;
						if (!flag25)
						{
							if (num.HasValue)
							{
								int valueOrDefault6 = num.GetValueOrDefault();
								string text18 = PartlyWarmedNote(gameRecord, valueOrDefault6, timeSpan4);
								if (text18 != null)
								{
									text5 = "partly warmed for driver " + gameRecord.WarmedDriverVersion + ": " + text18;
									goto IL_13ee;
								}
							}
							text5 = "warmed for driver " + gameRecord.WarmedDriverVersion;
						}
						else
						{
							text5 = "partly compiled for driver " + gameRecord.WarmedDriverVersion + ": " + RefusedNote(rejected.GetValueOrDefault(), num4.GetValueOrDefault());
						}
						goto IL_13ee;
					}
					tuple4 = (GameStatus.Stale, text16);
				}
			}
			else
			{
				tuple4 = (GameStatus.Unsupported, "the compile can't reach this game: it reads its shaders from a different driver cache than the one SCSKiller fills. Clear its cache to free the space");
			}
		}
		else
		{
			tuple4 = (GameStatus.Unsupported, ShaderModReason(reShadeInstall));
		}
		goto IL_1436;
	}

	private GameState WithRecorder(GameState s, GameRecord rec, bool ours, string exeDir, string dllName, bool shared = false)
	{
		string text = Collections.RecordedBy(s.Game.Id);
		bool flag = Foreign(exeDir);
		bool ours2 = ours | flag;
		bool flag2 = text != null;
		if (flag2)
		{
			bool flag3;
			switch (RecorderSkip(s, null))
			{
			case "anti-cheat":
			case "not supported yet":
			case "not DirectX 12":
			case "the recorder crashes this game, so it's off for now":
				flag3 = true;
				break;
			default:
				flag3 = false;
				break;
			}
			flag2 = !flag3;
		}
		string text2 = (flag2 ? SharedRecorder(Collections.Member(text).Value.Game.Name) : (RecorderSkip(s, (text != null) ? null : ModSkip(s.Game, exeDir, rec, ours2, dllName)) ?? _recorderSkips.GetValueOrDefault(s.Game.Id)));
		RecorderOverride recorderOverride = (RecorderOverride)(((int?)rec.Recorder) ?? (ours ? 1 : 0));
		bool flag4 = text == null && (flag || Foreign(RecordedAt(s.Game, rec)));
		long num = Length(Path.Combine(exeDir, "scskiller.db"));
		bool flag5 = OfflineEntry(s.Game, s.AntiCheat, s.Engine) != null && GameVerdicts.Current.Unsupported(s.Game, s.Engine) == null;
		GameState gameState = s with
		{
			RecorderOverride = recorderOverride,
			RecorderSkip = text2,
			RecorderEffective = ((text != null) ? shared : (!flag4 && RecorderWanted(recorderOverride, Settings.RecordAllGames && s.NoStutter == null, text2, Frames11(s.Engine, Vendor.Caps)))),
			RecorderForeign = flag4,
			RecorderNote = (_recorderNotes.GetValueOrDefault(s.Game.Id) ?? ((recorderOverride == RecorderOverride.Off && rec.RecorderOrphans.Any(File.Exists)) ? "a mod SCSKiller recorded alongside was removed, so recording is off for this game" : null)),
			RecorderMod = ((text != null) ? null : ModName(exeDir, rec, ours2, dllName)),
			RecorderRefused = ((!ours) ? null : LastLaunch(s.Game, rec, exeDir, dllName, s.Engine?.GraphicsApi)),
			RecordAlongsideMod = rec.RecordAlongsideMod,
			RecordingBytes = (from f in RecordingFiles(s.Game)
				where Path.GetFileName(f) != "scskiller_frames.bin"
				select f).Sum((Func<string, long>)Length),
			RecordingPaused = ours && DbCap(s.Game) is long valueOrDefault && num >= valueOrDefault,
			OfflineEligible = flag5,
			OfflineRecord = rec.OfflineRecord,
			OfflineRunning = OfflineLive(s.Game.Id) || rec.OfflineSession != null,
			RiskEligible = !flag5 && RiskOffered(s),
			RiskNoLaunch = !flag5 && RiskOfferedButLaunch(s.Game, s.Engine, s.AnyAntiCheat) && !StoreLaunches(s.Game),
			SessionNote = _sessionNotes.GetValueOrDefault(s.Game.Id)
		};
		return gameState;
	}

	private static long Length(string path)
	{
		FileInfo fileInfo = new FileInfo(path);
		if (fileInfo == null || !fileInfo.Exists)
		{
			return 0L;
		}
		return fileInfo.Length;
	}

	private IEnumerable<string> RecordingFiles(Game g)
	{
		List<string> list = new List<string>();
		list.AddRange(((Collections.RecordedBy(g.Id) != null || Foreign(g)) ? Array.Empty<string>() : RecorderDataFiles).Select((string f) => Path.Combine(Path.GetDirectoryName(g.ExePath), f)));
		list.Add(RecordingPath(g.Id));
		list.AddRange(SessionFiles.Select((string f) => Path.Combine(OfflineSessionDir(Store, g.Id), f)));
		list.AddRange(new string[2] { "recording.all.db", "recording.all.db.key" }.Select((string f) => Path.Combine(Store.GameDir(g.Id), f)));
		return new _003C_003Ez__ReadOnlyList<string>(list);
	}

	private FrameReport? Frames(string exeDir, string exe, IReadOnlySet<string>? rayQuery, PlayWindow? played, double? playCapMs = null)
	{
		FileInfo fileInfo = new FileInfo(Path.Combine(exeDir, "scskiller_frames.bin"));
		string text = Path.Combine(exeDir, "scskiller_creates.csv");
		FileInfo fileInfo2 = fileInfo;
		if (!fileInfo2.Exists)
		{
			return null;
		}
		(long, DateTime, long, PlayWindow) tuple = (fileInfo2.Length, fileInfo2.LastWriteTimeUtc, Length(text), played);
		string key = $"{fileInfo2.FullName}|{playCapMs}";
		if (_frames.TryGetValue(key, out (long, DateTime, long, PlayWindow, FrameReport) value))
		{
			long item = value.Item1;
			DateTime item2 = value.Item2;
			long item3 = value.Item3;
			PlayWindow item4 = value.Item4;
			(long, DateTime, long, PlayWindow) tuple2 = tuple;
			if (item == tuple2.Item1 && item2 == tuple2.Item2 && item3 == tuple2.Item3 && item4 == tuple2.Item4)
			{
				return value.Item5;
			}
		}
		FrameReport frameReport;
		try
		{
			frameReport = FrameLog.Read(fileInfo2.FullName, text, exe, rayQuery, played, playCapMs);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
		_frames[key] = (tuple.Item1, tuple.Item2, tuple.Item3, played, frameReport);
		return frameReport;
	}

	private void NoteFrames(Game g, FrameReport r)
	{
		FrameSession frameSession = FrameSessions.Of(r);
		if (_noted.TryGetValue(g.Id, out FrameSession value) && value == frameSession)
		{
			return;
		}
		try
		{
			FrameSessions.Note(Store, g.Id, r);
			_noted[g.Id] = frameSession;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			Log?.Report(g.Name + ": couldn't keep its frame times: " + ex.Message);
		}
	}

	public IReadOnlyList<FrameSession> FrameHistory(string gameId)
	{
		return FrameSessions.Read(Store, gameId);
	}

	public static long? DbCap(long limitBytes, long stored)
	{
		if (limitBytes <= 0)
		{
			return null;
		}
		return Math.Max(0L, limitBytes - stored);
	}

	private long? DbCap(Game g)
	{
		return DbCap((long)Settings.RecordingLimitMB * 1048576L, Length(RecordingPath(g.Id)));
	}

	public static string LimitText(int mb)
	{
		if (mb > 0)
		{
			if (mb % 1024 == 0)
			{
				return $"{mb / 1024} GB";
			}
			return $"{mb} MB";
		}
		return "Unlimited";
	}

	public static string PausedNote(Settings s)
	{
		return "Recording paused: limit reached (" + LimitText(s.RecordingLimitMB) + ")";
	}

	public static bool NeedsRtRecording(PlanStats? p)
	{
		if (RtLeft(p))
		{
			long? rtInline = p.RtInline;
			return !rtInline.HasValue || rtInline.GetValueOrDefault() <= 0;
		}
		return false;
	}

	private static bool RtLeft(PlanStats? p)
	{
		if ((object)p != null && p.RtUncovered > 0 && p.RtStateObjects == 0L)
		{
			return (double)p.RtUncovered > 0.1 * (double)p.RtLibraries;
		}
		return false;
	}

	public static bool RtInlineCovers(PlanStats? p)
	{
		if (RtLeft(p))
		{
			return p.RtInline > 0;
		}
		return false;
	}

	public static bool HasRayTracing(PlanStats? p)
	{
		if ((object)p != null)
		{
			if (p.RtLibraries <= 0 && p.RtStateObjects <= 0)
			{
				long? rtInline = p.RtInline;
				if (!rtInline.HasValue || rtInline.GetValueOrDefault() <= 0)
				{
					goto IL_0036;
				}
			}
			return true;
		}
		goto IL_0036;
		IL_0036:
		return false;
	}

	public static bool NeedsRtRecording(GameState s)
	{
		if (NeedsRtRecording(s.Plan) && !s.RtUnseen && !s.RtToPlan)
		{
			return !s.RtSkipped;
		}
		return false;
	}

	private static bool RtPlanCheck(GameRecord r, EngineInfo? e)
	{
		if (NeedsRtRecording(r.Plan?.Stats))
		{
			if (!(r.RecordingImportedAt > r.PlanBuiltAt) && (r.Plan.Stats.RtInline.HasValue || (object)e == null || !(e.Family == "Unreal") || !e.Version.StartsWith('5')))
			{
				return e?.NoRtPipelines ?? false;
			}
			return true;
		}
		return false;
	}

	public static bool RecordedEnough(GameState s)
	{
		return s.RecordedEnough;
	}

	public static string RanOtherExeNote(string exe)
	{
		return "The last launch ran " + exe + ", not the exe the recorder was set up for. It is set up for it now: play again.";
	}

	public static bool NeverRecorded(GameState s)
	{
		string recorderRefused = default;
		int num;
		if ((object)s != null && s.RecorderInstalled && s.RecorderSkip == null)
		{
			recorderRefused = s.RecorderRefused;
			num = ((recorderRefused != null) ? 1 : 0);
		}
		else
		{
			num = 0;
		}
		bool flag = (byte)num != 0;
		if (flag)
		{
			bool flag2;
			switch (recorderRefused)
			{
			case "The recorder never loaded in the last launch. Please report it with the game's name.":
			case "The recorder didn't load in the last launch. The game may have run on DirectX 11.":
			case "The game changed since the recorder was set up. It is set up again: play again.":
			case "The last launch wasn't recorded: the recorder was set up for the game played before. It is set up for this game now: play again.":
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			flag = flag2 || recorderRefused.StartsWith("The last launch ran ", StringComparison.Ordinal);
		}
		return flag;
	}

	private static string? LastLaunch(Game g, GameRecord rec, string exeDir, string dllName, string? api)
	{
		if (Collections.ExesBeside(g.ExePath).Any((string x) => !x.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase) && Attested(g with
		{
			ExePath = x
		})))
		{
			return null;
		}
		string path = RefusedFile(g.ExePath);
		PlayWindow lastPlay = rec.LastPlay;
		string text = (((object)lastPlay != null && File.Exists(path) && File.GetLastWriteTimeUtc(path) < lastPlay.From.UtcDateTime) ? null : Refused(g.ExePath));
		bool flag = NeverSaw(g.ExePath, exeDir, dllName, rec.LastPlay);
		if ((Unstamped(text) | flag) && rec != null)
		{
			DateTimeOffset? runsExeAt = rec.RunsExeAt;
			if (runsExeAt.HasValue)
			{
				DateTimeOffset valueOrDefault = runsExeAt.GetValueOrDefault();
				PlayWindow lastPlay2 = rec.LastPlay;
				if ((object)lastPlay2 != null)
				{
					string runsExeWas = rec.RunsExeWas;
					if (runsExeWas != null && lastPlay2.From <= valueOrDefault && valueOrDefault <= lastPlay2.To && rec.RunsExe == g.ExePath && !Path.GetFileName(runsExeWas).Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(runsExeWas).Equals(exeDir, StringComparison.OrdinalIgnoreCase))
					{
						return RanOtherExeNote(Path.GetFileName(g.ExePath));
					}
				}
			}
		}
		if (flag)
		{
			if (!(dllName == "d3d12.dll") || !(ApiKey(api) == "11+12"))
			{
				return "The recorder never loaded in the last launch. Please report it with the game's name.";
			}
			return "The recorder didn't load in the last launch. The game may have run on DirectX 11.";
		}
		string text2 = text;
		if (text2 != null)
		{
			if (text2 == "stamped for another game of its collection")
			{
				return "The last launch wasn't recorded: the recorder was set up for the game played before. It is set up for this game now: play again.";
			}
			if (!Unstamped(text) && !(text == "scskiller.ini changed while deciding"))
			{
				if (text2 == "loaded from another folder than the exe")
				{
					return "The last launch wasn't recorded: a mod loaded a copy of the recorder from another folder, where it can't record.";
				}
				return "The last launch wasn't recorded: " + text2 + ".";
			}
			return "The game changed since the recorder was set up. It is set up again: play again.";
		}
		return null;
	}

	private static bool Unstamped(string? why)
	{
		switch (why)
		{
		case "exe not as checked":
		case "no install root":
		case "not signed by SCSKiller":
			return true;
		default:
			return false;
		}
	}

	internal static bool RtUnseen(SessionStats? last, bool before)
	{
		if ((object)last != null)
		{
			if (last.StateObjectsReady > 0 || last.StateObjectsCompiled > 0 || last.StateObjectsStartupCompiled > 0)
			{
				return false;
			}
			return before || last.Duration >= EnoughRecording;
		}
		return before;
	}

	public static string RtNote(bool? inCommunityDb, bool entitled)
	{
		return "Ray-traced effects need one short recording" + ((!inCommunityDb.HasValue) ? ", or a community recording for this version" : DbNote(inCommunityDb, entitled));
	}

	private static string DbNote(bool? inDb, bool entitled)
	{
		if (inDb.HasValue)
		{
			if (inDb == true)
			{
				return "; " + (entitled ? "in the community database" : "in the community database for Patreon supporters");
			}
			return "; not in the community database yet";
		}
		return "";
	}

	private static CommunityEntry? DbEntry(CommunityManifest m, Game g, GameRecord rec, bool full)
	{
		return ((IndexIsInstalled(g, rec) || (g.Version == null && rec.IndexExeStamp == null)) ? m.Find(g, rec.IndexContentHash, full) : null) ?? m.Find(g, null, full);
	}

	private static OfficialEntry? OfficialEntry(CommunityManifest m, Game g, GameRecord rec)
	{
		return ((IndexIsInstalled(g, rec) || (g.Version == null && rec.IndexExeStamp == null)) ? m.FindOfficial(g, rec.IndexContentHash) : null) ?? m.FindOfficial(g, null);
	}

	public static string RtWhy(VendorCaps caps, EngineInfo? e)
	{
		if (caps.RtCacheGranularity != RtCacheGranularity.WholeObject)
		{
			string text;
			if ((object)e != null)
			{
				text = Format.Engine(e);
			}
			else
			{
				text = "this engine";
			}
			return "SCSKiller can't rebuild " + text + "'s ray tracing layout from the game files yet.";
		}
		return "This GPU's driver reuses ray tracing pipelines only exactly as the game builds them, and which ones it builds isn't in its files.";
	}

	private static string RtBlocked(bool rt, string? noRecording, string reason)
	{
		if (rt)
		{
			if (noRecording == null)
			{
				return reason + "; ray tracing needs a 5-min recording";
			}
			return reason + "; ray-traced effects aren't compiled: they need a recording, " + noRecording;
		}
		return reason;
	}

	public static bool RtAfterRecording(GameState s)
	{
		if (s.Status == GameStatus.Warmed)
		{
			return s.StatusReason.Contains("ray tracing needs a 5-min recording", StringComparison.Ordinal);
		}
		return false;
	}

	public static string? GuessNote(EngineInfo e)
	{
		bool versionGuessed = e.VersionGuessed;
		bool apiGuessed = e.ApiGuessed;
		string text;
		if (versionGuessed)
		{
			if (apiGuessed)
			{
				text = "Unreal version and DirectX";
			}
			else
			{
				if (!e.GraphicsApi.Contains("D3D12"))
				{
					goto IL_0043;
				}
				text = "Unreal version";
			}
		}
		else
		{
			if (!apiGuessed)
			{
				goto IL_0043;
			}
			text = "DirectX";
		}
		goto IL_0045;
		IL_0043:
		text = null;
		goto IL_0045;
		IL_0045:
		string text2 = text;
		if (text2 == null)
		{
			return e.Note;
		}
		return text2 + " guessed, so the compile could be wrong.";
	}

	private (EngineInfo Engine, string? Detected) WithApiChoice(Game g, EngineInfo e, DxApi? choice)
	{
		if (ApiKey(e.GraphicsApi) == null)
		{
			return (Engine: e, Detected: null);
		}
		string text = ((e.ApiGuessed && File.Exists(RecordingPath(g.Id))) ? Planner.RecordedApi(RecordingPath(g.Id)) : null);
		if (text != null)
		{
			return (Engine: e with
			{
				GraphicsApi = text + " (recording)",
				ApiGuessed = false
			}, Detected: e.GraphicsApi);
		}
		EngineInfo item;
		if (choice.HasValue)
		{
			DxApi valueOrDefault = choice.GetValueOrDefault();
			item = e with
			{
				GraphicsApi = valueOrDefault.ToString() + " (your choice)",
				ApiGuessed = false
			};
		}
		else
		{
			item = e;
		}
		return (Engine: item, Detected: e.GraphicsApi);
	}

	public static string? ApiKey(string? api)
	{
		if (api != null)
		{
			string text = (api.StartsWith("D3D11") ? "11" : "") + ((api.StartsWith("D3D11") && api.Contains("D3D12")) ? "+" : "") + (api.Contains("D3D12") ? "12" : "");
			if (text == null || text.Length <= 0)
			{
				return null;
			}
			return text;
		}
		return null;
	}

	public static string? ApiOverruled(GameState s)
	{
		if ((object)s != null)
		{
			DxApi? apiOverride = s.ApiOverride;
			if (apiOverride.HasValue)
			{
				DxApi valueOrDefault = apiOverride.GetValueOrDefault();
				EngineInfo engine = s.Engine;
				if ((object)engine != null && !engine.GraphicsApi.EndsWith(" (your choice)", StringComparison.Ordinal))
				{
					if (ApiKey(engine.GraphicsApi) == ApiKey(valueOrDefault.ToString()))
					{
						return null;
					}
					string value;
					if (engine.GraphicsApi.Contains("D3D12"))
					{
						value = "DirectX 12";
					}
					else
					{
						value = (engine.GraphicsApi.StartsWith("D3D11") ? "DirectX 11" : engine.GraphicsApi);
					}
					return $"Your recording shows {value}, so your DirectX {((valueOrDefault == DxApi.D3D11) ? "11" : "12")} choice isn't used.";
				}
			}
		}
		return null;
	}

	public static string? SessionKeptNote(GameState s)
	{
		EngineInfo engine = s.Engine;
		if ((object)engine != null)
		{
			if (engine.Encrypted)
			{
				return "Its recording is kept for later. It compiles once you add the game's AES key.";
			}
			if (engine.Unsupported != null)
			{
				return "This game isn't supported yet, so its recording is kept for later and nothing compiles from it now.";
			}
		}
		return null;
	}

	public static string NoLaunchNote(string store)
	{
		return "No recording session: SCSKiller can't start games from " + store + ".";
	}

	public static bool NeedsOfflineSession(GameState s)
	{
		if (s.Status == GameStatus.NeedsRecording)
		{
			return s.StatusReason == "EasyAntiCheat blocks the recorder, but you can record at your own risk with an offline session.";
		}
		return false;
	}

	public static bool NeedsAesKey(GameState s)
	{
		EngineInfo engine = s.Engine;
		if ((object)engine != null && engine.Family == "Unreal")
		{
			return engine.Encrypted;
		}
		return false;
	}

	private CommunityManifest? LocalManifest()
	{
		FileInfo fileInfo = new FileInfo(Path.Combine(Store.DataDir, "community", "manifest.bin"));
		string text = (fileInfo.Exists ? $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}" : "");
		lock (_scanLock)
		{
			if (text != _manifestStamp)
			{
				_manifestStamp = text;
				try
				{
					_manifest = (fileInfo.Exists ? CommunityManifest.Parse(File.ReadAllBytes(fileInfo.FullName)) : null);
				}
				catch (Exception ex) when ((ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) ? true : false)
				{
					_manifest = null;
				}
			}
			return _manifest;
		}
	}

	public static bool IsPartial(PlanStats? p)
	{
		if ((object)p != null && p.Uncovered > 0)
		{
			return (double)p.Uncovered > 0.1 * (double)(p.Uncovered + p.Recorded + p.Generated);
		}
		return false;
	}

	public static bool BothApis(PlanStats? p)
	{
		if ((object)p != null && p.D3D11Shaders > 0)
		{
			return p.Recorded + p.Generated + p.MiddlewareItems > 0;
		}
		return false;
	}

	public static long? PlanItems(PlanStats? p)
	{
		if ((object)p == null)
		{
			return null;
		}
		return p.Recorded + p.Generated + p.D3D11Shaders + p.MiddlewareItems + p.Variants;
	}

	public static bool IsPartlyCompiled(long? rejected, long? items)
	{
		return (double?)rejected > 0.1 * (double?)items;
	}

	private static long? Rejected(long? failed, long? crashed)
	{
		return failed + crashed.GetValueOrDefault();
	}

	public static bool IsPartlyCompiled(GameState s)
	{
		if ((object)s != null && s.Status == GameStatus.Warmed && !s.CompileUnreached)
		{
			return IsPartlyCompiled(Rejected(s.LastWarmFailed, s.LastWarmCrashed), PlanItems(s.Plan));
		}
		return false;
	}

	public static string PartlyCompiledNote(GameState s)
	{
		return PartlyCompiledNote(Rejected(s.LastWarmFailed, s.LastWarmCrashed).GetValueOrDefault(), PlanItems(s.Plan).GetValueOrDefault());
	}

	private static string PartlyCompiledNote(long rejected, long items)
	{
		return $"the driver skipped {rejected:N0} of {items:N0} pipelines";
	}

	public static string RefusedNote(GameState s)
	{
		return RefusedNote(Rejected(s.LastWarmFailed, s.LastWarmCrashed).GetValueOrDefault(), PlanItems(s.Plan).GetValueOrDefault());
	}

	private static string RefusedNote(long rejected, long items)
	{
		return $"the driver refused {rejected:N0} of {items:N0} pipelines, so those can still stutter the first time";
	}

	public static string? RefusedLine(GameState s)
	{
		long? lastWarmFailed = s.LastWarmFailed;
		if (lastWarmFailed.HasValue && lastWarmFailed.GetValueOrDefault() > 0)
		{
			if (IsPartlyCompiled(s))
			{
				lastWarmFailed = PlanItems(s.Plan);
				if (lastWarmFailed.HasValue)
				{
					long valueOrDefault = lastWarmFailed.GetValueOrDefault();
					return $"The driver refused {s.LastWarmFailed:N0} of {valueOrDefault:N0} pipelines. Nothing is broken: the game compiles any of them it uses itself, " + "the first time, which is when it can stutter. A driver or SCSKiller update may let them compile, so compile the game again after one.";
				}
			}
			return $"The driver skipped {s.LastWarmFailed:N0} combination{((s.LastWarmFailed == 1) ? "" : "s")}.";
		}
		return null;
	}

	public static bool NothingFailed(GameState s)
	{
		if (s.LastWarmFailed.HasValue)
		{
			return s.LastWarmFailed + s.LastWarmCrashed.GetValueOrDefault() == 0;
		}
		return false;
	}

	public static int? CarefulThreads(GpuVendor vendor)
	{
		if (vendor != GpuVendor.Amd)
		{
			return null;
		}
		return 4;
	}

	public static bool IsPartlyWarmed(LaunchCheck? launch)
	{
		if (launch != null)
		{
			return (double)launch.Compiles > 0.2 * (double)(launch.Hits + launch.Compiles);
		}
		return false;
	}

	public static bool IsPartlyWarmed(GameState s)
	{
		if (s.Status == GameStatus.Warmed)
		{
			CarefulCompile? careful = s.Careful;
			if ((object)careful == null)
			{
				return false;
			}
			return careful.LaunchCompiled > 0.2;
		}
		return false;
	}

	private static string? PartlyWarmedNote(GameRecord r, int threads, TimeSpan? careful)
	{
		LaunchCheck firstLaunch = r.FirstLaunch;
		if ((object)firstLaunch != null && IsPartlyWarmed(firstLaunch))
		{
			string text = $"{firstLaunch.Compiled * 100.0:0}% of the {firstLaunch.Hits + firstLaunch.Compiles:N0} pipelines its first launch created still compiled";
			string text4;
			if (!r.WarmedCareful)
			{
				Plan? plan = r.Plan;
				if ((object)plan == null || plan.Stats.Recorded != 0)
				{
					string text2 = $"; a careful compile ({threads} threads, in passes) reaches more of them";
					string text3;
					if (careful.HasValue)
					{
						TimeSpan valueOrDefault = careful.GetValueOrDefault();
						text3 = ", in about " + Duration(valueOrDefault);
					}
					else
					{
						text3 = "";
					}
					text4 = text2 + text3 + (r.Careful ? ": the next compile is careful" : "");
				}
				else
				{
					text4 = "; a careful compile only changes how the recorded pipelines compile, and there is no recording yet (turn recording on and play)";
				}
			}
			else
			{
				text4 = ", even after a careful compile";
			}
			return text + text4;
		}
		return null;
	}

	public static string Duration(TimeSpan t)
	{
		if (!(t.TotalMinutes < 1.0))
		{
			if (!(t.TotalHours < 1.0))
			{
				return $"{(int)t.TotalHours} h" + ((t.Minutes > 0) ? $" {t.Minutes} min" : "");
			}
			return $"{(int)Math.Round(t.TotalMinutes)} min";
		}
		return $"{Math.Max(1, (int)t.TotalSeconds)} s";
	}

	public static double? Coverage(PlanStats? p)
	{
		if ((object)p == null || p.StageSets <= 0)
		{
			return null;
		}
		return (double)(p.StageSets - p.LeftOut) / (double)p.StageSets;
	}

	public static int? CoveragePercent(PlanStats? p)
	{
		double? num = Coverage(p);
		if (num.HasValue)
		{
			double valueOrDefault = num.GetValueOrDefault();
			return Math.Min((int)Math.Floor(valueOrDefault * 100.0), (p.LeftOut > 0) ? 99 : 100);
		}
		return null;
	}

	public static int? ShownPercent(GameState s)
	{
		if (!s.CompileUnreached)
		{
			PlanStats plan = s.Plan;
			if ((object)plan != null)
			{
				long? num = PlanItems(plan);
				if (num.HasValue)
				{
					long valueOrDefault = num.GetValueOrDefault();
					if (valueOrDefault + s.RecordedSinceWarm > 0)
					{
						long num2;
						long num3;
						if (plan.StageSets <= 0)
						{
							num2 = 1L;
							num3 = 1L;
						}
						else
						{
							long num4 = plan.StageSets - plan.LeftOut;
							long stageSets = plan.StageSets;
							num3 = stageSets;
							num2 = num4;
						}
						long num5 = Math.Max(0L, valueOrDefault - Rejected(s.LastWarmFailed, s.LastWarmCrashed).GetValueOrDefault() - s.LastWarmDriverSkipped.GetValueOrDefault() - s.LastWarmUnbuilt.GetValueOrDefault() - s.NewPipelines.GetValueOrDefault());
						return (int)((Int128)num2 * (Int128)100 * num5 / ((Int128)num3 * (Int128)(valueOrDefault + s.RecordedSinceWarm)));
					}
				}
			}
		}
		return null;
	}

	public static bool ShowsCoverage(GameState s)
	{
		if (s.Plan != null)
		{
			if (s.Status == GameStatus.Unsupported)
			{
				return s.WarmedAt.HasValue;
			}
			return true;
		}
		return false;
	}

	public static string CacheBudgetTitle(VendorCaps caps)
	{
		if (!caps.PerGameCacheCap.HasValue)
		{
			return "More than the AMD shader cache holds";
		}
		return "More than the Intel shader cache keeps per game";
	}

	public static string CacheBudgetNote(VendorCaps caps)
	{
		long? perGameCacheCap = caps.PerGameCacheCap;
		string text;
		if (perGameCacheCap.HasValue)
		{
			long valueOrDefault = perGameCacheCap.GetValueOrDefault();
			text = "This game has more pipelines than the Intel driver keeps for one game (" + Format.Bytes(valueOrDefault) + "). ";
		}
		else
		{
			text = "This game has more pipelines than the AMD driver's whole shader cache can hold (" + Format.Bytes(17179869184L) + "). ";
		}
		return text + "So SCSKiller compiles the ones most likely to be used, recorded ones first. The rest compile while you play.";
	}

	public static string? PartialNote(PlanStats? p)
	{
		if (IsPartial(p))
		{
			if (p.Recorded == 0L)
			{
				return $"compiles {p.Generated:N0} pipelines; a 5-minute recording lets SCSKiller rebuild the rest";
			}
			return $"compiles {p.Recorded + p.Generated:N0} pipelines; {p.Uncovered:N0} more shader combinations use shader slots SCSKiller can't rebuild yet";
		}
		return null;
	}

	public static string ProgressText(QueueItem q)
	{
		WarmProgress progress = q.Progress;
		if ((object)progress == null)
		{
			return "";
		}
		if (q.Stage == QueueStage.Indexing)
		{
			return $" {progress.Done}/{progress.Total} packages";
		}
		string value = $"{progress.Failed} failed" + ((progress.Skipped > 0) ? $", {progress.Skipped} skipped: not in this install" : "");
		string text = $" {progress.Done}/{progress.Total} ({value}) {progress.PerSecond:0}/s";
		string text2 = MemoryLimitNote(progress);
		string text3 = ((text2 != null) ? (", " + text2) : "");
		bool flag;
		switch (q.Stage)
		{
		case QueueStage.Warming:
		case QueueStage.Done:
		case QueueStage.Stopped:
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		string text4;
		if (flag)
		{
			string note = q.Note;
			if (note != null)
			{
				text4 = " - " + note;
				goto IL_01d2;
			}
		}
		text4 = "";
		goto IL_01d2;
		IL_01d2:
		return text + text3 + text4;
	}

	public static string? MemoryLimitNote(WarmProgress p)
	{
		if ((object)p == null || p.Workers <= 0 || p.Workers >= p.Threads)
		{
			return null;
		}
		return $"memory limit: {p.Workers} of {p.Threads} threads";
	}

	public static string StalledNote(TimeSpan since)
	{
		return $"no progress for {Math.Max(1, (int)since.TotalMinutes)} min";
	}

	public static bool Stalled(QueueItem q)
	{
		if (q.Stage == QueueStage.Warming)
		{
			return q.Note?.StartsWith("no progress") ?? false;
		}
		return false;
	}

	private static string Took(TimeSpan t)
	{
		if (!(t.TotalHours >= 1.0))
		{
			if (t.TotalMinutes >= 1.0)
			{
				return $"{t.Minutes}m{t.Seconds:00}s";
			}
			return $"{(int)t.TotalSeconds}s";
		}
		return $"{(int)t.TotalHours}h{t.Minutes:00}m{t.Seconds:00}s";
	}

	public static string? WarmCounts(long failed, long skipped, long crashed = 0L, long driverSkipped = 0L, long unbuilt = 0L)
	{
		string text = string.Join(", ", new string[5]
		{
			(failed > 0) ? $"{failed} failed (the driver rejected them)" : null,
			(skipped > 0) ? $"{skipped} skipped (a shader not in this install)" : null,
			(crashed > 0) ? $"{crashed} skipped ({((crashed == 1) ? "it crashes" : "they crash")} the GPU driver)" : null,
			(driverSkipped > 0) ? $"{driverSkipped} skipped for this compile (the GPU driver crashed or hung on {((driverSkipped == 1) ? "it" : "them")} or on what {((driverSkipped == 1) ? "it builds" : "they build")} on)" : null,
			(unbuilt > 0) ? $"{unbuilt} skipped (SCSKiller can't build {((unbuilt == 1) ? "it" : "them")} yet)" : null
		}.OfType<string>());
		if (text == null || text.Length <= 0)
		{
			return null;
		}
		return text;
	}

	public static double DefaultWarmRate(GpuVendor vendor)
	{
		if (vendor != GpuVendor.Amd)
		{
			return 450.0;
		}
		return 160.0;
	}

	public static int AutoCompileMemoryGB(long totalPhysicalBytes)
	{
		double num = (double)totalPhysicalBytes / 1073741824.0;
		if (!(num <= 20.0))
		{
			if (!(num <= 28.0))
			{
				if (!(num <= 40.0))
				{
					if (num <= 56.0)
					{
						return 8;
					}
					return 16;
				}
				return 6;
			}
			return 4;
		}
		return 2;
	}

	public static int CompileMemoryGB(Settings s)
	{
		if (s.MaxCompileMemoryGB <= 0)
		{
			return AutoCompileMemoryGB();
		}
		return s.MaxCompileMemoryGB;
	}

	public static int AutoCompileMemoryGB()
	{
		return AutoCompileMemoryGB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
	}

	public static int RamGB(long totalPhysicalBytes)
	{
		return (int)(totalPhysicalBytes >> 30);
	}

	public static int RamGB()
	{
		return RamGB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
	}

	public static int MaxCompileMemoryGB(long totalPhysicalBytes)
	{
		return Math.Max(16, RamGB(totalPhysicalBytes) - 2);
	}

	public static bool ColdWarm(GameRecord rec, string? driverId)
	{
		if (rec.WarmedAt.HasValue && driverId != null)
		{
			return rec.WarmedDriverId != driverId;
		}
		return true;
	}

	public static double? MeasuredRate(IEnumerable<GameState> games)
	{
		List<(double, double)> list = (from g in games.Where((GameState g) =>
			{
				if ((object)g != null)
				{
					double? psoPerSecond = g.PsoPerSecond;
					if (psoPerSecond.HasValue && psoPerSecond.GetValueOrDefault() > 0.0)
					{
						return (object)g.Plan != null;
					}
				}
				return false;
			})
			select ((double Items, double Rate))(Items: PlanItems(g.Plan).Value, Rate: g.PsoPerSecond.Value) into g
			where g.Items > 0.0
			select g).ToList();
		if (list.Count <= 0)
		{
			return null;
		}
		return list.Sum(((double Items, double Rate) g) => g.Items) / list.Sum(((double Items, double Rate) g) => g.Items / g.Rate);
	}

	private void SeedWarmRate(double psoPerSecond)
	{
		Dictionary<GpuVendor, double> dictionary = Store.LoadWarmRates();
		if (!dictionary.ContainsKey(Vendor.Vendor))
		{
			dictionary[Vendor.Vendor] = psoPerSecond;
			Store.SaveWarmRates(dictionary);
			_warmRate = psoPerSecond;
		}
	}

	private Evaluation Evaluated(Game g, GameRecord rec, bool force, out bool fresh)
	{
		InlineArray7<object> buffer = default;
		buffer[0] = ExeStamp(g);
		buffer[1] = GameFiles.DirKey(g.InstallDir);
		buffer[2] = g.Version;
		buffer[3] = Vendor.Caps.Profile;
		buffer[4] = rec.RecordingImportedAt?.UtcTicks;
		buffer[5] = CoreBuild;
		buffer[6] = CommunityObjects(g.Id);
		string text = string.Join('|', (ReadOnlySpan<object?>)buffer);
		CommunityDownload communityDownload = OfficialInUse(g.Id);
		string obj = (((object)communityDownload != null) ? ("|official:" + communityDownload.Object) : "");
		DxApi? apiOverride = rec.ApiOverride;
		string text2 = text + obj + ((!apiOverride.HasValue) ? "" : ("|api:" + apiOverride.GetValueOrDefault()));
		Evaluation value = null;
		long num;
		lock (_scanLock)
		{
			if (_scan == null)
			{
				_scan = Store.LoadScan();
			}
			_scan.TryGetValue(g.Id, out value);
			num = ++_evaluations;
		}
		text2 += DetectStamp(g, value?.Engine);
		string text3 = text2 + RulesStamp(value?.Engine);
		fresh = force || value == null || value.Key != text3;
		if (fresh && !force && value != null && (OnlyBuildDiffers(value.Key, text3) || OnlyBuildDiffers(value.Key, text2)))
		{
			_redetect[g.Id] = g;
			fresh = false;
		}
		if (!fresh && value.AntiCheat != AntiCheat.None && VerdictMoved(g, value))
		{
			fresh = true;
		}
		if (!fresh)
		{
			if (value.AntiCheat != AntiCheat.None)
			{
				TakeOutNow(g, $"{value.AntiCheat} found");
			}
			else
			{
				AntiCheat antiCheat = GameFiles.DetectAntiCheat(g, quick: true);
				AntiCheat antiCheat2 = ((antiCheat != AntiCheat.None) ? antiCheat : (RecorderMayGoIn(g, rec, value.Engine, Vendor.Caps) ? FullCheckOnChange(g) : AntiCheat.None));
				if (antiCheat2 != AntiCheat.None)
				{
					AntiCheatFound(g, antiCheat2);
					return value with
					{
						AntiCheat = antiCheat2
					};
				}
			}
			Evaluation evaluation = Streamline(g, value);
			if ((object)evaluation != value)
			{
				lock (_scanLock)
				{
					if (_scan.TryGetValue(g.Id, out Evaluation value2) && (object)value2 == value)
					{
						_scan[g.Id] = evaluation;
					}
					Store.SaveScan(_scan);
				}
			}
			return evaluation;
		}
		long num2 = RemovalGen(g.Id);
		string text4 = FolderStamp(g);
		long num3 = num2;
		EngineInfo engineInfo = null;
		string apiDetected = null;
		PlanCheck check;
		try
		{
			engineInfo = _reader.Detect(g);
			if (engineInfo != null)
			{
				(engineInfo, apiDetected) = WithApiChoice(g, engineInfo, rec.ApiOverride);
			}
			if (rec != null && (object)rec.Plan != null && rec.PlanApi == null)
			{
				EngineInfo engineInfo2 = value?.Engine;
				if ((object)engineInfo2 != null && ApiKey(engineInfo2.GraphicsApi) != ApiKey(engineInfo?.GraphicsApi))
				{
					rec.PlanApi = ApiKey(engineInfo2.GraphicsApi);
					Store.SaveGame(g.Id, rec);
				}
			}
			if (rec != null && (object)rec.Plan != null && rec.PlanEngine == null)
			{
				EngineInfo engineInfo3 = value?.Engine;
				if ((object)engineInfo3 != null && engineInfo != null && EngineKey(engineInfo3) != EngineKey(engineInfo))
				{
					rec.PlanEngine = EngineKey(engineInfo3);
					Store.SaveGame(g.Id, rec);
				}
			}
			check = ((engineInfo == null) ? new PlanCheck(Readiness.Unsupported, "engine not supported yet") : CheckRecordings(g, engineInfo));
		}
		catch (Exception ex)
		{
			Log?.Report($"{g.Name}: checking it failed: {ex}");
			check = new PlanCheck(Readiness.Unsupported, Codecs.Reason(ex) ?? ((engineInfo == null) ? "couldn't read the game files" : ex.Message));
		}
		string foundAt = null;
		Evaluation evaluation2 = Streamline(g, new Evaluation(text2 + RulesStamp(engineInfo), engineInfo, GameFiles.DetectAntiCheat(g, quick: false, null, AntiCheat.None, (string p) =>
		{
			foundAt = p;
		}), check, null, StreamlineFirst: false, null, null, apiDetected));
		if (evaluation2.AntiCheat != AntiCheat.None)
		{
			AntiCheatFound(g, evaluation2.AntiCheat, evaluation2 with
			{
				Found = text4,
				FoundAt = foundAt
			});
		}
		else
		{
			EvaluateStep?.Invoke("checked");
			lock (_scanLock)
			{
				if (_scanStarted.GetValueOrDefault(g.Id) < num)
				{
					_verdicts.TryRemove(g.Id, out var _);
					_scanStarted[g.Id] = num;
					_scan[g.Id] = evaluation2 with
					{
						Clean = ((RemovalGen(g.Id) == num3) ? text4 : null)
					};
					Store.SaveScan(_scan);
				}
			}
		}
		return evaluation2;
	}

	private static Evaluation Streamline(Game g, Evaluation ev)
	{
		if (ev.AntiCheat != AntiCheat.None)
		{
			if (!ev.StreamlineFirst)
			{
				return ev;
			}
			return ev with
			{
				StreamlineFirst = false
			};
		}
		IReadOnlyList<string> streamlineFiles = ev.StreamlineFiles;
		if (streamlineFiles != null && GameFiles.Stamp(streamlineFiles) == ev.StreamlineStamp)
		{
			return ev;
		}
		List<string> list = new List<string>();
		bool valueOrDefault = GameFiles.StreamlineFirst(g.ExePath, list) == true;
		return ev with
		{
			StreamlineFirst = valueOrDefault,
			StreamlineFiles = list,
			StreamlineStamp = GameFiles.Stamp(list)
		};
	}

	private static bool VerdictMoved(Game g, Evaluation hit)
	{
		string text = FolderStamp(g);
		if (text != null)
		{
			if (!(text != hit.Found))
			{
				string foundAt = hit.FoundAt;
				if (foundAt != null)
				{
					return !Path.Exists(foundAt);
				}
				return false;
			}
			return true;
		}
		return false;
	}

	private static bool OnlyBuildDiffers(string cached, string now)
	{
		string[] array = cached.Split('|');
		string[] array2 = now.Split('|');
		string[] array3 = array;
		if (array3.Length != array2.Length || array3.Length < 6)
		{
			return false;
		}
		array3[5] = (array2[5] = "");
		return array3.SequenceEqual(array2);
	}

	private void RedetectInBackground()
	{
		lock (_scanLock)
		{
			if (_redetect.IsEmpty || !_redetecting.IsCompleted)
			{
				return;
			}
			_redetecting = Task.Run(() => LowIo(() =>
			{
				foreach (string id in _redetect.Keys)
				{
					if (_redetect.TryRemove(id, out Game value) && !UnlistedNow(id))
					{
						try
						{
							Refresh(Games.FirstOrDefault((GameState s) => s.Game.Id == id)?.Game ?? value, default, force: true);
						}
						catch (Exception ex)
						{
							Log?.Report(value.Name + ": detecting it again failed: " + ex.Message);
						}
					}
				}
				ReleaseMemory();
				SaveDllReads();
				return 0;
			}));
		}
	}

	internal static string? FolderStamp(Game g)
	{
		string text;
		try
		{
			text = string.Join('|', Vac.LaunchExes(g));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException) ? true : false)
		{
			return null;
		}
		return Stamp(g.Version + "|" + GameFiles.MarkersStamp + "|" + text, GameFiles.DirKey(g.InstallDir), GameFiles.DirKey(Path.GetDirectoryName(g.ExePath)));
	}

	private static string? Stamp(string? version, params string[] dirs)
	{
		StringBuilder stringBuilder = new StringBuilder(version).Append('|');
		try
		{
			foreach (string item in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
			{
				stringBuilder.Append('>').Append(item);
				foreach (FileSystemInfo item2 in new DirectoryInfo(item).EnumerateFileSystemInfos("*", AllEntries).OrderBy((FileSystemInfo e) => e.Name, StringComparer.Ordinal))
				{
					if (!(item2 is FileInfo fileInfo))
					{
						stringBuilder.Append('|').Append(item2.Name);
					}
					else if (!RecorderOwnFiles.Contains(fileInfo.Name) && !DataFile(fileInfo.Name))
					{
						stringBuilder.Append('|').Append(fileInfo.Name).Append(':')
							.Append(fileInfo.Length)
							.Append(':')
							.Append(fileInfo.LastWriteTimeUtc.Ticks);
					}
				}
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException) ? true : false)
		{
			return null;
		}
		return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(stringBuilder.ToString())));
	}

	private AntiCheat FullCheckOnChange(Game g)
	{
		if (!CleanSince(g, FolderStamp(g)))
		{
			return FullCheck(g);
		}
		return AntiCheat.None;
	}

	private AntiCheat FullCheck(Game g)
	{
		long num = RemovalGen(g.Id);
		string text = FolderStamp(g);
		long num2 = num;
		AntiCheat antiCheat = FullAntiCheatCheck(g);
		if (antiCheat != AntiCheat.None || text == null)
		{
			return antiCheat;
		}
		lock (_scanLock)
		{
			Evaluation evaluation = _scan?.GetValueOrDefault(g.Id);
			if ((object)evaluation != null && evaluation.AntiCheat == AntiCheat.None && !_verdicts.ContainsKey(g.Id) && evaluation.Clean != text && RemovalGen(g.Id) == num2)
			{
				_scan[g.Id] = evaluation with
				{
					Clean = text
				};
				try
				{
					Store.SaveScan(_scan);
				}
				catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
					Log?.Report(g.Name + ": couldn't save the scan cache: " + ex.Message);
				}
			}
		}
		return antiCheat;
	}

	private void NotClean(Game g, bool save = true)
	{
		lock (_scanLock)
		{
			Evaluation valueOrDefault = (_scan ?? (_scan = Store.LoadScan())).GetValueOrDefault(g.Id);
			if ((object)valueOrDefault == null || valueOrDefault.Clean == null)
			{
				return;
			}
			_scan[g.Id] = valueOrDefault with
			{
				Clean = null
			};
			if (!save)
			{
				return;
			}
			try
			{
				Store.SaveScan(_scan);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				Log?.Report(g.Name + ": couldn't save the scan cache: " + ex.Message);
			}
		}
	}

	private bool CleanSince(Game g, string? folders)
	{
		lock (_scanLock)
		{
			int result;
			if (folders != null)
			{
				Evaluation evaluation = _scan?.GetValueOrDefault(g.Id);
				if ((object)evaluation != null && evaluation.AntiCheat == AntiCheat.None && evaluation.Clean == folders)
				{
					result = ((!_verdicts.ContainsKey(g.Id)) ? 1 : 0);
					goto IL_005d;
				}
			}
			result = 0;
			goto IL_005d;
			IL_005d:
			return (byte)result != 0;
		}
	}

	private static T LowIo<T>(Func<T> f)
	{
		bool flag = SetThreadPriority(GetCurrentThread(), 65536);
		try
		{
			return f();
		}
		finally
		{
			if (flag)
			{
				SetThreadPriority(GetCurrentThread(), 131072);
			}
		}
	}

	private static T Lowered<T>(bool low, Func<T> f)
	{
		if (!low)
		{
			return f();
		}
		ThreadPriority priority = Thread.CurrentThread.Priority;
		Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
		try
		{
			return f();
		}
		finally
		{
			Thread.CurrentThread.Priority = priority;
		}
	}

	[DllImport("kernel32.dll")]
	private static extern bool SetThreadPriority(nint thread, int priority);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentThread();

	private string IndexStamp(Game g)
	{
		try
		{
			return _reader.IndexStamp(g);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return "?";
		}
	}

	private static string RulesStamp(EngineInfo? e)
	{
		if (!(e == null))
		{
			return "|" + RulesFile.Current.Hash(e);
		}
		return "";
	}

	private string DetectStamp(Game g, EngineInfo? engine)
	{
		try
		{
			return "|" + _reader.DetectStamp(g, engine);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return "|?";
		}
	}

	private static bool RecorderMayGoIn(Game g, GameRecord rec, EngineInfo? engine, VendorCaps caps)
	{
		if (rec.RecorderFiles.Count <= 0 && !(rec.RecorderChained != null) && rec.RecorderExe == null && rec.RecorderMoveFrom == null && ((object)engine == null || engine.Unsupported != null || !engine.GraphicsApi.Contains("D3D12")) && !Records11(engine, caps) && !Frames11(engine, caps) && !ProxyOnDisk(g))
		{
			return ProxyOnDisk(RecordedAt(g, rec));
		}
		return true;
	}

	private static bool ProxyOnDisk(Game g)
	{
		try
		{
			return OurProxyIn(Path.GetDirectoryName(g.ExePath));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return true;
		}
	}

	private string? StaleReason(Game g, GameRecord r, EngineInfo? e, Pending? pending = null, GpuSnapshot? gpu = null, bool status = false)
	{
		Pending p = pending ?? PendingOf(g, r, e);
		object obj = WarmChanged(g, r, e, gpu);
		if (obj == null)
		{
			if (!p.Unknown)
			{
				obj = null;
			}
			else
			{
				long num = p.Recorded + p.Planned.GetValueOrDefault();
				obj = ((num > 0) ? $"{num:N0} pipeline{((num == 1) ? "" : "s")} to compile again" : "compile again") + ": what the last compile replayed is no longer known";
			}
			if (obj == null)
			{
				if (CurrentPlanner(r.WarmedPlanVersion, r.WarmedPlanRules, r.WarmedReaderVersion, e))
				{
					if (!Maximum(r) || !r.WarmedPerStage)
					{
						if (RayTracing(r) && !r.WarmedRayTracing)
						{
							Plan? plan = r.Plan;
							if ((object)plan != null && plan.Stats.RtLibraries > 0)
							{
								obj = "ray tracing shaders are still to compile";
								goto IL_013b;
							}
						}
						obj = null;
					}
					else
					{
						obj = "Maximum mode: every shader pairing is still to compile";
					}
				}
				else
				{
					obj = PlannerChanged(r, p.Planned, e);
				}
				goto IL_013b;
			}
		}
		goto IL_01fd;
		IL_013b:
		if (obj == null)
		{
			long num2 = p.Recorded + (CurrentPlanner(r.WarmedPlanVersion, r.WarmedPlanRules, r.WarmedReaderVersion, e) ? p.Planned.GetValueOrDefault() : 0);
			if (num2 <= 0 || (status && num2 < (NewMinimum ?? NewShaders.Minimum(r.Plan?.Stats))))
			{
				return null;
			}
			obj = $"{num2:N0} new {NewKind(num2, p)}; compile again to include them";
		}
		goto IL_01fd;
		IL_01fd:
		return (string?)obj;
	}

	private static string NewKind(long n, Pending p)
	{
		string text = ((n == 1) ? "pipeline" : "pipelines");
		if (n != p.Upscaler)
		{
			if (n != p.Recorded - p.Upscaler)
			{
				if (p.Upscaler <= 0)
				{
					return text;
				}
				return $"{text} ({p.Upscaler:N0} upscaler)";
			}
			return text + " recorded";
		}
		return "upscaler " + text;
	}

	private string? PlannerChanged(GameRecord r, long? planned, EngineInfo? e)
	{
		if (CurrentPlanner(r.PlanVersion, r.PlanRules, r.PlanReaderVersion, e))
		{
			if (planned.HasValue)
			{
				long valueOrDefault = planned.GetValueOrDefault();
				if (valueOrDefault <= 0)
				{
					return null;
				}
				return $"SCSKiller can now compile {valueOrDefault:N0} more pipeline{((valueOrDefault == 1) ? "" : "s")} for this game";
			}
			return "SCSKiller can now compile more of this game";
		}
		return null;
	}

	private string? WarmChanged(Game g, GameRecord r, EngineInfo? e, GpuSnapshot? snapshot = null)
	{
		GpuSnapshot gpuSnapshot = snapshot ?? Snapshot();
		if (CurrentDriver(gpuSnapshot, r.WarmedDriverId, r.WarmedDriverVersion))
		{
			string text = TrimmedReason(g.Id, r);
			if (text == null)
			{
				if (!WarmMissesGame(r))
				{
					if (!r.AgsMissed || r.WarmedAgsApp == null)
					{
						string text2 = LayerNow(g);
						if (!(text2 != r.WarmedLayer))
						{
							string warmedExeName = r.WarmedExeName;
							if (warmedExeName == null || warmedExeName.Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase))
							{
								string text3 = WarmExeName(g, r);
								string text4 = r.WarmedExeName ?? Path.GetFileName(g.ExePath);
								if (!(text3 != text4) || !CaseMatters(g, r, text4))
								{
									if (Reread(r, e) || !(IndexStamp(g) != (r.WarmedIndexStamp ?? "")))
									{
										if (g.Version == null || r.WarmedGameVersion == null)
										{
											if (!(r.WarmedExeStamp != ExeStamp(g)))
											{
												return IndexChanged(r, e);
											}
											return "game updated since the warm";
										}
										if (!(g.Version != r.WarmedGameVersion))
										{
											return IndexChanged(r, e);
										}
										return $"game updated since the warm (build {r.WarmedGameVersion} -> {g.Version})";
									}
									return "game shaders changed since the warm";
								}
								return $"the game runs as {text3}, the warm filled the cache of {text4} (the driver keys it on the exe name's exact case)";
							}
							return "the last compile filled the cache of " + warmedExeName + ", but the game runs " + Path.GetFileName(g.ExePath);
						}
						if (text2 != null)
						{
							if (r.WarmedLayer != null)
							{
								return "the HDR mod changed since the compile";
							}
							return "the last compile didn't run through the game's HDR mod";
						}
						return "the HDR mod the last compile ran through is gone";
					}
					return AgsMissedReason(r);
				}
				return "the compile didn't reach this game's cache: the game uses another driver-cache key";
			}
			return text;
		}
		return "driver changed: " + r.WarmedDriverVersion + " -> " + gpuSnapshot.Gpu.DriverVersion;
	}

	private bool Reread(GameRecord r, EngineInfo? e)
	{
		if (e != null)
		{
			return (r.WarmedReaderVersion ?? 1) != _reader.ReaderVersion(e);
		}
		return false;
	}

	private bool NeedsPlanCheck(Game g, GameRecord r, EngineInfo? e)
	{
		bool flag = r.WarmedAt.HasValue;
		if (flag)
		{
			bool flag2 = !CurrentPlanner(r.PlanVersion, r.PlanRules, r.PlanReaderVersion, e) || r.PlanCommunity != CommunityObjects(g.Id) || r.PlanOfficial != OfficialInUse(g.Id)?.Object || OtherApi(r, e) || OtherEngine(r, e) || r.PlanFiltering != Filtering(g.Id, every: false, e);
			if (!flag2)
			{
				bool flag3 = r.PlanRayTracing != RayTracing(r);
				if (flag3)
				{
					PlanStats planStats = r.Plan?.Stats;
					bool flag4 = (((object)planStats != null && (planStats.RtLibraries > 0 || planStats.RtStateObjects > 0)) ? true : false);
					flag3 = flag4;
				}
				flag2 = flag3;
			}
			flag = flag2;
		}
		if (!flag || WarmChanged(g, r, e) != null)
		{
			return RtPlanCheck(r, e);
		}
		return true;
	}

	private static bool OtherApi(GameRecord r, EngineInfo? e)
	{
		if (r.PlanApi != null && e != null)
		{
			return r.PlanApi != ApiKey(e.GraphicsApi);
		}
		return false;
	}

	private static bool OtherEngine(GameRecord r, EngineInfo? e)
	{
		if (r.PlanEngine != null && e != null)
		{
			return r.PlanEngine != EngineKey(e);
		}
		return false;
	}

	private static string EngineKey(EngineInfo e)
	{
		return $"{e.Family}|{e.Version}|{e.Fork}";
	}

	private bool OlderPlanner(Game g, GameRecord r, EngineInfo? e)
	{
		if (r.WarmedAt.HasValue && r.WarmedPlanVersion != 42 && r.PlanVersion != 42)
		{
			return WarmChanged(g, r, e) == null;
		}
		return false;
	}

	private bool CurrentPlanner(int version, string? rules, int? reader, EngineInfo? e)
	{
		if (version == 42)
		{
			if (!(e == null))
			{
				if ((rules ?? RulesFile.Embedded.Hash(e)) == RulesFile.Current.Hash(e))
				{
					return (reader ?? 1) == _reader.ReaderVersion(e);
				}
				return false;
			}
			return true;
		}
		return false;
	}

	private string? RulesGrew(Game g, GameRecord r, EngineInfo e)
	{
		string text = RulesFile.Embedded.Hash(e);
		if (!((r.PlanRules ?? text) == (r.WarmedPlanRules ?? text)))
		{
			string warmKeysFile = r.WarmKeysFile;
			if (warmKeysFile != null)
			{
				HashSet<string> hashSet = KeyFiles.Set(Path.Combine(Store.GameDir(g.Id), warmKeysFile));
				if (hashSet != null)
				{
					long? planned = PendingOf(g, r, e).Planned;
					if (planned.HasValue)
					{
						long valueOrDefault = planned.GetValueOrDefault();
						if (valueOrDefault > hashSet.Count)
						{
							return $"SCSKiller can now compile {valueOrDefault:N0} more pipeline{((valueOrDefault == 1) ? "" : "s")} for this game: more than its last compile, so not while idle";
						}
					}
				}
			}
		}
		return null;
	}

	private void CheckPlan(string gameId, bool prompt = false)
	{
		lock (_lock)
		{
			if (_queue.Any((QueueItem q) => q.GameId == gameId && q.Stage != QueueStage.Done))
			{
				return;
			}
		}
		Add(gameId, whenIdle: true, planCheck: true, prompt);
	}

	public static string? PlanCheckLine(IEnumerable<QueueItem> queue)
	{
		List<QueueItem> list = queue.Where((QueueItem q) => q.PlanCheck && !Finished(q.Stage)).ToList();
		if (list != null)
		{
			int count = list.Count;
			if (count > 0)
			{
				return $"Checking {count} game{((count == 1) ? "" : "s")} for more to compile" + (list.All((QueueItem q) => q.Note == "starts when the PC is idle") ? " (while idle)" : "");
			}
		}
		return null;
	}

	public static IReadOnlyList<string> BulkRemovable(IEnumerable<QueueItem> queue, IReadOnlySet<string>? only = null)
	{
		return (from q in queue
			where !q.PlanCheck && !Format.Running(q) && (only == null || only.Contains(q.GameId))
			select q.GameId).ToList();
	}

	private static HashSet<string>? PlanKeys(string path)
	{
		try
		{
			return (from r in PlanFile.Read(path).Records
				where r.Tag != 'B'
				select r.Key).ToHashSet();
		}
		catch (Exception ex) when ((ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
		{
			return null;
		}
	}

	private static string PlanFingerprint(IEnumerable<string> keys)
	{
		return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(string.Concat(keys.Order(StringComparer.Ordinal)))));
	}

	private static string MapsFingerprint(ShaderIndex index)
	{
		return PlanFingerprint(index.Maps.Select((ShaderMap m) =>
		{
			Encoding uTF = Encoding.UTF8;
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(4, 6);
			defaultInterpolatedStringHandler.AppendFormatted(m.Hash);
			defaultInterpolatedStringHandler.AppendLiteral("|");
			defaultInterpolatedStringHandler.AppendFormatted(m.Library);
			defaultInterpolatedStringHandler.AppendLiteral("|");
			defaultInterpolatedStringHandler.AppendFormatted(m.Platform);
			defaultInterpolatedStringHandler.AppendLiteral("|");
			defaultInterpolatedStringHandler.AppendFormatted(m.IsPipeline);
			defaultInterpolatedStringHandler.AppendLiteral("|");
			defaultInterpolatedStringHandler.AppendFormatted(string.Join(',', m.Shaders));
			string rootSignature = m.RootSignature;
			defaultInterpolatedStringHandler.AppendFormatted((rootSignature != null) ? ("|" + rootSignature) : "");
			return Convert.ToHexStringLower(SHA1.HashData(uTF.GetBytes(defaultInterpolatedStringHandler.ToStringAndClear())));
		}));
	}

	private string? IndexChanged(GameRecord r, EngineInfo? e)
	{
		if (Reread(r, e) || !(r.IndexContentHash != r.WarmedIndexHash))
		{
			return null;
		}
		return "game shaders changed since the warm";
	}

	private bool CaseMatters(Game g, GameRecord r, string warmedExeName)
	{
		if (CaseSensitiveCache && r.WarmedAgsApp == null)
		{
			return AmdAppCache.IsNameHashed(r.CacheKeys, warmedExeName) != false;
		}
		return false;
	}

	public static string WarmExeName(Game g, GameRecord r)
	{
		string launchedExeName = r.LaunchedExeName;
		if (launchedExeName == null || !launchedExeName.Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase))
		{
			return Path.GetFileName(g.ExePath);
		}
		return launchedExeName;
	}

	private bool MergeLaunched(Game g, GameRecord rec, LaunchedExe? marker)
	{
		string fileName = Path.GetFileName(g.ExePath);
		string text = WarmExeName(g, rec);
		bool result = false;
		LaunchedExe[] array = new LaunchedExe[2]
		{
			marker,
			_launched.GetValueOrDefault(fileName)
		};
		foreach (LaunchedExe launchedExe in array)
		{
			if (!(launchedExe == null) && launchedExe.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
			{
				DateTimeOffset at = launchedExe.At;
				DateTimeOffset? launchedExeSeenAt = rec.LaunchedExeSeenAt;
				if (!(at <= launchedExeSeenAt))
				{
					string name = launchedExe.Name;
					launchedExeSeenAt = launchedExe.At;
					rec.LaunchedExeName = name;
					rec.LaunchedExeSeenAt = launchedExeSeenAt;
					result = true;
				}
			}
		}
		if (WarmExeName(g, rec) != text && CaseMatters(g, rec, text))
		{
			rec.ResumeAt = 0L;
		}
		return result;
	}

	private bool MergeAgsSeen(Game g, GameRecord rec, AgsRegistration? seen)
	{
		if (Vendor.Vendor == GpuVendor.Amd && !(seen == null))
		{
			string app = seen.App;
			string engine = seen.Engine;
			string agsSeenApp = rec.AgsSeenApp;
			string agsSeenEngine = rec.AgsSeenEngine;
			if (!(app == agsSeenApp) || !(engine == agsSeenEngine))
			{
				agsSeenEngine = seen.App;
				agsSeenApp = seen.Engine;
				rec.AgsSeenApp = agsSeenEngine;
				rec.AgsSeenEngine = agsSeenApp;
				string text = AmdAppCache.AgsKey(WarmExeName(g, rec), seen.App);
				List<string> list = rec.GameKeys.Where((string k) => k.StartsWith("dxc:", StringComparison.Ordinal)).ToList();
				Log?.Report($"{g.Name}: the game registered the AGS app name {seen.App} (engine {seen.Engine}), key {text}: " + (list.Contains(text) ? "the key it holds, so its compiles register it" : ((list.Count > 0) ? ("not the key it holds (" + string.Join(", ", list) + "), so not used") : "used once the game is seen holding that key")));
				return true;
			}
		}
		return false;
	}

	private HashSet<string> LearnKeysOfRunning(IReadOnlySet<string> running)
	{
		HashSet<string> hashSet = new HashSet<string>();
		if (AppCache == null || running.Count == 0)
		{
			return hashSet;
		}
		foreach (GameState game in Games)
		{
			string fileName = Path.GetFileName(game.Game.ExePath);
			if (running.Contains(fileName) && game.AntiCheat == AntiCheat.None && !IsUnchecked(game))
			{
				GameRecord gameRecord = Store.LoadGame(game.Game.Id);
				IReadOnlySet<string> readOnlySet = LearnKeys(game.Game, gameRecord, WarmExeName(game.Game, gameRecord));
				if (readOnlySet.Count != 0)
				{
					Store.SaveGame(game.Game.Id, gameRecord);
					hashSet.Add(game.Game.Id);
				}
			}
		}
		return hashSet;
	}

	private IReadOnlySet<string> LearnKeys(Game g, GameRecord rec, string exe)
	{
		return LearnKeys(g, rec, exe, OpenKeys(g, exe));
	}

	private IReadOnlySet<string> LearnKeys(Game g, GameRecord rec, string exe, IReadOnlySet<string> open)
	{
		HashSet<string> hashSet = open.Where((string k) => !rec.GameKeys.Contains(k)).ToHashSet();
		NoteProfile(g, exe, hashSet.Where((string k) => !rec.CacheKeys.Contains(k)).ToList());
		rec.CacheKeys.UnionWith(hashSet);
		rec.GameKeys.UnionWith(hashSet);
		return hashSet;
	}

	private IReadOnlySet<string> OpenKeys(Game g, string exe)
	{
		try
		{
			return AppCache?.KeysOpenBy(exe) ?? new HashSet<string>();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			Log?.Report(g.Name + ": cache attribution: " + ex.Message);
			return new HashSet<string>();
		}
	}

	private async Task LearnWhilePlaying(Game g)
	{
		while (IsPlaying(g.Id))
		{
			string exe = WarmExeName(g, Store.LoadGame(g.Id));
			IReadOnlySet<string> readOnlySet = OpenKeys(g, exe);
			if (readOnlySet != null && readOnlySet.Count > 0)
			{
				GameRecord gameRecord = Store.LoadGame(g.Id);
				if (LearnKeys(g, gameRecord, exe, readOnlySet).Count > 0)
				{
					Store.SaveGame(g.Id, gameRecord);
					Refresh(g);
				}
				OldCacheWhilePlaying(g);
				break;
			}
			await Task.Delay(AttributionInterval);
		}
	}

	public static bool WarmMissesGame(GameRecord r)
	{
		if (r.GameKeys.Count > 0)
		{
			HashSet<string> hashSet = r.WarmedKeys ?? r.CacheKeys.Except(r.GameKeys).ToHashSet();
			if (hashSet != null && hashSet.Count > 0)
			{
				return !hashSet.Overlaps(r.GameKeys);
			}
		}
		return false;
	}

	private bool CantReach(Game g, GameRecord r, GpuSnapshot gpu)
	{
		if (WarmMissesGame(r) && (!Vendor.Caps.PackageKeyed || g.Store != SCSKiller.Core.Store.Xbox) && ((r.WarmedDriverId == null && r.WarmedDriverVersion == null) || CurrentDriver(gpu, r.WarmedDriverId, r.WarmedDriverVersion)) && AgsFor(g, r)?.App == r.WarmedAgsApp)
		{
			return WarmExeName(g, r) == (r.WarmedExeName ?? Path.GetFileName(g.ExePath));
		}
		return false;
	}

	public static bool AgsLaunchMissed(GameRecord r)
	{
		if (r.WarmedAgsApp != null && !r.GameKeys.Any((string k) => k.StartsWith("dxc:", StringComparison.Ordinal)))
		{
			return IsPartlyWarmed(r.FirstLaunch);
		}
		return false;
	}

	private static string AgsMissedReason(GameRecord r)
	{
		string? warmedAgsApp = r.WarmedAgsApp;
		LaunchCheck firstLaunch = r.FirstLaunch;
		return "the compile didn't reach this game's cache (it registered the AGS app name " + warmedAgsApp + (((object)firstLaunch != null) ? $"; the first launch still compiled {firstLaunch.Compiled * 100.0:0}% of its pipelines" : "") + "): the next compile runs without it";
	}

	public static bool Unreached((int N, double MedianMs) warmed)
	{
		if ((long)warmed.N >= 100L)
		{
			return warmed.MedianMs >= 20.0;
		}
		return false;
	}

	public static bool IsUnreached(GameRecord r)
	{
		DateTimeOffset? unreachedWarm = r.UnreachedWarm;
		if (unreachedWarm.HasValue)
		{
			DateTimeOffset valueOrDefault = unreachedWarm.GetValueOrDefault();
			if (r.WarmedAt.HasValue)
			{
				return r.WarmedAt <= valueOrDefault;
			}
			return true;
		}
		return false;
	}

	private (int N, double MedianMs)? WarmedCreates(Game g, GameRecord rec, LaunchCheck first, string csv, IReadOnlySet<string>? rayQuery, GpuSnapshot gpu)
	{
		EngineInfo e;
		lock (_scanLock)
		{
			e = (_scan ?? (_scan = Store.LoadScan())).GetValueOrDefault(g.Id)?.Engine;
		}
		string warmKeysFile = rec.WarmKeysFile;
		if (warmKeysFile != null)
		{
			if (StaleReason(g, rec, e, null, gpu) == null && CurrentPlanner(rec.WarmedPlanVersion, rec.WarmedPlanRules, rec.WarmedReaderVersion, e))
			{
				DateTimeOffset? warmedAt = rec.WarmedAt;
				if (warmedAt.HasValue)
				{
					DateTimeOffset at = warmedAt.GetValueOrDefault();
					IAppCache? appCache = AppCache;
					if (appCache == null || !appCache.FilesOf(rec.WarmedKeys ?? rec.CacheKeys).Any((FileInfo f) => f.CreationTimeUtc > at.UtcDateTime))
					{
						try
						{
							HashSet<string> hashSet = KeyFiles.Set(Path.Combine(Store.GameDir(g.Id), warmKeysFile));
							if (hashSet == null)
							{
								return null;
							}
							HashSet<string> warmed = WarmedRecords(InputsNow(g, rec), hashSet, CrashInputs(CrashKeysNow(rec), rec.Plan?.FilePath), rayQuery);
							long cleared = rec.CacheClearedAt?.ToUnixTimeMilliseconds() ?? 0;
							return WarmedCreates(from l in SessionLog.Launches(csv, 0L)
								where !(l.Start < cleared)
								select l, first.At.ToUnixTimeMilliseconds(), warmed, rayQuery);
						}
						catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) ? true : false)
						{
							return null;
						}
					}
				}
			}
		}
		return null;
	}

	internal static HashSet<string> WarmedRecords(IEnumerable<string> inputs, IReadOnlySet<string> baseline, IReadOnlySet<string> crash, IReadOnlySet<string>? rayQuery = null)
	{
		Dictionary<string, bool> dictionary = new Dictionary<string, bool>();
		foreach (string input in inputs)
		{
			List<string> list = WarmInputs.Records(input).ToList();
			bool flag = WarmInputs.Taken(baseline, input, crash.Contains) && !crash.Contains(WarmInputs.Key(input)) && !list.Any((string r) => crash.Contains(r) || (rayQuery?.Contains(r) ?? false));
			foreach (string item in list)
			{
				dictionary[item] = dictionary.GetValueOrDefault(item, defaultValue: true) & flag;
			}
		}
		HashSet<string> hashSet = new HashSet<string>();
		foreach (string item2 in from x in dictionary
			where x.Value
			select x.Key)
		{
			hashSet.Add(item2);
		}
		return hashSet;
	}

	internal static (int N, double MedianMs) WarmedCreates(IEnumerable<SessionLog.CsvLaunch> launches, long start, IReadOnlySet<string> warmed, IReadOnlySet<string>? rayQuery)
	{
		HashSet<string> before = new HashSet<string>();
		foreach (SessionLog.CsvLaunch launch in launches)
		{
			if (launch.Start == start)
			{
				List<double> list = (from c in launch.Creates.Where(((double T, char Kind, double Ms, string Key, long? Tid, bool Presents) c) =>
					{
						bool flag = c.Key != null && char.IsUpper(c.Kind);
						if (flag)
						{
							char item = c.Kind;
							bool flag2 = ((item == 'A' || item == 'R') ? true : false);
							flag = !flag2;
						}
						return flag;
					}).DistinctBy(((double T, char Kind, double Ms, string Key, long? Tid, bool Presents) c) => c.Key).Where(((double T, char Kind, double Ms, string Key, long? Tid, bool Presents) c) =>
					{
						if (warmed.Contains(c.Key) && !before.Contains(c.Key))
						{
							IReadOnlySet<string>? readOnlySet = rayQuery;
							if (readOnlySet == null)
							{
								return true;
							}
							return !readOnlySet.Contains(c.Key);
						}
						return false;
					})
					select c.Ms).Order().ToList();
				return (N: list.Count, MedianMs: (list.Count > 0) ? list[list.Count / 2] : 0.0);
			}
			before.UnionWith(launch.Creates.Select(((double T, char Kind, double Ms, string Key, long? Tid, bool Presents) c) => c.Key).OfType<string>());
		}
		return (N: 0, MedianMs: 0.0);
	}

	public static string LinkTargetMissing(string dir)
	{
		return "the driver's cache folder " + dir + " is a link whose target is missing: reconnect that drive, or remove the link so the driver makes the folder again";
	}

	private string? TrimmedReason(string gameId, GameRecord r)
	{
		string text = Trimmed(gameId, r);
		if (text == null || KeptReason(r) != null)
		{
			return null;
		}
		return text;
	}

	private string? KeptReason(GameRecord r)
	{
		if (!(AppCache is AmdAppCache amdAppCache))
		{
			if (!OverCacheLimit(r))
			{
				return null;
			}
			return "this game's shaders don't fit the driver's shader cache, raise its size";
		}
		if (!amdAppCache.Full())
		{
			return null;
		}
		return "the driver's shader cache is full, so it removed older games' files";
	}

	private bool OverCacheLimit(GameRecord r)
	{
		if (!(AppCache is AmdAppCache))
		{
			long num = Math.Max(r.LastCacheGrowthBytes.GetValueOrDefault(), r.WarmedFileBytes?.Values.Sum() ?? 0);
			CacheLimit cacheLimit = Vendor.GetCacheLimit();
			if ((object)cacheLimit != null)
			{
				long? bytes = cacheLimit.Bytes;
				if (bytes.HasValue)
				{
					long valueOrDefault = bytes.GetValueOrDefault();
					return num >= valueOrDefault;
				}
			}
		}
		return false;
	}

	private string? Trimmed(string gameId, GameRecord r)
	{
		if (AppCacheFiles.MissingTarget(AppCache) != null)
		{
			return null;
		}
		IAppCache appCache = AppCache;
		string result;
		if (!(appCache is AmdAppCache amdAppCache))
		{
			if (appCache != null)
			{
				HashSet<string> warmedKeys = r.WarmedKeys;
				object obj;
				if (warmedKeys != null && warmedKeys.Count > 0)
				{
					IReadOnlyList<FileInfo> readOnlyList = appCache.FilesOf(warmedKeys);
					if (readOnlyList.Count != 0)
					{
						Dictionary<string, long> warmedFileBytes = r.WarmedFileBytes;
						obj = ((warmedFileBytes != null && warmedFileBytes.Count > 0 && warmedFileBytes.Any((KeyValuePair<string, long> w) => !readOnlyList.Any((FileInfo n) => n.Length >= w.Value && n.Name.Equals(w.Key, StringComparison.OrdinalIgnoreCase)))) ? "part of this game's compiled shader cache is gone, compile again" : null);
					}
					else
					{
						obj = "this game's compiled shader cache is gone, compile again";
					}
				}
				else
				{
					obj = null;
				}
				result = (string)obj;
			}
			else
			{
				result = null;
			}
			goto IL_0152;
		}
		HashSet<string> warmedFiles = r.WarmedFiles;
		object obj2;
		if (warmedFiles != null && warmedFiles.Count > 0)
		{
			IReadOnlyList<string> readOnlyList2 = amdAppCache.Missing(warmedFiles);
			if (readOnlyList2 != null && readOnlyList2.Count > 0)
			{
				AmdKept kept = Keeping(gameId);
				List<string> list = readOnlyList2.Where((string n) => !KeptSame(kept, r, n)).ToList();
				if (list != null && list.Count > 0)
				{
					obj2 = ((list.Count == warmedFiles.Count) ? "this game's compiled shader cache is gone, compile again" : "part of this game's compiled shader cache is gone, compile again");
					goto IL_00c9;
				}
			}
		}
		obj2 = null;
		goto IL_00c9;
		IL_00c9:
		result = (string)obj2;
		goto IL_0152;
		IL_0152:
		return result;
	}

	private void NoteProfile(Game g, string exe, IEnumerable<string> keys)
	{
		if (AmdAppCache.IsNameHashed(keys, exe) != false)
		{
			return;
		}
		IProgress<string>? log = Log;
		if (log != null)
		{
			string text = $"{g.Name}: the driver keys {exe}'s cache as {string.Join(", ", keys.Where((string k) => k.StartsWith("dxc:")))}, not its name hash {AmdAppCache.DxcKey(exe)} ";
			AgsRegistration agsRegistration = Ags(g);
			log.Report(text + (((object)agsRegistration != null && keys.Contains(AmdAppCache.AgsKey(Path.GetFileName(g.ExePath), agsRegistration.App))) ? ("(its AGS app name " + agsRegistration.App) : "(an app profile") + ": the exe name's case doesn't matter)");
		}
	}

	private AgsRegistration? Ags(Game g, GameRecord? r = null, GameRecord? seen = null)
	{
		if (Vendor.Vendor != GpuVendor.Amd)
		{
			return null;
		}
		GameRecord gameRecord = seen ?? r;
		if (gameRecord != null && gameRecord.AgsSeenApp != null)
		{
			AgsRegistration agsRegistration = AmdAgs.Learned(gameRecord.AgsSeenApp, gameRecord.AgsSeenEngine, WarmExeName(g, gameRecord), gameRecord.GameKeys);
			if ((object)agsRegistration != null)
			{
				return agsRegistration;
			}
		}
		EngineInfo engine;
		lock (_scanLock)
		{
			engine = _scan?.GetValueOrDefault(g.Id)?.Engine;
		}
		object obj = AmdAgs.Of(g, engine);
		if (obj == null)
		{
			if (r != null)
			{
				return AmdAgs.Recovered(g, engine, WarmExeName(g, r), r.GameKeys);
			}
			obj = null;
		}
		return (AgsRegistration?)obj;
	}

	public string? AgsKey(string gameId)
	{
		return AgsKey(Find(gameId).Game, Store.LoadGame(gameId));
	}

	private string? AgsKey(Game g, GameRecord? r)
	{
		AgsRegistration agsRegistration = Ags(g, r);
		if ((object)agsRegistration == null)
		{
			return null;
		}
		return AmdAppCache.AgsKey(Path.GetFileName(g.ExePath), agsRegistration.App);
	}

	private AgsRegistration? AgsFor(Game g, GameRecord r, bool recover = false)
	{
		return AgsFor(Ags(g, recover ? r : null, r), WarmExeName(g, r), r);
	}

	public static AgsRegistration? AgsFor(AgsRegistration? a, string warmExeName, GameRecord r)
	{
		if (a == null)
		{
			return null;
		}
		List<string> list = r.GameKeys.Where((string k) => k.StartsWith("dxc:", StringComparison.Ordinal)).ToList();
		if (list.Count > 0)
		{
			if (list.Contains(AmdAppCache.HintKey(warmExeName)) || (!list.Contains(AmdAppCache.AgsKey(warmExeName, a.App)) && (a.ExeUnread || !((r.WarmedAgsApp == null) ? WarmMissesGame(r) : (r.WarmedAgsApp == a.App)))))
			{
				return null;
			}
			return a;
		}
		if (a.ExeUnread || r.AgsMissed || !AmdAppCache.ProvenAgsApp(a.App))
		{
			return null;
		}
		return a;
	}

	public AgsRegistration? WarmAgs(string gameId)
	{
		return AgsFor(Find(gameId).Game, Store.LoadGame(gameId), recover: true);
	}

	private static string ExeStamp(Game g)
	{
		FileInfo fileInfo = new FileInfo(g.ExePath);
		if (fileInfo == null || !fileInfo.Exists)
		{
			return "";
		}
		return $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
	}

	public IReadOnlyList<GameState> StaleGames()
	{
		return Games.Where((GameState s) => s.Status == GameStatus.Stale).ToList();
	}

	public IReadOnlyList<GameState> DriverStaleGames()
	{
		lock (_lock)
		{
			return _games.Where((GameState s) => s.Status == GameStatus.Stale && _driverStale.GetValueOrDefault(s.Game.Id) && s.NoStutter == null).ToList();
		}
	}

	public IReadOnlyList<GameState> IdleRewarmGames()
	{
		return DriverStaleGames().Where((GameState s) =>
		{
			EngineInfo engine = s.Engine;
			return (object)engine == null || Store.LoadGame(s.Game.Id).IdleHeldBack != HeldBackKey(RulesFile.Current.Hash(engine));
		}).ToList();
	}

	private string HeldBackKey(string rules)
	{
		return DriverId + "|" + rules;
	}

	private string StaleKey(GameState s)
	{
		return DriverId + "|" + (s.Game.Version ?? ExeStamp(s.Game));
	}

	public IReadOnlyList<CachePart> GameCaches(string gameId, bool gamePrecache = false)
	{
		GameState gameState = Find(gameId);
		GameRecord rec = Store.LoadGame(gameId);
		List<CachePart> list = new List<CachePart>();
		if (AppCache != null)
		{
			HashSet<string> item = DriverKeys(gameState.Game, rec).Keys;
			if (item != null && item.Count > 0)
			{
				list.Add(Part("Driver cache", AppCache.FilesOf(item)));
			}
		}
		if (gameState.AntiCheat != AntiCheat.None)
		{
			return list;
		}
		list.Add(Part("Windows shader cache", D3DSCache.FoldersOf(Path.Combine(LocalAppData, "D3DSCache"), gameState.Game).SelectMany(D3DSCache.CacheFiles)));
		if (!gamePrecache)
		{
			return list.Where((CachePart p) => p.Files.Count > 0).ToList();
		}
		list.Add(Part("Game's pipeline cache", from f in OwnPipelineCaches.FilesOf(gameState.Game, gameState.Engine, Documents, RoamingAppData)
			select new FileInfo(f)));
		HashSet<string> others = Games.Where((GameState o) => o.Game.Id != gameId).SelectMany((GameState o) => UnrealUserCache.Folders(o.Game)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		Game game = gameState.Game;
		string[] array = SavedRoots(gameState.Game);
		int num = 0;
		string[] array2 = new string[1 + array.Length];
		ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(array);
		readOnlySpan.CopyTo(new Span<string>(array2).Slice(num, readOnlySpan.Length));
		num += readOnlySpan.Length;
		array2[num] = ProgramData;
		list.Add(Part("Game's shader precache", from f in UnrealUserCache.PrecacheFilesOf(game, new _003C_003Ez__ReadOnlyArray<string>(array2), from n in UnrealUserCache.Folders(gameState.Game)
				where !others.Contains(n)
				select n)
			select new FileInfo(f)));
		return list.Where((CachePart p) => p.Files.Count > 0).ToList();
	}

	private static CachePart Part(string name, IEnumerable<FileInfo> files)
	{
		List<FileInfo> source = files.ToList();
		return new CachePart(name, source.Select((FileInfo f) => f.FullName).ToList(), source.Sum((FileInfo f) => f.Length));
	}

	private string[] SavedRoots(Game g)
	{
		if (g.Store == SCSKiller.Core.Store.Xbox && g.Id.StartsWith("xbox:", StringComparison.Ordinal))
		{
			string[] array = new string[3] { LocalAppData, MyGames, null };
			InlineArray5<string> buffer = default;
			buffer[0] = LocalAppData;
			buffer[1] = "Packages";
			buffer[2] = AppStore.Segment(g.Id.Substring("xbox:".Length));
			buffer[3] = "LocalCache";
			buffer[4] = "Local";
			array[2] = Path.Combine(buffer);
			return array;
		}
		return new string[2] { LocalAppData, MyGames };
	}

	public bool ClearGameCache(string gameId, bool gamePrecache = false)
	{
		string text = PairHoldsCache(gameId);
		if (text != null)
		{
			throw new InvalidOperationException(text);
		}
		return ClearCache(gameId, gamePrecache);
	}

	private bool ClearCache(string gameId, bool gamePrecache)
	{
		GameState gameState = Find(gameId);
		GameRecord rec = Store.LoadGame(gameId);
		Path.GetFileName(gameState.Game.ExePath);
		lock (_lock)
		{
			if (_current == gameId)
			{
				throw new InvalidOperationException("a compile of " + gameState.Game.Name + " is in progress");
			}
		}
		if (GameRunning(gameState.Game))
		{
			throw new InvalidOperationException(gameState.Game.Name + " is running");
		}
		HashSet<string> item = DriverKeys(gameState.Game, rec).Keys;
		bool flag = AppCache != null && item.Count > 0;
		if (flag)
		{
			List<(string, List<string>)> list = SharedWith(gameId, item);
			if (list != null && list.Count > 0)
			{
				throw new InvalidOperationException(gameState.Game.Name + "'s driver cache is shared with " + string.Join(", ", list.Select(((string Name, List<string> Keys) x) => x.Name + " (" + string.Join(", ", x.Keys) + ")")) + ": the driver gives their exes the same key (an app profile, or the same exe name), so clearing it would clear theirs too");
			}
		}
		IReadOnlyList<CachePart> readOnlyList = GameCaches(gameId, gamePrecache);
		if (!flag && readOnlyList.Count == 0)
		{
			return false;
		}
		AppCacheFiles.DeleteAll((from f in readOnlyList.SelectMany((CachePart p) => p.Files)
			select new FileInfo(f)).ToList());
		foreach (string item2 in readOnlyList.Where((CachePart p) => p.Name == "Windows shader cache").SelectMany((CachePart p) => p.Files).Select(Path.GetDirectoryName)
			.Distinct())
		{
			try
			{
				Directory.Delete(item2);
			}
			catch (IOException)
			{
			}
		}
		if (!flag)
		{
			return true;
		}
		ForgetWarm(gameId, rec);
		Refresh(gameState.Game);
		return true;
	}

	private void ForgetWarm(string gameId, GameRecord rec)
	{
		if (!IsUnreached(rec))
		{
			rec.UnreachedWarm = null;
		}
		GameRecord gameRecord = rec;
		GameRecord gameRecord2 = rec;
		GameRecord gameRecord3 = rec;
		rec.WarmedAt = null;
		gameRecord.LastWarmTime = null;
		gameRecord2.LastCacheGrowthBytes = null;
		gameRecord3.ResumeAt = 0L;
		if (!WarmMissesGame(rec))
		{
			gameRecord3 = rec;
			rec.WarmedDriverVersion = null;
			gameRecord3.WarmedDriverId = null;
		}
		gameRecord3 = rec;
		gameRecord2 = rec;
		gameRecord = rec;
		GameRecord gameRecord4 = rec;
		GameRecord gameRecord5 = rec;
		rec.LastWarmFailed = null;
		gameRecord3.LastWarmSkipped = null;
		gameRecord2.LastWarmNeedsRecording = null;
		gameRecord.LastWarmCrashed = null;
		gameRecord4.LastWarmDriverSkipped = null;
		gameRecord5.LastWarmUnbuilt = null;
		gameRecord5 = rec;
		gameRecord4 = rec;
		gameRecord = rec;
		gameRecord2 = rec;
		DateTimeOffset? cacheClearedAt = Clock();
		rec.WarmedCareful = false;
		gameRecord5.FirstLaunch = null;
		gameRecord4.WarmedFiles = null;
		gameRecord.WarmedFileIds = null;
		gameRecord2.CacheClearedAt = cacheClearedAt;
		Store.SaveGame(gameId, rec);
		DropKept(gameId);
	}

	public void SetCarefulCompile(string gameId, bool on)
	{
		GameState gameState = Find(gameId);
		if (!CarefulThreads(Vendor.Vendor).HasValue)
		{
			throw new InvalidOperationException("the careful compile is for AMD drivers, not " + Vendor.Gpu.Name);
		}
		lock (_lock)
		{
			if (_current == gameId)
			{
				throw new InvalidOperationException("a compile of " + gameState.Game.Name + " is in progress");
			}
		}
		GameRecord gameRecord = Store.LoadGame(gameId);
		if (gameRecord.Careful != on)
		{
			GameRecord gameRecord2 = gameRecord;
			bool careful = on;
			gameRecord.Careful = careful;
			gameRecord2.ResumeAt = 0L;
			Store.SaveGame(gameId, gameRecord);
			Refresh(gameState.Game);
		}
	}

	public void SetNewShadersMode(string gameId, NewShadersMode? value)
	{
		GameState gameState = Find(gameId);
		GameRecord gameRecord = Store.LoadGame(gameId);
		if (gameRecord.NewShaders != value)
		{
			gameRecord.NewShaders = value;
			Store.SaveGame(gameId, gameRecord);
			Refresh(gameState.Game);
		}
	}

	public void SetApiOverride(string gameId, DxApi? value)
	{
		GameState gameState = Find(gameId);
		lock (_lock)
		{
			if (_current == gameId)
			{
				throw new InvalidOperationException("a compile of " + gameState.Game.Name + " is in progress");
			}
		}
		GameRecord gameRecord = Store.LoadGame(gameId);
		if (gameRecord.ApiOverride == value)
		{
			return;
		}
		GameRecord gameRecord2 = gameRecord;
		DxApi? apiOverride = value;
		gameRecord.ApiOverride = apiOverride;
		gameRecord2.ResumeAt = 0L;
		if (gameRecord.Plan != null)
		{
			gameRecord2 = gameRecord;
			if (gameRecord2.PlanApi == null)
			{
				string text = (gameRecord2.PlanApi = ApiKey(gameState.Engine?.GraphicsApi));
			}
		}
		Store.SaveGame(gameId, gameRecord);
		Refresh(gameState.Game, default, force: false, prompt: true);
		if (CheckPlans)
		{
			GameRecord gameRecord3 = Store.LoadGame(gameId);
			if (gameRecord3 != null && (object)gameRecord3.Plan != null && !gameRecord3.WarmedAt.HasValue)
			{
				EngineInfo engine = Find(gameId).Engine;
				if ((object)engine != null && OtherApi(gameRecord3, engine))
				{
					CheckPlan(gameId, prompt: true);
				}
			}
		}
		lock (_recorderLock)
		{
			if (!Reconcile(Find(gameId)))
			{
				Refresh(gameState.Game);
			}
		}
	}

	private List<(string Name, List<string> Keys)> SharedWith(string gameId, IReadOnlySet<string> keys)
	{
		List<(string, List<string>)> list = new List<(string, List<string>)>();
		string fileName = Path.GetFileName(Find(gameId).Game.ExePath);
		foreach (GameState game in Games)
		{
			if (!(game.Game.Id == gameId))
			{
				GameRecord other = Store.LoadGame(game.Game.Id);
				string hint = AmdAppCache.HintKey(WarmExeName(game.Game, other));
				string ags = ((AgsFor(game.Game, other) != null) ? AgsKey(game.Game, null) : null);
				bool sameName = Path.GetFileName(game.Game.ExePath).Equals(fileName, StringComparison.OrdinalIgnoreCase);
				List<string> list2 = keys.Where((string k) => (sameName && (!(AppCache is AmdAppCache) || k.StartsWith("dx:", StringComparison.Ordinal))) || other.CacheKeys.Contains(k) || k == hint || k == ags).Order().ToList();
				if (list2.Count > 0)
				{
					list.Add((game.Game.Name, list2));
				}
			}
		}
		return list;
	}

	private (HashSet<string> Keys, string? Gap) DriverKeys(Game g, GameRecord rec)
	{
		HashSet<string> hashSet = rec.CacheKeys.ToHashSet();
		string fileName = Path.GetFileName(g.ExePath);
		IAppCache appCache = AppCache;
		AmdAppCache amd = appCache as AmdAppCache;
		if (amd == null)
		{
			return (Keys: hashSet, Gap: (AppCache == null || hashSet.Count > 0) ? null : ("the driver cache key of " + fileName + " isn't learned yet (SCSKiller learns it when it compiles the game or sees it running): the driver cache is not cleared"));
		}
		List<string> source = new string[3] { fileName, rec.LaunchedExeName, rec.WarmedExeName }.OfType<string>().Distinct().ToList();
		List<string> dxc = hashSet.Where((string k) => k.StartsWith("dxc:", StringComparison.Ordinal)).ToList();
		AgsRegistration agsRegistration = AgsFor(g, rec);
		object obj;
		if ((object)agsRegistration != null)
		{
			string text = AgsKey(g, null);
			if (text != null && !hashSet.Contains(text) && (dxc.Count == 0 || amd.FilesOf(new _003C_003Ez__ReadOnlySingleElementList<string>(text)).Count > 0))
			{
				obj = $"the D3D12 key {text} that {g.Name} gets from its AGS app name {agsRegistration.App} isn't learned yet (SCSKiller learns it when it compiles the game or sees it running): it is not cleared";
				goto IL_017b;
			}
		}
		obj = null;
		goto IL_017b;
		IL_017b:
		string text2 = (string)obj;
		if (dxc.Count > 0 && !source.Any((string n) => dxc.Contains(AmdAppCache.DxcKey(n))))
		{
			return (Keys: hashSet, Gap: text2);
		}
		hashSet.UnionWith(from k in source.Select(AmdAppCache.HintKey)
			where amd.FilesOf(new _003C_003Ez__ReadOnlySingleElementList<string>(k)).Count > 0
			select k);
		return (Keys: hashSet, Gap: (dxc.Count > 0 || text2 != null) ? text2 : ("the D3D12 driver cache key of " + fileName + " isn't learned yet: clearing takes the key the driver gives that exe name; a driver app profile's key or a D3D11 key would be missed"));
	}

	public string? DriverCacheGap(string gameId)
	{
		return DriverKeys(Find(gameId).Game, Store.LoadGame(gameId)).Gap;
	}

	public bool SetEncryptionKey(string gameId, string key)
	{
		Game game = Games.FirstOrDefault((GameState s) => s.Game.Id == gameId)?.Game ?? throw new ArgumentException("unknown game " + gameId);
		return ((_reader as UnrealReader) ?? (_reader as EngineReaders)?.Get<UnrealReader>())?.SetKey(game, key) ?? false;
	}

	public void DismissStale()
	{
		Store.SaveDismissed(StaleGames().ToDictionary((GameState s) => s.Game.Id, StaleKey));
	}

	public bool ShouldNotifyStale()
	{
		if (Settings.OnDriverUpdate != DriverUpdateMode.Ask)
		{
			return false;
		}
		Dictionary<string, string> skipped = Store.LoadDismissed();
		return DriverStaleGames().Any((GameState s) => !skipped.TryGetValue(s.Game.Id, out string value) || value != StaleKey(s));
	}

	public void ApplyDriverUpdateMode()
	{
		if (Settings.OnDriverUpdate == DriverUpdateMode.Off)
		{
			ScheduledTask.Unregister();
		}
		else
		{
			ScheduledTask.Register(ScheduledTask.TaskExe() ?? throw new FileNotFoundException("the command-line tool (scskiller.exe) is not next to the app"));
		}
	}

	private void Refresh(Game g, CancellationToken ct = default(CancellationToken), bool force = false, bool prompt = false)
	{
		long num = Ticket();
		GameState gameState = Evaluate(g, force, out var _, out var driverStale, ct);
		GameState gameState2;
		lock (_lock)
		{
			if (_evaluatedAt.GetValueOrDefault(g.Id) > num || Unlisted(g.Id) || StaleCopy(g) || (IsUnchecked(gameState) && _evaluatedAt.ContainsKey(g.Id)))
			{
				return;
			}
			if (!IsUnchecked(gameState))
			{
				Dictionary<string, long> evaluatedAt = _evaluatedAt;
				string id = g.Id;
				Dictionary<string, bool> driverStale2 = _driverStale;
				string id2 = g.Id;
				long value = num;
				bool value2 = driverStale;
				evaluatedAt[id] = value;
				driverStale2[id2] = value2;
			}
			gameState = WithVerdict(gameState);
			int num2 = _games.FindIndex((GameState x) => x.Game.Id == g.Id);
			gameState2 = ((num2 >= 0) ? _games[num2] : null);
			if (num2 >= 0)
			{
				_games[num2] = gameState;
			}
			else
			{
				_games.Add(gameState);
			}
		}
		GameChanged?.Invoke(gameState);
		if (gameState2?.RecorderInstalled != gameState.RecorderInstalled)
		{
			(IReadOnlyList<CollectionGame>, CollectionGame)? tuple = Collections.Member(g.Id);
			if (tuple.HasValue)
			{
				(IReadOnlyList<CollectionGame>, CollectionGame) valueOrDefault = tuple.GetValueOrDefault();
				if (valueOrDefault.Item1[0].Id == g.Id)
				{
					foreach (Game item in (from e in valueOrDefault.Item1.Skip(1)
						select Games.FirstOrDefault((GameState x) => x.Game.Id == e.Id)?.Game).OfType<Game>())
					{
						Refresh(item, ct);
					}
				}
			}
		}
		if (CheckPlans && (prompt || !Settings.HddMode) && (object)gameState != null && (object)gameState.Engine != null && gameState.Status != GameStatus.Unsupported && (gameState.RtToPlan || NeedsPlanCheck(g, Store.LoadGame(g.Id), gameState.Engine)))
		{
			CheckPlan(g.Id, prompt);
		}
	}

	private GameState WithVerdict(GameState s)
	{
		if (s.AntiCheat != AntiCheat.None || !_verdicts.TryGetValue(s.Game.Id, out var value))
		{
			return s;
		}
		return s with
		{
			AntiCheat = value
		};
	}

	private long Ticket()
	{
		return Interlocked.Increment(ref _tickets);
	}

	private void Publish(long started, long discovering, List<GameState> states)
	{
		if (started > _listed)
		{
			List<GameState> list = new List<GameState>();
			list.AddRange(states);
			list.AddRange(_games.Where((GameState g) => _evaluatedAt.GetValueOrDefault(g.Game.Id) > discovering && !Unlisted(g.Game.Id) && !states.Any((GameState gameState) => gameState.Game.Id == g.Game.Id)));
			List<GameState> games = list;
			_listed = started;
			_games = games;
			return;
		}
		foreach (GameState s in states)
		{
			int num = _games.FindIndex((GameState x) => x.Game.Id == s.Game.Id);
			if (num >= 0)
			{
				_games[num] = s;
			}
		}
	}

	private List<GameState> Newest(List<GameState> states, List<long> tickets, List<bool> driverStale)
	{
		List<GameState> list = new List<GameState>(states.Count);
		for (int i = 0; i < states.Count; i++)
		{
			string id = states[i].Game.Id;
			if (Unlisted(id))
			{
				continue;
			}
			if (StaleCopy(states[i].Game))
			{
				GameState gameState = _games.Find((GameState x) => x.Game.Id == id);
				if ((object)gameState != null)
				{
					list.Add(gameState);
				}
				continue;
			}
			if (_evaluatedAt.GetValueOrDefault(id) > tickets[i] || (IsUnchecked(states[i]) && _evaluatedAt.ContainsKey(id)))
			{
				GameState gameState2 = _games.Find((GameState x) => x.Game.Id == id);
				if ((object)gameState2 != null)
				{
					states[i] = gameState2;
					goto IL_0134;
				}
			}
			if (!IsUnchecked(states[i]))
			{
				Dictionary<string, long> evaluatedAt = _evaluatedAt;
				string key = id;
				Dictionary<string, bool> driverStale2 = _driverStale;
				string key2 = id;
				long value = tickets[i];
				bool value2 = driverStale[i];
				evaluatedAt[key] = value;
				driverStale2[key2] = value2;
			}
			goto IL_0134;
			IL_0134:
			list.Add(WithVerdict(states[i]));
		}
		return list;
	}

	private Game Current(Game g)
	{
		return Discovered(g) ?? Following(g);
	}

	private Game? Discovered(Game g)
	{
		foreach (IGameSource item in _sources.Where((IGameSource s) => s.Store == g.Store))
		{
			try
			{
				Game game = item.Discover().FirstOrDefault((Game x) => x.Id == g.Id);
				if ((object)game != null)
				{
					return Following(Unreal(game));
				}
			}
			catch (Exception ex)
			{
				Log?.Report($"{item.Store}: discovery failed: {ex.Message}");
			}
		}
		return null;
	}

	private Game Following(Game g)
	{
		GameRecord gameRecord = Store.LoadGame(g.Id);
		string runsExe = gameRecord.RunsExe;
		if (runsExe == null)
		{
			return g;
		}
		if (File.Exists(runsExe) && GameFiles.Inside(g.InstallDir, runsExe) && !(gameRecord.RunsExeFrom != g.ExePath) && !(gameRecord.RunsExeBuild != Build(g, runsExe)))
		{
			string[]? array = Collections.Exes(g);
			if ((array == null || array.Contains(Path.GetFileName(runsExe), StringComparer.OrdinalIgnoreCase)) && !LeavesUnreal(g.ExePath, runsExe))
			{
				if (!Unconfirmed(g))
				{
					return g with
					{
						ExePath = runsExe
					};
				}
				return g;
			}
		}
		GameRecord gameRecord2 = gameRecord;
		GameRecord gameRecord3 = gameRecord;
		GameRecord gameRecord4 = gameRecord;
		GameRecord gameRecord5 = gameRecord;
		gameRecord.RunsExe = null;
		gameRecord2.RunsExeFrom = null;
		gameRecord3.RunsExeBuild = null;
		gameRecord4.RunsExeAt = null;
		gameRecord5.RunsExeWas = null;
		Store.SaveGame(g.Id, gameRecord);
		Log?.Report(LeavesUnreal(g.ExePath, runsExe) ? $"{g.Name}: no longer following {runsExe}: {NotUnrealReason(g)}" : (g.Name + ": no longer following " + runsExe + ": the game changed since it ran from there"));
		return g;
	}

	private static bool LeavesUnreal(string from, string to)
	{
		if (!GameFiles.IsShipping(from) || GameFiles.IsShipping(to))
		{
			if (GameFiles.InBinaries(from))
			{
				return !GameFiles.InBinaries(to);
			}
			return false;
		}
		return true;
	}

	private static string NotUnrealReason(Game g)
	{
		return "the game is " + Path.GetFileName(g.ExePath) + ", an Unreal " + (GameFiles.IsShipping(g.ExePath) ? "Shipping exe" : "game exe in a Binaries folder");
	}

	private static string Build(Game g, string exe)
	{
		string text = g.Version;
		if (text == null)
		{
			FileInfo fileInfo = new FileInfo(exe);
			if (fileInfo == null || !fileInfo.Exists)
			{
				return "";
			}
			text = $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
		}
		return text;
	}

	private bool FollowRunningExe(GameState s, DateTimeOffset at)
	{
		Game g = s.Game;
		if (s.AntiCheat != AntiCheat.None || Unconfirmed(g))
		{
			return true;
		}
		string name = Path.GetFileName(g.ExePath);
		string dir = Path.GetDirectoryName(g.ExePath);
		List<(int, int, string)> source = Processes(arg: false);
		List<(int, int, string)> list = source.Where(((int Pid, int Parent, string Exe) p) => p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
		List<(int, int, string)> list2 = source.Where(((int Pid, int Parent, string Exe) p) =>
		{
			if (!p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, p.Exe)) && !LauncherOrHelper(g, p.Exe))
			{
				string[]? array = Collections.Exes(g);
				if (array == null)
				{
					return true;
				}
				return array.Contains(p.Exe, StringComparer.OrdinalIgnoreCase);
			}
			return false;
		}).ToList();
		if (list.Count + list2.Count == 0)
		{
			return false;
		}
		if (GameFiles.DetectAntiCheat(g, quick: true) != AntiCheat.None)
		{
			return true;
		}
		List<string> list3 = (from p in list.Select(((int Pid, int Parent, string Exe) p) => ProcessPath(p.Pid)).OfType<string>()
			where GameFiles.Inside(g.InstallDir, p) && !LeavesUnreal(g.ExePath, p)
			select p).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (list3.Count == 0)
		{
			list3 = (from p in list2.Select(((int Pid, int Parent, string Exe) p) => ProcessPath(p.Pid)).OfType<string>()
				where dir.Equals(Path.GetDirectoryName(p), StringComparison.OrdinalIgnoreCase) && !LeavesUnreal(g.ExePath, p)
				select p).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		}
		if (list3.Contains(g.ExePath, StringComparer.OrdinalIgnoreCase))
		{
			return true;
		}
		if (list3 != null && list3.Count == 1)
		{
			string text = list3[0];
			GameRecord gameRecord = Store.LoadGame(g.Id);
			gameRecord.RunsExeFrom = ((gameRecord.RunsExe != null) ? gameRecord.RunsExeFrom : g.ExePath);
			GameRecord gameRecord2 = gameRecord;
			GameRecord gameRecord3 = gameRecord;
			GameRecord gameRecord4 = gameRecord;
			string runsExe = text;
			string runsExeBuild = Build(g, text);
			DateTimeOffset? runsExeAt = at;
			string exePath = g.ExePath;
			gameRecord.RunsExe = runsExe;
			gameRecord2.RunsExeBuild = runsExeBuild;
			gameRecord3.RunsExeAt = runsExeAt;
			gameRecord4.RunsExeWas = exePath;
			Store.SaveGame(g.Id, gameRecord);
			string value = (dir.Equals(Path.GetDirectoryName(text), StringComparison.OrdinalIgnoreCase) ? "is set up for it" : "moves there");
			Log?.Report($"{g.Name}: runs {text}, not {g.ExePath}: following it (the recorder {value} once the game exits)");
			RecorderLog($"{g.Name}: the game runs {text}, not {g.ExePath}");
			return true;
		}
		return false;
	}

	private static bool LauncherOrHelper(Game g, string exe)
	{
		if (!XboxSource.LauncherOrUtility(exe))
		{
			return GameFiles.NotTheGameExe(g.InstallDir, Path.Combine(Path.GetDirectoryName(g.ExePath), exe));
		}
		return true;
	}

	private GameState Find(string gameId)
	{
		return Games.FirstOrDefault((GameState s) => s.Game.Id == gameId) ?? throw new ArgumentException("unknown game '" + gameId + "' (scan first)");
	}

	public void RenameManualGame(string gameId, string name)
	{
		if (Find(gameId).Game.Store == SCSKiller.Core.Store.Manual)
		{
			ManualSource manual = Manual;
			if (manual != null)
			{
				manual.Rename(gameId, name);
				GameState gameState = null;
				lock (_lock)
				{
					int num = _games.FindIndex((GameState x) => x.Game.Id == gameId);
					if (num >= 0)
					{
						List<GameState> games = _games;
						GameState gameState2 = _games[num]with
						{
							Game = _games[num].Game with
							{
								Name = name.Trim()
							}
						};
						GameState gameState3 = gameState2;
						games[num] = gameState2;
						gameState = gameState3;
					}
				}
				if (gameState != null)
				{
					GameChanged?.Invoke(gameState);
				}
				KeepList();
				return;
			}
		}
		throw new InvalidOperationException("only a game you added can be renamed");
	}

	private string RecordingPath(string gameId)
	{
		return Path.Combine(Store.GameDir(gameId), "recording.db");
	}

	private void AdoptManual(Game added, Game owner)
	{
		string text = RecordingPath(added.Id);
		string text2 = RecordingPath(owner.Id);
		string text3 = text;
		string dir = Path.GetDirectoryName(added.ExePath);
		try
		{
			if (File.Exists(text3))
			{
				using (Recordings.Lock(text2))
				{
					using (Recordings.Lock(text3))
					{
						Directory.CreateDirectory(Store.GameDir(owner.Id));
						Recordings.Merge(text2, text3, null);
						Imported(owner);
						File.Delete(text3);
						RecorderLog(owner.Name + ": the recording made when you added it by hand now belongs to its store entry");
					}
				}
			}
			if (Foreign(dir))
			{
				return;
			}
			GameRecord gameRecord = Store.LoadGame(added.Id);
			if (gameRecord.RecorderFiles.Count > 0 || gameRecord.RecorderChained != null || gameRecord.RecorderExe != null || ProxyOnDisk(added) || ProxyOnDisk(RecordedAt(added, gameRecord)))
			{
				TakeOutNow(added, "its store entry took it over", null, anyCopys: false);
				gameRecord = Store.LoadGame(added.Id);
				if (gameRecord.RecorderFiles.Count > 0 || gameRecord.RecorderChained != null || gameRecord.RecorderRollback || ProxyOnDisk(added) || ProxyOnDisk(RecordedAt(added, gameRecord)))
				{
					return;
				}
			}
			if (!RecorderDataFiles.Concat(Recordings.AppFiles).Append("scskiller.armed").Append("scskiller.ledger")
				.Any((string f) => File.Exists(Path.Combine(dir, f))) || GameRunning(added))
			{
				return;
			}
			FileInfo fileInfo = new FileInfo(Path.Combine(dir, "scskiller.db"));
			bool flag = fileInfo != null && fileInfo.Exists && fileInfo.Length > 0 && $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}" != gameRecord.RecordingInbox;
			Directory.CreateDirectory(Store.GameDir(owner.Id));
			if (RemoveRecorderData(dir, text2, gameRecord.RecordingInbox, () => GameRunning(added), IniPending(Store, dir)))
			{
				File.Delete(Path.Combine(dir, "scskiller.armed"));
				if (flag)
				{
					Imported(owner);
				}
				RecorderLog(owner.Name + ": the recorder's data files removed (" + dir + "), left by the copy you added by hand");
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) ? true : false)
		{
			RecorderLog(owner.Name + ": couldn't clear what SCSKiller kept for it as a game you added (tried again at the next scan): " + ex.Message);
		}
	}

	private void Imported(Game g)
	{
		GameRecord gameRecord = Store.LoadGame(g.Id);
		gameRecord.RecordingImportedAt = DateTimeOffset.Now;
		Store.SaveGame(g.Id, gameRecord);
	}

	private string RayQueryKeysPath(string gameId)
	{
		return Path.Combine(Store.GameDir(gameId), "rayquery.keys");
	}

	private PlanCheck CheckRecordings(Game g, EngineInfo engine)
	{
		List<Recording> source = (from f in new string[1] { RecordingPath(g.Id) }.Concat(Downloads(g.Id)).Where(File.Exists)
			select new Recording(f)).ToList();
		PlanCheck planCheck = _planner.Check(g, engine, source.FirstOrDefault(), Vendor.Caps);
		foreach (Recording item in source.Skip(1))
		{
			if (planCheck.Readiness == Readiness.NeedsRecording)
			{
				planCheck = _planner.Check(g, engine, item, Vendor.Caps);
			}
		}
		return planCheck;
	}

	private Recording? PrepareRecording(Game game, EngineInfo engine, ShaderIndex index, string work, bool rayTracing, bool every, CancellationToken ct)
	{
		string text = RecordingPath(game.Id);
		List<string> list = Downloads(game.Id);
		if (!File.Exists(text) && list.Count == 0)
		{
			return null;
		}
		Directory.CreateDirectory(work);
		string text2 = text;
		HashSet<string> hashSet = ForeignVariants(game, every, engine);
		if (list.Count > 0)
		{
			SCSKiller.Core.App.Community.Union(text, list, text2 = Path.Combine(work, "merged.db"), hashSet);
		}
		if (hashSet.Count > 0)
		{
			Log?.Report($"{game.Name}: {hashSet.Count:N0} community recording pipelines made at another texture filtering setting than the one compiled left out" + ((every || File.Exists(text)) ? "" : " (Maximum mode compiles them)"));
		}
		if (!rayTracing)
		{
			string text3 = Path.Combine(work, "no-rt.db");
			long num = 0L;
			using (BufferedStream s = new BufferedStream(File.Create(text3), 1048576))
			{
				foreach (PsoDb.Rec item in PsoDb.Read(text2))
				{
					if (Planner.LeftOutWithoutRt(item))
					{
						num++;
					}
					else
					{
						PsoDb.Write(s, item.Tag, item.Payload);
					}
				}
			}
			if (text2 != text)
			{
				File.Delete(text2);
			}
			text2 = text3;
			if (num > 0)
			{
				Log?.Report($"{game.Name}: {"Ray tracing skipped (your setting)"}, {num:N0} recorded ray tracing state objects left out");
			}
		}
		string text4 = Path.Combine(work, "recording.db");
		RehydrateResult rehydrateResult = Rehydrate.Run(text2, text4, game, engine, _reader, index, null, ct, (IReadOnlySet<string> h) => Middleware.Blobs(game, h));
		if (text2 != text)
		{
			File.Delete(text2);
		}
		string path = Store.GameDir(game.Id);
		CommunityDownload communityDownload = CommunityInUse(game.Id);
		if ((object)communityDownload != null && !string.Equals(communityDownload.ContentHash, index.ContentHash, StringComparison.OrdinalIgnoreCase) && MatchesNothing(Path.Combine(path, "community.db"), hashSet, rayTracing, rehydrateResult.Missing))
		{
			try
			{
				SCSKiller.Core.App.Community.Write(Path.Combine(path, "community.json"), communityDownload with
				{
					MatchesNothingIn = index.ContentHash
				});
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
			if (CommunityInUse(game.Id) == null)
			{
				Log?.Report(game.Name + ": the community recording has none of this install's shaders (another version of the game): not used");
				return PrepareRecording(game, engine, index, work, rayTracing, every, ct);
			}
		}
		if (rehydrateResult.Found > 0 || !rehydrateResult.Complete)
		{
			Log?.Report($"{game.Name}: recording rehydrated from the install ({rehydrateResult.Found} shaders" + ((rehydrateResult.FoundInMiddleware > 0) ? $", {rehydrateResult.FoundInMiddleware} of them from middleware DLLs" : "") + (rehydrateResult.Complete ? ")" : $", {rehydrateResult.Missing.Count} not in this install: those PSOs are skipped)"));
		}
		return new Recording(text4);
	}

	private static bool MatchesNothing(string communityDb, IReadOnlySet<string> leftOut, bool rayTracing, IReadOnlyList<string> missing)
	{
		HashSet<string> other = (from x in PsoDb.Read(communityDb)
			where x.Tag == 'B'
			select PsoDb.Hex(x.Payload.AsSpan(0, 20))).ToHashSet();
		HashSet<string> hashSet = Rehydrate.References(from x in PsoDb.Read(communityDb)
			where !leftOut.Contains(x.Key) && (rayTracing || !Planner.LeftOutWithoutRt(x))
			select x);
		hashSet.ExceptWith(other);
		if (hashSet.Count > 0)
		{
			return hashSet.IsSubsetOf(missing);
		}
		return false;
	}

	private CommunityDownload? CommunityKept(string gameId)
	{
		if (Settings.UseCommunityDb)
		{
			CommunityDownload communityDownload = SCSKiller.Core.App.Community.Downloaded(Store.GameDir(gameId));
			if ((object)communityDownload != null && (!communityDownload.Full || FullEntries))
			{
				return communityDownload;
			}
		}
		return null;
	}

	private CommunityDownload? CommunityInUse(string gameId)
	{
		CommunityDownload communityDownload = CommunityKept(gameId);
		if ((object)communityDownload == null || OtherVersion(communityDownload, gameId))
		{
			return null;
		}
		return communityDownload;
	}

	private bool OtherVersion(CommunityDownload d, string gameId, GameRecord? rec = null)
	{
		if (!ShaderIndex.IsEmpty(d.ContentHash))
		{
			string matchesNothingIn = d.MatchesNothingIn;
			if (matchesNothingIn != null)
			{
				return matchesNothingIn == (rec ?? Store.LoadGame(gameId)).IndexContentHash;
			}
			return false;
		}
		return true;
	}

	private CommunityDownload? DrawsInUse(string gameId)
	{
		string text = Store.GameDir(gameId);
		if (Settings.UseCommunityDb)
		{
			CommunityDownload communityDownload = SCSKiller.Core.App.Community.DownloadedDraws(text);
			if ((object)communityDownload != null)
			{
				FileInfo fileInfo = new FileInfo(Path.Combine(text, "draws.db"));
				try
				{
					(long, DateTime) tuple = (fileInfo.Length, fileInfo.LastWriteTimeUtc);
					if (_drawsChecked.TryGetValue(fileInfo.FullName, out var value))
					{
						(long, DateTime) tuple2 = value;
						(long, DateTime) tuple3 = tuple;
						if (tuple2.Item1 == tuple3.Item1 && tuple2.Item2 == tuple3.Item2)
						{
							return communityDownload;
						}
					}
					if (SCSKiller.Core.App.Community.Check(File.ReadAllBytes(fileInfo.FullName), draws: true).HasValue)
					{
						_drawsChecked[fileInfo.FullName] = tuple;
						return communityDownload;
					}
					Log?.Report((Games.FirstOrDefault((GameState s) => s.Game.Id == gameId)?.Game.Name ?? gameId) + ": the downloaded DirectX 11 layouts were damaged and are deleted; they download again at the next sync");
					File.Delete(Path.Combine(text, "draws.json"));
					File.Delete(fileInfo.FullName);
				}
				catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
				}
				return null;
			}
		}
		return null;
	}

	private string? CommunityObjects(string gameId)
	{
		string text = CommunityInUse(gameId)?.Object;
		string text2 = DrawsInUse(gameId)?.Object;
		if (text2 == null)
		{
			return text;
		}
		return text + "|draws:" + text2;
	}

	private CommunityDownload? OfficialInUse(string gameId)
	{
		CommunityDownload communityDownload = SCSKiller.Core.App.Community.DownloadedOfficial(Store.GameDir(gameId));
		if ((object)communityDownload == null || !OfficialAllowed(communityDownload.Visibility))
		{
			return null;
		}
		return communityDownload;
	}

	private bool OfficialAllowed(OfficialVisibility? v)
	{
		if (!Settings.UseCommunityDb)
		{
			if (v == OfficialVisibility.Everyone)
			{
				return !DbEntitled();
			}
			return false;
		}
		return true;
	}

	private List<string> Downloads(string gameId)
	{
		string[] array = ((!(CommunityInUse(gameId) != null)) ? Array.Empty<string>() : new string[1] { Path.Combine(Store.GameDir(gameId), "community.db") });
		string[] array2 = ((!(OfficialInUse(gameId) != null)) ? Array.Empty<string>() : new string[1] { Path.Combine(Store.GameDir(gameId), "official.db") });
		string[] array3 = ((!(DrawsInUse(gameId) != null)) ? Array.Empty<string>() : new string[1] { Path.Combine(Store.GameDir(gameId), "draws.db") });
		int num = array.Length + array2.Length + array3.Length;
		List<string> list = new List<string>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<string> span = CollectionsMarshal.AsSpan(list);
		int num2 = 0;
		ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(array);
		readOnlySpan.CopyTo(span.Slice(num2, readOnlySpan.Length));
		num2 += readOnlySpan.Length;
		ReadOnlySpan<string> readOnlySpan2 = new ReadOnlySpan<string>(array2);
		readOnlySpan2.CopyTo(span.Slice(num2, readOnlySpan2.Length));
		num2 += readOnlySpan2.Length;
		ReadOnlySpan<string> readOnlySpan3 = new ReadOnlySpan<string>(array3);
		readOnlySpan3.CopyTo(span.Slice(num2, readOnlySpan3.Length));
		num2 += readOnlySpan3.Length;
		return list;
	}

	public void StartCommunitySync(IEnumerable<GameState>? states = null, bool fresh = false)
	{
		List<Game> games = (from s in states ?? Games
			where s.Engine != null
			select s.Game).ToList();
		lock (_scanLock)
		{
			Community community = Community;
			if (community == null)
			{
				return;
			}
			if (fresh)
			{
				bool flag = _freshGames != null;
				_freshGames = games;
				if (flag)
				{
					return;
				}
				CommunitySync = CommunitySync.ContinueWith((Task _) =>
				{
					List<Game> freshGames;
					lock (_scanLock)
					{
						freshGames = _freshGames;
						_freshGames = null;
					}
					community.Expire();
					return SyncCommunity(freshGames);
				}, TaskScheduler.Default).Unwrap();
			}
			else if (_freshGames != null)
			{
				_freshGames = games;
			}
			else if (CommunitySync.IsCompleted)
			{
				CommunitySync = Task.Run(() => SyncCommunity(games));
			}
		}
	}

	private async Task SyncCommunity(IReadOnlyList<Game> games)
	{
		_ = 1;
		try
		{
			foreach (Game item in await SyncPacks(games, CancellationToken.None))
			{
				Refresh(item);
			}
			if (!Settings.UseCommunityDb && DbEntitled())
			{
				return;
			}
			foreach (Game g in games)
			{
				if (await SyncCommunity(g, null, CancellationToken.None) || DbEntryChanged(g))
				{
					Refresh(g);
				}
			}
		}
		catch (Exception ex)
		{
			Log?.Report("community database: " + ex.Message);
		}
		finally
		{
			RequestCollect();
		}
	}

	private async Task<List<Game>> SyncPacks(IReadOnlyList<Game> games, CancellationToken ct)
	{
		List<Game> changed = new List<Game>();
		Community community = Community;
		if (community != null)
		{
			MiddlewarePacks shared = (_planner as Planner)?.SharedPacks;
			if (shared != null)
			{
				string gpu = PackGpu(Vendor.Vendor);
				if (gpu != null)
				{
					List<(Game Game, MiddlewareDll Dll)> dlls = games.SelectMany((Game g) => from item2 in Dlls(g)
						where item2.Packable && Middleware.SharedVendors.Contains(item2.Vendor)
						select (Game: g, Dll: item2)).ToList();
					bool flag = dlls.Count == 0;
					CommunityManifest manifest = default;
					if (!flag)
					{
						manifest = await community.ManifestAsync(ct);
						flag = manifest == null || !manifest.HasPacks;
					}
					if (flag)
					{
						return changed;
					}
					List<(Game, MiddlewareDll, MiddlewareImage, string)> source = (from x in dlls
						select (Game: x.Game, Dll: x.Dll, Image: Middleware.TryScan(x.Dll.Path)) into x
						where x.Image != null
						select (Game: x.Game, Dll: x.Dll, Image: x.Image, Key: HashOnly.PackKey(gpu, x.Dll.Vendor, x.Dll.Name, x.Image.ContentHash))).ToList();
					foreach (IGrouping<string, (Game, MiddlewareDll, MiddlewareImage, string)> item2 in source.GroupBy(((Game Game, MiddlewareDll Dll, MiddlewareImage Image, string Key) x) => x.Key))
					{
						(Game, MiddlewareDll, MiddlewareImage, string) tuple = item2.First();
						MiddlewareDll d = tuple.Item2;
						MiddlewareImage image = tuple.Item3;
						string item = tuple.Item4;
						CommunityEntry e = manifest.FindPack(item);
						if ((object)e == null || shared.Load(d, image)?.Header.Object == e.Object)
						{
							continue;
						}
						List<Game> users = item2.Select(((Game Game, MiddlewareDll Dll, MiddlewareImage Image, string Key) x) => x.Game).DistinctBy((Game g) => g.Id).ToList();
						int? num = await community.DownloadPackAsync(e, d, image, shared, gpu == "amd", ct);
						if (num.HasValue)
						{
							int valueOrDefault = num.GetValueOrDefault();
							Log?.Report($"{d.Name} ({image.ContentHash.Substring(0, 12)}): shared pack downloaded ({valueOrDefault:N0} pipelines, {e.Uploaders} contributor{((e.Uploaders == 1) ? "" : "s")})");
							changed.AddRange(users.Where((Game g) => !changed.Any((Game c) => c.Id == g.Id)));
						}
						else
						{
							string problem = community.Problem;
							if (problem != null)
							{
								Log?.Report(d.Name + ": shared pack: " + problem);
							}
						}
					}
					return changed;
				}
			}
		}
		return changed;
	}

	private bool SeedsPacks(Game g)
	{
		EngineInfo engineInfo;
		lock (_scanLock)
		{
			engineInfo = _scan?.GetValueOrDefault(g.Id)?.Engine;
		}
		if (engineInfo != null)
		{
			return Planner.SeedsPacks(engineInfo);
		}
		return false;
	}

	private List<MiddlewareDll> Dlls(Game g)
	{
		lock (_scanLock)
		{
			Evaluation valueOrDefault = (_scan ?? (_scan = Store.LoadScan())).GetValueOrDefault(g.Id);
			if ((object)valueOrDefault == null || valueOrDefault.AntiCheat != AntiCheat.None || _verdicts.ContainsKey(g.Id))
			{
				return new List<MiddlewareDll>();
			}
		}
		return Middleware.Detect(Path.GetDirectoryName(g.ExePath));
	}

	private (List<PsoDb.Rec> Entries, List<string> RootSignatures) PackSeeds(MiddlewarePacks packs, MiddlewareDll d)
	{
		if (!d.Packable || !Directory.Exists(Path.Combine(packs.Dir, d.Vendor)))
		{
			return (Entries: new List<PsoDb.Rec>(), RootSignatures: new List<string>());
		}
		return packs.Seeds(d, OnAmd);
	}

	private HashSet<string> InputsNow(Game g, GameRecord r, EngineInfo? e = null)
	{
		var (inputs, extra) = InputFiles(g, r, e);
		return KeyFiles.Derived(Store.GameDir(g.Id), inputs, extra, () =>
		{
			ScsKiller scsKiller = this;
			Game g2 = g;
			GameRecord r2 = r;
			EngineInfo e2 = e;
			return WarmInputs.Of(scsKiller.RecordedNow(g2, r2, null, null, e2), r.Plan?.FilePath, SeedsNow(g).SelectMany(((List<PsoDb.Rec> Entries, List<string> RootSignatures) s) => s.Entries), Elsewhere(g, r));
		});
	}

	private (string?[] Inputs, string Extra) InputFiles(Game g, GameRecord r, EngineInfo? e = null)
	{
		string path = Store.GameDir(g.Id);
		List<MiddlewareDll> dlls = (from d in Dlls(g)
			where d.Packable
			select d).ToList();
		IEnumerable<string> collection = ((_planner is Planner planner && SeedsPacks(g)) ? new MiddlewarePacks[2] { planner.Packs, planner.SharedPacks }.OfType<MiddlewarePacks>().ToList() : new List<MiddlewarePacks>()).SelectMany((MiddlewarePacks x) => dlls.Where((MiddlewareDll d) => Directory.Exists(Path.Combine(x.Dir, d.Vendor))).Select((MiddlewareDll d) =>
		{
			MiddlewareImage middlewareImage = Middleware.TryScan(d.Path);
			return ((object)middlewareImage == null) ? null : x.PathOf(d.Vendor, d.Name, middlewareImage.ContentHash);
		}));
		List<string> list = new List<string>();
		list.Add(RecordingPath(g.Id));
		list.AddRange(Downloads(g.Id));
		list.Add(r.Plan?.FilePath);
		list.Add(Path.Combine(path, "index.shaders"));
		list.AddRange(dlls.Select((MiddlewareDll d) => d.Path));
		list.AddRange(collection);
		string[] item = list.ToArray();
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(4, 7);
		defaultInterpolatedStringHandler.AppendFormatted(IndexIsInstalled(g, r));
		defaultInterpolatedStringHandler.AppendLiteral("|");
		defaultInterpolatedStringHandler.AppendFormatted(r.IndexContentHash);
		defaultInterpolatedStringHandler.AppendLiteral("|");
		defaultInterpolatedStringHandler.AppendFormatted(OnAmd);
		defaultInterpolatedStringHandler.AppendLiteral("|");
		defaultInterpolatedStringHandler.AppendFormatted(Vendor.Vendor);
		defaultInterpolatedStringHandler.AppendLiteral("|");
		defaultInterpolatedStringHandler.AppendFormatted(RayTracing(r) ? "" : "no-rt");
		defaultInterpolatedStringHandler.AppendFormatted(Maximum(r) ? "|max" : "");
		int? num = Filtering(g.Id, Maximum(r), e);
		defaultInterpolatedStringHandler.AppendFormatted((!num.HasValue) ? "" : ("|af" + num.GetValueOrDefault()));
		return (Inputs: item, Extra: defaultInterpolatedStringHandler.ToStringAndClear());
	}

	private List<(List<PsoDb.Rec> Entries, List<string> RootSignatures)> SeedsNow(Game g)
	{
		if (!(_planner is Planner planner) || !SeedsPacks(g))
		{
			return new List<(List<PsoDb.Rec>, List<string>)>();
		}
		return new MiddlewarePacks[2] { planner.Packs, planner.SharedPacks }.OfType<MiddlewarePacks>().SelectMany((MiddlewarePacks x) => from d in Dlls(g)
			where d.Packable
			select PackSeeds(x, d)).ToList();
	}

	private Func<string, bool> Elsewhere(Game g, GameRecord r)
	{
		HashSet<string> shipped = Shipped(g, r);
		List<MiddlewareImage> images = (from d in Dlls(g)
			where d.Packable
			select Middleware.TryScan(d.Path)).OfType<MiddlewareImage>().ToList();
		HashSet<string> roots = SeedsNow(g).SelectMany(((List<PsoDb.Rec> Entries, List<string> RootSignatures) s) => s.RootSignatures).ToHashSet();
		return (string h) =>
		{
			HashSet<string> hashSet = shipped;
			return hashSet == null || hashSet.Contains(h) || roots.Contains(h) || images.Any((MiddlewareImage i) => i.Containers.ContainsKey(h));
		};
	}

	private WarmInputs.Recorded RecordedNow(Game g, GameRecord r, bool? rayTracing = null, bool? every = null, EngineInfo? e = null)
	{
		string text = RecordingPath(g.Id);
		List<string> list = Downloads(g.Id);
		int num = 0;
		string[] array = new string[1 + list.Count];
		array[num] = text;
		num++;
		Span<string> span = CollectionsMarshal.AsSpan(list);
		span.CopyTo(new Span<string>(array).Slice(num, span.Length));
		num += span.Length;
		return WarmInputs.Recorded.Read(new _003C_003Ez__ReadOnlyArray<string>(array), Elsewhere(g, r), Vendor.Vendor == GpuVendor.Nvidia, ForeignVariants(g, every ?? Maximum(r), e), rayTracing ?? RayTracing(r));
	}

	private HashSet<string> ForeignVariants(Game g, bool every, EngineInfo? e = null)
	{
		string text = RecordingPath(g.Id);
		List<string> list = Downloads(g.Id);
		int? num = Filtering(g.Id, every, e);
		int? af = num;
		List<string> downloads = list;
		string local = text;
		if (downloads.Count != 0 && (!every || File.Exists(local)))
		{
			string text2 = Store.GameDir(g.Id);
			string text3 = (every ? "|variants-every" : "|variants");
			num = af;
			string name = text2 + text3 + num;
			text = local;
			list = downloads;
			int num2 = 0;
			string[] array = new string[1 + list.Count];
			array[num2] = text;
			num2++;
			Span<string> span = CollectionsMarshal.AsSpan(list);
			span.CopyTo(new Span<string>(array).Slice(num2, span.Length));
			num2 += span.Length;
			return KeyFiles.Derived(name, new _003C_003Ez__ReadOnlyArray<string>(array), "", () => SamplerVariants.Foreign(local, downloads, every, af));
		}
		return new HashSet<string>();
	}

	private int? Filtering(string id, bool every, EngineInfo? e = null)
	{
		if (every || Downloads(id).Count == 0)
		{
			return null;
		}
		if (e == null)
		{
			lock (_scanLock)
			{
				e = (_scan ?? (_scan = Store.LoadScan())).GetValueOrDefault(id)?.Engine;
			}
		}
		return e?.MaxAnisotropy;
	}

	private IReadOnlySet<string> CrashKeysNow(GameRecord r)
	{
		if (!CrashKeysCurrent(r, Snapshot()))
		{
			return new HashSet<string>();
		}
		return r.CrashKeys;
	}

	private static bool CrashKeysCurrent(GameRecord r, GpuSnapshot s)
	{
		if (!(r.CrashKeysDriver == s.Id))
		{
			return CurrentDriver(s, null, r.CrashKeysDriver);
		}
		return true;
	}

	internal static IReadOnlySet<string> CrashInputs(IReadOnlySet<string> crash, string? planFile)
	{
		if (crash.Count != 0)
		{
			return crash.Union(from x in Planner.PlanInputs(Planner.PlanBody(planFile))
				where crash.Contains(x.Rec.Key)
				select x.Key).ToHashSet();
		}
		return crash;
	}

	private Pending PendingOf(Game g, GameRecord r, EngineInfo? e)
	{
		if (!r.WarmedAt.HasValue)
		{
			return new Pending(0L, null, Unknown: false, 0L);
		}
		string path = Store.GameDir(g.Id);
		string text = PendingStamp(g, r, e);
		if (text != null)
		{
			PendingCount pending = r.Pending;
			if ((object)pending != null && pending.Key == text)
			{
				return new Pending(pending.Recorded, pending.Planned, pending.Unknown, pending.Upscaler);
			}
		}
		string warmKeysFile = r.WarmKeysFile;
		HashSet<string> hashSet = ((warmKeysFile != null) ? KeyFiles.Set(Path.Combine(path, warmKeysFile)) : null);
		string planKeysFile = r.PlanKeysFile;
		HashSet<string> made = ((planKeysFile != null) ? KeyFiles.Set(Path.Combine(path, planKeysFile)) : null);
		IReadOnlySet<string> readOnlySet = CrashInputs(CrashKeysNow(r), r.Plan?.FilePath);
		HashSet<string> hashSet2 = InputsNow(g, r, e);
		List<string> list = WarmInputs.Fresh(hashSet2, hashSet, readOnlySet.Contains);
		List<PsoDb.Rec> list2 = SeedsNow(g).SelectMany(((List<PsoDb.Rec> Entries, List<string> RootSignatures) x) => x.Entries).ToList();
		if (hashSet != null && list.Count > 0 && Vendor.Vendor == GpuVendor.Nvidia)
		{
			list = list.Except(WarmInputs.StagesTaken(r.Plan?.FilePath, list2, list.ToHashSet(), hashSet)).ToList();
		}
		HashSet<string> hashSet3 = list2.Select((PsoDb.Rec rec) => rec.Key).Concat(from x in Planner.PlanBody(r.Plan?.FilePath)
			where x.Tag == 'M'
			select MiddlewarePacks.Unwrap(x).Entry.Key).ToHashSet();
		Dictionary<string, string> one = ((list.Count > 0) ? PipelineKeys(g, r, e) : new Dictionary<string, string>());
		List<string> source = list.Where((string k) =>
		{
			HashSet<string> hashSet4 = made;
			return hashSet4 == null || !hashSet4.Contains(k);
		}).ToList();
		Pending result = new Pending(source.Select(P).Distinct().Count(), (made == null || !CurrentPlanner(r.PlanVersion, r.PlanRules, r.PlanReaderVersion, e)) ? ((long?)null) : new long?(list.Where(made.Contains).Select(P).Distinct()
			.Count()), hashSet == null, source.Where(hashSet3.Contains).Select(P).Distinct()
			.Count());
		if (text != null && hashSet2.Count > 0)
		{
			r.Pending = new PendingCount(text, result.Recorded, result.Planned, result.Unknown, result.Upscaler);
		}
		return result;
		string P(string k)
		{
			return one.GetValueOrDefault(k, k);
		}
	}

	private Dictionary<string, string> PipelineKeys(Game g, GameRecord r, EngineInfo? engine)
	{
		HashSet<string> hashSet = ForeignVariants(g, Maximum(r), engine);
		List<PsoDb.Rec> list = new List<PsoDb.Rec>();
		List<PsoDb.Rec> list2 = new List<PsoDb.Rec>();
		Dictionary<string, byte[]> dictionary = new Dictionary<string, byte[]>();
		List<PsoDb.Rec> list3 = list2;
		List<PsoDb.Rec> list4 = list;
		try
		{
			foreach (PsoDb.Rec item in Downloads(g.Id).Prepend(RecordingPath(g.Id)).Where(File.Exists).SelectMany((string f) => PsoDb.Read(f))
				.Concat(Planner.PlanBody(r.Plan?.FilePath)))
			{
				if (item.Tag == 'B')
				{
					if (item.Payload.Length > 20 && Dxbc.IsRootSignatureOnly(item.Payload.AsSpan(20)))
					{
						dictionary[PsoDb.Hex(item.Payload.AsSpan(0, 20))] = item.Payload[20..];
					}
				}
				else if (item.Tag == 'N')
				{
					list3.Add(item);
				}
				else if (!hashSet.Contains(item.Key))
				{
					list4.Add(item);
				}
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) ? true : false)
		{
			return new Dictionary<string, string>();
		}
		Dictionary<string, string> dictionary2 = new Dictionary<string, string>();
		foreach (PsoDb.Rec item2 in list4)
		{
			string text = SamplerVariants.Pipeline(item2, ((IReadOnlyDictionary<string, byte[]>)dictionary).GetValueOrDefault<string, byte[]>);
			if (text != null)
			{
				dictionary2[item2.Key] = text;
			}
		}
		foreach (PsoDb.Rec item3 in list3.Where((PsoDb.Rec n) => n.Payload.Length == 36))
		{
			string valueOrDefault = dictionary2.GetValueOrDefault(PsoDb.Hex(item3.Payload.AsSpan(0, 20)));
			if (valueOrDefault != null)
			{
				string key = item3.Key;
				byte b = 78;
				byte[] array = Convert.FromHexString(valueOrDefault);
				byte[] subArray = item3.Payload[20..];
				int num = 0;
				byte[] array2 = new byte[1 + (array.Length + subArray.Length)];
				array2[num] = b;
				num++;
				ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(array);
				readOnlySpan.CopyTo(new Span<byte>(array2).Slice(num, readOnlySpan.Length));
				num += readOnlySpan.Length;
				ReadOnlySpan<byte> readOnlySpan2 = new ReadOnlySpan<byte>(subArray);
				readOnlySpan2.CopyTo(new Span<byte>(array2).Slice(num, readOnlySpan2.Length));
				num += readOnlySpan2.Length;
				dictionary2[key] = PsoDb.Hex(SHA1.HashData(new ReadOnlySpan<byte>(array2)));
			}
		}
		return dictionary2;
	}

	private string? PendingStamp(Game g, GameRecord r, EngineInfo? e)
	{
		string path = Store.GameDir(g.Id);
		var (array, value) = InputFiles(g, r, e);
		try
		{
			string[] array2 = array;
			int num = 0;
			string[] array3 = new string[2 + array2.Length];
			ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(array2);
			readOnlySpan.CopyTo(new Span<string>(array3).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			int num2 = num;
			string warmKeysFile = r.WarmKeysFile;
			array3[num2] = ((warmKeysFile != null) ? Path.Combine(path, warmKeysFile) : null);
			num++;
			int num3 = num;
			string planKeysFile = r.PlanKeysFile;
			array3[num3] = ((planKeysFile != null) ? Path.Combine(path, planKeysFile) : null);
			return KeyFiles.DerivedStamp(new _003C_003Ez__ReadOnlyArray<string>(array3), $"{value}|{string.Join(',', CrashKeysNow(r).Order())}|{CurrentPlanner(r.PlanVersion, r.PlanRules, r.PlanReaderVersion, e)}|planned-units");
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	private static HashSet<string> PlannerMade(Plan plan, string work)
	{
		string text = Path.Combine(work, "recording.db");
		HashSet<string> recorded = (File.Exists(text) ? PipelineKeys(text) : new HashSet<string>());
		HashSet<string> hashSet = new HashSet<string>();
		foreach (string item in from x in Planner.PlanInputs(Planner.PlanBody(plan.FilePath))
			where x.Rec.Tag != 'M'
			select x.Key into k
			where !recorded.Contains(k)
			select k)
		{
			hashSet.Add(item);
		}
		return hashSet;
	}

	private static HashSet<string> PlanPipelineKeys(string path)
	{
		try
		{
			return (from r in PlanFile.Read(path).Records
				where r.Tag != 'B'
				select (r.Tag != 'M') ? r.Key : MiddlewarePacks.Unwrap(r).Entry.Key).ToHashSet();
		}
		catch (Exception ex) when ((ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
		{
			return new HashSet<string>();
		}
	}

	private bool DbEntryChanged(Game g)
	{
		GameState gameState = Games.FirstOrDefault((GameState s) => s.Game.Id == g.Id);
		if ((object)gameState == null)
		{
			return false;
		}
		CommunityManifest communityManifest = LocalManifest();
		CommunityEntry communityEntry = ((communityManifest != null) ? DbEntry(communityManifest, g, Store.LoadGame(g.Id), FullEntries) : null);
		if (gameState.InCommunityDb == ((communityManifest != null) ? new bool?(communityEntry != null) : ((bool?)null)))
		{
			return gameState.CommunityDbPsos != (communityEntry?.Psos ?? 0);
		}
		return true;
	}

	private async Task<bool> SyncCommunity(Game g, string? contentHash, CancellationToken ct)
	{
		Community community = Community;
		bool flag = community == null || (!Settings.UseCommunityDb && DbEntitled());
		CommunityManifest communityManifest = default;
		if (!flag)
		{
			communityManifest = await community.ManifestAsync(ct);
			flag = communityManifest == null;
		}
		if (flag)
		{
			return false;
		}
		GameRecord gameRecord = ((contentHash != null) ? null : Store.LoadGame(g.Id));
		CommunityEntry entry = ((gameRecord == null) ? communityManifest.Find(g, contentHash, FullEntries) : DbEntry(communityManifest, g, gameRecord, FullEntries));
		OfficialEntry official = ((gameRecord == null) ? communityManifest.FindOfficial(g, contentHash) : OfficialEntry(communityManifest, g, gameRecord));
		Stage[] array = Planner.D3D11Stages(Vendor.Caps);
		object obj;
		if (array != null && !array.Contains(Stage.Vertex, null))
		{
			string text = contentHash ?? (IndexIsInstalled(g, gameRecord) ? gameRecord.IndexContentHash : null);
			if (text != null && text.Length == 40)
			{
				obj = communityManifest.FindDraws(text);
				goto IL_01c4;
			}
		}
		obj = null;
		goto IL_01c4;
		IL_01c4:
		CommunityEntry draws = (CommunityEntry)obj;
		string dir = Store.GameDir(g.Id);
		bool got = false;
		if (Settings.UseCommunityDb && entry != null)
		{
			CommunityDownload communityDownload = SCSKiller.Core.App.Community.Downloaded(dir);
			if (entry.Object != communityDownload?.Object || ((object)communityDownload != null && communityDownload.Full && !entry.Full))
			{
				CommunityDownload communityDownload2 = await community.DownloadAsync(entry, dir, ct);
				if ((object)communityDownload2 != null)
				{
					Log?.Report($"{g.Name}: community recording downloaded ({communityDownload2.Psos:N0} pipelines, {entry.Uploaders} contributor{((entry.Uploaders == 1) ? "" : "s")})");
					got = true;
				}
				else
				{
					string problem = community.Problem;
					if (problem != null)
					{
						Log?.Report(g.Name + ": community database: " + problem);
					}
				}
			}
		}
		if (Settings.UseCommunityDb && draws != null && draws.Object != SCSKiller.Core.App.Community.DownloadedDraws(dir)?.Object)
		{
			CommunityDownload communityDownload3 = await community.DownloadDrawsAsync(draws, dir, ct);
			if ((object)communityDownload3 != null)
			{
				Log?.Report($"{g.Name}: community DirectX 11 layouts downloaded ({communityDownload3.Psos:N0})");
				got = true;
			}
			else
			{
				string problem2 = community.Problem;
				if (problem2 != null)
				{
					Log?.Report(g.Name + ": community database: " + problem2);
				}
			}
		}
		if (official != null)
		{
			try
			{
				if (SCSKiller.Core.App.Community.Restate(dir, official, ct))
				{
					got = true;
				}
				lock (_restateFailed)
				{
					_restateFailed.Remove(g.Id);
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				bool flag2;
				lock (_restateFailed)
				{
					flag2 = _restateFailed.Add(g.Id);
				}
				if (flag2)
				{
					Log?.Report(g.Name + ": official recording: its visibility couldn't be updated (" + ex.Message + ")");
				}
			}
		}
		if (official != null && OfficialAllowed(official.Visibility) && official.Object != SCSKiller.Core.App.Community.DownloadedOfficial(dir)?.Object)
		{
			CommunityDownload communityDownload4 = await community.DownloadOfficialAsync(official, dir, ct);
			if ((object)communityDownload4 != null)
			{
				Log?.Report($"{g.Name}: official recording downloaded ({communityDownload4.Psos:N0} pipelines)");
				got = true;
			}
			else
			{
				string problem3 = community.Problem;
				if (problem3 != null)
				{
					Log?.Report(g.Name + ": official recording: " + problem3);
				}
			}
		}
		return got;
	}

	public void StartSharing(IEnumerable<Game>? games = null)
	{
		Sharing sharing = Sharing;
		if (sharing == null || !Settings.ShareRecordings)
		{
			return;
		}
		List<Game> list = (games ?? Games.Select((GameState s) => s.Game)).ToList();
		lock (_scanLock)
		{
			SharingPass = SharingPass.ContinueWith((Task _) => Share(sharing, list), TaskScheduler.Default).Unwrap();
		}
	}

	private async Task Share(Sharing sharing, List<Game> games)
	{
		sharing.Waiting = null;
		await SharePacks(sharing);
		string logged = null;
		DateTimeOffset warmed = default;
		Plan plan = default;
		foreach (Game g in games)
		{
			try
			{
				if (g.Store == SCSKiller.Core.Store.Manual)
				{
					continue;
				}
				GameRecord rec = Store.LoadGame(g.Id);
				string v = g.Version;
				if (v == null || rec.IndexGameVersion != v)
				{
					continue;
				}
				string hash = rec.IndexContentHash;
				if (hash == null || hash.Length != 40 || rec.ShaderCount == 0 || ShaderIndex.IsEmpty(hash) || BlockingMod(g) != null)
				{
					continue;
				}
				string gameDir = Store.GameDir(g.Id);
				string contentHash = hash;
				Func<UploadMeta> meta = () => UploadMetaOf(g, rec, v, hash);
				Func<IEnumerable<string>> middlewareShaders = () => (from d in Dlls(g)
					where d.Packable
					select d).SelectMany((MiddlewareDll d) => Middleware.Scan(d.Path).Containers.Keys);
				Func<IReadOnlySet<string>> layerMade = LayerMadeNow;
				PlanStats planStats = OwnPlanCheck(rec, hash);
				SharedRecording sharedRecording = await sharing.ShareAsync(gameDir, contentHash, meta, middlewareShaders, layerMade, ((object)planStats != null) ? $"{planStats.RsRule}:{planStats.BuiltOk}/{planStats.BuiltN}@{rec.PlanVersion}" : null);
				if ((object)sharedRecording != null)
				{
					Log?.Report($"{g.Name}: recording shared ({sharedRecording.Psos:N0} pipelines, {sharedRecording.NewPsos:N0} new to the community database)");
					Refresh(g);
				}
				else
				{
					string problem = sharing.Problem;
					if (problem != null && problem != logged)
					{
						IProgress<string>? log = Log;
						if (log != null)
						{
							string name = g.Name;
							string text;
							logged = (text = problem);
							log.Report(name + ": " + text);
						}
					}
				}
				int num;
				if (rec != null)
				{
					DateTimeOffset? warmedAt = rec.WarmedAt;
					if (warmedAt.HasValue)
					{
						warmed = warmedAt.GetValueOrDefault();
						plan = rec.Plan;
						if ((object)plan != null && rec.WarmedPlanVersion > 0 && plan.IndexContentHash == hash)
						{
							num = ((rec.WarmedIndexHash == hash) ? 1 : 0);
							goto IL_0574;
						}
					}
				}
				num = 0;
				goto IL_0574;
				IL_0574:
				bool flag = (byte)num != 0;
				if (flag)
				{
					flag = await sharing.ReportAsync(Store.GameDir(g.Id), warmed.UtcTicks.ToString(CultureInfo.InvariantCulture), () => OutcomeReport.Build(Path.Combine(SessionDir(g), "scskiller_creates.csv"), Path.GetFileName(g.ExePath), warmed, rec.LastPlay, (Vendor.Vendor == GpuVendor.Nvidia) ? SessionLog.ReadRayQueryKeys(RayQueryKeysPath(g.Id)) : null, new OutcomeReport(hash, ShareVendor(Vendor.Vendor), GpuModel(), DriverVersion(), AppVersion.Current.ToString(), plan.Stats.RsRule, rec.WarmedPlanVersion, 0L), 100L));
				}
				if (flag)
				{
					Log?.Report(g.Name + ": the first launch after the warm reported");
				}
			}
			catch (Exception ex)
			{
				Log?.Report(g.Name + ": sharing the recording failed: " + ex.Message);
			}
		}
		string waiting = sharing.Waiting;
		if (waiting != null)
		{
			Log?.Report("sharing waits for a recording to be whole (a game still recording?): " + waiting);
		}
		RequestCollect();
	}

	private HashSet<string> LayerMadeNow()
	{
		HashSet<string> hashSet = Recordings.LayerMadeOnDisk(Store.DataDir);
		MiddlewarePacks middlewarePacks = (_planner as Planner)?.Packs;
		if (middlewarePacks != null)
		{
			hashSet.UnionWith(middlewarePacks.LayerMade());
		}
		return hashSet;
	}

	private string SessionDir(Game g)
	{
		string exeDir = Path.GetDirectoryName(g.ExePath);
		if (!SessionFiles.Any((string f) => File.Exists(Path.Combine(exeDir, f))))
		{
			return OfflineSessionDir(Store, g.Id);
		}
		return exeDir;
	}

	private async Task SharePacks(Sharing sharing)
	{
		MiddlewarePacks middlewarePacks = (_planner as Planner)?.Packs;
		if (middlewarePacks == null)
		{
			return;
		}
		string text = PackGpu(Vendor.Vendor);
		if (text == null)
		{
			return;
		}
		try
		{
			try
			{
				middlewarePacks.Exclude(Recordings.LayerMadeOnDisk(Store.DataDir));
			}
			catch (Recordings.IncompleteLayerList incompleteLayerList)
			{
				sharing.Waiting = incompleteLayerList.Message;
				return;
			}
			foreach (var (value, value2) in await sharing.SharePacksAsync(middlewarePacks.Dir, text, AppVersion.Current.ToString(), LayerMadeNow))
			{
				Log?.Report($"{value}: upscaler pack shared ({value2:N0} pipelines)");
			}
			string problem = sharing.Problem;
			if (problem != null)
			{
				Log?.Report("sharing upscaler packs: " + problem);
			}
		}
		catch (Exception ex)
		{
			Log?.Report("sharing upscaler packs failed: " + ex.Message);
		}
	}

	private string? GpuModel()
	{
		string text = Vendor.Gpu.Name.Trim();
		if (text == null || text.Length <= 0)
		{
			return null;
		}
		return text.Substring(0, Math.Min(text.Length, 128));
	}

	private string? DriverVersion()
	{
		string driverVersion = Vendor.Gpu.DriverVersion;
		if (driverVersion != null)
		{
			int length = driverVersion.Length;
			if (length > 0 && length <= 32)
			{
				return driverVersion;
			}
		}
		return null;
	}

	private static PlanStats? OwnPlanCheck(GameRecord rec, string contentHash)
	{
		Plan plan = rec.Plan;
		if ((object)plan != null)
		{
			PlanStats stats = plan.Stats;
			if ((object)stats != null && stats.BuiltN.HasValue && plan.IndexContentHash == contentHash && rec.PlanCommunity == null)
			{
				return plan.Stats;
			}
		}
		return null;
	}

	private string? EngineLabel(Game g)
	{
		EngineInfo engineInfo;
		lock (_scanLock)
		{
			engineInfo = _scan?.GetValueOrDefault(g.Id)?.Engine;
		}
		if ((object)engineInfo != null)
		{
			EngineInfo engineInfo2 = engineInfo;
			string text = $"{engineInfo2.Family} {engineInfo2.Version} {engineInfo2.Fork}".TrimEnd();
			if (text.Length <= 128)
			{
				return text;
			}
			return text.Substring(0, 128);
		}
		return null;
	}

	private UploadMeta UploadMetaOf(Game g, GameRecord rec, string version, string contentHash)
	{
		PlanStats planStats = OwnPlanCheck(rec, contentHash);
		return new UploadMeta(g.Id + "@" + version, contentHash, EngineLabel(g), ShareVendor(Vendor.Vendor), AppVersion.Current.ToString(), (from d in (from d in Dlls(g)
				where d.Packable
				select d).Take(64)
			select new UploadDll(Clip(d.Name), Middleware.Scan(d.Path).ContentHash)).ToArray(), planStats?.RsRule, planStats?.BuiltOk, planStats?.BuiltN, (planStats != null) ? new int?(rec.PlanVersion) : ((int?)null), DriverVersion(), GpuModel());
		static string? Clip(string? s)
		{
			if (s == null || s.Length <= 128)
			{
				return s;
			}
			return s.Substring(0, 128);
		}
	}

	private bool ImportRecording(Game g, GameRecord rec, bool migrate = false, CancellationToken ct = default(CancellationToken))
	{
		if (Collections.RecordedBy(g.Id) != null)
		{
			return false;
		}
		bool flag = Foreign(g);
		FileInfo fileInfo = new FileInfo(Path.Combine(Path.GetDirectoryName(g.ExePath), "scskiller.db"));
		string text = RecordingPath(g.Id);
		string text2 = Path.Combine(Store.GameDir(g.Id), "recording.all.db");
		string path = Path.Combine(fileInfo.DirectoryName, "scskiller.keys");
		if (!File.Exists(text) && File.Exists(path) && Length(path) != 8 + Math.Max(0L, Length(Path.Combine(Store.GameDir(g.Id), "index.shaders")) - 20))
		{
			WriteKeys(g, rec, ct);
		}
		bool flag2 = Legacy(g.Id);
		if (flag2 && !migrate)
		{
			return false;
		}
		string text3 = ((fileInfo.Exists && !flag) ? $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}" : null);
		bool flag3 = !flag && fileInfo != null && fileInfo.Exists && fileInfo.Length > 0 && text3 != rec.RecordingInbox;
		if (!flag3 & flag2)
		{
			CancellationToken ct2 = ct;
			using (Recordings.Lock(text, null, ct2))
			{
				if (Length(text) == 0L && Legacy(g.Id))
				{
					try
					{
						string[] array = new string[3]
						{
							text2,
							text2 + ".key",
							text
						};
						for (int i = 0; i < array.Length; i++)
						{
							File.Delete(array[i]);
						}
					}
					catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
					{
						Log?.Report($"{g.Name}: could not delete {text2}: {ex.Message}");
						return false;
					}
					return true;
				}
			}
		}
		if (!flag3 && !flag2)
		{
			if (flag || fileInfo == null || !fileInfo.Exists || fileInfo.Length <= 0)
			{
				return false;
			}
			CancellationToken ct2 = ct;
			using (Recordings.Lock(text, null, ct2))
			{
				if (!GameFolderWrite(g) || Foreign(g) || !Recordings.Rotate(fileInfo.FullName, fileInfo.Length))
				{
					return false;
				}
				if (rec.KeysPending)
				{
					WriteKeys(g, rec, ct);
				}
			}
			rec.RecordingInbox = null;
			return true;
		}
		int count;
		try
		{
			CancellationToken ct2 = ct;
			using (Recordings.Lock(text, null, ct2))
			{
				if (flag3 && Foreign(g))
				{
					return false;
				}
				Directory.CreateDirectory(Store.GameDir(g.Id));
				HashSet<string> hashSet = Shipped(g, rec);
				HashSet<string> hashSet2 = Recordings.Merge(text, flag3 ? fileInfo.FullName : null, (hashSet == null) ? null : new Func<string, bool>(hashSet.Contains), out var nvAdded);
				File.Delete(text2);
				File.Delete(text2 + ".key");
				count = hashSet2.Count;
				string recordingInbox = text3;
				string recordingIndexHash = ((hashSet != null) ? rec.IndexContentHash : null);
				rec.RecordingInbox = recordingInbox;
				rec.RecordingIndexHash = recordingIndexHash;
				if (((hashSet2.Count > 0) | nvAdded) || (flag3 && !flag2 && PsoDb.Read(fileInfo.FullName).Any((PsoDb.Rec r) =>
				{
					bool flag4;
					switch (r.Tag)
					{
					case 'B':
					case 'L':
					case 'N':
					case 'W':
						flag4 = true;
						break;
					default:
						flag4 = false;
						break;
					}
					return !flag4;
				})))
				{
					rec.RecordingImportedAt = DateTimeOffset.Now;
				}
				WriteKeys(g, rec);
				try
				{
					((_planner as Planner)?.Packs)?.Exclude(Recordings.LayerMade(text));
				}
				catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException || ex2 is InvalidDataException) ? true : false)
				{
					Log?.Report(g.Name + ": upscaler packs not updated: " + ex2.Message);
				}
				if (flag3 && GameFolderWrite(g))
				{
					Store.SaveGame(g.Id, rec);
					InboxRotating?.Invoke();
					if (Recordings.Rotate(fileInfo.FullName, fileInfo.Length))
					{
						rec.RecordingInbox = null;
					}
				}
			}
		}
		catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException || ex3 is InvalidDataException) ? true : false)
		{
			Log?.Report($"{g.Name}: could not import {fileInfo.FullName}: {ex3.Message}");
			return false;
		}
		if (count > 0)
		{
			StartSharing(new _003C_003Ez__ReadOnlySingleElementList<Game>(g));
		}
		UpdateRecorderIni(g, rec);
		if (Collections.Member(g.Id).HasValue)
		{
			SplitCollection(g);
		}
		return true;
	}

	private void SplitCollection(Game first)
	{
		(IReadOnlyList<CollectionGame>, CollectionGame)? tuple = Collections.Member(first.Id);
		if (!tuple.HasValue || tuple.GetValueOrDefault().Item1[0].Id != first.Id || Legacy(first.Id))
		{
			return;
		}
		string text = RecordingPath(first.Id);
		List<string> list = new List<string>();
		try
		{
			using (Recordings.Lock(text))
			{
				if (!File.Exists(text))
				{
					return;
				}
				HashSet<string> own = Shipped(first, Store.LoadGame(first.Id));
				HashSet<string> moved = new HashSet<string>();
				foreach (Game item in Collections.Expand(first).Skip(1))
				{
					if (Legacy(item.Id))
					{
						continue;
					}
					HashSet<string> theirs = Shipped(item, Store.LoadGame(item.Id));
					if (theirs == null)
					{
						continue;
					}
					List<PsoDb.Rec> list2 = PsoDb.Read(text).Where((PsoDb.Rec r) =>
					{
						char tag = r.Tag;
						if ((tag == 'C' || tag == 'G' || tag == 'S') ? true : false)
						{
							IEnumerable<string> enumerable = Shaders(r);
							if (enumerable != null)
							{
								return enumerable.All(theirs.Contains);
							}
						}
						return false;
					}).ToList();
					List<PsoDb.Rec> list3 = PsoDb.Read(text).Where((PsoDb.Rec r) =>
					{
						char tag = r.Tag;
						return (tag == 'N' || tag == 'W') ? true : false;
					}).ToList();
					if (list2.Count + list3.Count == 0)
					{
						continue;
					}
					HashSet<string> named = Rehydrate.References(list2);
					string text2 = Path.Combine(Store.GameDir(first.Id), $"split-{Environment.ProcessId}.tmp");
					int count;
					bool nvAdded;
					try
					{
						PsoDb.WriteCompact(text2, (from r in PsoDb.Read(text)
							where r.Tag == 'B' && named.Contains(PsoDb.Hex(r.Payload.AsSpan(0, 20)))
							select r).Concat(list3).Concat(list2));
						using (Recordings.Lock(RecordingPath(item.Id)))
						{
							Directory.CreateDirectory(Store.GameDir(item.Id));
							count = Recordings.Merge(RecordingPath(item.Id), text2, theirs.Contains, out nvAdded).Count;
						}
					}
					finally
					{
						File.Delete(text2);
					}
					if ((count > 0) | nvAdded)
					{
						Imported(item);
						list.Add(item.Id);
						if (count > 0)
						{
							Log?.Report($"{item.Name}: {count:N0} pipelines from {first.Name}'s recorder");
						}
					}
					if (own != null)
					{
						moved.UnionWith(from r in list2
							where !Shaders(r).All(own.Contains)
							select r.Key);
					}
				}
				if (moved.Count > 0)
				{
					PsoDb.WriteCompact(text, (from r in PsoDb.Read(text)
						where r.Tag == 'B' || !moved.Contains(r.Key)
						select r).ToList());
					KeyFiles.Forget(text);
				}
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) ? true : false)
		{
			Log?.Report(first.Name + ": handing its collection's records on failed: " + ex.Message);
		}
		foreach (string id in list)
		{
			Game game = Games.FirstOrDefault((GameState s) => s.Game.Id == id)?.Game;
			if ((object)game != null)
			{
				Refresh(game);
			}
		}
		static IEnumerable<string>? Shaders(PsoDb.Rec r)
		{
			try
			{
				return PsoDb.Parse(r).Stages.Values;
			}
			catch (Exception ex2) when ((ex2 is InvalidDataException || ex2 is ArgumentException || ex2 is KeyNotFoundException || ex2 is IndexOutOfRangeException || ex2 is OverflowException) ? true : false)
			{
				return null;
			}
		}
	}

	private bool InboxPending(Game g, GameRecord rec)
	{
		if (Collections.RecordedBy(g.Id) == null && !Foreign(g))
		{
			FileInfo fileInfo = new FileInfo(Path.Combine(Path.GetDirectoryName(g.ExePath), "scskiller.db"));
			if (fileInfo != null && fileInfo.Exists && fileInfo.Length > 0)
			{
				return $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}" != rec.RecordingInbox;
			}
		}
		return false;
	}

	private bool Legacy(string gameId)
	{
		if (!File.Exists(RecordingPath(gameId)) || PsoDb.IsCompact(RecordingPath(gameId)))
		{
			return File.Exists(Path.Combine(Store.GameDir(gameId), "recording.all.db"));
		}
		return true;
	}

	private static bool IndexIsInstalled(Game g, GameRecord r)
	{
		if (g.Version == null || r.IndexGameVersion == null)
		{
			return r.IndexExeStamp == ExeStamp(g);
		}
		return g.Version == r.IndexGameVersion;
	}

	private HashSet<string>? Shipped(Game g, GameRecord rec)
	{
		if (IndexIsInstalled(g, rec))
		{
			string indexContentHash = rec.IndexContentHash;
			if (indexContentHash != null)
			{
				return SCSKiller.Core.App.Sharing.Shipped(Store.GameDir(g.Id), indexContentHash);
			}
		}
		return null;
	}

	private int WriteKeys(Game g, GameRecord rec, CancellationToken ct = default(CancellationToken))
	{
		string text = Collections.RecordedBy(g.Id);
		if (text != null)
		{
			GameRecord gameRecord = Store.LoadGame(text);
			if (!gameRecord.KeysPending)
			{
				gameRecord.KeysPending = true;
				Store.SaveGame(text, gameRecord);
			}
			rec.KeysPending = false;
			return 0;
		}
		string dir = Path.GetDirectoryName(g.ExePath);
		string text2 = Path.Combine(dir, "scskiller.keys");
		if (Foreign(dir))
		{
			return 0;
		}
		if (!GameFolderWrite(g))
		{
			return KeysLater(rec);
		}
		using (Recordings.Lock(RecordingPath(g.Id), null, ct))
		{
			if (!GameFolderWrite(g))
			{
				return KeysLater(rec);
			}
			if (Foreign(dir))
			{
				return 0;
			}
			try
			{
				string dll = ProxyNames.Select((string n) => Path.Combine(dir, n)).FirstOrDefault(IsOurProxy);
				GameRecord gameRecord2;
				if (dll != null)
				{
					string indexContentHash = rec.IndexContentHash;
					HashSet<string> hashSet = ((indexContentHash != null) ? SCSKiller.Core.App.Sharing.Shipped(Store.GameDir(g.Id), indexContentHash) : null);
					bool flag = hashSet != null && IndexIsInstalled(g, rec);
					if (flag)
					{
						(IReadOnlyList<CollectionGame>, CollectionGame)? tuple = Collections.Member(g.Id);
						if (tuple.HasValue)
						{
							foreach (Game item in (from e in tuple.GetValueOrDefault().Item1.Skip(1)
								select Games.FirstOrDefault((GameState s) => s.Game.Id == e.Id)?.Game).OfType<Game>())
							{
								HashSet<string> hashSet2 = Shipped(item, Store.LoadGame(item.Id));
								if (hashSet2 != null)
								{
									hashSet.UnionWith(hashSet2);
								}
							}
						}
					}
					bool later = false;
					int result = Recordings.WriteKeys(RecordingPath(g.Id), hashSet, text2, flag, () =>
					{
						if (GameFolderWrite(g))
						{
							return IsOurProxy(dll);
						}
						later = true;
						return false;
					});
					gameRecord2 = rec;
					string keysIndexHash = (flag ? rec.IndexContentHash : null);
					bool keysPending = later;
					rec.KeysIndexHash = keysIndexHash;
					gameRecord2.KeysPending = keysPending;
					if (!later)
					{
						PublishWarmed(g, dir);
					}
					return result;
				}
				File.Delete(text2);
				File.Delete(Path.Combine(dir, "scskiller.warmed"));
				gameRecord2 = rec;
				rec.KeysIndexHash = null;
				gameRecord2.KeysPending = false;
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				rec.KeysPending = true;
				RecorderLog($"{g.Name}: couldn't write {text2}: {ex.Message}");
			}
			return 0;
		}
	}

	private void PublishWarmed(Game g, string dir)
	{
		List<string> list = (Collections.Member(g.Id)?.All.Select((CollectionGame e) => e.Id) ?? new _003C_003Ez__ReadOnlySingleElementList<string>(g.Id)).Select((string id) => Path.Combine(Store.GameDir(id), "scskiller.warmed")).Where(File.Exists).ToList();
		string text = Path.Combine(dir, "scskiller.warmed");
		if (list.Count == 0)
		{
			File.Delete(text);
			return;
		}
		try
		{
			if (list != null && list.Count == 1)
			{
				string sourceFileName = list[0];
				File.Copy(sourceFileName, text + ".tmp", overwrite: true);
			}
			else
			{
				File.WriteAllBytes(text + ".tmp", Recordings.UnionWarmed(list));
			}
			File.Move(text + ".tmp", text, overwrite: true);
		}
		finally
		{
			File.Delete(text + ".tmp");
		}
	}

	private static int KeysLater(GameRecord rec)
	{
		rec.KeysPending = true;
		return 0;
	}

	private ShaderIndex Index(Game g, EngineInfo engine, GameRecord rec, CancellationToken ct)
	{
		string text = ExeStamp(g);
		ShaderIndex shaderIndex;
		try
		{
			shaderIndex = _reader.Index(g, engine, Log, new Reporter<(int, int)>(((int Done, int Total) r) =>
			{
				ReadProgress(g.Id, r.Done, r.Total);
			}), ct);
		}
		finally
		{
			RequestCollect();
		}
		string contentHash = shaderIndex.ContentHash;
		int? shaderCount = shaderIndex.Shaders.Count;
		string version = g.Version;
		string indexExeStamp = text;
		rec.IndexContentHash = contentHash;
		rec.ShaderCount = shaderCount;
		rec.IndexGameVersion = version;
		rec.IndexExeStamp = indexExeStamp;
		SCSKiller.Core.App.Sharing.SaveShipped(Store.GameDir(g.Id), shaderIndex);
		CompactRecording(g, rec, shaderIndex, ct);
		Store.SaveGame(g.Id, rec);
		string by = Collections.RecordedBy(g.Id);
		if (by != null)
		{
			Game game = Games.FirstOrDefault((GameState s) => s.Game.Id == by)?.Game;
			if ((object)game != null)
			{
				SplitCollection(game);
			}
		}
		else if (Collections.Member(g.Id).HasValue)
		{
			SplitCollection(g);
		}
		return shaderIndex;
	}

	private void ReadProgress(string id, int done, int total)
	{
		QueueItem obj;
		lock (_lock)
		{
			int num = ((_current == id) ? _queue.FindIndex((QueueItem q) => q.GameId == id && q.Stage == QueueStage.Indexing) : (-1));
			if (num < 0)
			{
				return;
			}
			WarmProgress? progress = _queue[num].Progress;
			if ((object)progress != null && progress.Done > done)
			{
				return;
			}
			obj = (_queue[num] = _queue[num]with
			{
				Progress = new WarmProgress(done, total, 0L, 0.0, 0L, 0L, null, 0, 0, 0L)
			});
		}
		QueueChanged?.Invoke(obj);
	}

	private void RetryIndexes()
	{
		if (_playing.Count > 0 || _indexRetry.IsEmpty || (double)(Environment.TickCount64 - Interlocked.Read(in _indexRetried)) < IndexRetryInterval.TotalMilliseconds)
		{
			return;
		}
		Interlocked.Exchange(ref _indexRetried, Environment.TickCount64);
		foreach (string id in _indexRetry.Keys)
		{
			if (_indexRetry.TryRemove(id, out var _))
			{
				Game game = Games.FirstOrDefault((GameState s) => s.Game.Id == id)?.Game;
				if ((object)game != null)
				{
					IndexForRecorder(game);
				}
			}
		}
	}

	private bool AnyCompileRunning()
	{
		if (!Compiling && !QueueRunning)
		{
			return OtherCompile();
		}
		return true;
	}

	private void ParkIndex(string id, CancellationTokenSource cts)
	{
		_parkedIndexes[id] = 0;
		cts.Cancel();
	}

	private static bool Indexed(Game g, GameRecord rec)
	{
		if (rec.IndexContentHash != null && IndexIsInstalled(g, rec))
		{
			return rec.IndexExeStamp == ExeStamp(g);
		}
		return false;
	}

	private void IndexForRecorder(Game g)
	{
		if (!ManageRecorders)
		{
			return;
		}
		GameState gameState = Games.FirstOrDefault((GameState s) => s.Game.Id == g.Id);
		if ((object)gameState == null)
		{
			return;
		}
		EngineInfo engine = gameState.Engine;
		if ((object)engine == null || engine.Unsupported != null || engine.Encrypted || Indexed(g, Store.LoadGame(g.Id)))
		{
			return;
		}
		CancellationTokenSource cts = new CancellationTokenSource();
		if (!_recorderIndexes.TryAdd(g.Id, cts))
		{
			return;
		}
		lock (_recorderIndexes)
		{
			RecorderIndexing = RecorderIndexing.ContinueWith((Task _) =>
			{
				RunRecorderIndex(g.Id, cts);
			}, CancellationToken.None, TaskContinuationOptions.None, BelowNormalScheduler.Instance);
		}
	}

	private void RunRecorderIndex(string id, CancellationTokenSource cts)
	{
		CancellationToken token = cts.Token;
		string text = id;
		Game game = null;
		bool flag = false;
		bool flag2 = false;
		try
		{
			while (((Settings.PauseWhileGaming && _playing.Count > 0) || AnyCompileRunning()) && !token.IsCancellationRequested)
			{
				Thread.Sleep(Poll);
			}
			Interlocked.Increment(ref _readingIndexes);
			flag2 = true;
			if (AnyCompileRunning())
			{
				ParkIndex(id, cts);
			}
			token.ThrowIfCancellationRequested();
			using (new Timer((object? _) =>
			{
				if (AnyCompileRunning())
				{
					ParkIndex(id, cts);
				}
			}, null, Poll, Poll))
			{
				GameState gameState = Games.FirstOrDefault((GameState s) => s.Game.Id == id);
				if ((object)gameState == null)
				{
					return;
				}
				EngineInfo engine = gameState.Engine;
				if ((object)engine == null || engine.Unsupported != null || engine.Encrypted)
				{
					return;
				}
				Game game2 = gameState.Game;
				text = game2.Name;
				if (GameRunning(game2))
				{
					_indexRetry[id] = true;
					return;
				}
				using FileStream fileStream = CompileLock(id);
				if (fileStream == null)
				{
					return;
				}
				GameRecord rec = Store.LoadGame(id);
				if (Indexed(game2, rec))
				{
					return;
				}
				ShaderIndex shaderIndex = Index(game2, engine, rec, token);
				Log?.Report($"{text}: {shaderIndex.Shaders.Count:N0} shaders read from the game's files: the recorder records them by hash only");
				game = (Indexed(game2, rec) ? null : game2);
			}
		}
		catch (OperationCanceledException)
		{
			_indexRetry[id] = true;
		}
		catch (Exception ex2)
		{
			Log?.Report(text + ": couldn't read the game's shaders before recording: " + ex2.Message);
		}
		finally
		{
			if (flag2)
			{
				Interlocked.Decrement(ref _readingIndexes);
			}
			flag = _parkedIndexes.TryRemove(id, out var _) && game == null;
			_recorderIndexes.TryRemove(id, out CancellationTokenSource _);
			RequestCollect();
		}
		if (game != null)
		{
			IndexForRecorder(game);
		}
		else if (flag)
		{
			GameState gameState2 = Games.FirstOrDefault((GameState s) => s.Game.Id == id);
			if ((object)gameState2 != null)
			{
				IndexForRecorder(gameState2.Game);
			}
		}
	}

	private void CompactRecording(Game g, GameRecord rec, ShaderIndex index, CancellationToken ct = default(CancellationToken))
	{
		bool flag = rec.RecordingIndexHash == index.ContentHash;
		if ((flag && rec.KeysIndexHash == index.ContentHash) || Legacy(g.Id))
		{
			return;
		}
		try
		{
			string store = RecordingPath(g.Id);
			CancellationToken ct2 = ct;
			using (Recordings.Lock(store, null, ct2))
			{
				if (!flag && File.Exists(RecordingPath(g.Id)))
				{
					Recordings.Merge(RecordingPath(g.Id), null, index.Shaders.ContainsKey);
				}
				rec.RecordingIndexHash = index.ContentHash;
				int num = WriteKeys(g, rec);
				if (num > 0)
				{
					Log?.Report($"{g.Name}: {num} recorded pipelines name shaders this build doesn't ship: they're skipped when compiling, and recorded again if the game still creates them");
				}
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) ? true : false)
		{
			Log?.Report(g.Name + ": compacting the recording failed: " + ex.Message);
		}
	}

	private static HashSet<string> PipelineKeys(string db)
	{
		return (from r in PsoDb.Read(db).Where((PsoDb.Rec r) =>
			{
				bool flag;
				switch (r.Tag)
				{
				case 'B':
				case 'L':
				case 'N':
				case 'W':
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				return !flag;
			})
			select r.Key).ToHashSet();
	}

	private void StartMigration(IEnumerable<GameState> states)
	{
		List<Game> games = (from s in states
			select s.Game into g
			where Legacy(g.Id)
			select g).ToList();
		lock (_scanLock)
		{
			if (games.Count > 0 && RecordingMigration.IsCompleted)
			{
				RecordingMigration = Task.Run(() =>
				{
					MigrateRecordings(games);
				});
			}
		}
	}

	private void MigrateRecordings(IReadOnlyList<Game> games)
	{
		foreach (Game game in games)
		{
			while (true)
			{
				Task task;
				lock (_lock)
				{
					if (_current == null)
					{
						break;
					}
					task = _worker ?? Task.CompletedTask;
					goto IL_004c;
				}
				IL_004c:
				task.ContinueWith((Task _) =>
				{
				}).Wait(TimeSpan.FromSeconds(1L));
			}
			if (GameRunning(game))
			{
				continue;
			}
			try
			{
				lock (_recorderLock)
				{
					GameRecord gameRecord = Store.LoadGame(game.Id);
					MigrationLoaded?.Invoke();
					long num = RecordingFiles(game).Sum((Func<string, long>)Length);
					if (!ImportRecording(game, gameRecord, migrate: true))
					{
						continue;
					}
					Store.SaveGame(game.Id, gameRecord);
					Log?.Report($"{game.Name}: recording stored compactly ({num >> 20} MB -> {RecordingFiles(game).Sum((Func<string, long>)Length) >> 20} MB)");
					goto IL_01c4;
				}
				IL_01c4:
				Refresh(game);
			}
			catch (Exception ex)
			{
				Log?.Report(game.Name + ": storing the recording compactly failed: " + ex.Message);
			}
		}
	}

	public static bool IsOurProxy(string dll)
	{
		if (File.Exists(dll))
		{
			return ((ReadOnlySpan<byte>)File.ReadAllBytes(dll).AsSpan()).IndexOf((ReadOnlySpan<byte>)ProxyMarker) >= 0;
		}
		return false;
	}

	private static string Sha256(string path)
	{
		using FileStream source = File.OpenRead(path);
		return Convert.ToHexString(SHA256.HashData(source));
	}

	public static string SharedRecorder(string first)
	{
		return "it shares " + first + "'s recorder, so turn recording on there";
	}

	public static string RecordOnFirst(string first)
	{
		return "turn on recording on " + first + "'s page and play this game for about 5 minutes";
	}

	public static string SharedOn(string first)
	{
		return first + "'s recorder is on: play this game for about 5 minutes";
	}

	public static string ShaderModReason(string mod, LayerBlock why)
	{
		return why switch
		{
			LayerBlock.NotBesideExe => "ReShade must be next to the game's exe to compile through " + mod, 
			LayerBlock.OptiScalerOff => "Set LoadReshade=true in OptiScaler.ini to compile through " + mod, 
			LayerBlock.OptiScalerIni => "Fix the section headers in OptiScaler.ini to compile through " + mod, 
			LayerBlock.ProxyLibrary => "Give the DLL ReShade.ini's ProxyLibrary loads a name the game loads by itself (OptiScaler: winmm.dll), then set EnableProxyLibrary=0, to compile through " + mod, 
			_ => "Rename ReShade to dxgi.dll next to the exe to compile through " + mod, 
		};
	}

	private static string ShaderModReason(ReShadeInstall r)
	{
		return ShaderModReason(r.ShaderMod.Mod, r.Block);
	}

	private static ReShadeInstall? BlockingMod(Game g)
	{
		if (GameFiles.DetectAntiCheat(g, quick: true) == AntiCheat.None)
		{
			ReShadeInstall reShadeInstall = ReShade.Detect(g);
			if ((object)reShadeInstall != null && reShadeInstall.Blocks)
			{
				return reShadeInstall;
			}
		}
		return null;
	}

	public static string ShaderModNote(GameState s)
	{
		if (s.ShaderModLayer)
		{
			return s.ShaderMod + " changes this game's pipelines: compiles run through a copy of ReShade and " + s.ShaderMod + ", and " + (s.ShaderModAsD3D12 ? "with ReShade installed as d3d12.dll, the ones it adds while the game draws are recorded only with Record alongside ReShade on" : "the ones it adds while the game draws are recorded as you play");
		}
		return s.ShaderMod + " replaces some shaders: those few pipelines aren't precompiled";
	}

	public (string Dir, string Fingerprint)? LayerFor(Game g, string workDir)
	{
		if (GameFiles.DetectAntiCheat(g) == AntiCheat.None)
		{
			ReShadeInstall reShadeInstall = ReShade.Detect(g);
			if ((object)reShadeInstall != null)
			{
				string fingerprint = reShadeInstall.Fingerprint;
				if (fingerprint != null)
				{
					return (ReShade.Stage(reShadeInstall, Path.Combine(workDir, "layer"), 134217728L, 536870912L), fingerprint);
				}
			}
		}
		return null;
	}

	private string? LayerNow(Game g)
	{
		if (ScannedAntiCheat(g) != AntiCheat.None)
		{
			return null;
		}
		return ReShade.Detect(g)?.Fingerprint;
	}

	private AntiCheat ScannedAntiCheat(Game g)
	{
		lock (_scanLock)
		{
			Evaluation valueOrDefault = (_scan ?? (_scan = Store.LoadScan())).GetValueOrDefault(g.Id);
			if ((object)valueOrDefault != null)
			{
				return valueOrDefault.AntiCheat;
			}
		}
		return GameFiles.DetectAntiCheat(g);
	}

	public static string? RecorderSkip(GameState s, string? modSkip)
	{
		if (!s.AnyAntiCheat)
		{
			if (!RecorderCrashes(s.Game))
			{
				if (!s.ShaderModBlocks)
				{
					if (!s.RootUnconfirmed)
					{
						if (!s.StreamlineFirst)
						{
							if (!(s.Engine == null) && (s.Status != GameStatus.Unsupported || !(s.StatusReason != "the compile can't reach this game: it reads its shaders from a different driver cache than the one SCSKiller fills. Clear its cache to free the space")))
							{
								if (s.Engine.GraphicsApi.Contains("D3D12") || s.Records11)
								{
									if (modSkip == null)
									{
										if (!s.Game.ExePath.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
										{
											return null;
										}
										return "the game folder needs administrator";
									}
									return modSkip;
								}
								return "not DirectX 12";
							}
							return "not supported yet";
						}
						return "DLSS on NVIDIA loads DirectX 12 before the recorder can";
					}
					return "game folder not confirmed";
				}
				return "an HDR mod changes every pipeline";
			}
			return "the recorder crashes this game, so it's off for now";
		}
		return "anti-cheat";
	}

	internal static bool RecorderCrashes(Game g)
	{
		string id = g.Id;
		if ((!(id == "steam:3357650") && !(id == "steam:700330")) || 1 == 0)
		{
			HashSet<string> noRecorderExes = NoRecorderExes;
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(g.ExePath);
			string item;
			if (!fileNameWithoutExtension.EndsWith("_Plus", StringComparison.OrdinalIgnoreCase))
			{
				item = fileNameWithoutExtension;
			}
			else
			{
				string text = fileNameWithoutExtension;
				item = text.Substring(0, text.Length - 5);
			}
			return noRecorderExes.Contains(item);
		}
		return true;
	}

	public static string? CrashNote(Game g)
	{
		if (!RecorderCrashes(g))
		{
			return null;
		}
		return "Not available: " + g.Name + " doesn't start with the recorder in its folder.";
	}

	public static bool Records11(EngineInfo? engine, VendorCaps caps)
	{
		if ((object)engine != null && engine.Unsupported == null)
		{
			return Records11Api(engine, caps);
		}
		return false;
	}

	private static bool Records11Api(EngineInfo? e, VendorCaps caps)
	{
		if ((object)e != null && e.GraphicsApi.StartsWith("D3D11") && !e.GraphicsApi.Contains("D3D12"))
		{
			return !Planner.D3D11CoversVs(caps);
		}
		return false;
	}

	public static bool Frames11(EngineInfo? engine, VendorCaps caps)
	{
		if ((object)engine != null && engine.Unsupported == null)
		{
			if (engine.GraphicsApi.StartsWith("D3D11") && !engine.GraphicsApi.Contains("D3D12"))
			{
				Stage[]? array = Planner.D3D11Stages(caps);
				if (array == null)
				{
					return false;
				}
				return array.Contains(Stage.Vertex, null);
			}
		}
		return false;
	}

	private static bool As11(EngineInfo? engine, GameRecord rec, VendorCaps caps)
	{
		if (!Records11(engine, caps))
		{
			if (Frames11(engine, caps))
			{
				BeforeAfterRecord beforeAfter = rec.BeforeAfter;
				if (beforeAfter != null)
				{
					return beforeAfter.Stage != BeforeAfterStage.Done;
				}
				return false;
			}
			return false;
		}
		return true;
	}

	private static bool RecorderWanted(RecorderOverride o, bool recordAllGames, string? skip, bool frames11)
	{
		if (!frames11)
		{
			return RecorderEffective(o, recordAllGames, skip);
		}
		if (skip == null)
		{
			return o != RecorderOverride.Off;
		}
		return false;
	}

	private string? PairRecorderSkip(GameState s, GameRecord rec)
	{
		string directoryName = Path.GetDirectoryName(s.Game.ExePath);
		bool ours;
		try
		{
			ours = IsOurProxy(Path.Combine(directoryName, "d3d11.dll"));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			ours = false;
		}
		return RecorderSkip(s with
		{
			Records11 = true
		}, ModSkip(s.Game, directoryName, rec, ours, "d3d11.dll")) ?? _recorderSkips.GetValueOrDefault(s.Game.Id);
	}

	private static string RecorderDll(GameRecord rec, EngineInfo? engine, VendorCaps caps, bool ags)
	{
		if (!rec.RecorderFiles.ContainsKey("amd_ags_x64.dll") && !(rec.RecorderChained?.Name == "amd_ags_real.dll"))
		{
			if (!rec.RecorderFiles.ContainsKey("d3d11.dll") && !(rec.RecorderChained?.Name == "d3d11.scskiller-next.dll"))
			{
				if (!rec.RecorderFiles.ContainsKey("d3d12.dll") && !(rec.RecorderChained != null))
				{
					if (!ags)
					{
						if (!As11(engine, rec, caps))
						{
							return "d3d12.dll";
						}
						return "d3d11.dll";
					}
					return "amd_ags_x64.dll";
				}
				return "d3d12.dll";
			}
			return "d3d11.dll";
		}
		return "amd_ags_x64.dll";
	}

	private static bool FramesOnly(string dll, VendorCaps caps)
	{
		if (dll == "d3d11.dll")
		{
			return Planner.D3D11CoversVs(caps);
		}
		return false;
	}

	private string? FramesOnlyRefusal(Game g)
	{
		if (!Planner.D3D11CoversVs(Vendor.Caps))
		{
			return null;
		}
		string directoryName = Path.GetDirectoryName(g.ExePath);
		try
		{
			if (!IsOurProxy(Path.Combine(directoryName, "d3d11.dll")))
			{
				return null;
			}
			BeforeAfterRecord beforeAfter = Store.LoadGame(g.Id).BeforeAfter;
			if (beforeAfter == null || beforeAfter.Stage == BeforeAfterStage.Done)
			{
				return "its frame times recorder has no before and after recording in progress";
			}
			string path = Path.Combine(directoryName, "scskiller.ini");
			object result;
			if (File.Exists(path))
			{
				string text = GetIni(Store, directoryName)?.Tracked;
				if (text != null && text.Length > 0 && text.Split('|').Contains(Sha256(path)) && File.ReadAllLines(path).Contains("frames_only=1"))
				{
					result = null;
					goto IL_00dd;
				}
			}
			result = "its scskiller.ini doesn't keep the d3d11.dll to frame times";
			goto IL_00dd;
			IL_00dd:
			return (string?)result;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return "its d3d11.dll or scskiller.ini can't be read: " + ex.Message;
		}
	}

	private static string ChainOf(string dll)
	{
		if (!(dll == "d3d11.dll"))
		{
			if (!(dll == "amd_ags_x64.dll"))
			{
				return "d3d12.scskiller-next.dll";
			}
			return "amd_ags_real.dll";
		}
		return "d3d11.scskiller-next.dll";
	}

	private static string RoleOf(ChainedDll c)
	{
		if (!(c.Name == "d3d11.scskiller-next.dll"))
		{
			if (!(c.Name == "amd_ags_real.dll"))
			{
				return "d3d12.dll";
			}
			return "amd_ags_x64.dll";
		}
		return "d3d11.dll";
	}

	private static bool Orphaned(string dir, GameRecord rec)
	{
		ChainedDll recorderChained = rec.RecorderChained;
		if ((object)recorderChained != null)
		{
			string text = Path.Combine(dir, RoleOf(recorderChained));
			if (!File.Exists(text) && !File.Exists(text + ".scskiller-new"))
			{
				try
				{
					return File.ReadAllLines(Path.Combine(dir, "scskiller.ini")).Contains("next=" + recorderChained.Name);
				}
				catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
					return false;
				}
			}
		}
		return false;
	}

	public static bool OurProxyIn(string dir)
	{
		return ProxyNames.Any((string n) => IsOurProxy(Path.Combine(dir, n)));
	}

	private static string? ModSkip(Game g, string dir, GameRecord rec, bool ours, string name)
	{
		string text = ReShade.PackageRoot(g);
		if (text != null && !text.Equals(GameFiles.DirKey(dir), StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(text, name)))
		{
			return "a DLL in the game's package folder loads instead of the recorder";
		}
		string text2 = Path.Combine(dir, name);
		if (name == "amd_ags_x64.dll")
		{
			return null;
		}
		ChainedDll recorderChained = rec.RecorderChained;
		string text3;
		if ((object)recorderChained != null)
		{
			text3 = Path.Combine(dir, recorderChained.Name);
		}
		else
		{
			text3 = ((!ours && File.Exists(text2)) ? text2 : null);
		}
		if (text3 == null)
		{
			return null;
		}
		string result;
		if (!rec.RecordAlongsideMod)
		{
			result = "a mod's DLL is in the recorder's place";
		}
		else
		{
			string text4 = ChainBlocker(text3);
			string text5;
			if (text4 == null)
			{
				text5 = null;
			}
			else
			{
				text5 = ((!(text4 == "vkd3d-proton or DXVK runs the game on Vulkan, whose pipelines a DirectX warm doesn't compile")) ? "a mod's DLL is in the recorder's place and stops working renamed" : "vkd3d-proton or DXVK runs the game on Vulkan, whose pipelines a DirectX warm doesn't compile");
			}
			result = text5;
		}
		return result;
	}

	public static string? ChainBlocker(string dll)
	{
		string productName;
		string text2;
		bool flag;
		try
		{
			string? text = Middleware.ExportName(dll);
			productName = FileVersionInfo.GetVersionInfo(dll).ProductName;
			text2 = text;
			Span<byte> span = File.ReadAllBytes(dll).AsSpan();
			flag = ((ReadOnlySpan<byte>)span).IndexOf("vkd3d-proton"u8) >= 0 || ((ReadOnlySpan<byte>)span).IndexOf("DXVK_HUD"u8) >= 0 || ((ReadOnlySpan<byte>)span).IndexOf("DXVK_LOG_LEVEL"u8) >= 0;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return Path.GetFileName(dll) + " can't be read";
		}
		string[] array = new string[2] { text2, productName };
		foreach (string n in array)
		{
			if (n != null && new string[3] { "OptiScaler", "SpecialK", "Special K" }.Any((string m) => n.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
			{
				return n + " picks its role from its file name";
			}
		}
		if (!flag)
		{
			return null;
		}
		return "vkd3d-proton or DXVK runs the game on Vulkan, whose pipelines a DirectX warm doesn't compile";
	}

	public static string NotChainableReason(string? mod, string dll = "d3d12.dll")
	{
		if (mod == null || !mod.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase))
		{
			return (mod ?? "another mod") + " is installed as " + dll + ", and the recorder can't run alongside it: renamed it stops working";
		}
		return "OptiScaler is installed as " + dll + ", and renamed it stops working (it picks its role from its file name). To record alongside it, rename it to dxgi.dll (or winmm.dll or version.dll if a dxgi.dll is already there): OptiScaler supports those names";
	}

	public string? RecorderMod(string gameId)
	{
		string directoryName = Path.GetDirectoryName(Find(gameId).Game.ExePath);
		GameRecord rec = Store.LoadGame(gameId);
		string text = RecorderDll(rec, Find(gameId).Engine, Vendor.Caps, Find(gameId).AgsHost);
		return ModName(directoryName, rec, IsOurProxy(Path.Combine(directoryName, text)), text);
	}

	private static string? ModName(string dir, GameRecord rec, bool ours, string name)
	{
		if (name == "amd_ags_x64.dll")
		{
			return null;
		}
		string text = Path.Combine(dir, rec.RecorderChained?.Name ?? name);
		if (!File.Exists(text) || ((rec.RecorderChained == null) & ours))
		{
			return null;
		}
		try
		{
			FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(text);
			return new string[3]
			{
				versionInfo.ProductName,
				versionInfo.FileDescription,
				Middleware.ExportName(text)
			}.FirstOrDefault((string n) => !string.IsNullOrWhiteSpace(n))?.Trim() ?? name;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return name;
		}
	}

	public void SetRecordAlongsideMod(string gameId, bool on)
	{
		lock (_recorderLock)
		{
			GameState gameState = Find(gameId);
			if (!(Unconfirmed(gameState.Game) & on))
			{
				GameRecord gameRecord = Store.LoadGame(gameId);
				gameRecord.RecordAlongsideMod = on;
				Store.SaveGame(gameId, gameRecord);
				if (!Reconcile(gameState))
				{
					Refresh(gameState.Game);
				}
			}
		}
	}

	public static bool RecorderEffective(RecorderOverride o, bool recordAllGames, string? skip)
	{
		if (skip == null)
		{
			if (o != RecorderOverride.On)
			{
				return (o == RecorderOverride.Default) & recordAllGames;
			}
			return true;
		}
		return false;
	}

	public void InstallRecorder(string gameId)
	{
		SetRecorderOverride(gameId, RecorderOverride.On);
		GameState gameState = Find(gameId);
		if (!gameState.RecorderInstalled || gameState.RecorderNote != null)
		{
			throw new InvalidOperationException($"{gameState.Game.Name}: recorder not {(gameState.RecorderInstalled ? "updated" : "installed")}: {gameState.RecorderNote ?? gameState.RecorderSkip}");
		}
	}

	public bool ClearRecording(string gameId)
	{
		GameState gameState = Find(gameId);
		lock (_lock)
		{
			if (_current == gameId)
			{
				throw new InvalidOperationException("a compile of " + gameState.Game.Name + " is in progress");
			}
		}
		lock (_recorderLock)
		{
			using (Recordings.Lock(RecordingPath(gameId)))
			{
				if (!GameFolderWrite(gameState.Game))
				{
					throw new InvalidOperationException(gameState.Game.Name + " is running");
				}
				List<FileInfo> list = (from f in RecordingFiles(gameState.Game)
					select new FileInfo(f) into f
					where f.Exists
					select f).ToList();
				if (list.Count == 0)
				{
					return false;
				}
				AppCacheFiles.DeleteAll(list);
				GameRecord gameRecord = Store.LoadGame(gameId);
				GameRecord gameRecord2 = gameRecord;
				GameRecord gameRecord3 = gameRecord;
				GameRecord gameRecord4 = gameRecord;
				DateTimeOffset? recordingImportedAt = DateTimeOffset.Now;
				gameRecord.RecordingImportedAt = recordingImportedAt;
				gameRecord2.RecordingInbox = null;
				gameRecord3.RecordingIndexHash = null;
				gameRecord4.RecordedLong = false;
				gameRecord.LastPlay = null;
				WriteKeys(gameState.Game, gameRecord);
				Store.SaveGame(gameId, gameRecord);
				RecorderLog(gameState.Game.Name + ": recording cleared (" + string.Join(", ", list.Select((FileInfo f) => f.FullName)) + ")");
				UpdateRecorderIni(gameState.Game, gameRecord);
				Refresh(gameState.Game);
				return true;
			}
		}
	}

	public void UninstallRecorder(string gameId)
	{
		SetRecorderOverride(gameId, RecorderOverride.Off);
		GameState gameState = Find(gameId);
		if ((object)gameState != null && gameState.RecorderInstalled)
		{
			throw new InvalidOperationException(gameState.Game.Name + ": recorder not removed: " + gameState.RecorderNote);
		}
	}

	private bool Unlisted(string id)
	{
		if (!_removed.Contains(id))
		{
			return _hidden.ContainsKey(id);
		}
		return true;
	}

	private bool UnlistedNow(string id)
	{
		lock (_lock)
		{
			return Unlisted(id);
		}
	}

	private List<Game> Visible(List<Game> found)
	{
		while (true)
		{
			long hideChanges;
			lock (_lock)
			{
				hideChanges = _hideChanges;
			}
			Dictionary<string, Game> hidden = (from g in found.DistinctBy((Game g) => g.Id)
				where Store.LoadGame(g.Id).Hidden
				select g).ToDictionary((Game g) => g.Id);
			lock (_lock)
			{
				if (hideChanges != _hideChanges)
				{
					continue;
				}
				_hidden = hidden;
				return found.Where((Game g) => !hidden.ContainsKey(g.Id)).ToList();
			}
		}
	}

	private bool Unconfirmed(Game g)
	{
		if (g.Store == SCSKiller.Core.Store.Manual)
		{
			ManualSource? manual = Manual;
			if (manual == null)
			{
				return true;
			}
			return !manual.Confirmed(g);
		}
		return false;
	}

	private bool StaleCopy(Game g)
	{
		if (g.Store == SCSKiller.Core.Store.Manual)
		{
			return Manual?.Stale(g) ?? false;
		}
		return false;
	}

	private Game? HiddenOwner(string exe)
	{
		return HiddenGames.FirstOrDefault((Game g) => GameFiles.Inside(g.InstallDir, exe));
	}

	private GameState? Listed(string exe)
	{
		List<GameState> source = Games.Where((GameState s) => s.Game.Store != SCSKiller.Core.Store.Manual).ToList();
		return source.FirstOrDefault((GameState s) =>
		{
			if (!string.Equals(s.Game.ExePath, exe, StringComparison.OrdinalIgnoreCase))
			{
				string[]? array = Collections.Exes(s.Game);
				if (array != null && array.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase))
				{
					return GameFiles.Inside(s.Game.InstallDir, exe);
				}
				return false;
			}
			return true;
		}) ?? source.FirstOrDefault((GameState s) => GameFiles.Inside(s.Game.InstallDir, exe));
	}

	public ManualAdd PreviewManualGame(string exePath)
	{
		if (Manual == null)
		{
			throw new InvalidOperationException("adding games isn't available here");
		}
		GameState gameState = Listed(Path.GetFullPath(exePath));
		if ((object)gameState != null)
		{
			return new ManualAdd(gameState.Game, Existed: true);
		}
		Game game = HiddenOwner(Path.GetFullPath(exePath));
		if ((object)game != null)
		{
			return new ManualAdd(game, Existed: true);
		}
		ManualEntry manualEntry = ManualSource.Resolve(exePath);
		GameState gameState2 = Listed(manualEntry.Exe);
		if ((object)gameState2 != null)
		{
			return new ManualAdd(gameState2.Game, Existed: true);
		}
		string id = ManualSource.IdOf(manualEntry.Exe);
		Game game2 = Games.FirstOrDefault((GameState s) => s.Game.Id == id)?.Game;
		ManualEntry manualEntry2 = Manual.Entries().FirstOrDefault((ManualEntry e) => ManualSource.IdOf(e.Exe) == id);
		if ((object)manualEntry2 == null)
		{
			if (!Manual.InFolder(id) || !(game2 != null))
			{
				return new ManualAdd(ManualSource.ToGame(manualEntry), Existed: false);
			}
			return new ManualAdd(game2, Existed: true);
		}
		return new ManualAdd(game2 ?? ManualSource.ToGame(manualEntry2), Existed: true);
	}

	public string? ManualFolderProblem(string exePath, string installDir)
	{
		return ManualSource.RootProblem(installDir, exePath, Games.Select((GameState s) => s.Game));
	}

	public ManualAdd AddManualGame(string exePath, string? installDir = null)
	{
		ManualSource manual = Manual ?? throw new InvalidOperationException("adding games isn't available here");
		GameState gameState = Listed(Path.GetFullPath(exePath));
		if ((object)gameState != null)
		{
			return new ManualAdd(gameState.Game, Existed: true);
		}
		Game game = HiddenOwner(Path.GetFullPath(exePath));
		if ((object)game != null)
		{
			return new ManualAdd(game, Existed: true);
		}
		string full = Path.GetFullPath(exePath);
		object obj = manual.Entries().FirstOrDefault((ManualEntry e) => string.Equals(e.Exe, full, StringComparison.OrdinalIgnoreCase));
		if (obj == null)
		{
			GameState gameState2 = Games.FirstOrDefault((GameState s) => manual.InFolder(s.Game.Id) && string.Equals(s.Game.ExePath, full, StringComparison.OrdinalIgnoreCase));
			obj = (((object)gameState2 != null) ? new ManualEntry(full, gameState2.Game.InstallDir, gameState2.Game.Name) : null) ?? ManualSource.Resolve(exePath);
		}
		ManualEntry manualEntry = (ManualEntry)obj;
		GameState gameState3 = Listed(manualEntry.Exe);
		if ((object)gameState3 != null)
		{
			return new ManualAdd(gameState3.Game, Existed: true);
		}
		if (installDir != null)
		{
			string text = ManualFolderProblem(manualEntry.Exe, installDir);
			if (text != null)
			{
				throw new ArgumentException(text);
			}
			manualEntry = manualEntry with
			{
				InstallDir = GameFiles.DirKey(installDir),
				Confirmed = true
			};
		}
		ManualAdd added;
		lock (_recorderLock)
		{
			string id = ManualSource.IdOf(manualEntry.Exe);
			ManualEntry manualEntry2 = ((installDir != null) ? manual.Entries().FirstOrDefault((ManualEntry e) => ManualSource.IdOf(e.Exe) == id) : null);
			if (manualEntry2 != null && !GameFiles.DirKey(manualEntry2.InstallDir).Equals(manualEntry.InstallDir, StringComparison.OrdinalIgnoreCase))
			{
				Game game2 = ManualSource.ToGame(manualEntry2);
				NewRemoval(game2);
				GameRecord gameRecord = Store.LoadGame(id);
				string directoryName = Path.GetDirectoryName(game2.ExePath);
				if (!Foreign(directoryName))
				{
					StripIni(directoryName, Store, gameRecord.RecorderChained?.Name, game2.InstallDir, game2.Name, RecorderLog);
				}
				Store.SaveGame(id, gameRecord);
				FolderChanging?.Invoke();
			}
			(Game Game, bool Existed) tuple = manual.Add(manualEntry);
			Game item = tuple.Game;
			bool item2 = tuple.Existed;
			added = new ManualAdd(item, item2);
		}
		lock (_lock)
		{
			_removed.Remove(added.Game.Id);
		}
		Log?.Report($"{added.Game.Name}: {(added.Existed ? "game folder set" : "added to the library")} ({added.Game.ExePath}, {(manualEntry.Confirmed ? "confirmed" : "suggested")} folder {added.Game.InstallDir})");
		GameState gameState4 = Games.FirstOrDefault((GameState s) => s.Game.Id == added.Game.Id);
		if ((object)gameState4 == null || IsUnchecked(gameState4))
		{
			RefreshAsked(Following(Unreal(added.Game)));
		}
		else if (manualEntry.Confirmed)
		{
			Refresh(added.Game);
			if (ManageRecorders)
			{
				ReconcileRecorders(added.Game.Id);
			}
		}
		return added;
	}

	public void RemoveManualGame(string gameId)
	{
		ManualSource manual = Manual;
		Game g = Games.FirstOrDefault((GameState s) => s.Game.Id == gameId)?.Game ?? manual?.Entries().Select(ManualSource.ToGame).FirstOrDefault((Game x) => x.Id == gameId);
		if (manual == null || (object)g == null || g.Store != SCSKiller.Core.Store.Manual)
		{
			throw new InvalidOperationException("only a game you added can be removed from the library");
		}
		using (CompileLock(gameId) ?? throw new InvalidOperationException("a compile of " + g.Name + " is in progress"))
		{
			lock (_recorderLock)
			{
				GameRecord gameRecord = Store.LoadGame(gameId);
				List<string> source = (from e in new string[2]
					{
						gameRecord.RecorderExe ?? gameRecord.RecorderMoveFrom,
						g.ExePath
					}.OfType<string>()
					select Path.GetDirectoryName(e)).ToList();
				List<string> list = (from d in new string[4] { gameRecord.RecorderExe, gameRecord.RecorderMoveFrom, gameRecord.RecorderMoveTo, g.ExePath }.OfType<string>().Select(Path.GetDirectoryName).OfType<string>()
						.Distinct(StringComparer.OrdinalIgnoreCase)
					where !Directory.Exists(d) || !Foreign(d)
					select d).ToList();
				List<string> away;
				if (gameRecord.RecorderFiles.Count > 0 || gameRecord.RecorderChained != null || gameRecord.RecorderRollback || list.Any(Left) || list.Any((string d) => (!Directory.Exists(d) && !Gone(d)) || IniPending(Store, d)))
				{
					if (GameRunning(g))
					{
						throw new InvalidOperationException(g.Name + " is running: close the game first");
					}
					gameRecord.RecorderRollback = false;
					foreach (string item in list)
					{
						bool flag = RemoveRecorder(item, gameRecord, Store, g.Name, RecorderLog, g.InstallDir);
						Store.SaveGame(gameId, gameRecord);
						if (flag && source.Contains(item, StringComparer.OrdinalIgnoreCase) && !RemoveRecorderData(item, RecordingPath(gameId), gameRecord.RecordingInbox, () => GameRunning(g), gameRecord.RecorderRollback || IniPending(Store, item)) && GameRunning(g))
						{
							throw new InvalidOperationException(g.Name + " started: close the game first");
						}
					}
					if (gameRecord.RecorderRollback || list.Any((string d) => IniPending(Store, d)))
					{
						Store.SaveGame(gameId, gameRecord);
						throw new InvalidOperationException(g.Name + ": the recorder's scskiller.ini can't be taken out yet: try again once nothing holds it");
					}
					away = list.Where((string d) => !Directory.Exists(d) && !Gone(d)).ToList();
					if (!Away(gameRecord.RecorderMoveFrom))
					{
						GameRecord gameRecord2 = gameRecord;
						gameRecord.RecorderMoveFrom = null;
						gameRecord2.RecorderMoveTo = null;
					}
					if (!Away(gameRecord.RecorderExe) && !Away(gameRecord.RecorderMoveFrom))
					{
						gameRecord.RecorderInstallDir = null;
					}
					Store.SaveGame(gameId, gameRecord);
					RecorderLog(g.Name + ": recorder removed before the game left the library (" + string.Join(", ", list.Except(away)) + ")" + ((away.Count > 0) ? ("; left in " + string.Join(", ", away) + " until it can be reached") : ""));
				}
				bool Away(string? exe)
				{
					if (exe != null)
					{
						return away.Contains(Path.GetDirectoryName(exe), StringComparer.OrdinalIgnoreCase);
					}
					return false;
				}
			}
			DropKept(gameId);
			List<QueueItem> list2 = new List<QueueItem>();
			lock (_lock)
			{
				if (_current == gameId)
				{
					throw new InvalidOperationException("a compile of " + g.Name + " is in progress");
				}
				_whenIdle.Remove(gameId);
				_planOnly.Remove(gameId);
				_prompt.Remove(gameId);
				list2.AddRange(_queue.Where((QueueItem q) => q.GameId == gameId));
				_queue.RemoveAll((QueueItem q) => q.GameId == gameId);
				_removed.Add(gameId);
				_games.RemoveAll((GameState s) => s.Game.Id == gameId);
			}
			manual.Remove(gameId);
			DeleteSessionSnapshots(gameId);
			Log?.Report(g.Name + ": removed from the library");
			foreach (QueueItem item2 in list2)
			{
				QueueChanged?.Invoke(item2);
			}
		}
		static bool Left(string dir)
		{
			if (!OurProxyIn(dir))
			{
				return RecorderDataFiles.Concat(Recordings.AppFiles).Append("scskiller.ledger").Any((string f) => File.Exists(Path.Combine(dir, f)));
			}
			return true;
		}
	}

	public void HideGame(string gameId)
	{
		Game game = Find(gameId).Game;
		if (game.Store == SCSKiller.Core.Store.Manual)
		{
			throw new InvalidOperationException("a game you added is removed, not hidden");
		}
		using (CompileLock(gameId) ?? throw new InvalidOperationException("a compile of " + game.Name + " is in progress"))
		{
			lock (_lock)
			{
				if (_current == gameId)
				{
					throw new InvalidOperationException("a compile of " + game.Name + " is in progress");
				}
			}
			if (GameRunning(game))
			{
				throw new InvalidOperationException(game.Name + " is running: close the game first");
			}
			BeforeAfterRecord beforeAfter = Store.LoadGame(gameId).BeforeAfter;
			if (beforeAfter != null && beforeAfter.Stage != BeforeAfterStage.Done)
			{
				EndBeforeAfter(gameId, compile: false);
			}
			if (IsUnchecked(Find(gameId)))
			{
				Refresh(game);
			}
			if ((IsUnchecked(Find(gameId)) || _unread.ContainsKey(gameId)) && RecorderOnRecord(Store.LoadGame(gameId)))
			{
				throw new InvalidOperationException(game.Name + ": couldn't read its files, so its recorder can't be taken out");
			}
			if (Find(gameId).RecorderInstalled)
			{
				UninstallRecorder(gameId);
			}
			GameRecord gameRecord = Store.LoadGame(gameId);
			gameRecord.Hidden = true;
			Store.SaveGame(gameId, gameRecord);
			List<QueueItem> list = new List<QueueItem>();
			bool flag;
			lock (_lock)
			{
				if (!(flag = _current == gameId))
				{
					_whenIdle.Remove(gameId);
					_planOnly.Remove(gameId);
					_prompt.Remove(gameId);
					list.AddRange(_queue.Where((QueueItem q) => q.GameId == gameId));
					_queue.RemoveAll((QueueItem q) => q.GameId == gameId);
					_hidden[gameId] = game;
					_hideChanges++;
					_games.RemoveAll((GameState s) => s.Game.Id == gameId);
				}
			}
			if (flag)
			{
				gameRecord.Hidden = false;
				Store.SaveGame(gameId, gameRecord);
				lock (_lock)
				{
					_hidden.Remove(gameId);
					_hideChanges++;
				}
				Refresh(game);
				throw new InvalidOperationException("a compile of " + game.Name + " is in progress");
			}
			Log?.Report(game.Name + ": hidden from the library");
			foreach (QueueItem item in list)
			{
				QueueChanged?.Invoke(item);
			}
			KeepList();
		}
	}

	public void ShowGame(string gameId)
	{
		Game valueOrDefault;
		lock (_lock)
		{
			valueOrDefault = _hidden.GetValueOrDefault(gameId);
		}
		if (valueOrDefault == null)
		{
			throw new ArgumentException(gameId + " isn't a hidden game");
		}
		GameRecord gameRecord = Store.LoadGame(gameId);
		gameRecord.Hidden = false;
		Store.SaveGame(gameId, gameRecord);
		lock (_lock)
		{
			_hidden.Remove(gameId);
			_hideChanges++;
		}
		Log?.Report(valueOrDefault.Name + ": shown in the library again");
		Refresh(valueOrDefault);
		if (ManageRecorders)
		{
			ReconcileRecorders(gameId);
		}
		KeepList();
	}

	public void SetRecorderOverride(string gameId, RecorderOverride value)
	{
		lock (_recorderLock)
		{
			GameState gameState = Find(gameId);
			if (!Unconfirmed(gameState.Game) || value != RecorderOverride.On)
			{
				GameRecord gameRecord = Store.LoadGame(gameId);
				if (value == RecorderOverride.On)
				{
					Claim(gameState.Game, gameRecord);
				}
				gameRecord.Recorder = value;
				Store.SaveGame(gameId, gameRecord);
				if (!Reconcile(gameState))
				{
					Refresh(gameState.Game);
				}
			}
		}
	}

	private static bool Chained(string dir)
	{
		return new string[3] { "d3d12.scskiller-next.dll", "d3d11.scskiller-next.dll", "amd_ags_real.dll" }.Any((string n) => File.Exists(Path.Combine(dir, n)));
	}

	private bool PlacedAsRecorded(string dir, GameRecord rec)
	{
		try
		{
			string path = Path.Combine(dir, "scskiller.ini");
			if (rec.RecorderFiles.Count > 0 && rec.RecorderFiles.All((KeyValuePair<string, string> f) => f.Key.Equals("scskiller.ini", StringComparison.OrdinalIgnoreCase) || (File.Exists(Path.Combine(dir, f.Key)) && Sha256(Path.Combine(dir, f.Key)) == f.Value)) && File.Exists(path))
			{
				string text = GetIni(Store, dir)?.Tracked;
				if (text != null && text.Length > 0 && text.Split('|').Contains(Sha256(path)))
				{
					string text2 = GetIni(new AppStore(Path.GetDirectoryName(DefaultLedger)), dir)?.Tracked;
					return text2 == null || text2.Length <= 0 || !text2.Split('|').Contains(Sha256(path));
				}
			}
			return false;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return false;
		}
	}

	private static bool NamedByInstalled(string dir)
	{
		try
		{
			IEnumerable<string> enumerable;
			try
			{
				enumerable = Directory.EnumerateDirectories(Path.Combine(Path.GetDirectoryName(DefaultLedger), "games")).ToList();
			}
			catch (DirectoryNotFoundException)
			{
				return false;
			}
			foreach (string item in enumerable)
			{
				JsonDocument jsonDocument;
				try
				{
					using FileStream utf8Json = new FileStream(Path.Combine(item, "state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
					jsonDocument = JsonDocument.Parse(utf8Json);
				}
				catch (Exception ex2) when ((ex2 is FileNotFoundException || ex2 is DirectoryNotFoundException) ? true : false)
				{
					continue;
				}
				using (jsonDocument)
				{
					JsonElement rootElement = jsonDocument.RootElement;
					if (rootElement.ValueKind != JsonValueKind.Object)
					{
						return true;
					}
					JsonElement e = (rootElement.TryGetProperty("OfflineSession", out var value) ? value : default(JsonElement));
					JsonValueKind valueKind = e.ValueKind;
					if ((valueKind > JsonValueKind.Object && valueKind != JsonValueKind.Null) || 1 == 0)
					{
						return true;
					}
					string[] array = new string[3]
					{
						Field(rootElement, "RecorderExe"),
						Field(rootElement, "RecorderMoveFrom"),
						Field(e, "Exe")
					};
					foreach (string text in array)
					{
						if (text != null)
						{
							string fullPath = Path.GetFullPath(text);
							if (SameDir(Path.GetDirectoryName(fullPath) ?? fullPath, dir))
							{
								return true;
							}
						}
					}
				}
			}
			return false;
		}
		catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException || ex3 is JsonException || ex3 is ArgumentException || ex3 is NotSupportedException || ex3 is InvalidOperationException) ? true : false)
		{
			return true;
		}
		static string? Field(JsonElement jsonElement, string name)
		{
			if (jsonElement.ValueKind != JsonValueKind.Object || !jsonElement.TryGetProperty(name, out var value2) || value2.ValueKind == JsonValueKind.Null)
			{
				return null;
			}
			return value2.GetString();
		}
	}

	private void Claim(Game g, GameRecord rec, bool unmarked = false)
	{
		List<string> list = new Game[2]
		{
			g,
			RecordedAt(g, rec)
		}.Select((Game x) => Path.GetDirectoryName(x.ExePath)).Distinct(StringComparer.OrdinalIgnoreCase).Where(Foreign)
			.ToList();
		if (list.Count == 0)
		{
			return;
		}
		if (!GameFolderWrite(g))
		{
			throw new InvalidOperationException(g.Name + " is running: close it first to take over the recorder another SCSKiller copy placed");
		}
		string text = list.FirstOrDefault(Chained);
		if (text != null)
		{
			throw new InvalidOperationException("the recorder another SCSKiller copy placed in " + text + " chains to a mod: turn it off there first");
		}
		foreach (string dir in list)
		{
			StampLedger(dir, !unmarked);
			if (unmarked && (NamedByInstalled(dir) || !PlacedAsRecorded(dir, rec)))
			{
				Unstamp(dir);
				throw new InvalidOperationException("it changed while it was checked");
			}
			string ini = Path.Combine(dir, "scskiller.ini");
			if (File.Exists(ini) && File.ReadAllText(ini).StartsWith("[scskiller]\r\n; written by SCSKiller: record the pipelines this game creates. Removed by 'uninstall recorder'.\r\nmode=record\r\n", StringComparison.Ordinal))
			{
				SetIni(Store, dir, (IniOwner? e) =>
				{
					IniOwner? obj = e ?? new IniOwner(dir);
					return obj with
					{
						Tracked = Sha256(ini),
						Users = false,
						Remove = false,
						Install = g.InstallDir
					};
				});
			}
			RecorderLog(g.Name + ": took over the recorder another SCSKiller copy placed (" + dir + ")");
		}
		rec.KeysPending = true;
		if (!unmarked)
		{
			rec.RecorderClaimed = true;
		}
		_recorderNotes.TryRemove(g.Id, out string _);
	}

	public void ReconcileRecorders(string? gameId = null)
	{
		if (gameId != null)
		{
			gameId = Collections.RecordedBy(gameId) ?? gameId;
		}
		lock (_recorderLock)
		{
			foreach (GameState item in Games.Where((GameState s) => gameId == null || s.Game.Id == gameId))
			{
				Reconcile(item);
			}
		}
	}

	private bool Reconcile(GameState s)
	{
		Game value = s.Game;
		string id = s.Game.Id;
		Game g = value;
		if (OfflineLive(id) || _unread.ContainsKey(id) || IsUnchecked(s) || Collections.RecordedBy(id) != null)
		{
			return false;
		}
		string dir = Path.GetDirectoryName(g.ExePath);
		GameRecord gameRecord = Store.LoadGame(id);
		if (!DefaultCopy)
		{
			string recorderExe = gameRecord.RecorderExe;
			if (recorderExe != null && gameRecord.RecorderChained == null && gameRecord.RecorderMoveFrom == null)
			{
				string directoryName = Path.GetDirectoryName(recorderExe);
				if (directoryName != null && SameDir(directoryName, dir) && !File.Exists(Path.Combine(dir, "scskiller.ledger")) && Foreign(dir) && !Chained(dir) && PlacedAsRecorded(dir, gameRecord) && !NamedByInstalled(dir))
				{
					if (!GameFolderWrite(g))
					{
						return false;
					}
					try
					{
						ClaimStep?.Invoke();
						Claim(g, gameRecord, unmarked: true);
						Store.SaveGame(id, gameRecord);
						Refresh(g);
					}
					catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) ? true : false)
					{
						RecorderLog($"{g.Name}: couldn't take over the recorder in {dir}: {ex.Message}");
					}
				}
			}
		}
		if (Foreign(g) || Foreign(RecordedAt(g, gameRecord)))
		{
			if (_recorderNotes.GetValueOrDefault(id) == "placed by another SCSKiller copy, so it may still record. Turn it on here to take it over, then off to remove it")
			{
				return false;
			}
			_recorderNotes[id] = "placed by another SCSKiller copy, so it may still record. Turn it on here to take it over, then off to remove it";
			RecordersLog(Store.DataDir, g.Name + ": recorder placed by another SCSKiller copy, so it may still record. Turn it on here to take it over, then off to remove it");
			Refresh(g);
			return true;
		}
		string name = RecorderDll(gameRecord, s.Engine, Vendor.Caps, s.AgsHost);
		string text = Path.Combine(dir, name);
		bool flag = IsOurProxy(text);
		bool flag2 = false;
		string text2 = gameRecord.RecorderMoveFrom ?? gameRecord.RecorderExe;
		string text3 = ((text2 == null) ? null : Path.GetDirectoryName(text2));
		bool flag3 = text3 != null && !text3.Equals(dir, StringComparison.OrdinalIgnoreCase) && (Directory.Exists(text3) || !Gone(text3)) && (gameRecord.RecorderMoveFrom != null || gameRecord.RecorderFiles.Count > 0 || gameRecord.RecorderChained != null || OurProxyIn(text3) || IniPending(Store, text3));
		string value2;
		string value3;
		if (flag3 && (gameRecord.RecorderMoveFrom == null || gameRecord.RecorderMoveTo != g.ExePath))
		{
			GameRecord gameRecord2 = gameRecord;
			value2 = text2;
			value3 = g.ExePath;
			gameRecord.RecorderMoveFrom = value2;
			gameRecord2.RecorderMoveTo = value3;
			Store.SaveGame(id, gameRecord);
		}
		if (gameRecord.RecorderRollback || _rollbacks.ContainsKey(id))
		{
			string text4;
			if (GameRunning(g))
			{
				text4 = "removed when the game exits";
			}
			else
			{
				text4 = (TakeOut(g, gameRecord, "a failed install's recorder") ? null : "couldn't remove all of it yet: tried again");
			}
			if (text4 == null)
			{
				_recorderNotes.TryRemove(id, out value3);
			}
			else
			{
				_recorderNotes[id] = text4;
			}
			Refresh(g);
			return true;
		}
		if (!gameRecord.Recorder.HasValue)
		{
			gameRecord.Recorder = ((flag && DefaultCopy) ? RecorderOverride.On : RecorderOverride.Default);
			Store.SaveGame(id, gameRecord);
			flag2 = true;
		}
		bool flag4 = false;
		if (flag3)
		{
			Game g2 = RecordedAt(g, gameRecord);
			string text5 = null;
			_moving[id] = g;
			try
			{
				if (!Directory.Exists(text3))
				{
					text5 = "moves when " + text3 + " can be reached";
				}
				else if (GameRunning(g2))
				{
					text5 = "moves when the game exits";
				}
				else
				{
					if (s.AntiCheat == AntiCheat.None)
					{
						MoveSessionFiles(text3, dir);
					}
					if (!Uninstall(g2, gameRecord))
					{
						if (GameRunning(g2))
						{
							text5 = "moves when the game exits";
						}
						else
						{
							text5 = (IniPending(Store, text3) ? ("moves once the scskiller.ini in " + text3 + " can be taken out") : ("couldn't move: the recording in " + text3 + " couldn't be imported"));
						}
					}
				}
				if (text5 == null)
				{
					GameRecord gameRecord2 = gameRecord;
					gameRecord.RecorderMoveFrom = null;
					gameRecord2.RecorderMoveTo = null;
					Store.SaveGame(id, gameRecord);
					RecorderLog($"{g.Name}: recorder removed ({text3}): the game runs {g.ExePath}");
					bool flag5 = IsOurProxy(text);
					flag2 = true;
					flag4 = true;
					flag = flag5;
				}
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException || ex2 is InvalidOperationException) ? true : false)
			{
				text5 = "couldn't move: " + ex2.Message;
				RecorderLog(g.Name + ": recorder " + text5);
			}
			finally
			{
				_moving.TryRemove(id, out value);
			}
			if (text5 != null)
			{
				if (text5 != _recorderNotes.GetValueOrDefault(id))
				{
					ConcurrentDictionary<string, string> recorderNotes = _recorderNotes;
					value3 = id;
					value2 = text5;
					recorderNotes[value3] = value2;
					flag2 = true;
				}
				if (flag2)
				{
					Refresh(g);
				}
				return flag2;
			}
		}
		else if (gameRecord.RecorderMoveFrom != null)
		{
			GameRecord gameRecord2 = gameRecord;
			gameRecord.RecorderMoveFrom = null;
			gameRecord2.RecorderMoveTo = null;
			if (gameRecord.RecorderExe == null)
			{
				gameRecord.RecorderInstallDir = null;
			}
			Store.SaveGame(id, gameRecord);
		}
		bool flag6 = ProxyNames.Any((string n) =>
		{
			try
			{
				return n != name && IsOurProxy(Path.Combine(dir, n));
			}
			catch (Exception ex8) when ((ex8 is IOException || ex8 is UnauthorizedAccessException) ? true : false)
			{
				return false;
			}
		});
		if (((flag | flag6) || gameRecord.RecorderFiles.Count > 0 || gameRecord.RecorderChained != null) && (gameRecord.RecorderExe != g.ExePath || gameRecord.RecorderInstallDir != g.InstallDir))
		{
			GameRecord gameRecord2 = gameRecord;
			value3 = g.ExePath;
			value2 = g.InstallDir;
			gameRecord.RecorderExe = value3;
			gameRecord2.RecorderInstallDir = value2;
			Store.SaveGame(id, gameRecord);
		}
		string stamp = null;
		long gen = 0L;
		bool flag7 = false;
		if (s.AntiCheat == AntiCheat.None && (flag || gameRecord.RecorderFiles.Count > 0 || gameRecord.RecorderChained != null))
		{
			string? text6 = Stamp(ArmTarget(g).ExePath);
			gen = RemovalGen(id);
			stamp = text6;
			AntiCheat antiCheat = FullCheck(g);
			if (antiCheat != AntiCheat.None)
			{
				AntiCheatFound(g, antiCheat);
				Refresh(g);
				return true;
			}
			flag7 = true;
		}
		bool flag8 = As11(s.Engine, gameRecord, Vendor.Caps);
		bool flag9 = !Orphaned(dir, gameRecord) && RecorderWanted(gameRecord.Recorder.Value, Settings.RecordAllGames && s.NoStutter == null, RecorderSkip(s with
		{
			Records11 = flag8
		}, ModSkip(g, dir, gameRecord, flag, name)), Frames11(s.Engine, Vendor.Caps));
		int num;
		if (!_pairInstall.ContainsKey(id))
		{
			BeforeAfterRecord beforeAfter = gameRecord.BeforeAfter;
			if (beforeAfter == null || beforeAfter.Stage == BeforeAfterStage.Done)
			{
				num = (gameRecord.RecorderClaimed ? 1 : 0);
				goto IL_0920;
			}
		}
		num = 1;
		goto IL_0920;
		IL_0920:
		bool flag10 = (byte)num != 0;
		int num2;
		if (flag9 & flag)
		{
			string text7 = ProxySha(name);
			if (text7 != null && Sha256(text) != text7)
			{
				num2 = (((FileVersion(text) <= FileVersion(ProxySource(name))) | flag10) ? 1 : 0);
				goto IL_096e;
			}
		}
		num2 = 0;
		goto IL_096e;
		IL_096e:
		bool flag11 = (byte)num2 != 0;
		string text8 = null;
		bool flag12 = (flag9 & flag4) && (!flag || (ProxySha(name) != null && ((FileVersion(text) <= FileVersion(ProxySource(name))) | flag10)));
		string text9;
		if (s.AgsHost)
		{
			text9 = "amd_ags_x64.dll";
		}
		else
		{
			text9 = (flag8 ? "d3d11.dll" : "d3d12.dll");
		}
		bool flag13 = flag9 && (flag || gameRecord.RecorderFiles.Count > 0 || gameRecord.RecorderChained != null) && name != text9;
		bool flag14 = !flag9 && gameRecord.RecorderExe != null && Path.GetDirectoryName(gameRecord.RecorderExe).Equals(dir, StringComparison.OrdinalIgnoreCase) && IniPending(Store, dir);
		if ((((flag9 != flag) | flag11 | flag12 | flag13 | flag6) || (!flag9 && (gameRecord.RecorderFiles.Count > 0 || gameRecord.RecorderChained != null))) | flag14)
		{
			if (GameRunning(g))
			{
				if (flag11)
				{
					text8 = "updates when the game exits";
				}
				else
				{
					text8 = (flag9 ? "installs when the game exits" : "removed when the game exits");
				}
				if (!flag9 && name == "d3d11.dll" && Frames11(s.Engine, Vendor.Caps))
				{
					Revoke(dir, on: true, RecorderLog);
				}
			}
			else
			{
				try
				{
					if (flag9)
					{
						string? text10 = Stamp(ArmTarget(g).ExePath);
						gen = RemovalGen(id);
						stamp = text10;
						if ((flag13 | flag6) && !Uninstall(g, gameRecord))
						{
							throw new InvalidOperationException(IniPending(Store, dir) ? "its scskiller.ini can't be taken out yet" : "the recording couldn't be imported");
						}
						Install(g, gameRecord, flag13 ? text9 : name);
						if (!Attest(ArmTarget(g), gameRecord, stamp, gen))
						{
							text8 = ForeignIni(dir, gameRecord);
						}
					}
					else
					{
						Uninstall(g, gameRecord);
					}
					if (gameRecord.RecorderClaimed)
					{
						gameRecord.RecorderClaimed = false;
						Store.SaveGame(id, gameRecord);
					}
					_recorderSkips.TryRemove(id, out value2);
					RecorderLog($"{g.Name}: recorder {(flag11 ? "updated" : (flag9 ? "installed" : "removed"))} ({dir})");
					flag2 = true;
				}
				catch (UnauthorizedAccessException ex3) when (flag11)
				{
					text8 = "couldn't update: " + ex3.Message;
					RecorderLog(g.Name + ": recorder " + text8);
				}
				catch (UnauthorizedAccessException)
				{
					if (_recorderSkips.GetValueOrDefault(id) != "the game folder needs administrator")
					{
						_recorderSkips[id] = "the game folder needs administrator";
						RecorderLog($"{g.Name}: recorder not installed: {"the game folder needs administrator"} ({dir})");
						flag2 = true;
					}
				}
				catch (Exception ex5) when ((ex5 is IOException || ex5 is InvalidOperationException) ? true : false)
				{
					string text11;
					if (flag11)
					{
						text11 = "couldn't update: ";
					}
					else
					{
						text11 = (flag9 ? "couldn't install: " : "couldn't remove: ");
					}
					text8 = text11 + ex5.Message;
					RecorderLog(g.Name + ": recorder " + text8);
				}
			}
		}
		else if (flag9 && !GameRunning(g))
		{
			if (gameRecord.KeysPending || gameRecord.RecorderClaimed)
			{
				try
				{
					if (gameRecord.KeysPending)
					{
						WriteKeys(g, gameRecord);
					}
					gameRecord.RecorderClaimed = false;
					Store.SaveGame(id, gameRecord);
				}
				catch (Exception ex6) when ((ex6 is IOException || ex6 is UnauthorizedAccessException) ? true : false)
				{
					RecorderLog(g.Name + ": couldn't write the keys file: " + ex6.Message);
				}
			}
			if (flag7 && !File.Exists(Path.Combine(dir, "scskiller.ini")))
			{
				UpdateRecorderIni(g, gameRecord, "");
			}
			if (flag7 && !Attest(ArmTarget(g), gameRecord, stamp, gen))
			{
				text8 = ForeignIni(dir, gameRecord);
			}
			else
			{
				UpdateRecorderIni(g, gameRecord);
			}
		}
		else if (!flag9 && !flag && gameRecord.RecorderExe == null && RecorderDataFiles.Concat(Recordings.AppFiles).Append("scskiller.ledger").Any((string f) => File.Exists(Path.Combine(dir, f))) && !GameRunning(g))
		{
			try
			{
				if (RemoveRecorderData(dir, RecordingPath(id), gameRecord.RecordingInbox, () => GameRunning(g), gameRecord.RecorderRollback || IniPending(Store, dir)))
				{
					RecorderLog(g.Name + ": the recorder's data files removed (" + dir + ")");
					flag2 = true;
				}
			}
			catch (Exception ex7) when ((ex7 is IOException || ex7 is UnauthorizedAccessException || ex7 is InvalidDataException) ? true : false)
			{
			}
		}
		if (text8 != _recorderNotes.GetValueOrDefault(id))
		{
			if (text8 == null)
			{
				_recorderNotes.TryRemove(id, out value2);
			}
			else
			{
				_recorderNotes[id] = text8;
			}
			flag2 = true;
		}
		if (flag2)
		{
			Refresh(g);
		}
		return flag2;
	}

	private static void MoveSessionFiles(string from, string to)
	{
		string[] sessionFiles = SessionFiles;
		foreach (string path in sessionFiles)
		{
			FileInfo fileInfo = new FileInfo(Path.Combine(from, path));
			if (fileInfo.Exists)
			{
				FileInfo fileInfo2 = new FileInfo(Path.Combine(to, path));
				if (fileInfo2.Exists && fileInfo2.LastWriteTimeUtc >= fileInfo.LastWriteTimeUtc)
				{
					fileInfo.Delete();
				}
				else
				{
					fileInfo.MoveTo(fileInfo2.FullName, overwrite: true);
				}
			}
		}
	}

	private void RecorderLog(string line)
	{
		Log?.Report(line);
		RecordersLog(Store.DataDir, line);
	}

	private static void RecordersLog(string dataDir, string line)
	{
		AppendLog(dataDir, "recorders.log", line, 0L);
	}

	public static void AppendLog(string dataDir, string file, string line, long maxBytes = 0L)
	{
		Mutex mutex = null;
		bool flag = false;
		try
		{
			string fullPath = Path.GetFullPath(Path.Combine(dataDir, file));
			try
			{
				mutex = new Mutex(initiallyOwned: false, LogMutexName(fullPath));
				try
				{
					flag = mutex.WaitOne(TimeSpan.FromSeconds(1L));
				}
				catch (AbandonedMutexException)
				{
					flag = true;
				}
			}
			catch (Exception)
			{
			}
			Directory.CreateDirectory(dataDir);
			if (flag && maxBytes > 0)
			{
				FileInfo fileInfo = new FileInfo(fullPath);
				if (fileInfo != null && fileInfo.Exists && fileInfo.Length >= maxBytes)
				{
					try
					{
						File.Move(fullPath, fullPath + ".1", overwrite: true);
					}
					catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException) ? true : false)
					{
					}
				}
			}
			int num = 1;
			while (true)
			{
				try
				{
					using FileStream fileStream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
					fileStream.Write(Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:u} {line}{Environment.NewLine}"));
					break;
				}
				catch (IOException) when (num < 5)
				{
					Thread.Sleep(20);
				}
				num++;
			}
		}
		catch (Exception)
		{
		}
		finally
		{
			if (flag)
			{
				try
				{
					mutex.ReleaseMutex();
				}
				catch (Exception)
				{
				}
			}
			mutex?.Dispose();
		}
	}

	internal static string LogMutexName(string path)
	{
		return "Global\\SCSKiller.Log." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Substring(0, 32);
	}

	private string? ProxySha(string name = "d3d12.dll")
	{
		string text = ProxySource(name);
		object obj;
		if (text != null && File.Exists(text))
		{
			if (!(name == "amd_ags_x64.dll"))
			{
				obj = _proxySha;
				if (obj == null)
				{
					return _proxySha = Sha256(text);
				}
			}
			else
			{
				obj = _agsSha;
				if (obj == null)
				{
					return _agsSha = Sha256(text);
				}
			}
		}
		else
		{
			obj = null;
		}
		return (string?)obj;
	}

	private string? ProxySource(string name)
	{
		if (!(name == "amd_ags_x64.dll"))
		{
			return _proxyDll;
		}
		return AgsDll;
	}

	private static Version FileVersion(string path)
	{
		FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
		return new Version(versionInfo.FileMajorPart, versionInfo.FileMinorPart, versionInfo.FileBuildPart, versionInfo.FilePrivatePart);
	}

	private static IReadOnlySet<string> RunningProcessNames()
	{
		return (from p in ProcessTree.Snapshot()
			select Path.GetFileNameWithoutExtension(p.Exe)).ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	internal static bool RecorderInstallAllowed(string dataDir, string? moved, string? testFlag)
	{
		if (moved != null && Path.GetFullPath(dataDir).Equals(moved, StringComparison.OrdinalIgnoreCase))
		{
			return testFlag == "1";
		}
		return true;
	}

	private void Install(Game g, GameRecord rec, string name)
	{
		if (!RecorderInstallAllowed(Store.DataDir, AppStore.Moved, Environment.GetEnvironmentVariable("SCSKILLER_TEST_RECORDER")))
		{
			throw new InvalidOperationException("the data folder is moved (SCSKILLER_DATA_DIR), which SCSKiller's uninstall doesn't know: set SCSKILLER_TEST_RECORDER=1 to install a recorder anyway, and take it out yourself");
		}
		RefuseAntiCheat(g, rec);
		if (!GameFolderWrite(g))
		{
			throw new InvalidOperationException(g.Name + " is running");
		}
		string src = ProxySource(name) ?? throw new FileNotFoundException("the recorder for " + name + " was not found next to the app");
		string directoryName = Path.GetDirectoryName(g.ExePath);
		string text = Path.Combine(directoryName, name);
		string text2 = Path.Combine(directoryName, "scskiller.ini");
		var (iniState, judged) = JudgeIni(directoryName, Store, rec.RecorderChained?.Name, g.InstallDir);
		bool flag;
		switch (iniState)
		{
		case IniState.Unknown:
			throw new IOException(text2 + " can't be read: not installing until it can");
		case IniState.Absent:
		case IniState.Ours:
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		bool flag2 = flag;
		bool flag3 = File.Exists(text) && !IsOurProxy(text);
		if (name == "amd_ags_x64.dll" && !flag3 && !File.Exists(Path.Combine(directoryName, "amd_ags_real.dll")))
		{
			throw new InvalidOperationException(text + ": the game's own AGS isn't there to put the recorder in front of. Not installing.");
		}
		if (!flag2 && FramesOnly(name, Vendor.Caps))
		{
			throw new InvalidOperationException(text2 + " is not SCSKiller's: the frame times recorder needs its own. Not installing.");
		}
		GameRecord gameRecord = rec;
		string exePath = g.ExePath;
		string installDir = g.InstallDir;
		rec.RecorderExe = exePath;
		gameRecord.RecorderInstallDir = installDir;
		StampLedger(directoryName);
		string value = Sha256(src);
		bool flag4 = rec.RecorderFiles.TryGetValue(name, out string value2);
		if (flag3)
		{
			if ((name == "amd_ags_x64.dll") ? (!GameFiles.WrappableAgs.Contains(Sha256(text))) : (!rec.RecordAlongsideMod))
			{
				throw new InvalidOperationException(text + " already exists and is not SCSKiller's (another mod or wrapper). Not replacing it.");
			}
			if (name != "amd_ags_x64.dll")
			{
				string text3 = ChainBlocker(text);
				if (text3 != null)
				{
					throw new InvalidOperationException(text + ": " + text3 + ". Not renaming it.");
				}
			}
			if (!flag2)
			{
				throw new InvalidOperationException(text2 + " is not SCSKiller's: the chain to " + text + " needs its own. Not renaming it.");
			}
			string text4 = Path.Combine(directoryName, ChainOf(name));
			if (File.Exists(text4))
			{
				throw new InvalidOperationException(text4 + " already exists. Not renaming " + text + " over it.");
			}
			rec.RecorderChained = new ChainedDll(ChainOf(name), Sha256(text));
			rec.RecorderFiles[name] = value;
			InstallStep?.Invoke("chain");
			Store.SaveGame(g.Id, rec);
			if (!GameFolderWrite(g))
			{
				rec.RecorderChained = null;
				if (flag4)
				{
					rec.RecorderFiles[name] = value2;
				}
				else
				{
					rec.RecorderFiles.Remove(name);
				}
				Store.SaveGame(g.Id, rec);
				throw new InvalidOperationException(g.Name + " is running");
			}
			File.Delete(text2);
			File.Move(text, text4);
		}
		else
		{
			rec.RecorderFiles[name] = value;
			Store.SaveGame(g.Id, rec);
		}
		InstallStep?.Invoke("copy");
		if (!GameFolderWrite(g))
		{
			if (flag3)
			{
				gameRecord = rec;
				ConcurrentDictionary<string, bool> rollbacks = _rollbacks;
				installDir = g.Id;
				gameRecord.RecorderRollback = true;
				rollbacks[installDir] = true;
				Store.SaveGame(g.Id, rec);
			}
			throw new InvalidOperationException(g.Name + " is running");
		}
		try
		{
			Place(text, (string temp) =>
			{
				File.Copy(src, temp, overwrite: true);
			}, replace: true);
		}
		catch when (flag3)
		{
			File.Move(Path.Combine(directoryName, ChainOf(name)), text);
			rec.RecorderChained = null;
			Store.SaveGame(g.Id, rec);
			throw;
		}
		AntiCheat antiCheat = AntiCheat.None;
		Exception ex = null;
		try
		{
			rec.RecorderFiles[name] = Sha256(text);
			InstallStep?.Invoke("copied");
			antiCheat = GameFiles.DetectAntiCheat(g, quick: true);
			if (antiCheat == AntiCheat.None && !GameFolderWrite(g))
			{
				throw new InvalidOperationException(g.Name + " started while the recorder was installed");
			}
			if ((antiCheat == AntiCheat.None) & flag2)
			{
				Store.SaveGame(g.Id, rec);
				if (!WriteIni(directoryName, IniText(g, rec), judged, rec.RecorderChained?.Name, g.InstallDir, () => GameFolderWrite(g), (string step) =>
				{
					if (step == "saved")
					{
						InstallStep?.Invoke("ini");
					}
				}, install: true) && !GameFolderWrite(g))
				{
					throw new InvalidOperationException(g.Name + " started while the recorder was installed");
				}
			}
		}
		catch (Exception ex2)
		{
			ex = ex2;
		}
		if (antiCheat == AntiCheat.None)
		{
			antiCheat = GameFiles.DetectAntiCheat(g);
		}
		if (antiCheat == AntiCheat.None && ex == null)
		{
			try
			{
				InstallStep?.Invoke("keys");
				WriteKeys(g, rec);
				Store.SaveGame(g.Id, rec);
				antiCheat = GameFiles.DetectAntiCheat(g);
				if (antiCheat == AntiCheat.None)
				{
					lock (_scanLock)
					{
						_scan?.Remove(g.Id);
						return;
					}
				}
			}
			catch (Exception ex3)
			{
				ex = ex3;
			}
		}
		if (antiCheat != AntiCheat.None)
		{
			AntiCheatFound(g, antiCheat, null, rec);
			throw AntiCheatRefusal(g, antiCheat);
		}
		TakeOut(g, rec, "the recorder install failed (" + ex.Message + ")");
		ExceptionDispatchInfo.Throw(ex);
	}

	private bool GameFolderWrite(Game g)
	{
		return !GameRunning(g);
	}

	private bool GameRunning(Game g)
	{
		if (!IsPlaying(g.Id) && !_offline.ContainsKey(g.Id) && !Running(g.ExePath, g.InstallDir, ProcessNames(), OnlyElsewhere))
		{
			if (_moving.TryGetValue(g.Id, out Game value) && value != g)
			{
				return Running(value.ExePath, value.InstallDir, ProcessNames(), OnlyElsewhere);
			}
			return false;
		}
		return true;
	}

	private bool OnlyElsewhere(string name, string?[] roots)
	{
		List<(int Pid, int Parent, string Exe)> all = Processes(arg: true);
		List<(int, int, string)> list = all.Where(((int Pid, int Parent, string Exe) p) => Path.GetFileNameWithoutExtension(p.Exe).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
		if (list != null && list.Count > 0)
		{
			return list.All(((int Pid, int Parent, string Exe) p) => Outside(p.Pid, roots) && !OurCopy(p, all));
		}
		return false;
	}

	private bool Outside(int pid, string?[] roots)
	{
		string text = ProcessPath(pid);
		if (text == null)
		{
			return false;
		}
		foreach (string item in roots.OfType<string>().Where(Directory.Exists))
		{
			if (!GameFiles.Inside(item, text))
			{
				string text2 = GameFiles.FinalPath(item);
				if (text2 != null && !GameFiles.Inside(text2, text))
				{
					continue;
				}
			}
			return false;
		}
		return true;
	}

	private static Game RecordedAt(Game g, GameRecord rec)
	{
		string text = rec.RecorderMoveFrom ?? rec.RecorderExe;
		if (text == null)
		{
			return g;
		}
		return g with
		{
			ExePath = text,
			InstallDir = (rec.RecorderInstallDir ?? g.InstallDir)
		};
	}

	private bool RunsFromItsFolder(Game g)
	{
		string?[] roots = new string[2]
		{
			g.InstallDir,
			Path.GetDirectoryName(g.ExePath)
		};
		string[] array = Collections.Exes(g);
		HashSet<string> names = ((array != null) ? array.Select((string n) => Path.GetFileNameWithoutExtension(n)).ToHashSet(StringComparer.OrdinalIgnoreCase) : ExeNamesIn(roots));
		if (names == null)
		{
			return true;
		}
		if (names.Count == 0)
		{
			return false;
		}
		List<(int Pid, int Parent, string Exe)> all = Processes(arg: false);
		string exe = Path.GetFileName(g.ExePath);
		return all.Any(((int Pid, int Parent, string Exe) p) => names.Contains(Path.GetFileNameWithoutExtension(p.Exe)) && !OurCopy(p, all) && (p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase) || !Outside(p.Pid, roots)));
	}

	private bool OurCopy((int Pid, int Parent, string Exe) p, List<(int Pid, int Parent, string Exe)> all)
	{
		string text = ProcessPath(p.Pid);
		if (text == null)
		{
			return all.Any(((int Pid, int Parent, string Exe) w) => w.Pid == p.Parent && IsWarm(w) && OwnWarm(w.Pid));
		}
		string text2 = Path.Combine(Store.DataDir, "games");
		if (!Staged(text2, text))
		{
			string text3 = GameFiles.FinalPath(text2);
			if (text3 == null || !Staged(text3, text))
			{
				int? num = StagedBy(text);
				if (num.HasValue)
				{
					int pid = num.GetValueOrDefault();
					if (all.Any(((int Pid, int Parent, string Exe) w) => w.Pid == pid && IsWarm(w)))
					{
						string text4 = ProcessPath(pid);
						if (text4 != null)
						{
							string[] roots = Games.SelectMany((GameState s) => new string[2]
							{
								s.Game.InstallDir,
								Path.GetDirectoryName(s.Game.ExePath)
							}).ToArray();
							if (Outside(p.Pid, roots) && Outside(pid, roots))
							{
								return SameBytes(text, text4);
							}
							return false;
						}
					}
				}
				return false;
			}
		}
		return true;
		static bool IsWarm((int Pid, int Parent, string Exe) w)
		{
			return w.Exe.Equals("scskiller_warm.exe", StringComparison.OrdinalIgnoreCase);
		}
		static bool Staged(string games, string path)
		{
			if (GameFiles.Inside(games, path))
			{
				string[] array = Path.GetRelativePath(games, path).Split(Path.DirectorySeparatorChar);
				if (array != null && array.Length >= 4)
				{
					string name = array[1];
					string text5 = array[2];
					if (IsWorkDir(name))
					{
						return text5.StartsWith("stage-", StringComparison.OrdinalIgnoreCase);
					}
				}
			}
			return false;
		}
	}

	private static int? StagedBy(string path)
	{
		string text = Path.GetDirectoryName(path);
		while (text != null)
		{
			string directoryName = Path.GetDirectoryName(text);
			if (directoryName == null)
			{
				break;
			}
			if (IsWorkDir(Path.GetFileName(directoryName)))
			{
				string[] array = Path.GetFileName(text).Split('-');
				if (array != null && array.Length == 3)
				{
					string text2 = array[0];
					string s = array[1];
					string text3 = array[2];
					if (text2.Equals("stage", StringComparison.OrdinalIgnoreCase) && int.TryParse(s, NumberStyles.None, null, out var result) && result > 0 && text3.Length > 0 && text3.All(char.IsAsciiDigit))
					{
						return result;
					}
				}
			}
			text = directoryName;
		}
		return null;
	}

	private static bool SameBytes(string a, string b)
	{
		try
		{
			FileInfo fileInfo = new FileInfo(a);
			FileInfo fileInfo2 = new FileInfo(b);
			FileInfo fileInfo3 = fileInfo;
			return fileInfo3.Length == fileInfo2.Length && fileInfo3.Length <= 67108864 && ((ReadOnlySpan<byte>)File.ReadAllBytes(a).AsSpan()).SequenceEqual((ReadOnlySpan<byte>)File.ReadAllBytes(b));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return false;
		}
	}

	// Exo Shaders: PollGames asks this for every library game every few seconds;
	// a folder's exe names only change when its own entries change, which moves
	// the folder's last-write time, so one stat replaces a full listing.
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Written, string[] Names)> ExeNameCache = new(StringComparer.OrdinalIgnoreCase);

	private static string[] ExeNamesOf(string dir)
	{
		DateTime written = Directory.GetLastWriteTimeUtc(dir);
		if (ExeNameCache.TryGetValue(dir, out var cached) && cached.Written == written)
		{
			return cached.Names;
		}
		string[] names = Directory.EnumerateFiles(dir, "*.exe").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToArray();
		ExeNameCache[dir] = (written, names);
		return names;
	}

	private static HashSet<string>? ExeNamesIn(string?[] dirs)
	{
		try
		{
			return dirs.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists)
				.SelectMany(ExeNamesOf)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	private bool TakeOut(Game g, GameRecord rec, string cause)
	{
		NewRemoval(g);
		if (rec.RecorderMoveFrom == null)
		{
			string recorderExe = rec.RecorderExe;
			if (recorderExe != null && !Path.GetDirectoryName(recorderExe).Equals(Path.GetDirectoryName(g.ExePath), StringComparison.OrdinalIgnoreCase))
			{
				GameRecord gameRecord = rec;
				GameRecord gameRecord2 = rec;
				string recorderMoveFrom = recorderExe;
				string exePath = g.ExePath;
				gameRecord.RecorderMoveFrom = recorderMoveFrom;
				gameRecord2.RecorderMoveTo = exePath;
				try
				{
					Store.SaveGame(g.Id, rec);
				}
				catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
					RecorderLog(g.Name + ": couldn't save the recorder's record: " + ex.Message);
				}
			}
		}
		Game at = RecordedAt(g, rec);
		string directoryName = Path.GetDirectoryName(g.ExePath);
		List<(string, string, bool)> list = new (string, string, bool)[2]
		{
			(Path.GetDirectoryName(at.ExePath), rec.RecorderInstallDir ?? at.InstallDir, true),
			(directoryName, g.InstallDir, true)
		}.Concat(from e in new string[2] { rec.RecorderExe, rec.RecorderMoveTo }.OfType<string>()
			select (Dir: Path.GetDirectoryName(e), Install: rec.RecorderInstallDir ?? at.InstallDir, Keys: false)).DistinctBy(((string Dir, string Install, bool Keys) tuple) => tuple.Dir, StringComparer.OrdinalIgnoreCase).ToList();
		List<string> list2 = list.Select(((string Dir, string Install, bool Keys) tuple) => tuple.Dir).ToList();
		bool flag;
		if (!GameFolderWrite(g) || !GameFolderWrite(at))
		{
			foreach (var item in list)
			{
				StripIni(item.Item1, Store, rec.RecorderChained?.Name, item.Item2, g.Name, RecorderLog);
			}
			GameRecord gameRecord = rec;
			ConcurrentDictionary<string, bool> rollbacks = _rollbacks;
			string exePath = g.Id;
			gameRecord.RecorderRollback = true;
			rollbacks[exePath] = true;
			flag = false;
			RecorderLog(g.Name + ": " + cause + ": the recorder is removed when the game exits");
		}
		else
		{
			try
			{
				bool flag2 = false;
				rec.RecorderRollback = false;
				foreach (var f in list)
				{
					try
					{
						RemoveRecorder(f.Item1, rec, Store, g.Name, RecorderLog, f.Item2);
						if (!Directory.Exists(f.Item1))
						{
							continue;
						}
						string[] array = (f.Item3 ? Recordings.AppFiles : Array.Empty<string>());
						foreach (string path in array)
						{
							try
							{
								File.Delete(Path.Combine(f.Item1, path));
							}
							catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
							{
							}
						}
						if (f.Item3 && !RecorderDataFiles.Concat(Recordings.AppFiles).Any((string x) => File.Exists(Path.Combine(f.Item1, x))) && !IniPending(Store, f.Item1) && !rec.RecorderRollback)
						{
							File.Delete(Path.Combine(f.Item1, "scskiller.ledger"));
						}
					}
					catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException) ? true : false)
					{
						flag2 = true;
						RecorderLog($"{g.Name}: {cause}; removing the recorder from {f.Item1} failed ({ex3.Message}): tried again");
					}
				}
				flag = !flag2 && !rec.RecorderRollback && rec.RecorderFiles.Count == 0 && !list2.Any((string d) => IniPending(Store, d) || (!Directory.Exists(d) && !Gone(d)));
				GameRecord gameRecord = rec;
				GameRecord gameRecord3 = rec;
				bool value = !flag;
				gameRecord.KeysIndexHash = null;
				gameRecord3.RecorderRollback = value;
				if (flag)
				{
					_rollbacks.TryRemove(g.Id, out value);
				}
				else
				{
					_rollbacks[g.Id] = true;
				}
				RecorderLog(flag ? $"{g.Name}: {cause}: recorder removed ({string.Join(", ", list2)})" : $"{g.Name}: {cause}: recorder removed ({string.Join(", ", list2)}) but for files that can't be taken out yet: tried again later");
			}
			catch (Exception ex4) when ((ex4 is IOException || ex4 is UnauthorizedAccessException) ? true : false)
			{
				GameRecord gameRecord = rec;
				ConcurrentDictionary<string, bool> rollbacks2 = _rollbacks;
				string exePath = g.Id;
				gameRecord.RecorderRollback = true;
				rollbacks2[exePath] = true;
				flag = false;
				RecorderLog($"{g.Name}: {cause}; removing the recorder failed ({ex4.Message}): removed when the game exits");
			}
		}
		try
		{
			Store.SaveGame(g.Id, rec);
		}
		catch (Exception ex5) when ((ex5 is IOException || ex5 is UnauthorizedAccessException) ? true : false)
		{
			RecorderLog(g.Name + ": couldn't save the recorder's record: " + ex5.Message);
		}
		return flag;
	}

	private void AntiCheatFound(Game g, AntiCheat found, Evaluation? fresh = null, GameRecord? installing = null, bool unfinished = false)
	{
		NewRemoval(g);
		string found2 = ((unfinished && fresh == null) ? FolderStamp(g) : null);
		bool flag;
		lock (_scanLock)
		{
			if (_scan == null)
			{
				_scan = Store.LoadScan();
			}
			int num;
			if (!(fresh != null))
			{
				Evaluation valueOrDefault = _scan.GetValueOrDefault(g.Id);
				num = (((object)valueOrDefault != null && valueOrDefault.AntiCheat != found) ? 1 : 0);
			}
			else
			{
				num = 1;
			}
			flag = (byte)num != 0;
			if (fresh != null)
			{
				_scan[g.Id] = fresh;
			}
			else if (flag)
			{
				_scan[g.Id] = _scan[g.Id]with
				{
					AntiCheat = found,
					Clean = null,
					Found = found2,
					FoundAt = null
				};
			}
			_scanStarted[g.Id] = ++_evaluations;
			_verdicts[g.Id] = found;
		}
		lock (_lock)
		{
			int num2 = _games.FindIndex((GameState x) => x.Game.Id == g.Id);
			if (num2 >= 0 && _games[num2].AntiCheat != found)
			{
				_games[num2] = _games[num2]with
				{
					AntiCheat = found
				};
			}
		}
		TakeOutNow(g, $"{found} found", installing);
		EndPair(g, installing);
		if (!flag)
		{
			return;
		}
		lock (_scanLock)
		{
			try
			{
				Store.SaveScan(_scan);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				RecorderLog($"{g.Name}: couldn't save that it uses {found}: {ex.Message}");
			}
		}
	}

	private void TakeOutNow(Game g, string why, GameRecord? installing = null, bool anyCopys = true)
	{
		NewRemoval(g);
		if (OfflineLive(g.Id))
		{
			return;
		}
		string exeDir = Path.GetDirectoryName(g.ExePath);
		GameRecord gameRecord = null;
		try
		{
			gameRecord = Store.LoadGame(g.Id);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
		{
		}
		foreach (string item in from d in new string[4]
			{
				g.ExePath,
				gameRecord?.RecorderExe,
				gameRecord?.RecorderMoveFrom,
				gameRecord?.RecorderMoveTo
			}.OfType<string>().Select(Path.GetDirectoryName).OfType<string>()
				.Distinct(StringComparer.OrdinalIgnoreCase)
			where File.Exists(Path.Combine(d, "scskiller.ini"))
			select d)
		{
			Revoke(item, on: true, RecorderLog);
		}
		lock (_recorderLock)
		{
			GameRecord gameRecord2 = installing ?? Store.LoadGame(g.Id);
			if (!anyCopys && (Foreign(g) || Foreign(RecordedAt(g, gameRecord2))))
			{
				return;
			}
			try
			{
				if (!ProxyOnDisk(g) && !ProxyOnDisk(RecordedAt(g, gameRecord2)) && gameRecord2.RecorderFiles.Count == 0 && gameRecord2.RecorderChained == null)
				{
					StripIni(exeDir, Store, null, g.InstallDir, g.Name, RecorderLog);
					{
						foreach (string item2 in (from e in new string[3] { gameRecord2.RecorderExe, gameRecord2.RecorderMoveFrom, gameRecord2.RecorderMoveTo }.OfType<string>()
							select Path.GetDirectoryName(e) into d
							where !d.Equals(exeDir, StringComparison.OrdinalIgnoreCase)
							select d).Distinct(StringComparer.OrdinalIgnoreCase))
						{
							StripIni(item2, Store, null, gameRecord2.RecorderInstallDir ?? g.InstallDir, g.Name, RecorderLog);
						}
						return;
					}
				}
				TakeOut(g, gameRecord2, why);
			}
			catch (Exception ex2)
			{
				GameRecord gameRecord3 = gameRecord2;
				ConcurrentDictionary<string, bool> rollbacks = _rollbacks;
				string id = g.Id;
				gameRecord3.RecorderRollback = true;
				rollbacks[id] = true;
				RecorderLog($"{g.Name}: {why}; removing the recorder failed ({ex2.Message}): it's tried again at the next reconcile");
				try
				{
					Store.SaveGame(g.Id, gameRecord2);
				}
				catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException) ? true : false)
				{
				}
			}
		}
	}

	private void RefuseAntiCheat(Game g, GameRecord rec)
	{
		if (VacCheck(g))
		{
			throw new InvalidOperationException(g.Name + " uses VAC: a DLL in its folder could get the account banned. Not installing.");
		}
		AntiCheat antiCheat = GameFiles.DetectAntiCheat(g);
		if (antiCheat == AntiCheat.None)
		{
			return;
		}
		AntiCheatFound(g, antiCheat, null, rec);
		throw AntiCheatRefusal(g, antiCheat);
	}

	private static InvalidOperationException AntiCheatRefusal(Game g, AntiCheat antiCheat)
	{
		return new InvalidOperationException($"{g.Name} uses {antiCheat}: a DLL in its folder could get the account banned. Not installing.");
	}

	internal bool AgsEligible(Game g, GameRecord rec, bool risk, bool ignoreLatch = false)
	{
		if (Vendor.Vendor != GpuVendor.Amd)
		{
			return false;
		}
		bool flag;
		try
		{
			flag = VacCheck(g);
		}
		catch
		{
			return false;
		}
		if (!AgsFolderListable(g))
		{
			return false;
		}
		if (!risk)
		{
			if ((ignoreLatch || !rec.AgsIneligible) && !flag && !g.Id.StartsWith("riot:", StringComparison.Ordinal) && !GameFiles.RiotGames(g.InstallDir))
			{
				return !GameFiles.RiotGames(g.ExePath);
			}
			return false;
		}
		return true;
	}

	private static bool AgsFolderListable(Game g)
	{
		try
		{
			long num = 0L;
			foreach (string item in Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(g.ExePath)))
			{
				_ = item;
				if (++num > 99998)
				{
					return false;
				}
			}
			return true;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is ArgumentException) ? true : false)
		{
			return false;
		}
	}

	internal string SignedLines(Game g, GameRecord rec, bool session = false, bool riskSession = false)
	{
		return (AgsEligible(g, rec, riskSession, session) ? "vendor=amd\r\n" : "") + ((session || rec.OfflineSession != null) ? "" : RunIni(g, rec.BeforeAfter));
	}

	private string RunIni(Game g, BeforeAfterRecord? ba)
	{
		GameState gameState = Games.FirstOrDefault((GameState s) => s.Game.Id == g.Id);
		if ((object)gameState == null || gameState.AntiCheat != AntiCheat.None || gameState.Vac || _verdicts.ContainsKey(g.Id))
		{
			return "";
		}
		return OverlayIni(ba);
	}

	internal static string OverlayIni(BeforeAfterRecord? ba)
	{
		string text;
		if (ba == null || !ba.Overlay)
		{
			text = null;
		}
		else
		{
			string text2;
			switch (ba.Stage)
			{
			case BeforeAfterStage.BeforeArmed:
			case BeforeAfterStage.BeforePlaying:
				text2 = "before";
				break;
			case BeforeAfterStage.AfterArmed:
			case BeforeAfterStage.AfterPlaying:
				text2 = "after";
				break;
			default:
				text2 = null;
				break;
			}
			text = text2;
		}
		string text3 = text;
		if (text3 != null)
		{
			return $"overlay=1\r\nab_play_s={ba.Minutes * 60}\r\nab_tag={text3}\r\n";
		}
		return "";
	}

	private string? Resigned(Game g, GameRecord rec)
	{
		string text = AttestedStamp(g);
		if (text == null)
		{
			return null;
		}
		return Signed(g.ExePath, RootValue(g), text, SignedLines(g, rec));
	}

	private string IniText(Game g, GameRecord rec, string stamp = "")
	{
		return IniOf(DbCap(g)?.ToString(), ChainNext(Path.GetDirectoryName(g.ExePath), rec), FramesOnly(File.Exists(Path.Combine(Path.GetDirectoryName(g.ExePath), "d3d11.dll")) ? "d3d11.dll" : "d3d12.dll", Vendor.Caps), RootValue(g), stamp);
	}

	private static string IniOf(string? cap, string? next, bool framesOnly, string? root, string stamp)
	{
		return "[scskiller]\r\n; written by SCSKiller: record the pipelines this game creates. Removed by 'uninstall recorder'.\r\nmode=record\r\n" + ((cap == null) ? "" : (IniComments[0] + "\r\nmax_db_bytes=" + cap + "\r\n")) + ((next == null) ? "" : (IniComments[1] + "\r\nnext=" + next + "\r\n")) + (framesOnly ? (IniComments[4] + "\r\nframes_only=1\r\n") : "") + ((root == null) ? "" : (IniComments[2] + "\r\nroot=" + root + "\r\n")) + ((stamp == "") ? "" : (IniComments[3] + "\r\n" + stamp));
	}

	private static string? ChainNext(string dir, GameRecord rec)
	{
		ChainedDll recorderChained = rec.RecorderChained;
		if ((object)recorderChained == null || recorderChained.Name == "amd_ags_real.dll" || !File.Exists(Path.Combine(dir, recorderChained.Name)))
		{
			return null;
		}
		return recorderChained.Name;
	}

	internal static bool OurIniBytes(byte[] bytes, string? chain, string? root)
	{
		if (bytes.Any((byte b) => b >= 128))
		{
			return false;
		}
		string text = Encoding.ASCII.GetString(bytes);
		Dictionary<string, string> values = new Dictionary<string, string>();
		string[] array = text.Split("\r\n");
		foreach (string text2 in array)
		{
			if (!text2.StartsWith(';'))
			{
				int num2 = text2.IndexOf('=');
				if (num2 > 0 && !values.TryAdd(text2.Substring(0, num2), text2.Substring(num2 + 1)))
				{
					return false;
				}
			}
		}
		string? text3 = V("max_db_bytes");
		string? text4 = V("next");
		string? text5 = V("frames_only");
		string? text6 = V("root");
		string text7 = V("sig");
		string text8 = text6;
		string text9 = text5;
		string text10 = text4;
		string text11 = text3;
		if ((text11 != null && !Long(text11)) || (text10 != null && text10 != chain) || (text9 != null && text9 != "1") || (text8 != null && text8 != root))
		{
			return false;
		}
		string? text12 = V("vendor");
		string? text13 = V("overlay");
		string? text14 = V("ab_play_s");
		string text15 = V("ab_tag");
		string text16 = text14;
		string text17 = text13;
		string text18 = text12;
		string stamp = "";
		if (text7 != null)
		{
			string? text19 = V("exe_size");
			string? text20 = V("exe_time");
			string? text21 = V("pid");
			string? text22 = V("pid_time");
			string text23 = V("risk");
			string text24 = text22;
			string text25 = text21;
			string text26 = text20;
			string text27 = text19;
			bool flag = text8 == null || text7.Length != 40 || !text7.All(char.IsAsciiHexDigitLower) || !Long(text27) || !Long(text26) || text25 == null != (text24 == null) || (text25 != null && (!Int(text25) || !Long(text24))) || (text23 != null && (text25 == null || text23 != "1")) || (text18 != null && text18 != "amd") || text17 == null != (text16 == null) || text17 == null != (text15 == null);
			if (!flag)
			{
				bool flag2 = text17 != null;
				if (flag2)
				{
					bool flag3 = text17 != "1" || !Int(text16);
					if (!flag3)
					{
						bool flag4 = ((text15 == "before" || text15 == "after") ? true : false);
						flag3 = !flag4;
					}
					flag2 = flag3;
				}
				flag = flag2;
			}
			if (flag)
			{
				return false;
			}
			stamp = $"exe_size={text27}\r\nexe_time={text26}\r\n" + ((text25 == null) ? "" : BoundTo(int.Parse(text25), long.Parse(text24))) + ((text23 == null) ? "" : "risk=1\r\n") + ((text18 == null) ? "" : "vendor=amd\r\n") + ((text17 == null) ? "" : $"overlay=1\r\nab_play_s={text16}\r\nab_tag={text15}\r\n") + "sig=" + text7 + "\r\n";
		}
		return text == IniOf(text11, text10, text9 != null, text8, stamp);
		static bool Int(string? v)
		{
			if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
			{
				return result.ToString(CultureInfo.InvariantCulture) == v;
			}
			return false;
		}
		static bool Long(string? v)
		{
			if (long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
			{
				return result.ToString(CultureInfo.InvariantCulture) == v;
			}
			return false;
		}
		string? V(string key)
		{
			return values.GetValueOrDefault(key);
		}
	}

	private static string FullDir(string dir)
	{
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
	}

	internal static IniOwner? GetIni(AppStore store, string dir)
	{
		dir = FullDir(dir);
		if (!Directory.Exists(dir))
		{
			return null;
		}
		return store.LoadInis().GetValueOrDefault(dir);
	}

	internal static void SetIni(AppStore store, string dir, Func<IniOwner?, IniOwner?> change, TimeSpan? wait = null)
	{
		dir = FullDir(dir);
		if (!Directory.Exists(dir))
		{
			return;
		}
		store.UpdateInis((Dictionary<string, IniOwner> all) =>
		{
			IniOwner iniOwner = change(all.GetValueOrDefault(dir));
			if (((object)iniOwner != null && (iniOwner.Tracked != null || iniOwner.Users || iniOwner.Remove)) ? true : false)
			{
				all[dir] = iniOwner with
				{
					Dir = dir
				};
			}
			else
			{
				all.Remove(dir);
			}
		}, wait);
	}

	internal static bool IniPending(AppStore store, string dir)
	{
		return GetIni(store, dir)?.Remove ?? false;
	}

	private static void MarkUsers(AppStore store, string dir, TimeSpan? wait = null)
	{
		SetIni(store, dir, (IniOwner? e) =>
		{
			IniOwner? obj = e ?? new IniOwner(dir);
			return obj with
			{
				Tracked = null,
				Users = true,
				Remove = false
			};
		}, wait);
	}

	private static (IniState State, byte[]? Bytes) JudgeIni(string dir, AppStore store, string? chain, string? installDir, TimeSpan? wait = null)
	{
		string path = Path.Combine(dir, "scskiller.ini");
		IniOwner ini = GetIni(store, dir);
		byte[] array;
		try
		{
			if (!File.Exists(path))
			{
				if (Directory.Exists(dir) ? (!Directory.EnumerateFiles(dir, "scskiller.ini").Any()) : Gone(dir))
				{
					if (((object)ini != null && (ini.Tracked != null || ini.Remove)) ? true : false)
					{
						SetIni(store, dir, (IniOwner? e) => e with
						{
							Tracked = null,
							Remove = false
						}, wait);
					}
					return (State: IniState.Absent, Bytes: null);
				}
				if (!Directory.Exists(dir))
				{
					return (State: IniState.Unknown, Bytes: null);
				}
			}
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (fileStream.Length > 65536)
			{
				MarkUsers(store, dir, wait);
				return (State: IniState.NotOurs, Bytes: null);
			}
			array = new byte[fileStream.Length];
			fileStream.ReadExactly(array);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return (State: IniState.Unknown, Bytes: null);
		}
		string text = ini?.Tracked;
		if (text != null && text.Length > 0)
		{
			if (text.Split('|').Contains(Convert.ToHexString(SHA256.HashData(array))))
			{
				return (State: IniState.Ours, Bytes: array);
			}
			MarkUsers(store, dir, wait);
			return (State: IniState.NotOurs, Bytes: array);
		}
		string text2 = installDir ?? ini?.Install;
		string text3 = ((text2 != null) ? RootValue(dir, text2) : null);
		if (((object)ini == null || !ini.Users) && OurIniBytes(array, chain ?? ini?.Chain, text3))
		{
			return (State: IniState.Ours, Bytes: array);
		}
		if (((object)ini == null || !ini.Users) && text3 == null && Encoding.ASCII.GetString(array).Contains("\r\nroot="))
		{
			return (State: IniState.Unknown, Bytes: array);
		}
		if ((object)ini == null || !ini.Users)
		{
			MarkUsers(store, dir, wait);
		}
		return (State: IniState.NotOurs, Bytes: array);
	}

	private static bool IniUnchanged(string ini, byte[]? judged)
	{
		return IniNow(ini, judged) == true;
	}

	private static bool? IniNow(string ini, byte[]? judged)
	{
		try
		{
			if (!File.Exists(ini))
			{
				return (Directory.EnumerateFiles(Path.GetDirectoryName(ini), "scskiller.ini").Any() || judged != null) ? ((bool?)null) : new bool?(true);
			}
			using FileStream fileStream = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			byte[] array = new byte[Math.Min(fileStream.Length, 65537L)];
			fileStream.ReadExactly(array);
			return judged != null && ((ReadOnlySpan<byte>)array.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)judged);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	private static string IniTracked(string text, byte[]? judged)
	{
		return Hex(text) + ((judged == null) ? "" : ("|" + Convert.ToHexString(SHA256.HashData(judged))));
	}

	private bool WriteIni(string dir, string text, byte[]? judged, string? chain, string installDir, Func<bool> still, Action<string>? step, bool install = false)
	{
		dir = FullDir(dir);
		if (!Directory.Exists(dir))
		{
			return false;
		}
		string text2 = Path.Combine(dir, "scskiller.ini");
		string text3 = dir;
		string text4 = IniTracked(text, judged);
		string intent = text4;
		string key = text3;
		string ini = text2;
		IniOwner was = null;
		bool go = false;
		Store.UpdateInis((Dictionary<string, IniOwner> all) =>
		{
			was = all.GetValueOrDefault(key);
			if (install || (((object)was == null || !was.Remove) && OurProxyIn(dir)))
			{
				if (judged != null)
				{
					string text5 = was?.Tracked;
					if (text5 != null && text5.Length > 0 && !text5.Split('|').Contains(Convert.ToHexString(SHA256.HashData(judged))))
					{
						return;
					}
				}
				string key2 = key;
				IniOwner iniOwner = was ?? new IniOwner(dir);
				all[key2] = iniOwner with
				{
					Dir = dir,
					Tracked = intent,
					Users = false,
					Remove = false,
					Chain = chain,
					Install = installDir
				};
				go = true;
			}
		});
		if (!go)
		{
			return false;
		}
		step?.Invoke("saved");
		bool written = false;
		Store.UpdateInis((Dictionary<string, IniOwner> all) =>
		{
			IniOwner valueOrDefault = all.GetValueOrDefault(key);
			if ((object)valueOrDefault != null && !valueOrDefault.Remove && !(valueOrDefault.Tracked != intent))
			{
				bool? flag = (still() ? IniNow(ini, judged) : ((bool?)null));
				if (flag.HasValue)
				{
					if (flag == true)
					{
						Place(ini, (string temp) =>
						{
							File.WriteAllText(temp, text);
						}, replace: true);
						all[key] = valueOrDefault with
						{
							Tracked = Hex(text)
						};
						written = true;
					}
					else
					{
						all[key] = valueOrDefault with
						{
							Tracked = null,
							Users = true
						};
					}
				}
				else if (was != null)
				{
					all[key] = was;
				}
				else
				{
					all.Remove(key);
				}
			}
		});
		if (written)
		{
			step?.Invoke("written");
		}
		return written;
	}

	private void UpdateRecorderIni(Game g, GameRecord rec, string? stamp = null, Func<bool>? still = null)
	{
		if (Collections.RecordedBy(g.Id) != null || !GameFolderWrite(g) || Foreign(g))
		{
			return;
		}
		try
		{
			string dir = Path.GetDirectoryName(g.ExePath);
			if (!OurProxyIn(dir))
			{
				return;
			}
			IniOwner ini = GetIni(Store, dir);
			if ((object)ini != null && ini.Remove)
			{
				return;
			}
			(IniState State, byte[]? Bytes) tuple = JudgeIni(dir, Store, rec.RecorderChained?.Name, g.InstallDir);
			IniState item = tuple.State;
			byte[] item2 = tuple.Bytes;
			IniOwner ini2 = GetIni(Store, dir);
			bool flag = (uint)item <= 1u;
			if (!flag || ((object)ini2 != null && ini2.Remove))
			{
				return;
			}
			string text = IniText(g, rec, stamp ?? Resigned(g, rec) ?? IniStamp(g.ExePath) ?? "");
			if (item2 != null && ((ReadOnlySpan<byte>)item2.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)Encoding.UTF8.GetBytes(text)))
			{
				if (!(ini2?.Tracked != Hex(text)))
				{
					return;
				}
				SetIni(Store, dir, (IniOwner? e) =>
				{
					if (((object)e == null || !e.Remove) && OurProxyIn(dir))
					{
						IniOwner? obj = e ?? new IniOwner(dir);
						return obj with
						{
							Tracked = Hex(text),
							Users = false,
							Chain = rec.RecorderChained?.Name,
							Install = g.InstallDir
						};
					}
					return e;
				});
			}
			else
			{
				WriteIni(dir, text, item2, rec.RecorderChained?.Name, g.InstallDir, () =>
				{
					Func<bool>? func = still;
					return (func == null || func()) && GameFolderWrite(g);
				}, AttestStep);
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			RecorderLog(g.Name + ": couldn't update the recorder's scskiller.ini: " + ex.Message);
		}
	}

	private bool Uninstall(Game g, GameRecord rec)
	{
		string directoryName = Path.GetDirectoryName(g.ExePath);
		if (!RemoveRecorder(directoryName, rec, Store, g.Name, RecorderLog, g.InstallDir))
		{
			Store.SaveGame(g.Id, rec);
			if (!Directory.Exists(directoryName))
			{
				return Gone(directoryName);
			}
			return true;
		}
		ImportRecording(g, rec, migrate: true);
		Store.SaveGame(g.Id, rec);
		try
		{
			if (RemoveRecorderData(directoryName, RecordingPath(g.Id), rec.RecordingInbox, () => GameRunning(g), rec.RecorderRollback || IniPending(Store, directoryName)))
			{
				if (!IniPending(Store, directoryName))
				{
					return true;
				}
				RecorderLog(g.Name + ": the recorder's scskiller.ini in " + directoryName + " isn't out yet: tried again later");
				return false;
			}
			RecorderLog(g.Name + ": the game started: the recorder's data files left in " + directoryName + " for the next reconcile");
			return false;
		}
		catch (InvalidDataException ex)
		{
			RecorderLog($"{g.Name}: left the recorder's data files in {directoryName}: {ex.Message}");
		}
		return false;
	}

	private static bool RemoveRecorder(string dir, GameRecord rec, AppStore store, string name, Action<string> log, string? installDir = null, TimeSpan? wait = null)
	{
		GameRecord gameRecord2;
		if (!Directory.Exists(dir))
		{
			if (!Gone(dir))
			{
				log(name + ": " + dir + " can't be reached: the recorder's files there are left for later");
				return false;
			}
			if (At(rec.RecorderExe))
			{
				GameRecord gameRecord = rec;
				gameRecord2 = rec;
				GameRecord gameRecord3 = rec;
				Dictionary<string, string> recorderFiles = new Dictionary<string, string>();
				gameRecord.RecorderExe = null;
				gameRecord2.RecorderFiles = recorderFiles;
				gameRecord3.RecorderChained = null;
				if (rec.RecorderMoveFrom == null)
				{
					rec.RecorderInstallDir = null;
				}
			}
			if (At(rec.RecorderMoveFrom))
			{
				gameRecord2 = rec;
				GameRecord gameRecord4 = rec;
				gameRecord2.RecorderMoveFrom = null;
				gameRecord4.RecorderMoveTo = null;
			}
			if (At(rec.RecorderMoveTo))
			{
				rec.RecorderMoveTo = null;
			}
			return false;
		}
		bool flag = Orphaned(dir, rec);
		gameRecord2 = rec;
		if ((object)gameRecord2.RecorderChained == null)
		{
			ChainedDll chainedDll = (gameRecord2.RecorderChained = (from n in new string[2] { "d3d12.scskiller-next.dll", "d3d11.scskiller-next.dll" }.Concat((!IsOurProxy(Path.Combine(dir, "amd_ags_x64.dll")) && !IsOurProxy(Path.Combine(dir, "amd_ags_x64.dll.scskiller-new"))) ? Array.Empty<string>() : new string[1] { "amd_ags_real.dll" })
				select Path.Combine(dir, n) into p
				where File.Exists(p) && !rec.RecorderOrphans.Any((string o) => o.Equals(p, StringComparison.OrdinalIgnoreCase))
				select new ChainedDll(Path.GetFileName(p), "")).FirstOrDefault());
		}
		bool flag2 = false;
		try
		{
			StripIni(dir, store, rec.RecorderChained?.Name, installDir ?? rec.RecorderInstallDir, name, log, wait);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			GameRecord gameRecord5 = rec;
			flag2 = true;
			gameRecord5.RecorderRollback = true;
			log($"{name}: couldn't take {Path.Combine(dir, "scskiller.ini")} out ({ex.Message}): tried again later");
		}
		foreach (string item in Directory.EnumerateFiles(dir, "scskiller.armed*"))
		{
			try
			{
				File.Delete(item);
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
			{
				log($"{name}: couldn't delete {item}: {ex2.Message}");
			}
		}
		string[] proxyNames = ProxyNames;
		foreach (string text in proxyNames)
		{
			string text2 = Path.Combine(dir, text);
			if (IsOurProxy(text2))
			{
				ChainedDll recorderChained = rec.RecorderChained;
				if ((object)recorderChained != null && RoleOf(recorderChained) == text)
				{
					File.Move(text2, text2 + ".scskiller-new", overwrite: true);
				}
				else
				{
					File.Delete(text2);
				}
			}
			else if (File.Exists(text2) && rec.RecorderFiles.ContainsKey(text))
			{
				log(name + ": left " + text2 + ": not SCSKiller's");
			}
			rec.RecorderFiles.Remove(text);
			string text3 = Path.Combine(dir, "_storage_", text);
			if (IsOurProxy(text3))
			{
				File.Delete(text3);
			}
		}
		ChainedDll recorderChained2 = rec.RecorderChained;
		if ((object)recorderChained2 != null)
		{
			string text4 = Path.Combine(dir, RoleOf(recorderChained2));
			string text5 = Path.Combine(dir, recorderChained2.Name);
			if (flag)
			{
				rec.RecorderOrphans.Add(text5);
				rec.Recorder = RecorderOverride.Off;
				log($"{name}: {text4} was deleted while the recorder ran alongside a mod (its uninstall?): {text5} left unused, recording turned off");
			}
			else if (!File.Exists(text5))
			{
				log($"{name}: {text5} (the mod's {Path.GetFileName(text4)} SCSKiller renamed) is gone");
			}
			else if (File.Exists(text4))
			{
				log($"{name}: left {text5}: another {Path.GetFileName(text4)} is in its place");
			}
			else
			{
				if (recorderChained2.Sha256 != "" && Sha256(text5) != recorderChained2.Sha256)
				{
					log(name + ": " + text5 + " changed since SCSKiller renamed it (the mod updated itself?): put back as it is");
				}
				File.Move(text5, text4);
			}
			rec.RecorderChained = null;
			try
			{
				File.Delete(text4 + ".scskiller-new");
			}
			catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException) ? true : false)
			{
				log($"{name}: left {text4 + ".scskiller-new"}: {ex3.Message}");
			}
		}
		foreach (var (text8, text9) in rec.RecorderFiles.Where((KeyValuePair<string, string> f) => f.Key != "scskiller.ini").ToList())
		{
			string text10 = Path.Combine(dir, text8);
			try
			{
				if (File.Exists(text10) && Sha256(text10) == text9)
				{
					File.Delete(text10);
				}
				else if (File.Exists(text10))
				{
					log(name + ": left " + text10 + ": changed since SCSKiller installed it");
				}
			}
			catch (Exception ex4)
			{
				log($"{name}: left {text10}: {ex4.Message}");
			}
			rec.RecorderFiles.Remove(text8);
		}
		if (flag2 || IniPending(store, dir))
		{
			return true;
		}
		string recorderExe = rec.RecorderExe;
		if (recorderExe != null && !FullDir(Path.GetDirectoryName(recorderExe)).Equals(FullDir(dir), StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		gameRecord2 = rec;
		GameRecord gameRecord6 = rec;
		string recorderInstallDir = ((rec.RecorderMoveFrom != null) ? rec.RecorderInstallDir : null);
		gameRecord2.RecorderExe = null;
		gameRecord6.RecorderInstallDir = recorderInstallDir;
		return true;
		bool At(string? exe)
		{
			if (exe != null)
			{
				return FullDir(Path.GetDirectoryName(exe)).Equals(FullDir(dir), StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
	}

	public static void RemoveAllRecorders(AppStore store, IReadOnlySet<string>? running = null, TimeSpan? budget = null)
	{
		RemoveAllRecorders(store, (running == null) ? new Func<IReadOnlySet<string>>(RunningProcessNames) : ((Func<IReadOnlySet<string>>)(() => running)), budget);
	}

	public static void RemoveAllRecorders(AppStore store, Func<IReadOnlySet<string>> running, TimeSpan? budget)
	{
		Stopwatch clock = Stopwatch.StartNew();
		TimeSpan limit = budget ?? UninstallBudget;
		string path = Path.Combine(store.DataDir, "games");
		try
		{
			MigrateInis(store, Left());
		}
		catch (Exception ex)
		{
			Log("the records' scskiller.ini entries: " + ex.Message);
		}
		List<(string, string, Func<bool>)> list = new List<(string, string, Func<bool>)>();
		foreach (string item in (Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>()).Select((string d) => AppStore.GameId(Path.GetFileName(d))).Order())
		{
			try
			{
				GameRecord gameRecord = store.LoadGame(item);
				string exe = gameRecord.RecorderExe ?? gameRecord.RecorderMoveFrom ?? gameRecord.OfflineSession?.Exe;
				if (exe == null)
				{
					continue;
				}
				string directoryName = Path.GetDirectoryName(exe);
				string installDir = gameRecord.RecorderInstallDir ?? gameRecord.OfflineSession?.InstallDir;
				string moveTo = gameRecord.RecorderMoveTo;
				HashSet<string> tree = ((installDir == null) ? new HashSet<string>() : InstallExeNames(installDir, Late));
				if (clock.Elapsed > limit)
				{
					Log("out of time: recorder left in " + directoryName);
					continue;
				}
				if (Foreign(directoryName))
				{
					Log("recorder left in " + directoryName + ": another SCSKiller copy's");
					continue;
				}
				if (tree == null)
				{
					Log(installDir + " not read whole in time: recorder left in " + directoryName);
					continue;
				}
				if (Runs())
				{
					Log(exe + " is running: recorder left in " + directoryName);
					continue;
				}
				foreach (string item2 in (from e in new string[3] { exe, gameRecord.RecorderMoveFrom, moveTo }.OfType<string>()
					select Path.GetDirectoryName(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
				{
					try
					{
						if (Foreign(item2))
						{
							Log("recorder left in " + item2 + ": another SCSKiller copy's");
							continue;
						}
						if (RemoveRecorder(item2, gameRecord, store, item2, Log, installDir, Left()) && item2.Equals(directoryName, StringComparison.OrdinalIgnoreCase))
						{
							list.Add((item, item2, Runs));
						}
						if (Directory.Exists(item2))
						{
							Log((gameRecord.RecorderFiles.Count == 0 && !IniPending(store, item2)) ? ("recorder removed from " + item2) : ("recorder removed from " + item2 + " but for files that can't be taken out yet"));
						}
					}
					catch (Exception ex2)
					{
						Log(item2 + ": " + ex2.Message);
					}
				}
				store.SaveGame(item, gameRecord, Left());
				bool Runs()
				{
					IReadOnlySet<string> readOnlySet = running();
					if (!Running(exe, installDir, readOnlySet) && (moveTo == null || !Running(moveTo, installDir, readOnlySet)) && tree != null)
					{
						return tree.Overlaps(readOnlySet);
					}
					return true;
				}
			}
			catch (Exception ex3)
			{
				Log(item + ": " + ex3.Message);
			}
		}
		foreach (var (gameId, text, runs) in list)
		{
			try
			{
				if (clock.Elapsed > limit)
				{
					Log("out of time: the recorder's data files left in " + text);
					continue;
				}
				GameRecord gameRecord2 = store.LoadGame(gameId);
				if (!RemoveRecorderData(text, Path.Combine(store.GameDir(gameId), "recording.db"), gameRecord2.RecordingInbox, runs, gameRecord2.RecorderRollback || IniPending(store, text), Left()))
				{
					Log("the game started: the recorder's data files left in " + text);
				}
			}
			catch (Exception ex4)
			{
				Log(text + ": " + ex4.Message);
			}
		}
		bool Late()
		{
			return clock.Elapsed > limit;
		}
		TimeSpan Left()
		{
			TimeSpan timeSpan = limit - clock.Elapsed;
			if (!(timeSpan > TimeSpan.Zero))
			{
				return TimeSpan.Zero;
			}
			return timeSpan;
		}
		void Log(string line)
		{
			RecordersLog(store.DataDir, "uninstall: " + line);
		}
	}

	private void CleanUninstalled(IReadOnlySet<string> found)
	{
		string path = Path.Combine(Store.DataDir, "games");
		lock (_recorderLock)
		{
			string[] array = (Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>());
			foreach (string text in array)
			{
				string id = AppStore.GameId(Path.GetFileName(text));
				string exe;
				string installDir;
				string dir;
				try
				{
					GameRecord gameRecord = Store.LoadGame(id);
					if (found.Contains(text))
					{
						goto IL_00ed;
					}
					exe = gameRecord.RecorderExe;
					if (exe == null)
					{
						goto IL_00ed;
					}
					installDir = gameRecord.RecorderInstallDir;
					if (installDir == null || gameRecord.RecorderMoveFrom != null || gameRecord.OfflineSession != null || !SteamUninstalled(id, exe, installDir))
					{
						goto IL_00ed;
					}
					dir = Path.GetDirectoryName(exe);
					DateTimeOffset utcNow = DateTimeOffset.UtcNow;
					DateTimeOffset? recorderGoneSince = gameRecord.RecorderGoneSince;
					TimeSpan? timeSpan = utcNow - recorderGoneSince;
					if (!timeSpan.HasValue || timeSpan < TimeSpan.Zero)
					{
						gameRecord.RecorderGoneSince = DateTimeOffset.UtcNow;
						Store.SaveGame(id, gameRecord);
						RecorderLog($"{id}: uninstalled by Steam, the recorder's files left in {dir}: removed by a scan {GoneFor.TotalMinutes:0} minutes or more from now");
					}
					else
					{
						if (timeSpan < GoneFor)
						{
							continue;
						}
						if (Foreign(dir))
						{
							RecorderLog(id + ": recorder left in " + dir + ": another SCSKiller copy's");
							continue;
						}
						string text2 = Links(installDir, dir);
						if (text2 != null)
						{
							RecorderLog($"{id}: recorder left in {dir}: {text2} is a link");
						}
						else
						{
							if (Hold())
							{
								continue;
							}
							List<string> source = Ours(dir);
							try
							{
								gameRecord.RecorderRollback = false;
								if (!RemoveRecorder(dir, gameRecord, Store, id, RecorderLog, installDir))
								{
									Store.SaveGame(id, gameRecord);
									continue;
								}
								GameRecord gameRecord2 = gameRecord;
								string recorderExe = exe;
								string recorderInstallDir = installDir;
								gameRecord.RecorderExe = recorderExe;
								gameRecord2.RecorderInstallDir = recorderInstallDir;
								Store.SaveGame(id, gameRecord);
								if (!RemoveRecorderData(dir, RecordingPath(id), gameRecord.RecordingInbox, Hold, gameRecord.RecorderRollback || IniPending(Store, dir)))
								{
									RecorderLog(id + ": the recorder's data files left in " + dir + ": the game started or came back");
								}
								else if (gameRecord.RecorderFiles.Count <= 0 && !gameRecord.RecorderRollback && !IniPending(Store, dir))
								{
									gameRecord2 = gameRecord;
									GameRecord gameRecord3 = gameRecord;
									gameRecord.RecorderExe = null;
									gameRecord2.RecorderInstallDir = null;
									gameRecord3.RecorderGoneSince = null;
									Store.SaveGame(id, gameRecord);
									goto IL_05d2;
								}
							}
							finally
							{
								foreach (string item in source.Where((string f) => !File.Exists(Path.Combine(dir, f))))
								{
									RecorderLog(id + ": deleted " + Path.Combine(dir, item));
								}
								foreach (string item2 in Ours(dir))
								{
									RecorderLog(id + ": left " + Path.Combine(dir, item2));
								}
							}
						}
						continue;
					}
					goto end_IL_006d;
					IL_05d2:
					string text3 = (Hold() ? null : dir);
					while (text3 != null && (GameFiles.Inside(installDir, text3) || GameFiles.DirKey(text3).Equals(GameFiles.DirKey(installDir), StringComparison.OrdinalIgnoreCase)) && !Directory.EnumerateFileSystemEntries(text3).Any())
					{
						Directory.Delete(text3);
						RecorderLog(id + ": deleted the empty folder " + text3);
						text3 = Path.GetDirectoryName(text3);
					}
					RecorderLog(id + ": recorder removed from " + dir + ": the game was uninstalled");
					goto end_IL_006d;
					IL_00ed:
					if (gameRecord.RecorderGoneSince.HasValue)
					{
						gameRecord.RecorderGoneSince = null;
						Store.SaveGame(id, gameRecord);
					}
					end_IL_006d:;
				}
				catch (Exception ex)
				{
					RecorderLog(id + ": the recorder's files in an uninstalled game's folder: " + ex.Message);
				}
				bool Hold()
				{
					if (!Running(exe, installDir, ProcessNames()) && SteamUninstalled(id, exe, installDir))
					{
						return Links(installDir, dir) != null;
					}
					return true;
				}
			}
		}
	}

	private static List<string> Ours(string dir)
	{
		return RecorderOwnFiles.Where((string f) => File.Exists(Path.Combine(dir, f))).ToList();
	}

	private static string? Links(string installDir, string dir)
	{
		string text = GameFiles.DirKey(dir);
		while (Path.GetDirectoryName(text) != null)
		{
			if ((File.GetAttributes(text) & FileAttributes.ReparsePoint) != 0)
			{
				return text;
			}
			text = Path.GetDirectoryName(text);
		}
		return (from f in Ours(dir)
			select Path.Combine(dir, f)).FirstOrDefault((string f) => (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0);
	}

	private static bool Present(string path)
	{
		try
		{
			File.GetAttributes(path);
			return true;
		}
		catch (Exception ex) when ((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? true : false)
		{
			return false;
		}
	}

	internal static bool SteamUninstalled(string id, string exe, string installDir)
	{
		try
		{
			string text = id.Replace('_', ':');
			uint? num = SteamSource.AppId(text);
			if (num.HasValue)
			{
				uint valueOrDefault = num.GetValueOrDefault();
				string text2 = GameFiles.DirKey(Collections.AppDir(new Game(text, "", SCSKiller.Core.Store.Steam, installDir, exe)));
				string directoryName = Path.GetDirectoryName(text2);
				string directoryName2 = Path.GetDirectoryName(directoryName);
				if (directoryName == null || directoryName2 == null || !Path.GetFileName(directoryName).Equals("common", StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(directoryName2).Equals("steamapps", StringComparison.OrdinalIgnoreCase) || !GameFiles.Inside(text2, exe))
				{
					return false;
				}
				string pathRoot = Path.GetPathRoot(text2);
				if (pathRoot != null && pathRoot.Length == 3)
				{
					DriveInfo driveInfo = new DriveInfo(pathRoot);
					if (driveInfo != null && driveInfo.DriveType == DriveType.Fixed && driveInfo.IsReady)
					{
						if (Present(exe) || !Directory.Exists(Path.GetDirectoryName(exe)) || !Directory.Exists(directoryName2))
						{
							return false;
						}
						if (Present(Path.Combine(directoryName2, $"appmanifest_{valueOrDefault}.acf")) || SteamSource.AppAt(directoryName2, text2).Id.HasValue)
						{
							return false;
						}
						string text3 = Path.Combine(directoryName2, "downloading");
						return !Present(Path.Combine(text3, $"{valueOrDefault}")) && !Present(Path.Combine(directoryName2, "temp", $"{valueOrDefault}")) && (!Present(text3) || !Directory.EnumerateFiles(text3, $"state_{valueOrDefault}_*").Any());
					}
				}
				return false;
			}
			return false;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is SecurityException) ? true : false)
		{
			return false;
		}
	}

	private static bool RemoveRecorderData(string dir, string recording, string? imported, Func<bool> runs, bool keepMarker = false, TimeSpan? wait = null)
	{
		if (!Directory.Exists(dir))
		{
			return true;
		}
		FileInfo fileInfo = new FileInfo(Path.Combine(dir, "scskiller.db"));
		if (fileInfo != null && fileInfo.Exists && fileInfo.Length > 0 && $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}" != imported)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(recording));
			using (Recordings.Lock(recording, wait))
			{
				Recordings.Merge(recording, fileInfo.FullName, null);
			}
		}
		if (runs())
		{
			return false;
		}
		string text = Path.Combine(Path.GetDirectoryName(recording), "offline-session");
		if (SessionFiles.Any((string f) => File.Exists(Path.Combine(dir, f))))
		{
			Directory.CreateDirectory(text);
			MoveSessionFiles(dir, text);
		}
		Exception ex = null;
		foreach (string item in RecorderDataFiles.Concat(Recordings.AppFiles))
		{
			try
			{
				File.Delete(Path.Combine(dir, item));
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
			{
				if (ex2 == null)
				{
					ex = ex2;
				}
			}
		}
		if (ex != null)
		{
			throw ex;
		}
		if (!keepMarker)
		{
			File.Delete(Path.Combine(dir, "scskiller.ledger"));
		}
		return true;
	}

	private static bool Running(string exe, string? installDir, IReadOnlySet<string> names, Func<string, string?[], bool>? elsewhere = null)
	{
		string directoryName = Path.GetDirectoryName(exe);
		string[] roots = ((installDir == null) ? new string[4]
		{
			directoryName,
			Path.GetDirectoryName(directoryName),
			Path.GetDirectoryName(Path.GetDirectoryName(directoryName)),
			Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(directoryName)))
		} : new string[2] { installDir, directoryName });
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(exe);
		if (names.Contains(fileNameWithoutExtension))
		{
			Func<string, string?[], bool>? func = elsewhere;
			if (func == null || !func(fileNameWithoutExtension, roots))
			{
				goto IL_00bd;
			}
		}
		HashSet<string> hashSet = ExeNamesIn(roots);
		if (hashSet != null)
		{
			return hashSet.Any((string n) =>
			{
				if (names.Contains(n))
				{
					Func<string, string?[], bool>? func2 = elsewhere;
					if (func2 == null)
					{
						return true;
					}
					return !func2(n, roots);
				}
				return false;
			});
		}
		goto IL_00bd;
		IL_00bd:
		return true;
	}

	internal static HashSet<string>? InstallExeNames(string installDir, Func<bool> late)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (!Directory.Exists(installDir))
		{
			return hashSet;
		}
		int num = 0;
		try
		{
			foreach (FileSystemInfo item in new DirectoryInfo(installDir).EnumerateFileSystemInfos("*", InstallTree))
			{
				if (++num > 2000000 || late())
				{
					return null;
				}
				if (item is FileInfo && item.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
				{
					hashSet.Add(Path.GetFileNameWithoutExtension(item.Name));
				}
			}
			return hashSet;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException) ? true : false)
		{
			return null;
		}
	}

	public void Enqueue(string gameId)
	{
		Add(gameId, whenIdle: false);
	}

	public void EnqueueWhenIdle(string gameId)
	{
		Add(gameId, whenIdle: true);
	}

	private static bool Finished(QueueStage s)
	{
		if ((uint)(s - 6) <= 2u)
		{
			return true;
		}
		return false;
	}

	private int WaitingStart()
	{
		if (_queue.Count <= 0 || !(_queue[0].GameId == _current))
		{
			return 0;
		}
		return 1;
	}

	private void Add(string gameId, bool whenIdle, bool planCheck = false, bool prompt = false, bool pair = false)
	{
		if (!planCheck)
		{
			GameState gameState = Games.FirstOrDefault((GameState s) => s.Game.Id == gameId);
			if ((object)gameState != null && (GameVerdicts.Current.Unsupported(gameState.Game, gameState.Engine) != null || gameState.StatusReason == "the compile can't reach this game: it reads its shaders from a different driver cache than the one SCSKiller fills. Clear its cache to free the space"))
			{
				return;
			}
		}
		if (!pair)
		{
			string text = PairHoldsCache(gameId, compiling: true);
			if (text != null)
			{
				Log?.Report(gameId + ": not queued: " + text);
				return;
			}
		}
		QueueItem queueItem2;
		lock (_lock)
		{
			if (Unlisted(gameId))
			{
				return;
			}
			int num = _queue.FindIndex((QueueItem q) => q.GameId == gameId);
			bool flag = num >= 0 && !Finished(_queue[num].Stage);
			bool flag2 = !whenIdle && _whenIdle.Remove(gameId);
			if (!planCheck)
			{
				flag2 |= _planOnly.Remove(gameId);
			}
			if (flag && !flag2)
			{
				return;
			}
			if (planCheck)
			{
				_planOnly.Add(gameId);
			}
			if (planCheck & prompt)
			{
				_prompt.Add(gameId);
			}
			else
			{
				_prompt.Remove(gameId);
			}
			if (whenIdle)
			{
				_whenIdle.Add(gameId);
			}
			if (flag)
			{
				List<QueueItem> queue = _queue;
				QueueItem queueItem = _queue[num]with
				{
					Error = null,
					Note = ((!whenIdle) ? null : ((_queue[num].Stage == QueueStage.Waiting) ? "starts when the PC is idle" : _queue[num].Note)),
					PlanCheck = false
				};
				queueItem2 = queueItem;
				queue[num] = queueItem;
			}
			else
			{
				if (num >= 0)
				{
					_queue.RemoveAt(num);
				}
				queueItem2 = new QueueItem(gameId, QueueStage.Waiting, null, null, (whenIdle && !prompt) ? "starts when the PC is idle" : null, planCheck);
				int num2 = _queue.FindIndex((QueueItem q) => Finished(q.Stage));
				_queue.Insert((num2 < 0) ? _queue.Count : num2, queueItem2);
			}
			if ((whenIdle || _running) && _worker == null)
			{
				_worker = Task.Run((Func<Task?>)Work);
			}
		}
		QueueChanged?.Invoke(queueItem2);
	}

	public void StartQueue()
	{
		List<QueueItem> list;
		lock (_lock)
		{
			list = _queue.Where((QueueItem q) =>
			{
				QueueStage stage = q.Stage;
				return (uint)(stage - 6) <= 1u;
			}).ToList();
			List<QueueItem> collection = (from q in _queue
				where q.Stage == QueueStage.Stopped
				select q with
				{
					Stage = QueueStage.Waiting,
					Error = null,
					Note = null
				}).ToList();
			_queue.RemoveAll((QueueItem q) => Finished(q.Stage));
			_queue.InsertRange(WaitingStart(), collection);
			RunWaiting();
			list.AddRange(_queue);
		}
		foreach (QueueItem item in list)
		{
			QueueChanged?.Invoke(item);
		}
	}

	public void CompileMax(string gameId)
	{
		lock (_lock)
		{
			_maxNext.Add(gameId);
		}
		Compile(gameId);
	}

	public void Compile(string gameId)
	{
		string text = PairHoldsCache(gameId, compiling: true);
		if (text != null)
		{
			throw new InvalidOperationException(text);
		}
		Compile(gameId, pair: false);
	}

	private void Compile(string gameId, bool pair)
	{
		Add(gameId, whenIdle: false, planCheck: false, prompt: false, pair);
		QueueItem queueItem;
		lock (_lock)
		{
			if (_running)
			{
				return;
			}
			RunWaiting();
			queueItem = _queue.FirstOrDefault((QueueItem q) => q.GameId == gameId);
		}
		if (queueItem != null)
		{
			QueueChanged?.Invoke(queueItem);
		}
	}

	private void RunWaiting()
	{
		if (_current != null)
		{
			_whenIdle.Remove(_current);
		}
		_running = _queue.Any((QueueItem q) => q.Stage == QueueStage.Waiting && !_whenIdle.Contains(q.GameId));
		if (_running && _worker == null)
		{
			_worker = Task.Run((Func<Task?>)Work);
		}
	}

	public void MoveInQueue(string gameId, int index)
	{
		QueueItem queueItem;
		lock (_lock)
		{
			int num = _queue.FindIndex((QueueItem q) => q.GameId == gameId);
			if (num < 0 || _queue[num].Stage != QueueStage.Waiting)
			{
				return;
			}
			queueItem = _queue[num];
			_queue.RemoveAt(num);
			int num2 = WaitingStart();
			List<int> list = Enumerable.Range(num2, _queue.Count - num2).Where((int j) =>
			{
				QueueItem queueItem2 = _queue[j];
				return (object)queueItem2 != null && queueItem2.Stage == QueueStage.Waiting && !queueItem2.PlanCheck;
			}).ToList();
			index = Math.Clamp(index, 0, list.Count);
			List<QueueItem> queue = _queue;
			int index2;
			if (index >= list.Count)
			{
				if (list.Count <= 0)
				{
					index2 = num2;
				}
				else
				{
					index2 = list[list.Count - 1] + 1;
				}
			}
			else
			{
				index2 = list[index];
			}
			queue.Insert(index2, queueItem);
		}
		QueueChanged?.Invoke(queueItem);
	}

	public void Remove(string gameId)
	{
		QueueItem obj;
		bool flag;
		lock (_lock)
		{
			_whenIdle.Remove(gameId);
			_planOnly.Remove(gameId);
			_prompt.Remove(gameId);
			_maxNext.Remove(gameId);
			int num = _queue.FindIndex((QueueItem q) => q.GameId == gameId);
			if (num < 0)
			{
				return;
			}
			obj = _queue[num];
			_queue.RemoveAt(num);
			flag = _current == gameId;
		}
		QueueChanged?.Invoke(obj);
		if (flag)
		{
			StopCurrent();
		}
	}

	public void PauseQueue()
	{
		_go.Reset();
		IWarmRun run;
		lock (_lock)
		{
			run = _run;
		}
		run?.Pause();
		SetCurrent(QueueStage.Paused);
	}

	public void ResumeQueue()
	{
		IWarmRun run;
		lock (_lock)
		{
			if (_current != null)
			{
				_whenIdle.Remove(_current);
			}
			run = _run;
		}
		_go.Set();
		if (run == null)
		{
			SetCurrent(_stage);
		}
	}

	public void StopQueue()
	{
		lock (_lock)
		{
			_running = false;
		}
		StopCurrent();
	}

	private void StopCurrent()
	{
		CancellationTokenSource itemCts;
		lock (_lock)
		{
			itemCts = _itemCts;
		}
		itemCts?.Cancel();
		IWarmRun run;
		lock (_lock)
		{
			run = _run;
		}
		run?.Stop();
	}

	private void Set(QueueItem item)
	{
		lock (_lock)
		{
			int num = _queue.FindIndex((QueueItem q) => q.GameId == item.GameId);
			if (num < 0)
			{
				return;
			}
			item = item with
			{
				PlanCheck = _queue[num].PlanCheck
			};
			_queue.RemoveAt(num);
			_queue.Insert(Finished(item.Stage) ? _queue.Count : num, item);
		}
		QueueChanged?.Invoke(item);
		BeforeAfterQueued(item);
	}

	private void SetCurrent(QueueStage stage, string? note = null)
	{
		QueueItem queueItem;
		lock (_lock)
		{
			queueItem = _queue.FirstOrDefault((QueueItem q) => q.GameId == _current);
		}
		if (queueItem != null)
		{
			Set(queueItem with
			{
				Stage = stage,
				Note = note,
				Progress = ((queueItem.Stage == QueueStage.Indexing && stage != QueueStage.Indexing) ? null : queueItem.Progress)
			});
		}
	}

	private bool WaitsForIdle(string id)
	{
		bool flag;
		lock (_lock)
		{
			if (!_whenIdle.Contains(id))
			{
				return false;
			}
			flag = _prompt.Contains(id);
		}
		if (!flag)
		{
			return !UserIdle;
		}
		if (Settings.PauseWhileGaming)
		{
			return _playing.Count > 0;
		}
		return false;
	}

	private async Task Work()
	{
		while (true)
		{
			string id = null;
			QueueItem queueItem = null;
			CancellationToken ct = default;
			bool flag;
			string key;
			lock (_lock)
			{
				List<string> source = (from q in _queue
					where q.Stage == QueueStage.Waiting
					select q.GameId).ToList();
				string text = (_running ? source.FirstOrDefault((string g) => !_whenIdle.Contains(g)) : null);
				if (_running && text == null)
				{
					QueueItem? queueItem2 = _queue.FirstOrDefault();
					_running = false;
					queueItem = queueItem2;
				}
				List<string> list = source.Where(_whenIdle.Contains).ToList();
				if (flag = text == null && list.Count == 0)
				{
					_worker = null;
				}
				else
				{
					string obj = text ?? list.FirstOrDefault((string g) => !WaitsForIdle(g));
					key = obj;
					id = obj;
					if (key != null)
					{
						int index = _queue.FindIndex((QueueItem q) => q.GameId == id);
						QueueItem item = _queue[index];
						_queue.RemoveAt(index);
						_queue.Insert(0, item);
						key = (_current = id);
						_stage = QueueStage.Waiting;
						_itemCts = new CancellationTokenSource();
						ct = _itemCts.Token;
					}
				}
			}
			if (queueItem != null)
			{
				QueueChanged?.Invoke(queueItem);
			}
			if (flag)
			{
				break;
			}
			if (id == null)
			{
				await Task.Delay(Poll);
				continue;
			}
			foreach (KeyValuePair<string, CancellationTokenSource> recorderIndex in _recorderIndexes)
			{
				recorderIndex.Deconstruct(out key, out var value);
				string id2 = key;
				CancellationTokenSource cts = value;
				ParkIndex(id2, cts);
			}
			while (Volatile.Read(in _readingIndexes) > 0 && !ct.IsCancellationRequested)
			{
				await Task.Delay(50);
			}
			try
			{
				IDisposable hold;
				while (true)
				{
					IDisposable disposable;
					hold = (disposable = HoldForCompile());
					if (disposable != null || ct.IsCancellationRequested)
					{
						break;
					}
					await Task.Delay(Poll);
				}
				using (hold)
				{
					FileStream own = null;
					bool flag2 = false;
					while (hold != null)
					{
						FileStream fileStream;
						own = (fileStream = CompileLock(id));
						if (fileStream != null || ct.IsCancellationRequested)
						{
							break;
						}
						if (!flag2)
						{
							SetCurrent(QueueStage.Waiting, "waits for another SCSKiller compile of this game");
						}
						await Task.Delay(Poll);
						flag2 = true;
					}
					if (own == null)
					{
						Set(new QueueItem(id, QueueStage.Stopped, null, null));
						continue;
					}
					using (own)
					{
						await RunItem(id, ct);
					}
				}
			}
			catch (Exception ex)
			{
				Log?.Report(id + ": " + ex.Message);
				QueueItem queueItem3;
				lock (_lock)
				{
					queueItem3 = _queue.FirstOrDefault((QueueItem q) => q.GameId == id);
				}
				if (queueItem3 != null && !Finished(queueItem3.Stage))
				{
					Set(queueItem3 with
					{
						Stage = QueueStage.Failed,
						Error = ex.Message
					});
				}
			}
			finally
			{
				lock (_lock)
				{
					_current = null;
					_run = null;
					_itemCts = null;
				}
			}
		}
		ReleaseMemory();
	}

	private FileStream? CompileLock(string id)
	{
		Directory.CreateDirectory(Store.GameDir(id));
		try
		{
			return new FileStream(Path.Combine(Store.GameDir(id), "compile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		}
		catch (IOException ex) when ((uint)((ex.HResult & 0xFFFF) - 32) <= 1u)
		{
			return null;
		}
	}

	private static void ReleaseMemory()
	{
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
	}

	private async Task Gate(string id, CancellationToken ct)
	{
		while (!_go.IsSet || WaitsForIdle(id))
		{
			await Task.Delay(Poll, ct);
		}
	}

	private async Task RunItem(string id, CancellationToken ct)
	{
		WarmProgress progress = null;
		long lastDone = -1L;
		long crashed = 0L;
		long unbuilt = 0L;
		Stopwatch warmClock = null;
		bool rtSkipped = false;
		WarmResult ended = null;
		TimeSpan took = default;
		Stopwatch advanced = Stopwatch.StartNew();
		Game compiling = null;
		GameState state = Games.FirstOrDefault((GameState s) => s.Game.Id == id);
		Game installed = (compiling = ((state == null) ? null : Current(state.Game)));
		Evaluation now = ((installed == null) ? null : Evaluated(installed, Store.LoadGame(id), force: false, out var fresh));
		if (NotReady() != null)
		{
			Evaluation evaluation = now;
			if ((object)evaluation != null && evaluation.Check.Readiness == Readiness.Ready)
			{
				Refresh(installed, ct);
				state = Games.FirstOrDefault((GameState s) => s.Game.Id == id);
			}
		}
		string text = NotReady();
		if (text != null)
		{
			Stage(QueueStage.Failed, text);
			if (installed != null)
			{
				Refresh(installed, ct);
			}
			return;
		}
		EngineInfo engine = now.Engine;
		Game game = installed;
		bool whenIdle;
		lock (_lock)
		{
			whenIdle = _whenIdle.Contains(id);
		}
		GameRecord rec = Store.LoadGame(id);
		string work = Path.Combine(Store.GameDir(id), "work");
		string indexed = null;
		int replans = 0;
		DateTime started = DateTime.UtcNow;
		bool refreshed = false;
		bool checking;
		lock (_lock)
		{
			checking = _planOnly.Contains(id);
		}
		try
		{
			work = FreshWork(Store.GameDir(id), Log);
			if (InboxPending(game, rec))
			{
				Stage(QueueStage.Indexing);
				Refresh(game, ct);
				rec = Store.LoadGame(id);
			}
			string indexStamp;
			ShaderIndex index;
			bool maxOnce;
			bool prepared;
			Recording recording;
			WarmInputs.Recorded recorded;
			string preparedCommunity;
			string preparedOfficial;
			bool flag;
			do
			{
				IL_0483:
				string rules = RulesFile.Current.Hash(engine);
				Stage(QueueStage.Indexing);
				await Gate(id, ct);
				indexStamp = IndexStamp(game);
				index = Lowered(checking, () => Index(game, engine, rec, ct));
				indexed = index.ContentHash;
				using (CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
				{
					wait.CancelAfter(TimeSpan.FromSeconds(20L));
					try
					{
						await SyncPacks(new _003C_003Ez__ReadOnlySingleElementList<Game>(game), wait.Token);
						await SyncCommunity(game, index.ContentHash, wait.Token);
					}
					catch (OperationCanceledException) when (!ct.IsCancellationRequested)
					{
						Log?.Report(game.Name + ": community database: no answer in time, compiling without it");
					}
				}
				lock (_lock)
				{
					maxOnce = _maxNext.Contains(id);
				}
				prepared = false;
				recording = null;
				recorded = null;
				preparedCommunity = null;
				preparedOfficial = null;
				Planner packs = _planner as Planner;
				flag = false;
				string shared = packs?.SharedFingerprint(game);
				string maps = MapsFingerprint(index);
				if (PlanIsStale(id, rec, engine, maxOnce) || rec.PlanMaps != maps || (packs != null && packs.PackFingerprint(game, shared) != (rec.PlanMiddleware ?? "")))
				{
					Stage(QueueStage.Planning);
					await Gate(id, ct);
					GameRecord gameRecord = rec;
					GameRecord gameRecord2 = rec;
					GameRecord gameRecord3 = rec;
					fresh = PerStagePlans(rec, maxOnce);
					bool? planMaximumCapped = true;
					bool planRayTracing = RayTracing(rec);
					gameRecord.PlanPerStage = fresh;
					gameRecord2.PlanMaximumCapped = planMaximumCapped;
					gameRecord3.PlanRayTracing = planRayTracing;
					if (rec.Plan != null)
					{
						rec.Plan = null;
						Store.SaveGame(id, rec);
					}
					Plan plan = Lowered(checking, () => _planner.Build(game, engine, index, Prepared(), Vendor.Caps, Store.GameDir(id), Log, ct, Maximum(rec) | maxOnce, rec.PlanRayTracing));
					if (RulesFile.Current.Hash(engine) != rules)
					{
						int num = replans + 1;
						replans = num;
						if (num <= 2)
						{
							Log?.Report(game.Name + ": the planning rules changed while it indexed and planned: again with the new ones");
							goto IL_0483;
						}
					}
					gameRecord2 = rec;
					GameRecord gameRecord4 = rec;
					Plan plan2 = plan;
					string planRules = rules;
					gameRecord2.Plan = plan2;
					gameRecord4.PlanRules = planRules;
					gameRecord2 = rec;
					gameRecord = rec;
					GameRecord gameRecord5 = rec;
					GameRecord gameRecord6 = rec;
					DateTimeOffset? planBuiltAt = DateTimeOffset.Now;
					int? planReaderVersion = _reader.ReaderVersion(engine);
					gameRecord2.PlanBuiltAt = planBuiltAt;
					gameRecord.PlanVersion = 42;
					gameRecord5.PlanReaderVersion = planReaderVersion;
					gameRecord6.ResumeAt = 0L;
					gameRecord5 = rec;
					gameRecord = rec;
					gameRecord2 = rec;
					GameRecord gameRecord7 = rec;
					planRules = maps;
					string planApi = ApiKey(engine.GraphicsApi);
					string planEngine = EngineKey(engine);
					planReaderVersion = Filtering(id, every: false, engine);
					gameRecord5.PlanMaps = planRules;
					gameRecord.PlanApi = planApi;
					gameRecord2.PlanEngine = planEngine;
					gameRecord7.PlanFiltering = planReaderVersion;
					rec.PlanMiddleware = ((packs == null) ? null : (packs.SeededFingerprint(rec.Plan) ?? packs.PackFingerprint(game, shared)));
					gameRecord2 = rec;
					GameRecord gameRecord8 = rec;
					planEngine = preparedCommunity;
					planApi = preparedOfficial;
					gameRecord2.PlanCommunity = planEngine;
					gameRecord8.PlanOfficial = planApi;
					HashSet<string> hashSet = PlanKeys(rec.Plan.FilePath);
					rec.PlanItems = ((hashSet == null) ? null : PlanFingerprint(hashSet));
					rec.PlanKeysFile = KeyFiles.Write(Store.GameDir(id), "plan", PlannerMade(rec.Plan, work));
					if (flag = !maxOnce && PendingOf(game, rec, engine).Planned == 0 && !CurrentPlanner(rec.WarmedPlanVersion, rec.WarmedPlanRules, rec.WarmedReaderVersion, engine) && StaleReason(game, rec, engine) == null)
					{
						if (Reread(rec, engine))
						{
							gameRecord2 = rec;
							GameRecord gameRecord9 = rec;
							planApi = index.ContentHash;
							planEngine = indexStamp;
							gameRecord2.WarmedIndexHash = planApi;
							gameRecord9.WarmedIndexStamp = planEngine;
						}
						gameRecord2 = rec;
						gameRecord = rec;
						gameRecord5 = rec;
						GameRecord gameRecord10 = rec;
						int num = rec.PlanVersion;
						planEngine = rec.PlanRules;
						planReaderVersion = rec.PlanReaderVersion;
						planApi = rec.PlanItems;
						gameRecord2.WarmedPlanVersion = num;
						gameRecord.WarmedPlanRules = planEngine;
						gameRecord5.WarmedReaderVersion = planReaderVersion;
						gameRecord10.WarmedPlanItems = planApi;
					}
					Store.SaveGame(id, rec);
					Store.PruneKeyFiles(id);
				}
				if (maxOnce)
				{
					break;
				}
				lock (_lock)
				{
					maxOnce = _maxNext.Contains(id);
				}
			}
			while (maxOnce);
			bool flag2;
			lock (_lock)
			{
				flag2 = _planOnly.Remove(id);
				_prompt.Remove(id);
			}
			ScsKiller scsKiller;
			string gameId;
			string note;
			if (flag | flag2)
			{
				scsKiller = this;
				gameId = id;
				if (!flag)
				{
					if (rec.WarmedAt.HasValue)
					{
						string text2 = StaleReason(game, rec, engine);
						if (text2 != null)
						{
							note = text2;
							goto IL_120b;
						}
					}
					note = "plan built";
				}
				else
				{
					note = "already compiled: the new plan adds nothing";
				}
				goto IL_120b;
			}
			if (whenIdle)
			{
				string text3 = RulesGrew(game, rec, engine);
				if (text3 != null)
				{
					rec.IdleHeldBack = HeldBackKey(rec.PlanRules ?? RulesFile.Embedded.Hash(engine));
					Store.SaveGame(id, rec);
					Set(new QueueItem(id, QueueStage.Done, null, null, text3));
					return;
				}
			}
			Stage(QueueStage.Materializing);
			await Gate(id, ct);
			Prepared();
			HashSet<string> inputs = WarmInputs.Of(recorded, rec.Plan.FilePath, Array.Empty<PsoDb.Rec>(), Elsewhere(game, rec));
			if (recorded.LaunchOnly > 0)
			{
				Log?.Report($"{game.Name}: {recorded.LaunchOnly:N0} recorded ray tracing pipelines are compiled but never count as new: the game names them anew each launch, and this GPU's driver reuses one only exactly as it was built");
			}
			if (_planner is Planner planner)
			{
				planner.Log = Log;
			}
			AgsRegistration agsUsed = null;
			_planner.Materialize(rec.Plan, game, engine, _reader, recording, work, ct);
			(string Dir, string Fingerprint)? layer = null;
			if (_warmer is Warmer warmer)
			{
				Warmer warmer2 = warmer;
				Warmer warmer3 = warmer;
				IProgress<string> log = Log;
				Func<Game, AgsRegistration> ags = (Game g) => agsUsed = AgsFor(g, rec, recover: true);
				Func<Game, string, string> layer2 = (Game _, string _) => layer?.Dir;
				warmer2.Log = log;
				warmer3.Ags = ags;
				warmer.Layer = layer2;
			}
			SessionLog.WriteRayQueryKeys(new _003C_003Ez__ReadOnlyArray<string>(new string[2]
			{
				Path.Combine(work, "scskiller.db"),
				Path.Combine(work, "scskiller_gen.db")
			}), RayQueryKeysPath(id));
			string compiledFile = Path.Combine(Store.GameDir(id), "compiled.keys");
			string[] workDbs = new string[2]
			{
				Path.Combine(work, "scskiller.db"),
				Path.Combine(work, "scskiller_gen.db")
			};
			HashSet<string> compiled = null;
			string stoppedFailed = Path.Combine(Store.GameDir(id), "stopped-scskiller_failed.keys");
			string text4 = FullReplayReason(game, rec, engine, Snapshot());
			if (text4 != null)
			{
				Log?.Report(game.Name + ": replaying every pipeline: " + text4);
			}
			else
			{
				HashSet<string> hashSet2 = CompiledItems.Load(compiledFile);
				if (hashSet2 == null)
				{
					Log?.Report(game.Name + ": replaying every pipeline: what earlier compiles created isn't on record");
				}
				else
				{
					compiled = hashSet2;
					long value = CompiledItems.LeaveOut(workDbs, hashSet2);
					Log?.Report($"{game.Name}: {value:N0} pipelines an earlier compile on this driver created are left out");
				}
			}
			long skipped = Planner.SkippedIn(work);
			long needsRecording = Planner.NeedsRecordingIn(work);
			int? cap = (rec.Careful ? CarefulThreads(Vendor.Vendor) : ((int?)null));
			int carefulPasses = (cap.HasValue ? WarmPasses.Write(work) : 0);
			if (carefulPasses > 0)
			{
				Log?.Report($"{game.Name}: careful compile: the recorded PSOs in {carefulPasses} passes on at most {cap} threads, then the rest");
			}
			Stage(QueueStage.Warming);
			string exe = WarmExeName(game, rec);
			HashSet<string> cacheKeys = new HashSet<string>();
			string driverId;
			GpuInfo gpu2;
			string ags2;
			WarmOptions options;
			WarmResult result;
			double seconds;
			long items;
			while (true)
			{
				await WhileGameRuns(exe, game.Name, ct, () =>
				{
					if (LearnKeys(game, rec, WarmExeName(game, rec)).Count > 0)
					{
						Store.SaveGame(id, rec);
					}
				});
				await Gate(id, ct);
				if (MergeLaunched(game, rec, null))
				{
					Store.SaveGame(id, rec);
				}
				exe = WarmExeName(game, rec);
				RedetectGpu();
				GpuSnapshot gpuSnapshot = Snapshot();
				GpuInfo gpu = gpuSnapshot.Gpu;
				string planApi = gpuSnapshot.Id;
				driverId = planApi;
				gpu2 = gpu;
				if (AdoptDriverId(rec, gpuSnapshot))
				{
					Store.SaveGame(id, rec);
				}
				if (rec.CrashKeys.Count > 0 && !CrashKeysCurrent(rec, gpuSnapshot))
				{
					Log?.Report($"{game.Name}: retrying the {rec.CrashKeys.Count} pipelines that crashed another driver on driver {gpu2.DriverVersion}");
					rec.CrashKeys.Clear();
					Store.SaveGame(id, rec);
				}
				if (driverId != null && rec.ResumeAt > 0 && rec.ResumeGpu != driverId)
				{
					Log?.Report($"{game.Name}: the stopped compile was for {((rec.ResumeGpu == null) ? "an unknown" : "another")} driver: starting over on driver {gpu2.DriverVersion}");
					GameRecord gameRecord5 = rec;
					GameRecord gameRecord = rec;
					GameRecord gameRecord2 = rec;
					GameRecord gameRecord11 = rec;
					gameRecord5.ResumeAt = 0L;
					gameRecord.ResumeItems = 0L;
					gameRecord2.ResumeSeconds = 0.0;
					gameRecord11.ResumeFailed = 0L;
					Store.SaveGame(id, rec);
				}
				if (compiled != null)
				{
					string text5 = FullReplayReason(game, rec, engine, gpuSnapshot);
					if (text5 != null)
					{
						Log?.Report(game.Name + ": replaying every pipeline: " + text5);
						_planner.Materialize(rec.Plan, game, engine, _reader, recording, work, ct);
						compiled = null;
					}
				}
				if (rec.ResumeAt > 0 && rec.ResumeLeftOut != (compiled != null))
				{
					Log?.Report(game.Name + ": the stopped compile " + (rec.ResumeLeftOut ? "left out" : "replayed") + " what was compiled before: starting over");
					GameRecord gameRecord2 = rec;
					GameRecord gameRecord = rec;
					GameRecord gameRecord5 = rec;
					GameRecord gameRecord12 = rec;
					gameRecord2.ResumeAt = 0L;
					gameRecord.ResumeItems = 0L;
					gameRecord5.ResumeSeconds = 0.0;
					gameRecord12.ResumeFailed = 0L;
					Store.SaveGame(id, rec);
				}
				ReShadeInstall reShadeInstall = BlockingMod(game);
				if ((object)reShadeInstall != null)
				{
					throw new NotReadyException("not ready: " + ShaderModReason(reShadeInstall));
				}
				string text6 = PairHoldsCache(id, compiling: true);
				if (text6 != null)
				{
					Log?.Report(game.Name + ": not compiled: " + text6);
					throw new NotReadyException(text6);
				}
				layer = LayerFor(game, work);
				if (layer.HasValue)
				{
					Log?.Report(game.Name + ": compiling through a copy of the game's ReShade layer");
				}
				if (rec.ResumeAt > 0 && rec.ResumeLayer != layer?.Fingerprint)
				{
					Log?.Report(game.Name + ": the stopped compile ran through " + ((rec.ResumeLayer == null) ? "no" : "another") + " ReShade layer: starting over");
					GameRecord gameRecord5 = rec;
					GameRecord gameRecord = rec;
					GameRecord gameRecord2 = rec;
					GameRecord gameRecord13 = rec;
					gameRecord5.ResumeAt = 0L;
					gameRecord.ResumeItems = 0L;
					gameRecord2.ResumeSeconds = 0.0;
					gameRecord13.ResumeFailed = 0L;
					Store.SaveGame(id, rec);
				}
				if (rec.ResumeAt > 0 && !exe.Equals(rec.ResumeExe, StringComparison.OrdinalIgnoreCase))
				{
					Log?.Report($"{game.Name}: the stopped compile was for {rec.ResumeExe ?? "another exe"}: starting over for {exe}");
					GameRecord gameRecord2 = rec;
					GameRecord gameRecord = rec;
					GameRecord gameRecord5 = rec;
					GameRecord gameRecord14 = rec;
					gameRecord2.ResumeAt = 0L;
					gameRecord.ResumeItems = 0L;
					gameRecord5.ResumeSeconds = 0.0;
					gameRecord14.ResumeFailed = 0L;
					Store.SaveGame(id, rec);
				}
				ags2 = AgsFor(game, rec, recover: true)?.App ?? "";
				string text7 = rec.ResumeAgs ?? AgsFor(game, rec)?.App ?? "";
				if (rec.ResumeAt > 0 && text7 != ags2)
				{
					Log?.Report(game.Name + ": the stopped compile was for " + ((text7 == "") ? "the exe name's cache" : ("the AGS app name " + text7)) + ": starting over");
					GameRecord gameRecord5 = rec;
					GameRecord gameRecord = rec;
					GameRecord gameRecord2 = rec;
					GameRecord gameRecord15 = rec;
					gameRecord5.ResumeAt = 0L;
					gameRecord.ResumeItems = 0L;
					gameRecord2.ResumeSeconds = 0.0;
					gameRecord15.ResumeFailed = 0L;
					Store.SaveGame(id, rec);
				}
				bool flag3;
				lock (_lock)
				{
					flag3 = Background || _whenIdle.Contains(id);
				}
				int num2 = ThreadsOverride ?? (flag3 ? Settings.BackgroundThreads : Settings.Threads);
				WarmPriority priority = (flag3 ? WarmPriority.Idle : Settings.Priority);
				long resumeAt = rec.ResumeAt;
				int memoryMB = CompileMemoryGB(Settings) * 1024;
				object skipKeys;
				IReadOnlyCollection<string> readOnlyCollection;
				if (rec.CrashKeys.Count <= 0)
				{
					skipKeys = null;
				}
				else
				{
					readOnlyCollection = new _003C_003Ez__ReadOnlyArray<string>(rec.CrashKeys.ToArray());
					skipKeys = readOnlyCollection;
				}
				int carefulThreads;
				if (cap.HasValue)
				{
					int valueOrDefault = cap.GetValueOrDefault();
					carefulThreads = Math.Min(num2, valueOrDefault);
				}
				else
				{
					carefulThreads = 0;
				}
				options = new WarmOptions(num2, priority, resumeAt, memoryMB, (IReadOnlyCollection<string>?)skipKeys, carefulThreads);
				if (options.StartAt == 0L)
				{
					GameRecord gameRecord2 = rec;
					GameRecord gameRecord = rec;
					GameRecord gameRecord5 = rec;
					GameRecord gameRecord16 = rec;
					gameRecord2.ResumeItems = 0L;
					gameRecord.ResumeSeconds = 0.0;
					gameRecord5.ResumeFailed = 0L;
					gameRecord16.ResumeDriverSkipped = 0L;
				}
				if (options.StartAt == 0L)
				{
					File.Delete(stoppedFailed);
				}
				Game game2 = game with
				{
					ExePath = Path.Combine(Path.GetDirectoryName(game.ExePath), exe)
				};
				Reporter<WarmProgress> progress2 = new Reporter<WarmProgress>((WarmProgress p) =>
				{
					progress = p with
					{
						Failed = p.Failed + rec.ResumeFailed,
						DriverSkipped = p.DriverSkipped + rec.ResumeDriverSkipped,
						Skipped = skipped
					};
					Stage(QueueStage.Warming);
				});
				Stopwatch stopwatch = Stopwatch.StartNew();
				bool planRayTracing = !rec.PlanRayTracing;
				rtSkipped = planRayTracing;
				warmClock = stopwatch;
				if (PutBack(id, late: false))
				{
					ShowKept(id);
				}
				IWarmRun warmRun = ((_warmer is Warmer warmer4) ? warmer4.Start(game2, work, options, progress2, gpu2) : _warmer.Start(game2, work, options, progress2));
				lock (_lock)
				{
					_run = warmRun;
				}
				Task attribution = Attribution(warmRun, exe);
				if (ct.IsCancellationRequested)
				{
					warmRun.Stop();
				}
				if (!_go.IsSet)
				{
					warmRun.Pause();
				}
				bool yielded;
				(result, yielded) = await Watch(warmRun, id, exe, game.Name);
				await attribution;
				result = result with
				{
					Failed = rec.ResumeFailed + result.Failed,
					DriverSkipped = rec.ResumeDriverSkipped + result.DriverSkipped,
					Skipped = skipped
				};
				unbuilt = Math.Min(result.Failed, Unbuilt11(WorkLogs(work)));
				progress = new WarmProgress(result.Done, result.Total, result.Failed - unbuilt, progress?.PerSecond ?? 0.0, result.CacheGrowthBytes, result.Skipped, null, 0, 0, result.DriverSkipped);
				NoteProfile(game, exe, cacheKeys.Where((string k) => !rec.CacheKeys.Contains(k)).ToList());
				rec.CacheKeys.UnionWith(cacheKeys);
				readOnlyCollection = result.Crashed;
				if (readOnlyCollection != null && readOnlyCollection.Count > 0)
				{
					rec.CrashKeysDriver = driverId ?? rec.CrashKeysDriver;
				}
				rec.CrashKeys.UnionWith(result.Crashed ?? Array.Empty<string>());
				crashed = result.Crashed?.Count ?? 0;
				long num3 = Math.Max(0L, options.StartAt - rec.ResumeFailed - rec.ResumeDriverSkipped) + Math.Max(0L, result.Done - options.StartAt - (result.Failed - rec.ResumeFailed) - (result.DriverSkipped - rec.ResumeDriverSkipped) - crashed);
				if ((object)result != null && result.Outcome == WarmOutcome.Completed && result.Failed > 0 && num3 == 0L && (compiled == null || compiled.Count <= 0))
				{
					result = result with
					{
						Outcome = WarmOutcome.Failed,
						Error = AllFailedReason(work, result.Failed),
						Resumable = false
					};
					GameRecord gameRecord5 = rec;
					GameRecord gameRecord = rec;
					GameRecord gameRecord2 = rec;
					GameRecord gameRecord17 = rec;
					gameRecord5.ResumeAt = 0L;
					gameRecord.ResumeItems = 0L;
					gameRecord2.ResumeSeconds = 0.0;
					gameRecord17.ResumeFailed = 0L;
					rec.IdleHeldBack = HeldBackKey(rec.PlanRules ?? RulesFile.Embedded.Hash(engine));
					KeepRejectsLog(work, id, result.Failed);
				}
				long num4 = rec.ResumeItems + result.Done - options.StartAt;
				double num5 = rec.ResumeSeconds + result.Elapsed.TotalSeconds;
				seconds = num5;
				items = num4;
				WarmResult warmResult = result;
				TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);
				took = timeSpan;
				ended = warmResult;
				if (!yielded || result.Outcome != WarmOutcome.Stopped || ct.IsCancellationRequested)
				{
					break;
				}
				KeepResume();
				Store.SaveGame(id, rec);
				continue;
			}
			switch (result.Outcome)
			{
			case WarmOutcome.Completed:
			{
				bool flag4 = ColdWarm(rec, driverId);
				if (compiled != null)
				{
					string text8 = CacheFilesChanged(id, rec);
					if (text8 != null)
					{
						Log?.Report(game.Name + ": " + text8 + " during the compile: only what it created stays on record as compiled");
						compiled = null;
					}
				}
				GameRecord gameRecord2 = rec;
				GameRecord gameRecord18 = rec;
				string planApi = driverId;
				string planEngine = gpu2.DriverVersion;
				gameRecord2.WarmedDriverId = planApi;
				gameRecord18.WarmedDriverVersion = planEngine;
				gameRecord2 = rec;
				GameRecord gameRecord = rec;
				GameRecord gameRecord19 = rec;
				int num = rec.PlanVersion;
				planEngine = rec.PlanRules;
				int? planReaderVersion = rec.PlanReaderVersion;
				gameRecord2.WarmedPlanVersion = num;
				gameRecord.WarmedPlanRules = planEngine;
				gameRecord19.WarmedReaderVersion = planReaderVersion;
				gameRecord = rec;
				gameRecord2 = rec;
				GameRecord gameRecord20 = rec;
				planEngine = exe;
				bool planRayTracing = rec.PlanPerStage;
				fresh = rec.PlanRayTracing;
				gameRecord.WarmedExeName = planEngine;
				gameRecord2.WarmedPerStage = planRayTracing;
				gameRecord20.WarmedRayTracing = fresh;
				gameRecord2 = rec;
				GameRecord gameRecord21 = rec;
				planEngine = agsUsed?.App;
				fresh = rec.AgsMissed && agsUsed == null;
				gameRecord2.WarmedAgsApp = planEngine;
				gameRecord21.AgsMissed = fresh;
				gameRecord2 = rec;
				GameRecord gameRecord22 = rec;
				planEngine = rec.PlanItems;
				gameRecord2.WarmedPlanItems = planEngine;
				gameRecord22.IdleHeldBack = null;
				rec.WarmedLayer = layer?.Fingerprint;
				gameRecord2 = rec;
				gameRecord = rec;
				GameRecord gameRecord23 = rec;
				DateTimeOffset? planBuiltAt = DateTimeOffset.Now;
				TimeSpan? lastWarmTime = TimeSpan.FromSeconds(seconds);
				long? lastCacheGrowthBytes = result.CacheGrowthBytes;
				gameRecord2.WarmedAt = planBuiltAt;
				gameRecord.LastWarmTime = lastWarmTime;
				gameRecord23.LastCacheGrowthBytes = lastCacheGrowthBytes;
				gameRecord = rec;
				gameRecord2 = rec;
				GameRecord gameRecord5 = rec;
				GameRecord gameRecord24 = rec;
				lastCacheGrowthBytes = result.Failed - unbuilt;
				long? lastWarmSkipped = result.Skipped;
				long? lastWarmNeedsRecording = needsRecording;
				HashSet<string> hashSet3 = new HashSet<string>();
				foreach (string item in cacheKeys)
				{
					hashSet3.Add(item);
				}
				HashSet<string> warmedKeys = hashSet3;
				gameRecord.LastWarmFailed = lastCacheGrowthBytes;
				gameRecord2.LastWarmSkipped = lastWarmSkipped;
				gameRecord5.LastWarmNeedsRecording = lastWarmNeedsRecording;
				gameRecord24.WarmedKeys = warmedKeys;
				rec.WarmedFiles = ((AppCache is AmdAppCache amdAppCache) ? amdAppCache.D3D12FileNames(cacheKeys) : AppCache?.FilesOf(cacheKeys).Select((FileInfo f) => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
				rec.WarmedFileBytes = AppCache?.FilesOf(cacheKeys).Where((FileInfo f) => rec.WarmedFiles.Contains(f.Name)).ToDictionary((FileInfo f) => f.Name, (FileInfo f) => f.Length, StringComparer.OrdinalIgnoreCase);
				GameRecord gameRecord25 = rec;
				IAppCache appCache = AppCache;
				AmdAppCache dxc = appCache as AmdAppCache;
				gameRecord25.WarmedFileIds = ((dxc != null) ? (from n in rec.WarmedFiles
					select (n: n, Id: AmdKept.Id(Path.Combine(dxc.DxcDir, n))) into x
					where x.Id != null
					select x).ToDictionary(((string n, string Id) x) => x.n, ((string n, string Id) x) => x.Id, StringComparer.OrdinalIgnoreCase) : null);
				KeepAside(id, rec, game.Name);
				gameRecord24 = rec;
				gameRecord5 = rec;
				GameRecord gameRecord26 = rec;
				lastWarmNeedsRecording = crashed;
				lastWarmSkipped = result.DriverSkipped;
				lastCacheGrowthBytes = unbuilt;
				gameRecord24.LastWarmCrashed = lastWarmNeedsRecording;
				gameRecord5.LastWarmDriverSkipped = lastWarmSkipped;
				gameRecord26.LastWarmUnbuilt = lastCacheGrowthBytes;
				try
				{
					List<string> failed = Directory.EnumerateFiles(work, "scskiller_failed.keys", SearchOption.AllDirectories).Append(stoppedFailed).ToList();
					string path = Path.Combine(Store.GameDir(id), "scskiller.warmed");
					AppStore.WriteAtomic(path, Recordings.Warmed(workDbs, failed, (compiled != null && File.Exists(path)) ? File.ReadAllBytes(path) : null));
					rec.KeysPending = true;
					IEnumerable<string> keys;
					if (options.StartAt <= 0)
					{
						keys = CompiledItems.Created(workDbs, failed).Union(compiled ?? new HashSet<string>());
					}
					else
					{
						IEnumerable<string> enumerable = compiled ?? new HashSet<string>();
						keys = enumerable;
					}
					AppStore.WriteAtomic(compiledFile, CompiledItems.Bytes(keys));
					lock (_lock)
					{
						_maxNext.Remove(id);
					}
					File.Delete(stoppedFailed);
				}
				catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException || ex2 is InvalidDataException) ? true : false)
				{
					Log?.Report(game.Name + ": couldn't note what the compile created: " + ex2.Message);
					try
					{
						File.Delete(compiledFile);
					}
					catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException) ? true : false)
					{
						Log?.Report($"{game.Name}: {"compiled.keys"} not removed: {ex3.Message}");
					}
				}
				IEnumerable<string> enumerable2 = inputs.Select(WarmInputs.Token);
				if (Vendor.Vendor == GpuVendor.Nvidia)
				{
					enumerable2 = enumerable2.Concat(WarmInputs.PlannedStages(rec.Plan.FilePath, inputs, CrashInputs(CrashKeysNow(rec), rec.Plan.FilePath).Contains));
				}
				rec.WarmKeysFile = KeyFiles.Write(Store.GameDir(id), "warm", enumerable2.Distinct());
				gameRecord5 = rec;
				gameRecord24 = rec;
				gameRecord2 = rec;
				GameRecord gameRecord27 = rec;
				planEngine = index.ContentHash;
				planApi = ExeStamp(game);
				string planRules = game.Version;
				string warmedIndexStamp = indexStamp;
				gameRecord5.WarmedIndexHash = planEngine;
				gameRecord24.WarmedExeStamp = planApi;
				gameRecord2.WarmedGameVersion = planRules;
				gameRecord27.WarmedIndexStamp = warmedIndexStamp;
				rec.CompileFailedBuild = null;
				gameRecord2 = rec;
				gameRecord24 = rec;
				gameRecord5 = rec;
				GameRecord gameRecord28 = rec;
				gameRecord2.ResumeAt = 0L;
				gameRecord24.ResumeItems = 0L;
				gameRecord5.ResumeSeconds = 0.0;
				gameRecord28.ResumeFailed = 0L;
				gameRecord5 = rec;
				GameRecord gameRecord29 = rec;
				bool warmedCareful = carefulPasses > 0;
				gameRecord5.WarmedCareful = warmedCareful;
				gameRecord29.FirstLaunch = null;
				if (flag4 && items >= 1000 && seconds > 1.0 && carefulPasses == 0)
				{
					rec.PsoPerSecond = (double)items / seconds;
					SeedWarmRate(rec.PsoPerSecond.Value);
					long num6 = result.Done - options.StartAt;
					if (result.CacheGrowthBytes > 0 && num6 > 0)
					{
						rec.BytesPerPso = (double)result.CacheGrowthBytes / (double)num6;
					}
				}
				Store.SaveGame(id, rec);
				Store.PruneKeyFiles(id);
				KeepRejectsLog(work, id, result.Failed);
				SendDiagnostics(game, index.ContentHash, work, null, result.Crashed);
				Close();
				Ended("done");
				Stage(QueueStage.Done);
				break;
			}
			case WarmOutcome.Stopped:
				KeepResume();
				Store.SaveGame(id, rec);
				Ended("stopped");
				Stage(QueueStage.Stopped);
				break;
			default:
				if (result.Resumable)
				{
					KeepResume();
				}
				Store.SaveGame(id, rec);
				SendDiagnostics(game, index.ContentHash, work, DiagnosticFailure.FromWarm(result.Error), result.Crashed);
				Ended("failed");
				Stage(QueueStage.Failed, result.Error + KeptLog(result.LogPath, id));
				break;
			}
			return;
			IL_120b:
			scsKiller.Set(new QueueItem(gameId, QueueStage.Done, null, null, note));
			Task Attribution(IWarmRun run, string exeFileName)
			{
				IAppCache cache = AppCache;
				if (cache != null)
				{
					return Task.Run(async () =>
					{
						while (!run.Completion.IsCompleted)
						{
							try
							{
								cacheKeys.UnionWith(cache.KeysOpenBy(exeFileName));
							}
							catch (Exception ex10) when ((ex10 is IOException || ex10 is UnauthorizedAccessException) ? true : false)
							{
								Log?.Report(game.Name + ": cache attribution: " + ex10.Message);
							}
							CacheSampled?.Invoke();
							await Task.WhenAny(run.Completion, Task.Delay((cacheKeys.Count > 0) ? AttributionInterval : Poll));
						}
					});
				}
				return Task.CompletedTask;
			}
			void KeepResume()
			{
				long done = result.Done;
				long resumeItems = items;
				double resumeSeconds = seconds;
				long failed2 = result.Failed;
				string resumeGpu = driverId;
				string resumeLayer = layer?.Fingerprint;
				string resumeExe = exe;
				string resumeAgs = ags2;
				rec.ResumeAt = done;
				rec.ResumeItems = resumeItems;
				rec.ResumeSeconds = resumeSeconds;
				rec.ResumeFailed = failed2;
				rec.ResumeGpu = resumeGpu;
				rec.ResumeLayer = resumeLayer;
				rec.ResumeExe = resumeExe;
				rec.ResumeAgs = resumeAgs;
				long resumeDriverSkipped = result.DriverSkipped;
				bool resumeLeftOut = compiled != null;
				rec.ResumeDriverSkipped = resumeDriverSkipped;
				rec.ResumeLeftOut = resumeLeftOut;
				KeepStoppedFailed();
			}
			void KeepStoppedFailed()
			{
				try
				{
					using FileStream fileStream = new FileStream(stoppedFailed, FileMode.Append);
					foreach (string item2 in Directory.EnumerateFiles(work, "scskiller_failed.keys", SearchOption.AllDirectories))
					{
						fileStream.Write(File.ReadAllBytes(item2));
					}
					foreach (string item3 in Directory.EnumerateFiles(work, "scskiller_failed.keys", SearchOption.AllDirectories))
					{
						File.Delete(item3);
					}
				}
				catch (Exception ex10) when ((ex10 is IOException || ex10 is UnauthorizedAccessException) ? true : false)
				{
					rec.ResumeAt = 0L;
					Log?.Report(game.Name + ": keeping what the stopped compile didn't create failed (" + ex10.Message + "): it starts over");
				}
			}
			Recording? Prepared()
			{
				if (prepared)
				{
					return recording;
				}
				if (Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any())
				{
					work = FreshWork(Store.GameDir(id), Log);
				}
				string store = RecordingPath(id);
				CancellationToken ct2 = ct;
				using (Recordings.Lock(store, null, ct2))
				{
					Recording recording2 = PrepareRecording(game, engine, index, work, rec.PlanRayTracing, Maximum(rec) | maxOnce, ct);
					recording = recording2;
					prepared = true;
					recorded = RecordedNow(game, rec, rec.PlanRayTracing, Maximum(rec) | maxOnce, engine);
					RecordingsRead?.Invoke();
					string text9 = CommunityObjects(id);
					string text10 = OfficialInUse(id)?.Object;
					preparedOfficial = text10;
					preparedCommunity = text9;
				}
				return recording;
			}
		}
		catch (OperationCanceledException)
		{
			Ended("cancelled");
			Stage(QueueStage.Stopped);
		}
		catch (Exception ex5)
		{
			Ended("failed");
			if (indexed != null)
			{
				SendDiagnostics(game, indexed, work, DiagnosticFailure.FromException(_stage, ex5), null);
			}
			Stage(QueueStage.Failed, (ex5 is NotReadyException) ? ex5.Message : CompileError(id, _stage, ex5));
		}
		finally
		{
			try
			{
				if (_warmer is Warmer warmer5)
				{
					warmer5.Ags = null;
				}
				KeepLastLog(work, id);
				try
				{
					if (Directory.Exists(work))
					{
						Directory.Delete(work, recursive: true);
					}
				}
				catch (Exception ex6) when ((ex6 is IOException || ex6 is UnauthorizedAccessException) ? true : false)
				{
					Log?.Report("could not delete " + work + ": " + ex6.Message);
				}
				try
				{
					foreach (string item4 in D3DSCache.Made(Path.Combine(LocalAppData, "D3DSCache"), work, started))
					{
						try
						{
							Directory.Delete(item4, recursive: true);
						}
						catch (Exception ex7) when ((ex7 is IOException || ex7 is UnauthorizedAccessException) ? true : false)
						{
							Log?.Report("could not delete " + item4 + ": " + ex7.Message);
						}
					}
				}
				catch (Exception ex8)
				{
					Log?.Report(game.Name + ": D3DSCache not cleaned: " + ex8.Message);
				}
			}
			finally
			{
				if (!refreshed)
				{
					Close();
				}
				StartSharing(new _003C_003Ez__ReadOnlySingleElementList<Game>(game));
				if (ManageRecorders && !ct.IsCancellationRequested)
				{
					try
					{
						ReconcileRecorders(id);
					}
					catch (Exception ex9)
					{
						Log?.Report(game.Name + ": reconciling the recorder failed: " + ex9.Message);
					}
				}
			}
		}
		void Close()
		{
			refreshed = true;
			try
			{
				Refresh(game, ct);
			}
			catch (Exception ex10) when ((ex10 is IOException || ex10 is UnauthorizedAccessException) ? true : false)
			{
				Log?.Report(game.Name + ": refreshing failed: " + ex10.Message);
			}
		}
		void Ended(string verb)
		{
			if (warmClock != null)
			{
				string value2 = Games.FirstOrDefault((GameState s) => s.Game.Id == id)?.Game.Name ?? id;
				string text9 = $"compile {value2}: {verb} {((verb == "done") ? "in" : "after")} {Took((ended != null) ? took : warmClock.Elapsed)}";
				if ((object)ended != null)
				{
					WarmResult warmResult2 = ended;
					text9 += $", {Math.Max(0L, warmResult2.Done - warmResult2.Failed - warmResult2.DriverSkipped - ((warmResult2.Outcome == WarmOutcome.Completed) ? crashed : 0)):N0} of {warmResult2.Total:N0} created, {warmResult2.Failed - unbuilt:N0} failed";
					if (warmResult2.Skipped > 0)
					{
						text9 += $", {warmResult2.Skipped:N0} skipped (a shader not in this install)";
					}
					if (crashed > 0)
					{
						text9 += $", {crashed:N0} skipped (crash the GPU driver)";
					}
					if (warmResult2.DriverSkipped > 0)
					{
						text9 += $", {warmResult2.DriverSkipped:N0} skipped for this compile (the GPU driver crashed or hung on them or on what they build on)";
					}
					if (unbuilt > 0)
					{
						text9 += $", {unbuilt:N0} skipped (SCSKiller can't build them yet)";
					}
				}
				if (rtSkipped)
				{
					text9 += ". Ray tracing skipped (your setting)";
				}
				warmClock = null;
				Log?.Report(text9);
			}
		}
		string? NotReady()
		{
			if (!(state == null) && !(installed == null))
			{
				if (!(now?.Engine == null) && now.Check.Readiness == Readiness.Ready)
				{
					ReShadeInstall reShadeInstall2 = BlockingMod(installed);
					if ((object)reShadeInstall2 == null)
					{
						string text9 = AppCacheFiles.MissingTarget(AppCache);
						if (text9 == null)
						{
							if (state.Status != GameStatus.Unsupported && (state.Status != GameStatus.NeedsRecording || NeedsRtRecording(state)))
							{
								return null;
							}
							return "not ready: " + state.StatusReason;
						}
						return "not ready: " + LinkTargetMissing(text9);
					}
					return "not ready: " + ShaderModReason(reShadeInstall2);
				}
				return "not ready: " + now?.Check.Reason;
			}
			return "unknown game (scan first)";
		}
		void Stage(QueueStage s, string? error = null)
		{
			if (s != QueueStage.Paused)
			{
				_stage = s;
			}
			bool flag5 = (uint)(s - 6) <= 2u;
			bool flag6 = !flag5 && (!_go.IsSet || _pauseWhy != null);
			if (flag6 || (progress?.Done ?? (-1)) != lastDone)
			{
				lastDone = progress?.Done ?? (-1);
				advanced.Restart();
			}
			string text9;
			if (flag6)
			{
				text9 = _pauseWhy;
			}
			else
			{
				flag5 = ((s == QueueStage.Done || s == QueueStage.Stopped) ? true : false);
				object obj;
				if (!flag5 || (object)progress == null)
				{
					if (s == QueueStage.Warming && advanced.Elapsed > StallAfter)
					{
						obj = StalledNote(advanced.Elapsed);
					}
					else
					{
						obj = ((s != QueueStage.Warming) ? null : progress?.Note);
					}
				}
				else
				{
					WarmProgress warmProgress = progress;
					obj = WarmCounts(warmProgress.Failed, warmProgress.Skipped, crashed, warmProgress.DriverSkipped, unbuilt);
				}
				text9 = (string)obj;
			}
			string note2 = text9;
			Set(new QueueItem(id, flag6 ? QueueStage.Paused : s, progress, error, note2));
			if (s == QueueStage.Failed)
			{
				NoteFailed(id, compiling);
			}
		}
	}

	private void NoteFailed(string id, Game? compiled)
	{
		bool flag;
		lock (_lock)
		{
			flag = _queue.FirstOrDefault((QueueItem q) => q.GameId == id)?.PlanCheck ?? false;
		}
		if (!flag)
		{
			Game game = compiled ?? Games.FirstOrDefault((GameState s) => s.Game.Id == id)?.Game;
			if ((object)game != null)
			{
				GameRecord gameRecord = Store.LoadGame(id);
				gameRecord.CompileFailedBuild = game.Version ?? "";
				Store.SaveGame(id, gameRecord);
			}
		}
	}

	private string CompileError(string id, QueueStage stage, Exception e)
	{
		while (e is AggregateException ex)
		{
			ReadOnlyCollection<Exception> innerExceptions = ex.InnerExceptions;
			if (innerExceptions == null || innerExceptions.Count != 1)
			{
				break;
			}
			e = ex.InnerExceptions[0];
		}
		string text = Codecs.Reason(e);
		if (text != null)
		{
			Log?.Report($"{id}: {stage} failed: {e}");
		}
		string text2 = text;
		if (text2 == null)
		{
			bool flag = (uint)(stage - 1) <= 3u;
			text2 = (flag ? $"{stage} failed: " : "") + e.Message;
		}
		string text3 = text2;
		string text4 = Path.Combine(Store.GameDir(id), "compile-error.log");
		try
		{
			AppStore.WriteAtomic(text4, Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:O}\r\nSCSKiller {AppVersion.Current}\r\nstage: {stage}\r\n\r\n{e}\r\n"));
		}
		catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
		{
			return text3;
		}
		return text ?? (text3 + " (log: " + text4 + ")");
	}

	private string KeptLog(string log, string id)
	{
		string text = Path.Combine(Store.GameDir(id), "warm-failed.log");
		try
		{
			string text2 = (File.Exists(log) ? log : Directory.EnumerateFiles(Path.GetDirectoryName(log), "scskiller.log", SearchOption.AllDirectories).FirstOrDefault());
			if (text2 == null)
			{
				return "";
			}
			File.Copy(text2, text, overwrite: true);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) ? true : false)
		{
			return "";
		}
		return " (log: " + text + ")";
	}

	private void SendDiagnostics(Game game, string contentHash, string work, DiagnosticFailure? failure, IReadOnlyCollection<string>? crashed)
	{
		Diagnostics diagnostics = Diagnostics;
		if (diagnostics == null || !Settings.ShareDiagnostics || contentHash.Length != 40)
		{
			return;
		}
		try
		{
			List<string> logLines = (Directory.Exists(work) ? Directory.EnumerateFiles(work, "scskiller.log", SearchOption.AllDirectories).SelectMany(File.ReadLines).ToList() : new List<string>());
			DiagnosticsReport report = DiagnosticsReport.Build(logLines, failure, crashed, contentHash, EngineLabel(game), ShareVendor(Vendor.Vendor), GpuModel(), DriverVersion(), AppVersion.Current.ToString());
			if ((object)report != null)
			{
				DiagnosticsSent = Task.Run(() => diagnostics.SendAsync(report));
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			Log?.Report(game.Name + ": reading the compile's diagnostics failed: " + ex.Message);
		}
	}

	private void KeepRejectsLog(string work, string id, long failed)
	{
		string path = Path.Combine(Store.GameDir(id), "warm-rejects.log");
		try
		{
			if (failed == 0L)
			{
				File.Delete(path);
				return;
			}
			List<string> list = Directory.EnumerateFiles(work, "scskiller.log", SearchOption.AllDirectories).OrderBy(File.GetLastWriteTimeUtc).ToList();
			if (list.Count > 0)
			{
				File.WriteAllLines(path, list.SelectMany(File.ReadLines));
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			Log?.Report("keeping the warm's rejects log failed: " + ex.Message);
		}
	}

	private string AllFailedReason(string work, long failed)
	{
		return AllFailedReason(WorkLogs(work), failed);
	}

	private static List<string> WorkLogs(string work)
	{
		try
		{
			return Directory.EnumerateFiles(work, "scskiller.log", SearchOption.AllDirectories).SelectMany(File.ReadLines).ToList();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return new List<string>();
		}
	}

	private static bool DriverFailed11(Match m)
	{
		if (!(m.Groups[1].Value == "batch") && !m.Groups[2].Value.StartsWith("the runtime rejects", StringComparison.Ordinal))
		{
			return m.Groups[2].Value == "exception";
		}
		return true;
	}

	public static long Unbuilt11(IEnumerable<string> log)
	{
		return (from l in log
			select FailTally11.Match(l) into m
			where m.Success && !DriverFailed11(m)
			select m).Sum((Match m) => long.Parse(m.Groups[3].Value));
	}

	public static string AllFailedReason(IEnumerable<string> log, long failed)
	{
		List<(int, long)> list = (from l in log
			select (d3d12: FailTally.Match(l), d3d11: FailTally11.Match(l)) into m
			where m.d3d12.Success || m.d3d11.Success
			select m).Select(((Match d3d12, Match d3d11) m) =>
		{
			var (match, _) = m;
			return (match == null || !match.Success) ? (kind: DriverFailed11(m.d3d11) ? 1 : 3, n: long.Parse(m.d3d11.Groups[3].Value)) : (kind: (!match.Groups[2].Value.Contains("root signature creation failed")) ? (match.Groups[2].Value.Contains("hr=0x") ? 1 : 2) : 0, n: long.Parse(match.Groups[1].Value));
		}).GroupBy(((int kind, long n) t) => t.kind, (int kind, IEnumerable<(int kind, long n)> ts) => (kind: kind, n: ts.Sum(((int kind, long n) t) => t.n))).ToList();
		string text = $"all {failed:N0} pipelines failed";
		if (list.Count == 0)
		{
			return text;
		}
		int item = list.MaxBy(((int kind, long n) r) => r.n).Item1;
		if (list.Count == 1)
		{
			return item switch
			{
				0 => $"the driver refused the root signature of all {failed:N0} pipelines", 
				1 => $"the driver rejected all {failed:N0} pipelines", 
				3 => $"SCSKiller can't build any of the {failed:N0} pipelines yet", 
				_ => $"SCSKiller couldn't read any of the {failed:N0} pipelines", 
			};
		}
		string text2 = text;
		return text2 + item switch
		{
			0 => ", most because the driver refused their root signature", 
			1 => ", most rejected by the driver", 
			3 => ", most because SCSKiller can't build them yet", 
			_ => ", most because SCSKiller couldn't read them", 
		};
	}

	private void KeepLastLog(string work, string id)
	{
		try
		{
			if (!Directory.Exists(work))
			{
				return;
			}
			List<string> list = Directory.EnumerateFiles(work, "scskiller.log", SearchOption.AllDirectories).OrderBy(File.GetLastWriteTimeUtc).ToList();
			if (list.Count == 0)
			{
				return;
			}
			string path = Path.Combine(work, "warm-last.log");
			using (FileStream destination = File.Create(path))
			{
				foreach (string item in list)
				{
					using FileStream fileStream = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
					fileStream.CopyTo(destination);
				}
			}
			using FileStream log = File.OpenRead(path);
			AppStore.WriteAtomic(Path.Combine(Store.GameDir(id), "warm-last.log"), Bounded(log, 4194304));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			Log?.Report("keeping the last warm's log failed: " + ex.Message);
		}
	}

	internal static string FreshWork(string gameDir, IProgress<string>? log)
	{
		string text = null;
		Exception innerException = null;
		for (int i = 0; i < 10; i++)
		{
			string text2 = Path.Combine(gameDir, (i == 0) ? "work" : $"work-{i}");
			bool flag = false;
			while (true)
			{
				try
				{
					if (Directory.Exists(text2))
					{
						Directory.Delete(text2, recursive: true);
					}
					if (text == null)
					{
						Directory.CreateDirectory(text2);
						text = text2;
					}
				}
				catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
					if (!flag)
					{
						Thread.Sleep(500);
						goto IL_00c6;
					}
					log?.Report("could not delete " + text2 + ": " + ex.Message);
					innerException = ex;
				}
				break;
				IL_00c6:
				flag = true;
			}
		}
		return text ?? throw new IOException("no work folder can be made in " + gameDir, innerException);
	}

	private static bool IsWorkDir(string name)
	{
		if (!name.Equals("work", StringComparison.OrdinalIgnoreCase))
		{
			if (name.StartsWith("work-", StringComparison.OrdinalIgnoreCase) && name.Length > 5)
			{
				return name.Substring(5).All(char.IsAsciiDigit);
			}
			return false;
		}
		return true;
	}

	internal static byte[] Bounded(Stream log, int max)
	{
		long length = log.Length;
		int num3;
		int num4;
		if (length > max)
		{
			int num = max / 4;
			int num2 = max - max / 4;
			num3 = num2;
			num4 = num;
		}
		else
		{
			int num5 = (int)length;
			num3 = 0;
			num4 = num5;
		}
		byte[] array = ((length <= max) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes($"\r\n[... {length - num4 - num3:N0} bytes left out ...]\r\n"));
		byte[] array2 = new byte[num4 + array.Length + num3];
		log.Position = 0L;
		log.ReadExactly(array2, 0, num4);
		array.CopyTo(array2, num4);
		log.Position = length - num3;
		log.ReadExactly(array2, num4 + array.Length, num3);
		return array2;
	}

	internal string? FullReplayReason(string gameId)
	{
		GameState gameState = Find(gameId);
		return FullReplayReason(gameState.Game, Store.LoadGame(gameId), gameState.Engine, Snapshot());
	}

	private string? FullReplayReason(Game g, GameRecord r, EngineInfo? e, GpuSnapshot snap)
	{
		if (ColdWarm(r, snap.Id))
		{
			return "nothing compiled on this driver is on record";
		}
		string text = WarmChanged(g, r, e, snap);
		if (text != null)
		{
			return text;
		}
		if (r.PlanPerStage != r.WarmedPerStage)
		{
			return "the plan settings changed";
		}
		if (r.Careful || r.WarmedCareful)
		{
			return "a careful compile";
		}
		if (IsPartlyWarmed(r.FirstLaunch) || IsUnreached(r))
		{
			return "the game's launch after the last compile still compiled";
		}
		string text2 = CacheFilesChanged(g.Id, r);
		if (text2 != null)
		{
			return text2;
		}
		return null;
	}

	private string? CacheFilesChanged(string gameId, GameRecord r)
	{
		IAppCache appCache = AppCache;
		if (appCache != null)
		{
			HashSet<string> warmedKeys = r.WarmedKeys;
			if (warmedKeys != null && warmedKeys.Count > 0)
			{
				HashSet<string> files = r.WarmedFiles;
				if (files != null && files.Count > 0)
				{
					Dictionary<string, long> bytes = r.WarmedFileBytes;
					if (bytes != null)
					{
						List<FileInfo> now = (from f in appCache.FilesOf(warmedKeys)
							where files.Contains(f.Name)
							select f).ToList();
						AmdKept kept = ((AppCache is AmdAppCache) ? Keeping(gameId) : null);
						if (files.Any((string n) => !now.Any((FileInfo f) => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) && !KeptSame(kept, r, n)))
						{
							return "some of the game's cache files are gone";
						}
						if (now.Any((FileInfo f) => MadeAgain(f) || f.Length < bytes.GetValueOrDefault(f.Name)))
						{
							return "some of the game's cache files were made again since the last compile";
						}
						return null;
					}
				}
			}
		}
		return "the game's cache files aren't known";
		bool MadeAgain(FileInfo f)
		{
			string text = r.WarmedFileIds?.GetValueOrDefault(f.Name);
			if (text == null)
			{
				DateTimeOffset? warmedAt = r.WarmedAt;
				if (warmedAt.HasValue)
				{
					DateTimeOffset valueOrDefault = warmedAt.GetValueOrDefault();
					return f.CreationTimeUtc > valueOrDefault.UtcDateTime;
				}
				return false;
			}
			return AmdKept.Id(f.FullName) != text;
		}
	}

	private bool PlanIsStale(string id, GameRecord r, EngineInfo? e, bool maxOnce = false)
	{
		Plan plan = r.Plan;
		if ((object)plan != null && File.Exists(plan.FilePath) && !(plan.IndexContentHash != r.IndexContentHash) && !(plan.VendorProfile != Vendor.Caps.Profile) && !(r.RecordingImportedAt > r.PlanBuiltAt) && CurrentPlanner(r.PlanVersion, r.PlanRules, r.PlanReaderVersion, e) && r.PlanPerStage == PerStagePlans(r, maxOnce) && !Uncapped(r, maxOnce) && !OverCacheBudget(Vendor.Caps, r.Plan?.Stats) && !(r.PlanCommunity != CommunityObjects(id)) && !(r.PlanOfficial != OfficialInUse(id)?.Object) && !RtPlanCheck(r, e) && !OtherApi(r, e) && !OtherEngine(r, e) && r.PlanRayTracing == RayTracing(r))
		{
			return r.PlanFiltering != Filtering(id, every: false, e);
		}
		return true;
	}

	private bool PerStagePlans(GameRecord r, bool maxOnce = false)
	{
		if (Vendor.Caps.PerStageCache && !Maximum(r))
		{
			return !maxOnce;
		}
		return false;
	}

	private bool Uncapped(GameRecord r, bool maxOnce = false)
	{
		if (Maximum(r) | maxOnce)
		{
			VendorCaps caps = Vendor.Caps;
			if ((object)caps != null && caps.PerStageCache && !caps.StateIndependentCache)
			{
				return r.PlanMaximumCapped != true;
			}
		}
		return false;
	}

	internal static bool OverCacheBudget(VendorCaps caps, PlanStats? p)
	{
		if ((object)caps != null && caps.PerStageCache && !caps.StateIndependentCache && (object)p != null && !p.CacheBudgetLeftOut.HasValue)
		{
			return p.Recorded + p.Generated + p.Variants > PlanBuilder.CacheBudgetOf(caps) / 28672;
		}
		return false;
	}

	private bool Maximum(GameRecord r)
	{
		if (!Settings.MaximumPlans)
		{
			return r.Maximum == true;
		}
		return true;
	}

	private bool RayTracing(GameRecord r)
	{
		return r.RayTracing ?? Settings.CompileRayTracing;
	}

	public void SetGameRayTracing(string gameId, bool? on)
	{
		GameState gameState = Find(gameId);
		lock (_lock)
		{
			if (_current == gameId)
			{
				throw new InvalidOperationException("a compile of " + gameState.Game.Name + " is in progress");
			}
		}
		GameRecord gameRecord = Store.LoadGame(gameId);
		if (gameRecord.RayTracing != on)
		{
			gameRecord.RayTracing = on;
			Store.SaveGame(gameId, gameRecord);
			Refresh(gameState.Game);
		}
	}

	public void SetGameLeftOutOfAddAll(string gameId, bool on)
	{
		GameState gameState = Find(gameId);
		GameRecord gameRecord = Store.LoadGame(gameId);
		if (gameRecord.LeftOutOfAddAll != on)
		{
			gameRecord.LeftOutOfAddAll = on;
			Store.SaveGame(gameId, gameRecord);
			Refresh(gameState.Game);
		}
	}

	public void SetGameMaximum(string gameId, bool on)
	{
		GameState gameState = Find(gameId);
		lock (_lock)
		{
			if (_current == gameId)
			{
				throw new InvalidOperationException("a compile of " + gameState.Game.Name + " is in progress");
			}
		}
		GameRecord gameRecord = Store.LoadGame(gameId);
		if (gameRecord.Maximum == true != on)
		{
			GameRecord gameRecord2 = gameRecord;
			bool? maximum = (on ? new bool?(true) : ((bool?)null));
			gameRecord.Maximum = maximum;
			gameRecord2.ResumeAt = 0L;
			Store.SaveGame(gameId, gameRecord);
			Refresh(gameState.Game);
			KeepList();
		}
	}

	public CompatSent? ReportSent(string gameId)
	{
		return CompatSent.For(Store.GameDir(gameId), CompatReport.BuildKey(Find(gameId).Game));
	}

	public async Task<string?> SendReportAsync(string gameId, CompatChoice choice, bool frameGen, bool upscaler, bool mods, CancellationToken ct = default(CancellationToken))
	{
		Game listed = Find(gameId).Game;
		Game game = await Task.Run(() => Discovered(listed), ct);
		if ((object)game == null)
		{
			throw new InvalidOperationException("SCSKiller can't tell which version of this game is installed.");
		}
		if (game.Version != listed.Version)
		{
			Refresh(game);
		}
		GameState gameState = Find(gameId);
		string text = PackGpu(Vendor.Vendor);
		string text2 = CompatReport.WhyNot(gameState, text);
		if (text2 != null)
		{
			throw new InvalidOperationException(text2);
		}
		Sharing? sharing = Sharing ?? throw new InvalidOperationException("reports aren't sent from here");
		string key = CompatReport.BuildKey(gameState.Game);
		CompatReport report = new CompatReport(gameState.Game.Id, key, choice, frameGen, upscaler, mods, CpuModel(), GpuModel(), text, DriverVersion(), CompatReport.ThisWindows, AppVersion.Current.ToString(), CompatReport.CompileOf(gameState, Settings.MaximumPlans));
		string text3 = await sharing.SendCompatAsync(report, ct);
		if (text3 != null)
		{
			return text3;
		}
		CompatSent.Save(Store.GameDir(gameId), key, new CompatSent(choice, DateTimeOffset.UtcNow));
		return null;
	}

	public string ProblemReport(string gameId, string store, IReadOnlyList<(string Label, string Value)> page, bool withLog)
	{
		Game game = Find(gameId).Game;
		IReadOnlyList<string> log = (withLog ? SCSKiller.Core.App.ProblemReport.LogLines(Store.DataDir, new _003C_003Ez__ReadOnlyArray<string>(new string[4]
		{
			game.Name,
			game.Id,
			Path.GetFileName(Store.GameDir(game.Id)),
			Path.GetFileNameWithoutExtension(game.ExePath)
		})) : null);
		return SCSKiller.Core.App.ProblemReport.Build(AppVersion.Current.ToString(), game.Name, store, ShareVendor(Vendor.Vendor), GpuModel(), DriverVersion(), CompatReport.ThisWindows, page, log, SCSKiller.Core.App.ProblemReport.Masker.ThisPc);
	}

	public Task<(string? Id, string? Problem)> SendProblemAsync(string message, string report, CancellationToken ct = default(CancellationToken))
	{
		return SCSKiller.Core.App.ProblemReport.SendAsync(message, report, ct);
	}

	private static string? CpuModel()
	{
		using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0");
		string text = (registryKey?.GetValue("ProcessorNameString") as string)?.Trim();
		return (text != null && text.Length > 0) ? text.Substring(0, Math.Min(text.Length, 100)) : null;
	}

	private long? MaximumAdds(PlanStats? plan)
	{
		VendorCaps caps = Vendor.Caps;
		if ((object)caps == null || !caps.PerStageCache || caps.StateIndependentCache || (object)plan == null || plan.Generated <= 0)
		{
			return null;
		}
		return (long)PlanBuilder.MaximumExtras(Vendor.Caps, plan.Recorded + plan.Generated) * 22528L;
	}

	private MaximumMode.Suggestion? MaximumSuggestion(Game g, GameRecord r)
	{
		if (!Maximum(r))
		{
			return MaximumMode.Current.For(g, PackGpu(Vendor.Vendor));
		}
		return null;
	}

	private async Task<(WarmResult Result, bool Yielded)> Watch(IWarmRun run, string id, string exe, string name)
	{
		string applied = "";
		bool yielded = false;
		while (await Task.WhenAny(run.Completion, Task.Delay(Poll)) != run.Completion)
		{
			if (yielded)
			{
				continue;
			}
			IReadOnlySet<string> readOnlySet = Running();
			if (readOnlySet.Contains(exe))
			{
				yielded = true;
				_ownGameSeen[exe] = Stopwatch.GetTimestamp();
				_pauseWhy = StoppedFor(name, exe);
				SetCurrent(QueueStage.Paused, _pauseWhy);
				run.Stop();
				continue;
			}
			bool flag;
			lock (_lock)
			{
				flag = Background || _whenIdle.Contains(id);
			}
			string text = ((flag && Settings.PauseWhileGaming) ? GameNameIn(readOnlySet) : null);
			ref string? pauseWhy = ref _pauseWhy;
			string? text2;
			if (text != null)
			{
				text2 = "paused while " + text + " is running";
			}
			else
			{
				text2 = (WaitsForIdle(id) ? "paused until the PC is idle" : null);
			}
			pauseWhy = text2;
			string text3 = (_go.IsSet ? _pauseWhy : (_pauseWhy ?? "paused"));
			if (!(text3 == applied))
			{
				applied = text3;
				if (text3 == null)
				{
					run.Resume();
					SetCurrent(QueueStage.Warming);
				}
				else
				{
					run.Pause();
					SetCurrent(QueueStage.Paused, _pauseWhy);
				}
			}
		}
		_pauseWhy = null;
		return (Result: await run.Completion, Yielded: yielded);
	}

	private string StoppedFor(string name, string exe)
	{
		List<(int Pid, int Parent, string Exe)> all = Processes(arg: true);
		List<string> list = all.Where(((int Pid, int Parent, string Exe) p) => p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase) && !OurCopy(p, all)).Take(3).Select(((int Pid, int Parent, string Exe) p) =>
		{
			string text = ProcessPath(p.Pid);
			return (text != null) ? $"process {p.Pid}: {text}" : $"process {p.Pid}";
		})
			.ToList();
		return "stopped while " + name + " is running: continues when it exits" + ((list.Count > 0) ? (" (" + string.Join(", ", list) + ")") : "");
	}

	private async Task WhileGameRuns(string exe, string name, CancellationToken ct, Action? learn = null)
	{
		bool runs = false;
		if (!Waits())
		{
			return;
		}
		try
		{
			_pauseWhy = StoppedFor(name, exe);
			SetCurrent(QueueStage.Paused, _pauseWhy);
			Stopwatch sampled = null;
			bool shown = true;
			while (Waits())
			{
				if (runs != shown)
				{
					ScsKiller scsKiller = this;
					bool flag;
					shown = (flag = runs);
					scsKiller._pauseWhy = (flag ? StoppedFor(name, exe) : $"continues {GameClosedFor.TotalMinutes:0} min after {name} closes");
					SetCurrent(QueueStage.Paused, _pauseWhy);
				}
				if (runs && learn != null && (sampled == null || sampled.Elapsed >= AttributionInterval))
				{
					sampled = Stopwatch.StartNew();
					learn();
				}
				await Task.Delay(Poll, ct);
			}
		}
		finally
		{
			_pauseWhy = null;
			SetCurrent(_go.IsSet ? _stage : QueueStage.Paused);
		}
		bool Waits()
		{
			if (runs = Running().Contains(exe))
			{
				_ownGameSeen[exe] = Stopwatch.GetTimestamp();
			}
			if (!runs)
			{
				if (_ownGameSeen.TryGetValue(exe, out var value))
				{
					return Stopwatch.GetElapsedTime(value) < GameClosedFor;
				}
				return false;
			}
			return true;
		}
	}

	private IReadOnlySet<string> Running()
	{
		IReadOnlySet<string> readOnlySet = RunningGameExes();
		foreach (string name in readOnlySet)
		{
			if (Games.Any((GameState s) =>
			{
				string fileName = Path.GetFileName(s.Game.ExePath);
				return fileName != name && fileName.Equals(name, StringComparison.OrdinalIgnoreCase);
			}))
			{
				_launched.AddOrUpdate(name, (string n) => new LaunchedExe(n, DateTimeOffset.Now), (string n, LaunchedExe old) => (!(old.Name == n)) ? new LaunchedExe(n, DateTimeOffset.Now) : old);
			}
		}
		return readOnlySet;
	}

	private IReadOnlySet<string> DiscoveredGamesRunning()
	{
		Dictionary<string, string> disk = Games.Select((GameState s) => Path.GetFileName(s.Game.ExePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary((string n) => n, StringComparer.OrdinalIgnoreCase);
		List<(int Pid, int Parent, string Exe)> all = Processes(arg: true);
		return (from p in all
			where disk.ContainsKey(p.Exe) && !OurCopy(p, all)
			select disk[p.Exe]).ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	private string? GameNameIn(IReadOnlySet<string> runningExes)
	{
		return Games.FirstOrDefault((GameState s) => runningExes.Contains(Path.GetFileName(s.Game.ExePath)))?.Game.Name;
	}

	public bool IsPlaying(string gameId)
	{
		return _playing.Contains(gameId);
	}

	public bool GameRunning()
	{
		if (Volatile.Read(in _listed) != 0L && RunningGameExes().Count <= 0)
		{
			return _playing.Count > 0;
		}
		return true;
	}

	private void RequestCollect()
	{
		_collect = true;
	}

	internal static bool CollectNow(bool pending, bool compiling, bool unseen, TimeSpan userIdle)
	{
		if (pending && !compiling)
		{
			if (!unseen)
			{
				return userIdle >= CollectAfterIdle;
			}
			return true;
		}
		return false;
	}

	public Task WatchGames(CancellationToken ct)
	{
		return Task.Run(async () =>
		{
			Stopwatch gpuChecked = Stopwatch.StartNew();
			Stopwatch recordersChecked = null;
			Task check = null;
			bool wasUnseen = false;
			while (!ct.IsCancellationRequested)
			{
				try
				{
					PollGames();
				}
				catch (Exception ex)
				{
					Log?.Report("watching games: " + ex.Message);
				}
				if ((check == null || check.IsCompleted) && (recordersChecked?.Elapsed >= RecorderCheckInterval || (recordersChecked == null && Volatile.Read(in _listed) > 0)))
				{
					recordersChecked = Stopwatch.StartNew();
					check = Task.Run((Func<Task<List<Task>>?>)WalkRecorderGames);
					check.ContinueWith((Task t) =>
					{
						Log?.Report("checking recorder games for anti-cheat: " + t.Exception.InnerException?.Message);
					}, TaskContinuationOptions.OnlyOnFaulted);
				}
				if (gpuChecked.Elapsed >= GpuCheckInterval)
				{
					RedetectGpu();
					gpuChecked.Restart();
				}
				bool flag;
				lock (_lock)
				{
					flag = _current != null;
				}
				if (!flag && KeyFiles.DropIdle(KeyCacheIdle))
				{
					RequestCollect();
				}
				bool flag2 = Unseen();
				if (flag2 && !wasUnseen)
				{
					RequestCollect();
				}
				wasUnseen = flag2;
				if (CollectNow(_collect, flag, flag2, IdleTime()))
				{
					_collect = false;
					GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
					if (flag2)
					{
						TrimWorkingSet();
					}
				}
				try
				{
					bool busy = _playing.Count > 0 || _toFollow.Count > 0 || flag || AnyCompileRunning();
					await Task.Delay(busy ? WatchInterval : IdleWatchInterval, ct);
				}
				catch (OperationCanceledException)
				{
				}
			}
			if (check != null)
			{
				try
				{
					await check;
				}
				catch (Exception)
				{
				}
			}
		});
	}

	public async Task CheckRecorderGames()
	{
		await Task.WhenAll(await WalkRecorderGames());
	}

	// Exo Shaders: the 2-minute walk re-read every recorder game's folders for
	// anti-cheat although nothing had changed. A game whose exe and top-level
	// folders are as they were at its last clean walk is skipped; an update
	// (new exe, new files beside it) changes the key and walks it again.
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _cleanWalks = new();

	private static string? WalkKey(Game g)
	{
		string? exe = Stamp(g.ExePath);
		string? folder = FolderStamp(g);
		return exe == null || folder == null ? null : exe + "|" + folder;
	}

	private bool CleanWalkUnchanged(Game g)
	{
		return _cleanWalks.TryGetValue(g.Id, out var key) && WalkKey(g) == key;
	}

	internal async Task<List<Task>> WalkRecorderGames()
	{
		RetryIndexes();
		List<Task> removals = new List<Task> { Off(CleanOfflineSessions, "cleaning up after offline sessions") };
		IReadOnlyList<GameState> states = Games;
		HashSet<string> vac = (from gameState in states
			where VacCheck(gameState.Game)
			select gameState.Game.Id).ToHashSet();
		List<(Game Game, string Stamp, long Gen, Task<AntiCheat> Walk)> pending = (from gameState in states
			where gameState.AntiCheat == AntiCheat.None && !IsUnchecked(gameState) && !vac.Contains(gameState.Game.Id) && Collections.RecordedBy(gameState.Game.Id) == null && RecorderIn(gameState)
			select ArmTarget(gameState.Game) into g
			where !CleanWalkUnchanged(g)
			select (Game: g, Stamp: Stamp(g.ExePath), Gen: RemovalGen(g.Id)) into c
			select (Game: c.Game, Stamp: c.Stamp, Gen: c.Gen, Walk: Task.Run(() => LowIo(() => FullCheck(c.Game))))).ToList();
		Task deadline = Task.Delay(RecorderCheckTimeout);
		while (pending.Count > 0)
		{
			foreach (var p in (await Task.WhenAny(pending.Select(((Game Game, string Stamp, long Gen, Task<AntiCheat> Walk) tuple) => tuple.Walk).Append(deadline)) == deadline) ? pending.ToList() : pending.Where(((Game Game, string Stamp, long Gen, Task<AntiCheat> Walk) tuple) => tuple.Walk.IsCompleted).ToList())
			{
				pending.Remove(p);
				bool unfinished = !p.Walk.IsCompletedSuccessfully;
				AntiCheat found = (unfinished ? AntiCheat.Other : p.Walk.Result);
				if (found == AntiCheat.None)
				{
					if (WalkKey(p.Game) is { } key)
					{
						_cleanWalks[p.Game.Id] = key;
					}
					Restamp(p.Game, p.Stamp, p.Gen);
					continue;
				}
				_cleanWalks.TryRemove(p.Game.Id, out _);
				removals.Add(Off(() =>
				{
					AntiCheatFound(p.Game, found, null, null, unfinished);
					Refresh(p.Game);
				}, p.Game.Name + ": removing the recorder"));
			}
		}
		foreach (GameState s in states.Where((GameState gameState) => gameState.AntiCheat != AntiCheat.None || vac.Contains(gameState.Game.Id)))
		{
			if (_removalTried.TryGetValue(s.Game.Id, out Stopwatch value) && value.Elapsed < RemovalRetryInterval)
			{
				continue;
			}
			GameRecord gameRecord = Store.LoadGame(s.Game.Id);
			if (gameRecord.RecorderFiles.Count != 0 || !(gameRecord.RecorderChained == null) || ProxyOnDisk(s.Game) || ProxyOnDisk(RecordedAt(s.Game, gameRecord)) || new string[4]
			{
				s.Game.ExePath,
				gameRecord.RecorderExe,
				gameRecord.RecorderMoveFrom,
				gameRecord.RecorderMoveTo
			}.OfType<string>().Any((string e) => IniPending(Store, Path.GetDirectoryName(e))))
			{
				_removalTried[s.Game.Id] = Stopwatch.StartNew();
				removals.Add(Off(() =>
				{
					TakeOutNow(s.Game, (s.AntiCheat != AntiCheat.None) ? $"{s.AntiCheat} found" : "VAC found");
					Refresh(s.Game);
				}, s.Game.Name + ": removing the recorder failed"));
			}
		}
		return removals;
		Task Off(Action work, string what)
		{
			return Task.Run(() =>
			{
				try
				{
					work();
				}
				catch (Exception ex)
				{
					Log?.Report(what + ": " + ex.Message);
				}
			});
		}
	}

	private bool RecorderIn(GameState s)
	{
		if (s.RecorderInstalled)
		{
			return true;
		}
		try
		{
			GameRecord gameRecord = Store.LoadGame(s.Game.Id);
			return gameRecord.RecorderFiles.Count > 0 || gameRecord.RecorderChained != null || gameRecord.RecorderExe != null;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
		{
			return true;
		}
	}

	private void Restamp(Game g, string? stamp, long gen)
	{
		if (!ManageRecorders || stamp == null || !Monitor.TryEnter(_recorderLock))
		{
			return;
		}
		try
		{
			if (!OfflineLive(g.Id) && GameFolderWrite(g) && !Attested(g))
			{
				string text = IniStamp(g.ExePath);
				if (Attest(g, Store.LoadGame(g.Id), stamp, gen) && text != null && text.Length > 0)
				{
					RecorderLog(g.Name + ": the recorder's scskiller.ini didn't match " + Path.GetFileName(g.ExePath) + " (the game changed): checked again");
				}
			}
		}
		finally
		{
			Monitor.Exit(_recorderLock);
		}
	}

	private static bool DataFile(string name)
	{
		string extension = Path.GetExtension(name);
		if (GameFiles.Marker(name) == AntiCheat.None)
		{
			if (!DataTypes.Contains(extension))
			{
				if (extension.Length == 5 && extension.StartsWith(".log", StringComparison.OrdinalIgnoreCase))
				{
					char c = extension[4];
					if (c >= '1' && c <= '9')
					{
						goto IL_0052;
					}
				}
				return name.Equals("uplay_install.state", StringComparison.OrdinalIgnoreCase);
			}
			goto IL_0052;
		}
		return false;
		IL_0052:
		return true;
	}

	private static bool SameDir(string a, string b)
	{
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
	}

	internal static bool Foreign(string dir)
	{
		try
		{
			string path = Path.Combine(dir, "scskiller.ledger");
			if (File.Exists(path))
			{
				return !SameDir(File.ReadAllText(path).Trim(), LedgerDir);
			}
			return !DefaultCopy && (RecorderOwnFiles.Any((string f) => ((!(f == "d3d12.dll") && !(f == "d3d11.dll")) || 1 == 0) && File.Exists(Path.Combine(dir, f))) || OurProxyIn(dir));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) ? true : false)
		{
			return true;
		}
	}

	private static bool Foreign(Game g)
	{
		return Foreign(Path.GetDirectoryName(g.ExePath));
	}

	private static void StampLedger(string dir, bool replace = true)
	{
		string path = Path.Combine(dir, "scskiller.ledger");
		if (DefaultCopy)
		{
			File.Delete(path);
			return;
		}
		Place(path, (string temp) =>
		{
			File.WriteAllText(temp, Path.GetFullPath(LedgerDir));
		}, replace);
	}

	private static void Unstamp(string dir)
	{
		using SafeFileHandle safeFileHandle = CreateFileW(Path.Combine(dir, "scskiller.ledger"), 2147549184u, 0u, 0, 3u, 0u, 0);
		if (safeFileHandle.IsInvalid)
		{
			int lastWin32Error = Marshal.GetLastWin32Error();
			if ((uint)(lastWin32Error - 2) > 1u)
			{
				throw new IOException("couldn't open the scskiller.ledger in " + dir, Marshal.GetHRForLastWin32Error());
			}
			return;
		}
		long length = RandomAccess.GetLength(safeFileHandle);
		if (length > 131072)
		{
			return;
		}
		byte[] array = new byte[length];
		int num;
		for (int i = 0; i < array.Length; i += num)
		{
			if ((num = RandomAccess.Read(safeFileHandle, array.AsSpan(i), i)) == 0)
			{
				return;
			}
		}
		string a = Encoding.UTF8.GetString(array).Trim();
		try
		{
			if (!SameDir(a, LedgerDir))
			{
				return;
			}
		}
		catch (Exception ex) when ((ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) ? true : false)
		{
			return;
		}
		byte info = 1;
		if (SetFileInformationByHandle(safeFileHandle, 4, ref info, 1))
		{
			return;
		}
		throw new IOException("couldn't delete the scskiller.ledger in " + dir, Marshal.GetHRForLastWin32Error());
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte info, int size);

	internal static string? Stamp(string exe)
	{
		try
		{
			FileInfo fileInfo = new FileInfo(exe);
			return $"exe_size={fileInfo.Length}\r\nexe_time={fileInfo.LastWriteTimeUtc.ToFileTimeUtc()}\r\n";
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	private static string? IniStamp(string exe)
	{
		try
		{
			return string.Concat(from l in File.ReadAllLines(Path.Combine(Path.GetDirectoryName(exe), "scskiller.ini")).Where((string l) =>
				{
					string text = IniKey(l);
					return text != null && StampKeys.Contains(text);
				})
				select l + "\r\n");
		}
		catch (Exception ex) when ((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? true : false)
		{
			return "";
		}
		catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	private Game ArmTarget(Game g)
	{
		string text = CollectionArmExe(g);
		if (text == null)
		{
			return g;
		}
		return g with
		{
			ExePath = text
		};
	}

	internal string? CollectionArmExe(Game first)
	{
		(IReadOnlyList<CollectionGame>, CollectionGame)? tuple = Collections.Member(first.Id);
		if (tuple.HasValue)
		{
			(IReadOnlyList<CollectionGame>, CollectionGame) valueOrDefault = tuple.GetValueOrDefault();
			if (!(valueOrDefault.Item1[0].Id != first.Id))
			{
				IReadOnlyList<GameState> games = Games;
				string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first.InstallDir));
				if (valueOrDefault.Item1.Any((CollectionGame collectionGame) => _playing.Contains(collectionGame.Id)))
				{
					List<string> source = (from x in valueOrDefault.Item1.SelectMany((CollectionGame collectionGame) => collectionGame.Exes)
						select Path.Combine(root, x)).ToList();
					string text = source.FirstOrDefault((string x) => Attested(first with
					{
						ExePath = x
					})) ?? source.FirstOrDefault((string x) => x.Equals(_collectionArmed.GetValueOrDefault(first.Id), StringComparison.OrdinalIgnoreCase));
					if (text != null)
					{
						return text;
					}
				}
				(string, DateTimeOffset)? tuple2 = null;
				foreach (CollectionGame e in valueOrDefault.Item1)
				{
					Game game = games.FirstOrDefault((GameState s) => s.Game.Id == e.Id)?.Game;
					if ((object)game != null && GameFiles.DirKey(game.InstallDir).Equals(GameFiles.DirKey(first.InstallDir), StringComparison.OrdinalIgnoreCase) && e.Exes.Contains(Path.GetFileName(game.ExePath), StringComparer.OrdinalIgnoreCase) && Path.GetDirectoryName(Path.GetFullPath(game.ExePath)).Equals(root, StringComparison.OrdinalIgnoreCase))
					{
						GameRecord gameRecord = Store.LoadGame(e.Id);
						DateTimeOffset dateTimeOffset = new DateTimeOffset?[3]
						{
							gameRecord.LastPlay?.To,
							gameRecord.RunsExeAt,
							_endedAt.GetValueOrDefault(e.Id)
						}.Max() ?? DateTimeOffset.MinValue;
						if (!tuple2.HasValue || dateTimeOffset > tuple2.Value.Item2)
						{
							tuple2 = (game.ExePath, dateTimeOffset);
						}
					}
				}
				return tuple2?.Item1;
			}
		}
		return null;
	}

	private static void MarkCollectionSwitch(Game g)
	{
		string exePath = g.ExePath;
		string path = RefusedFile(g.ExePath);
		string exe = exePath;
		try
		{
			DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
			if (!(Refused(exe) != "exe not as checked") && !(lastWriteTimeUtc <= File.GetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(exe), "scskiller.ini"))) && Collections.ExesBeside(exe).Any((string x) => !x.Equals(exe, StringComparison.OrdinalIgnoreCase) && Attested(g with
			{
				ExePath = x
			})))
			{
				File.WriteAllText(path, File.ReadAllText(path).Split(' ', 2)[0] + " stamped for another game of its collection");
				File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
	}

	private static string? IniKey(string line)
	{
		int num = line.IndexOf('=');
		string text = ((num < 0) ? "" : line.Substring(0, num).Trim());
		bool flag = text.Length == 0;
		if (!flag)
		{
			char c = text[0];
			bool flag2 = ((c == ';' || c == '[') ? true : false);
			flag = flag2;
		}
		if (!flag)
		{
			return text.ToLowerInvariant();
		}
		return null;
	}

	internal static string Signed(string exe, string root, string stamp, string bound = "")
	{
		string directoryName = Path.GetDirectoryName(Path.GetFullPath(exe));
		string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Final(directoryName), root)));
		string s = FileKey(exe) + "\n" + FileKey(directoryName) + "\n" + stamp + bound + "root=" + root + "\n" + FileKey(path) + "\n";
		return stamp + bound + "sig=" + Convert.ToHexStringLower(HMACSHA1.HashData(RecorderKey(), Encoding.Unicode.GetBytes(s))) + "\r\n";
	}

	private static byte[] RecorderKey()
	{
		string text = Path.Combine(RecorderDir, "recorder.key");
		if (!File.Exists(text))
		{
			Directory.CreateDirectory(RecorderDir);
			string text2 = $"{text}.{Guid.NewGuid():N}";
			File.WriteAllText(text2, RandomNumberGenerator.GetHexString(64, lowercase: true));
			try
			{
				File.Move(text2, text);
			}
			catch (IOException)
			{
				File.Delete(text2);
			}
		}
		return File.ReadAllBytes(text);
	}

	internal static string FileKey(string path)
	{
		path = Path.GetFullPath(path);
		(byte[], string)? tuple = GameFiles.Identity(path);
		if (tuple.HasValue)
		{
			var (array, text) = tuple.GetValueOrDefault();
			if (array != null)
			{
				return Convert.ToHexStringLower(SHA1.HashData(array));
			}
			if (text != null)
			{
				return PathKey(text);
			}
		}
		return PathKey(path);
	}

	private static string PathKey(string path)
	{
		return Convert.ToHexStringLower(SHA1.HashData(Encoding.Unicode.GetBytes(LowerAZ(path))));
	}

	private static string LowerAZ(string path)
	{
		return string.Create(path.Length, path, (Span<char> to, string p) =>
		{
			for (int i = 0; i < p.Length; i++)
			{
				ref char reference = ref to[i];
				char c = p[i];
				reference = ((c >= 'A' && c <= 'Z') ? ((char)(p[i] + 32)) : p[i]);
			}
		});
	}

	private static string RootValue(Game g)
	{
		return RootValue(Path.GetDirectoryName(g.ExePath), g.InstallDir);
	}

	private static string RootValue(string exeDir, string installDir)
	{
		return Path.GetRelativePath(Final(exeDir), Final(installDir));
	}

	private static string Final(string dir)
	{
		return Path.TrimEndingDirectorySeparator(GameFiles.FinalPath(dir) ?? Path.GetFullPath(dir));
	}

	internal static bool Attested(Game g)
	{
		return AttestedStamp(g) != null;
	}

	private static string? AttestedStamp(Game g)
	{
		try
		{
			string directoryName = Path.GetDirectoryName(g.ExePath);
			string now = IniStamp(g.ExePath);
			if (now == null || now.Length <= 0)
			{
				return null;
			}
			string text = Lines((string k) => (k == "exe_size" || k == "exe_time") ? true : false);
			return (text == Stamp(g.ExePath) && now == Signed(g.ExePath, RootValue(g), text, Lines(SignedLineKeys.Contains)) && !RevokedKeys().Contains(FileKey(directoryName))) ? text : null;
			string Lines(Func<string, bool> keep)
			{
				return string.Concat(from l in now.Split("\r\n").Where((string l) =>
					{
						string text2 = IniKey(l);
						return text2 != null && keep(text2);
					})
					select l + "\r\n");
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	internal long RemovalGen(string gameId)
	{
		return _removalGen.GetValueOrDefault(Collections.RecordedBy(gameId) ?? gameId);
	}

	private void NewRemoval(Game g)
	{
		_removalGen.AddOrUpdate(Collections.RecordedBy(g.Id) ?? g.Id, 1L, (string _, long n) => n + 1);
		NotClean(g, save: false);
	}

	private bool Attest(Game g, GameRecord rec, string? stamp, long gen)
	{
		if (Collections.RecordedBy(g.Id) != null)
		{
			return false;
		}
		if (VacCheck(g))
		{
			TakeOutNow(g, "VAC found");
			return false;
		}
		if (!Foreign(g))
		{
			string text = FramesOnlyRefusal(g);
			if (text != null)
			{
				TakeOutNow(g, text);
				return false;
			}
		}
		(IReadOnlyList<CollectionGame>, CollectionGame)? tuple;
		if (RemovalGen(g.Id) == gen && !rec.RecorderRollback)
		{
			tuple = Collections.Member(g.Id);
			IEnumerable<string> source;
			if (tuple.HasValue)
			{
				source = tuple.GetValueOrDefault().Item1.Select((CollectionGame e) => e.Id);
			}
			else
			{
				IEnumerable<string> enumerable = new _003C_003Ez__ReadOnlySingleElementList<string>(g.Id);
				source = enumerable;
			}
			if (!source.Any(_rollbacks.ContainsKey))
			{
				string text2 = CollectionArmExe(g);
				bool num;
				if (text2 == null)
				{
					string runsExe = rec.RunsExe;
					if (runsExe == null)
					{
						goto IL_01a3;
					}
					num = !runsExe.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase);
				}
				else
				{
					num = !text2.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase);
				}
				if (num)
				{
					return Unstamped("the game runs another exe than " + Path.GetFileName(g.ExePath));
				}
				goto IL_01a3;
			}
		}
		return Unstamped("a removal came since its check began, or one waits");
		IL_01a3:
		string old = rec.RunsExeWas;
		string followedFrom = ((old != null && !old.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase) && !Games.Any((GameState s) => s.Game.Id != g.Id && s.Game.ExePath.Equals(old, StringComparison.OrdinalIgnoreCase))) ? old : null);
		string directoryName = Path.GetDirectoryName(g.ExePath);
		IEnumerable<string> enumerable2;
		if (!Directory.Exists(directoryName) || Foreign(g))
		{
			IEnumerable<string> enumerable = Array.Empty<string>();
			enumerable2 = enumerable;
		}
		else
		{
			enumerable2 = Directory.EnumerateFiles(directoryName, "scskiller.armed*");
		}
		foreach (string item in enumerable2)
		{
			try
			{
				File.Delete(item);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				RecorderLog($"{g.Name}: couldn't delete {item}: {ex.Message}");
			}
		}
		bool flag = Attested(g);
		string text3 = null;
		string text4;
		try
		{
			object obj;
			if (stamp != null)
			{
				if (Unconfirmed(g))
				{
					goto IL_0393;
				}
				if (g.Store == SCSKiller.Core.Store.Manual)
				{
					ManualSource? manual = Manual;
					if (manual == null || !manual.ConfirmedNow(g))
					{
						goto IL_0393;
					}
				}
				if (OurProxyIn(directoryName))
				{
					if (Foreign(g))
					{
						obj = "another SCSKiller copy placed it";
					}
					else
					{
						obj = ((BlockingMod(g) != null) ? "an HDR mod changes every pipeline" : null);
					}
				}
				else
				{
					obj = "SCSKiller's proxy isn't in " + directoryName;
				}
			}
			else
			{
				obj = Path.GetFileName(g.ExePath) + " couldn't be read when its check began";
			}
			goto IL_03b4;
			IL_03b4:
			text4 = (string)obj;
			if (text4 == null)
			{
				text3 = Signed(g.ExePath, RootValue(g), stamp, SignedLines(g, rec));
			}
			goto end_IL_030c;
			IL_0393:
			obj = "game folder not confirmed";
			goto IL_03b4;
			end_IL_030c:;
		}
		catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
		{
			text4 = "its stamp couldn't be signed: " + ex2.Message;
		}
		if (Collections.Member(g.Id).HasValue)
		{
			MarkCollectionSwitch(g);
		}
		UpdateRecorderIni(g, rec, text3 ?? "", () => RemovalGen(g.Id) == gen);
		if (RemovalGen(g.Id) != gen)
		{
			StripIni(directoryName, Store, rec.RecorderChained?.Name, g.InstallDir, g.Name, RecorderLog);
			try
			{
				Store.SaveGame(g.Id, rec);
			}
			catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException) ? true : false)
			{
				RecorderLog(g.Name + ": couldn't save the recorder's record: " + ex3.Message);
			}
			return Unstamped("a removal came while its stamp was written");
		}
		if (text3 == null)
		{
			return Unstamped(text4 ?? "its stamp couldn't be signed");
		}
		if (IniStamp(g.ExePath) != text3)
		{
			return Unstamped("its scskiller.ini in " + directoryName + " couldn't be written (one that isn't SCSKiller's is left as it is)");
		}
		IniState iniState;
		try
		{
			iniState = JudgeIni(directoryName, Store, rec.RecorderChained?.Name, g.InstallDir).State;
		}
		catch (Exception ex4) when ((ex4 is IOException || ex4 is UnauthorizedAccessException) ? true : false)
		{
			iniState = IniState.Unknown;
			RecorderLog($"{g.Name}: whose the scskiller.ini in {directoryName} is couldn't be recorded: {ex4.Message}");
		}
		if (iniState != IniState.Ours)
		{
			Revoke(directoryName, on: true, RecorderLog);
			return Unstamped("the scskiller.ini in " + directoryName + " isn't SCSKiller's (left as it is)");
		}
		_unstamped.TryRemove(g.Id, out string _);
		Revoke(directoryName, on: false, RecorderLog);
		if (followedFrom != null && !Games.Any((GameState s) => s.Game.ExePath.Equals(followedFrom, StringComparison.OrdinalIgnoreCase)))
		{
			try
			{
				File.Delete(RefusedFile(followedFrom));
			}
			catch (Exception ex5) when ((ex5 is IOException || ex5 is UnauthorizedAccessException) ? true : false)
			{
			}
		}
		bool flag2 = false;
		if (Collections.Member(g.Id).HasValue)
		{
			foreach (string item2 in from x in Collections.ExesBeside(g.ExePath)
				where !x.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase)
				select x)
			{
				try
				{
					File.Delete(RefusedFile(item2));
				}
				catch (Exception ex6) when ((ex6 is IOException || ex6 is UnauthorizedAccessException) ? true : false)
				{
				}
			}
			flag2 = !flag;
			_collectionArmed[g.Id] = Path.GetFullPath(g.ExePath);
		}
		if (!flag)
		{
			RecorderLog(g.Name + ": recorder stamped for " + Path.GetFileName(g.ExePath));
		}
		if (flag2)
		{
			Game game = Games.FirstOrDefault((GameState s) => s.Game.Id == g.Id)?.Game;
			if ((object)game != null)
			{
				Refresh(game);
			}
		}
		tuple = Collections.Member(g.Id);
		IEnumerable<Game> enumerable3;
		if (tuple.HasValue)
		{
			enumerable3 = tuple.GetValueOrDefault().Item1.Select((CollectionGame e) => Games.FirstOrDefault((GameState s) => s.Game.Id == e.Id)?.Game).OfType<Game>();
		}
		else
		{
			IEnumerable<Game> enumerable4 = new _003C_003Ez__ReadOnlySingleElementList<Game>(g);
			enumerable3 = enumerable4;
		}
		foreach (Game item3 in enumerable3)
		{
			IndexForRecorder(item3);
		}
		return true;
		bool Unstamped(string why)
		{
			if (_unstamped.TryGetValue(g.Id, out string value2) && value2 == why)
			{
				return false;
			}
			_unstamped[g.Id] = why;
			RecorderLog(g.Name + ": the recorder records nothing: " + why);
			return false;
		}
	}

	private string? ForeignIni(string dir, GameRecord rec)
	{
		if (JudgeIni(dir, Store, rec.RecorderChained?.Name, rec.RecorderInstallDir).State != IniState.NotOurs)
		{
			return null;
		}
		return "records nothing: the scskiller.ini in its folder isn't SCSKiller's, so it's left as it is";
	}

	internal static string? Refused(string exe)
	{
		string text;
		try
		{
			text = File.ReadAllText(RefusedFile(exe));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
		string[] array = text.Split(' ', 2);
		if (array != null && array.Length == 2)
		{
			string text2 = array[1];
			string text3 = text2.Trim();
			if (text3 != null && text3.Length > 0)
			{
				return text3;
			}
		}
		return null;
	}

	internal static string RefusedFile(string exe)
	{
		return Path.Combine(RecorderDir, "refused", FileKey(exe));
	}

	internal static bool NeverSaw(string exe, string exeDir, string dllName, PlayWindow? run)
	{
		if ((object)run == null)
		{
			return false;
		}
		DateTime utcDateTime = run.From.UtcDateTime;
		string path = Path.Combine(exeDir, dllName);
		if (File.Exists(path) && File.GetCreationTimeUtc(path) < utcDateTime && !(Written(Path.Combine(exeDir, "scskiller.log")) >= utcDateTime))
		{
			return !(Written(RefusedFile(exe)) >= utcDateTime);
		}
		return false;
		static DateTime? Written(string f)
		{
			if (!File.Exists(f))
			{
				return null;
			}
			return File.GetLastWriteTimeUtc(f);
		}
	}

	internal static void MigrateInis(AppStore store, TimeSpan? wait = null)
	{
		string path = Path.Combine(store.DataDir, "games");
		foreach (string item in (Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>()).Select((string d) => AppStore.GameId(Path.GetFileName(d))))
		{
			GameRecord rec = store.LoadGame(item);
			string hash = rec.RecorderFiles.GetValueOrDefault("scskiller.ini");
			if (hash == null && !rec.IniIsUsers)
			{
				continue;
			}
			string text = rec.RecorderExe ?? rec.RecorderMoveFrom ?? rec.OfflineSession?.Exe;
			string folder = ((text != null) ? Path.GetDirectoryName(text) : null);
			if (folder != null && !Directory.Exists(folder))
			{
				continue;
			}
			if (folder != null)
			{
				if (rec.IniIsUsers)
				{
					SetIni(store, folder, (IniOwner? e) => e ?? new IniOwner(folder, null, Users: true), wait);
				}
				else if (hash != null)
				{
					bool remove = rec.OfflineSession == null && !rec.RecorderFiles.Keys.Any((string f) => (f == "d3d12.dll" || f == "d3d11.dll") ? true : false);
					SetIni(store, folder, (IniOwner? e) => e ?? new IniOwner(folder, hash, Users: false, remove, rec.RecorderChained?.Name, rec.RecorderInstallDir ?? rec.OfflineSession?.InstallDir), wait);
				}
			}
			else if (folder == null && rec.IniIsUsers)
			{
				continue;
			}
			rec.IniIsUsers = false;
			rec.RecorderFiles.Remove("scskiller.ini");
			store.SaveGame(item, rec, wait);
		}
	}

	private void DeleteLedger()
	{
		string ledgerDir = LedgerDir;
		try
		{
			if (Directory.Exists(ledgerDir))
			{
				Directory.Delete(ledgerDir, recursive: true);
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			RecordersLog(Store.DataDir, $"couldn't delete {ledgerDir} ({ex.Message}): its entries are revoked, and it's deleted at the next start");
			try
			{
				foreach (string item in from f in Directory.EnumerateFiles(ledgerDir)
					where Path.GetExtension(f) == ""
					select f)
				{
					try
					{
						File.WriteAllText(item + ".revoked", "");
					}
					catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
					{
						RecordersLog(Store.DataDir, "couldn't revoke " + item + ": " + ex2.Message);
					}
				}
			}
			catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException) ? true : false)
			{
				RecordersLog(Store.DataDir, "couldn't list " + ledgerDir + ": " + ex3.Message);
			}
		}
	}

	private static void StripIni(string dir, AppStore store, string? chain, string? installDir, string name, Action<string> log, TimeSpan? wait = null)
	{
		string text = Path.Combine(dir, "scskiller.ini");
		var (iniState, judged) = JudgeIni(dir, store, chain, installDir, wait);
		switch (iniState)
		{
		case IniState.Absent:
			Revoke(dir, on: false, log);
			return;
		case IniState.Unknown:
			SetIni(store, dir, (IniOwner? e) => Pending(e, e?.Tracked ?? ""), wait);
			Revoke(dir, on: true, log);
			log(name + ": couldn't read " + text + ": the recorder there is refused until it can be");
			return;
		case IniState.NotOurs:
			SetIni(store, dir, (IniOwner? e) => ((object)e == null || !e.Remove) ? e : e with
			{
				Remove = false,
				Tracked = null
			}, wait);
			Revoke(dir, on: true, log);
			return;
		}
		SetIni(store, dir, (IniOwner? e) => Pending(e, Convert.ToHexString(SHA256.HashData(judged))), wait);
		try
		{
			if (!IniUnchanged(text, judged))
			{
				throw new IOException("it changed while it was checked");
			}
			File.Delete(text);
			SetIni(store, dir, (IniOwner? _) => (IniOwner?)null, wait);
			Revoke(dir, on: false, log);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			Revoke(dir, on: true, log);
			log($"{name}: couldn't take {text} out ({ex.Message}): the recorder there is refused until it's gone");
		}
		IniOwner Pending(IniOwner? e, string? tracked)
		{
			IniOwner? obj = e ?? new IniOwner(dir);
			return obj with
			{
				Tracked = tracked,
				Remove = true,
				Chain = (chain ?? e?.Chain),
				Install = (installDir ?? e?.Install)
			};
		}
	}

	private static HashSet<string> RevokedKeys()
	{
		if (!File.Exists(Revoked))
		{
			return new HashSet<string>();
		}
		return (from l in File.ReadAllLines(Revoked)
			where l.Length > 0
			select l).ToHashSet();
	}

	private static void Revoke(string dir, bool on, Action<string> log)
	{
		string item = FileKey(dir);
		string revoked = Revoked;
		lock (_revokedLock)
		{
			try
			{
				using Mutex mutex = new Mutex(initiallyOwned: false, "Local\\SCSKiller-recorder-revoked");
				bool flag;
				try
				{
					flag = mutex.WaitOne(TimeSpan.FromSeconds(10L));
				}
				catch (AbandonedMutexException)
				{
					flag = true;
				}
				if (!flag)
				{
					throw new IOException("another SCSKiller process holds it");
				}
				try
				{
					List<string> list = RevokedKeys().ToList();
					if (list.Contains(item) != on)
					{
						if (on)
						{
							list.Add(item);
						}
						else
						{
							list.Remove(item);
						}
						if (list.Count == 0)
						{
							File.Delete(revoked);
							return;
						}
						Directory.CreateDirectory(RecorderDir);
						string text = $"{revoked}.{Guid.NewGuid():N}";
						File.WriteAllLines(text, list);
						File.Move(text, revoked, overwrite: true);
					}
				}
				finally
				{
					mutex.ReleaseMutex();
				}
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
			{
				log("couldn't update " + revoked + ": " + ex2.Message);
			}
		}
	}

	public void PollGames()
	{
		IReadOnlyList<GameState> games = Games;
		if (games.Count == 0)
		{
			PutBackSetAside();
			return;
		}
		DateTimeOffset dateTimeOffset = Clock();
		HashSet<string> running = new HashSet<string>(Running(), StringComparer.OrdinalIgnoreCase);
		HashSet<string> hashSet = (from s in games
			where running.Contains(Path.GetFileName(s.Game.ExePath)) || RunsFromItsFolder(s.Game)
			select s.Game.Id).ToHashSet();
		HashSet<string> was = _playing;
		List<string> list = hashSet.Where((string item) => !was.Contains(item)).ToList();
		List<string> ended = new List<string>();
		foreach (string item in was)
		{
			if (hashSet.Contains(item))
			{
				_absent.Remove(item);
				continue;
			}
			int num = (_absent[item] = _absent.GetValueOrDefault(item) + 1);
			if (num >= 2)
			{
				ended.Add(item);
			}
		}
		foreach (string item2 in list)
		{
			DateTimeOffset? lastPoll = _lastPoll;
			if (lastPoll.HasValue)
			{
				DateTimeOffset valueOrDefault = lastPoll.GetValueOrDefault();
				_runs[item2] = new PlayWindow(valueOrDefault, dateTimeOffset);
			}
		}
		foreach (string item3 in list)
		{
			if (_recorderIndexes.TryGetValue(item3, out CancellationTokenSource value))
			{
				value.Cancel();
			}
		}
		foreach (string item4 in hashSet)
		{
			if (_runs.TryGetValue(item4, out PlayWindow value2))
			{
				_runs[item4] = value2 with
				{
					To = dateTimeOffset
				};
			}
		}
		_lastPoll = dateTimeOffset;
		_toFollow.UnionWith(list);
		_toFollow.IntersectWith(hashSet);
		foreach (GameState item5 in games.Where((GameState s) => _toFollow.Contains(s.Game.Id)))
		{
			try
			{
				if (FollowRunningExe(item5, dateTimeOffset))
				{
					_toFollow.Remove(item5.Game.Id);
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				_toFollow.Remove(item5.Game.Id);
				Log?.Report(item5.Game.Name + ": couldn't record the exe it runs: " + ex.Message);
			}
		}
		BeforeAfterTick(games, hashSet, dateTimeOffset);
		if (list.Count + ended.Count == 0)
		{
			PutBackSetAside();
			return;
		}
		foreach (string item6 in ended)
		{
			if (_runs.Remove(item6, out PlayWindow value3))
			{
				GameRecord gameRecord = Store.LoadGame(item6);
				gameRecord.LastPlay = value3;
				Store.SaveGame(item6, gameRecord);
			}
		}
		foreach (string item7 in ended)
		{
			_absent.Remove(item7);
			_endedAt[item7] = dateTimeOffset;
		}
		HashSet<string> hashSet2 = new HashSet<string>();
		foreach (string item8 in was.Except(ended))
		{
			hashSet2.Add(item8);
		}
		foreach (string item9 in list)
		{
			hashSet2.Add(item9);
		}
		_playing = hashSet2;
		foreach (string id in list)
		{
			bool flag = PutBack(id, late: true);
			BeforeAfterInfo beforeAfterInfo = BeforeAfterStarted(id);
			GameState state = null;
			lock (_lock)
			{
				int num3 = _games.FindIndex((GameState x) => x.Game.Id == id);
				if (num3 >= 0)
				{
					List<GameState> games2 = _games;
					GameState gameState = _games[num3]with
					{
						Playing = true,
						BeforeAfter = (beforeAfterInfo ?? _games[num3].BeforeAfter)
					};
					GameState value4 = gameState;
					state = gameState;
					games2[num3] = value4;
				}
			}
			if (state != null)
			{
				GameChanged?.Invoke(state);
			}
			if (flag)
			{
				ShowKept(id);
			}
			if (AppCache != null && (object)state != null && state.AntiCheat == AntiCheat.None && !IsUnchecked(state))
			{
				_learning[id] = Task.Run(() => LearnWhilePlaying(state.Game));
			}
		}
		foreach (GameState item10 in games.Where((GameState s) => ended.Contains(s.Game.Id)))
		{
			string by = Collections.RecordedBy(item10.Game.Id);
			if (by != null)
			{
				GameState gameState2 = games.FirstOrDefault((GameState x) => x.Game.Id == by);
				if ((object)gameState2 != null)
				{
					Refresh(Current(gameState2.Game));
				}
			}
			Game g = Current(item10.Game);
			KeepAside(g.Id, Store.LoadGame(g.Id), g.Name);
			Refresh(g);
			GameState value4 = Games.FirstOrDefault((GameState x) => x.Game.Id == g.Id);
			if ((object)value4 != null)
			{
				string recorderRefused = value4.RecorderRefused;
				if (recorderRefused != null && !value4.OfflineRunning)
				{
					PlayWindow lastPlay = Store.LoadGame(g.Id).LastPlay;
					if ((object)lastPlay != null)
					{
						string text = ((File.GetLastWriteTimeUtc(RefusedFile(g.ExePath)) >= lastPlay.From.UtcDateTime) ? Refused(g.ExePath) : null);
						IProgress<string> log = Log;
						if (log != null)
						{
							string current8 = g.Name;
							string text2;
							if (text != null)
							{
								text2 = "the recorder refused it: " + text;
							}
							else
							{
								bool flag2 = ((recorderRefused == "The recorder never loaded in the last launch. Please report it with the game's name." || recorderRefused == "The recorder didn't load in the last launch. The game may have run on DirectX 11.") ? true : false);
								text2 = (flag2 ? "the recorder never loaded, no scskiller.log written during the run" : ("no refusal for its exe, the page says: " + recorderRefused));
							}
							log.Report(current8 + ": the launch recorded nothing: " + text2);
						}
					}
				}
			}
			BeforeAfterExited(g);
			StartSharing(new _003C_003Ez__ReadOnlySingleElementList<Game>(g));
			if (ManageRecorders)
			{
				try
				{
					ReconcileRecorders(g.Id);
				}
				catch (Exception ex2)
				{
					Log?.Report(g.Name + ": reconciling the recorder failed: " + ex2.Message);
				}
			}
		}
		if (ended.Count > 0)
		{
			KeepList();
		}
		PutBackSetAside();
	}

	public void RefreshGame(string gameId)
	{
		Refresh(Find(gameId).Game);
	}

	public void RefreshCacheSizes()
	{
		IAppCache appCache = AppCache;
		if (appCache == null)
		{
			return;
		}
		foreach (GameState s in Games)
		{
			HashSet<string> cacheKeys = Store.LoadGame(s.Game.Id).CacheKeys;
			if (cacheKeys.Count == 0)
			{
				continue;
			}
			long num = appCache.SizeOf(cacheKeys);
			if (num == s.CacheOnDisk)
			{
				continue;
			}
			GameState gameState = s with
			{
				CacheOnDisk = num
			};
			lock (_lock)
			{
				int num2 = _games.FindIndex((GameState x) => x.Game.Id == s.Game.Id);
				if (num2 < 0 || _games[num2] != s)
				{
					continue;
				}
				_games[num2] = gameState;
				goto IL_010d;
			}
			IL_010d:
			GameChanged?.Invoke(gameState);
		}
	}

	private static TimeSpan UserIdleTime()
	{
		LastInputInfo info = new LastInputInfo
		{
			Size = 8u
		};
		if (!GetLastInputInfo(ref info))
		{
			return TimeSpan.MaxValue;
		}
		return TimeSpan.FromMilliseconds((uint)(Environment.TickCount - (int)info.Time));
	}

	[DllImport("user32.dll")]
	private static extern bool GetLastInputInfo(ref LastInputInfo info);

	private string KeptDir(string gameId)
	{
		return Path.Combine(Store.GameDir(gameId), "kept");
	}

	private AmdKept? KeptOf(string gameId)
	{
		if (!(AppCache is AmdAppCache amdAppCache))
		{
			return null;
		}
		return new AmdKept(amdAppCache.DxcDir, KeptDir(gameId));
	}

	private AmdKept? Keeping(string gameId)
	{
		if (!Settings.KeepCachesAside || !(AppCache is AmdAppCache amdAppCache) || !AmdKept.Usable(amdAppCache.DxcDir, Store.DataDir))
		{
			return null;
		}
		return new AmdKept(amdAppCache.DxcDir, KeptDir(gameId));
	}

	private HashSet<string> OwnKeys(string gameId, GameRecord rec)
	{
		HashSet<string> hashSet = rec.CacheKeys.Where((string k) => k.StartsWith("dxc:", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (hashSet.Count > 0)
		{
			foreach (var item2 in SharedWith(gameId, hashSet))
			{
				List<string> item = item2.Keys;
				hashSet.ExceptWith(item);
			}
		}
		return hashSet;
	}

	private void KeepAside(string gameId, GameRecord rec, string name)
	{
		try
		{
			lock (_keptLock)
			{
				if (!rec.WarmedAt.HasValue)
				{
					return;
				}
				AmdKept amdKept = Keeping(gameId);
				if (amdKept == null || !(AppCache is AmdAppCache amdAppCache))
				{
					return;
				}
				string driverId = DriverId;
				if (driverId != null)
				{
					string driver = amdKept.Read().Driver;
					if (driver != null && driver != driverId && !Drop(amdKept, gameId, "kept for another driver"))
					{
						return;
					}
				}
				HashSet<string> keys = OwnKeys(gameId, rec);
				AmdKept.State state = amdKept.Keep(amdAppCache.D3D12FileNames(keys), keys, DriverId);
				if ((object)state != null && state.Unkept > 0)
				{
					Log?.Report($"{name}: {state.Unkept} cache file{((state.Unkept == 1) ? "" : "s")} not kept aside, in use by {state.HeldBy ?? "another app"}");
				}
			}
		}
		catch (Exception ex)
		{
			Log?.Report(name + ": keeping the cache aside failed: " + ex.Message);
		}
	}

	public void PutBackKept(string gameId)
	{
		try
		{
			if (PutBack(gameId, late: false))
			{
				ShowKept(gameId);
			}
		}
		catch (Exception ex)
		{
			Log?.Report("putting " + gameId + "'s cache back failed: " + ex.Message);
		}
	}

	private bool PutBack(string gameId, bool late)
	{
		try
		{
			lock (_keptLock)
			{
				AmdKept amdKept = Keeping(gameId);
				if (amdKept == null || !amdKept.Any || !(AppCache is AmdAppCache amdAppCache))
				{
					return false;
				}
				string driverId = DriverId;
				if (driverId != null)
				{
					string driver = amdKept.Read().Driver;
					if (driver != null && driver != driverId)
					{
						Drop(amdKept, gameId, "kept for another driver");
						return true;
					}
				}
				HashSet<string> hashSet = OwnKeys(gameId, Store.LoadGame(gameId));
				if (amdAppCache.D3D12FileNames(hashSet).Count == 0 && amdAppCache.DxcBytes() < 8589934592L)
				{
					Drop(amdKept, gameId, "the driver's cache was reset");
					return true;
				}
				return amdKept.PutBack(hashSet.Contains, late, DateTimeOffset.Now) > 0;
			}
		}
		catch (Exception ex)
		{
			Log?.Report(gameId + ": putting the kept cache back failed: " + ex.Message);
			return false;
		}
	}

	private bool Drop(AmdKept k, string gameId, string why)
	{
		bool flag = k.Drop();
		Log?.Report(gameId + ": kept cache files removed, " + why + (flag ? "" : ", some are in use and stay until the next time"));
		return flag;
	}

	private void DropKept(string gameId)
	{
		try
		{
			lock (_keptLock)
			{
				if (Directory.Exists(KeptDir(gameId)))
				{
					Drop(new AmdKept("", KeptDir(gameId)), gameId, "the game's cache was cleared");
				}
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			Log?.Report(gameId + ": removing the kept cache failed: " + ex.Message);
		}
	}

	public void DropKeptCache(string gameId)
	{
		DropKept(gameId);
		Refresh(Find(gameId).Game);
	}

	private void DropStaleKept()
	{
		string path = Path.Combine(Store.DataDir, "games");
		bool flag = !Settings.KeepCachesAside || !(AppCache is AmdAppCache);
		if (!Directory.Exists(path) || (!flag && DriverId == null))
		{
			return;
		}
		try
		{
			lock (_keptLock)
			{
				foreach (string item in Directory.EnumerateDirectories(path))
				{
					string text = Path.Combine(item, "kept");
					if (!Directory.Exists(text))
					{
						continue;
					}
					AmdKept amdKept = new AmdKept("", text);
					if (!flag)
					{
						string driver = amdKept.Read().Driver;
						if (driver == null || !(driver != DriverId))
						{
							continue;
						}
					}
					Drop(amdKept, Path.GetFileName(item), flag ? "keeping caches aside is off" : "kept for another driver");
				}
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			Log?.Report("removing kept caches failed: " + ex.Message);
		}
		foreach (GameState game in Games)
		{
			ShowKept(game.Game.Id);
		}
	}

	private KeptCache? KeptState(string gameId)
	{
		try
		{
			AmdKept amdKept = KeptOf(gameId);
			if (amdKept == null || !amdKept.Any)
			{
				return null;
			}
			(long Bytes, long Frees) tuple = amdKept.Size();
			long item = tuple.Bytes;
			long item2 = tuple.Frees;
			AmdKept.State state = amdKept.Read();
			return new KeptCache(item, item2, state.PutBackAt, state.Unkept, state.HeldBy, state.Late);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	private void ShowKept(string gameId)
	{
		KeptCache keptCache = KeptState(gameId);
		HashSet<string> cacheKeys = Store.LoadGame(gameId).CacheKeys;
		long? num = ((cacheKeys != null && cacheKeys.Count > 0 && AppCache != null) ? new long?(AppCache.SizeOf(cacheKeys)) : ((long?)null));
		GameState gameState = null;
		lock (_lock)
		{
			int num2 = _games.FindIndex((GameState x) => x.Game.Id == gameId);
			if (num2 >= 0 && (_games[num2].Kept != keptCache || _games[num2].CacheOnDisk != num))
			{
				List<GameState> games = _games;
				GameState gameState2 = _games[num2]with
				{
					Kept = keptCache,
					CacheOnDisk = num
				};
				gameState = gameState2;
				games[num2] = gameState2;
			}
		}
		if (gameState != null)
		{
			GameChanged?.Invoke(gameState);
		}
	}

	private bool KeptSame(AmdKept? kept, GameRecord r, string name)
	{
		string text = kept?.KeptId(name);
		if (text != null)
		{
			string text2 = r.WarmedFileIds?.GetValueOrDefault(name);
			if (text2 != null)
			{
				return text2 == text;
			}
			return true;
		}
		return false;
	}

	internal static string SessionNote(string? refused, string neverSaw = "The recorder never loaded in the last launch. Please report it with the game's name.")
	{
		if (refused == null)
		{
			return neverSaw;
		}
		if (Unstamped(refused) || refused == "scskiller.ini changed while deciding")
		{
			return "This launch started before SCSKiller could attach: nothing was recorded. Try again.";
		}
		return "The last launch wasn't recorded: " + refused + ".";
	}

	private bool OfflineLive(string gameId)
	{
		if (!_offline.ContainsKey(gameId))
		{
			ConcurrentDictionary<string, bool>? offlineLeft = _offlineLeft;
			if (offlineLeft != null && offlineLeft.ContainsKey(gameId))
			{
				OfflineSession offlineSession = Store.LoadGame(gameId).OfflineSession;
				if ((object)offlineSession != null && offlineSession.Resumed)
				{
					return Alive(offlineSession);
				}
			}
			return false;
		}
		return true;
	}

	public bool BeginUpdate()
	{
		lock (_updateGate)
		{
			if (OfflineBlocksUpdate)
			{
				return false;
			}
			_updating++;
			return true;
		}
	}

	public void EndUpdate()
	{
		lock (_updateGate)
		{
			_updating--;
		}
	}

	private static string HelperMutex(string gameId)
	{
		return "Local\\SCSKiller-offline-cleanup-" + gameId.Replace('\\', '_');
	}

	private static bool HelperRuns(string gameId)
	{
		if (!Mutex.TryOpenExisting(HelperMutex(gameId), out Mutex result))
		{
			return false;
		}
		result.Dispose();
		return true;
	}

	internal static bool OthersRun(OfflineSession s, List<(int Pid, int Parent, string Exe)> all)
	{
		HashSet<string> names = ExeNamesIn(new string[2]
		{
			s.InstallDir,
			Path.GetDirectoryName(s.Exe)
		});
		if (names != null)
		{
			return all.Any(((int Pid, int Parent, string Exe) p) => p.Pid != s.Pid && names.Contains(Path.GetFileNameWithoutExtension(p.Exe)));
		}
		return true;
	}

	private OfflineEacGame? OfflineEntry(Game g, AntiCheat antiCheat, EngineInfo? engine)
	{
		if (!Unconfirmed(g) && !RecorderCrashes(g))
		{
			return OfflineEac.Of(g, antiCheat, engine, Settings.RecordAntiCheatGames);
		}
		return null;
	}

	public void SetOfflineRecording(string gameId, bool on)
	{
		lock (_recorderLock)
		{
			GameState gameState = Find(gameId);
			if (on && !gameState.OfflineEligible && !gameState.RiskEligible)
			{
				throw new InvalidOperationException(gameState.Game.Name + ": recording while you play isn't available for this game");
			}
			GameRecord gameRecord = Store.LoadGame(gameId);
			gameRecord.OfflineRecord = on;
			Store.SaveGame(gameId, gameRecord);
			if (!on)
			{
				_sessionNotes.TryRemove(gameId, out string _);
			}
		}
		RefreshGame(gameId);
	}

	public static string AntiCheatName(AntiCheat a)
	{
		return a switch
		{
			AntiCheat.EasyAntiCheat => "EasyAntiCheat", 
			AntiCheat.BattlEye => "BattlEye", 
			_ => "anti-cheat", 
		};
	}

	public static string AntiCheatName(GameState s)
	{
		return AntiCheatName(s.AntiCheat, s.Vac);
	}

	private static string AntiCheatName(AntiCheat a, bool vac)
	{
		if (!((a == AntiCheat.None) & vac))
		{
			return AntiCheatName(a);
		}
		return "VAC";
	}

	private bool RiskOffered(GameState s)
	{
		return RiskOffered(s.Game, s.Engine, s.AnyAntiCheat);
	}

	private bool RiskOffered(Game g, EngineInfo? engine, bool anyAntiCheat)
	{
		if (RiskOfferedButLaunch(g, engine, anyAntiCheat))
		{
			return StoreLaunches(g);
		}
		return false;
	}

	private bool RiskOfferedButLaunch(Game g, EngineInfo? engine, bool anyAntiCheat)
	{
		if ((Settings.RecordAntiCheatGames & anyAntiCheat) && !Unconfirmed(g) && !RecorderCrashes(g) && (object)engine != null)
		{
			if (engine.GraphicsApi.Contains("D3D12") || Records11Api(engine, Vendor.Caps))
			{
				return GameVerdicts.Current.Unsupported(g, engine) == null;
			}
		}
		return false;
	}

	internal static string RiskDll(EngineInfo? e, VendorCaps caps)
	{
		if (!Records11Api(e, caps) && ((object)e == null || !(e.Family == "Unreal") || !(ApiKey(e.GraphicsApi) == "11+12") || !e.Version.StartsWith("4.") || Planner.D3D11CoversVs(caps)))
		{
			return "d3d12.dll";
		}
		return "d3d11.dll";
	}

	private static string RiskNeverSaw(EngineInfo? e, string dll)
	{
		if (!(ApiKey(e?.GraphicsApi) != "11+12"))
		{
			if (!(dll == "d3d12.dll"))
			{
				return "The recorder didn't load in the last launch. The game may have run on DirectX 12.";
			}
			return "The recorder didn't load in the last launch. The game may have run on DirectX 11.";
		}
		return "The recorder never loaded in the last launch. Please report it with the game's name.";
	}

	private void RiskOff()
	{
		_sessionNotes.Clear();
		lock (_recorderLock)
		{
			foreach (GameState item in Games.Where((GameState s) => OfflineEac.Of(s.Game, s.AntiCheat, s.Engine) == null))
			{
				try
				{
					GameRecord gameRecord = Store.LoadGame(item.Game.Id);
					if (gameRecord.OfflineRecord)
					{
						gameRecord.OfflineRecord = false;
						Store.SaveGame(item.Game.Id, gameRecord);
					}
				}
				catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
					RecorderLog(item.Game.Name + ": couldn't turn off Record while I play: " + ex.Message);
				}
			}
		}
		CleanOfflineSessions();
	}

	public Task<AntiCheat> CheckAntiCheatAsync(string gameId)
	{
		Game g = Find(gameId).Game;
		return Task.Run(() => GameFiles.DetectAntiCheat(g));
	}

	public Task StartRiskSession(string gameId, bool confirmed, AntiCheat shown)
	{
		GameState gameState = Find(gameId);
		Game game = gameState.Game;
		if (!confirmed)
		{
			throw new InvalidOperationException(game.Name + ": a session starts only after you confirm it");
		}
		lock (_updateGate)
		{
			if (_updating > 0)
			{
				throw new InvalidOperationException("An update is being installed. Try again after it.");
			}
			_offlineStarting++;
		}
		try
		{
			return StartRisk(gameState, game, gameId, shown);
		}
		finally
		{
			lock (_updateGate)
			{
				_offlineStarting--;
			}
		}
	}

	private Task StartRisk(GameState s, Game g, string gameId, AntiCheat shown)
	{
		Task result;
		lock (_recorderLock)
		{
			GameRecord rec = Store.LoadGame(gameId);
			string dir = Path.GetDirectoryName(g.ExePath);
			bool manual = g.Store == SCSKiller.Core.Store.Manual;
			string dll = RiskDll(s.Engine, Vendor.Caps);
			string neverSaw = RiskNeverSaw(s.Engine, dll);
			string[] array = (DefaultCopy ? Array.Empty<string>() : new string[1] { "scskiller.ledger" });
			int num = 0;
			string[] array2 = new string[2 + array.Length];
			ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(array);
			readOnlySpan.CopyTo(new Span<string>(array2).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			array2[num] = dll;
			num++;
			array2[num] = "scskiller.ini";
			string[] placed = array2;
			string stamp = Stamp(g.ExePath);
			ProcessStartInfo launch = null;
			AntiCheat found = AntiCheat.None;
			bool vac = false;
			string text = Refusal();
			if (text != null)
			{
				throw new InvalidOperationException(g.Name + ": no session: " + text);
			}
			_sessionNotes.TryRemove(gameId, out string value);
			DateTime since = DateTime.UtcNow;
			OfflineSession session = new OfflineSession(gameId, g.ExePath, g.InstallDir, 0, 0L, Resumed: false, placed.Select((string f) => f + ".scskiller-new").ToArray(), Risk: true);
			GameRecord gameRecord = rec;
			GameRecord gameRecord2 = rec;
			GameRecord gameRecord3 = rec;
			value = g.ExePath;
			string installDir = g.InstallDir;
			OfflineSession offlineSession = session;
			gameRecord.RecorderExe = value;
			gameRecord2.RecorderInstallDir = installDir;
			gameRecord3.OfflineSession = offlineSession;
			Store.SaveGame(gameId, rec);
			OfflineLeft()[gameId] = true;
			RecorderLog($"{g.Name}: session in a game with {AntiCheatName(found, vac)}, at the user's own risk: recorder in as {dll} ({dir})");
			SafeProcessHandle process = null;
			try
			{
				if (CleanupHelper != null)
				{
					AddRunOnce(gameId, CleanupHelper);
					using (Process.Start(new ProcessStartInfo(CleanupHelper, new _003C_003Ez__ReadOnlyArray<string>(new string[2] { "--offline-cleanup", gameId }))
					{
						UseShellExecute = false,
						CreateNoWindow = true
					}) ?? throw new InvalidOperationException("its cleanup helper didn't start"))
					{
					}
				}
				if (manual)
				{
					process = StartAttested(g.ExePath, OfflineArguments, (SafeProcessHandle h, int pid, long started) =>
					{
						_offline[gameId] = h;
						Publish(IniText(g, rec, Signed(g.ExePath, RootValue(g), stamp, BoundTo(pid, started) + "risk=1\r\n" + SignedLines(g, rec, session: true, riskSession: true))), (GameRecord now) =>
						{
							OfflineSession offlineSession2 = session with
							{
								Pid = pid,
								Started = started
							};
							OfflineSession offlineSession3 = offlineSession2;
							session = offlineSession2;
							now.OfflineSession = offlineSession3;
						});
					}, () =>
					{
						GameRecord gameRecord4 = Store.LoadGame(gameId);
						OfflineSession offlineSession2 = session with
						{
							Resumed = true
						};
						OfflineSession offlineSession3 = offlineSession2;
						session = offlineSession2;
						gameRecord4.OfflineSession = offlineSession3;
						Store.SaveGame(gameId, gameRecord4);
					});
				}
				else
				{
					_offline[gameId] = new SafeProcessHandle(0, ownsHandle: false);
					Publish(IniText(g, rec));
					Process.Start(launch)?.Dispose();
				}
			}
			catch (Exception ex)
			{
				_offline.TryRemove(gameId, out SafeProcessHandle _);
				CleanOffline(gameId);
				throw new InvalidOperationException(g.Name + ": the session didn't start: " + ex.Message, ex);
			}
			if (process != null)
			{
				RecorderLog($"{g.Name}: session started ({g.ExePath}, process {session.Pid}): only that process records");
				result = Task.Run(() => EndOffline(g, process, since, risk: true, neverSaw));
			}
			else
			{
				RecorderLog($"{g.Name}: session started through its store: it records the first {Path.GetFileName(g.ExePath)} that runs from {dir}");
				result = Task.Run(() => BindRisk(g, stamp, since, neverSaw));
			}
			void Publish(string ini, Action<GameRecord>? also = null)
			{
				GameRecord gameRecord4 = Store.LoadGame(gameId);
				if (gameRecord4.OfflineSession == null)
				{
					throw new InvalidOperationException("its session was cleaned up meanwhile");
				}
				gameRecord4.RecorderFiles = new Dictionary<string, string> { [dll] = ProxySha(dll) };
				SetIni(Store, dir, (IniOwner? _) => new IniOwner(dir, Hex(ini), Users: false, Remove: false, null, g.InstallDir));
				also?.Invoke(gameRecord4);
				Store.SaveGame(gameId, gameRecord4);
				if (!DefaultCopy)
				{
					Place(Path.Combine(dir, "scskiller.ledger"), (string temp) =>
					{
						WriteNew(temp, Path.GetFullPath(LedgerDir));
					});
				}
				Place(Path.Combine(dir, dll), (string temp) =>
				{
					File.Copy(_proxyDll, temp);
				});
				Place(Path.Combine(dir, "scskiller.ini"), (string temp) =>
				{
					WriteNew(temp, ini);
				});
			}
			string? Refusal()
			{
				if (!Settings.RecordAntiCheatGames)
				{
					return "allow recording in games with anti-cheat in Settings first";
				}
				if (!s.RiskEligible)
				{
					return "recording while you play isn't available for this game";
				}
				if (!rec.OfflineRecord)
				{
					return "turn on Record while I play for this game first";
				}
				if (OfflineLive(gameId) || rec.OfflineSession != null)
				{
					return "the last session's files are still being removed";
				}
				if (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.RecorderExe != null || rec.RecorderMoveFrom != null || rec.RecorderRollback)
				{
					return "a recorder's removal is still pending";
				}
				lock (_lock)
				{
					if (_current == gameId)
					{
						return "a compile of it is in progress";
					}
				}
				if (GameRunning(g))
				{
					return "it is running: close the game first";
				}
				if (_proxyDll == null || !File.Exists(_proxyDll))
				{
					return "the recorder (" + dll + ") wasn't found next to SCSKiller";
				}
				if (stamp == null)
				{
					return "its exe can't be read";
				}
				AntiCheat antiCheat = GameFiles.DetectAntiCheat(g);
				bool flag = VacCheck(g);
				vac = flag;
				found = antiCheat;
				if (found != shown)
				{
					return AntiCheatName(found, vac) + " was found, not " + AntiCheatName(shown, vac) + ": confirm again";
				}
				if (!manual && (launch = LaunchCommand(g)) == null)
				{
					return "its store has no launch SCSKiller can start";
				}
				string text2 = RecorderOwnFiles.Concat(placed).FirstOrDefault((string f) => Path.Exists(Path.Combine(dir, f)));
				if (text2 != null)
				{
					return text2 + " is already in its folder";
				}
				ReShadeInstall reShadeInstall = ReShade.Detect(g);
				if ((object)reShadeInstall != null && reShadeInstall.ProxyLibrary)
				{
					return "ReShade chains a proxy library ([PROXY])";
				}
				return null;
			}
		}
		Refresh(g);
		return result;
	}

	private async Task BindRisk(Game g, string stamp, DateTime since, string neverSaw)
	{
		SafeProcessHandle process = null;
		int pid = 0;
		SafeProcessHandle value;
		try
		{
			Stopwatch clock = Stopwatch.StartNew();
			while (true)
			{
				process = (value = RunningExe(g, out pid));
				if (value != null)
				{
					break;
				}
				if (clock.Elapsed > TimeSpan.FromMinutes(5L))
				{
					throw new InvalidOperationException("the game didn't start within 5 minutes");
				}
				await Task.Delay(50);
			}
			if (!GetProcessTimes(process, out var created, out var _, out var _, out var _))
			{
				throw new Win32Exception();
			}
			lock (_recorderLock)
			{
				using (new AppStore.PathGate(Path.Combine(Store.GameDir(g.Id), "offline")))
				{
					GameRecord gameRecord = Store.LoadGame(g.Id);
					if (gameRecord.OfflineSession == null)
					{
						throw new InvalidOperationException("its session was cleaned up meanwhile");
					}
					gameRecord.OfflineSession = gameRecord.OfflineSession with
					{
						Pid = pid,
						Started = created,
						Resumed = true
					};
					Store.SaveGame(g.Id, gameRecord);
					_offline[g.Id] = process;
					string directoryName = Path.GetDirectoryName(g.ExePath);
					(IniState State, byte[]? Bytes) tuple = JudgeIni(directoryName, Store, null, g.InstallDir);
					IniState item = tuple.State;
					byte[] item2 = tuple.Bytes;
					string text = IniText(g, gameRecord, Signed(g.ExePath, RootValue(g), stamp, BoundTo(pid, created) + "risk=1\r\n" + SignedLines(g, gameRecord, session: true, riskSession: true)));
					if (item != IniState.Ours || !WriteIni(directoryName, text, item2, null, g.InstallDir, () => true, null))
					{
						throw new InvalidOperationException("its scskiller.ini changed meanwhile");
					}
				}
			}
		}
		catch (Exception ex)
		{
			_offline.TryRemove(g.Id, out value);
			process?.Dispose();
			CleanOffline(g.Id);
			Refresh(g);
			throw new InvalidOperationException(g.Name + ": the session didn't start: " + ex.Message, ex);
		}
		RecorderLog($"{g.Name}: session bound to process {pid} ({g.ExePath}): only that process records");
		Refresh(g);
		await EndOffline(g, process, since, risk: true, neverSaw);
	}

	private SafeProcessHandle? RunningExe(Game g, out int pid)
	{
		string name = Path.GetFileName(g.ExePath);
		string text = GameFiles.FinalPath(Path.GetDirectoryName(g.ExePath));
		string real = ((text != null) ? Path.Combine(text, name) : null);
		foreach (var item in Processes(arg: true).Where(((int Pid, int Parent, string Exe) p) => p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase) && Ours(p.Pid)))
		{
			SafeProcessHandle safeProcessHandle = OpenProcess(1052672u, inherit: false, item.Item1);
			if (safeProcessHandle.IsInvalid || !Ours(item.Item1))
			{
				safeProcessHandle.Dispose();
				continue;
			}
			(pid, _, _) = item;
			return safeProcessHandle;
		}
		pid = 0;
		return null;
		bool Ours(int arg)
		{
			string text2 = ProcessPath(arg);
			if (text2 != null)
			{
				if (!text2.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase))
				{
					if (real != null)
					{
						return text2.Equals(real, StringComparison.OrdinalIgnoreCase);
					}
					return false;
				}
				return true;
			}
			return false;
		}
	}

	private bool Admitted(Game g, DateTime since, bool risk)
	{
		string[] array = new string[2]
		{
			Path.GetDirectoryName(g.ExePath),
			OfflineSessionDir(Store, g.Id)
		};
		foreach (string path in array)
		{
			try
			{
				FileInfo fileInfo = new FileInfo(Path.Combine(path, "scskiller.log"));
				if (!fileInfo.Exists || fileInfo.LastWriteTimeUtc < since)
				{
					continue;
				}
				using StreamReader streamReader = new StreamReader(new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
				if (!risk || streamReader.ReadToEnd().Contains("risk=1: recording at the user's own risk"))
				{
					return true;
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				return true;
			}
		}
		return false;
	}

	public Task StartOfflineSession(string gameId, bool confirmed)
	{
		GameState gameState = Find(gameId);
		Game game = gameState.Game;
		if (!confirmed)
		{
			throw new InvalidOperationException(game.Name + ": an offline session starts only after you confirm it");
		}
		lock (_updateGate)
		{
			if (_updating > 0)
			{
				throw new InvalidOperationException("An update is being installed. Try again after it.");
			}
			_offlineStarting++;
		}
		try
		{
			return StartOffline(gameState, game, gameId);
		}
		finally
		{
			lock (_updateGate)
			{
				_offlineStarting--;
			}
		}
	}

	private Task StartOffline(GameState s, Game g, string gameId)
	{
		lock (_recorderLock)
		{
			GameRecord rec = Store.LoadGame(gameId);
			string dir = Path.GetDirectoryName(g.ExePath);
			OfflineEacGame entry = OfflineEntry(g, s.AntiCheat, s.Engine);
			string appIdFile = Path.Combine(dir, "steam_appid.txt");
			bool addAppId = !File.Exists(appIdFile);
			string dll = RiskDll(s.Engine, Vendor.Caps);
			string neverSaw = RiskNeverSaw(s.Engine, dll);
			List<string> list = new List<string>();
			list.AddRange(DefaultCopy ? Array.Empty<string>() : new string[1] { "scskiller.ledger" });
			list.Add(dll);
			list.Add("scskiller.ini");
			list.AddRange((!addAppId) ? Array.Empty<string>() : new string[1] { "steam_appid.txt" });
			string[] placed = list.ToArray();
			string stamp = Stamp(g.ExePath);
			string text = Refusal();
			if (text != null)
			{
				throw new InvalidOperationException((text == "Start Steam first (offline mode is fine).") ? text : (g.Name + ": no offline session: " + text));
			}
			string gameId2 = gameId;
			string exePath = g.ExePath;
			string installDir = g.InstallDir;
			long started = 0L;
			list = new List<string>();
			list.AddRange(placed.Select((string f) => f + ".scskiller-new"));
			list.AddRange((!addAppId) ? Array.Empty<string>() : new string[1] { "steam_appid.txt" });
			OfflineSession session = new OfflineSession(gameId2, exePath, installDir, 0, started, Resumed: false, list.ToArray());
			GameRecord gameRecord = rec;
			GameRecord gameRecord2 = rec;
			GameRecord gameRecord3 = rec;
			string exePath2 = g.ExePath;
			string installDir2 = g.InstallDir;
			OfflineSession offlineSession = session;
			gameRecord.RecorderExe = exePath2;
			gameRecord2.RecorderInstallDir = installDir2;
			gameRecord3.OfflineSession = offlineSession;
			Store.SaveGame(gameId, rec);
			OfflineLeft()[gameId] = true;
			DateTime since = DateTime.UtcNow;
			RecorderLog($"{g.Name}: offline session without EasyAntiCheat: recorder in as {dll} ({dir})");
			SafeProcessHandle process = null;
			try
			{
				if (CleanupHelper != null)
				{
					AddRunOnce(gameId, CleanupHelper);
					using (Process.Start(new ProcessStartInfo(CleanupHelper, new _003C_003Ez__ReadOnlyArray<string>(new string[2] { "--offline-cleanup", gameId }))
					{
						UseShellExecute = false,
						CreateNoWindow = true
					}) ?? throw new InvalidOperationException("its cleanup helper didn't start"))
					{
					}
				}
				process = StartAttested(g.ExePath, OfflineArguments, (SafeProcessHandle h, int pid, long num) =>
				{
					_offline[gameId] = h;
					string ini = IniText(g, rec, Signed(g.ExePath, RootValue(g), stamp, BoundTo(pid, num) + SignedLines(g, rec, session: true)));
					GameRecord gameRecord4 = Store.LoadGame(gameId);
					if (gameRecord4.OfflineSession == null)
					{
						throw new InvalidOperationException("its session was cleaned up meanwhile");
					}
					gameRecord4.RecorderFiles = new Dictionary<string, string> { [dll] = ProxySha(dll) };
					SetIni(Store, dir, (IniOwner? _) => new IniOwner(dir, Hex(ini), Users: false, Remove: false, null, g.InstallDir));
					if (addAppId)
					{
						gameRecord4.RecorderFiles["steam_appid.txt"] = Hex(entry.AppId);
					}
					gameRecord4.OfflineSession = (session = session with
					{
						Pid = pid,
						Started = num
					});
					Store.SaveGame(gameId, gameRecord4);
					if (!DefaultCopy)
					{
						Place(Path.Combine(dir, "scskiller.ledger"), (string temp) =>
						{
							WriteNew(temp, Path.GetFullPath(LedgerDir));
						});
					}
					Place(Path.Combine(dir, dll), (string temp) =>
					{
						File.Copy(_proxyDll, temp);
					});
					Place(Path.Combine(dir, "scskiller.ini"), (string temp) =>
					{
						WriteNew(temp, ini);
					});
					if (addAppId)
					{
						Place(Path.Combine(dir, "steam_appid.txt"), (string temp) =>
						{
							WriteNew(temp, entry.AppId);
						});
					}
					AntiCheat antiCheat = GameFiles.DetectAntiCheat(g, quick: true, null, AntiCheat.EasyAntiCheat);
					if (antiCheat != AntiCheat.None)
					{
						throw new InvalidOperationException($"{antiCheat} appeared in its folder");
					}
				}, () =>
				{
					GameRecord gameRecord4 = Store.LoadGame(gameId);
					OfflineSession offlineSession2 = session with
					{
						Resumed = true
					};
					OfflineSession offlineSession3 = offlineSession2;
					session = offlineSession2;
					gameRecord4.OfflineSession = offlineSession3;
					Store.SaveGame(gameId, gameRecord4);
				});
			}
			catch (Exception ex)
			{
				_offline.TryRemove(gameId, out SafeProcessHandle _);
				CleanOffline(gameId);
				throw new InvalidOperationException(g.Name + ": the offline session didn't start: " + ex.Message, ex);
			}
			RecorderLog($"{g.Name}: offline session started ({g.ExePath}, process {session.Pid}): only that process records");
			Refresh(g);
			return Task.Run(() => EndOffline(g, process, since, risk: false, neverSaw));
			string? Refusal()
			{
				if (entry == null)
				{
					return "offline sessions without EasyAntiCheat aren't available for this game";
				}
				GameVerdicts.Verdict verdict = GameVerdicts.Current.Unsupported(g, s.Engine);
				if ((object)verdict != null)
				{
					return "not supported: " + verdict.Text;
				}
				if (!rec.OfflineRecord)
				{
					return "allow offline sessions for this game first";
				}
				if (OfflineLive(gameId) || rec.OfflineSession != null)
				{
					return "the last offline session's files are still being removed";
				}
				if (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.RecorderExe != null || rec.RecorderMoveFrom != null || rec.RecorderRollback)
				{
					return "a recorder's removal is still pending";
				}
				lock (_lock)
				{
					if (_current == gameId)
					{
						return "a compile of it is in progress";
					}
				}
				if (GameRunning(g))
				{
					return "it is running: close the game first";
				}
				if (_proxyDll == null || !File.Exists(_proxyDll))
				{
					return "the recorder (" + dll + ") wasn't found next to SCSKiller";
				}
				if (stamp == null)
				{
					return "its exe can't be read";
				}
				if (GameFiles.DetectAntiCheat(g) != AntiCheat.EasyAntiCheat || GameFiles.DetectAntiCheat(g, quick: false, null, AntiCheat.EasyAntiCheat) != AntiCheat.None)
				{
					return "its install has another anti-cheat, or couldn't be read whole";
				}
				string text2 = RecorderOwnFiles.Concat(placed).FirstOrDefault((string f) => Path.Exists(Path.Combine(dir, f)));
				if (text2 != null)
				{
					return text2 + " is already in its folder";
				}
				if (!addAppId && File.ReadAllText(appIdFile).Trim() != entry.AppId)
				{
					return "its folder has a steam_appid.txt with another app id";
				}
				ReShadeInstall reShadeInstall = ReShade.Detect(g);
				if ((object)reShadeInstall != null)
				{
					return "ReShade (" + Path.GetFileName(reShadeInstall.Dll) + ") is in its folder";
				}
				if (!ProcessNames().Contains("steam"))
				{
					return "Start Steam first (offline mode is fine).";
				}
				return null;
			}
		}
	}

	internal static string BoundTo(int pid, long created)
	{
		return $"pid={pid}\r\npid_time={created}\r\n";
	}

	private static string Hex(string text)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
	}

	private static void Place(string path, Action<string> write, bool replace = false)
	{
		string text = path + ".scskiller-new";
		try
		{
			write(text);
			File.Move(text, path, replace);
		}
		catch
		{
			try
			{
				File.Delete(text);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
			throw;
		}
	}

	private static void WriteNew(string path, string text)
	{
		using FileStream fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
		fileStream.Write(Encoding.UTF8.GetBytes(text));
	}

	private async Task EndOffline(Game g, SafeProcessHandle process, DateTime since, bool risk, string neverSaw)
	{
		try
		{
			await Task.Factory.StartNew(() => WaitForSingleObject(process, uint.MaxValue), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		}
		finally
		{
			_offline.TryRemove(g.Id, out SafeProcessHandle _);
			process.Dispose();
		}
		string kind = (risk ? "session" : "offline session");
		RecorderLog(g.Name + ": the " + kind + "'s game exited");
		bool admitted = Admitted(g, since, risk);
		bool flag = CleanOffline(g.Id);
		while (!flag & risk)
		{
			OfflineSession offlineSession = Store.LoadGame(g.Id).OfflineSession;
			if ((object)offlineSession == null || !OfflineRuns(offlineSession))
			{
				break;
			}
			await Task.Delay(2000);
			flag = CleanOffline(g.Id);
		}
		if (!flag)
		{
			RecorderLog(g.Name + ": the " + kind + "'s files that are left go when nothing runs from the game folder");
		}
		if (!admitted)
		{
			string text = ((File.GetLastWriteTimeUtc(RefusedFile(g.ExePath)) < since) ? null : Refused(g.ExePath));
			_sessionNotes[g.Id] = SessionNote(text, neverSaw);
			RecorderLog($"{g.Name}: the {kind} recorded nothing: {((text != null) ? ("the recorder refused it: " + text) : "the recorder never loaded, no scskiller.log written during the run")}");
		}
		Refresh(g);
	}

	private ConcurrentDictionary<string, bool> OfflineLeft()
	{
		ConcurrentDictionary<string, bool> offlineLeft = _offlineLeft;
		if (offlineLeft != null)
		{
			return offlineLeft;
		}
		ConcurrentDictionary<string, bool> concurrentDictionary = new ConcurrentDictionary<string, bool>();
		string path = Path.Combine(Store.DataDir, "games");
		string[] array = (Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>());
		foreach (string path2 in array)
		{
			try
			{
				OfflineSession offlineSession = Store.LoadGame(AppStore.GameId(Path.GetFileName(path2))).OfflineSession;
				if ((object)offlineSession != null)
				{
					concurrentDictionary[offlineSession.GameId] = true;
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException) ? true : false)
			{
			}
		}
		return Interlocked.CompareExchange(ref _offlineLeft, concurrentDictionary, null) ?? concurrentDictionary;
	}

	private void CleanOfflineSessions()
	{
		foreach (string key in OfflineLeft().Keys)
		{
			try
			{
				if (CleanOffline(key))
				{
					_offlineLeft.TryRemove(key, out var _);
				}
			}
			catch (Exception ex)
			{
				RecorderLog(key + ": cleaning up after an offline session failed: " + ex.Message);
			}
		}
	}

	private bool CleanOffline(string id)
	{
		if (OfflineLive(id))
		{
			return false;
		}
		bool flag;
		lock (_recorderLock)
		{
			flag = !OfflineLive(id) && CleanOfflineSession(Store, id, OfflineRuns, RecorderLog) == null;
		}
		if (flag)
		{
			_offlineLeft?.TryRemove(id, out var _);
		}
		if (flag)
		{
			GameState gameState = Games.FirstOrDefault((GameState s) => s.Game.Id == id);
			if ((object)gameState != null)
			{
				Refresh(gameState.Game);
				StartSharing(new _003C_003Ez__ReadOnlySingleElementList<Game>(gameState.Game));
			}
		}
		return flag;
	}

	internal static List<string>? CleanOfflineSession(AppStore store, string id, Func<OfflineSession, bool> runs, Action<string> log)
	{
		string dir;
		List<string> left;
		using (new AppStore.PathGate(Path.Combine(store.GameDir(id), "offline")))
		{
			MigrateInis(store);
			GameRecord gameRecord = store.LoadGame(id);
			OfflineSession s = gameRecord.OfflineSession;
			if ((object)s == null)
			{
				RunOnce(id, null);
				return null;
			}
			string text = OfflineEac.Current.FirstOrDefault((OfflineEacGame e) => e.Id == id || e.Exe.Equals(Path.GetFileName(s.Exe), StringComparison.OrdinalIgnoreCase))?.Name ?? id;
			if (Foreign(Path.GetDirectoryName(s.Exe)))
			{
				log(text + ": the offline session in " + Path.GetDirectoryName(s.Exe) + " is another SCSKiller copy's: left to it");
				gameRecord.OfflineSession = null;
				store.SaveGame(id, gameRecord);
				return null;
			}
			if (Alive(s))
			{
				if (s.Resumed || !EndSuspended(s))
				{
					return gameRecord.RecorderFiles.Keys.ToList();
				}
				log($"{text}: the offline session's process {s.Pid} was never resumed: ended");
			}
			dir = Path.GetDirectoryName(s.Exe);
			GameRecord gameRecord4;
			GameRecord gameRecord3;
			if (!Directory.Exists(dir))
			{
				if (!Gone(dir))
				{
					int num = 1;
					List<string> list = new List<string>(num);
					CollectionsMarshal.SetCount(list, num);
					CollectionsMarshal.AsSpan(list)[0] = dir;
					return list;
				}
				log(text + ": offline session cleaned up: its game folder " + dir + " is gone");
				GameRecord gameRecord2 = gameRecord;
				gameRecord3 = gameRecord;
				gameRecord4 = gameRecord;
				Dictionary<string, string> recorderFiles = new Dictionary<string, string>();
				gameRecord.OfflineSession = null;
				gameRecord2.RecorderFiles = recorderFiles;
				gameRecord3.RecorderExe = null;
				gameRecord4.RecorderInstallDir = null;
				store.SaveGame(id, gameRecord);
				RunOnce(id, null);
				return null;
			}
			left = new List<string>();
			HashSet<string> data = RecorderDataFiles.Concat(Recordings.AppFiles).Append("scskiller.ledger").ToHashSet(StringComparer.OrdinalIgnoreCase);
			HashSet<string> hashSet = (s.Created ?? Array.Empty<string>()).Where((string f) => !data.Contains(f) && !f.Equals("scskiller.ini", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
			try
			{
				StripIni(dir, store, null, s.InstallDir, text, log);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				left.Add("scskiller.ini");
				log($"{text}: couldn't take {Path.Combine(dir, "scskiller.ini")} out ({ex.Message}): tried again later");
			}
			string[] array = new string[2] { "d3d12.dll", "d3d11.dll" };
			foreach (string text2 in array)
			{
				try
				{
					if (IsOurProxy(Path.Combine(dir, text2)))
					{
						File.Delete(Path.Combine(dir, text2));
					}
				}
				catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
				{
					left.Add(text2);
				}
			}
			HashSet<string> hashSet2;
			try
			{
				hashSet2 = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>()
					.ToHashSet(StringComparer.OrdinalIgnoreCase);
			}
			catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException || ex3 is SecurityException) ? true : false)
			{
				hashSet2 = null;
			}
			foreach (var (text5, text6) in gameRecord.RecorderFiles.Where((KeyValuePair<string, string> f) => !f.Key.Equals("scskiller.ini", StringComparison.OrdinalIgnoreCase)).ToList())
			{
				string text7 = Path.Combine(dir, text5);
				try
				{
					if (hashSet2 == null)
					{
						left.Add(text5);
						continue;
					}
					if (!hashSet2.Contains(text5))
					{
						gameRecord.RecorderFiles.Remove(text5);
						continue;
					}
					bool flag = !Named(text5);
					if (flag)
					{
						bool flag2 = ((text5 == "d3d12.dll" || text5 == "d3d11.dll") ? true : false);
						flag = !flag2 || !IsOurProxy(text7);
					}
					if (flag && Sha256(text7) != text6)
					{
						log(text + ": left " + text7 + ": not the file the offline session placed");
						gameRecord.RecorderFiles.Remove(text5);
					}
					else
					{
						File.Delete(text7);
						hashSet2.Remove(text5);
						gameRecord.RecorderFiles.Remove(text5);
					}
				}
				catch (Exception ex4) when ((ex4 is IOException || ex4 is UnauthorizedAccessException) ? true : false)
				{
					left.Add(text5);
				}
			}
			foreach (string item in hashSet)
			{
				string text8 = Path.Combine(dir, item);
				try
				{
					if (hashSet2 == null)
					{
						left.Add(item);
					}
					else if (hashSet2.Contains(item))
					{
						bool flag2 = Named(item);
						if (!flag2)
						{
							bool flag3 = ((item == "d3d12.dll" || item == "d3d11.dll") ? true : false);
							flag2 = flag3 && IsOurProxy(text8);
						}
						if (flag2 || (gameRecord.RecorderFiles.TryGetValue(item, out string value) && Sha256(text8) == value))
						{
							Delete(item);
						}
						else
						{
							log(text + ": left " + text8 + ": not SCSKiller's");
						}
					}
				}
				catch (Exception ex5) when ((ex5 is IOException || ex5 is UnauthorizedAccessException) ? true : false)
				{
					left.Add(item);
				}
			}
			if (IniPending(store, dir))
			{
				left.Add("scskiller.ini");
			}
			if (s.Created != null)
			{
				OfflineSession offlineSession = s with
				{
					Created = s.Created.Where((string f) => left.Contains(f) || (data.Contains(f) && File.Exists(Path.Combine(dir, f)))).ToArray()
				};
				OfflineSession offlineSession2 = offlineSession;
				s = offlineSession;
				gameRecord.OfflineSession = offlineSession2;
			}
			store.SaveGame(id, gameRecord);
			if (left.Count > 0)
			{
				return left;
			}
			bool flag4 = runs(s);
			FileInfo fileInfo = new FileInfo(Path.Combine(dir, "scskiller.db"));
			if (!flag4 && fileInfo != null && fileInfo.Exists && fileInfo.Length > 0)
			{
				try
				{
					string store2 = Path.Combine(store.GameDir(id), "recording.db");
					using (Recordings.Lock(store2))
					{
						Recordings.Merge(store2, fileInfo.FullName, null);
					}
					gameRecord.RecordingImportedAt = DateTimeOffset.Now;
				}
				catch (Exception ex6) when ((ex6 is IOException || ex6 is UnauthorizedAccessException || ex6 is InvalidDataException) ? true : false)
				{
					log(text + ": the offline session's recording couldn't be imported (" + ex6.Message + "): its files stay until it is");
					flag4 = true;
				}
			}
			if (!flag4)
			{
				try
				{
					Directory.CreateDirectory(OfflineSessionDir(store, id));
					MoveSessionFiles(dir, OfflineSessionDir(store, id));
				}
				catch (Exception ex7) when ((ex7 is IOException || ex7 is UnauthorizedAccessException) ? true : false)
				{
					log(text + ": the offline session's report couldn't be kept (" + ex7.Message + "): its files stay until it is");
					flag4 = true;
				}
			}
			List<string> list2 = data.Where((string f) => File.Exists(Path.Combine(dir, f))).ToList();
			if (!flag4)
			{
				list2.Where((string f) => !f.Equals("scskiller.ledger", StringComparison.OrdinalIgnoreCase)).ToList().ForEach(Delete);
				if (left.Count == 0 && list2.Contains("scskiller.ledger", StringComparer.OrdinalIgnoreCase))
				{
					Delete("scskiller.ledger");
				}
			}
			else
			{
				left.AddRange(list2);
			}
			if (left.Count > 0)
			{
				store.SaveGame(id, gameRecord);
				return left;
			}
			log(text + ": offline session cleaned up");
			gameRecord4 = gameRecord;
			gameRecord3 = gameRecord;
			gameRecord.OfflineSession = null;
			gameRecord4.RecorderExe = null;
			gameRecord3.RecorderInstallDir = null;
			store.SaveGame(id, gameRecord);
			RunOnce(id, null);
			return null;
		}
		void Delete(string file)
		{
			try
			{
				File.Delete(Path.Combine(dir, file));
			}
			catch (Exception ex8) when ((ex8 is IOException || ex8 is UnauthorizedAccessException) ? true : false)
			{
				left.Add(file);
			}
		}
		static bool Named(string file)
		{
			if (!file.StartsWith("scskiller", StringComparison.OrdinalIgnoreCase))
			{
				return file.EndsWith(".scskiller-new", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
	}

	private static bool Alive(OfflineSession s)
	{
		if (s.Pid == 0)
		{
			return false;
		}
		using SafeProcessHandle safeProcessHandle = OpenProcess(1052672u, inherit: false, s.Pid);
		long created;
		long exited;
		long kernel;
		long user;
		return !safeProcessHandle.IsInvalid && GetProcessTimes(safeProcessHandle, out created, out exited, out kernel, out user) && created == s.Started && WaitForSingleObject(safeProcessHandle, 0u) == 258;
	}

	internal static bool Gone(string dir)
	{
		while (true)
		{
			string directoryName = Path.GetDirectoryName(dir);
			if (directoryName == null)
			{
				break;
			}
			try
			{
				return !Directory.EnumerateFileSystemEntries(directoryName).Any((string e) => Path.GetFileName(e).Equals(Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase));
			}
			catch (DirectoryNotFoundException)
			{
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException || ex2 is SecurityException) ? true : false)
			{
				return false;
			}
			dir = directoryName;
		}
		return false;
	}

	private static bool EndSuspended(OfflineSession s)
	{
		using SafeProcessHandle safeProcessHandle = OpenProcess(1052673u, inherit: false, s.Pid);
		long created;
		long exited;
		long kernel;
		long user;
		return !safeProcessHandle.IsInvalid && GetProcessTimes(safeProcessHandle, out created, out exited, out kernel, out user) && created == s.Started && TerminateProcess(safeProcessHandle, 1u) && WaitForSingleObject(safeProcessHandle, 5000u) == 0;
	}

	internal static string OfflineSessionDir(AppStore store, string gameId)
	{
		return Path.Combine(store.GameDir(gameId), "offline-session");
	}

	internal static long? StartedAt(int pid)
	{
		using SafeProcessHandle safeProcessHandle = OpenProcess(4096u, inherit: false, pid);
		long created;
		long exited;
		long kernel;
		long user;
		return (!safeProcessHandle.IsInvalid && GetProcessTimes(safeProcessHandle, out created, out exited, out kernel, out user)) ? new long?(created) : ((long?)null);
	}

	public static int RunOfflineCleanup(AppStore store, string gameId, TimeSpan? giveUp = null, string? exe = null, Func<OfflineSession, bool>? othersRun = null)
	{
		if (othersRun == null)
		{
			othersRun = (OfflineSession s) => OthersRun(s, ProcessTree.Snapshot());
		}
		SafeProcessHandle app;
		using (new Mutex(initiallyOwned: false, HelperMutex(gameId)))
		{
			app = StartingApp();
			try
			{
				Stopwatch stopwatch = Stopwatch.StartNew();
				while (true)
				{
					try
					{
						OfflineSession offlineSession = store.LoadGame(gameId).OfflineSession;
						if ((object)offlineSession == null)
						{
							return 0;
						}
						if (exe != null)
						{
							RunOnce(gameId, exe);
						}
						if (offlineSession.Pid != 0 || !SettingUp())
						{
							if (offlineSession.Pid != 0)
							{
								using SafeProcessHandle safeProcessHandle = OpenProcess(1052672u, inherit: false, offlineSession.Pid);
								if (safeProcessHandle.IsInvalid || !GetProcessTimes(safeProcessHandle, out var created, out var _, out var _, out var _) || created != offlineSession.Started)
								{
									goto end_IL_00ab;
								}
								if (offlineSession.Resumed)
								{
									WaitForSingleObject(safeProcessHandle, uint.MaxValue);
									goto end_IL_00ab;
								}
								if (WaitForSingleObject(safeProcessHandle, 60000u) != 258)
								{
									goto end_IL_00ab;
								}
								OfflineSession offlineSession2 = store.LoadGame(gameId).OfflineSession;
								if ((object)offlineSession2 == null || !offlineSession2.Resumed)
								{
									goto end_IL_00ab;
								}
								goto end_IL_0051;
								end_IL_00ab:;
							}
							if (CleanOfflineSession(store, gameId, othersRun, Log) == null)
							{
								return 0;
							}
							goto IL_01a1;
						}
						end_IL_0051:;
					}
					catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
					{
						Log(gameId + ": " + ex.Message);
						goto IL_01a1;
					}
					goto IL_01cf;
					IL_01a1:
					if (stopwatch.Elapsed > (giveUp ?? TimeSpan.FromHours(24)))
					{
						break;
					}
					goto IL_01cf;
					IL_01cf:
					Thread.Sleep(SettingUp() ? 200 : 5000);
				}
				return 1;
			}
			finally
			{
				if (app != null)
				{
					((IDisposable)app).Dispose();
				}
			}
		}
		void Log(string line)
		{
			RecordersLog(store.DataDir, "offline cleanup: " + line);
		}
		bool SettingUp()
		{
			if (app != null)
			{
				return WaitForSingleObject(app, 0u) == 258;
			}
			return false;
		}
	}

	private static SafeProcessHandle? StartingApp()
	{
		List<(int, int, string)> source = ProcessTree.Snapshot();
		(int Pid, int Parent, string Exe) me = source.FirstOrDefault(((int Pid, int Parent, string Exe) p) => p.Pid == Environment.ProcessId);
		if (me.Parent != 0)
		{
			(int, int, string) tuple = source.FirstOrDefault(((int Pid, int Parent, string Exe) p) => p.Pid == me.Parent);
			if (tuple.Item1 != 0 && tuple.Item3.Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
			{
				SafeProcessHandle safeProcessHandle = OpenProcess(1048576u, inherit: false, tuple.Item1);
				if (!safeProcessHandle.IsInvalid)
				{
					return safeProcessHandle;
				}
				safeProcessHandle.Dispose();
				return null;
			}
		}
		return null;
	}

	internal static (string Name, string Command) RunOnceEntry(string gameId, string exe)
	{
		return (Name: "!SCSKiller offline cleanup " + gameId, Command: $"\"{exe}\" {"--offline-cleanup"} \"{gameId}\"");
	}

	private static void RunOnce(string gameId, string? exe)
	{
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce");
			var (name, value) = RunOnceEntry(gameId, exe ?? "");
			if (exe != null)
			{
				registryKey.SetValue(name, value);
			}
			else if (registryKey.GetValue(name) != null)
			{
				registryKey.DeleteValue(name, throwOnMissingValue: false);
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException) ? true : false)
		{
		}
	}

	internal static SafeProcessHandle StartAttested(string exe, string args, Action<SafeProcessHandle, int, long> setUp, Action? resumed = null)
	{
		StartupInfo si = new StartupInfo
		{
			Size = Marshal.SizeOf<StartupInfo>()
		};
		StringBuilder cmd = new StringBuilder("\"" + exe + "\"" + ((args.Length > 0) ? (" " + args) : ""));
		if (!CreateProcessW(exe, cmd, 0, 0, inherit: false, 4u, 0, Path.GetDirectoryName(exe), ref si, out var pi))
		{
			throw new Win32Exception();
		}
		SafeProcessHandle safeProcessHandle = new SafeProcessHandle(pi.Process, ownsHandle: true);
		try
		{
			if (!GetProcessTimes(safeProcessHandle, out var created, out var _, out var _, out var _))
			{
				throw new Win32Exception();
			}
			setUp(safeProcessHandle, pi.ProcessId, created);
			if (ResumeThread(pi.Thread) == uint.MaxValue)
			{
				throw new Win32Exception();
			}
			resumed?.Invoke();
			return safeProcessHandle;
		}
		catch
		{
			TerminateProcess(safeProcessHandle, 1u);
			safeProcessHandle.Dispose();
			throw;
		}
		finally
		{
			CloseHandle(pi.Thread);
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CreateProcessW(string app, StringBuilder cmd, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint env, string dir, ref StartupInfo si, out ProcessInformation pi);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint ResumeThread(nint thread);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool TerminateProcess(SafeProcessHandle process, uint code);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

	[DllImport("kernel32.dll")]
	private static extern uint WaitForSingleObject(SafeProcessHandle h, uint ms);

	static ScsKiller()
	{
		string[] recorderDataFiles = RecorderDataFiles;
		string[] appFiles = Recordings.AppFiles;
		int num = 0;
		string[] array = new string[13 + (recorderDataFiles.Length + appFiles.Length)];
		ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(recorderDataFiles);
		readOnlySpan.CopyTo(new Span<string>(array).Slice(num, readOnlySpan.Length));
		num += readOnlySpan.Length;
		ReadOnlySpan<string> readOnlySpan2 = new ReadOnlySpan<string>(appFiles);
		readOnlySpan2.CopyTo(new Span<string>(array).Slice(num, readOnlySpan2.Length));
		num += readOnlySpan2.Length;
		array[num] = "d3d12.dll";
		num++;
		array[num] = "d3d11.dll";
		num++;
		array[num] = "scskiller.ini";
		num++;
		array[num] = "d3d12.scskiller-next.dll";
		num++;
		array[num] = "d3d11.scskiller-next.dll";
		num++;
		array[num] = "amd_ags_real.dll";
		num++;
		array[num] = "scskiller.armed";
		num++;
		array[num] = "d3d12.dll.scskiller-new";
		num++;
		array[num] = "d3d11.dll.scskiller-new";
		num++;
		array[num] = "amd_ags_x64.dll.scskiller-new";
		num++;
		array[num] = "scskiller.ini.scskiller-new";
		num++;
		array[num] = "scskiller.ledger";
		num++;
		array[num] = "scskiller.ledger.scskiller-new";
		RecorderOwnFiles = new HashSet<string>(new _003C_003Ez__ReadOnlyArray<string>(array), StringComparer.OrdinalIgnoreCase);
		DataTypes = new HashSet<string>(new _003C_003Ez__ReadOnlyArray<string>(new string[23]
		{
			".log", ".ini", ".txt", ".json", ".cfg", ".xml", ".csv", ".md", ".html", ".png",
			".jpg", ".jpeg", ".bmp", ".dds", ".tga", ".sav", ".tmp", ".bak", ".dmp", ".cache",
			".cache2", ".pdb", ".error"
		}), StringComparer.OrdinalIgnoreCase);
		DefaultLedger = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller", "armed");
		StampKeys = new HashSet<string>(new _003C_003Ez__ReadOnlyArray<string>(new string[10] { "exe_size", "exe_time", "pid", "pid_time", "risk", "vendor", "overlay", "ab_play_s", "ab_tag", "sig" }), StringComparer.OrdinalIgnoreCase);
		SignedLineKeys = new HashSet<string>(new _003C_003Ez__ReadOnlyArray<string>(new string[4] { "vendor", "overlay", "ab_play_s", "ab_tag" }), StringComparer.OrdinalIgnoreCase);
		_revokedLock = new object();
	}
}
