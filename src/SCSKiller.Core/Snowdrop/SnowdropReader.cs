using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Snowdrop;

/// <summary>Massive's Snowdrop engine on PC (Star Wars Outlaws, D3D12), by its data files: {game}\sdf\pc\data\sdf.sdftoc
/// (a "WEST" TOC, version 41 or 42) and the sdf-?-NNNN[-xx-yy].sdfdata files beside it ("BERG"). A data file is a stream
/// of Oodle pages (64 KB each unpacked, <see cref="PageLength"/>) and raw runs; a shader asset is an 8-byte header
/// (<see cref="Prefix"/>: u32 0xDABC02E3, u32 1) then its DXBC/DXIL container, in one page, over several (a page short of
/// 64 KB is stored raw), or raw. No TOC entry names the assets: each file is walked page by page (<see cref="Walk"/>),
/// resyncing past what isn't an asset. A file found to hold none is remembered (sdf-walk.txt, by name, size and the TOC's
/// stamp for it) and not walked again. No root signatures ship (the engine builds them at run time, <see cref="SnowdropRootSig"/>);
/// every shader is in one pool. EngineInfo: Family "Snowdrop", Version "SDF", Fork the game's folder.</summary>
public sealed class SnowdropReader(string dataDir) : IEngineReader
{
    public const string Family = "Snowdrop", Version = "SDF";
    const string Toc = @"sdf\pc\data\sdf.sdftoc";
    const string WalkFile = "sdf-walk.txt", WalkHeader = "snowdrop-walk 2";
    const int Page = 64 << 10;
    const int MaxSlots = 64 << 10, MaxSettings = 4096;
    const int MaxWalkRecord = 16 << 20, MaxAsset = 8 << 20;
    /// <summary>The read window: holds the largest asset with its pages' headers.</summary>
    const int WindowSize = 9 << 20;
    /// <summary>Resyncs on page-like bytes per file before the rest of it is left out (a file of noise can't take hours).</summary>
    const int MaxResyncs = 1 << 18;

