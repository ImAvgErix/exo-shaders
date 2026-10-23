using System.Globalization;
using System.Text;
using System.Text.Json;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Vulkan;

/// <summary>Plans and work folders for Vulkan games. A Vulkan plan is the recorded pipelines ('V' items, an entry name each)
/// and its work folder a Fossilize archive the warmer replays through the driver (scskiller.foz, with scskiller_validated.foz
/// listing the shader modules already known good). Where it applies:
/// <list type="bullet">
/// <item>Steam games only: SCSKiller's layer turns on by SteamAppId (<see cref="VulkanLayer"/>), and Steam's own pipeline
/// cache (<see cref="SteamPipelineCache"/>) is the other source of pipelines.</item>
/// <item>NVIDIA (<see cref="ExeNameKey"/>): planned straight from Steam's cache, no session of ours needed; the compile writes
/// the driver cache Steam points the game at (nvidiav1\GLCache, <see cref="SteamCache"/>).</item>
/// <item>AMD (<see cref="DeviceKey"/>): the driver's cache is keyed by the device, so the compile needs the Vulkan device of
/// one session recorded through our layer (<see cref="DeviceNeeded"/>) and Steam's AMDv1 cache variables
/// (<see cref="SteamCacheNeeded"/>).</item>
/// <item>Intel: no Vulkan plans (its caps have no Vulkan key).</item>
/// </list></summary>
public static class VulkanPlan
{
    /// <summary>The archives a plan reads, open for the length of one call.</summary>
    sealed class Archive(FozSource source, FileStream file, List<Foz.Entry> entries) : IDisposable
    {
        public FozSource Source { get; } = source;
        public FileStream File { get; } = file;
        public List<Foz.Entry> Entries { get; } = entries;

        public void Dispose() => File.Dispose();
    }

    public const string RecordingFile = "recording.foz";
    public const string WorkFile = "scskiller.foz";
    public const string ValidatedFile = "scskiller_validated.foz";
    public const string InboxPattern = "scskiller.*.foz";
    public const string Platform = "vulkan";
    public const char Tag = 'V';

    // VendorCaps.VulkanKey: what the vendor's Vulkan pipeline cache is keyed by
    public const string ExeNameKey = "exe-name", AppNameKey = "app-name", ContentKey = "content", DeviceKey = "device";

    /// <summary>The replay creates its device like this (<see cref="VulkanLayer.Device"/>) when the variable is set.</summary>
    public const string DeviceVariable = "SCSKILLER_REPLAY_DEVICE";
    public const string DeviceNeeded = "turn on recording and start the game once, so the compile uses its Vulkan device";
    public const string SteamCacheNeeded = "start the game once from Steam with Shader Pre-Caching on";

    const ulong LibraryBit = 0x800;                  // VK_PIPELINE_CREATE_2_LIBRARY_BIT_KHR
    const ulong DescriptorHeapBit = 0x1000000000;    // VK_PIPELINE_CREATE_2_DESCRIPTOR_HEAP_BIT_EXT
    const long Flags2SType = 1000470005;             // VkPipelineCreateFlags2CreateInfo
    const uint RayQueryKhr = 4472, RayQueryProvisional = 6016;   // SPIR-V capabilities
    const int BytesPerPipeline = 24 << 10;           // a pipeline's estimated share of a capped cache

    // Fields of an entry that name other entries, by tag (descriptor set layout, pipeline layout, pipelines)
    static readonly Dictionary<int, HashSet<string>> HandleFields = new()
    {
        [2] = new(StringComparer.Ordinal) { "immutableSamplers" },
        [3] = new(StringComparer.Ordinal) { "setLayouts" },
        [Foz.Graphics] = PipelineHandles(),
        [Foz.Compute] = PipelineHandles(),
        [Foz.RayTracing] = PipelineHandles(),
    };

    static HashSet<string> PipelineHandles() => new(StringComparer.Ordinal) { "layout", "renderPass", "basePipelineHandle", "module", "libraries" };

