using System.Diagnostics;
using System.Runtime;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.RedEngine;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.RedEngine;

/// <summary>The Witcher 3's DX12 build on this machine (read-only; skipped when absent): found before the carver, every
/// technique a pipeline, planned without a recording on NVIDIA with every stage set covered by the engine's root signatures.</summary>
[Trait("Needs", "Game")]
[Collection(TimingCollection.Name)]   // the managed heap is the process's: measured with nothing else running
public class RedEngineGameTests(ITestOutputHelper output)
{
    static long Live()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(true);
    }

    [Fact]
    public void PlansTheWitcher3WithoutARecording()
    {
        var dir = TestEnv.GameDir("The Witcher 3");
        var game = new Game("steam:292030", "The Witcher 3", Store.Steam, dir, Path.Combine(dir, @"bin\x64_dx12\witcher3.exe"));
        if (!File.Exists(Path.Combine(dir, @"content\content0\shaderdx12_0.cache"))) return;
        var reader = new RedEngineReader();
        var sw = Stopwatch.StartNew();
        var engine = new EngineReaders((RedEngineReader.Family, reader), (CarvedReader.Family, new CarvedReader())).Detect(game)!;
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Equal(RedEngineReader.Family, engine.Family);
        Assert.Equal(Readiness.Ready, new Planner().Check(game, engine, null, Ff7.Nvidia).Readiness);
        sw.Restart();
        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"index {sw.Elapsed.TotalSeconds:F1}s");
        Assert.True(index.Maps.Count(m => m.IsPipeline) > 60_000);
        foreach (var maximum in new[] { false, true })
        {
            var before = Live();
            sw.Restart();
            var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, Ff7.TempDir($"redengine-w3-{maximum}"), null, CancellationToken.None, maximum);
            var heap = (GC.GetGCMemoryInfo().HeapSizeBytes - before) >> 20;   // the heap at the plan's last GC, garbage included
            output.WriteLine($"plan (maximum {maximum}) {sw.Elapsed.TotalSeconds:F1}s, managed heap growth {heap} MB: {plan.Stats}");
            Assert.True(plan.Stats.RootSigRuleVerified);
            Assert.Equal(3, plan.Stats.RootSignatures);
            Assert.Equal(0, plan.Stats.Uncovered);
            Assert.True(plan.Stats.LeftOut < plan.Stats.StageSets / 1000); // engine-pool pairings of stage sets no root signature is confirmed for
            Assert.True(heap < 512);   // measured 91 MB, 135 MB with maximum
        }
    }
}
