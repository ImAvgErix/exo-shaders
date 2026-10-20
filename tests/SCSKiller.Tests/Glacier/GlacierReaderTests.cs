using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;
using SCSKiller.Core;
using SCSKiller.Core.Glacier;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.Glacier;

/// <summary>The Glacier 2 reader on a synthetic install laid out as 007 First Light's: RPKG version 2 archives in Runtime, each
/// resource XORed with the fixed key and LZ4-packed or stored, material effects (MATE) holding shaders compiled here
/// (<see cref="Hlsl"/>) after their tables, each carrying its root signature.</summary>
public class GlacierReaderTests
{
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    static readonly byte[] Key = [0xDC, 0x45, 0xA6, 0x9C, 0xD3, 0x72, 0x4C, 0xAB];

    /// <summary>An RPKG version 2 archive: header, the deleted resources (a patch), index, resource table (<paramref name="stride"/>
    /// 20: 007 First Light's entries; 24: HITMAN 3's, with a states size), then the data. Each resource has one reference.</summary>
    internal static byte[] Archive(int stride, ulong[]? deleted, params (ulong Hash, string Type, byte[] Data, bool Pack)[] resources)
    {
        var data = new MemoryStream();
        var index = new MemoryStream();
        var table = new MemoryStream();
        var stored = new List<(byte[] Bytes, int Packed)>();
        foreach (var (_, _, d, pack) in resources)
        {
            var packed = new byte[LZ4Codec.MaximumOutputSize(d.Length)];
            var raw = pack ? packed[..LZ4Codec.Encode(d, packed)] : (byte[])d.Clone();
            for (var i = 0; i < raw.Length; i++) raw[i] ^= Key[i & 7];
            stored.Add((raw, pack ? raw.Length : 0));
        }
        foreach (var ((hash, type, d, _), i) in resources.Select((r, i) => (r, i)))
        {
            table.Write(Encoding.ASCII.GetBytes(new string(type.Reverse().ToArray())));
            table.Write(BitConverter.GetBytes(13));
            if (stride == 24) table.Write(BitConverter.GetBytes(0));
            table.Write(BitConverter.GetBytes(d.Length)); table.Write(BitConverter.GetBytes(d.Length)); table.Write(BitConverter.GetBytes(-1));
            table.Write(BitConverter.GetBytes(0xC0000001)); table.WriteByte(0x1F); table.Write(BitConverter.GetBytes(resources[0].Hash));
        }
        var start = 25 + (deleted == null ? 0 : 4 + 8 * deleted.Length) + 20 * resources.Length + table.Length;
        foreach (var ((hash, _, _, _), (bytes, packed)) in resources.Zip(stored))
        {
            index.Write(BitConverter.GetBytes(hash)); index.Write(BitConverter.GetBytes(start + data.Length));
            index.Write(BitConverter.GetBytes(0x80000000u | (uint)packed));
            data.Write(bytes);
        }
        var o = new MemoryStream();
        o.Write("2KPR"u8); o.Write(BitConverter.GetBytes(1)); o.Write([0, 0, (byte)(deleted == null ? 0 : 1), (byte)'x', (byte)'x']);
        o.Write(BitConverter.GetBytes(resources.Length)); o.Write(BitConverter.GetBytes(20 * resources.Length)); o.Write(BitConverter.GetBytes((int)table.Length));
        if (deleted != null) { o.Write(BitConverter.GetBytes(deleted.Length)); foreach (var d in deleted) o.Write(BitConverter.GetBytes(d)); }
        o.Write(index.ToArray()); o.Write(table.ToArray()); o.Write(data.ToArray());
        return o.ToArray();
    }

    /// <summary>A material effect: its tables, then the shaders with a few bytes between them.</summary>
    static byte[] Effect(params byte[][] shaders) => [.. new byte[236], .. shaders.SelectMany(s => s.Concat(new byte[3]))];

