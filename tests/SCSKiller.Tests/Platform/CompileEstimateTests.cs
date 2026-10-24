using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

/// <summary>
/// Ways a game's compile estimate could be wrong (System › Shaders adds them up for the queue's time):
/// - a rate measured on this driver is looked up under another key (NVIDIA 621's "|noflush"), so every estimate falls to the
///   vendor's default;
/// - an earlier build's record loses its rate's driver (none kept, or its warm forgotten by a cleared cache), or keeps a
///   rewarm's rate as the game's cold rate;
/// - a rewarm onto a cache that still held the game counts as a cold compile, so the next cold one reads as seconds;
/// - another driver's, another game's or the default rate is called learned, or a stopped compile's segments are ignored;
/// - the pools weigh a small game like a big one;
/// - reading, planning and preparing the files is left out;
/// - a stale game is estimated for every pipeline when only its new ones compile, for only the new ones when its cache may
///   no longer hold the rest, or a stopped compile's created pipelines count again;
/// - warmrates.json's vendor-only key from an earlier build is read as this driver's rate.
/// </summary>
public sealed class CompileEstimateTests
{
    const string Here = "10de:2484:87bc1043:32.0.16.2101|noflush", Plain = "10de:2484:87bc1043:32.0.16.2101", Before = "10de:2484:87bc1043:32.0.16.1770";

    static GameRecord Rec(double? rate = null, string? driver = null, long resumeItems = 0, double resumeSeconds = 0, string? resumeGpu = null) =>
        new() { PsoPerSecond = rate, PsoPerSecondDriver = driver, ResumeItems = resumeItems, ResumeSeconds = resumeSeconds, ResumeGpu = resumeGpu };

    [Fact]
    public void The_games_own_rate_on_this_driver_is_learned()
    {
        Assert.Equal(new CompileEstimate.Rate(120, true), CompileEstimate.RateFor(Rec(120, Here), Here, 300, 400, 500, 450));
    }

    [Fact]
    public void A_rate_measured_before_the_flushes_were_skipped_stands_in_but_is_not_learned()
    {
        Assert.Equal(new CompileEstimate.Rate(59, false), CompileEstimate.RateFor(Rec(59, Plain), Here, 300, 400, 500, 450));
        Assert.Equal(new CompileEstimate.Rate(125, false), CompileEstimate.RateFor(Rec(125, CompileEstimate.UnknownDriver), Here, 300, 400, 500, 450));
    }

    [Fact]
    public void A_stopped_compiles_segments_are_the_latest_measure()
    {
        // Beast of Reincarnation: 229,470 pipelines in 1 h 20 min on 617.70, an older complete compile at 97/s
        var stopped = Rec(97.35, CompileEstimate.UnknownDriver, 229_470, 4797.4, Before);
        Assert.Equal(new CompileEstimate.Rate(229_470 / 4797.4, false), CompileEstimate.RateFor(stopped, Here, null, null, null, 450));
        // on this driver (no flush skipping): learned
        Assert.True(CompileEstimate.RateFor(Rec(resumeItems: 5000, resumeSeconds: 50, resumeGpu: Plain), Plain, null, null, null, 450).Learned);
        // too few, or a rewarm's: not a measure
        Assert.Equal(450, CompileEstimate.RateFor(Rec(resumeItems: 900, resumeSeconds: 9, resumeGpu: Plain), Plain, null, null, null, 450).PerSecond);
        Assert.Equal(450, CompileEstimate.RateFor(Rec(resumeItems: 50_000, resumeSeconds: 10, resumeGpu: Plain), Plain, null, null, null, 450).PerSecond);
    }

    [Fact]
    public void Without_its_own_rate_its_engine_then_this_pc_then_the_last_warm_then_the_default_stand_in()
    {
        Assert.Equal(new CompileEstimate.Rate(300, false), CompileEstimate.RateFor(Rec(), Here, 300, 400, 500, 450));
        Assert.Equal(400, CompileEstimate.RateFor(Rec(), Here, null, 400, 500, 450).PerSecond);
        Assert.Equal(500, CompileEstimate.RateFor(Rec(), Here, null, null, 500, 450).PerSecond);
        Assert.Equal(new CompileEstimate.Rate(450, false), CompileEstimate.RateFor(Rec(), Here, null, null, null, 450));
        Assert.False(CompileEstimate.RateFor(Rec(120, Here), null, null, null, null, 450).Learned);   // no driver known: nothing is this one's
    }

    [Fact]
    public void A_pool_weighs_each_game_by_its_pipelines()
    {
        // 100,000 at 100/s (1000 s) and 10,000 at 1000/s (10 s): 110,000 in 1010 s
        Assert.Equal(110_000 / 1010.0, CompileEstimate.Pool([(100_000, 100), (10_000, 1000)]));
        Assert.Null(CompileEstimate.Pool([]));
        Assert.Null(CompileEstimate.Pool([(0, 100), (5000, 0)]));
    }

    [Fact]
    public void A_rate_only_a_cache_that_held_the_game_gives_is_a_rewarm()
    {
        Assert.True(CompileEstimate.Rewarm(148_040 / 32.7, null));    // VALORANT after the driver update (cold: 627/s)
        Assert.False(CompileEstimate.Rewarm(148_040 / 236.0, null));
        Assert.True(CompileEstimate.Rewarm(500, 100));                 // five times its own cold rate on the same driver
        Assert.False(CompileEstimate.Rewarm(300, 100));
        Assert.False(CompileEstimate.Rewarm(1500, 1000));               // a fast PC's cold compile
    }

