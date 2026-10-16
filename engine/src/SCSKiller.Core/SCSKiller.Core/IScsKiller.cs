using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SCSKiller.Core.App;

namespace SCSKiller.Core;

public interface IScsKiller
{
	IGpuVendorBackend Vendor { get; }

	IReadOnlyList<GpuChoice> GpuChoices => Array.Empty<GpuChoice>();

	bool HasIntelGpu => false;

	Settings Settings { get; set; }

	IReadOnlyList<GameState> Games { get; }

	IReadOnlyList<QueueItem> Queue { get; }

	bool QueueRunning { get; }

	bool Compiling { get; }

	IReadOnlyList<Game> HiddenGames { get; }

	event Action<GameState>? GameChanged;

	event Action<QueueItem>? QueueChanged;

	IReadOnlyList<FrameSession> FrameHistory(string gameId)
	{
		return Array.Empty<FrameSession>();
	}

	Task<IReadOnlyList<GameState>> ScanAsync(CancellationToken ct, bool userRequested = false);

	Task<IReadOnlyList<GameState>> RescanAsync(CancellationToken ct, bool userRequested = false);

	void Enqueue(string gameId);

	void StartQueue();

	void Compile(string gameId);

	void CompileMax(string gameId);

	void MoveInQueue(string gameId, int index);

	void EnqueueWhenIdle(string gameId);

	void Remove(string gameId);

	void PauseQueue();

	void ResumeQueue();

	void StopQueue();

	IReadOnlyList<GameState> StaleGames();

	IReadOnlyList<GameState> DriverStaleGames();

	void DismissStale();

	bool ShouldNotifyStale();

	void ApplyDriverUpdateMode();

	bool SetEncryptionKey(string gameId, string key);

	IReadOnlyList<CachePart> GameCaches(string gameId, bool gamePrecache = false);

	bool ClearGameCache(string gameId, bool gamePrecache = false);

	void SetCarefulCompile(string gameId, bool on);

	void SetNewShadersMode(string gameId, NewShadersMode? value);

	void InstallRecorder(string gameId);

	void UninstallRecorder(string gameId);

	void RefreshGame(string gameId);

	void RefreshCacheSizes();

	void SetRecorderOverride(string gameId, RecorderOverride value);

	void SetRecordAlongsideMod(string gameId, bool on);

	void SetOfflineRecording(string gameId, bool on);

	Task StartOfflineSession(string gameId, bool confirmed);

	Task<AntiCheat> CheckAntiCheatAsync(string gameId);

	Task StartRiskSession(string gameId, bool confirmed, AntiCheat shown);

	void ReconcileRecorders(string? gameId = null);

	ManualAdd PreviewManualGame(string exePath);

	string? ManualFolderProblem(string exePath, string installDir);

	ManualAdd AddManualGame(string exePath, string? installDir = null);

	void RemoveManualGame(string gameId);

	void HideGame(string gameId);

	void ShowGame(string gameId);

	bool ClearRecording(string gameId);

	CacheInventory CacheInventory();

	CacheDeleteResult DeleteCaches(IReadOnlyList<CacheItem> items);

	long OldDriverBytes();

	void DropKeptCache(string gameId)
	{
	}

	void PutBackKept(string gameId)
	{
	}

	void SetApiOverride(string gameId, DxApi? value);

	void SetGameMaximum(string gameId, bool on);

	void SetGameRayTracing(string gameId, bool? on);

	void SetGameLeftOutOfAddAll(string gameId, bool on);

	string? BeforeAfterBlocked(string gameId);

	void StartBeforeAfter(string gameId, int minutes, bool overlay = true);

	void BeforeAfterRecorderReady(string gameId);

	void ContinueBeforeAfter(string gameId);

	void RecordAfterAgain(string gameId);

	void EndBeforeAfter(string gameId, bool compile);

	BeforeAfterResult? BeforeAfterReport(string gameId);

	CompatSent? ReportSent(string gameId);

	Task<string?> SendReportAsync(string gameId, CompatChoice choice, bool frameGen, bool upscaler, bool mods, CancellationToken ct = default(CancellationToken));

	string ProblemReport(string gameId, string store, IReadOnlyList<(string Label, string Value)> page, bool withLog);

	Task<(string? Id, string? Problem)> SendProblemAsync(string message, string report, CancellationToken ct = default(CancellationToken));
}
