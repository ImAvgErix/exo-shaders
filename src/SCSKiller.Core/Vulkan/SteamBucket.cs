namespace SCSKiller.Core.Vulkan;

/// <summary>One bucket of a game's Steam pipeline cache (<see cref="SteamPipelineCache.Buckets"/>): <see cref="Dir"/> is
/// fozpipelinesv6 itself (<see cref="Hash"/> null) or a steamapprun_pipeline_cache.&lt;hash&gt; folder. <see cref="Recorded"/>:
/// the archives Steam recorded on this PC; <see cref="Download"/>: Steam's downloaded archive, if any; <see cref="Apps"/>: the
/// application infos they name; <see cref="RecordedAt"/>: the newest recorded archive's write time (UTC; MinValue = none).</summary>
public sealed record SteamBucket(string Dir, ulong? Hash, IReadOnlyList<string> Recorded, string? Download, IReadOnlyList<FozApp> Apps, DateTime RecordedAt);
