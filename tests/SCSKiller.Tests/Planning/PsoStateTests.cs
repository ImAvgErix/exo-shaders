using System.Buffers.Binary;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Tests.Planning;

public class PsoStateTests
{
    static readonly string Rs = new('a', 40), Vs = new('b', 40), Ps = new('c', 40);
    static readonly Dictionary<int, string> VsPs = new() { [(int)Stage.Vertex] = Vs, [(int)Stage.Pixel] = Ps };

    [Fact]
    public void ReadsBackASynthesizedStream()
    {
        List<LayoutElem> layout = [new("POSITION", 0, 6, 0), new("ATTRIBUTE", 3, 28, 16, 2, 1, 1)];
        var s = ParseState(new Rec('S', Stream(Rs, VsPs, layout, 3, [10, 24], D32Float)))!;
        Assert.Equal(Rs, s.Rs);
        Assert.Equal(VsPs, s.Stages);
        Assert.Equal(layout, s.Layout);
        Assert.Equal(3u, s.Topology);
        Assert.Equal([10u, 24u], s.RtFormats);
        Assert.Equal([0xF, 0xF], s.RtWriteMasks);
        Assert.Equal([-1, -1], s.LogicOps);
        Assert.Equal(D32Float, s.Dsv);
        Assert.Equal(1u, s.SampleCount);
        Assert.Null(ParseState(new Rec('C', new byte[40])));
    }

    [Fact]
    public void DefaultLayoutFieldsWriteWhatTheyAlwaysDid()
    {
        var b = LayoutBytes([new("TEXCOORD", 1, 16, 12)]);
        // count, name length, name, index, format, slot 0, offset, per-vertex, step 0
        Assert.Equal(4 + 4 + 8 + 24, b.Length);
        uint[] tail = [1, 16, 0, 12, 0, 0];
        for (var i = 0; i < 6; i++) Assert.Equal(tail[i], BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16 + 4 * i)));
    }

    /// <summary>Every recorded FF7 Rebirth PSO parses, and its layout re-serializes to the recorded bytes.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7StatesParseAndLayoutsRoundTrip()
    {
        if (!File.Exists(Ff7.RehydratedDb)) return;
        var recs = Read(Ff7.RehydratedDb).Where(r => r.Tag != 'B').ToList();
        Assert.Equal(994, recs.Count);
        var n = 0;
        foreach (var r in recs.Where(r => r.Tag is 'G' or 'S'))
        {
            var s = ParseState(r)!;
            Assert.Equal(Parse(r).Tuple, s.Tuple);
            Assert.Equal(Parse(r).Topology, s.Topology);
            Assert.Equal(s.RtFormats.Length, s.RtWriteMasks.Length);
            var bytes = LayoutBytes(s.Layout);
            Assert.Equal(Parse(r).HasLayout, s.Layout.Count > 0);
            if (s.Layout.Count > 0) Assert.True(r.Payload.AsSpan().IndexOf(bytes) >= 0); // names make a non-empty layout's bytes unique
            if (!s.Stages.ContainsKey((int)Stage.Compute)) n++;
            else Assert.True(s.Layout.Count == 0 && s.RtFormats.Length == 0);
        }
        Assert.Equal(567, n); // graphics; the other 427 are compute streams
    }

    /// <summary>The AMD session has 'G' records too: they parse and round-trip the same way.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7AmdSessionStatesParse()
    {
        if (!File.Exists(Ff7.AmdSessionDb)) return;
        var recs = Read(Ff7.AmdSessionDb).Where(r => r.Tag is 'G' or 'S').ToList();
        Assert.Contains(recs, r => r.Tag == 'G');
        foreach (var r in recs)
        {
            var s = ParseState(r)!;
            Assert.Equal(Parse(r).Tuple, s.Tuple);
            Assert.Equal(Parse(r).Topology, s.Topology);
            if (s.Layout.Count > 0) Assert.True(r.Payload.AsSpan().IndexOf(LayoutBytes(s.Layout)) >= 0);
        }
    }
}

public class StreamWriteMaskTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static readonly string Rs = new('a', 40), Vs = new('b', 40), Ps = new('c', 40), Gs = new('d', 40), Cs = new('e', 40);

    static IEnumerable<byte[]> Neutral()
    {
        yield return Stream(Rs, new Dictionary<int, string> { [(int)Stage.Vertex] = Vs, [(int)Stage.Pixel] = Ps }, [new("POSITION", 0, 6, 0)], 3, [R16G16B16A16Float], 0);
        yield return Stream(Rs, new Dictionary<int, string> { [(int)Stage.Vertex] = Vs, [(int)Stage.Geometry] = Gs, [(int)Stage.Pixel] = Ps }, null, 2, [10, 3, 4], D32Float);
        yield return Stream(Rs, new Dictionary<int, string> { [(int)Stage.Vertex] = Vs }, [], 3, [], D32Float);
        yield return Stream(Rs, new Dictionary<int, string> { [(int)Stage.Compute] = Cs }, null, 0, [], 0);
    }

    /// <summary>Keys of the streams above before write masks became a parameter: synthesized NVIDIA templates stay byte-identical.</summary>
    [Fact]
    public void NeutralStreamsAreUnchanged()
    {
        var keys = Neutral().Select(b => new Rec('S', b).Key).ToList();
        output.WriteLine(string.Join("\n", keys));
        Assert.Equal(["4bc174c6e0a2798dfff1923e32a30503b012ed4e", "3d87ff7126687c3f992e3c2d309c590c8cd24aa4",
            "a362e815ed005770d4975441efa29bee850535c8", "e740b2f421fccb63e33ac8abc9aea4338a1e449a"], keys);
        var gs = Stream(Rs, new Dictionary<int, string> { [(int)Stage.Vertex] = Vs, [(int)Stage.Geometry] = Gs, [(int)Stage.Pixel] = Ps }, null, 2, [10, 3, 4], D32Float, [0xF, 0xF, 0xF]);
        Assert.Equal(keys[1], new Rec('S', gs).Key); // all 0xF = the default
    }

    [Fact]
    public void WriteMasksRoundTripPerTarget()
    {
        var st = new Dictionary<int, string> { [(int)Stage.Vertex] = Vs, [(int)Stage.Pixel] = Ps };
        var same = ParseState(new Rec('S', Stream(Rs, st, [], 3, [10, 28], 0, [0, 0])))!;
        Assert.Equal([0, 0], same.RtWriteMasks);
        var mixed = ParseState(new Rec('S', Stream(Rs, st, [], 3, [10, 28, 24], 0, [0xF, 0, 0x7])))!; // needs IndependentBlendEnable
        Assert.Equal([0xF, 0, 0x7], mixed.RtWriteMasks);
        Assert.Equal([-1, -1, -1], mixed.LogicOps);
    }
}
