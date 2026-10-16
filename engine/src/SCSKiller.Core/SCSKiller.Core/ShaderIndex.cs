using System;
using System.Collections.Generic;

namespace SCSKiller.Core;

public sealed record ShaderIndex(string ContentHash, IReadOnlyList<string> Platforms, IReadOnlyDictionary<string, ShaderInfo> Shaders, IReadOnlyList<ShaderMap> Maps, IReadOnlyDictionary<string, byte[]>? RootSignatures = null)
{
	public const string EmptyContentHash = "da39a3ee5e6b4b0d3255bfef95601890afd80709";

	public static bool IsEmpty(string? contentHash)
	{
		return string.Equals(contentHash, "da39a3ee5e6b4b0d3255bfef95601890afd80709", StringComparison.OrdinalIgnoreCase);
	}
}
