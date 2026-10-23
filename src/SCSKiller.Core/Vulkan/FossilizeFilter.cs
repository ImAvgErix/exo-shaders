using System.Text;
using System.Text.Json;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Vulkan;

/// <summary>Steam's fossilize_engine_filters.json (in Steam's folder): which application info fields and device features
/// split a game's pipeline cache into buckets (steamapprun_pipeline_cache.&lt;hash&gt;). <see cref="Hash"/> computes a bucket's
/// hash the way Fossilize's application info filter does, so <see cref="Matches"/> can tell whether a bucket was recorded on
/// this GPU's vendor.</summary>
public sealed class FossilizeFilter
{
    sealed record Rule(string[] Variants, string[] FeatureVariants, uint[] AppDeltas, uint[] EngineDeltas)
    {
        public bool Buckets => Variants.Length + FeatureVariants.Length > 0;
        public bool Known => Variants.Concat(FeatureVariants).All(KnownVariants.Contains);
        public bool NamesVendor => Variants.Concat(FeatureVariants).Contains("VendorID");
    }

    /// <summary>The device features a bucket can depend on.</summary>
    public readonly record struct Features(bool BindlessUbo, bool Mutable, bool BufferDeviceAddress, bool FsrAttachment, bool FsrPipeline,
        bool FsrPrimitive, bool DynamicRendering, bool DescriptorBuffer, bool DescriptorBufferPush, bool DescriptorHeap)
    {
        /// <summary>Every combination (1024).</summary>
        public static IEnumerable<Features> All() =>
            Enumerable.Range(0, 1 << 10).Select(b => new Features((b & 1) != 0, (b & 2) != 0, (b & 4) != 0, (b & 8) != 0, (b & 0x10) != 0,
                (b & 0x20) != 0, (b & 0x40) != 0, (b & 0x80) != 0, (b & 0x100) != 0, (b & 0x200) != 0));
    }

    /// <summary>Fossilize's Hasher: FNV-1 over u32s.</summary>
    sealed class Hasher
    {
        public ulong Value { get; private set; } = 0xcbf29ce484222325;

        public void U32(uint v) => Value = (Value * 0x100000001b3UL) ^ v;

        public void String(string s)
        {
            U32(0xff);
            foreach (var b in Encoding.UTF8.GetBytes(s)) U32(b);
        }
    }

    public const string FileName = "fossilize_engine_filters.json";
    const int MaxFile = 1 << 20;

    static readonly HashSet<string> KnownVariants = new(StringComparer.Ordinal)
    {
        "VendorID", "MutableDescriptorType", "BindlessUBO", "BufferDeviceAddress", "ApplicationVersion", "EngineVersion",
        "ApplicationVersionMajor", "ApplicationVersionMinor", "ApplicationVersionPatch", "EngineVersionMajor", "EngineVersionMinor",
        "EngineVersionPatch", "ApplicationName", "EngineName", "FragmentShadingRate", "DynamicRendering", "DescriptorBuffer", "DescriptorHeap",
    };

    // PCI vendor ids a bucket may have been recorded on: AMD, NVIDIA, Intel, ARM, Qualcomm, Imagination, Apple, Broadcom,
    // Microsoft, Mesa
    static readonly uint[] Vendors = [0x1002, 0x10DE, 0x8086, 0x13B5, 0x5143, 0x1010, 0x106B, 0x14E4, 0x1414, 0x10005];

    readonly Dictionary<string, Rule> _apps = new(StringComparer.Ordinal);
    readonly Dictionary<string, Rule> _engines = new(StringComparer.Ordinal);
    Rule _default = new([], [], [], []);

