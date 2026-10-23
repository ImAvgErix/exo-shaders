using System.Globalization;
using System.Security;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Vulkan;

/// <summary>Steam's Vulkan pipeline cache of a game (Shader Pre-Caching), in
/// <c>&lt;library&gt;\steamapps\shadercache\&lt;appid&gt;\fozpipelinesv6</c>: the archives Steam's Fossilize layer recorded on this
/// PC (steamapp_pipeline_cache.foz, steamapprun_pipeline_cache.*.foz) and the one it downloaded (steam_pipeline_cache.foz),
/// at the top or per bucket in steamapprun_pipeline_cache.&lt;hash&gt; folders (<see cref="FossilizeFilter"/>). Only Steam games
/// have one. SCSKiller reads these and never writes Steam's folders (<see cref="Owned"/>).</summary>
public static class SteamPipelineCache
{
    public const string Folder = "fozpipelinesv6";
    public const string Download = "steam_pipeline_cache.foz";
    public const string Merged = "steamapp_pipeline_cache.foz";
    public const string RunPrefix = "steamapprun_pipeline_cache.";

    public static string Dir(string library, uint appId) =>
        Path.Combine(library, "steamapps", "shadercache", appId.ToString(CultureInfo.InvariantCulture), Folder);

    /// <summary>The cache's buckets: <paramref name="dir"/> itself first, then each bucket folder in name order. Empty when
    /// <paramref name="dir"/> doesn't exist.</summary>
    public static List<SteamBucket> Buckets(string dir, IProgress<string>? log = null)
    {
        if (!Directory.Exists(dir)) return [];
        List<SteamBucket> buckets = [Read(dir, null, log)];
        foreach (var sub in Directory.EnumerateDirectories(dir, RunPrefix + "*").Order(StringComparer.Ordinal))
            if (Path.GetFileName(sub)[RunPrefix.Length..] is { Length: 16 } hex && ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, null, out var hash))
                buckets.Add(Read(sub, hash, log));
        return buckets;
    }

    static SteamBucket Read(string dir, ulong? hash, IProgress<string>? log)
    {
        var recorded = new[] { Path.Combine(dir, Merged) }.Where(File.Exists)
            .Concat(Directory.EnumerateFiles(dir, RunPrefix + "*.foz").Order(StringComparer.Ordinal)).ToList();
        var download = Path.Combine(dir, Download);
        // A bucket folder Steam recorded nothing in names its apps in its download
        List<string> read = recorded.Count == 0 && hash != null ? (File.Exists(download) ? [download] : []) : recorded;
        List<FozApp> apps = [];
        foreach (var path in read)
        {
            try
            {
                using var f = Open(path);
                // A bucket's archives are all one app's: its first application info is enough
                IEnumerable<Foz.Entry> infos = hash == null ? Foz.List(f).Where(e => e.Tag == Foz.ApplicationInfo)
                    : (Foz.First(f, e => e.Tag == Foz.ApplicationInfo) is { } first ? [first] : []);
                foreach (var e in infos)
                    if (FozApp.Parse(e.Hash, Foz.Payload(f, e)) is { } app && !apps.Contains(app)) apps.Add(app);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                log?.Report($"{path} can't be read: {e.Message}");
            }
        }
        return new SteamBucket(dir, hash, recorded, File.Exists(download) ? download : null, apps,
            recorded.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max());
    }

    /// <summary>Opens a regular file (no link) for reading while Steam may write it.</summary>
    internal static FileStream Open(string path) => GameFiles.OpenRegular(path) ?? throw new InvalidDataException("not a regular file");

    /// <summary>The bucket a plan reads for this GPU's vendor: one that fits it (or can't be told apart) and has archives,
    /// preferring a sure fit, then one recorded on this PC, then the newest, then the newest engine version. With
    /// <paramref name="engine"/>, only buckets of that engine name. Null without a filter (Steam's file missing): which bucket
    /// is this GPU's can't be told.</summary>
    public static SteamBucket? Choose(IEnumerable<SteamBucket> buckets, uint vendorId, FossilizeFilter? filter, string? engine = null) =>
        filter == null ? null
        : buckets.Where(b => engine == null || b.Apps.Any(a => a.Engine == engine))
            .Select(b => (Bucket: b, Fits: Fits(b, vendorId, filter)))
            .Where(x => x.Fits != false && (x.Bucket.Recorded.Count > 0 || x.Bucket.Download != null))
            .OrderByDescending(x => x.Fits == true)
            .ThenByDescending(x => x.Bucket.Recorded.Count > 0)
            .ThenByDescending(x => x.Bucket.RecordedAt)
            .ThenByDescending(x => x.Bucket.Apps.Select(a => a.EngineVersion).DefaultIfEmpty(0u).Max())
            .ThenBy(x => x.Bucket.Hash)
            .Select(x => x.Bucket).FirstOrDefault();

    /// <summary>The top folder fits every GPU; a bucket fits when one of its apps hashes to it on this vendor, doesn't when
    /// it's another vendor's, and null when that can't be told.</summary>
    internal static bool? Fits(SteamBucket b, uint vendorId, FossilizeFilter filter)
    {
        if (b.Hash is not { } hash) return true;
        var matches = b.Apps.Select(a => filter.Matches(hash, a, vendorId)).ToList();
        return matches.Contains(true) ? true : matches.Contains(false) ? false : null;
    }

    /// <summary>The archives a plan reads: SCSKiller's own recording first, then the bucket's recorded ones, then its
    /// download (in the top folder, only the pipelines of the bucket's apps).</summary>
    public static List<FozSource> Sources(SteamBucket bucket, string? ours)
    {
        List<FozSource> sources = [];
        if (ours != null) sources.Add(new FozSource(ours, FozOrigin.Ours));
        sources.AddRange(bucket.Recorded.Select(p => new FozSource(p, FozOrigin.SteamLocal)));
        if (bucket.Download != null)
            sources.Add(new FozSource(bucket.Download, FozOrigin.SteamDownload) { Apps = bucket.Hash == null ? bucket.Apps.Select(a => a.Hash).ToHashSet() : null });
        return sources;
    }

    /// <summary><paramref name="path"/> is (or, through a link or junction, lands) in Steam's folders: any steamapps or
    /// shadercache folder, or a Steam library (<paramref name="libraries"/>, else the ones Steam lists).</summary>
    public static bool Owned(string path, IEnumerable<string>? libraries = null)
    {
        var full = Path.GetFullPath(path);
        var target = LinkTarget(full);
        var paths = new[] { full, Resolved(full), target, target == null ? null : Resolved(target) }.OfType<string>().Distinct().ToArray();
        var libs = (libraries ?? KnownLibraries()).SelectMany(l => new[] { Path.GetFullPath(l), GameFiles.FinalPath(l) }).OfType<string>()
            .Select(l => Path.TrimEndingDirectorySeparator(l) + Path.DirectorySeparatorChar).Distinct().ToList();
        return paths.Any(p =>
            p.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                .Any(s => s.Equals("steamapps", StringComparison.OrdinalIgnoreCase) || s.Equals("shadercache", StringComparison.OrdinalIgnoreCase))
            || libs.Any(l => (p + Path.DirectorySeparatorChar).StartsWith(l, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The path with its nearest existing folder's links and junctions resolved; null when none can be opened.</summary>
    static string? Resolved(string full)
    {
        var below = new Stack<string>();
        for (var dir = Path.GetDirectoryName(full); dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (GameFiles.FinalPath(dir) is { } final) return Path.Combine([final, .. below, Path.GetFileName(full)]);
            below.Push(Path.GetFileName(dir));
        }
        return null;
    }

    static string? LinkTarget(string full)
    {
        try
        {
            return File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static List<string> KnownLibraries()
    {
        try
        {
            var root = SteamSource.Root();
            return [root, .. SteamSource.Libraries(root)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return [];
        }
    }
}
