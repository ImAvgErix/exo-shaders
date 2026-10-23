using System.Buffers.Binary;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

public class PipelineCacheTests : IDisposable
{
    readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "scskiller-pipecache-" + Guid.NewGuid().ToString("N")[..8])).FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>Event 161-163's payload as the manifest lays it out: eight UInt32s.</summary>
    static byte[] Lookups(uint required, uint psdb, uint cache, uint stores = 0, uint ignored = 0)
    {
        var b = new byte[CreateLookups.Size];
        uint[] v = [required, psdb, cache, ignored, 7, 8, 9, stores];
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    [Fact]
    public void The_lookup_payload_reads_its_eight_fields_in_the_manifests_order()
    {
        Assert.Equal(new CreateLookups(3, 1, 2, 4, 7, 8, 9, 5), CreateLookups.Read(Lookups(3, 1, 2, stores: 5, ignored: 4)));
        Assert.Null(CreateLookups.Read(new byte[CreateLookups.Size - 1]));   // another event version: not guessed at
        Assert.NotNull(CreateLookups.Read(new byte[CreateLookups.Size + 8]));   // fields added at the end: the known ones still read
    }

    /// <summary>A pipeline came from the cache when its required lookups all hit, PSDB and driver cache together; one without
    /// lookups isn't a pipeline. A begin and its end pair per thread and kind, so creates on several threads at once and a
    /// state object inside a pipeline create time right; an end without its begin (the session started mid-create) counts nothing.</summary>
    [Fact]
    public void A_tally_counts_pipelines_from_the_cache_and_times_each_create_on_its_thread()
    {
        var t = new PipelineCacheTally();
        Assert.True(t.Empty);
        t.Add(155, 1, 1_000, []);          // thread 1: CreatePipelineState begins
        t.Add(155, 2, 2_000, []);          // thread 2 too
        t.Add(157, 1, 3_000, []);          // thread 1: a CreateStateObject within it
        t.Add(158, 1, 13_000, []);         //   ... 1 ms
        t.Add(156, 2, 22_000, []);         // thread 2's ends: 2 ms
        t.Add(156, 1, 31_000, []);         // thread 1's: 3 ms
        t.Add(160, 3, 50_000, []);         // an end whose begin came before the session: nothing
        t.Add(161, 1, 0, Lookups(2, 0, 2, stores: 0));    // all from the driver cache
        t.Add(161, 2, 0, Lookups(3, 1, 1, stores: 1));    // one missed: compiled and stored
        t.Add(162, 1, 0, Lookups(1, 1, 0));               // a state object from a precompiled database
        t.Add(163, 1, 0, Lookups(2, 1, 1));               // an addition: one from each
        t.Add(161, 1, 0, Lookups(0, 0, 0));               // nothing to look up: not a pipeline
        t.Add(161, 1, 0, new byte[4]);                    // short: skipped
        t.Add(42, 1, 0, Lookups(9, 9, 9));                // another event of the provider
        Assert.False(t.Empty);

        var at = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
        var u = PipelineCacheTally.Sum([t], at)!;
        Assert.Equal(new PipelineCacheUse(at, Pipelines: 4, FromCache: 3, FromPsdb: 1, Lookups: 8, PsdbHits: 3, CacheHits: 4, Stores: 1, Creates: 3, CreateMs: 6), u);
        Assert.Equal(75, u.Percent);
        Assert.Equal("75% of pipelines came from the cache", u.Verdict);
        Assert.Equal("3 of 4 pipelines from the cache (1 precompiled by Windows), 1 compiled. Lookups: 8, 4 in the driver cache, 3 in precompiled databases; 1 stored. Creating 3 pipelines took 6 ms", u.Detail);
    }

    /// <summary>One run's processes (a game restarting itself, a launcher's child) sum into one; a run with nothing counted
    /// is none. The percentage rounds down: 99.9% isn't "100%", and no pipeline that needed a lookup has none.</summary>
    [Fact]
    public void A_runs_processes_sum_into_one_and_only_all_from_the_cache_reads_100_percent()
    {
        var at = DateTimeOffset.Now;
        Assert.Null(PipelineCacheTally.Sum([new PipelineCacheTally(), new PipelineCacheTally()], at));
        Assert.Null(PipelineCacheTally.Sum([], at));

        var a = new PipelineCacheTally();
        var b = new PipelineCacheTally();
        for (var i = 0; i < 999; i++) a.Add(161, 1, 0, Lookups(1, 0, 1));
        b.Add(161, 1, 0, Lookups(1, 0, 0, stores: 1));
        var u = PipelineCacheTally.Sum([a, b], at)!;
        Assert.Equal((1000L, 999L, 1L), (u.Pipelines, u.FromCache, u.Stores));
        Assert.Equal(99, u.Percent);

        var timedOnly = new PipelineCacheTally();
        timedOnly.Add(155, 1, 0, []);
        timedOnly.Add(156, 1, 5_000_000, []);   // 0.5 s, no lookups reported
        var t = PipelineCacheTally.Sum([timedOnly], at)!;
        Assert.Null(t.Percent);
        Assert.Equal("No pipeline needed the cache", t.Verdict);
        Assert.Equal(500, t.CreateMs);

        var all = new PipelineCacheTally();
        all.Add(161, 1, 0, Lookups(4, 4, 0));
        Assert.Equal("100% of pipelines came from the cache", PipelineCacheTally.Sum([all], at)!.Verdict);
    }

    /// <summary>ASDInit's fields, as TDH hands them (struct members as "Struct.Field"): versions are four 16-bit parts, the
    /// sources and step are named, an unknown value stays a number, and an event naming nothing is none.</summary>
    [Fact]
    public void The_ASDInit_identity_reads_names_versions_and_sources()
    {
        var at = DateTimeOffset.Now;
        var id = AsdIdentity.From(new Dictionary<string, object>
        {
            ["Step"] = 1UL,
            ["ApplicationDesc.Name"] = "Contoso Racer",
            ["ApplicationDesc.EngineName"] = "Unreal Engine",
            ["ApplicationDesc.Version"] = 0x0001_0002_0003_0004UL,
            ["ApplicationDesc.EngineVersion"] = 0x0005_0008_0000_0000UL,
            ["ApplicationDesc.ExeFilename"] = "Racer-Win64-Shipping.exe",
            ["ApplicationIdentity.ApplicationName"] = "the PSDB's, not D3D's",
            ["ApplicationDescSource"] = 2UL,
            ["DefaultPsdbSource"] = 9UL,
        }, at);
        Assert.Equal(new AsdIdentity(at, "Success", "Contoso Racer", "Unreal Engine", "1.2.3.4", "5.8.0.0", "Racer-Win64-Shipping.exe", "SetApplicationIdentity", "9"), id);
        Assert.Equal("\"Contoso Racer\" 1.2.3.4 (Unreal Engine 5.8.0.0), from SetApplicationIdentity, PSDB setup: Success", id!.ToString());

        // what this PC's own D3D12 device sent (no registration): the step alone
        Assert.Equal("ReadApplicationRegistration", AsdIdentity.From(new Dictionary<string, object> { ["Step"] = 2UL, ["ApplicationDesc.Name"] = "" }, at)!.Step);
        Assert.Null(AsdIdentity.From(new Dictionary<string, object> { ["schemaVersion"] = 3UL }, at));
    }

    /// <summary>A process counts for the game when it runs from its install or exe folder or is named as its exe; a folder
    /// whose name only starts like the install's is another game's.</summary>
    [Fact]
    public void A_process_counts_for_the_game_it_runs_from()
    {
        var g = new Game("steam:1", "Racer", Store.Steam, @"D:\Games\Racer", @"D:\Games\Racer\Binaries\Win64\Racer-Win64-Shipping.exe");
        Assert.True(ScsKiller.OfGame(g, @"D:\Games\Racer\Binaries\Win64\Racer-Win64-Shipping.exe"));
        Assert.True(ScsKiller.OfGame(g, @"d:\games\racer\Racer.exe"));                        // the launcher stub at the root
        Assert.True(ScsKiller.OfGame(g, @"E:\Moved\Racer-Win64-Shipping.exe"));               // the exe's name elsewhere
        Assert.False(ScsKiller.OfGame(g, @"D:\Games\Racer 2\Racer2.exe"));
        Assert.False(ScsKiller.OfGame(g, @"D:\Games\RacerTools\Editor.exe"));
        Assert.False(ScsKiller.OfGame(g, @"C:\Windows\explorer.exe"));
    }

    /// <summary>The run's counts and the identity go on the game's record and come back as they were.</summary>
    [Fact]
    public void The_cache_use_and_identity_are_kept_on_the_games_record()
    {
        var store = new AppStore(Path.Combine(_dir, "data"));
        var at = DateTimeOffset.Parse("2026-10-22T12:00:00+02:00");
        var rec = store.LoadGame("steam:1");
        rec.LastPlayCache = new PipelineCacheUse(at, 1234, 1135, 10, 2468, 20, 2250, 99, 1300, 3210.5);
        rec.AsdIdentity = new AsdIdentity(at, "Success", "Racer", "Unreal Engine", "1.0.0.0", "5.8.0.0", "Racer.exe", "SetApplicationIdentity", "ShaderCacheRegistration");
        store.SaveGame("steam:1", rec);

        var back = store.LoadGame("steam:1");
        Assert.Equal(rec.LastPlayCache, back.LastPlayCache);
        Assert.Equal(rec.AsdIdentity, back.AsdIdentity);
        Assert.Equal("91% of pipelines came from the cache", back.LastPlayCache!.Verdict);
    }
}
