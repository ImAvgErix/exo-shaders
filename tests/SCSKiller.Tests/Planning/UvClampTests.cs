using SCSKiller.Core;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Planning;

/// <summary>Unreal's local vertex factory points the UV channels a mesh lacks at its last one, so one VS is drawn with its
/// texture-coordinate elements packed (0/4/8) or aliased (0/4/4, 0/0/0): FF7 session 2's last VS units the plan missed.</summary>
public class UvClampTests
{
    const uint Rg16F = 34, Rgba8 = 28, Rgb32F = 6;

    static List<LayoutElem> L(params (int Index, uint Format, uint Slot, uint Offset)[] e) =>
        e.Select(x => new LayoutElem("ATTRIBUTE", x.Index, x.Format, x.Offset, x.Slot)).ToList();

    [Fact]
    public void VariantsForOneToAllChannelsOfEachRun()
    {
        // FF7's: ATTRIBUTE5/6/7 (R16G16_FLOAT, slot 2) recorded at 0/4/4, drawn at 0/4/8
        var recorded = L((0, Rgb32F, 0, 0), (4, Rgba8, 3, 4), (5, Rg16F, 2, 0), (6, Rg16F, 2, 4), (7, Rg16F, 2, 4));
        var v = ExactLayouts.UvClampVariants(recorded).Select(Offsets).ToList();
        Assert.Equal(["0,4,0,0,0", "0,4,0,4,4", "0,4,0,4,8"], v);
        // two elements at the same offset (a one-channel mesh) get the packed one; a packed one the aliased one
        Assert.Contains("0,4", ExactLayouts.UvClampVariants(L((4, Rg16F, 3, 0), (5, Rg16F, 3, 0))).Select(Offsets));
        Assert.Contains("0,0", ExactLayouts.UvClampVariants(L((5, Rg16F, 2, 0), (6, Rg16F, 2, 4))).Select(Offsets));
        // no run: other formats, slots, gaps in the semantic index or the offsets
        Assert.Empty(ExactLayouts.UvClampVariants(L((3, 30, 3, 0), (4, Rgba8, 3, 4))));
        Assert.Empty(ExactLayouts.UvClampVariants(L((5, Rg16F, 2, 0), (6, Rg16F, 1, 4))));
        Assert.Empty(ExactLayouts.UvClampVariants(L((5, Rg16F, 2, 0), (7, Rg16F, 2, 4))));
        Assert.Empty(ExactLayouts.UvClampVariants(L((5, Rg16F, 2, 0), (6, Rg16F, 2, 12))));

        static string Offsets(List<LayoutElem> l) => string.Join(',', l.Select(e => e.Offset));
    }

    static readonly ShaderInfo Vs = Shader("uv-vs", Stage.Vertex, [In("ATTRIBUTE", 5, 0, 3), In("ATTRIBUTE", 6, 1, 3)]);
    static readonly ShaderInfo Vs2 = Shader("uv-vs2", Stage.Vertex, [In("ATTRIBUTE", 5, 0, 3), In("ATTRIBUTE", 6, 1, 3)]);

    [Fact]
    public void RecordedLayoutsGetTheirVariantsUnderAmdOnly()
    {
        var rec = Gfx(Hash("rs"), Vs, null, L((5, Rg16F, 2, 0), (6, Rg16F, 2, 0)), []);
        ExactLayouts X(UnitPolicy p) => ExactLayouts.Build([rec], new Dictionary<string, byte[]>(), p, new[] { Vs, Vs2 }.ToDictionary(s => s.Sha1));
        var amd = X(UnitPolicy.Amd);
        var exact = amd.Layouts(Vs);
        Assert.Equal(Provenance.Exact, exact.Provenance);
        Assert.Equal(["0,0", "0,4"], exact.Value.Select(l => string.Join(',', l.Select(e => e.Offset)))); // recorded first
        Assert.Single(amd.Layouts(Vs2).Value); // inferred from VS's signature: Recorded mode leaves it alone
        Assert.Single(X(UnitPolicy.Nvidia).Layouts(Vs).Value); // NVIDIA doesn't key on the layout
    }
}
