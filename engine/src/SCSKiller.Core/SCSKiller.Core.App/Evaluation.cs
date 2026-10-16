using System.Collections.Generic;

namespace SCSKiller.Core.App;

public sealed record Evaluation(string Key, EngineInfo? Engine, AntiCheat AntiCheat, PlanCheck Check, string? Clean = null, bool StreamlineFirst = false, IReadOnlyList<string>? StreamlineFiles = null, string? StreamlineStamp = null, string? ApiDetected = null, string? Found = null, string? FoundAt = null);
