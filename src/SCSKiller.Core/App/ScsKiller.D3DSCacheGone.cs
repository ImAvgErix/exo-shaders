using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.App;

public sealed partial class ScsKiller
{
    /// <summary>
    /// NVIDIA from 621: the last complete warm's D3DSCache dbs went into the game's own folder and it holds none now, as
    /// Windows' cleanup leaves it (<see cref="WindowsCleanup"/>), and no other folder holds dbs for the game's exe. Kept on
    /// the record until a later warm: the game's next run refills the folder with what it compiles itself, not the warm.
    /// </summary>
    bool D3DSCacheGone(Game g, GameRecord r)
    {
        if (!OperatingSystem.IsWindows() || Vendor.Vendor != GpuVendor.Nvidia || r.WarmedAt is not { } warmed) return false;
        if (r.D3DSCacheEmptiedAt > warmed) return true;
        // the warm's dbs wait for their fold (D3DSCachePending): the folder fills at the game's exit or the next start
        if (D3DSCachePending(g.Id, r)) return false;
        var root = Path.Combine(LocalAppData, "D3DSCache");
        if (!D3DSCacheEmptied(root, r)) return false;
        if (D3DSCache.GameFolder(root, g) is { } found)   // the exe's folder is another now: remembered, so the next look reads that one
        {
            (r.D3DSCacheFolder, r.D3DSCacheExe) = (Path.GetFileName(found.Dir), found.Exe);
            GameD3DSCache(g);
            return false;
        }
        var at = DateTimeOffset.UtcNow;
        r.D3DSCacheEmptiedAt = at;
        AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
        {
            var rec = Store.LoadGame(g.Id);
            if (rec.WarmedAt != warmed) return;   // a warm finished meanwhile
            rec.D3DSCacheEmptiedAt = at;
            Store.SaveGame(g.Id, rec);
        });
        Log?.Report($"{g.Name}: its D3DSCache folder {r.D3DSCacheFolder ?? r.D3DSCacheMade} was emptied, so the compiled pipelines there are gone"
                    + (WindowsCleanup.Check().Automatic ? " (Windows' automatic cleanup can delete them)" : ""));
        return true;
    }

    /// <summary>The last complete warm (NVIDIA 621 or later, D3D12) left its D3DSCache dbs in the game's own folder under
    /// <paramref name="root"/>, and that folder holds no db now. A link, or a folder that can't be listed, is never judged.</summary>
    internal static bool D3DSCacheEmptied(string root, GameRecord r)
    {
        if (r is not { WarmedAt: not null, WarmedD3DSCache: true } || r.Plan?.Platform == "vulkan" || !(NvidiaMajor(r.WarmedDriverVersion) >= 621)) return false;
        if ((r.D3DSCacheFolder ?? r.D3DSCacheMade) is not { } name || !IsFolderName(name)) return false;
        var dir = Path.Combine(root, name);
        try
        {
            return !Directory.Exists(dir)
                || !new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint) && !Directory.EnumerateFiles(dir, "*.dxcache").Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
