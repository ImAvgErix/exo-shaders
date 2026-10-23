using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.BlackSpace;

/// <summary>Pearl Abyss's BlackSpace Engine on PC (Crimson Desert, D3D12), by its archives (meta\0.papgt, NNNN\0.pamt
/// indexes over N.paz, <see cref="Pamt"/>). One archive group holds the D3D12 shader cache (<see cref="ShaderDir"/>):
/// <list type="bullet">
/// <item>*.padxil: a 36-byte "PASC" header (u32 at 16: where the container starts) then a DXIL container. Named
/// "{pass:x8}_…": the lookup3 hash (<see cref="PazCrypto.Hash(string, uint)"/>) of the render pass's name.</item>
/// <item>*.pars: each pass's root signature (an RTS0-only container), named by the pass in lower case, encrypted: they
/// open only with the user's key (<see cref="BlackSpaceKeys"/>).</item>
/// </list>
/// A pass's hash is tied to its name by the names in the game's exe (<see cref="PassNames"/>) or the planning rules
/// (PassNames), so its shaders take its root signature. Maps: one pool per pass (its graphics shaders), and each compute
/// shader one IsPipeline map. EngineInfo: Family "BlackSpace", Version "padxil" (+"+RTS0" with a key that opens them).</summary>
public sealed class BlackSpaceReader(string dataDir) : IEngineReader
{
    public const string Family = "BlackSpace", Format = "padxil";
    public const string ShaderDir = "shadercache__/d3d12/windows";
    /// <summary>Goes up when Index reads the same files otherwise (it is in the content hash).</summary>
    const int IndexVersion = 1;
    const int HeaderSize = 36;

    public sealed record Loc(string Paz, Pamt.Entry Entry, byte KeyByte);

    /// <summary>Where each container is and the root signatures, per game id, from the last Index (in memory only:
    /// ReadShaders re-indexes when it runs without an Index in the same process).</summary>
    readonly ConcurrentDictionary<string, (IReadOnlyDictionary<string, Loc> Shaders, IReadOnlyDictionary<string, byte[]> Roots)> located = new();
    readonly BlackSpaceKeys keys = new(dataDir);

    /// <summary>A BlackSpace game whose root signatures don't open yet: it plans with the user's key.</summary>
    public static bool NeedsKey(EngineInfo e) => e is { Family: Family, Version: Format };

    public int ReaderVersion(EngineInfo engine) => 2;

    /// <summary>Stores the user's key for the game when it opens one of its root signatures.</summary>
    public bool SetKey(Game game, string key) => keys.Set(game, key, k => Opens(game, k));

