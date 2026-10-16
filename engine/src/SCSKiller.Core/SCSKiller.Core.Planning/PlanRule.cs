using System.Text.Json.Serialization;

namespace SCSKiller.Core.Planning;

public sealed record PlanRule(string Id, string[] Requires, RuleMatch Match, RootSigRule? RootSignature = null, string[][]? StageSets = null, RtRule? Rt = null, bool Verified = false, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] AgsRule? Ags = null);
