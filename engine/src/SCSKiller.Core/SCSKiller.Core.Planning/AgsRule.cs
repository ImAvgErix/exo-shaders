using System.Collections.Generic;

namespace SCSKiller.Core.Planning;

public sealed record AgsRule(Dictionary<string, string>? Profiles = null, string[]? Proven = null);