    [Fact]
    public void An_earlier_builds_rate_keeps_its_warms_driver_or_an_unknown_one()
    {
        var r = new GameRecord { PsoPerSecond = 122.15, WarmedDriverId = Plain };
        CompileEstimate.Migrate(r);
        Assert.Equal(Plain, r.PsoPerSecondDriver);

        r = new GameRecord { PsoPerSecond = 125.4 };   // its cache was cleared: the warm and its driver forgotten
        CompileEstimate.Migrate(r);
        Assert.Equal(CompileEstimate.UnknownDriver, r.PsoPerSecondDriver);

        r = new GameRecord { PsoPerSecond = 120, PsoPerSecondDriver = Here, WarmedDriverId = Before };   // this build's: as it is
        CompileEstimate.Migrate(r);
        Assert.Equal(Here, r.PsoPerSecondDriver);
    }

    [Fact]
    public void An_earlier_builds_rewarm_rate_goes_and_its_last_compile_is_cold_only_when_it_measured_the_rate()
    {
        var plan = new Plan("g", "c", "PCD3D_SM5", "nvidia-1", new PlanStats(0, 0, 0, 0, true, D3D11Shaders: 148_040), @"C:\x\plan.bin");
        var valorant = new GameRecord { Plan = plan, PsoPerSecond = 148_040 / 32.68, LastWarmTime = TimeSpan.FromSeconds(32.68), WarmedDriverId = Plain };
        CompileEstimate.Migrate(valorant);
        Assert.Equal(((double?)null, (string?)null, (bool?)false), (valorant.PsoPerSecond, valorant.PsoPerSecondDriver, valorant.LastWarmCold));

        var cold = new GameRecord { Plan = plan, PsoPerSecond = 148_040 / 236.0, LastWarmTime = TimeSpan.FromSeconds(236), WarmedDriverId = Plain };
        CompileEstimate.Migrate(cold);
        Assert.True(cold.LastWarmCold);

        var rewarm = new GameRecord { Plan = plan, PsoPerSecond = 148_040 / 236.0, LastWarmTime = TimeSpan.FromSeconds(33), WarmedDriverId = Plain };
        CompileEstimate.Migrate(rewarm);   // the same driver's later rewarm: its rate stays, its time isn't a compile's
        Assert.Equal(((bool?)false, (double?)(148_040 / 236.0)), (rewarm.LastWarmCold, rewarm.PsoPerSecond));

        rewarm.LastWarmTime = TimeSpan.FromSeconds(236);
        CompileEstimate.Migrate(rewarm);   // decided once
        Assert.False(rewarm.LastWarmCold);
    }

    [Fact]
    public void Warm_rates_keep_only_keys_that_name_a_driver()
    {
        var rates = CompileEstimate.MigrateWarmRates(new() { ["Nvidia"] = 97.3, ["Nvidia|" + Here] = 140, ["Nvidia|" + Before] = 4530 });
        Assert.Equal(new Dictionary<string, double> { ["Nvidia|" + Here] = 140 }, rates);
    }

    [Fact]
    public void Reading_planning_and_preparing_count_in_the_time()
    {
        var (time, low) = CompileEstimate.Of(80_000, 80_000, null, 100);
        Assert.Equal(TimeSpan.FromSeconds(80_000 / CompileEstimate.PrepItemsPerSecond + 800), time);
        Assert.Null(low);
    }

    [Fact]
    public void A_stale_game_compiles_its_new_pipelines_and_at_most_every_one_while_its_cache_may_not_hold_the_rest()
    {
        var prep = 100_000 / CompileEstimate.PrepItemsPerSecond;
        // new pipelines only: the rest is left out
        Assert.Equal((TimeSpan.FromSeconds(prep + 20), (TimeSpan?)null), ScsKiller.NextCompile(100_000, true, 2000, true, null, 0, 100));
        // a game update: its new ones at least, every one at most
        Assert.Equal((TimeSpan.FromSeconds(prep + 1000), (TimeSpan?)TimeSpan.FromSeconds(prep)),
            ScsKiller.NextCompile(100_000, true, 0, true, "game updated since the warm (build 1 -> 2)", 0, 100));
        // what the last compile replayed isn't known
        Assert.Equal(TimeSpan.FromSeconds(prep + 1000), ScsKiller.NextCompile(100_000, true, 500, false, null, 0, 100).Time);
        // another driver: everything again, onto a cold cache
        Assert.Equal((TimeSpan.FromSeconds(prep + 1000), (TimeSpan?)null), ScsKiller.NextCompile(100_000, true, 2000, true, "driver changed: 617.70 -> 621.01", 0, 100));
        Assert.True(ScsKiller.CacheMayHold(ScsKiller.TrimmedPartReason));
        Assert.False(ScsKiller.CacheMayHold(ScsKiller.TrimmedAllReason));
    }

    [Fact]
    public void A_stopped_compile_on_this_driver_goes_on_where_it_stopped()
    {
        var prep = 100_000 / CompileEstimate.PrepItemsPerSecond;
        Assert.Equal(TimeSpan.FromSeconds(prep + 400), ScsKiller.NextCompile(100_000, false, 0, true, null, 60_000, 100).Time);
    }
}
