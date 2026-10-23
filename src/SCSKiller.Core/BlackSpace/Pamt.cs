using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.BlackSpace;

/// <summary>A BlackSpace archive group's index (NNNN\0.pamt beside its N.paz files): u32 hash, u32 .paz count, a byte, three
/// bytes XORed into the key byte, then the .paz table (12 bytes each: 8, u32 size), the folder and file name tries (u32 size
/// each), the folders (16 bytes: 4, name, first file, file count) and the files (20 bytes: name, offset, stored size, size,
/// u16 .paz, u16 flags). Every count, offset and name is checked against the index and its .paz sizes before use.</summary>
public sealed class Pamt
{
    /// <param name="Stored">its bytes in the .paz (compressed, encrypted)</param>
    /// <param name="Flags">low nibble: compression (0 none, 2 LZ4 block); next: encryption (0 none, 3 ChaCha20, <see cref="PazCrypto"/>)</param>
    public sealed record Entry(string Path, int Paz, uint Offset, uint Stored, uint Size, ushort Flags)
    {
        public int Compression => Flags & 0xF;
        public int Encryption => (Flags >> 4) & 0xF;
    }

    public const uint MaxEntry = 64 << 20;
    public const int MaxIndex = 64 << 20, MaxPath = 1024;
    /// <summary>The names an index may spell out: <see cref="MaxExpansion"/> characters per byte of it (a trie shares
    /// prefixes), at most <see cref="MaxChars"/>: a crafted trie can't make it build gigabytes of strings.</summary>
    const long MaxExpansion = 16, MaxChars = 192 << 20;
    /// <summary>LZ4's best ratio is about 255: a larger claimed size isn't an LZ4 block.</summary>
    const ulong MaxRatio = 255;

    public uint Hash { get; }
    /// <summary>The byte folded into each entry's key (<see cref="PazCrypto.Decrypt"/>).</summary>
    public byte KeyByte { get; }
    public IReadOnlyList<uint> PazSizes { get; }
    public IReadOnlyList<string> Folders { get; }
    public IReadOnlyList<Entry> Files { get; }

    static long Budget(int bytes) => Math.Min(MaxExpansion * bytes + 1024, MaxChars);

    Pamt(uint hash, byte keyByte, IReadOnlyList<uint> paz, IReadOnlyList<string> folders, IReadOnlyList<Entry> files) =>
        (Hash, KeyByte, PazSizes, Folders, Files) = (hash, keyByte, paz, folders, files);

    public static Pamt Read(string path) => Parse(Bytes(path));

    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static byte[] Bytes(string path)
    {
        using var f = Open(path);
        if (f.Length > MaxIndex) throw new InvalidDataException($"an index of {f.Length >> 20} MB");
        var b = new byte[f.Length];
        f.ReadExactly(b);
        return b;
    }

    /// <summary>Only the folder names, reading the index up to its folder trie (finding the group that holds the shaders
    /// reads a few KB of each, not all of every index).</summary>
    public static IReadOnlyList<string> FolderNames(string path)
    {
        using var f = Open(path);
        byte[] Head(long n)
        {
            if (n > f.Length || n > MaxIndex) throw new InvalidDataException("a table past the end of the index");
            var b = new byte[n];
            f.Position = 0;
            f.ReadExactly(b);
            return b;
        }
        var trieAt = 12 + 12L * BinaryPrimitives.ReadUInt32LittleEndian(Head(8).AsSpan(4));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(Head(trieAt + 4).AsSpan((int)trieAt));
        var trie = Head(trieAt + 4 + size).AsSpan((int)trieAt + 4);
        var budget = Budget(trie.Length);
        return [.. Trie(trie, ref budget).Values];
    }

