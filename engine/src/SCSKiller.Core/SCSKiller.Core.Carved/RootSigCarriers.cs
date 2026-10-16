using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace SCSKiller.Core.Carved;

internal sealed class RootSigCarriers
{
	private readonly Dictionary<string, string> rsOnly = new Dictionary<string, string>();

	private readonly Dictionary<string, string> carrier = new Dictionary<string, string>();

	private readonly Dictionary<string, string> partOf = new Dictionary<string, string>();

	private readonly Dictionary<string, byte[]> parts = new Dictionary<string, byte[]>();

	private readonly Dictionary<string, byte[]> blobs = new Dictionary<string, byte[]>();

	public IReadOnlyDictionary<string, byte[]> Blobs => blobs;

	public void See(ReadOnlySpan<byte> c, string sha, int kind)
	{
		ReadOnlySpan<byte> source = Dxbc.Part(c, "RTS0"u8);
		if (source.IsEmpty)
		{
			return;
		}
		string text = Convert.ToHexStringLower(SHA1.HashData(source));
		if (!parts.TryGetValue(text, out byte[] value))
		{
			value = (parts[text] = source.ToArray());
		}
		if (kind < 0)
		{
			rsOnly.TryAdd(text, sha);
			blobs[sha] = value;
			return;
		}
		if (carrier.TryAdd(text, sha))
		{
			blobs[sha] = value;
		}
		partOf[sha] = text;
	}

	public void Apply(Dictionary<string, ShaderInfo> shaders)
	{
		foreach (var (key, key2) in partOf)
		{
			if (shaders.TryGetValue(key, out ShaderInfo value))
			{
				shaders[key] = value with
				{
					RootSignature = (rsOnly.GetValueOrDefault(key2) ?? carrier[key2])
				};
			}
		}
	}
}
