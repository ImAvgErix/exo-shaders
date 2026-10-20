using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Warming;

namespace SCSKiller.Tests.Platform;

public class WarmPassesTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-passes-test-" + Guid.NewGuid().ToString("N")[..8]);

    public WarmPassesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    static string H(int i) => $"{i:x40}";

    [Fact]
    public void No_pass_has_two_recorded_items_of_one_shader_set_and_every_item_is_in_exactly_one_pass_in_order()
    {
        var rng = new Random(7);
        var sets = Enumerable.Range(0, 5000).Select(_ => rng.Next(10) == 0 ? null : $"set{rng.Next(300)}").ToList();
        const int recorded = 4000;
        var pass = WarmPasses.Split(sets, recorded);
        Assert.Equal(sets.Count, pass.Length);
        var careful = pass.Take(recorded).Distinct().Order().ToList();
        Assert.Equal(Enumerable.Range(1, careful.Count).Select(i => (byte)i), careful);   // 1, 2, ... without gaps
        foreach (var k in careful)
        {
            var items = Enumerable.Range(0, recorded).Where(i => pass[i] == k && sets[i] != null).Select(i => sets[i]).ToList();
            Assert.Equal(items.Count, items.Distinct().Count());
        }
        Assert.All(pass.Skip(recorded), p => Assert.Equal(WarmPasses.FastPass, p));    // the plan's items: one fast pass
        Assert.Equal(pass, WarmPasses.Split(sets, recorded));                            // the same items, the same passes
        Assert.All(Enumerable.Range(0, recorded).Where(i => sets[i] == null), i => Assert.Equal(1, pass[i]));

        // the k-th recorded item of a set goes to pass k; the fast pass after the careful ones
        Assert.Equal([1, 1, 2, 1, 3, 255, 255], WarmPasses.Split(["a", "b", "a", null, "a", "b", "c"], 5));
        Assert.Equal([255, 255], WarmPasses.Split(["a", "a"], 0));
    }

    [Fact]
    public void A_set_with_more_siblings_than_the_pass_bound_shares_passes_round_robin()
    {
        var pass = WarmPasses.Split(Enumerable.Repeat<string?>("x", 100).Append("other").ToList(), 101);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (byte)(1 + i % WarmPasses.MaxPasses)).Append((byte)1), pass);
    }

    [Fact]
    public void Passes_run_in_their_numbers_order_and_progress_and_the_resume_point_map_both_ways()
    {
        File.WriteAllBytes(Path.Combine(_dir, WarmPasses.FileName), WarmPasses.Split(["a", "b", "a", null, "a", "b", "c"], 5));   // 1 1 2 1 3 F F
        var p = WarmPasses.Read(_dir)!;
        Assert.Equal((4, 3, 7L), (p.Count, p.CarefulCount, p.Total));
        Assert.Equal([1, 2, 3, 255], Enumerable.Range(0, p.Count).Select(p.Number));
        Assert.Equal([false, false, false, true], Enumerable.Range(0, p.Count).Select(p.IsFast));
        Assert.Equal([0L, 1, 3], p.ItemsOf(0));
        Assert.Equal([5L, 6], p.ItemsOf(3));
        Assert.Equal(2, p.Overall(0, 3));    // items 0 and 1 of pass 1 are below done 3
        Assert.Equal(3, p.Overall(0, 7));    // pass 1 complete
        Assert.Equal(4, p.Overall(1, 3));    // + item 2
        Assert.Equal(7, p.Overall(3, 7));
        Assert.Equal((0, 3L), p.Locate(2));
        Assert.Equal((1, 2L), p.Locate(3));
        Assert.Equal((3, 6L), p.Locate(6));
        Assert.Equal((3, 7L), p.Locate(7));  // all done
        foreach (var o in Enumerable.Range(0, 7)) { var (k, s) = p.Locate(o); Assert.Equal(o, p.Overall(k, s)); }

        Assert.Null(WarmPasses.Read(Path.Combine(_dir, "none")));
    }
    [Fact]
    public void Item_sets_follow_the_proxys_item_order_the_recording_first_and_a_plan_item_takes_its_templates_stages_under_its_own()
    {
        Dictionary<int, string> St(params (Stage S, int H)[] s) => s.ToDictionary(x => (int)x.S, x => H(x.H));
        var t1 = PsoDb.Stream(H(99), St((Stage.Vertex, 1), (Stage.Pixel, 2)), [], 3, [28], 0);
        var t1b = PsoDb.Stream(H(98), St((Stage.Vertex, 1), (Stage.Pixel, 2)), [], 3, [28], 0);   // same shaders, another root signature
        var cs = PsoDb.Compute(H(99), H(3));
        using (var main = File.Create(Path.Combine(_dir, "scskiller.db")))   // the recording
        {
            PsoDb.WriteBlob(main, H(1), "vs"u8);
            PsoDb.Write(main, 'S', t1);
            PsoDb.Write(main, 'C', cs);
            PsoDb.Write(main, 'N', new byte[36]);
            PsoDb.Write(main, 'W', new byte[40]);   // a layer's pairing: no item
            PsoDb.Write(main, 'S', t1b);
        }
        var tmplKey = new PsoDb.Rec('S', t1).Key;
        using (var gen = File.Create(Path.Combine(_dir, "scskiller_gen.db")))
        {
            PsoDb.WriteBlob(gen, H(2), "ps"u8);
            PsoDb.Write(gen, 'S', t1);    // a record the main db has: one item
            PsoDb.Write(gen, 'S', PsoDb.Stream(H(97), St((Stage.Vertex, 1), (Stage.Pixel, 2)), [], 3, [28], 0));   // a template of the plan
            PsoDb.Write(gen, 'P', PsoDb.Item(tmplKey, H(99), St((Stage.Pixel, 5)), null));                     // VS 1 from the template
            PsoDb.Write(gen, 'P', PsoDb.Item(tmplKey, H(99), St((Stage.Vertex, 1), (Stage.Pixel, 2)), null));
            PsoDb.Write(gen, 'P', PsoDb.Item(H(77), H(99), St((Stage.Vertex, 6)), null));                     // no such template
            PsoDb.Write(gen, '1', PsoDb.D3D11Item(Stage.Vertex, H(1)));
        }
        string Set(params (Stage S, int H)[] s) => string.Join(',', s.OrderBy(x => (int)x.S).Select(x => $"{(int)x.S}:{H(x.H)}"));
        var (sets, recorded) = WarmPasses.ItemSets(_dir);
        Assert.Equal(3, recorded);
        Assert.Equal<string?>([
            Set((Stage.Vertex, 1), (Stage.Pixel, 2)), Set((Stage.Compute, 3)), Set((Stage.Vertex, 1), (Stage.Pixel, 2)), Set((Stage.Vertex, 1), (Stage.Pixel, 2)),
            Set((Stage.Vertex, 1), (Stage.Pixel, 5)), Set((Stage.Vertex, 1), (Stage.Pixel, 2)), Set((Stage.Vertex, 6)), null], sets);

        Assert.Equal(2, WarmPasses.Write(_dir));   // the recording's two siblings split; the plan's items (siblings too) in the fast pass
        Assert.Equal([1, 1, 2, 255, 255, 255, 255, 255], File.ReadAllBytes(Path.Combine(_dir, WarmPasses.FileName)));

        File.Delete(Path.Combine(_dir, "scskiller.db"));   // no recording: nothing careful, no pass file
        Assert.Equal(0, WarmPasses.Write(_dir));
        Assert.False(File.Exists(Path.Combine(_dir, WarmPasses.FileName)));
    }
}
