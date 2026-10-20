using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Nixxes;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Glacier;

/// <summary>IO Interactive's Glacier 2 (007 First Light): its data is in the RPKG archives of the install's Runtime folder
/// (<see cref="Rpkg"/>), and its shaders in the material effect resources (MATE), each a few to a few dozen DXIL containers
/// after the effect's tables, each carrying the root signature the game creates for it (RTS0 part). Measured on 007 First
/// Light: no other resource type holds a container. Each MATE is one pool, paired by linkage; the planner plans without a
/// recording when (nearly) every shader carries its root signature ("+RTS0" in Version, from Detect's sample).
/// EngineInfo: Family "Glacier 2", Version "DXIL" / "DXBC" / "DXIL+DXBC" [+ "+RTS0"].</summary>
public sealed class GlacierReader : IEngineReader
{
    public const string Family = "Glacier 2";
    static readonly uint Mate = Rpkg.Type("MATE");

    /// <summary>Bytes Detect reads: archive tables, smallest archive first (007 First Light's: 22 and 36 MB), then MATE resources.</summary>
    const long DetectBudget = 64L << 20;
    /// <summary>MATE resources Detect samples: enough shaders to tell whether they carry their root signatures.</summary>
    const int DetectResources = 8;
    /// <summary>A bigger resource isn't read (an array's limit; 007 First Light's biggest MATE: 13 MB).</summary>
    const int MaxResource = 1 << 30;