    static readonly Regex DataName = new(@"^sdf-[a-z]-(\d{4})(-[a-z]{2}-[a-z]{2})?\.sdfdata$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A shader asset's first bytes: its 8-byte header, then "DXBC".</summary>
    static ReadOnlySpan<byte> Prefix => [0xE3, 0x02, 0xBC, 0xDA, 0x01, 0x00, 0x00, 0x00, (byte)'D', (byte)'X', (byte)'B', (byte)'C'];

    public sealed record Loc(string File, long Offset);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index (in memory only: ReadShaders
    /// re-indexes when it runs without an Index in the same process).</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    public int ReaderVersion(EngineInfo engine) => 2;

    /// <summary>The game's sdf.sdftoc, in a folder of the install; null if none.</summary>
    public static string? Find(Game game)
    {
        if (!Directory.Exists(game.InstallDir)) return null;
        try { return Directory.EnumerateDirectories(game.InstallDir).Order(StringComparer.Ordinal).Select(d => GameFiles.Find(d, Toc)).FirstOrDefault(File.Exists); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public EngineInfo? Detect(Game game)
    {
        if (Find(game) is not { } toc) return null;
        var slots = ReadHeader(toc, out var why);
        var folder = Path.GetFileName(Path.GetFullPath(Path.Combine(toc, "..", "..", "..", "..")));
        return new EngineInfo(Family, Version, folder, "D3D12", false, slots == null ? why : null);
    }

    /// <summary>The TOC's stamp for each data file slot (its last column), or null and <paramref name="why"/> when the TOC
    /// isn't one this reads. Header: "WEST", u32 version at 4, slots at 20, settings at 28, "massive\0" at 48; after 98
    /// bytes, 320 more when byte 96 is set, 8 per setting and 60, then the slot table, by column (60 bytes per slot).</summary>
    internal static ulong[]? ReadHeader(string toc, out string? why)
    {
        FileStream f;
        try { f = new FileStream(toc, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1); }
        catch (UnauthorizedAccessException e) { throw new IOException(e.Message, e); }
        using (f)
        {
            Span<byte> h = stackalloc byte[98];
            if (f.ReadAtLeast(h, h.Length, throwOnEndOfStream: false) < h.Length || !h.StartsWith("WEST"u8))
            {
                why = "sdf.sdftoc isn't a Snowdrop TOC";
                return null;
            }
            uint version = U(h[4..]), slots = U(h[20..]), settings = U(h[28..]);
            if (version is not (41 or 42))
            {
                why = $"sdf.sdftoc version {version} isn't read";
                return null;
            }
            if (slots is 0 or > MaxSlots || settings > MaxSettings || !h[48..].StartsWith("massive\0"u8))
            {
                why = "sdf.sdftoc's header doesn't read";
                return null;
            }
            var skip = (h[96] != 0 ? 320 : 0) + 8L * settings + 60;
            var table = new byte[60 * slots];
            if (f.Length < f.Position + skip + table.Length)
            {
                why = "sdf.sdftoc is shorter than its header says";
                return null;
            }
            f.Position += skip;
            f.ReadExactly(table);
            var stamps = new ulong[slots];
            for (var i = 0; i < slots; i++) stamps[i] = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan((int)(52 * slots) + 8 * i));
            why = null;
            return stamps;
        }
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => Index(game, engine, log, null, ct);

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var toc = Find(game) ?? throw new FileNotFoundException($@"{game.InstallDir}: no *\{Toc}");
        var stamps = ReadHeader(toc, out var why) ?? throw new InvalidDataException(why);
        Codecs.Load(zlib: false);
        if (OodleHelper.Instance == null) throw new InvalidDataException(Codecs.NativeOodle ? Codecs.NoOodleDll : Codecs.NoNativeOodle);
        var dir = Path.GetDirectoryName(toc)!;
        // each data file by name, size and the TOC's stamp for its slot: a patched file walks again
        var files = new List<(string Path, string Key)>();
        foreach (var path in GameFiles.EnumerateFiles(dir, "*.sdfdata"))
        {
            if (DataName.Match(Path.GetFileName(path)) is not { Success: true } m) continue;
            var slot = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            long length;
            try { length = new FileInfo(path).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            files.Add((path, FormattableString.Invariant($"{Path.GetFileName(path)}\t{length}\t{(slot < stamps.Length ? stamps[slot] : 0):x16}")));
        }
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
        var walkPath = WalkPath(game);
        var known = ReadWalk(walkPath);
        var toWalk = files.Where(f => known.GetValueOrDefault(f.Key, -1) != 0).ToList();
        var locs = new Dictionary<string, Loc>();
        var shaders = new Dictionary<string, ShaderInfo>();
        var pooled = new List<string>();
        var withShaders = 0;
        var walked = new Dictionary<string, int>();
        var content = new StringBuilder();
        var counts = new Counts();
        var page = new byte[Page];
        var buf = new byte[WindowSize];
        long bytes = 0;
        for (var i = 0; i < toWalk.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((i, toWalk.Count));
            var (path, key) = toWalk[i];
            var pool = new List<string>();
            bool whole;
            try
            {
                using var s = Open(path);
                bytes += s.Length;
                whole = Walk(new Window(s, buf), page, counts, (at, c) =>
                {
                    var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                    pool.Add(sha);
                    if (!locs.TryAdd(sha, new Loc(path, at)) || Dxbc.Kind(c) < 0) return;
                    try
                    {
                        if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info) shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                    }
                    catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { counts.Unparseable++; }
                }, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                counts.Unreadable++;
                counts.FirstUnreadable ??= $"{Path.GetFileName(path)}: {e.Message}";
                continue;
            }
            if (whole) walked[key] = pool.Count;   // a file cut short walks again next time
            if (pool.Count == 0) continue;
            content.Append(key).Append('\n');
            var before = pooled.Count;
            pooled.AddRange(pool.Where(shaders.ContainsKey));
            if (pooled.Count > before) withShaders++;
        }
        progress?.Report((toWalk.Count, toWalk.Count));
        foreach (var (_, key) in files)
            if (!walked.ContainsKey(key) && known.GetValueOrDefault(key, -1) == 0) walked[key] = 0;
        log?.Report($"{files.Count} data files, {toWalk.Count} walked ({bytes >> 20} MB), {withShaders} with shaders -> {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"), {shaders.Values.Count(s => s.ShaderModel.Contains("_6_", StringComparison.Ordinal))} DXIL, no root signatures (built at run time)"
            + counts.Warnings + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        if (shaders.Count == 0) throw new InvalidDataException("no shader in " + dir + counts.Warnings);
        WriteWalk(walkPath, walked);
        located[game.Id] = locs;
        var hash = CarvedReader.Sha1Hex(content.ToString());
        return new ShaderIndex(hash, ["D3D12"], shaders, [new ShaderMap(hash, "sdf", "D3D12", [.. pooled.Distinct()])]);
    }

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            try { Index(game, engine, null, ct); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return; }
            locs = located[game.Id];
        }
        var page = new byte[Page];
        var buf = new byte[WindowSize];
        foreach (var inFile in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.File))
            try
            {
                using var s = Open(inFile.Key);
                var w = new Window(s, buf);
                foreach (var (sha, at) in inFile.OrderBy(x => x.At.Offset))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Asset(w, at.Offset, page, out _) is { } c && Convert.ToHexStringLower(SHA1.HashData(c)) == sha) sink(sha, c);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

    string WalkPath(Game game) => Path.Combine(new AppStore(dataDir).GameDir(game.Id), WalkFile);

    /// <summary>The shaders found in each data file walked whole before (by its key); empty when unreadable or another version.</summary>
    static Dictionary<string, int> ReadWalk(string path)
    {
        var walked = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxWalkRecord) return walked;
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0] != WalkHeader) return walked;
            foreach (var line in lines.Skip(1))
                if (line.LastIndexOf('\t') is var tab and > 0 && int.TryParse(line.AsSpan(tab + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    walked[line[..tab]] = n;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { walked.Clear(); }
        return walked;
    }

    static void WriteWalk(string path, Dictionary<string, int> walked)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path + ".tmp", [WalkHeader, .. walked.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}\t{k.Value}")]);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Walks a data file: at each position a page that starts an asset, a raw asset, or a page skipped whole; else
    /// resyncs to the next asset prefix or a page whose bytes decode (<see cref="Resumes"/>). False when the file was cut
    /// short (<see cref="MaxResyncs"/>): its shaders so far are found, but it isn't remembered.</summary>
    internal static bool Walk(Window w, byte[] page, Counts counts, Action<long, byte[]> found, CancellationToken ct)
    {
        if (!w.At(0, 4).StartsWith("BERG"u8))
        {
            counts.NotData++;
            return true;
        }
        long p = 8;
        var resyncs = 0;
        var afterRaw = false;   // after a raw asset, page-like bytes must decode to count as a page
        while (p < w.Length)
        {
            ct.ThrowIfCancellationRequested();
            var len = PageLength(w.At(p, 8));
            if (len > 0 && afterRaw && !Resumes(w, p, len, page)) len = 0;
            afterRaw = false;
            var head = w.At(p, 24);
            if (len > 0 && !Starts(head, len))
            {
                p += len;
                continue;
            }
            if (len > 0 || head.StartsWith(Prefix))
            {
                if (Asset(w, p, page, out var next) is { } c)
                {
                    found(p, c);
                    afterRaw = len == 0;
                    p = next;
                    continue;
                }
                counts.Broken++;
                if (len > 0)
                {
                    p += len;
                    continue;
                }
            }
            for (p++; ; p++)
            {
                ct.ThrowIfCancellationRequested();
                var span = w.At(p, 1 << 20);
                if (span.Length < 8) return true;
                var i = span.IndexOfAny((byte)0x8C, (byte)0xE3);   // a page header's or the prefix's first byte
                if (i < 0)
                {
                    p += span.Length - 7;
                    continue;
                }
                p += i;
                if (w.At(p, 12).StartsWith(Prefix)) break;
                if (PageLength(w.At(p, 8)) is var l and > 0)
                {
                    if (++resyncs > MaxResyncs)
                    {
                        counts.Cut++;
                        return false;
                    }
                    if (Resumes(w, p, l, page)) break;
                }
            }
        }
        return true;
    }

    /// <summary>Whether page-like bytes at <paramref name="p"/> are a page: they decode to a whole page or an asset's start,
    /// or another page header follows them (or the file ends).</summary>
    static bool Resumes(Window w, long p, int len, byte[] page)
    {
        page.AsSpan(0, Prefix.Length).Clear();
        if (Decode(w.At(p, len), page) == Page || page.AsSpan().StartsWith(Prefix)) return true;
        return p + len == w.Length || PageLength(w.At(p + len, 8)) > 0;
    }

    /// <summary>The container of the asset at <paramref name="p"/> (raw or paged) and where the stream goes on; null if it
    /// isn't one, or isn't a valid container.</summary>
    internal static byte[]? Asset(Window w, long p, byte[] page, out long next)
    {
        next = p;
        var head = w.At(p, 40);
        var len = PageLength(head);
        if (len == 0)
        {
            if (head.Length < 40 || !head.StartsWith(Prefix)) return null;
            var rawSize = U(head[32..]) + 8L;
            if (rawSize is > MaxAsset or < 40) return null;
            var raw = w.At(p, (int)rawSize);
            if (raw.Length < rawSize) return null;
            next = p + rawSize;
            return Valid(raw[8..].ToArray());
        }
        var src = w.At(p, len);
        if (src.Length < len) return null;
        page.AsSpan(0, 40).Clear();
        var decoded = Decode(src, page);
        if (!page.AsSpan().StartsWith(Prefix)) return null;
        var size = U(page.AsSpan(32)) + 8L;
        if (size is > MaxAsset or < 40) return null;
        if (size <= Page)
        {
            if (Decode(src, page.AsSpan(0, (int)size)) != size) return null;
            next = p + len;
            return Valid(page[8..(int)size]);
        }
        if (decoded != Page) return null;
        var asset = new byte[size];
        page.CopyTo(asset, 0);
        var q = p + len;
        for (var i = Page; i < size;)
        {
            var pageLen = PageLength(w.At(q, 8));
            var want = (int)Math.Min(Page, size - i);
            if (pageLen > 0 && Decode(w.At(q, pageLen), asset.AsSpan(i, want)) == want) q += pageLen;
            else if (w.At(q, want) is var stored && stored.Length == want)   // a page that didn't pack is stored raw
            {
                stored.CopyTo(asset.AsSpan(i));
                q += want;
            }
            else return null;
            i += want;
        }
        next = q;
        return Valid(asset[8..]);
    }

    static byte[]? Valid(byte[] c) => Dxbc.Valid(c) ? c : null;

    /// <summary>The bytes of the Oodle block at <paramref name="h"/> (Kraken, Mermaid or Leviathan; byte 1's high bit adds
    /// a 3-byte field), header included; 0 if it isn't one.</summary>
    internal static int PageLength(ReadOnlySpan<byte> h)
    {
        if (h.Length < 5 || h[0] != 0x8C || (h[1] & 0x7F) is not (6 or 10 or 12)) return 0;
        var packed = ((h[2] << 16) & 0x30000) | (h[3] << 8) | h[4];
        return packed == 0x3FFFF ? 0 : 5 + (h[1] >> 7) * 3 + packed + 1;
    }

    /// <summary>Whether a page's first literals are an asset's header.</summary>
    static bool Starts(ReadOnlySpan<byte> h, int len)
    {
        var at = 5 + (h[1] >> 7) * 3;
        return h.Length >= at + 11 && (h[(at + 3)..].StartsWith(Prefix[..8]) || h[at..].StartsWith(Prefix[..8]));
    }

    static int Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        try { return (int)OodleHelper.Instance!.Decompress(src, dst); }
        catch (Exception e) when (e is not OutOfMemoryException) { return 0; }
    }

    static uint U(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32LittleEndian(b);

    internal sealed class Counts
    {
        public int Unparseable, Broken, Cut, Unreadable, NotData;
        public string? FirstUnreadable;

        public string Warnings =>
            (Unparseable > 0 ? $", {Unparseable} unparseable" : "")
            + (Broken > 0 ? $"; warning: {Broken} shader assets don't decode or aren't valid containers" : "")
            + (Cut > 0 ? $"; warning: {Cut} data files have too much unpaged data, the rest of each left out" : "")
            + (NotData > 0 ? $"; {NotData} files aren't data files" : "")
            + (Unreadable > 0 ? $"; warning: {Unreadable} data files don't read ({FirstUnreadable})" : "");
    }

    /// <summary>A forward read window over a data file: At reads ahead in place, re-reads from the stream only on a jump
    /// back or past what it holds.</summary>
    internal sealed class Window(Stream s, byte[] buf)
    {
        long start;
        int len;

        public long Length { get; } = s.Length;

        /// <summary>Up to <paramref name="n"/> bytes at <paramref name="pos"/> (fewer at the file's end, at most the window).</summary>
        public ReadOnlySpan<byte> At(long pos, int n)
        {
            if (pos < 0 || pos >= Length) return default;
            n = Math.Min(n, buf.Length);
            if (pos < start || pos > start + len)
            {
                s.Position = pos;
                (start, len) = (pos, 0);
            }
            else if (pos + n > start + len)
            {
                var keep = (int)(start + len - pos);
                Buffer.BlockCopy(buf, (int)(pos - start), buf, 0, keep);
                (start, len) = (pos, keep);
            }
            while (pos + n > start + len && len < buf.Length && s.Read(buf, len, buf.Length - len) is var r and > 0) len += r;
            var at = (int)(pos - start);
            return buf.AsSpan(at, Math.Min(n, len - at));
        }
    }
}
