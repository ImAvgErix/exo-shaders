using SCSKiller.Core;
using SCSKiller.Core.Planning;
using Xunit.Abstractions;
using static SCSKiller.Core.Planning.PsoDb;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Planning;

public class UnitCoverTests(ITestOutputHelper output)
{
    static readonly string Rs = Hash("rs");
    static readonly ShaderInfo Vs = Shader("vs", Stage.Vertex, [In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3)]);
    static readonly ShaderInfo Ps = Shader("ps", Stage.Pixel, [], [Target(0)]);
    static readonly Dictionary<int, string> VsPs = new() { [(int)Stage.Vertex] = Vs.Sha1, [(int)Stage.Pixel] = Ps.Sha1 };
    static readonly Dictionary<int, string> VsOnly = new() { [(int)Stage.Vertex] = Vs.Sha1 };

    static List<LayoutElem> Layout(uint posFormat, uint uvOffset) => [new("POSITION", 0, posFormat, 0), new("TEXCOORD", 0, 34, uvOffset)];
    static readonly List<List<LayoutElem>> ThreeLayouts = [Layout(6, 12), Layout(2, 16), Layout(6, 16)];

    static ExactLayouts Facts(UnitPolicy p, params Rec[] recs) =>
        ExactLayouts.Build(recs, new Dictionary<string, byte[]>(), p, new[] { Vs, Ps }.ToDictionary(s => s.Sha1));

    [Fact]
    public void ZipsCandidatesInsteadOfTheProduct()
    {
        var cover = new UnitCover(Facts(UnitPolicy.Amd));
        var picks = cover.Cover(VsPs, Rs, ThreeLayouts, [3], ["fp16", "41"]);
        Assert.Equal(3, picks.Count); // max(3 layouts, 2 shapes), not 6
        Assert.Equal(3, cover.Covered.Count(u => u.Stage == Stage.Vertex));
        Assert.Equal(2, cover.Covered.Count(u => u.Stage == Stage.Pixel));
        Assert.Empty(cover.Cover(VsPs, Rs, ThreeLayouts, [3], ["fp16", "41"])); // all covered now
        var more = cover.Cover(VsPs, Rs, ThreeLayouts, [3], ["fp16", "41", "61", "35"]); // two new shapes: two PSOs on covered layouts
        Assert.Equal(["61", "35"], more.Select(p => p.Shape));
    }

    [Fact]
    public void RecordedUnitsCountAsCovered()
    {
        var recorded = Gfx(Rs, Vs, Ps, [.. Layout(6, 12), new("COLOR", 0, 28, 20)], [10]); // COLOR isn't declared: not in the unit
        var cover = UnitCover.Seeded(Facts(UnitPolicy.Amd, recorded));
        Assert.Equal(2, cover.Covered.Count);
        Assert.Empty(cover.Cover(VsPs, Rs, [ExactLayouts.ReadLayout(Layout(6, 12), Vs)], [3], ["fp16"]));
        var picks = cover.Cover(VsPs, Rs, [ExactLayouts.ReadLayout(Layout(6, 12), Vs), ExactLayouts.ReadLayout(Layout(2, 16), Vs)], [3], ["fp16"]);
        Assert.Equal("fp16", Assert.Single(picks).Shape); // only the new layout, with a covered shape
        Assert.Equal(2u, picks[0].Layout[0].Format);
    }

    [Fact]
    public void PoliciesKeyDifferently()
    {
        // AMD: a depth pass (VS alone) and VS+PS compile the VS separately; the topology and layout are in the VS key
        var amd = new UnitCover(Facts(UnitPolicy.Amd));
        Assert.Single(amd.Cover(VsPs, Rs, [Layout(6, 12)], [3], ["fp16"]));
        Assert.Single(amd.Cover(VsOnly, Rs, [Layout(6, 12)], [3], []));
        Assert.Single(amd.Cover(VsOnly, Rs, [Layout(6, 12)], [2], []));
        Assert.Equal(3, amd.Covered.Count(u => u.Stage == Stage.Vertex));

        // NVIDIA: shader + root signature only
        var nv = new UnitCover(Facts(UnitPolicy.Nvidia));
        Assert.Single(nv.Cover(VsPs, Rs, ThreeLayouts, [3, 2], ["fp16", "41"]));
        Assert.Empty(nv.Cover(VsOnly, Rs, [Layout(6, 12)], [2], []));
        Assert.Single(nv.Cover(VsOnly, Hash("rs2"), [Layout(6, 12)], [2], []));
        Assert.Equal(3, nv.Covered.Count);
    }

    /// <summary>How one stage's units of a holdout recording fare against a basis recording: already among the basis's
    /// units, else resolved from the basis (<see cref="ExactLayouts.Layouts"/> / <see cref="ExactLayouts.Shapes"/> /
    /// <see cref="ExactLayouts.Topologies"/> for this shader, with the holdout PSO's own stages and root signature) to a
    /// candidate list that contains it (Hit) or not (Miss), by provenance. Stages keyed on the root signature alone always hit.</summary>
    sealed record HoldoutRow(Stage Stage, int Units, int InBasis, int[] Hit, int[] Miss)
    {
        public override string ToString() => $"{Stage}: {Units} units, {InBasis} in basis ({100.0 * InBasis / Math.Max(1, Units):F0}%), "
            + $"resolved hit exact/inferred/guessed {string.Join('/', Hit)}, miss {string.Join('/', Miss)}";
    }

    /// <summary>Unit coverage of <paramref name="holdout"/>'s recording by <paramref name="basis"/>'s (same policy for both;
    /// keys are content hashes of the root signatures, so two machines' recordings compare).</summary>
    static List<HoldoutRow> Holdout(ExactLayouts basis, ExactLayouts holdout)
    {
        var inBasis = UnitCover.RecordedUnits(basis).ToHashSet();
        var seen = new HashSet<Unit>();
        var rows = new Dictionary<Stage, (int Units, int In, int[] Hit, int[] Miss)>();
        void Count(Unit u, Func<(bool Hit, Provenance P)> resolve)
        {
            if (!seen.Add(u)) return;
            if (!rows.TryGetValue(u.Stage, out var r)) rows[u.Stage] = r = (0, 0, new int[3], new int[3]);
            r.Units++;
            if (inBasis.Contains(u)) r.In++;
            else
            {
                var (hit, p) = resolve();
                (hit ? r.Hit : r.Miss)[(int)p]++;
            }
            rows[u.Stage] = r;
        }

        foreach (var c in holdout.Compute) Count(new Unit(Stage.Compute, c.Stages[(int)Stage.Compute], holdout.RsKey(c.Rs)), () => (true, Provenance.Exact));
        foreach (var s in holdout.Graphics)
        {
            var shape = ExactLayouts.ExportShape(s);
            var vi = holdout.Shaders.GetValueOrDefault(s.Stages.GetValueOrDefault((int)Stage.Vertex) ?? "");
            var read = vi != null ? ExactLayouts.ReadLayout(ExactLayouts.Explicit(s.Layout), vi) : ExactLayouts.Explicit(s.Layout);
            bool Makes(Unit u, IReadOnlyList<LayoutElem> l, uint t, string sh) => UnitCover.Units(holdout, s.Stages, s.Rs, l, t, sh).Contains(u);
            foreach (var u in UnitCover.Units(holdout, s.Stages, s.Rs, read, s.Topology, shape))
                Count(u, () =>
                {
                    if (u.Stage == Stage.Vertex && vi != null)
                    {
                        var (layouts, topos) = (basis.Layouts(vi), basis.Topologies(s.Stages));
                        // the layout's provenance: an unrecorded VS's topology is a triangle-list default, right for nearly all
                        return (layouts.Value.Any(l => topos.Value.Any(t => Makes(u, l, t, shape))), layouts.Provenance);
                    }
                    if (u.Stage == Stage.Pixel && holdout.Shaders.TryGetValue(u.Shader, out var pi))
                    {
                        var shapes = basis.Shapes(pi);
                        return (shapes.Value.Any(sh => Makes(u, read, s.Topology, sh)), shapes.Provenance);
                    }
                    return (u.Stage is not (Stage.Vertex or Stage.Pixel), Provenance.Exact); // RS-only stages; a VS/PS without its signature can't resolve
                });
        }
        return rows.OrderBy(r => r.Key).Select(r => new HoldoutRow(r.Key, r.Value.Units, r.Value.In, r.Value.Hit, r.Value.Miss)).ToList();
    }

    /// <summary>Cross-machine generalization (what a community database relies on): the AMD session's units against the
    /// NVIDIA recording's and back, per policy.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7HoldoutBothDirections()
    {
        if (!File.Exists(Ff7.RehydratedDb) || !File.Exists(Ff7.AmdSessionDb)) return;
        foreach (var p in new[] { UnitPolicy.Amd, UnitPolicy.Nvidia })
        {
            var (nv, amd) = (ExactLayouts.FromDb(Ff7.RehydratedDb, p), ExactLayouts.FromDb(Ff7.AmdSessionDb, p));
            foreach (var (name, basis, holdout) in new[] { ("basis AMD session, holdout NVIDIA recording", amd, nv), ("basis NVIDIA recording, holdout AMD session", nv, amd) })
            {
                var rows = Holdout(basis, holdout);
                output.WriteLine($"{p.Name} policy, {name}:");
                foreach (var r in rows) output.WriteLine("  " + r);
                Assert.All(rows, r => Assert.Equal(r.Units, r.InBasis + r.Hit.Sum() + r.Miss.Sum()));
                Assert.Equal(UnitCover.RecordedUnits(holdout).Distinct().Count(), rows.Sum(r => r.Units));
            }
        }
    }

    /// <summary>Distinct (root signature, stages) tuples of a recording's graphics PSOs, in recording order.</summary>
    static List<PsoState> Tuples(ExactLayouts x) => x.Graphics.DistinctBy(s => s.Tuple).ToList();

    /// <summary>Greedy cover of the recorded stage sets from an empty cover, each with its shaders' recorded layouts,
    /// topologies and shapes.</summary>
    /// <returns>graphics PSOs: one pass in recording order (<see cref="UnitCover.Cover"/>), and <see cref="UnitCover.CoverAll"/></returns>
    static (int OnePass, int TwoPass) GreedyRecorded(ExactLayouts x, out int optimum)
    {
        // each tuple with the layouts, topologies and shapes recorded for it: any combination of them is recorded units only
        // (a VS's layouts from its other root signatures would be new units)
        var cands = x.Graphics.GroupBy(s => s.Tuple).Select(g =>
        {
            var s = g.First();
            var vs = x.Shaders.GetValueOrDefault(s.Stages.GetValueOrDefault((int)Stage.Vertex) ?? "");
            return new UnitCover.Candidate(s.Stages, s.Rs, vs != null ? g.Select(t => ExactLayouts.ReadLayout(t.Layout, vs)).DistinctBy(ExactLayoutsTests.L).ToList() : [],
                vs != null ? g.Select(t => t.Topology).Distinct().ToList() : [], g.Select(ExactLayouts.ExportShape).Distinct().ToList());
        }).ToList();
        optimum = Optimum(x, cands);
        var one = new UnitCover(x);
        var n = cands.Sum(c => one.Cover(c.Stages, c.Rs, c.Layouts, c.Topologies, c.Shapes).Count);
        var two = new UnitCover(x);
        var m = two.CoverAll(cands).Count;
        Assert.True(one.Covered.SetEquals(UnitCover.RecordedUnits(x).Where(u => u.Stage != Stage.Compute)));
        Assert.True(two.Covered.SetEquals(one.Covered));
        return (n, m);
    }

    /// <summary>The fewest graphics PSOs covering the candidates' units: a PSO covers one VS/MS unit and at most one PS unit
    /// (GS units ride along), so it's a minimum edge cover, |VS/MS units| + |PS units| - a maximum matching (Kuhn).</summary>
    static int Optimum(ExactLayouts x, List<UnitCover.Candidate> cands)
    {
        var edges = new Dictionary<Unit, HashSet<Unit>>();
        var ps = new HashSet<Unit>();
        foreach (var c in cands)
            foreach (var l in c.Layouts.DefaultIfEmpty([]))
                foreach (var t in c.Topologies.DefaultIfEmpty(0u))
                    foreach (var s in c.Shapes.DefaultIfEmpty(""))
                    {
                        var us = UnitCover.Units(x, c.Stages, c.Rs, l, t, s).ToList();
                        var a = us.First(u => u.Stage is Stage.Vertex or Stage.Mesh);
                        if (!edges.TryGetValue(a, out var e)) edges[a] = e = [];
                        foreach (var b in us.Where(u => u.Stage == Stage.Pixel)) { e.Add(b); ps.Add(b); }
                    }
        var match = new Dictionary<Unit, Unit>();
        bool Augment(Unit a, HashSet<Unit> seen)
        {
            foreach (var b in edges[a])
                if (seen.Add(b) && (!match.TryGetValue(b, out var other) || Augment(other, seen))) { match[b] = a; return true; }
            return false;
        }
        var m = edges.Keys.Count(a => Augment(a, []));
        return edges.Count + ps.Count - m;
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7UnitsAndGreedyCoverPerPolicy()
    {
        if (!File.Exists(Ff7.RehydratedDb)) return;
        // the design's first keys (whole RS for the VS, depth pass = VS+PS): the Python pass's one-pass greedy got 381 (its
        // order and ties differ) over a lower bound of 313
        var design = UnitPolicy.Amd with { Name = "amd-design", NextStage = false };
        var rows = new List<string>();
        foreach (var p in new[] { design, design with { Name = "amd-design+next", NextStage = true }, UnitPolicy.Amd, UnitPolicy.Nvidia })
        {
            var x = ExactLayouts.FromDb(Ff7.RehydratedDb, p);
            var units = UnitCover.RecordedUnits(x).ToHashSet();
            int U(Stage s) => units.Count(u => u.Stage == s);
            var (one, two) = GreedyRecorded(x, out var optimum);
            var row = $"{p.Name}: VS {U(Stage.Vertex)} MS {U(Stage.Mesh)} GS {U(Stage.Geometry)} PS {U(Stage.Pixel)} CS {U(Stage.Compute)}; graphics PSOs: one pass {one}, two passes {two}, "
                + $"optimum {optimum}, max(VS+MS, PS) {Math.Max(U(Stage.Vertex) + U(Stage.Mesh), U(Stage.Pixel))}";
            output.WriteLine(row + $" ({Tuples(x).Count} recorded graphics tuples, {x.Graphics.Count} PSOs)");
            rows.Add(row);
        }
        // FF7's depth-pass VSs are never also drawn with a PS; +next also keys the VS on whether its PS has a render target
        // (4 VSs drawn both ways) and on how the PS reads it (+2-3 VS units); AMD keys the VS on the whole RS (a VS-visible key
        // merged 16 VS units, but FF7's session-2 A/B didn't bear it out); PS units bound the cover.
        Assert.Equal([
            "amd-design: VS 246 MS 27 GS 7 PS 313 CS 427; graphics PSOs: one pass 384, two passes 372, optimum 366, max(VS+MS, PS) 313",
            "amd-design+next: VS 249 MS 27 GS 7 PS 313 CS 427; graphics PSOs: one pass 385, two passes 373, optimum 368, max(VS+MS, PS) 313",
            "amd: VS 249 MS 27 GS 7 PS 313 CS 427; graphics PSOs: one pass 385, two passes 373, optimum 368, max(VS+MS, PS) 313",
            "nvidia: VS 233 MS 27 GS 7 PS 309 CS 427; graphics PSOs: one pass 371, two passes 360, optimum 356, max(VS+MS, PS) 309"], rows);
    }
}
