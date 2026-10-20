using System.Diagnostics;
using System.Runtime;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Northlight;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Northlight;

/// <summary>Control on this machine (Xbox app; read-only; skipped when absent): found before the carver, every effect record
/// a pipeline, planned without a recording on NVIDIA with the engine's root signatures covering every shader; what the carver
/// alone finds, for comparison.</summary>
[Trait("Needs", "Game")]
[Collection(TimingCollection.Name)]   // the managed heap is the process's: measured with nothing else running
public class NorthlightGameTests(ITestOutputHelper output)
{
    static long Live()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(true);
    }

    [Fact]
    public void PlansControlWithoutARecording()
    {
        var dir = TestEnv.GameDir(@"Control PCGP\Content");
        var game = new Game("xbox:Control", "Control", Store.Xbox, dir, Path.Combine(dir, "Game_rmdutggamepass_f.exe"));
        if (!Directory.Exists(Path.Combine(dir, @"data\shaders\build\pc_dxil"))) return;
        var reader = new NorthlightReader();
        var sw = Stopwatch.StartNew();
        var engine = new EngineReaders((NorthlightReader.Family, reader), (CarvedReader.Family, new CarvedReader())).Detect(game)!;
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}; anti-cheat {GameFiles.DetectAntiCheat(game)}");
        Assert.Equal(NorthlightReader.Family, engine.Family);
        Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
        Assert.Equal("D3D11 or D3D12", engine.GraphicsApi);   // pc_dx11 ships beside pc_dxil
        Assert.Equal(new PlanCheck(Readiness.Ready, $"{Planner.Untested}; also compiles every DirectX 11 shader (the game may run on either)"), new Planner().Check(game, engine, null, Ff7.Nvidia));
        var before = Live();
        sw.Restart();
        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"index {sw.Elapsed.TotalSeconds:F1}s, managed heap growth {(GC.GetGCMemoryInfo().HeapSizeBytes - before) >> 20} MB");
        Assert.True(index.Maps.Count(m => m.IsPipeline) > 2000);
        Assert.Contains(index.Maps, m => m.Library == NorthlightReader.Libraries);
        foreach (var maximum in new[] { false, true })
        {
            before = Live();
            sw.Restart();
            var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, Ff7.TempDir($"northlight-control-{maximum}"),
                new Progress<string>(output.WriteLine), CancellationToken.None, maximum);
            var heap = (GC.GetGCMemoryInfo().HeapSizeBytes - before) >> 20;
            output.WriteLine($"plan (maximum {maximum}) {sw.Elapsed.TotalSeconds:F1}s, managed heap growth {heap} MB, {new FileInfo(plan.FilePath).Length / 1024} KiB: {plan.Stats}");
            Assert.Equal(0, plan.Stats.Uncovered);
            Assert.True(plan.Stats.D3D11Shaders > 1000);
            Assert.True(plan.Stats.LeftOut <= 1);   // one record whose VS writes no SV_Position
            Assert.True(heap < 512);
        }

        sw.Restart();
        var carver = new CarvedReader();
        var carved = carver.Detect(game, out var notes)!;
        var cindex = carver.Index(game, carved, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"the carver alone, {sw.Elapsed.TotalSeconds:F1}s: {carved}; {notes}; check: {new Planner().Check(game, carved, null, Ff7.Nvidia)}");
    }

    /// <summary>The whole per-stage plan, materialized from the install and replayed by scskiller_warm on WARP (the runtime's
    /// validation on the CPU, no GPU driver cache). Manual: SCSKILLER_WARP_TESTS=1 and this checkout's proxy built. The work
    /// folder holds game shader bytes and is deleted.</summary>
    [Fact]
    public void PlanReplaysOnWarp()
    {
        var dir0 = TestEnv.GameDir(@"Control PCGP\Content");
        var game = new Game("xbox:Control", "Control", Store.Xbox, dir0, Path.Combine(dir0, "Game_rmdutggamepass_f.exe"));
        var warm = Path.Combine(Ff7.ProxyBin, "scskiller_warm.exe");
        if (Environment.GetEnvironmentVariable("SCSKILLER_WARP_TESTS") != "1" || !Directory.Exists(dir0) || !File.Exists(warm)) return;
        var reader = new NorthlightReader();
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var dir = Ff7.TempDir("northlight-warp");
        try
        {
            var planner = new Planner();
            var plan = planner.Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, dir, new Progress<string>(output.WriteLine), CancellationToken.None);
            var work = Path.Combine(dir, "work");
            planner.Materialize(plan, game, engine, reader, null, work, CancellationToken.None);
            Ff7.CheckWarmReady(work);
            var psi = new ProcessStartInfo(warm, [work, $"scsk_northlight_warp_{Random.Shared.Next(100000, 999999)}.exe", "--threads", "8", "--adapter-luid", FromSoft.FromSoftGameTests.WarpLuid().ToString("x")])
                { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, UseShellExecute = false };
            string o;
            using (var p = Process.Start(psi)!) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
            var lines = o.Split('\n').Select(l => l.Trim()).ToList();
            var done = lines.Where(l => l.StartsWith('{')).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .LastOrDefault(e => e.GetProperty("event").GetString() == "done");
            Assert.True(done.ValueKind == System.Text.Json.JsonValueKind.Object, o);
            var log = Path.Combine(TestEnv.WarmStage(o, work), "scskiller.log");
            output.WriteLine($"{lines.FirstOrDefault(l => l.Contains("\"start\""))} {done}\n"
                + (File.Exists(log) ? string.Join('\n', File.ReadLines(log).Where(l => l.Contains("fail", StringComparison.OrdinalIgnoreCase) || l.Contains("d3d12 debug")).Take(60)) : ""));
            Assert.Equal(0, done.GetProperty("failed").GetInt64());
        }
        finally { Directory.Delete(dir, true); }
    }
}
