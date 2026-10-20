using System.Buffers.Binary;
using System.Text.RegularExpressions;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Nixxes;

namespace SCSKiller.Core.Glacier;

/// <summary>An RPKG version 2 archive of IO Interactive's Glacier 2 (as RPKG-Tool documents it). Little-endian header: "2KPR",
/// u32 1, u8 chunk, u8 chunk type, u8 patch, a 2-byte language tag, u32 resource count, u32 index
/// size, u32 resource table size; a patch archive (its file name holds "patch") then lists the resources it deletes (u32 count,
/// u64 hashes). Index, per resource: u64 hash, u64 data offset, u32 packed size (bit 31: XORed with a fixed 8-byte key; the
/// low 30 bits: the LZ4 block's size, 0 = stored). Resource table, in index order: the type's FourCC (reversed), u32 reference
/// table size, [u32 states size,] u32 size, u32 size in memory, u32 size in video memory, then the reference table. 007 First
/// Light's entries have no states size (20 bytes), HITMAN 3's do (24): the layout whose entries add up to the table is used.</summary>
public sealed partial class Rpkg
{
    public sealed record Resource(ulong Hash, uint Type, long Offset, int Packed, bool Xored, int Size);

    /// <summary>A FourCC as <see cref="Resource.Type"/> holds it (the table stores "MATE" as "ETAM").</summary>
    public static uint Type(string fourcc) => BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(fourcc));

    static ReadOnlySpan<byte> Key => [0xDC, 0x45, 0xA6, 0x9C, 0xD3, 0x72, 0x4C, 0xAB];

    /// <summary>The most index and table bytes an archive may claim (007 First Light's biggest: 36 MB).</summary>
    const int MaxTables = 256 << 20;

    public string Path { get; }
    /// <summary>From the file name (chunk&lt;N&gt;[patch&lt;M&gt;].rpkg): a later patch of a chunk replaces its resources.</summary>
    public int Chunk { get; }
    public int Patch { get; }
    public IReadOnlyList<ulong> Deleted { get; }
    public IReadOnlyList<Resource> Resources { get; }

    Rpkg(string path, int chunk, int patch, ulong[] deleted, Resource[] resources) =>
        (Path, Chunk, Patch, Deleted, Resources) = (path, chunk, patch, deleted, resources);

    [GeneratedRegex(@"^chunk(\d{1,6})(?:patch(\d{1,6}))?", RegexOptions.IgnoreCase)]
    private static partial Regex ChunkName();

    /// <summary>Whether the file starts with the RPKG version 2 magic.</summary>
    public static bool IsRpkg(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> m = stackalloc byte[4];
            return RandomAccess.Read(h, m, 0) == 4 && m.SequenceEqual("2KPR"u8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The archive's tables, or null when the file isn't one (or can't be read). They are charged to
    /// <paramref name="meter"/> before they're read; null when it can't pay for them.</summary>
    public static Rpkg? Open(string path, NixxesReader.Meter? meter = null)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = RandomAccess.GetLength(h);
            var head = new byte[25];
            if (RandomAccess.Read(h, head, 0) != head.Length || !head.AsSpan(0, 4).SequenceEqual("2KPR"u8)) return null;
            int count = I32(head, 13), indexSize = I32(head, 17), tableSize = I32(head, 21), at = 25;
            if (count < 0 || indexSize != count * 20L || tableSize < 0 || (long)indexSize + tableSize > MaxTables) return null;
            var name = ChunkName().Match(System.IO.Path.GetFileName(path));
            int chunk = name.Success ? int.Parse(name.Groups[1].Value) : 0, patch = name.Groups[2].Success ? int.Parse(name.Groups[2].Value) : 0;
            var deleted = Array.Empty<ulong>();
            if (System.IO.Path.GetFileName(path).Contains("patch", StringComparison.OrdinalIgnoreCase))
            {
                var n = new byte[4];
                if (RandomAccess.Read(h, n, at) != 4 || I32(n, 0) is var dn && (dn < 0 || dn > MaxTables / 8)) return null;
                var d = new byte[dn * 8];
                if (RandomAccess.Read(h, d, at + 4) != d.Length) return null;
                deleted = [.. Enumerable.Range(0, dn).Select(i => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(8 * i)))];
                at += 4 + d.Length;
            }
            if (meter != null && !meter.Take(indexSize + tableSize)) return null;
            var index = new byte[indexSize];
            var table = new byte[tableSize];
            if (RandomAccess.Read(h, index, at) != indexSize || RandomAccess.Read(h, table, at + indexSize) != tableSize) return null;
            var stride = Stride(table, count, 20) ? 20 : Stride(table, count, 24) ? 24 : 0;
            if (stride == 0) return null;
            var res = new Resource[count];
            for (int i = 0, t = 0; i < count; i++)
            {
                var e = index.AsSpan(20 * i);
                var packed = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
                var r = new Resource(BinaryPrimitives.ReadUInt64LittleEndian(e), BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(t)),
                    BinaryPrimitives.ReadInt64LittleEndian(e[8..]), (int)(packed & 0x3FFFFFFF), (packed & 0x80000000) != 0, I32(table, t + stride - 12));
                if (r.Offset < 0 || r.Size < 0 || r.Offset + (r.Packed > 0 ? r.Packed : r.Size) > len) return null;
                res[i] = r;
                t += stride + I32(table, t + 4);
            }
            return new Rpkg(path, chunk, patch, deleted, res);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The table holds exactly <paramref name="count"/> entries of this size (plus their reference tables).</summary>
    static bool Stride(byte[] table, int count, int stride)
    {
        long t = 0;
        for (var i = 0; i < count; i++)
        {
            if (t + stride > table.Length) return false;
            var refs = I32(table, (int)t + 4);
            if (refs < 0) return false;
            t += stride + refs;
        }
        return t == table.Length;
    }

    static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));

    /// <summary>A resource's bytes: un-XORed, then decompressed. InvalidDataException when they don't decompress to its size.</summary>
    public static byte[] Read(SafeFileHandle h, Resource r)
    {
        var raw = new byte[r.Packed > 0 ? r.Packed : r.Size];
        if (RandomAccess.Read(h, raw, r.Offset) != raw.Length) throw new InvalidDataException("a resource past the archive's end");
        if (r.Xored) for (var i = 0; i < raw.Length; i++) raw[i] ^= Key[i & 7];
        if (r.Packed == 0) return raw;
        var b = new byte[r.Size];
        if (LZ4Codec.Decode(raw, b) != b.Length) throw new InvalidDataException("a resource doesn't decompress to its size");
        return b;
    }

    /// <summary>The resources the game loads: per hash, its last chunk's latest patch's; those a later patch deletes are left out.</summary>
    public static IEnumerable<(Rpkg Archive, Resource Resource)> Current(IEnumerable<Rpkg> archives)
    {
        var current = new Dictionary<ulong, (Rpkg, Resource)>();
        foreach (var a in archives.OrderBy(a => a.Chunk).ThenBy(a => a.Patch))
        {
            foreach (var d in a.Deleted) current.Remove(d);
            foreach (var r in a.Resources) current[r.Hash] = (a, r);
        }
        return current.Values;
    }
}