    /// <summary>A recorded device as Steam's layer creates it: with VK_EXT_pipeline_creation_cache_control and its feature on
    /// (in VkPhysicalDeviceVulkan13Features, else its own struct), so the replay's driver cache keys match the game's under
    /// Steam. Unchanged when it isn't "extensions,flags,features".</summary>
    public static string UnderSteamLayer(string device)
    {
        const string Vulkan13 = "53:", CacheControl = "1000297000:";
        if (device.Split(',') is not [var extensionList, var middle, var featureList]) return device;
        var extensions = extensionList.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!extensions.Contains("VK_EXT_pipeline_creation_cache_control")) extensions.Add("VK_EXT_pipeline_creation_cache_control");
        var features = featureList.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int i;
        // pipelineCreationCacheControl is VkPhysicalDeviceVulkan13Features' fourth feature
        if ((i = features.FindIndex(s => s.StartsWith(Vulkan13, StringComparison.Ordinal) && s.Length > Vulkan13.Length + 3)) >= 0)
            features[i] = $"{features[i][..(Vulkan13.Length + 3)]}1{features[i][(Vulkan13.Length + 4)..]}";
        else if ((i = features.FindIndex(s => s.StartsWith(CacheControl, StringComparison.Ordinal) && s.Length > CacheControl.Length)) >= 0)
            features[i] = $"{CacheControl}1{features[i][(CacheControl.Length + 1)..]}";
        else features.Add($"{CacheControl}1");
        return $"{string.Join(' ', extensions)},{middle},{string.Join(' ', features)}";
    }

    /// <summary>The variable Steam points the vendor's Vulkan driver cache at, and its folders under the game's
    /// shadercache\&lt;appid&gt;: NVIDIA nvidiav1\GLCache, AMD AMDv1. Null for any other vendor.</summary>
    public static (string Variable, string[] Tail)? SteamCache(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => ("__GL_SHADER_DISK_CACHE_PATH", new[] { "nvidiav1", "GLCache" }),
        GpuVendor.Amd => ("AMD_VK_PIPELINE_CACHE_PATH", new[] { "AMDv1" }),
        _ => null,
    };

    /// <summary>Where Steam pointed the vendor's driver cache in the game's recorded session (<see cref="VulkanLayer.CacheEnv"/>);
    /// null when it didn't.</summary>
    public static string? CachePath(IReadOnlyDictionary<string, string>? env, GpuVendor vendor) =>
        SteamCache(vendor) is { } cache && env?.FirstOrDefault(kv => kv.Key.Equals(cache.Variable, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } path
            ? path : null;

    public static bool IsVulkan(EngineInfo e) => e.GraphicsApi.StartsWith("Vulkan", StringComparison.Ordinal);

    public static PsoDb.Rec Item(string name) => new(Tag, Encoding.ASCII.GetBytes(name));
    public static string Key(string name) => Item(name).Key;

    /// <summary>Ready when the recording (ours or Steam's archives) has a pipeline.</summary>
    public static PlanCheck Check(Recording? recording)
    {
        var has = recording?.VulkanSources is { } sources ? sources.Any(HasPipeline)
            : recording != null && File.Exists(recording.DbPath) && HasPipeline(recording.DbPath);
        return has ? new PlanCheck(Readiness.Ready, "planned from a recording")
            : new PlanCheck(Readiness.NeedsRecording, "turn on recording and play for about 5 minutes");
    }

    static bool HasPipeline(FozSource s)
    {
        try
        {
            using var f = SteamPipelineCache.Open(s.Path);
            return Foz.First(f, e => s.Apps is not { } apps ? Foz.IsPipeline(e.Tag)
                : e.Tag == Foz.BlobLink && Link(f, e) is { } link && Foz.IsPipeline(link.Tag) && apps.Contains(link.App)) != null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool HasPipeline(string foz)
    {
        try
        {
            return Foz.List(foz).Any(e => Foz.IsPipeline(e.Tag));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static List<VkPipeline> Pipelines(string foz) => Using(One(foz), a => Pipelines(a));

    /// <summary>The pipelines of every source, each name once (the first source's); a source that can't be read is logged
    /// and skipped.</summary>
    public static List<VkPipeline> Pipelines(IReadOnlyList<FozSource> sources, IProgress<string>? log = null) => Using(OpenAll(sources, log), a => Pipelines(a));

    static List<VkPipeline> Pipelines(List<Archive> archives)
    {
        var modules = new Dictionary<ulong, (Archive A, Foz.Entry E)>();
        var kinds = new Dictionary<int, HashSet<string>>();
        foreach (var a in archives)
            foreach (var e in a.Entries)
                if (e.Tag == Foz.ShaderModule) modules.TryAdd(e.Hash, (a, e));
                else if (Foz.IsPipeline(e.Tag)) Kind(kinds, e.Tag).Add($"{e.Hash:x16}");
        var rayQuery = new Dictionary<ulong, bool>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<VkPipeline> pipelines = [];
        foreach (var a in archives)
        {
            var linked = a.Source.Apps is { } apps ? Linked(a, apps) : null;
            foreach (var e in a.Entries.Where(e => Foz.IsPipeline(e.Tag) && (linked?.Contains((e.Tag, e.Hash)) ?? true)))
                if (seen.Add(e.Name)) pipelines.Add(Parse(a.File, e, kinds[e.Tag], RayQuery) with { Origin = a.Source.Origin });
        }
        return pipelines;

        bool RayQuery(string hex)
        {
            if (!Hex16(hex, out var hash) || !modules.TryGetValue(hash, out var module)) return false;
            if (rayQuery.TryGetValue(hash, out var uses)) return uses;
            try
            {
                uses = UsesRayQuery(Foz.Payload(module.A.File, module.E));
            }
            catch (Exception e) when (e is InvalidDataException or JsonException or FormatException or ArgumentException or KeyNotFoundException or InvalidOperationException)
            {
                uses = false;
            }
            return rayQuery[hash] = uses;
        }
    }

    static HashSet<string> Kind(Dictionary<int, HashSet<string>> kinds, int tag) =>
        kinds.TryGetValue(tag, out var set) ? set : kinds[tag] = new(StringComparer.Ordinal);

    /// <summary>Reads a pipeline entry: its library and descriptor heap flags, the pipelines of its kind (hex hashes in
    /// <paramref name="kind"/>) it names, and whether a module it names traces rays inline.</summary>
    static VkPipeline Parse(Stream f, Foz.Entry e, HashSet<string> kind, Func<string, bool> rayQuery)
    {
        List<string> needs = [];
        var needed = new HashSet<string>(StringComparer.Ordinal);
        bool rq = false, heap = false, library = false;
        var self = $"{e.Hash:x16}";
        try
        {
            using var doc = JsonDocument.Parse(Foz.Payload(f, e));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty(self, out var info))
                    {
                        library = Flags(info) is { } flags && (flags & LibraryBit) != 0;
                        Walk(info);
                    }
        }
        catch (Exception x) when (x is InvalidDataException or JsonException or InvalidOperationException)
        {
            // what was read so far stands
        }
        return new VkPipeline(e, library, rq, needs) { DescriptorHeap = heap };

        static ulong? Flags(JsonElement o) =>
            o.TryGetProperty("flags", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out var n) ? n : null;

        void Walk(JsonElement v)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.Object:
                    if (v.TryGetProperty("sType", out var sType) && sType.ValueKind == JsonValueKind.Number && sType.TryGetInt64(out var s) && s == Flags2SType
                        && Flags(v) is { } flags2)
                    {
                        library |= (flags2 & LibraryBit) != 0;
                        heap |= (flags2 & DescriptorHeapBit) != 0;
                    }
                    foreach (var p in v.EnumerateObject()) Walk(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var x in v.EnumerateArray()) Walk(x);
                    break;
                case JsonValueKind.String when v.GetString() is { Length: 16 } hex:
                    if (hex != self && kind.Contains(hex) && Foz.Name(e.Tag, Convert.ToUInt64(hex, 16)) is var name && needed.Add(name)) needs.Add(name);
                    rq |= rayQuery(hex);
                    break;
            }
        }
    }

    /// <summary>Lowercase, 16 hex digits: how Fossilize writes a hash.</summary>
    static bool Hex16(string? s, out ulong hash)
    {
        hash = 0;
        return s is { Length: 16 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && ulong.TryParse(s, NumberStyles.AllowHexSpecifier, null, out hash);
    }

    /// <summary>A shader module entry declares the RayQueryKHR capability (or the provisional one) in one of its SPIR-V
    /// modules: inline ray tracing. The code is base64 or, past the JSON's NUL, varint-packed words.</summary>
    internal static bool UsesRayQuery(byte[] payload)
    {
        var end = Array.IndexOf(payload, (byte)0);
        using var doc = JsonDocument.Parse(payload.AsMemory(0, end < 0 ? payload.Length : end));
        foreach (var m in doc.RootElement.GetProperty("shaderModules").EnumerateObject().Select(p => p.Value))
        {
            var size = m.GetProperty("codeSize").GetInt64();
            if (size < 0 || size / 4 > payload.Length) throw new InvalidDataException("a code size past the payload");
            var words = new uint[size / 4];
            if (m.TryGetProperty("varintOffset", out var offset))
            {
                if (end < 0 || !Varint(payload.AsSpan(end + 1 + offset.GetInt32(), m.GetProperty("varintSize").GetInt32()), words))
                    throw new InvalidDataException("bad varint SPIR-V");
            }
            else Buffer.BlockCopy(Convert.FromBase64String(m.GetProperty("code").GetString()!), 0, words, 0, words.Length * 4);
            // Capabilities come first, before OpMemoryModel (14); OpCapability is 17
            for (var i = 5; i < words.Length;)
            {
                int count = (int)(words[i] >> 16), op = (int)(words[i] & 0xFFFF);
                if (count == 0 || op == 14) break;
                if (op == 17 && i + 1 < words.Length && words[i + 1] is RayQueryKhr or RayQueryProvisional) return true;
                i += count;
            }
        }
        return false;
    }

    /// <summary>Fossilize's varint SPIR-V: 7 bits a byte, low first; the span must hold exactly the words.</summary>
    static bool Varint(ReadOnlySpan<byte> b, uint[] words)
    {
        var at = 0;
        for (var i = 0; i < words.Length; i++)
        {
            uint word = 0;
            var shift = 0;
            do
            {
                if (at >= b.Length || shift >= 32) return false;
                word |= (uint)(b[at] & 0x7F) << shift;
                shift += 7;
            } while ((b[at++] & 0x80) != 0);
            words[i] = word;
        }
        return at == b.Length;
    }

    /// <summary>Plans the recording's pipelines (no ray tracing ones without <paramref name="rayTracing"/>). With a capped
    /// Vulkan cache (<see cref="VendorCaps.VulkanCacheCap"/>) only as many as fit it, each with what it needs.</summary>
    public static Plan Build(Game game, Recording recording, VendorCaps caps, string outDir, bool rayTracing) =>
        Using(recording.VulkanSources is { } sources ? OpenAll(sources, null) : One(recording.DbPath), archives =>
        {
            var pipelines = Pipelines(archives);
            return Build(game, pipelines, Needs(archives, pipelines), caps, outDir, rayTracing);
        });

    static Plan Build(Game game, List<VkPipeline> all, Func<string, IReadOnlyList<string>?> needs, VendorCaps caps, string outDir, bool rayTracing)
    {
        var planned = all.Where(p => rayTracing || p.Entry.Tag != Foz.RayTracing).ToList();
        // An app-name keyed cache can't hold descriptor heap pipelines
        var heap = caps.VulkanKey == AppNameKey ? planned.RemoveAll(p => p.DescriptorHeap) : 0;
        var rt = all.Count(p => p.Entry.Tag == Foz.RayTracing);
        var leftOut = all.Count - planned.Count;
        long? budgetLeftOut = null;
        if (caps.VulkanCacheCap is { } cap)
        {
            var within = WithinBudget(planned, needs, cap / BytesPerPipeline);
            budgetLeftOut = planned.Count - within.Count;
            planned = within;
        }
        var stats = new PlanStats(planned.Count, Generated: 0, SynthesizedTemplates: 0, RootSignatures: 0, RootSigRuleVerified: false,
            RtLibraries: rt, StageSets: all.Count, LeftOut: leftOut, RtStateObjects: rt, RtInline: all.Count(p => p.RayQuery),
            CacheBudgetLeftOut: budgetLeftOut, DescriptorHeap: heap);
        // No index: the content hash is SHA-1 of nothing
        var plan = new Plan(game.Id, "da39a3ee5e6b4b0d3255bfef95601890afd80709", Platform, caps.Profile, stats, Path.Combine(outDir, "plan.bin"));
        PlanFile.Write(plan, planned.Select(p => Item(p.Name)));
        return plan;
    }

    /// <summary>The pipelines, in order, whose pipelines needed (with theirs) still fit <paramref name="room"/> pipelines in all.</summary>
    static List<VkPipeline> WithinBudget(List<VkPipeline> planned, Func<string, IReadOnlyList<string>?> needs, long room)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        List<VkPipeline> within = [];
        foreach (var p in planned)
        {
            var adds = new HashSet<string>(StringComparer.Ordinal);
            var todo = new Stack<string>([p.Name]);
            while (todo.TryPop(out var n))
            {
                if (taken.Contains(n) || !adds.Add(n)) continue;
                foreach (var need in needs(n) ?? []) todo.Push(need);
            }
            if (taken.Count + adds.Count > room) continue;
            taken.UnionWith(adds);
            within.Add(p);
        }
        return within;
    }

    /// <summary>What a pipeline needs: the listed ones' as listed, any other's (another source's) read when asked.</summary>
    static Func<string, IReadOnlyList<string>?> Needs(List<Archive> archives, List<VkPipeline> listed)
    {
        var known = listed.ToDictionary(p => p.Name, p => p.Needs, StringComparer.Ordinal);
        var byName = new Dictionary<string, (Archive A, Foz.Entry E)>(StringComparer.Ordinal);
        var kinds = new Dictionary<int, HashSet<string>>();
        foreach (var a in archives)
            foreach (var e in a.Entries.Where(e => Foz.IsPipeline(e.Tag)))
            {
                byName.TryAdd(e.Name, (a, e));
                Kind(kinds, e.Tag).Add($"{e.Hash:x16}");
            }
        return n => known.TryGetValue(n, out var needs) ? needs
            : byName.TryGetValue(n, out var x) ? known[n] = Parse(x.A.File, x.E, kinds[x.E.Tag], _ => false).Needs
            : null;
    }

    /// <summary>Writes the work folder of a plan: scskiller.foz (the planned pipelines and everything they need),
    /// scskiller_validated.foz (the shader modules the replay needn't validate) and skipped.txt (planned pipelines left
    /// out, "n 0").</summary>
    public static void Materialize(Plan plan, Recording recording, string workDir)
    {
        if (recording.VulkanSources is { } sources)
        {
            Materialize(plan, sources, workDir);
            return;
        }
        Directory.CreateDirectory(workDir);
        var wanted = Planned(plan);
        var pipelines = Pipelines(recording.DbPath);
        var keep = Closure(pipelines, wanted);
        Write(recording.DbPath, Path.Combine(workDir, WorkFile), e => !Foz.IsPipeline(e.Tag) || keep.Contains(e.Name));
        var modules = Foz.List(recording.DbPath).Where(e => e.Tag == Foz.ShaderModule).Select(e => e.Hash).ToList();
        WriteNew(Path.Combine(workDir, ValidatedFile), w =>
        {
            Foz.WriteMagic(w);
            foreach (var m in modules) Foz.AppendName(w, Foz.ShaderModule, m);
        });
        wanted.ExceptWith(pipelines.Select(p => p.Name));
        File.WriteAllText(Path.Combine(workDir, "skipped.txt"), $"{wanted.Count} 0");
    }

    /// <summary>The same from several archives (ours and Steam's): each entry is taken from the first source that has it, a
    /// pipeline that names an entry none has is left out with every pipeline that needs it, and only trusted sources'
    /// modules are listed as validated. Never writes into Steam's folders.</summary>
    public static void Materialize(Plan plan, IReadOnlyList<FozSource> sources, string workDir, IProgress<string>? log = null)
    {
        var work = Path.Combine(workDir, WorkFile);
        var validated = Path.Combine(workDir, ValidatedFile);
        var skipped = Path.Combine(workDir, "skipped.txt");
        if (new[] { work, validated, skipped }.Any(p => SteamPipelineCache.Owned(p)))
            throw new InvalidOperationException($"{workDir} is or links into Steam's folders, which SCSKiller never writes");
        Directory.CreateDirectory(workDir);
        var wanted = Planned(plan);
        var archives = OpenAll(sources, log);
        try
        {
            var byName = new Dictionary<string, (Archive A, Foz.Entry E)>(StringComparer.Ordinal);
            var byHash = new Dictionary<ulong, List<string>>();
            foreach (var a in archives)
                foreach (var e in a.Entries.Where(e => e.Tag is not (Foz.ApplicationInfo or Foz.BlobLink or Foz.BucketInfo)))
                    if (byName.TryAdd(e.Name, (a, e)))
                        (byHash.TryGetValue(e.Hash, out var same) ? same : byHash[e.Hash] = []).Add(e.Name);

            // What each wanted entry names, and the ones naming an entry no source has
            var refs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var users = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var broken = new HashSet<string>(StringComparer.Ordinal);
            var todo = new Stack<string>(wanted.Where(byName.ContainsKey));
            while (todo.TryPop(out var n))
            {
                if (refs.ContainsKey(n)) continue;
                var (names, missing) = References(byName[n].A.File, byName[n].E, byHash);
                refs[n] = names;
                if (missing) broken.Add(n);
                foreach (var r in names)
                {
                    (users.TryGetValue(r, out var u) ? u : users[r] = []).Add(n);
                    todo.Push(r);
                }
            }
            var spread = new Stack<string>(broken);
            while (spread.TryPop(out var n))
                foreach (var u in users.GetValueOrDefault(n) ?? [])
                    if (broken.Add(u)) spread.Push(u);

            var keep = new HashSet<string>(StringComparer.Ordinal);
            todo = new Stack<string>(wanted.Where(n => refs.ContainsKey(n) && !broken.Contains(n)));
            while (todo.TryPop(out var n))
                if (keep.Add(n))
                    foreach (var r in refs[n]) todo.Push(r);

            WriteNew(work, w =>
            {
                Foz.WriteMagic(w);
                // One application info, so the driver sees the game's
                var info = archives.SelectMany(a => a.Entries.Where(e => e.Tag == Foz.ApplicationInfo).Take(1).Select(e => (A: a, E: e))).FirstOrDefault();
                if (info.A != null) Foz.Copy(info.A.File, info.E, w);
                foreach (var a in archives)
                    foreach (var e in a.Entries.Where(e => keep.Contains(e.Name) && byName[e.Name].A == a))
                        Foz.Copy(a.File, e, w);
            });
            var modules = keep.Select(n => byName[n]).Where(x => x.E.Tag == Foz.ShaderModule).ToList();
            WriteNew(validated, w =>
            {
                Foz.WriteMagic(w);
                foreach (var m in modules.Where(m => m.A.Source.Trusted)) Foz.AppendName(w, Foz.ShaderModule, m.E.Hash);
            });
            WriteNew(skipped, w => w.Write(Encoding.ASCII.GetBytes($"{wanted.Count(n => !keep.Contains(n))} 0")));
        }
        finally
        {
            foreach (var a in archives) a.Dispose();
        }
    }

    static HashSet<string> Planned(Plan plan) =>
        PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == Tag).Select(r => Encoding.ASCII.GetString(r.Payload)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Writes <paramref name="path"/> through a temp file beside it, so a failed write leaves the old one.</summary>
    static void WriteNew(string path, Action<Stream> write)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20)) write(f);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>The entries an entry names through its handle fields (<see cref="HandleFields"/>), every tag of each hash;
    /// Missing: one it names is in no source.</summary>
    static (HashSet<string> Names, bool Missing) References(Stream f, Foz.Entry e, Dictionary<ulong, List<string>> byHash)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var missing = false;
        if (!HandleFields.TryGetValue(e.Tag, out var fields)) return (names, missing);
        try
        {
            using var doc = JsonDocument.Parse(Foz.Payload(f, e));
            Walk(doc.RootElement);
        }
        catch (Exception x) when (x is InvalidDataException or JsonException)
        {
            // what was read so far stands
        }
        return (names, missing);

        void Handle(JsonElement v)
        {
            if (v.ValueKind != JsonValueKind.String || !Hex16(v.GetString(), out var hash) || hash == 0 || hash == e.Hash) return;
            if (byHash.TryGetValue(hash, out var named)) names.UnionWith(named);
            else missing = true;
        }

        void Walk(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.Array)
                foreach (var x in v.EnumerateArray()) Walk(x);
            else if (v.ValueKind == JsonValueKind.Object)
                foreach (var p in v.EnumerateObject())
                    if (!fields.Contains(p.Name)) Walk(p.Value);
                    else if (p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var x in p.Value.EnumerateArray()) Handle(x);
                    else Handle(p.Value);
        }
    }

    static T Using<T>(List<Archive> archives, Func<List<Archive>, T> use)
    {
        try
        {
            return use(archives);
        }
        finally
        {
            foreach (var a in archives) a.Dispose();
        }
    }

    static List<Archive> One(string foz)
    {
        var f = Foz.OpenShared(foz);
        try
        {
            return [new Archive(new FozSource(foz, FozOrigin.Ours), f, Foz.List(f))];
        }
        catch
        {
            f.Dispose();
            throw;
        }
    }

    static List<Archive> OpenAll(IReadOnlyList<FozSource> sources, IProgress<string>? log)
    {
        List<Archive> archives = [];
        foreach (var s in sources)
        {
            FileStream? f = null;
            try
            {
                f = SteamPipelineCache.Open(s.Path);
                archives.Add(new Archive(s, f, Foz.List(f)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                f?.Dispose();
                log?.Report($"{s.Path} can't be read: {e.Message}");
            }
        }
        return archives;
    }

    /// <summary>The pipelines a download's blob links tie to one of <paramref name="apps"/>.</summary>
    static HashSet<(int, ulong)> Linked(Archive a, IReadOnlySet<ulong> apps)
    {
        var linked = new HashSet<(int, ulong)>();
        foreach (var e in a.Entries.Where(e => e.Tag == Foz.BlobLink))
            if (Link(a.File, e) is { } link && Foz.IsPipeline(link.Tag) && apps.Contains(link.App)) linked.Add((link.Tag, link.Hash));
        return linked;
    }

    /// <summary>A blob link entry: the application info and the entry (tag, hash) it ties together; null when it isn't one.</summary>
    static (ulong App, int Tag, ulong Hash)? Link(Stream f, Foz.Entry e)
    {
        try
        {
            using var doc = JsonDocument.Parse(Foz.Payload(f, e));
            return doc.RootElement.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.Object
                && link.TryGetProperty("application", out var app) && app.ValueKind == JsonValueKind.String && Hex16(app.GetString(), out var appHash)
                && link.TryGetProperty("tag", out var tag) && tag.TryGetInt32(out var t)
                && link.TryGetProperty("hash", out var hash) && hash.ValueKind == JsonValueKind.String && Hex16(hash.GetString(), out var h)
                ? (appHash, t, h) : null;
        }
        catch (Exception x) when (x is InvalidDataException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Drops from the work archive the pipelines already compiled (<paramref name="compiled"/>: their keys) and
    /// nothing a remaining one needs; returns how many went.</summary>
    public static long LeaveOut(string workFoz, IReadOnlySet<string> compiled)
    {
        var pipelines = Pipelines(workFoz);
        var keep = Closure(pipelines, pipelines.Where(p => !compiled.Contains(Key(p.Name))).Select(p => p.Name).ToHashSet(StringComparer.Ordinal));
        var gone = pipelines.Count - keep.Count;
        if (gone == 0) return 0;
        var tmp = workFoz + ".tmp";
        try
        {
            Write(workFoz, tmp, e => !Foz.IsPipeline(e.Tag) || keep.Contains(e.Name));
            File.Move(tmp, workFoz, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
        return gone;
    }

    /// <summary>The keys of the work archive's pipelines the replay created: all but those in the <paramref name="failed"/>
    /// files (20-byte keys).</summary>
    public static HashSet<string> Created(string workFoz, IEnumerable<string> failed)
    {
        var notCreated = failed.Where(File.Exists).SelectMany(p => File.ReadAllBytes(p).Chunk(20)).Where(c => c.Length == 20)
            .Select(c => PsoDb.Hex(c)).ToHashSet(StringComparer.Ordinal);
        return Foz.List(workFoz).Where(e => Foz.IsPipeline(e.Tag)).Select(e => Key(e.Name)).Where(k => !notCreated.Contains(k)).ToHashSet();
    }

    static HashSet<string> Closure(List<VkPipeline> pipelines, HashSet<string> wanted)
    {
        var byName = pipelines.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>(wanted.Where(byName.ContainsKey));
        while (todo.TryPop(out var n))
            if (closure.Add(n))
                foreach (var need in byName[n].Needs) todo.Push(need);
        return closure;
    }

    static void Write(string from, string to, Func<Foz.Entry, bool> keep)
    {
        using var f = Foz.OpenShared(from);
        var entries = Foz.List(f);
        using var w = new BufferedStream(File.Create(to), 1 << 20);
        Foz.WriteMagic(w);
        foreach (var e in entries.Where(keep)) Foz.Copy(f, e, w);
    }

    /// <summary>Merges the layer's archives in <paramref name="dir"/> (scskiller.*.foz) into <paramref name="store"/>, through
    /// a copy so the store never reads a file still being written. An archive the layer has let go of is deleted once merged;
    /// one still open (the game runs) is read as it is and left. Returns the new pipelines' names.</summary>
    public static List<string> Import(string dir, string store, IProgress<string>? log = null)
    {
        List<string> added = [];
        if (!Directory.Exists(dir)) return added;
        foreach (var path in Directory.EnumerateFiles(dir, InboxPattern).Order(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var copy = store + ".inbox.tmp";
            try
            {
                FileStream? held = null;
                try
                {
                    held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);   // fails while the layer writes it
                }
                catch (IOException)
                {
                }
                using var inbox = held ?? Foz.OpenShared(path);
                Directory.CreateDirectory(Path.GetDirectoryName(store)!);
                if (inbox.Length > Foz.MaxInbox) throw new InvalidDataException($"over {Foz.MaxInbox >> 30} GB");
                if (inbox.Length > 0)
                {
                    using (var c = File.Create(copy)) inbox.CopyTo(c);
                    using var from = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.None);
                    added.AddRange(Foz.Merge(store, from).Where(e => Foz.IsPipeline(e.Tag)).Select(e => e.Name));
                }
                if (held != null && !SteamPipelineCache.Owned(path)) File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                log?.Report($"could not import {path}: {e.Message}");
            }
            finally
            {
                File.Delete(copy);
            }
        }
        return added;
    }
}
