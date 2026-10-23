namespace SCSKiller.Core.Warming;

/// <summary>Picks where a game's warm runs: on Windows, here (<see cref="WindowsWarmHost"/>).</summary>
static class WarmHost
{
    public static IWarmHost For(Game game, string workDir, GpuInfo gpu, IProgress<string>? log, string? steamRoot = null, bool vulkan = false) =>
        new WindowsWarmHost(workDir);

    /// <summary>Why the game's warm can't run on this PC, one line; null = it can.</summary>
    public static string? Refusal(Game game, string? steamRoot = null, bool vulkan = false) => null;
}
