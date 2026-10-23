using System.Runtime.InteropServices;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Snowdrop;

/// <summary>The root signature Snowdrop (Star Wars Outlaws) builds at run time for a graphics or compute pipeline: the same
/// for every pipeline of a kind. Parameters: a root CBV (b0 space 2); a bindless table (unbounded SRV and UAV ranges, one
/// per space, and the AMD extension UAV); a table of 4 CBVs at b2600; tables of the engine's constant, texture and buffer
/// slots (each slot 20 registers apart: constants from slot <see cref="FirstConstants"/>, textures from
/// <see cref="FirstTexture"/>, buffers from <see cref="FirstBuffer"/>), with explicit offsets; a 4-sampler table; then root
/// SRVs t2940, t2960, t2980 (and t3000 for compute). Static samplers: the engine's 32 registered ones in space 1
/// (<see cref="Samplers"/>). Flags: input layout for graphics, deny every stage it doesn't run (mesh and amplification always).</summary>
public static class SnowdropRootSig
{
    const uint Srv = 0, Uav = 1, Cbv = 2, Sampler = 3, Unbounded = uint.MaxValue;
    const uint Volatile = 1;   // D3D12_DESCRIPTOR_RANGE_FLAG_DESCRIPTORS_VOLATILE
    /// <summary>The engine runs mesh pipelines too; their root signature isn't rebuilt (<see cref="Build"/> refuses them).</summary>
    internal const bool MeshShaders = true;
    const uint ExtensionSpace = 0x7FFF0ADE;   // AGS_DX12_SHADER_INSTRINSICS_SPACE_ID
    const uint FirstTexture = 32, FirstConstants = 132, FirstBuffer = 153;
    const uint Constants = 12, Buffers = 13;
    const uint MaxAnisotropy = 16;
    const uint SlotRegisters = 20;

    /// <summary>The registers of each texture slot (32..128): most hold one, a few an array.</summary>
    static readonly uint[] TextureCounts = [.. Enumerable.Range((int)FirstTexture, 97).Select(id => id switch
    {
        35 or 37 => 3u,
        45 or 88 or 125 or 126 => 2u,
        _ => 1u,
    })];

    /// <summary>The engine's registered samplers, in register order (s0..s31, space 1): D3D12_FILTER, address U, V, W,
    /// anisotropic (<see cref="MaxAnisotropy"/>, else 1), comparison function, border color.</summary>
    static readonly (uint Filter, uint U, uint V, uint W, bool Aniso, uint Compare, uint Border)[] Registered =
    [
        (0x55, 1, 1, 1, true, 1, 0), (0x00, 3, 3, 3, true, 1, 0), (0x00, 1, 1, 1, false, 1, 0), (0x14, 3, 3, 3, true, 1, 0),
        (0x55, 4, 4, 4, true, 1, 0), (0x15, 1, 3, 3, false, 1, 0), (0x15, 3, 3, 3, false, 1, 0), (0x94, 3, 3, 3, true, 5, 0),
        (0x00, 1, 1, 1, true, 1, 0), (0x15, 1, 1, 1, false, 1, 0), (0x55, 3, 3, 3, true, 1, 0), (0x14, 3, 3, 3, false, 1, 0),
        (0x14, 1, 1, 1, true, 1, 0), (0x00, 3, 3, 3, false, 1, 0), (0x00, 4, 4, 4, true, 1, 0), (0x80, 3, 3, 3, true, 5, 0),
        (0x55, 2, 2, 2, true, 1, 0), (0x55, 1, 3, 1, true, 1, 0), (0x55, 1, 3, 3, true, 1, 0), (0x55, 3, 3, 1, true, 1, 0),
        (0x55, 1, 1, 3, true, 1, 0), (0x55, 3, 3, 3, true, 1, 1), (0x55, 1, 4, 1, true, 1, 0), (0x55, 5, 5, 5, true, 1, 0),
        (0x01, 1, 1, 1, true, 1, 0), (0x55, 3, 1, 1, true, 1, 0), (0x55, 3, 1, 3, true, 1, 0), (0x14, 4, 3, 3, true, 1, 0),
        (0x55, 4, 4, 4, true, 1, 2), (0x14, 1, 3, 3, true, 1, 0), (0x14, 4, 4, 4, true, 1, 1), (0x00, 2, 2, 2, true, 1, 0),
    ];

