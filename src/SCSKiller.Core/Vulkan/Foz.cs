using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace SCSKiller.Core.Vulkan;

/// <summary>Fossilize's archive format (.foz, Fossilize's fossilize_db.cpp), the one Steam's pipeline cache and SCSKiller's
/// Vulkan layer write: a 16-byte header (magic + version byte), then entries of a 56-byte header and a payload. The header
/// starts with the entry's name, 40 hex digits (24 of tag, 16 of hash), then u32 stored size, u32 format (1 raw, 2 zlib),
/// u32 crc and u32 unpacked size. An entry name is unique per tag: a repeat is skipped, as Fossilize does.</summary>
public static class Foz
{
    public readonly record struct Entry(int Tag, ulong Hash, long Offset, long Length)
    {
        public string Name => Foz.Name(Tag, Hash);
    }

    // Fossilize's resource tags
    public const int ApplicationInfo = 0, ShaderModule = 4, Graphics = 6, Compute = 7, BlobLink = 8, RayTracing = 9, BucketInfo = 10;
    public const int TagCount = 11;

    public const int Version = 6, MinVersion = 5;
    const int NameLength = 40, HeadLength = 56;

    public const int MaxPayload = 64 << 20;
    public const int MaxEntries = 4_000_000;
    public const long MaxInbox = 4L << 30;   // the largest recording an import takes

    static readonly byte[] Magic = [0x81, .. "FOSSILIZEDB"u8, 0, 0, 0];

    public static bool IsPipeline(int tag) => tag is Graphics or Compute or RayTracing;

    public static string Name(int tag, ulong hash) => $"{tag:x24}{hash:x16}";

    public static List<Entry> List(Stream f) => List(f, out _);

    /// <summary>Every entry in file order; <paramref name="end"/>: where the last whole entry ends (a torn tail is left out).</summary>
    public static List<Entry> List(Stream f, out long end)
    {
        var list = new List<Entry>();
        end = Walk(f, e => { list.Add(e); return true; });
        return list;
    }

    public static Entry? First(Stream f, Func<Entry, bool> match)
    {
        Entry? found = null;
        Walk(f, e =>
        {
            if (!match(e)) return true;
            found = e;
            return false;
        });
        return found;
    }

    /// <summary>Calls <paramref name="each"/> on each entry until it returns false; returns where the walk stopped.</summary>
    static long Walk(Stream f, Func<Entry, bool> each)
    {
        var head = new byte[HeadLength];
        f.Position = 0;
        if (f.ReadAtLeast(head.AsSpan(0, Magic.Length + 1), Magic.Length + 1, false) < Magic.Length + 1
            || !head.AsSpan(0, Magic.Length).SequenceEqual(Magic) || head[Magic.Length] is < MinVersion or > Version)
            throw new InvalidDataException("not a Fossilize v5 or v6 archive");
        var seen = new HashSet<(int, ulong)>();
        long at = Magic.Length + 1, length = f.Length;
        while (at + HeadLength <= length)
        {
            f.Position = at;
            f.ReadExactly(head);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(NameLength));
            if (size > MaxPayload) throw new InvalidDataException($"an entry of {size} bytes at byte {at}");
            if (at + HeadLength + size > length) break;   // torn tail
            if (!ulong.TryParse(Encoding.ASCII.GetString(head, 0, 24), NumberStyles.HexNumber, null, out var tag)
                || !ulong.TryParse(Encoding.ASCII.GetString(head, 24, 16), NumberStyles.HexNumber, null, out var hash))
                throw new InvalidDataException($"an entry name that isn't hex at byte {at}");
            var e = new Entry((int)tag, hash, at, HeadLength + size);
            at += HeadLength + size;
            if (tag >= TagCount || !seen.Add(((int)tag, hash))) continue;
            if (seen.Count > MaxEntries) throw new InvalidDataException($"over {MaxEntries:N0} entries");
            if (!each(e)) break;
        }
        return at;
    }

    public static List<Entry> List(string path)
    {
        using var f = OpenShared(path);
        return List(f);
    }

    /// <summary>Read access that lets the writer (a running game's layer) keep writing, renaming or deleting it.</summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>The entry's payload, unpacked.</summary>
    public static byte[] Payload(Stream f, Entry e)
    {
        var head = new byte[HeadLength];
        f.Position = e.Offset;
        f.ReadExactly(head);
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(NameLength));
        var format = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(NameLength + 4));
        var unpacked = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(NameLength + 12));
        if (stored > MaxPayload || unpacked > MaxPayload) throw new InvalidDataException($"{e.Name}: a payload over {MaxPayload >> 20} MB");
        var payload = new byte[stored];
        f.ReadExactly(payload);
        switch (format)
        {
            case 1:
                return payload;
            case 2:
                var bytes = new byte[unpacked];
                using (var z = new ZLibStream(new MemoryStream(payload), CompressionMode.Decompress))
                    if (z.ReadAtLeast(bytes, bytes.Length, false) != bytes.Length || z.ReadByte() >= 0)
                        throw new InvalidDataException($"{e.Name}: unpacks to another size than its header's");
                return bytes;
            default:
                throw new InvalidDataException($"{e.Name}: unknown format {format}");
        }
    }

    /// <summary>Writes an entry, zlib-packed (no crc, as Fossilize writes with checksums off).</summary>
    public static void Append(Stream f, int tag, ulong hash, byte[] payload)
    {
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(payload);
        var head = new byte[HeadLength];
        Encoding.ASCII.GetBytes(Name(tag, hash), head);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(NameLength), (uint)packed.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(NameLength + 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(NameLength + 12), (uint)payload.Length);
        f.Write(head);
        f.Write(packed.GetBuffer(), 0, (int)packed.Length);
    }

    /// <summary>Writes an entry with an empty payload: the form of Fossilize's lists of validated or failed names.</summary>
    public static void AppendName(Stream f, int tag, ulong hash)
    {
        var head = new byte[HeadLength];
        Encoding.ASCII.GetBytes(Name(tag, hash), head);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(NameLength + 4), 1);
        f.Write(head);
    }

    public static void WriteMagic(Stream f)
    {
        f.Write(Magic);
        f.WriteByte(Version);
    }

    /// <summary>Copies the entry as it is stored, header included.</summary>
    public static void Copy(Stream from, Entry e, Stream to)
    {
        from.Position = e.Offset;
        var buffer = new byte[(int)Math.Min(e.Length, 1 << 20)];
        for (var left = e.Length; left > 0;)
        {
            var n = (int)Math.Min(left, buffer.Length);
            from.ReadExactly(buffer, 0, n);
            to.Write(buffer, 0, n);
            left -= n;
        }
    }

    /// <summary>Appends the entries of <paramref name="from"/> that <paramref name="store"/> lacks (created when missing; a
    /// torn tail of it is cut first). Returns the entries added.</summary>
    public static List<Entry> Merge(string store, Stream from)
    {
        var incoming = List(from);
        using var f = new FileStream(store, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        List<Entry> stored = [];
        long end;
        if (f.Length == 0)
        {
            WriteMagic(f);
            end = f.Position;
        }
        else stored = List(f, out end);
        var have = stored.Select(e => (e.Tag, e.Hash)).ToHashSet();
        var added = incoming.Where(e => !have.Contains((e.Tag, e.Hash))).ToList();
        if (added.Count == 0) return [];
        f.SetLength(end);
        f.Position = end;
        var w = new BufferedStream(f, 1 << 20);
        foreach (var e in added) Copy(from, e, w);
        w.Flush();
        return added;
    }
}