    [Fact]
    public void IndexesEachMaterialEffectAsAPoolOfTheCurrentResources()
    {
        var (vs, ps, replaced, deleted, text, patched) = (Hlsl.Vs(1, Hlsl.Rs1), Hlsl.Ps(1, Hlsl.Rs1), Hlsl.Ps(2, Hlsl.Rs2),
            Hlsl.Ps(3, Hlsl.Rs1), Hlsl.Ps(4, Hlsl.Rs1), Hlsl.Ps(5, Hlsl.Rs2));
        var dir = Ff7.TempDir("glacier-synthetic");
        var runtime = Path.Combine(dir, "Runtime");
        Directory.CreateDirectory(runtime);
        File.WriteAllBytes(Path.Combine(runtime, "chunk0.rpkg"), Archive(20, null,
            (1, "CBLU", new byte[85], true),
            (2, "MATE", Effect(vs, ps), true),
            (3, "MATE", Effect(replaced), false),
            (4, "MATE", Effect(deleted), true),
            (5, "TEXT", Effect(text), false)));   // shaders only in material effects
        // the patch, in HITMAN 3's table layout: deletes one effect and replaces another
        File.WriteAllBytes(Path.Combine(runtime, "chunk0patch1.rpkg"), Archive(24, [4], (3, "MATE", Effect(patched, vs), true)));
        File.WriteAllText(Path.Combine(runtime, "packagedefinition.txt"), "");
        var game = new Game("steam:3768760", "007 First Light", Store.Steam, dir, Path.Combine(dir, @"Retail\007FirstLight.exe"));

        var reader = new GlacierReader();
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(GlacierReader.Family, "DXBC+RTS0", null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, engine, null, Ff7.Nvidia));

        var log = new List<string>();
        var index = reader.Index(game, engine, new Progress(log.Add), CancellationToken.None);
        Assert.DoesNotContain(log, l => l.Contains("warning"));
        Assert.Equal(new[] { vs, ps, patched }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.All(index.Shaders.Values, s => Assert.NotNull(s.RootSignature));
        Assert.Equal(index.Shaders[Sha(vs)].RootSignature, index.Shaders[Sha(ps)].RootSignature);
        Assert.NotEqual(index.Shaders[Sha(ps)].RootSignature, index.Shaders[Sha(patched)].RootSignature);
        Assert.Equal(new[] { string.Join(',', Sha(vs), Sha(ps)), string.Join(',', Sha(patched), Sha(vs)) }.Order(),
            index.Maps.Select(m => string.Join(',', m.Shaders)).Order());

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, new[] { vs, patched }.Select(Sha).ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(new[] { vs, patched }.Select(Sha).Order(), got.Keys.Order());
        Assert.All(got, g => Assert.Equal(g.Key, Sha(g.Value)));

        var planner = new Planner();
        var planDir = Ff7.TempDir("glacier-plan");
        var plan = planner.Build(game, engine, index, null, Ff7.Nvidia, planDir, null, CancellationToken.None);
        Assert.True(plan.Stats.Generated >= 2, plan.Stats.ToString());
        var work = Path.Combine(planDir, "work");
        planner.Materialize(plan, game, engine, new GlacierReader(), null, work, CancellationToken.None);
        Ff7.CheckWarmReady(work);

        Assert.Null(reader.Detect(game with { InstallDir = runtime }));   // the archives are found in Runtime only
    }

    [Fact]
    public void NeedsARecordingWhenTheShadersCarryNoRootSignature()
    {
        var dir = Ff7.TempDir("glacier-no-rts0");
        Directory.CreateDirectory(Path.Combine(dir, "Runtime"));
        File.WriteAllBytes(Path.Combine(dir, @"Runtime\chunk0.rpkg"), Archive(20, null, (7, "MATE", Effect(Hlsl.Vs(1, null), Hlsl.Ps(1, null)), true)));
        var game = new Game("test:glacier", "Glacier", Store.Steam, dir, Path.Combine(dir, @"Retail\game.exe"));
        var engine = new GlacierReader().Detect(game)!;
        Assert.Equal("DXBC", engine.Version);
        Assert.Equal(Readiness.NeedsRecording, new Planner().Check(game, engine, null, Ff7.Nvidia).Readiness);
    }

    [Fact]
    public void OpensOnlyArchivesWhoseTablesAddUp()
    {
        var dir = Ff7.TempDir("glacier-bad");
        var good = Archive(20, null, (1, "MATE", Effect(Hlsl.Ps(1, Hlsl.Rs1)), true));
        File.WriteAllBytes(Path.Combine(dir, "chunk0.rpkg"), good);
        Assert.Equal(Rpkg.Type("MATE"), Assert.Single(Rpkg.Open(Path.Combine(dir, "chunk0.rpkg"))!.Resources).Type);
        var bad = (byte[])good.Clone();
        bad[21]++;   // the resource table one byte longer than its entries
        File.WriteAllBytes(Path.Combine(dir, "chunk1.rpkg"), bad);
        Assert.Null(Rpkg.Open(Path.Combine(dir, "chunk1.rpkg")));
        File.WriteAllBytes(Path.Combine(dir, "chunk2.rpkg"), [.. "GKPR"u8, .. good[4..]]);
        Assert.Null(Rpkg.Open(Path.Combine(dir, "chunk2.rpkg")));
    }

    sealed class Progress(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
