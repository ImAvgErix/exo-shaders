using System.Security.Cryptography;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.FromSoft;

/// <summary><see cref="RootSig.AsVersion10"/>: the root signatures FromSoftware's engine creates from the 1.1 ones its
/// shaders carry.</summary>
public class FromSoftRootSigTests
{
    /// <summary>Elden Ring's shaders carry two root signatures (graphics, compute); its recording creates every PSO of
    /// shipped shaders with these at version 1.0. The SHA-1s are the recorded blobs'.</summary>
    [Fact]
    public void EldenRingsRootSignaturesAreItsShadersAtVersion10()
    {
        var rows = new List<uint[]>();
        for (var vis = 1u; vis <= 5; vis++) // VS, HS, DS, GS, PS: CBVs b0-3, b4-13; SRVs t0-3, t4-19, t20-65; samplers s0-3, s4-15
            rows.AddRange([[0, vis, 2, 4, 0, 0, 0], [0, vis, 2, 10, 4, 0, 0], [0, vis, 0, 4, 0, 0, 0], [0, vis, 0, 16, 4, 0, 0], [0, vis, 0, 46, 20, 0, 0],
                [0, vis, 3, 4, 0, 0, 0], [0, vis, 3, 12, 4, 0, 0]]);
        rows.Add([0, 0, 1, 8, 0, 0, 0]);
        var graphics = RootSig.Serialize(new RootSig.Desc(1, rows), []);
        var compute = RootSig.Serialize(new RootSig.Desc(0x3e, [[0, 0, 0, 66, 0, 0, 0], [0, 0, 2, 14, 0, 0, 0], [0, 0, 3, 16, 0, 0, 0], [0, 0, 1, 8, 0, 0, 0]]), []);
        Assert.Equal("933b1951c4532d0912e164c233488ce262d302ef", Convert.ToHexStringLower(SHA1.HashData(RootSig.AsVersion10(graphics))));
        Assert.Equal("0e93201c87d0e117d87e3ee342d261ea3134531d", Convert.ToHexStringLower(SHA1.HashData(RootSig.AsVersion10(compute))));
    }

    /// <summary>Elden Ring's ray tracing root signatures (<see cref="SCSKiller.Core.FromSoft.SoulsRayTracing"/>), which no shader
    /// carries. The SHA-1s are the recorded blobs' (all 30 recorded collections use both).</summary>
    [Fact]
    public void EldenRingsRayTracingRootSignatures()
    {
        Assert.Equal("41b9c9383870b59ab7462ecfac7c0c1a8cdbf5f2", SCSKiller.Core.FromSoft.SoulsRayTracing.Global.Hash);
        Assert.Equal("c820a584432ba0523f0902c2c4a4c83b65ff6a54", SCSKiller.Core.FromSoft.SoulsRayTracing.Local.Hash);
    }

    /// <summary>Multi-range tables, root constants and descriptors and static samplers keep every register and visibility.</summary>
    [Fact]
    public void KeepsEveryRange()
    {
        var d = new RootSig.Desc(0x1, [[0, 5, 0, 4, 0, 0, 0, 2, 2, 0, 0, 0], [1, 0, 0, 1, 8], [2, 1, 3, 0, 0], [4, 0, 0, 999, 0]], AppendRanges: true);
        var sampler = new byte[52];
        BitConverter.GetBytes(0x15u).CopyTo(sampler, 0);
        for (var o = 4; o <= 12; o += 4) BitConverter.GetBytes(1u).CopyTo(sampler, o);
        BitConverter.GetBytes(float.MaxValue).CopyTo(sampler, 36);
        var v11 = RootSig.Serialize(d, sampler);
        var v10 = RootSig.AsVersion10(v11);
        Assert.Equal(1u, BitConverter.ToUInt32(RootSig.Rts0(v10), 0));
        var (a, b) = (RootSig.Parse(v11), RootSig.Parse(v10));
        Assert.Equal(a.Flags, b.Flags);
        Assert.Equal(a.Slots, b.Slots);
        Assert.Equal(RootSig.Samplers(v11), RootSig.Samplers(v10));
    }

