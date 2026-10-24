// Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 Exo Shaders contributors. GPL-3.0-or-later (see LICENSE, LICENSE-EXCEPTION.txt).

namespace SCSKiller.Core.App;

/// <summary>How long a game's next compile takes on this PC, from what compiles measured here: reading its shaders,
/// planning and preparing the files (<see cref="PrepItemsPerSecond"/>), then the pipelines it compiles at a cold rate
/// (<see cref="RateFor"/>). The estimate is learned when that rate is the game's own on this driver; anything else
/// stands in for it and is rough.</summary>
public static class CompileEstimate
{
    /// <summary>Plan items a compile reads, plans and prepares a second before its first pipeline (index, plan, materialize),
    /// measured on an RTX 3070 PC: Marvel Rivals' 1.7 million pipelines in 4 min, Counter-Strike 2's 147,000 in 34 s, Lords of
    /// the Fallen's 125,000 in 12 s.</summary>
    public const double PrepItemsPerSecond = 8000;

    /// <summary>The fastest a compile onto a cold cache goes: four times NVIDIA's default (its 30-thread compiles measured
    /// 450-1350 a second). One that went faster rewarmed a cache that still held the game: NVIDIA kept DirectX 11 caches from
    /// driver 617 to 621, and VALORANT's 148,000 shaders then took 33 s where a cold compile takes 4 min.</summary>
    public const double MaxColdRate = 4 * ScsKiller.DefaultPsoPerSecond;

    /// <summary>Fewer items than this measure no rate: start-up costs outweigh them.</summary>
    public const long MinItems = 1000;

    /// <summary>The driver of a rate an earlier build measured without keeping it, its warm forgotten since (a cleared
    /// cache): a rate of some earlier driver, never this one's.</summary>
    public const string UnknownDriver = "?";

    /// <summary>A rate a compile reached that only a cache still holding the game gives: past <see cref="MaxColdRate"/>, or
    /// four times the game's cold rate measured the same way (same driver key).</summary>
    public static bool Rewarm(double rate, double? sameKeyRate) => rate > Math.Min(MaxColdRate, 4 * (sameKeyRate ?? double.PositiveInfinity));

    /// <summary>Pipelines a second, and whether they are the game's own on this driver.</summary>
    public readonly record struct Rate(double PerSecond, bool Learned);

    /// <summary>The cold rate of the game's next compile, first that exists: its own measured on this driver key
    /// (<paramref name="key"/>, <see cref="ScsKiller"/>'s RateDriver) by a complete compile or the segments of a stopped one
    /// (learned); else, rough, a stopped compile's segments on another driver (the latest), its own on another driver, the
    /// games of its engine and API on this driver (<paramref name="engineHere"/>), every game on this driver
    /// (<paramref name="pcHere"/>), this driver's last cold compile (<paramref name="seeded"/>), the vendor's default.</summary>
    public static Rate RateFor(GameRecord rec, string? key, double? engineHere, double? pcHere, double? seeded, double fallback)
    {
        double? stopped = rec is { ResumeItems: >= MinItems, ResumeSeconds: > 1 } && rec.ResumeItems / rec.ResumeSeconds is var r && r <= MaxColdRate ? r : null;
        if (key != null && rec.PsoPerSecond is > 0 and var own && rec.PsoPerSecondDriver == key) return new(own, true);
        if (key != null && stopped is { } here && rec.ResumeGpu == key) return new(here, true);
        return new(stopped ?? (rec.PsoPerSecond is > 0 and var other ? other : engineHere ?? pcHere ?? seeded ?? fallback), false);
    }

    /// <summary>Items over the time each takes at its rate: one rate for them all. Null: none.</summary>
    public static double? Pool(IEnumerable<(double Items, double Rate)> measured)
    {
        var m = measured.Where(x => x is { Items: > 0, Rate: > 0 }).ToList();
        return m.Count > 0 ? m.Sum(x => x.Items) / m.Sum(x => x.Items / x.Rate) : null;
    }

    /// <summary>The compile's time: <paramref name="items"/> read, planned and prepared, then <paramref name="compiles"/> at
    /// <paramref name="rate"/>. <paramref name="atMost"/>: what it compiles when its cache no longer holds the rest (a stale
    /// game's every item), the time then, with <paramref name="compiles"/> alone as its low end.</summary>
    public static (TimeSpan Time, TimeSpan? Low) Of(long items, long compiles, long? atMost, double rate)
    {
        var prep = items / PrepItemsPerSecond;
        TimeSpan At(long n) => TimeSpan.FromSeconds(prep + Math.Max(0, n) / rate);
        return atMost is { } most && most > compiles ? (At(most), At(compiles)) : (At(compiles), null);
    }

    /// <summary>An earlier build's record in this build's terms: a rate it measured keeps the driver it was measured on
    /// (its warm's then, or <see cref="UnknownDriver"/> once that warm was forgotten), a rate past <see cref="MaxColdRate"/>
    /// goes (a rewarm taken for a cold compile), and its last compile counts as cold when that compile measured the rate.</summary>
    public static void Migrate(GameRecord r)
    {
        r.LastWarmCold ??= r is { LastWarmTime: { TotalSeconds: > 0 } t, PsoPerSecond: > 0 and <= MaxColdRate and var rate }
            && ScsKiller.PlanItems(r.Plan?.Stats) is > 0 and var n && Math.Abs(n / t.TotalSeconds - rate) <= 0.01 * rate;
        if (r.PsoPerSecond > MaxColdRate) (r.PsoPerSecond, r.PsoPerSecondDriver) = (null, null);
        if (r.PsoPerSecond != null) r.PsoPerSecondDriver ??= r.WarmedDriverId ?? UnknownDriver;
    }

    /// <summary>warmrates.json in this build's terms: an earlier build's vendor-only key names no driver and goes, as does a
    /// rate past <see cref="MaxColdRate"/>.</summary>
    public static Dictionary<string, double> MigrateWarmRates(Dictionary<string, double> rates) =>
        rates.Where(r => r.Key.Contains('|') && r.Value is > 0 and <= MaxColdRate).ToDictionary(r => r.Key, r => r.Value);
}
