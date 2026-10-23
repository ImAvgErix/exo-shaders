using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Carved;

/// <summary>The fields of a PE file's version resource SCSKiller reads (FileVersionInfo's names). <see cref="GetVersionInfo"/>
/// asks Windows; <see cref="Read"/> parses the resource itself (RT_VERSION's VS_VERSIONINFO: VS_FIXEDFILEINFO, then the
/// StringFileInfo table of its translation), for where FileVersionInfo doesn't read PE files. A file without one, or a
/// malformed one, reads as <see cref="None"/>.</summary>
public sealed record PeVersionInfo(int FileMajorPart, int FileMinorPart, int FileBuildPart, int FilePrivatePart, string? ProductName, string? FileDescription,
    string? OriginalFilename)
{
    public static readonly PeVersionInfo None = new(0, 0, 0, 0, null, null, null);

    /// <summary>The most of a version resource read (they are a few KB).</summary>
    const int MaxVersionBytes = 64 << 10;

    /// <summary>Like FileVersionInfo.GetVersionInfo: FileNotFoundException when the file doesn't exist.</summary>
    public static PeVersionInfo GetVersionInfo(string fileName)
    {
        var v = FileVersionInfo.GetVersionInfo(fileName);
        return new(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart, v.ProductName, v.FileDescription, v.OriginalFilename);
    }

    /// <summary><see cref="GetVersionInfo"/> without the OS: FileNotFoundException when the file doesn't exist, <see cref="None"/>
    /// when it doesn't read.</summary>
    internal static PeVersionInfo ReadManaged(string fileName)
    {
        fileName = Path.GetFullPath(fileName);
        if (!File.Exists(fileName)) throw new FileNotFoundException(fileName);
        try
        {
            using var f = GameFiles.OpenRegular(fileName);
            return f != null ? Read(f) : None;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return None; }
    }

    internal static PeVersionInfo Read(Stream pe)
    {
        try { return Resource(pe) is { } r ? Parse(r) : None; }
        catch (Exception e) when (e is BadImageFormatException or Malformed or ArgumentOutOfRangeException) { return None; }
    }

    /// <summary>The first RT_VERSION resource's bytes (id 1; language neutral, else en-US, else the first); null if none.</summary>
    static byte[]? Resource(Stream stream)
    {
        var headers = new PEHeaders(stream, (int)Math.Min(stream.Length, int.MaxValue));
        if (headers.PEHeader is not { NumberOfRvaAndSizes: > 2 } h || h.ResourceTableDirectory is not { Size: >= 16 } dir) return null;
        var pe = new Image(stream, headers);
        var root = pe.Offset(dir.RelativeVirtualAddress);
        var type = Entry(pe, root, root, 16, wantDir: true);   // RT_VERSION
        var name = type < 0 ? -1 : Entry(pe, root, type, 1, wantDir: true);
        if (name < 0) return null;
        var data = Entry(pe, root, name, 0, wantDir: false) is var neutral and >= 0 ? neutral
            : Entry(pe, root, name, 1033, wantDir: false) is var enUs and >= 0 ? enUs
            : First(pe, root, name);
        if (data < 0) return null;
        // IMAGE_RESOURCE_DATA_ENTRY: OffsetToData (an RVA), Size
        var at = pe.Offset(pe.U32(data));
        long size = pe.U32(data + 4);
        if (at + size > pe.Length) throw new Malformed();
        var b = new byte[Math.Min(size, MaxVersionBytes)];
        pe.Read(at, b);
        return b;
    }

    /// <summary>The entry of id <paramref name="id"/> in a resource directory (its id entries are sorted, after the named
    /// ones): its subdirectory or data entry's file offset; -1 if none, or not of the kind wanted.</summary>
    static long Entry(Image pe, long root, long dir, ushort id, bool wantDir)
    {
        int lo = pe.U16(dir + 12), hi = lo + pe.U16(dir + 14) - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var entry = dir + 16 + 8L * mid;
            var at = pe.U16(entry);
            if (at == id) return Target(pe, root, entry, wantDir);
            if (at > id) hi = mid - 1;
            else lo = mid + 1;
        }
        return -1;
    }

    static long First(Image pe, long root, long dir)
    {
        var count = pe.U16(dir + 12) + pe.U16(dir + 14);
        for (var i = 0; i < count; i++)
            if (Target(pe, root, dir + 16 + 8L * i, wantDir: false) is var t and >= 0) return t;
        return -1;
    }

    /// <summary>OffsetToData: high bit set for a subdirectory, the rest an offset from the resource directory's start.</summary>
    static long Target(Image pe, long root, long entry, bool wantDir)
    {
        var to = pe.U32(entry + 4);
        return (to >> 31 == 1) == wantDir ? root + (to & 0x7FFFFFFF) : -1;
    }

    static PeVersionInfo Parse(byte[] d)
    {
        if (d.Length < 6 || d[4] >= 32) throw new Malformed();
        var fixedInfo = Value(d, 0);
        if (ValueBytes(d, 0) < 52 || U32(d, fixedInfo) != 0xFEEF04BD) throw new Malformed();   // VS_FIXEDFILEINFO's signature
        uint ms = U32(d, fixedInfo + 8), ls = U32(d, fixedInfo + 12);
        // the translation it names (language << 16 | code page), else en-US Windows-1252
        var translation = Find(d, 0, "VarFileInfo") is var vars and >= 0 && Find(d, vars, "Translation") is var t and >= 0
            ? (uint)(U16(d, Value(d, t)) << 16 | U16(d, Value(d, t) + 2)) : 0x040904E4u;
        var strings = Table(d, translation);
        if (strings.FileVersion.Length == 0)
            foreach (var other in new uint[] { 0x040904B0, 0x040904E4, 0x04090000 })   // en-US Unicode, Windows-1252, neutral
                if (other != translation && (strings = Table(d, other)).FileVersion.Length > 0) break;
        return new((int)(ms >> 16), (int)(ms & 0xFFFF), (int)(ls >> 16), (int)(ls & 0xFFFF), strings.Product, strings.Description, strings.Original);
    }

    static (string FileVersion, string Product, string Description, string Original) Table(byte[] d, uint codePage)
    {
        var table = Find(d, 0, "StringFileInfo") is var info and >= 0 ? Find(d, info, codePage.ToString("X8")) : -1;
        return (Text(d, table, "FileVersion"), Text(d, table, "ProductName"), Text(d, table, "FileDescription"), Text(d, table, "OriginalFilename"));
    }

    /// <summary>A string's value (UTF-16, up to its NUL); "" if the table hasn't it.</summary>
    static string Text(byte[] d, int table, string key)
    {
        var block = table < 0 ? -1 : Find(d, table, key);
        if (block < 0 || U16(d, block + 2) == 0) return "";
        int start = Value(d, block), end = Math.Min(block + U16(d, block), d.Length);
        if (start >= end) return "";
        var chars = MemoryMarshal.Cast<byte, char>(d.AsSpan(start, (end - start) & ~1));
        var nul = chars.IndexOf('\0');
        return new string(nul >= 0 ? chars[..nul] : chars);
    }

    /// <summary>The child of <paramref name="block"/> keyed <paramref name="key"/> (ASCII, any case); -1 if none. A block:
    /// u16 length, u16 value length, u16 type (0 binary, 1 text), its key (UTF-16, NUL), its value and children, each 4-aligned.</summary>
    static int Find(byte[] d, int block, string key)
    {
        var end = block + U16(d, block);
        for (var i = Value(d, block) + Align(ValueBytes(d, block)); i < end;)
        {
            if (KeyIs(d, i, key)) return i;
            var length = U16(d, i);
            if (length == 0) return -1;
            i += Align(length);
        }
        return -1;
    }

    static bool KeyIs(byte[] d, int block, string key)
    {
        static int Lower(int c) => c is >= 'A' and <= 'Z' ? c + 32 : c;
        for (var i = 0; i < key.Length; i++)
            if (Lower(U16(d, block + 6 + 2 * i)) != Lower(key[i])) return false;
        return U16(d, block + 6 + 2 * key.Length) == 0;
    }

    /// <summary>Where a block's value starts: past its key's NUL, aligned.</summary>
    static int Value(byte[] d, int block)
    {
        var i = block + 6;
        while (U16(d, i) != 0) i += 2;
        return block + Align(i + 2 - block);
    }

    static int ValueBytes(byte[] d, int block) => U16(d, block + 2) * (U16(d, block + 4) == 0 ? 1 : 2);

    static int Align(int n) => (n + 3) & ~3;

    static ushort U16(byte[] d, int at) => at >= 0 && at <= d.Length - 2 ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at)) : throw new Malformed();

    static uint U32(byte[] d, int at) => at >= 0 && at <= d.Length - 4 ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)) : throw new Malformed();

    sealed class Malformed : Exception;

    /// <summary>The file's bytes by RVA, through its section table.</summary>
    sealed class Image(Stream stream, PEHeaders headers)
    {
        public readonly long Length = stream.Length;

        public long Offset(long rva)
        {
            foreach (var s in headers.SectionHeaders)
                if (rva >= (uint)s.VirtualAddress && rva < (long)(uint)s.VirtualAddress + (uint)s.SizeOfRawData)
                    return (uint)s.PointerToRawData + rva - (uint)s.VirtualAddress;
            throw new Malformed();
        }

        public void Read(long at, Span<byte> into)
        {
            if (at < 0 || at > Length - into.Length) throw new Malformed();
            stream.Position = at;
            stream.ReadExactly(into);
        }

        public ushort U16(long at)
        {
            Span<byte> b = stackalloc byte[2];
            Read(at, b);
            return BinaryPrimitives.ReadUInt16LittleEndian(b);
        }

        public uint U32(long at)
        {
            Span<byte> b = stackalloc byte[4];
            Read(at, b);
            return BinaryPrimitives.ReadUInt32LittleEndian(b);
        }
    }
}