    public sealed record Loc(string Archive, Rpkg.Resource Resource, int At, int Size);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. ponytail: in memory only, like the
    /// carver's; ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>The first MATE resources of the smallest archive that has any (a patch may have none). D3D12: RPKG version 2
    /// games run on nothing else.</summary>
    public EngineInfo? Detect(Game game)
    {
        var meter = new NixxesReader.Meter(DetectBudget);
        int programs = 0, dxil = 0, rts0 = 0, read = 0;
        foreach (var f in Archives(game).OrderBy(f => f.Length))
        {
            if (Rpkg.Open(f.FullName, meter) is not { } a) continue;
            using var h = File.OpenHandle(a.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var r in a.Resources.Where(r => r.Type == Mate).OrderBy(r => r.Offset))
            {
                if (read >= DetectResources || r.Size > MaxResource || !meter.Take(r.Size)) break;
                byte[] d;
                try { d = Rpkg.Read(h, r); }
                catch (InvalidDataException) { continue; }
                read++;
                foreach (var (_, c) in Dxbc.Containers(d))
                {
                    if (Dxbc.Kind(c) < 0) continue;
                    programs++;
                    if (!Dxbc.Part(c, "DXIL"u8).IsEmpty) dxil++;
                    if (!Dxbc.Part(c, "RTS0"u8).IsEmpty) rts0++;
                }
            }
            if (programs > 0) break;
        }
        if (programs == 0) return null;
        var version = (dxil == programs ? "DXIL" : dxil == 0 ? "DXBC" : "DXIL+DXBC") + (rts0 >= 0.9 * programs ? CarvedReader.EmbeddedRootSignatures : "");
        return new EngineInfo(Family, version, null, "D3D12", false, null);
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var platformOf = new Dictionary<string, string>();
        var carriers = new RootSigCarriers();
        var maps = new List<ShaderMap>();
        int containers = 0, effects = 0, badRs = 0, bad = 0, tooBig = 0, unreadable = 0;
        long read = 0;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var archives = new List<Rpkg>();
        foreach (var f in Archives(game).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
        {
            if (Rpkg.Open(f.FullName) is not { } a) continue;
            archives.Add(a);
            content.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(game.InstallDir, f.FullName)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
        }
        foreach (var archive in Rpkg.Current(archives).Where(x => x.Resource.Type == Mate).GroupBy(x => x.Archive))
        {
            var rel = Path.GetRelativePath(game.InstallDir, archive.Key.Path);
            using var h = File.OpenHandle(archive.Key.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var (_, r) in archive.OrderBy(x => x.Resource.Offset))
            {
                ct.ThrowIfCancellationRequested();
                if (r.Size > MaxResource) { tooBig++; continue; }
                byte[] d;
                try { d = Rpkg.Read(h, r); }
                catch (InvalidDataException) { unreadable++; continue; }
                read += d.Length;
                var lib = $"{rel}:{r.Hash:X16}";
                var pool = new List<string>();
                foreach (var (at, c) in Dxbc.Containers(d))
                {
                    containers++;
                    var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                    if (locs.TryAdd(sha, new Loc(archive.Key.Path, r, at, c.Length)))
                        try
                        {
                            if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info)
                            {
                                shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                                if (CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) is { } lanes) platformOf[sha] = lanes;
                                if (!Dxbc.Part(c, "RTS0"u8).IsEmpty)
                                    if (Dxbc.RootSignatureValid(c)) carriers.See(c, sha, Dxbc.Kind(c));
                                    else badRs++;
                            }
                        }
                        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; }   // valid container, odd program: not usable
                    if (shaders.ContainsKey(sha)) pool.Add(sha);
                }
                if (pool.Count == 0) continue;
                effects++;
                foreach (var g in pool.Distinct().GroupBy(s => platformOf.GetValueOrDefault(s, CarvedReader.Platform)))
                    maps.Add(new ShaderMap(CarvedReader.Sha1Hex(g.Key == CarvedReader.Platform ? lib : $"{lib}|{g.Key}"), lib, g.Key, g.ToList()));
            }
        }
        if (shaders.Count == 0) throw new InvalidDataException(@"Runtime: no shader in its archives: scan the game again");
        carriers.Apply(shaders);
        located[game.Id] = locs;
        var stages = shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count());
        log?.Report($"{effects} material effects with shaders ({read / 1e9:F2} GB decompressed): {containers} shader containers, {shaders.Count} shaders ("
            + string.Join(", ", stages.Select(g => $"{g.Count()} {g.Key}")) + $"), {shaders.Values.Select(s => s.RootSignature).OfType<string>().Distinct().Count()} root signatures, carried by "
            + string.Join(", ", stages.Select(g => $"{g.Count(s => s.RootSignature != null)} {g.Key}"))
            + $"; {shaders.Values.Count(s => s.Stage == Stage.Library)} ray tracing libraries, {shaders.Values.Count(s => s.InlineRayTracing)} tracing rays inline"
            + (badRs > 0 ? $"; {badRs} root signatures the runtime would reject, left out" : "")
            + (tooBig > 0 ? $"; warning: {tooBig} resources over {MaxResource >> 30} GB not read" : "")
            + (unreadable > 0 ? $"; warning: {unreadable} resources don't decompress" : "")
            + (bad > 0 ? $"; {bad} unparseable" : "")
            + string.Concat(platformOf.Values.GroupBy(p => p).Select(g => $"; {g.Count()} need {g.Key}, not planned for 32-lane GPUs"))
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform, .. platformOf.Values.Distinct().Order(StringComparer.Ordinal)], shaders, maps, carriers.Blobs);
    }

    /// <summary>Decompresses each resource holding a requested container once; one whose bytes changed since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Archive))
        {
            using var h = File.OpenHandle(file.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var resource in file.GroupBy(x => x.At.Resource).OrderBy(g => g.Key.Offset))
            {
                ct.ThrowIfCancellationRequested();
                byte[] d;
                try { d = Rpkg.Read(h, resource.Key); }
                catch (Exception e) when (e is InvalidDataException or ArgumentException) { continue; }
                foreach (var (sha, at) in resource)
                {
                    if (at.At + at.Size > d.Length) continue;
                    var b = d[at.At..(at.At + at.Size)];
                    if (Convert.ToHexStringLower(SHA1.HashData(b)) == sha) sink(sha, b);
                }
            }
        }
    }

    /// <summary>The RPKG archives in the install's Runtime folder.</summary>
    static IEnumerable<FileInfo> Archives(Game game)
    {
        var runtime = new DirectoryInfo(Path.Combine(game.InstallDir, "Runtime"));
        if (!runtime.Exists) return [];
        return runtime.EnumerateFiles("*.rpkg", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).Where(f => Rpkg.IsRpkg(f.FullName));
    }
}
