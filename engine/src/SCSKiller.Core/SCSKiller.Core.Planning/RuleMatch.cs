using System.Text.Json.Serialization;

namespace SCSKiller.Core.Planning;

public sealed record RuleMatch(string Family, string? From = null, string? To = null, string? Fork = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string[]? Games = null);
