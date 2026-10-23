using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SCSKiller.Core.Vulkan;

/// <summary>SCSKiller's Vulkan recorder: a Fossilize layer (VkLayer_scskiller.dll, in the data folder's vulkan-layer) with one
/// implicit layer manifest per game, registered under HKCU\SOFTWARE\Khronos\Vulkan\ImplicitLayers. A manifest turns its layer
/// on only in a process whose SteamAppId is the game's (the variable Steam sets when it starts a game), so only Steam
/// games can be recorded. The layer writes scskiller.*.foz archives and a scskiller_creates.csv log into the recorder's
/// inbox (<see cref="Inbox"/>); <see cref="VulkanPlan.Import"/> takes them in.</summary>
public static class VulkanLayer
{
    public const string Folder = "vulkan-layer";
    public const string Dll = "VkLayer_scskiller.dll";
    const string ManifestPrefix = "VkLayer_scskiller_", ManifestSuffix = ".json";

    internal static string RegistryKey { get; set; } = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";   // settable for tests

    /// <summary>The Steam app id of a Steam game; null for any other game.</summary>
    public static string? AppId(Game g) =>
        g.Store == Store.Steam && g.Id.StartsWith("steam:", StringComparison.Ordinal) && g.Id["steam:".Length..] is { Length: > 0 } id && id.All(char.IsAsciiDigit)
            ? id : null;

    public static string LayerName(string appId) => $"VK_LAYER_SCSKILLER_{appId}";
    public static string ManifestName(string appId) => $"{ManifestPrefix}{appId}{ManifestSuffix}";
    public static string Inbox(string recorderDir, string fileKey) => Path.Combine(recorderDir, "vulkan", fileKey);

