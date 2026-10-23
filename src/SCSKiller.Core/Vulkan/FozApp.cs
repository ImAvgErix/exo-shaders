using System.Text.Json;

namespace SCSKiller.Core.Vulkan;

/// <summary>A Fossilize application info entry (tag 0): the VkApplicationInfo a game created its instance with.</summary>
public sealed record FozApp(ulong Hash, string? Name, string? Engine, uint Version, uint EngineVersion)
{
    /// <summary>The entry's JSON (up to its first NUL); null when it isn't an application info.</summary>
    public static FozApp? Parse(ulong hash, byte[] payload)
    {
        try
        {
            var end = Array.IndexOf(payload, (byte)0);
            using var doc = JsonDocument.Parse(payload.AsMemory(0, end >= 0 ? end : payload.Length));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("applicationInfo", out var info)
                || info.ValueKind != JsonValueKind.Object) return null;
            return new FozApp(hash, Text(info, "applicationName"), Text(info, "engineName"), Number(info, "applicationVersion"), Number(info, "engineVersion"));
        }
        catch (JsonException)
        {
            return null;
        }

        static uint Number(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n) ? n : 0;

        static string? Text(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
