namespace SCSKiller.Core.App;

// Exo Shaders: how much of a game's play the cache served, as D3D12 itself counts it (PipelineCacheWatch).
public sealed partial class ScsKiller
{
    /// <summary>Watch D3D12's pipeline events while the watcher runs (<see cref="PipelineCacheWatch"/>). Off: the command
    /// line and tests.</summary>
    public bool WatchPipelineCache { get; set; }

    PipelineCacheWatch? _cacheWatch;
    bool _cacheWatchTried;   // once per run: refused rights don't change while it runs
    static readonly TimeSpan CacheWatchQuiet = TimeSpan.FromMinutes(10);

    /// <summary>Each poll: the session up (tried once), and the creates' events on unless only a compile runs.</summary>
    void PipelineCacheTick(bool playing)
    {
        if (!WatchPipelineCache) return;
        if (_cacheWatch == null)
        {
            if (_cacheWatchTried) return;
            _cacheWatchTried = true;
            _cacheWatch = PipelineCacheWatch.Start(ProcessPath, Store.DataDir, out var error);
            Log?.Report(_cacheWatch != null ? "watching D3D12's pipeline events: each game's cache use is measured while it plays"
                : $"pipeline cache use isn't measured: {PipelineCacheWatch.Why(error)}");
            if (_cacheWatch == null) return;
        }
        bool compiling;
        lock (_lock) compiling = _current != null;
        if (_cacheWatch.Creates(playing || !compiling) is var e and not 0) Log?.Report($"couldn't switch D3D12's pipeline events: error {e}");
        _cacheWatch.Prune(CacheWatchQuiet);
    }

    /// <summary>A run of the game ended: its processes' creates go on its record (none seen: none, so an older run's
    /// counts don't read as this one's), and the identity D3D12 gave it, if it sent one.</summary>
    void PipelineCacheExited(Game g, GameRecord rec, DateTimeOffset at)
    {
        if (_cacheWatch == null) return;
        var (use, identity) = _cacheWatch.Take(path => OfGame(g, path), at);
        rec.LastPlayCache = use;
        if (identity != null)
        {
            if (rec.AsdIdentity is not { } was || was with { SeenAt = identity.SeenAt } != identity) Log?.Report($"{g.Name}: D3D12 identifies it as {identity}");
            rec.AsdIdentity = identity;
        }
        Log?.Report(use is { } u ? $"{g.Name}: {u.Verdict}. {u.Detail}" : $"{g.Name}: D3D12 reported no pipeline creates (not DirectX 12, or its runtime doesn't report them)");
    }

    /// <summary>A process of the game: run from its install or exe folder, or named as its exe.</summary>
    internal static bool OfGame(Game g, string path)
    {
        if (Path.GetFileName(path).Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var dir in new[] { g.InstallDir, Path.GetDirectoryName(g.ExePath) })
            if (!string.IsNullOrEmpty(dir) && path.StartsWith(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>The game's last watched run, as D3D12 counted it; null: none measured.</summary>
    public PipelineCacheUse? LastPlayCache(string gameId) => Store.LoadGame(gameId).LastPlayCache;
}