    /// <summary>The implicit layer manifest for one game: on with SteamAppId=<paramref name="appId"/>, off with
    /// DISABLE_VK_LAYER_SCSKILLER_&lt;appid&gt;=1.</summary>
    public static string Manifest(string appId, string dllPath)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("file_format_version", "1.0.0");
            w.WriteStartObject("layer");
            w.WriteString("name", LayerName(appId));
            w.WriteString("type", "GLOBAL");
            w.WriteString("library_path", dllPath);
            w.WriteString("api_version", "1.4.303");
            w.WriteString("implementation_version", "1");
            w.WriteString("description", "SCSKiller's Vulkan recorder");
            w.WriteStartObject("functions");
            w.WriteString("vkGetInstanceProcAddr", "VK_LAYER_fossilize_GetInstanceProcAddr");
            w.WriteString("vkGetDeviceProcAddr", "VK_LAYER_fossilize_GetDeviceProcAddr");
            w.WriteEndObject();
            w.WriteStartObject("enable_environment");
            w.WriteString("SteamAppId", appId);
            w.WriteEndObject();
            w.WriteStartObject("disable_environment");
            w.WriteString($"DISABLE_{LayerName(appId)}", "1");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Other registered manifests of this game's layer that exist (another SCSKiller install's).</summary>
    public static List<string> Others(string appId, string manifest) =>
        Values().Where(v => Path.GetFileName(v).Equals(ManifestName(appId), StringComparison.OrdinalIgnoreCase) && !Same(v, manifest) && File.Exists(v)).ToList();

    public static bool Registered(string manifest) => Values().Any(v => Same(v, manifest));

    /// <summary>Registers the manifest as an enabled implicit layer (value 0) for this user.</summary>
    public static void Register(string manifest)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKey);
        key.SetValue(manifest, 0, RegistryValueKind.DWord);
    }

    public static void Unregister(string manifest)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
        if (key != null && key.GetValueNames().Any(v => Same(v, manifest))) key.DeleteValue(manifest, throwOnMissingValue: false);
    }

    /// <summary>Unregisters every manifest in <paramref name="folder"/>; returns them.</summary>
    public static List<string> UnregisterUnder(string folder)
    {
        var under = Under(folder);
        foreach (var m in under) Unregister(m);
        return under;
    }

    /// <summary>The registered manifests in <paramref name="folder"/> or below.</summary>
    public static List<string> Under(string folder)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Values().Where(v =>
        {
            try
            {
                return Path.GetFullPath(v).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }).ToList();
    }

    /// <summary>The app id a manifest of SCSKiller's is named for; null for any other file.</summary>
    public static string? ManifestAppId(string manifest)
    {
        var name = Path.GetFileName(manifest);
        return name.StartsWith(ManifestPrefix, StringComparison.Ordinal) && name.EndsWith(ManifestSuffix, StringComparison.Ordinal)
            && name[ManifestPrefix.Length..^ManifestSuffix.Length] is { Length: > 0 } id && id.All(char.IsAsciiDigit) ? id : null;
    }

    /// <summary>The driver cache variables the game ran with in its last session of the layer's log (<c>#cache,NAME=value</c>
    /// lines after the last <c>#session,</c>): NVIDIA's __GL_SHADER_DISK_CACHE* and AMD's pipeline cache ones, which Steam
    /// sets to point the driver at its own cache. Null when that session logged none.</summary>
    public static Dictionary<string, string>? CacheEnv(string csv)
    {
        const string Session = "#session,", Cache = "#cache,";
        Dictionary<string, string>? env = null;
        var inSession = false;
        using var r = new StreamReader(Foz.OpenShared(csv));
        while (r.ReadLine() is { } line)
        {
            if (line.StartsWith(Session, StringComparison.Ordinal))
            {
                env = null;
                inSession = true;
            }
            else if (inSession && line.StartsWith(Cache, StringComparison.Ordinal))
            {
                var eq = line.IndexOf('=', Cache.Length);
                if (eq > Cache.Length && CacheVariable(line[Cache.Length..eq]) && line.Length < 4096 && !line.Any(char.IsControl))
                    (env ??= new(StringComparer.OrdinalIgnoreCase))[line[Cache.Length..eq]] = line[(eq + 1)..];
            }
        }
        return env;
    }

    /// <summary>The Vulkan device of the last session of the layer's log (<c>#vkdevice,</c> line): "extensions,,features",
    /// space-separated VK_ names and sType:bits pairs; null when that session logged none or a malformed one.</summary>
    public static string? Device(string csv)
    {
        const string Session = "#session,", VkDevice = "#vkdevice,";
        string? device = null;
        var inSession = false;
        using var r = new StreamReader(Foz.OpenShared(csv));
        while (r.ReadLine() is { } line)
        {
            if (line.StartsWith(Session, StringComparison.Ordinal))
            {
                device = null;
                inSession = true;
            }
            else if (inSession && line.StartsWith(VkDevice, StringComparison.Ordinal)) device = DeviceOf(line[VkDevice.Length..]);
        }
        return device;
    }

    static string? DeviceOf(string fields)
    {
        if (fields.Length > 16384 || fields.Split(',') is not [var extensionList, _, var featureList]) return null;
        var extensions = extensionList.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var features = featureList.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!extensions.All(n => n.StartsWith("VK_", StringComparison.Ordinal) && n.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            || !features.All(f => f.Split(':') is [{ Length: > 0 and < 11 } sType, var bits] && sType.All(char.IsAsciiDigit)
                && (bits == "?" || bits.Length > 0 && bits.All(c => c is '0' or '1')))) return null;
        return $"{string.Join(' ', extensions)},,{string.Join(' ', features)}";
    }

    static bool CacheVariable(string name)
    {
        var n = name.ToUpperInvariant();
        return n.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
            && (n.StartsWith("__GL_SHADER_DISK_CACHE", StringComparison.Ordinal) || n.StartsWith("AMD_VK_PIPELINE_CACHE_", StringComparison.Ordinal)
                || n == "AMD_VK_USE_PIPELINE_CACHE");
    }

    static bool Same(string value, string manifest) => value.Equals(manifest, StringComparison.OrdinalIgnoreCase);

    static string[] Values()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
        return key?.GetValueNames() ?? [];
    }
}
