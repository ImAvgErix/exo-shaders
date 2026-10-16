using System.Text.Json.Serialization;

namespace SCSKiller.Core.Planning;

public sealed record RtRule(string Note, uint Flags = 0u, uint Depth = 0u, uint Payload = 0u, uint Attributes = 0u, DescData? Global = null, SamplerSet? GlobalSamplers = null, DescData? Local = null, string[]? Select = null, bool Config = true, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] NvSlot? Nv = null);