    bool Opens(Game game, byte[] key)
    {
        if (Root(game) is not { } root || ShaderPamt(root) is not { } path) return false;
        Pamt pamt;
        try { pamt = Pamt.Read(path); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
        return pamt.Files.Where(IsKeyedPars).Any(f => RootSignature(Path.GetDirectoryName(path)!, f, key, pamt.KeyByte) != null);
    }

    /// <summary>The folder holding meta\0.papgt: the install, or two up from the exe (bin64\CrimsonDesert.exe).</summary>
    static string? Root(Game game) =>
        new[] { game.InstallDir, Path.GetDirectoryName(Path.GetDirectoryName(game.ExePath)) }
            .FirstOrDefault(d => d != null && File.Exists(Path.Combine(d, "meta", "0.papgt")));

    /// <summary>The index (NNNN\0.pamt) of the group that holds <see cref="ShaderDir"/>; null if none.</summary>
    static string? ShaderPamt(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true }).Order(StringComparer.Ordinal))
        {
            var pamt = Path.Combine(dir, "0.pamt");
            if (Path.GetFileName(dir) is not { Length: 4 } name || !name.All(char.IsAsciiDigit) || !File.Exists(pamt)) continue;
            try
            {
                if (Pamt.FolderNames(pamt).Contains(ShaderDir)) return pamt;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public string DetectStamp(Game game, EngineInfo? engine) => Root(game) != null ? "paz:" + keys.KeyStamp(game) : "";

    /// <summary>Reads the indexes' folder tries; with a stored key, opens .pars entries until one is a root signature.</summary>
    public EngineInfo? Detect(Game game)
    {
        if (Root(game) is not { } root) return null;
        if (ShaderPamt(root) is not { } path) return new EngineInfo(Family, "-", null, "D3D12", false, "no D3D12 shader cache in its archives");
        Pamt pamt;
        try { pamt = Pamt.Read(path); }
        catch (InvalidDataException e) { return new EngineInfo(Family, "-", null, "D3D12", false, $"its shader archive doesn't open ({e.Message})"); }
        var keyed = keys.Stored(game) is { } key
            && pamt.Files.Where(IsKeyedPars).Any(f => RootSignature(Path.GetDirectoryName(path)!, f, key, pamt.KeyByte) != null);
        return new EngineInfo(Family, Format + (keyed ? CarvedReader.EmbeddedRootSignatures : ""), null, "D3D12", false, null);
    }

    static bool IsPars(string p) => p.StartsWith(ShaderDir + "/", StringComparison.Ordinal) && p.EndsWith(".pars", StringComparison.Ordinal);
    static bool IsKeyedPars(Pamt.Entry e) => IsPars(e.Path) && e.Encryption == 3;
    static bool IsShader(string p) => p.StartsWith(ShaderDir + "/", StringComparison.Ordinal) && p.EndsWith(".padxil", StringComparison.Ordinal);

    static string PazOf(string group, Pamt.Entry e) => Path.Combine(group, $"{e.Paz}.paz");
    static SafeFileHandle OpenPaz(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>The entry's bytes when they are a valid RTS0-only container; null if not (a wrong key reads as noise).</summary>
    static byte[]? RootSignature(string group, Pamt.Entry e, byte[] keyBase, byte keyByte)
    {
        try
        {
            using var paz = OpenPaz(PazOf(group, e));
            return Pamt.Open(paz, e, keyBase, keyByte, out _) is { } b && Dxbc.IsRootSignatureOnly(b) && Dxbc.RootSignatureValid(b) ? b : null;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The DXIL container a .padxil holds, when it is exactly one valid container; null if not.</summary>
    internal static byte[]? Container(byte[] padxil)
    {
        if (padxil.Length < HeaderSize || !padxil.AsSpan(0, 4).SequenceEqual("PASC"u8)) return null;
        var at = BinaryPrimitives.ReadUInt32LittleEndian(padxil.AsSpan(16));
        if (at < HeaderSize || at >= padxil.Length) return null;
        var c = padxil[(int)at..];
        return Dxbc.HeaderSize(c) == c.Length && Dxbc.Valid(c) ? c : null;
    }

    /// <summary>The pass hash a shader's file name starts with ("{pass:x8}_…"); null if none.</summary>
    internal static uint? PassOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.IndexOf('_') == 8 && uint.TryParse(name.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, null, out var pass) ? pass : null;
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => Index(game, engine, log, null, ct);

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var root = Root(game) ?? throw new InvalidDataException("not a BlackSpace install: scan the game again");
        var path = ShaderPamt(root) ?? throw new InvalidDataException("no D3D12 shader cache in its archives");
        var group = Path.GetDirectoryName(path)!;
        var indexBytes = Pamt.Bytes(path);
        var pamt = Pamt.Parse(indexBytes);
        var key = keys.Stored(game);
        var entries = pamt.Files.Where(f => IsShader(f.Path) || IsPars(f.Path)).OrderBy(f => f.Paz).ThenBy(f => f.Offset).ToList();
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var passes = new Dictionary<uint, HashSet<string>>();
        var passRoots = new Dictionary<string, string>();   // a .pars name (its pass, lower case) -> its root signature's SHA-1
        var lanes = new Dictionary<string, string>();
        var roots = new Dictionary<string, byte[]>();
        var skipped = new Dictionary<string, int>();
        int containers = 0, unparseable = 0, notRoots = 0, done = 0;
        var handles = new Dictionary<int, SafeFileHandle>();
        try
        {
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((++done, entries.Count));
                if (!handles.TryGetValue(e.Paz, out var paz)) paz = handles[e.Paz] = OpenPaz(PazOf(group, e));
                if (IsPars(e.Path))
                {
                    if (key == null) continue;
                    var why = e.Encryption == 0 ? "a root signature not encrypted"
                        : e.Encryption != 3 ? $"encryption {e.Encryption}"
                        : e.Compression is not (0 or 2) ? $"compression {e.Compression}"
                        : null;
                    if (why != null) { skipped[why] = skipped.GetValueOrDefault(why) + 1; continue; }
                    if (Pamt.Open(paz, e, key, pamt.KeyByte, out _) is not { } rs || !Dxbc.IsRootSignatureOnly(rs) || !Dxbc.RootSignatureValid(rs))
                    {
                        notRoots++;
                        continue;
                    }
                    var rsSha = Convert.ToHexStringLower(SHA1.HashData(rs));
                    roots[rsSha] = rs;
                    passRoots[Path.GetFileNameWithoutExtension(e.Path)] = rsSha;
                    continue;
                }
                if (Pamt.Open(paz, e, key, pamt.KeyByte, out var unread) is not { } padxil)
                {
                    skipped[unread!] = skipped.GetValueOrDefault(unread!) + 1;
                    continue;
                }
                if (Container(padxil) is not { } c) { unparseable++; continue; }
                containers++;
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                if (locs.TryAdd(sha, new Loc(PazOf(group, e), e, pamt.KeyByte)))
                    try
                    {
                        if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info)
                        {
                            shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                            if (info.Stage == Stage.Compute && CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) is { } platform) lanes[sha] = platform;
                        }
                    }
                    catch (Exception x) when (x is ArgumentException or IndexOutOfRangeException) { unparseable++; }
                if (!shaders.ContainsKey(sha)) continue;
                var pass = PassOf(e.Path) ?? 0;
                if (!passes.TryGetValue(pass, out var inPass)) passes[pass] = inPass = [];
                inPass.Add(sha);
            }
        }
        finally
        {
            foreach (var h in handles.Values) h.Dispose();
        }
        if (shaders.Count == 0) throw new InvalidDataException("no shader in " + GameFiles.Rel(root, path));

        var names = PassNames(game.ExePath, passRoots.Keys.ToHashSet(), passes.Keys.ToHashSet());
        var namesLine = string.Join(' ', names.OrderBy(n => n.Key).Select(n => $"{n.Key:x8}={n.Value}"));
        var byRules = RulesNames(RulesFile.Current.PassNamesFor(engine)?.PassNames, names, passRoots.Keys.ToHashSet(), passes.Keys.ToHashSet());
        string? RootOf(uint pass) => names.TryGetValue(pass, out var name) ? passRoots.GetValueOrDefault(name) : null;

        // a shader drawn in passes of different root signatures (or one without) carries none
        var shaderRoots = new Dictionary<string, HashSet<string?>>();
        foreach (var (pass, inPass) in passes)
            foreach (var sha in inPass)
            {
                if (!shaderRoots.TryGetValue(sha, out var rs)) shaderRoots[sha] = rs = [];
                rs.Add(RootOf(pass));
            }
        foreach (var (sha, rs) in shaderRoots)
            if (rs.Count == 1 && rs.Single() is { } only) shaders[sha] = shaders[sha] with { RootSignature = only };

        var maps = Maps(passes, shaders, p => names.GetValueOrDefault(p) ?? p.ToString("x8"), RootOf, h => lanes.GetValueOrDefault(h, "D3D12"));
        located[game.Id] = (locs, roots);
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        content.AppendData(Encoding.UTF8.GetBytes($"{Family} {IndexVersion}\n"));
        content.AppendData(indexBytes);
        content.AppendData(Encoding.UTF8.GetBytes(string.Join(' ', passRoots.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + "\n"));
        content.AppendData(Encoding.UTF8.GetBytes(namesLine + "\n"));
        var withRoot = shaders.Values.Count(s => s.RootSignature != null);
        log?.Report($"{GameFiles.Rel(root, path)}: {containers} shader containers, {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"), {shaders.Values.Count(s => s.InlineRayTracing)} tracing rays inline, {passes.Count} render passes, {names.Count} named{(byRules > 0 ? $" ({byRules} by the rules)" : "")}"
            + (key == null ? "; root signatures: none, no key for its .pars files"
                : $"; {roots.Count} root signatures, {withRoot} shaders with theirs"
                  + (notRoots > 0 ? $"; warning: {notRoots} .pars files aren't root signatures (a wrong key?), their passes have none" : ""))
            + string.Concat(skipped.Select(s => $"; warning: {s.Value} entries skipped ({s.Key})"))
            + (unparseable > 0 ? $"; {unparseable} unparseable" : "")
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), ["D3D12", .. lanes.Values.Distinct().Order(StringComparer.Ordinal)],
            shaders, maps, roots);
    }

    /// <summary>Per pass (by hash): its graphics shaders as one pool, and each compute shader as one IsPipeline map (once per
    /// root signature), all with the pass's root signature.</summary>
    internal static List<ShaderMap> Maps(Dictionary<uint, HashSet<string>> passes, IReadOnlyDictionary<string, ShaderInfo> shaders, Func<uint, string> name,
        Func<uint, string?> rootOf, Func<string, string>? platform = null)
    {
        var maps = new List<ShaderMap>();
        var computes = new HashSet<(string, string?)>();
        foreach (var (pass, inPass) in passes.OrderBy(p => p.Key))
        {
            var library = name(pass);
            var graphics = inPass.Where(h => shaders[h].Stage != Stage.Compute).Order(StringComparer.Ordinal).ToList();
            if (graphics.Count > 0) maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{Family}|{pass:x8}"), library, "D3D12", graphics, false, rootOf(pass)));
            foreach (var cs in inPass.Where(h => shaders[h].Stage == Stage.Compute).Order(StringComparer.Ordinal))
                if (computes.Add((cs, rootOf(pass))))
                    maps.Add(new ShaderMap(CarvedReader.Sha1Hex(cs + "|" + rootOf(pass)), library, platform?.Invoke(cs) ?? "D3D12", [cs], true, rootOf(pass)));
        }
        return maps;
    }

    /// <summary>Adds the planning rules' pass names (hash in hex -> name) that the exe didn't give: only one whose hash
    /// matches and that names a .pars file. Returns how many it added.</summary>
    internal static int RulesNames(IReadOnlyDictionary<string, string>? table, Dictionary<uint, string> found, IReadOnlySet<string> lower, IReadOnlySet<uint> wanted)
    {
        var added = 0;
        foreach (var (hex, name) in table ?? new Dictionary<string, string>())
            if (uint.TryParse(hex, NumberStyles.AllowHexSpecifier, null, out var pass) && wanted.Contains(pass) && !found.ContainsKey(pass)
                && PazCrypto.Hash(name) == pass && lower.Contains(name.ToLowerInvariant()))
            {
                found[pass] = name.ToLowerInvariant();
                added++;
            }
        return added;
    }

    /// <summary>The pass names in the exe: each word ([0-9A-Za-z_]+) that names a .pars file (<paramref name="lower"/>, compared
    /// in lower case) and whose hash, as spelled, is a pass of <paramref name="wanted"/>. By hash, the name in lower case.
    /// Reads the exe in 8 MB blocks, a word cut by a block's end carried into the next.</summary>
    internal static Dictionary<uint, string> PassNames(string exe, IReadOnlySet<string> lower, IReadOnlySet<uint> wanted)
    {
        var found = new Dictionary<uint, string>();
        if (lower.Count == 0 || wanted.Count == 0) return found;
        int min = lower.Min(n => n.Length), max = lower.Max(n => n.Length);
        void Word(ReadOnlySpan<byte> w)
        {
            if (w.Length < min || w.Length > max) return;
            var text = Encoding.ASCII.GetString(w).ToLowerInvariant();
            if (lower.Contains(text) && PazCrypto.Hash(w) is var hash && wanted.Contains(hash)) found[hash] = text;
        }
        static bool IsWord(byte b) => char.IsAsciiLetterOrDigit((char)b) || b == '_';
        try
        {
            using var f = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            var buf = new byte[(8 << 20) + max + 1];
            // start: where the word being read began; -1 none; -2 inside a word already longer than any name
            int carried = 0, end, start;
            var tooLong = false;
            while (true)
            {
                var n = f.Read(buf, carried, buf.Length - carried);
                end = carried + n;
                start = tooLong ? -2 : carried > 0 ? 0 : -1;
                for (var i = carried; i < end; i++)
                {
                    if (IsWord(buf[i]))
                    {
                        if (start == -1) start = i;
                        continue;
                    }
                    if (start >= 0) Word(buf.AsSpan(start, i - start));
                    start = -1;
                }
                if (n == 0) break;
                tooLong = start == -2 || start >= 0 && end - start > max;
                carried = start >= 0 && !tooLong ? end - start : 0;
                if (carried > 0) buf.AsSpan(start, carried).CopyTo(buf);
            }
            if (start >= 0) Word(buf.AsSpan(start, end - start));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var at))
        {
            Index(game, engine, null, ct);
            at = located[game.Id];
        }
        foreach (var sha in sha1s)
            if (at.Roots.TryGetValue(sha, out var rs)) sink(sha, rs);
        var keyBase = keys.Stored(game);
        foreach (var inPaz in sha1s.Where(at.Shaders.ContainsKey).Select(s => (Sha: s, At: at.Shaders[s])).GroupBy(x => x.At.Paz))
        {
            SafeFileHandle paz;
            try { paz = OpenPaz(inPaz.Key); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            using (paz)
                foreach (var (sha, loc) in inPaz.OrderBy(x => x.At.Entry.Offset))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Pamt.Open(paz, loc.Entry, keyBase, loc.KeyByte, out _) is { } padxil && Container(padxil) is { } c
                        && Convert.ToHexStringLower(SHA1.HashData(c)) == sha)
                        sink(sha, c);
                }
        }
    }
}
