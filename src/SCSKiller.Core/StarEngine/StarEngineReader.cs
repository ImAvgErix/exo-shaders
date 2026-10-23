namespace SCSKiller.Core.StarEngine;

/// <summary>Cloud Imperium's StarEngine (Star Citizen): recognized by its exe ({channel}\Bin64\StarCitizen.exe beside
/// {channel}\Data.p4k), so the game shows with its engine and the Vulkan renderer it offers. Its shaders aren't read yet:
/// Index is empty. EngineInfo: Family "StarEngine", GraphicsApi "Vulkan" (guessed: the game picks it in its settings).</summary>
public sealed class StarEngineReader : IEngineReader
{
    public const string Family = "StarEngine", Exe = "StarCitizen.exe";

    /// <summary>The release channel (LIVE, PTU, ...) the exe is in; null if it isn't Star Citizen's.</summary>
    public static string? Channel(string exe)
    {
        if (!Path.GetFileName(exe).Equals(Exe, StringComparison.OrdinalIgnoreCase)) return null;
        if (Path.GetDirectoryName(exe) is not { } bin || !Path.GetFileName(bin).Equals("Bin64", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.GetDirectoryName(bin) is { } channel && File.Exists(Path.Combine(channel, "Data.p4k")) ? Path.GetFileName(channel) : null;
    }

    public EngineInfo? Detect(Game game) =>
        Channel(game.ExePath) != null ? new EngineInfo(Family, "", null, "Vulkan", false, null, ApiGuessed: true, OffersVulkan: true) : null;

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) =>
        new(ShaderIndex.EmptyContentHash, [], new Dictionary<string, ShaderInfo>(), []);

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
}
