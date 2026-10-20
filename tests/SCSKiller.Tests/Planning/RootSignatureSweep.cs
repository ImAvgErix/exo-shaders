using System.Buffers.Binary;

namespace SCSKiller.Tests.Planning;

/// <summary>Random serialized root signatures (RTS0 parts, versions 1.0 to 1.2) whose fields stay mostly near the valid
/// ones, so the rules are what decides: few registers (they overlap), every flag bit and its neighbours, the enums' ends.
/// Deterministic for a seed. Linked into the conformance fixturegen, whose corpus carries them to the Rust build.</summary>
public static class RootSignatureSweep
{
    /// <summary>The sweep RootSignatureValidTests checks against the runtime and the corpus carries.</summary>
    public const int Seed = 20261002, Count = 4000;

    public static IEnumerable<byte[]> Parts(int seed, int count)
    {
        var rnd = new Random(seed);
        for (var i = 0; i < count; i++) yield return Part(rnd);
    }

    static uint Pick(Random r, params uint[] v) => v[r.Next(v.Length)];
    static uint Bits(Random r, params uint[] bits) => bits.Aggregate(0u, (a, b) => r.Next(3) == 0 ? a | b : a);

    static byte[] Part(Random r)
    {
        var ver = Pick(r, 1, 2, 3);
        var flags = Bits(r, 0x1, 0x2, 0x4, 0x8, 0x10, 0x20, 0x40, 0x80, 0x100, 0x200, 0x400, 0x800, 0x80000000) | (r.Next(40) == 0 ? 0x1000u : 0);
        var parms = new List<(uint Type, uint Vis, List<uint> Payload)>();
        for (var n = r.Next(4); n > 0; n--)
        {
            var type = r.Next(30) == 0 ? 5u : Pick(r, 0, 0, 1, 2, 3, 4);
            var vis = r.Next(30) == 0 ? 8u : Pick(r, 0, 0, 1, 5, 2, 3, 4, 6, 7);
            var p = new List<uint>();
            if (type == 0)
            {
                var ranges = r.Next(25) == 0 ? 0 : 1 + r.Next(2);
                var kind = Pick(r, 0, 1, 2, 3);
                for (var k = 0; k < ranges; k++)
                {
                    var t = r.Next(5) == 0 ? Pick(r, 0, 1, 2, 3, 4) : kind;
                    var num = r.Next(25) == 0 ? 0 : r.Next(8) == 0 ? uint.MaxValue : 1 + (uint)r.Next(3);
                    var lb = r.Next(25) == 0 ? uint.MaxValue - 1 : (uint)r.Next(3);
                    var space = r.Next(25) == 0 ? 0xFFFFFFF0 : (uint)r.Next(2);
                    var offset = r.Next(3) == 0 ? (uint)r.Next(4) : uint.MaxValue;
                    if (ver == 1) p.AddRange([t, num, lb, space, offset]);
                    else p.AddRange([t, num, lb, space, Bits(r, 0x1, 0x2, 0x4, 0x8, 0x10000) | (r.Next(40) == 0 ? 0x20u : 0), offset]);
                }
                p.Insert(0, (uint)ranges);
            }
            else if (type == 1) p.AddRange([(uint)r.Next(3), (uint)r.Next(2), 1 + (uint)r.Next(4)]);
            else if (type <= 4)
            {
                p.AddRange([(uint)r.Next(3), (uint)r.Next(2)]);
                if (ver > 1) p.Add(Bits(r, 0x2, 0x4, 0x8) | (r.Next(30) == 0 ? 0x1u : 0));
            }
            parms.Add((type, vis, p));
        }
        var samplers = new List<uint[]>();
        for (var n = r.Next(3); n > 0; n--)
        {
            uint baseFilter = r.Next(30) == 0 ? 2 : Pick(r, 0, 1, 4, 5, 0x10, 0x11, 0x14, 0x15, 0x55), reduction = r.Next(30) == 0 ? 4 : (uint)r.Next(4);
            uint Address() => r.Next(30) == 0 ? Pick(r, 0, 6) : 1 + (uint)r.Next(5);
            var s = new List<uint>
            {
                baseFilter | reduction << 7, Address(), Address(), Address(),
                BitConverter.SingleToUInt32Bits(r.Next(20) == 0 ? Pick(r, 16, 0x7FC00000, 0xC1800001) is var x ? BitConverter.UInt32BitsToSingle(x) : 0 : r.Next(-16, 16)),
                r.Next(20) == 0 ? 17u : (uint)r.Next(17), r.Next(20) == 0 ? Pick(r, 0, 9) : 1 + (uint)r.Next(8), r.Next(20) == 0 ? 5u : (uint)r.Next(5),
                BitConverter.SingleToUInt32Bits(r.Next(20) == 0 ? float.NaN : r.Next(0, 4)), BitConverter.SingleToUInt32Bits(r.Next(20) == 0 ? float.NaN : r.Next(2) == 0 ? float.MaxValue : r.Next(0, 16)),
                (uint)r.Next(3), r.Next(25) == 0 ? 0xFFFFFFF0 : (uint)r.Next(2), r.Next(30) == 0 ? 8u : Pick(r, 0, 0, 1, 5, 2, 3, 4, 6, 7),
            };
            if (ver == 3)
            {
                s.Add(Bits(r, 0x1, 0x2) | (r.Next(30) == 0 ? 0x4u : 0));
                // non-normalized coordinates hold only with no bias, LODs 0 and U, V clamped or bordered: often enough
                if ((s[13] & 2) != 0 && r.Next(2) == 0) (s[4], s[8], s[9], s[1], s[2]) = (0, 0, 0, Pick(r, 3, 4), Pick(r, 3, 4));
            }
            samplers.Add([.. s]);
        }
        return Encode(ver, flags, parms, samplers);
    }

    /// <summary>The RTS0 layout: header, parameter table, each parameter's payload (a table's ranges right after its
    /// header), the static samplers.</summary>
    public static byte[] Encode(uint ver, uint flags, List<(uint Type, uint Vis, List<uint> Payload)> parms, List<uint[]> samplers)
    {
        var words = new List<uint>();
        var payloadAt = 24 + 12 * parms.Count;
        var table = new List<uint>();
        foreach (var (type, vis, p) in parms)
        {
            var at = (uint)(payloadAt + 4 * words.Count);
            table.AddRange([type, vis, at]);
            if (type == 0) words.AddRange([p[0], at + 8, .. p.Skip(1)]);
            else words.AddRange(p);
        }
        var samplersAt = (uint)(payloadAt + 4 * words.Count);
        uint[] all = [ver, (uint)parms.Count, 24, (uint)samplers.Count, samplersAt, flags, .. table, .. words, .. samplers.SelectMany(s => s)];
        var b = new byte[4 * all.Length];
        for (var i = 0; i < all.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 * i), all[i]);
        return b;
    }
}
