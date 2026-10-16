using System.Text.Json.Serialization;

namespace SCSKiller.Core.App;

public sealed record UploadMeta(string StoreBuildKey, string ContentHash, string? Engine = null, string? Vendor = null, string? AppVersion = null, UploadDll[]? Middleware = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RsRule = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? BuiltOk = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? BuiltN = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PlannerVersion = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DriverVersion = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GpuModel = null);