    /// <summary>A hand-encoded 1.1 (version 2) or 1.2 (version 3) RTS0: <paramref name="rows"/> as in <see cref="RootSig.Desc"/>,
    /// each range's 6th value its descriptor flags and 7th its offset; root descriptors (2) carry flags as their 3rd value.</summary>
    static byte[] Rts0(uint version, uint flags, uint[][] rows, byte[] samplers)
    {
        var payload = new List<uint>();
        var at = 24 + 12 * rows.Length;
        var parms = new List<uint>();
        foreach (var r in rows)
        {
            parms.AddRange([r[0], r[1], (uint)(at + 4 * payload.Count)]);
            if (r[0] == 0)
            {
                var count = (r.Length - 2) / 6;
                payload.AddRange([(uint)count, (uint)(at + 4 * payload.Count + 8)]);
                for (var j = 0; j < count; j++) payload.AddRange(r.Skip(2 + 6 * j).Take(6));
            }
            else payload.AddRange(r.Skip(2));
        }
        var b = new byte[at + 4 * payload.Count + samplers.Length];
        uint[] head = [version, (uint)rows.Length, 24, (uint)(samplers.Length / (version == 3 ? 56 : 52)), (uint)(at + 4 * payload.Count), flags];
        for (var i = 0; i < head.Length; i++) BitConverter.GetBytes(head[i]).CopyTo(b, 4 * i);
        for (var i = 0; i < parms.Count; i++) BitConverter.GetBytes(parms[i]).CopyTo(b, 24 + 4 * i);
        for (var i = 0; i < payload.Count; i++) BitConverter.GetBytes(payload[i]).CopyTo(b, at + 4 * i);
        samplers.CopyTo(b, at + 4 * payload.Count);
        return b;
    }

    const uint Append = uint.MaxValue;

    /// <summary>The 1.0 blob's ranges in order: (type, count, base, space, offset) per range; root descriptors as (type, register, space).</summary>
    static List<uint[]> Rows10(byte[] blob)
    {
        var b = RootSig.Rts0(blob);
        uint U(uint o) => BitConverter.ToUInt32(b, (int)o);
        Assert.Equal(1u, U(0));
        var rows = new List<uint[]>();
        for (var i = 0u; i < U(4); i++)
        {
            var (type, p) = (U(U(8) + 12 * i), U(U(8) + 12 * i + 8));
            if (type == 0) for (var r = 0u; r < U(p); r++) rows.Add([.. Enumerable.Range(0, 5).Select(k => U(U(p + 4) + 20 * r + 4 * (uint)k))]);
            else rows.Add([type, U(p), U(p + 4)]);
        }
        return rows;
    }

    /// <summary>Descriptor and root descriptor flags (DATA_STATIC, DESCRIPTORS_VOLATILE) go; APPEND offsets become explicit,
    /// counted from the table's start; explicit offsets stay.</summary>
    [Fact]
    public void DropsFlagsAndMakesOffsetsExplicit()
    {
        var v11 = Rts0(2, 0, [
            [0, 0, 0, 4, 0, 0, 0x8, Append, 2, 2, 0, 0, 0x1, Append],  // SRV t0-3 data static, CBV b0-1 volatile: offsets 0, 4
            [0, 0, 1, 3, 0, 0, 0x2, 7, 1, 2, 5, 1, 0, Append],         // UAV u0-2 explicit 7, then u5 space 1 appended: 10
            [2, 0, 3, 0, 0x8],                                          // root CBV b3, data static
        ], []);
        List<uint[]> expected = [[0, 4, 0, 0, 0], [2, 2, 0, 0, 4], [1, 3, 0, 0, 7], [1, 2, 5, 1, 10], [2, 3, 0]];
        Assert.Equal(expected, Rows10(RootSig.AsVersion10(v11)));
    }

    /// <summary>A 1.2 static sampler with flags has no 1.0 form; one without converts.</summary>
    [Fact]
    public void RefusesA12SamplerWithFlags()
    {
        byte[] Sampler(uint flags)
        {
            var s = new byte[56];
            BitConverter.GetBytes(0x15u).CopyTo(s, 0);
            for (var o = 4; o <= 12; o += 4) BitConverter.GetBytes(1u).CopyTo(s, o);
            BitConverter.GetBytes(float.MaxValue).CopyTo(s, 36);
            BitConverter.GetBytes(flags).CopyTo(s, 52);
            return s;
        }
        Assert.Throws<RootSig.SerializeException>(() => RootSig.AsVersion10(Rts0(3, 0, [], Sampler(1))));
        Assert.Equal(52, RootSig.Samplers(RootSig.AsVersion10(Rts0(3, 0, [], Sampler(0)))).Length);
    }
}
