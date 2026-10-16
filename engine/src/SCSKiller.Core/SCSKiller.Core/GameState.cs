using System;
using System.Collections.Generic;
using SCSKiller.Core.App;

namespace SCSKiller.Core;

public sealed record GameState(Game Game, EngineInfo? Engine, AntiCheat AntiCheat, GameStatus Status, string StatusReason, int? ShaderCount, PlanStats? Plan, long? EstimatedCacheBytes, TimeSpan? EstimatedWarmTime, string? WarmedDriverVersion, DateTimeOffset? WarmedAt, TimeSpan? LastWarmTime, bool RecorderInstalled, SessionStats? LastSession, long? CacheOnDisk = null, string? LaunchedExeName = null, IReadOnlyList<MiddlewareTag>? Middleware = null, long? LastWarmFailed = null, long? LastWarmSkipped = null, KnownStutter? KnownStutter = null, CommunityInfo? Community = null, DateTimeOffset? RecordingSharedAt = null, bool? InCommunityDb = null, long? NewPipelines = null, bool Playing = false, RecorderOverride RecorderOverride = RecorderOverride.Default, bool RecorderEffective = false, bool RecorderForeign = false, string? RecorderSkip = null, string? RecorderNote = null, string? RecorderRefused = null, string? RecorderMod = null, bool RecordAlongsideMod = false, long? LastWarmNeedsRecording = null, long? LastWarmCrashed = null, long? LastWarmDriverSkipped = null, CarefulCompile? Careful = null, long RecordingBytes = 0L, bool RecordingPaused = false, long RecordedSinceWarm = 0L, int CommunityDbPsos = 0, double? PsoPerSecond = null, FrameReport? LastFrames = null, bool ReShade = false, string? ShaderMod = null, bool ShaderModBlocks = false, bool ShaderModLayer = false, bool ShaderModAsD3D12 = false, bool RootUnconfirmed = false, bool StreamlineFirst = false, bool AgsHost = false, bool Vac = false, bool Records11 = false, bool RtUnseen = false, bool RtToPlan = false, bool RtSkipped = false, bool? RayTracingOverride = null, bool LeftOutOfAddAll = false, bool RecordedEnough = false, bool OfflineEligible = false, bool OfflineRecord = false, bool OfflineRunning = false, CommunityInfo? Official = null, string? NoStutter = null, bool CompileUnreached = false, bool NotPlanned = false, bool NewOnly = false, NewShadersMode? NewShadersOverride = null, DxApi? ApiOverride = null, string? ApiDetected = null, string? MaximumSuggestion = null, bool? MaximumForGame = null, long? MaximumAddsBytes = null, DateTimeOffset? PlayedAt = null, SimilarSystems? Similar = null, bool CompileFailed = false, bool RiskEligible = false, bool RiskNoLaunch = false, string? SessionNote = null, BeforeAfterInfo? BeforeAfter = null, KeptCache? Kept = null, long? LastWarmUnbuilt = null, bool Dxvk = false, bool DxvkCompiled = false)
{
	public bool AnyAntiCheat
	{
		get
		{
			if (AntiCheat == AntiCheat.None)
			{
				return Vac;
			}
			return true;
		}
	}
}
