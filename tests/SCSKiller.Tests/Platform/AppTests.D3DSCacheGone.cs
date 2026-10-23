using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// NVIDIA from 621 keeps compiled pipelines in D3DSCache: a game's folder emptied after its warm (Windows' cleanup, a
// delete by hand) asks for a compile again, on fake D3DSCache folders under the test's own root.
public partial class AppTests
{
    /// <summary>It fails if a warmed game whose D3DSCache folder was emptied still reads as warmed, if the game's next run
    /// refilling the folder hides it before a compile, if a compile doesn't clear it, or if no compile is started for it.</summary>
    [Fact]
    public async Task A_warmed_games_emptied_D3DSCache_folder_asks_to_compile_again_until_a_compile()
    {
        var d3ds = Path.Combine(_root, "D3DSCache");   // Killer: LocalAppData = _root
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer, vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia, DriverVersion = "621.01" }));
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var dir = FakeD3DSCache.Folder(d3ds, "1111", _game.ExePath);   // the compile's dbs, in the game's own folder
        var rec = k.Store.LoadGame(_game.Id);
        Assert.True(rec.WarmedD3DSCache);
        rec.D3DSCacheFolder = "1111";
        k.Store.SaveGame(_game.Id, rec);
        k.RefreshGame(_game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);   // Windows' cleanup
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Stale, ScsKiller.TrimmedAllReason), (k.Games.Single().Status, k.Games.Single().StatusReason));
        var runs = warmer.Started.Count;

        FakeD3DSCache.Folder(d3ds, "1111", _game.ExePath);   // the game's next run compiles a few itself
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Stale, ScsKiller.TrimmedAllReason), (k.Games.Single().Status, k.Games.Single().StatusReason));
        Assert.Equal(runs, warmer.Started.Count);   // shown, never compiled by itself

        await WarmOnce(k, _game.Id);
        k.RefreshGame(_game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    /// <summary>alpha.3's own check (ScsKiller.D3DSCacheTrimmed, ahead of D3DSCacheGone) reads only the folders on record: an
    /// emptied one is lost even while another folder holds dbs for the game's exe, which the warm's weren't keyed to.</summary>
    [Fact]
    public async Task An_emptied_D3DSCache_folder_on_record_is_lost_with_the_games_dbs_elsewhere()
    {
        var d3ds = Path.Combine(_root, "D3DSCache");
        var k = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia, DriverVersion = "621.01" }));
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        Directory.CreateDirectory(Path.Combine(d3ds, "1111"));   // the folder the warm's dbs went to, empty now
        FakeD3DSCache.Folder(d3ds, "5555", _game.ExePath);
        var rec = k.Store.LoadGame(_game.Id);
        rec.D3DSCacheFolder = "1111";
        k.Store.SaveGame(_game.Id, rec);
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Stale, ScsKiller.TrimmedAllReason), (k.Games.Single().Status, k.Games.Single().StatusReason));
    }

    /// <summary>It fails if a folder that never held the warm's dbs (a driver before 621, a warm that deleted them, no
    /// folder known), a link, or one still holding a db counts as emptied, or an empty or missing one doesn't.</summary>
    [Fact]
    public void A_D3DSCache_folder_counts_as_emptied_only_after_a_warm_left_dbs_there()
    {
        var root = Path.Combine(_root, "D3DSCache");
        GameRecord Warmed(string? folder = "2222", string? made = null, string driver = "621.01", bool kept = true) =>
            new() { WarmedAt = DateTimeOffset.UtcNow, WarmedD3DSCache = kept, WarmedDriverVersion = driver, D3DSCacheFolder = folder, D3DSCacheMade = made };

        var dir = FakeD3DSCache.Folder(root, "2222", _game.ExePath);
        Assert.False(ScsKiller.D3DSCacheEmptied(root, Warmed()));
        foreach (var f in Directory.GetFiles(dir, "*.dxcache")) File.Delete(f);   // -shm and -wal alone hold nothing
        Assert.True(ScsKiller.D3DSCacheEmptied(root, Warmed()));
        Assert.True(ScsKiller.D3DSCacheEmptied(root, Warmed("3333")));   // folder gone
        Assert.True(ScsKiller.D3DSCacheEmptied(root, Warmed(null, made: "2222")));   // made before the game's first run

        Assert.False(ScsKiller.D3DSCacheEmptied(root, Warmed(driver: "617.70")));
        Assert.False(ScsKiller.D3DSCacheEmptied(root, Warmed(driver: "32.0.16.1770")));
        Assert.False(ScsKiller.D3DSCacheEmptied(root, Warmed(kept: false)));
        Assert.False(ScsKiller.D3DSCacheEmptied(root, Warmed(null)));
        Assert.False(ScsKiller.D3DSCacheEmptied(root, Warmed(@"..\x")));
        Assert.False(ScsKiller.D3DSCacheEmptied(root, new GameRecord { WarmedD3DSCache = true, WarmedDriverVersion = "621.01", D3DSCacheFolder = "2222" }));   // never warmed

        var target = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        try { Directory.CreateSymbolicLink(Path.Combine(root, "4444"), target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }   // no symlink right here
        Assert.False(ScsKiller.D3DSCacheEmptied(root, Warmed("4444")));
    }
}
