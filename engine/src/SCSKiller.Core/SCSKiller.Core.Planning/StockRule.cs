using System.Text.Json.Serialization;

namespace SCSKiller.Core.Planning;

public sealed record StockRule(string[] Stages, uint MaxSrvs, uint MaxSamplers, string[] UavStages, bool TablesWhenUsed = false, bool LayoutNeedsVertexInput = false, bool GrowSrvs = false, bool Ags = false, bool RootConstants = false, bool Diagnostic = false, bool HeapFlags = false, bool NvExtension = false, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DenyMesh = false);
