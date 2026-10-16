using System;
using System.Collections.Generic;

namespace SCSKiller.Core.App;

public sealed class GameRecord
{
	public string? IndexContentHash { get; set; }

	public string? IndexGameVersion { get; set; }

	public string? IndexExeStamp { get; set; }

	public string? KeysIndexHash { get; set; }

	public int? ShaderCount { get; set; }

	public Plan? Plan { get; set; }

	public DateTimeOffset? PlanBuiltAt { get; set; }

	public int PlanVersion { get; set; }

	public int WarmedPlanVersion { get; set; }

	public int? PlanReaderVersion { get; set; }

	public int? WarmedReaderVersion { get; set; }

	public string? PlanRules { get; set; }

	public string? WarmedPlanRules { get; set; }

	public string? IdleHeldBack { get; set; }

	public string? PlanItems { get; set; }

	public string? WarmedPlanItems { get; set; }

	public string? PlanKeysFile { get; set; }

	public string? WarmKeysFile { get; set; }

	public PendingCount? Pending { get; set; }

	public bool PlanPerStage { get; set; }

	public bool? PlanMaximumCapped { get; set; }

	public bool WarmedPerStage { get; set; }

	public string? PlanMiddleware { get; set; }

	public string? PlanCommunity { get; set; }

	public string? PlanOfficial { get; set; }

	public string? PlanMaps { get; set; }

	public long ResumeAt { get; set; }

	public long ResumeItems { get; set; }

	public double ResumeSeconds { get; set; }

	public long ResumeFailed { get; set; }

	public long ResumeDriverSkipped { get; set; }

	public string? ResumeGpu { get; set; }

	public string? ResumeLayer { get; set; }

	public string? ResumeExe { get; set; }

	public string? ResumeAgs { get; set; }

	public bool ResumeLeftOut { get; set; }

	public string? WarmedDriverVersion { get; set; }

	public string? WarmedDriverId { get; set; }

	public DateTimeOffset? WarmedAt { get; set; }

	public TimeSpan? LastWarmTime { get; set; }

	public long? LastCacheGrowthBytes { get; set; }

	public long? LastWarmFailed { get; set; }

	public long? LastWarmSkipped { get; set; }

	public long? LastWarmNeedsRecording { get; set; }

	public long? LastWarmCrashed { get; set; }

	public long? LastWarmDriverSkipped { get; set; }

	public long? LastWarmUnbuilt { get; set; }

	public HashSet<string> CrashKeys { get; set; } = new HashSet<string>();

	public string? CrashKeysDriver { get; set; }

	public string? WarmedIndexHash { get; set; }

	public string? WarmedExeStamp { get; set; }

	public string? WarmedGameVersion { get; set; }

	public string? WarmedLayer { get; set; }

	public string? WarmedIndexStamp { get; set; }

	public double? BytesPerPso { get; set; }

	public double? PsoPerSecond { get; set; }

	public DateTimeOffset? RecordingImportedAt { get; set; }

	public string? RecordingInbox { get; set; }

	public string? RecordingIndexHash { get; set; }

	public Dictionary<string, string> RecorderFiles { get; set; } = new Dictionary<string, string>();

	public bool IniIsUsers { get; set; }

	public string? RecorderExe { get; set; }

	public string? RecorderInstallDir { get; set; }

	public string? RecorderMoveFrom { get; set; }

	public string? RunsExe { get; set; }

	public string? RunsExeFrom { get; set; }

	public string? RunsExeBuild { get; set; }

	public DateTimeOffset? RunsExeAt { get; set; }

	public string? RunsExeWas { get; set; }

	public string? RecorderMoveTo { get; set; }

	public DateTimeOffset? RecorderGoneSince { get; set; }

	public RecorderOverride? Recorder { get; set; }

	public bool RecordAlongsideMod { get; set; }

	public ChainedDll? RecorderChained { get; set; }

	public HashSet<string> RecorderOrphans { get; set; } = new HashSet<string>();

	public bool RecorderRollback { get; set; }

	public bool RecorderClaimed { get; set; }

	public bool OfflineRecord { get; set; }

	public OfflineSession? OfflineSession { get; set; }

	public bool KeysPending { get; set; }

	public HashSet<string> CacheKeys { get; set; } = new HashSet<string>();

	public HashSet<string> GameKeys { get; set; } = new HashSet<string>();

	public HashSet<string>? WarmedKeys { get; set; }

	public HashSet<string>? WarmedFiles { get; set; }

	public Dictionary<string, long>? WarmedFileBytes { get; set; }

	public Dictionary<string, string>? WarmedFileIds { get; set; }

	public string? LaunchedExeName { get; set; }

	public DateTimeOffset? LaunchedExeSeenAt { get; set; }

	public string? WarmedExeName { get; set; }

	public string? WarmedAgsApp { get; set; }

	public bool AgsMissed { get; set; }

	public string? AgsSeenApp { get; set; }

	public string? AgsSeenEngine { get; set; }

	public bool AgsIneligible { get; set; }

	public bool Careful { get; set; }

	public NewShadersMode? NewShaders { get; set; }

	public bool? Maximum { get; set; }

	public bool? RayTracing { get; set; }

	public bool LeftOutOfAddAll { get; set; }

	public bool Hidden { get; set; }

	public bool PlanRayTracing { get; set; } = true;

	public bool WarmedRayTracing { get; set; } = true;

	public bool WarmedCareful { get; set; }

	public LaunchCheck? FirstLaunch { get; set; }

	public DateTimeOffset? UnreachedWarm { get; set; }

	public DateTimeOffset? CacheClearedAt { get; set; }

	public PlayWindow? LastPlay { get; set; }

	public bool RecordedLong { get; set; }

	public bool RtUnseen { get; set; }

	public DxApi? ApiOverride { get; set; }

	public string? PlanApi { get; set; }

	public string? PlanEngine { get; set; }

	public int? PlanFiltering { get; set; }

	public string? CompileFailedBuild { get; set; }

	public BeforeAfterRecord? BeforeAfter { get; set; }
}