    public static Pamt Parse(ReadOnlySpan<byte> b)
    {
        var r = new Reader(b);
        var hash = r.U32();
        var pazCount = r.U32();
        r.Skip(1);
        var keyByte = (byte)(r.U8() ^ r.U8() ^ r.U8());
        var paz = new uint[r.Count(pazCount, 12)];
        for (var i = 0; i < paz.Length; i++)
        {
            r.Skip(8);
            paz[i] = r.U32();
        }
        var budget = Budget(b.Length);
        var folderNames = Trie(r.Bytes(r.U32()), ref budget);
        var fileNames = Trie(r.Bytes(r.U32()), ref budget);
        var folders = new (uint Name, uint First, uint Count)[r.Count(r.U32(), 16)];
        for (var i = 0; i < folders.Length; i++)
        {
            r.Skip(4);
            folders[i] = (r.U32(), r.U32(), r.U32());
        }
        var files = new (uint Name, uint Offset, uint Stored, uint Size, ushort Paz, ushort Flags)[r.Count(r.U32(), 20)];
        for (var i = 0; i < files.Length; i++) files[i] = (r.U32(), r.U32(), r.U32(), r.U32(), r.U16(), r.U16());
        if (!r.AtEnd) throw new InvalidDataException("bytes after the file table");

        var entries = new List<Entry>(files.Length);
        var owned = new bool[files.Length];
        foreach (var (name, first, count) in folders)
        {
            if (!folderNames.TryGetValue(name, out var folder) || first > files.Length || count > files.Length - first)
                throw new InvalidDataException("a folder outside the tables");
            if (owned.AsSpan((int)first, (int)count).Contains(true)) throw new InvalidDataException("folders sharing files");
            owned.AsSpan((int)first, (int)count).Fill(true);
            for (var i = first; i < first + count; i++)
            {
                var f = files[i];
                if (!fileNames.TryGetValue(f.Name, out var file) || f.Paz >= paz.Length || f.Size > MaxEntry || f.Stored > MaxEntry
                    || (ulong)f.Offset + f.Stored > paz[f.Paz] || (f.Flags & 0xF) == 2 && f.Size > MaxRatio * f.Stored + 16)
                    throw new InvalidDataException("a file outside the tables");
                if ((budget -= folder.Length + 1 + file.Length) < 0) throw new InvalidDataException("names longer than the index holds");
                entries.Add(new Entry(folder + "/" + file, f.Paz, f.Offset, f.Stored, f.Size, f.Flags));
            }
        }
        foreach (var inPaz in entries.Where(f => f.Stored != 0).GroupBy(f => f.Paz))
        {
            long end = 0;
            foreach (var e in inPaz.OrderBy(f => f.Offset))
            {
                if (e.Offset < end) throw new InvalidDataException("files sharing bytes");
                end = (long)e.Offset + e.Stored;
            }
        }
        return new Pamt(hash, keyByte, paz, [.. folders.Select(f => folderNames[f.Name])], entries);
    }

    /// <summary>A name trie: records of (u32 parent's offset or 0xFFFFFFFF, u8 length, the name's tail), each name its
    /// parent's plus its tail, by record offset. A parent must come before its child.</summary>
    static Dictionary<uint, string> Trie(ReadOnlySpan<byte> b, ref long budget)
    {
        var names = new Dictionary<uint, string>();
        var r = new Reader(b);
        while (!r.AtEnd)
        {
            var at = (uint)r.Position;
            var parent = r.U32();
            var tail = Encoding.UTF8.GetString(r.Bytes(r.U8()));
            if (parent == uint.MaxValue) names[at] = tail;
            else if (parent < at && names.TryGetValue(parent, out var head) && head.Length + tail.Length <= MaxPath) names[at] = head + tail;
            else throw new InvalidDataException("a name whose parent isn't before it, or too long");
            if ((budget -= names[at].Length) < 0) throw new InvalidDataException("names longer than the index holds");
        }
        return names;
    }

    /// <summary>An entry's bytes, decrypted (with <paramref name="keyBase"/>) and decompressed; null and <paramref name="why"/>
    /// when it can't be read.</summary>
    public static byte[]? Open(SafeFileHandle paz, Entry e, byte[]? keyBase, byte keyByte, out string? why)
    {
        why = e.Compression is not (0 or 2) ? $"compression {e.Compression}"
            : e.Encryption is not (0 or 3) ? $"encryption {e.Encryption}"
            : e.Encryption == 3 && keyBase == null ? "encrypted"
            : e.Compression == 0 && e.Stored != e.Size ? "a stored entry of two sizes"
            : e.Flags > 0xFF ? $"flags 0x{e.Flags:x}"
            : null;
        if (why != null) return null;
        var stored = new byte[e.Stored];
        if ((long)e.Offset + e.Stored > RandomAccess.GetLength(paz) || RandomAccess.Read(paz, stored, e.Offset) != stored.Length)
        {
            why = "past the end of its .paz";
            return null;
        }
        if (e.Encryption == 3) PazCrypto.Decrypt(stored, e.Path, keyBase!, keyByte);
        if (e.Compression == 0) return stored;
        var data = new byte[e.Size];
        if (LZ4Codec.Decode(stored, data) == data.Length) return data;
        why = "an LZ4 block that doesn't decompress to its size";
        return null;
    }

    /// <summary>Little-endian reads that throw InvalidDataException past the end.</summary>
    ref struct Reader(ReadOnlySpan<byte> b)
    {
        readonly ReadOnlySpan<byte> b = b;
        int at;

        public readonly int Position => at;
        public readonly bool AtEnd => at == b.Length;

        ReadOnlySpan<byte> Take(long n)
        {
            if (n < 0 || n > b.Length - at) throw new InvalidDataException("a table past the end of the index");
            var s = b.Slice(at, (int)n);
            at += (int)n;
            return s;
        }

        public void Skip(long n) => Take(n);
        public ReadOnlySpan<byte> Bytes(long n) => Take(n);
        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        /// <summary><paramref name="n"/> records of <paramref name="size"/> bytes, when that many are left.</summary>
        public readonly int Count(uint n, int size) =>
            n <= (uint)(b.Length - at) / (uint)size ? (int)n : throw new InvalidDataException("a count past the end of the index");
    }
}