    /// <summary>The filter in <paramref name="steamRoot"/>; null when it's missing, too big or not one.</summary>
    public static FossilizeFilter? Read(string steamRoot)
    {
        try
        {
            using var f = GameFiles.OpenRegular(Path.Combine(steamRoot, FileName));
            if (f == null || f.Length > MaxFile) return null;
            using var r = new StreamReader(f);
            return Parse(r.ReadToEnd());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Null for anything but a FossilizeApplicationInfoFilter of version 2 or older.</summary>
    public static FossilizeFilter? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("asset", out var asset) || asset.ValueKind != JsonValueKind.String || asset.GetString() != "FossilizeApplicationInfoFilter"
                || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var v) || v > 2) return null;
            var filter = new FossilizeFilter();
            if (!Rules(root, "applicationFilters", filter._apps) || !Rules(root, "engineFilters", filter._engines)) return null;
            if (Names(root, "defaultBucketVariantDependencies") is not { } variants
                || Names(root, "defaultBucketVariantFeatureDependencies") is not { } features) return null;
            filter._default = new Rule(variants, features, [], []);
            return filter;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }

        static bool Rules(JsonElement root, string name, Dictionary<string, Rule> into)
        {
            if (!root.TryGetProperty(name, out var rules)) return true;
            if (rules.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in rules.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) return false;
                if (Names(p.Value, "bucketVariantDependencies") is not { } variants
                    || Names(p.Value, "bucketVariantFeatureDependencies") is not { } features
                    || Versions(p.Value, "applicationVersionBucketDeltas") is not { } appDeltas
                    || Versions(p.Value, "engineVersionBucketDeltas") is not { } engineDeltas) return false;
                into[p.Name] = new Rule(variants, features, appDeltas, engineDeltas);
            }
            return true;
        }

        // Missing = none; null = not a list of that kind
        static string[]? Names(JsonElement o, string name) =>
            !o.TryGetProperty(name, out var a) ? []
            : a.ValueKind == JsonValueKind.Array && a.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
                ? a.EnumerateArray().Select(x => x.GetString()!).ToArray()
                : null;

        static uint[]? Versions(JsonElement o, string name) =>
            !o.TryGetProperty(name, out var a) ? []
            : a.ValueKind == JsonValueKind.Array && a.EnumerateArray().All(x => x.TryGetUInt32(out var d) && d != 0)
                ? a.EnumerateArray().Select(x => x.GetUInt32()).ToArray()
                : null;
    }

    Rule? AppRule(FozApp app) => app.Name != null && _apps.TryGetValue(app.Name, out var r) ? r : null;
    Rule? EngineRule(FozApp app) => app.Engine != null && _engines.TryGetValue(app.Engine, out var r) ? r : null;
    IEnumerable<Rule> Applying(FozApp app) => new[] { AppRule(app), EngineRule(app) }.OfType<Rule>().DefaultIfEmpty(_default);

    /// <summary>The app's cache is split into buckets.</summary>
    public bool Buckets(FozApp app) => Applying(app).Any(r => r.Buckets);

    /// <summary>The bucket hash Fossilize gives this app on a device of <paramref name="vendorId"/> with
    /// <paramref name="features"/>; null when a rule names a variant this doesn't know.</summary>
    public ulong? Hash(FozApp app, uint vendorId, Features features)
    {
        if (!Applying(app).All(r => r.Known)) return null;
        var appRule = AppRule(app);
        var engineRule = EngineRule(app);
        var h = new Hasher();
        h.U32(0);
        if (appRule != null) Variants(h, appRule, app, vendorId, features);
        h.U32(0);
        if (engineRule != null)
        {
            Variants(h, engineRule, app, vendorId, features);
            foreach (var d in engineRule.AppDeltas.Where(d => app.Version >= d)) h.U32(d ^ 0xABBA);
            foreach (var d in engineRule.EngineDeltas.Where(d => app.EngineVersion >= d)) h.U32(d ^ 0xCAFE);
        }
        if (appRule == null && engineRule == null) Variants(h, _default, app, vendorId, features);
        return h.Value;
    }

    /// <summary>Whether <paramref name="bucket"/> is this app's on <paramref name="vendorId"/>'s GPUs: true = some feature set
    /// gives that hash; false = it's another vendor's; null = can't tell (the rules don't name the vendor, or no vendor's
    /// hash matches).</summary>
    public bool? Matches(ulong bucket, FozApp app, uint vendorId)
    {
        if (Gives(vendorId)) return true;
        if (!Applying(app).Any(r => r.NamesVendor) || !Vendors.Where(v => v != vendorId).Any(Gives)) return null;
        return false;

        bool Gives(uint vendor) => Features.All().Any(f => Hash(app, vendor, f) == bucket);
    }

    static void Variants(Hasher h, Rule rule, FozApp app, uint vendorId, Features f)
    {
        foreach (var name in rule.Variants) Variant(h, name, app, vendorId, f, feature: false);
        foreach (var name in rule.FeatureVariants) Variant(h, name, app, vendorId, f, feature: true);
    }

    /// <summary>Hashes one variant as Fossilize does. A feature variant hashes the feature struct's sType first, and only
    /// when the feature is on.</summary>
    static void Variant(Hasher h, string name, FozApp app, uint vendorId, Features f, bool feature)
    {
        switch (name)
        {
            case "VendorID": h.U32(vendorId); break;
            case "ApplicationVersion": h.U32(app.Version); break;
            case "ApplicationVersionMajor": h.U32(app.Version >> 22); break;
            case "ApplicationVersionMinor": h.U32((app.Version >> 12) & 0x3FF); break;
            case "ApplicationVersionPatch": h.U32(app.Version & 0xFFF); break;
            case "EngineVersion": h.U32(app.EngineVersion); break;
            case "EngineVersionMajor": h.U32(app.EngineVersion >> 22); break;
            case "EngineVersionMinor": h.U32((app.EngineVersion >> 12) & 0x3FF); break;
            case "EngineVersionPatch": h.U32(app.EngineVersion & 0xFFF); break;
            case "ApplicationName": h.String(app.Name ?? ""); break;
            case "EngineName": h.String(app.Engine ?? ""); break;
            case "BufferDeviceAddress": Flag(1000257000, f.BufferDeviceAddress); break;
            case "MutableDescriptorType": Flag(1000351000, f.Mutable); break;
            case "DynamicRendering": Flag(1000044003, f.DynamicRendering); break;
            case "DescriptorHeap": Flag(1000135009, f.DescriptorHeap); break;
            case "BindlessUBO":
                if (feature && f.BindlessUbo)
                {
                    h.U32(1000161001);
                    h.U32(10);
                }
                if (!feature || f.BindlessUbo) h.U32(f.BindlessUbo ? 1u : 0);
                break;
            case "FragmentShadingRate":
                var fsr = f.FsrAttachment || f.FsrPipeline || f.FsrPrimitive;
                if (feature && fsr) h.U32(1000226003);
                if (!feature || fsr)
                {
                    h.U32(f.FsrAttachment ? 1u : 0);
                    h.U32(f.FsrPipeline ? 1u : 0);
                    h.U32(f.FsrPrimitive ? 1u : 0);
                }
                break;
            case "DescriptorBuffer":
                var buffer = f.DescriptorBuffer || f.DescriptorBufferPush;
                if (feature && buffer) h.U32(1000316002);
                if (!feature || buffer)
                {
                    h.U32(f.DescriptorBuffer ? 1u : 0);
                    h.U32(f.DescriptorBufferPush ? 1u : 0);
                }
                break;
        }

        void Flag(uint sType, bool on)
        {
            if (feature && on) h.U32(sType);
            if (!feature || on) h.U32(on ? 1u : 0);
        }
    }
}
