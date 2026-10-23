namespace SCSKiller.Core.Vulkan;

/// <summary>One Fossilize archive a Vulkan plan reads. <see cref="Apps"/>: only pipelines linked to these application infos
/// (a download that holds several buckets' pipelines); null = all of them. Shader modules from a download aren't
/// <see cref="Trusted"/>: the replay validates them before the driver gets them.</summary>
public sealed record FozSource(string Path, FozOrigin Origin)
{
    public IReadOnlySet<ulong>? Apps { get; init; }

    public bool Trusted => Origin != FozOrigin.SteamDownload;
}