    /// <summary>The static samplers, as D3D12_STATIC_SAMPLER_DESC records (13 u32 each).</summary>
    public static byte[] Samplers => MemoryMarshal.AsBytes(Registered.SelectMany((s, i) => new uint[]
    {
        s.Filter, s.U, s.V, s.W, 0, s.Aniso ? MaxAnisotropy : 1, s.Compare, s.Border, 0, BitConverter.SingleToUInt32Bits(float.MaxValue), (uint)i, 1, 0,
    }).ToArray().AsSpan()).ToArray();

    public static RootSig.Desc Build(IReadOnlyDictionary<Stage, ShaderInfo> stages)
    {
        if (stages.ContainsKey(Stage.Mesh) || stages.ContainsKey(Stage.Amplification)) throw new RootSig.SerializeException("Snowdrop mesh pipelines aren't rebuilt");
        var compute = stages.ContainsKey(Stage.Compute);
        // each range: type, count, base register, space, flags, offset in its table
        var textures = new List<uint>();
        for (uint slot = 0, offset = 0; slot < TextureCounts.Length; offset += TextureCounts[slot++])
            textures.AddRange([Srv, TextureCounts[slot], (FirstTexture + slot) * SlotRegisters, 0, Volatile, offset]);
        List<uint[]> rows =
        [
            [2, 0, 0, 2, 0],   // root CBV b0 space 2
            Bindless(),
            Table([Cbv, 4, 2600, 0, Volatile, 0]),
            Table([.. Enumerable.Range(0, (int)Constants).SelectMany(i => new uint[] { Cbv, 1, (FirstConstants + (uint)i) * SlotRegisters, 0, Volatile, (uint)i })]),
            Table([.. textures]),
            Table([.. Enumerable.Range(0, (int)Buffers).SelectMany(i => new uint[] { Srv, 1, (FirstBuffer + (uint)i) * SlotRegisters, 0, Volatile, (uint)i })]),
            Table([Sampler, 4, 0, 0, 0, 0]),
        ];
        foreach (var reg in compute ? new uint[] { 2940, 2960, 2980, 3000 } : [2940, 2960, 2980]) rows.Add([3, 0, reg, 0, 0]);   // root SRVs
        // graphics: ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT, deny hull, domain, geometry; compute: deny every graphics stage; both deny AS and MS
        return new RootSig.Desc((compute ? 0x3Eu : 0x1D) | 0x300, rows, AppendRanges: false, Version10: false, RangeOffsets: true);
    }

    /// <summary>A descriptor table visible to every stage.</summary>
    static uint[] Table(uint[] ranges) => [0, 0, .. ranges];

    /// <summary>The bindless table: unbounded ranges at register 0, one per space, and the AMD extension UAV (u0).</summary>
    static uint[] Bindless()
    {
        var r = new List<uint>();
        void Spaces(uint type, uint first, int count)
        {
            for (var k = 0u; k < count; k++) r.AddRange([type, Unbounded, 0, first + k, Volatile, 0]);
        }
        Spaces(Srv, 600, 16);
        Spaces(Srv, 3380, 14);
        Spaces(Srv, 2900, 15);
        Spaces(Uav, 3340, 8);
        Spaces(Uav, 3360, 8);
        r.AddRange([Uav, 1, 0, ExtensionSpace, Volatile, 0]);
        Spaces(Srv, 3400, 64);
        Spaces(Uav, 3464, 64);
        return Table([.. r]);
    }
}
