using System.Diagnostics;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Glacier;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Glacier;

/// <summary>007 First Light on this machine (read-only; skipped when absent): found by the Glacier 2 reader before the
/// carver, its material effects' shaders indexed with the root signatures they carry, planned without a recording. Measured
/// (October 2026 build): 1,085 effects, 16,082 shaders (8,927 PS, 4,905 VS, 1,165 CS, 1,070 ray tracing libraries), 2 root
/// signatures; 8,418 of the 8,426 shaders in Retail\PipelineCache.bin among them; 128,761 stage sets, 25,507 pipelines planned
/// (alpha.3 plans each shader of a pool that pairs every VS with every PS once: 103,254 sets left out).</summary>
[Trait("Needs", "Game")]
public class GlacierGameTests(ITestOutputHelper output)
{
    [Fact]
    public void Indexes007FirstLight()
    {
        var dir = TestEnv.GameDir("007 First Light");
        var game = new Game("steam:3768760", "007 First Light", Store.Steam, dir, Path.Combine(dir, @"Retail\007FirstLight.exe"));
        if (!File.Exists(Path.Combine(dir, @"Runtime\chunk0.rpkg"))) return;
        Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
        var sw = Stopwatch.StartNew();
        var engine = SCSKiller.Core.App.ScsKiller.DefaultReaders(Ff7.TempDir("glacier-readers")).Detect(game)!;   // every reader, in scan order
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}");
        Assert.Equal(new EngineInfo(GlacierReader.Family, "DXIL+RTS0", null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, engine, null, Ff7.Nvidia));

        var reader = new GlacierReader();
        var log = new List<string>();
        var index = reader.Index(game, engine, new Log(l => { log.Add(l); output.WriteLine(l); }), CancellationToken.None);
        Assert.DoesNotContain(log, l => l.Contains("warning"));
        Assert.True(index.Shaders.Count > 15_000);
        Assert.All(index.Shaders.Values.Where(s => s.Stage != Stage.Library), s => Assert.NotNull(s.RootSignature));

        // the shaders Retail\PipelineCache.bin holds (DXIL containers back to back), against the index
        var cache = Path.Combine(dir, @"Retail\PipelineCache.bin");
        if (File.Exists(cache))
        {
            var inCache = Dxbc.Containers(File.ReadAllBytes(cache)).Select(c => (Sha: Convert.ToHexStringLower(SHA1.HashData(c.Container)), Kind: Dxbc.Kind(c.Container))).ToList();
            var found = inCache.Count(c => index.Shaders.ContainsKey(c.Sha));
            output.WriteLine($"PipelineCache.bin: {inCache.Count} containers, {found} in the index; not in it by kind: "
                + string.Join(", ", inCache.Where(c => !index.Shaders.ContainsKey(c.Sha)).GroupBy(c => c.Kind).Select(g => $"{g.Count()} kind {g.Key}")));
            Assert.True(found >= 0.99 * inCache.Count);
        }

        sw.Restart();
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("glacier-plan"), new Log(output.WriteLine), CancellationToken.None);
        output.WriteLine($"plan {sw.Elapsed.TotalSeconds:F1}s: {plan.Stats}");
        Assert.True(plan.Stats.StageSets > 100_000);
        Assert.True(plan.Stats.Generated > 20_000);
        Assert.Equal(0, plan.Stats.Uncovered);
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
