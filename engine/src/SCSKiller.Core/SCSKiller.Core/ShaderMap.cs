using System.Collections.Generic;

namespace SCSKiller.Core;

public sealed record ShaderMap(string Hash, string Library, string Platform, IReadOnlyList<string> Shaders, bool IsPipeline = false, string? RootSignature = null);
