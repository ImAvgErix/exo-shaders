using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

public sealed class CommunityManifest
{
	public const int RecordSize = 80;

	private readonly Dictionary<string, CommunityEntry> entries = new Dictionary<string, CommunityEntry>();

	private readonly Dictionary<string, CommunityEntry> fulls = new Dictionary<string, CommunityEntry>();

	private readonly Dictionary<string, CommunityEntry> packs = new Dictionary<string, CommunityEntry>();

	private readonly Dictionary<string, CommunityEntry> draws = new Dictionary<string, CommunityEntry>();

	private readonly Dictionary<string, string> aliases = new Dictionary<string, string>();

	private readonly Dictionary<string, OfficialEntry> officials = new Dictionary<string, OfficialEntry>();

	public uint Epoch { get; }

	public long Length { get; private init; }

	public bool HasPacks => packs.Count > 0;

	private CommunityManifest(uint epoch)
	{
		Epoch = epoch;
	}

	public static CommunityManifest Parse(ReadOnlySpan<byte> file)
	{
		if (file.Length < 80 || file.Length % 80 != 0 || file[0] != 77 || file[1] != 1)
		{
			throw new InvalidDataException("not a format-1 community manifest");
		}
		CommunityManifest communityManifest = new CommunityManifest(BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(4)))
		{
			Length = file.Length
		};
		for (int i = 80; i < file.Length; i += 80)
		{
			ReadOnlySpan<byte> readOnlySpan = file.Slice(i, 80);
			string text = Convert.ToHexStringLower(readOnlySpan.Slice(4, 20));
			switch ((char)readOnlySpan[0])
			{
			case 'D':
			case 'E':
			case 'P':
			case 'X':
				(readOnlySpan[0] switch
				{
					69 => communityManifest.entries, 
					88 => communityManifest.fulls, 
					80 => communityManifest.packs, 
					_ => communityManifest.draws, 
				})[text] = new CommunityEntry(text, Convert.ToHexStringLower(readOnlySpan.Slice(24, 32)), BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan.Slice(56)), (int)Math.Min(2147483647u, BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan.Slice(60))), BinaryPrimitives.ReadUInt16LittleEndian(readOnlySpan.Slice(2)), readOnlySpan[0] == 88);
				break;
			case 'O':
				if (!readOnlySpan.Slice(24, 32).ContainsAnyExcept((byte)0))
				{
					communityManifest.officials.Remove(text);
				}
				else
				{
					communityManifest.officials[text] = new OfficialEntry(text, Convert.ToHexStringLower(readOnlySpan.Slice(24, 32)), BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan.Slice(56)), (int)Math.Min(2147483647u, BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan.Slice(60))), (OfficialVisibility)readOnlySpan[1]);
				}
				break;
			case 'A':
				communityManifest.aliases[text] = Convert.ToHexStringLower(readOnlySpan.Slice(24, 20));
				break;
			case 'T':
				communityManifest.entries.Remove(text);
				communityManifest.fulls.Remove(text);
				communityManifest.packs.Remove(text);
				communityManifest.draws.Remove(text);
				communityManifest.officials.Remove(text);
				break;
			}
		}
		return communityManifest;
	}

	public static string? AliasKey(Game g)
	{
		string version = g.Version;
		if (version == null || version.Length <= 0)
		{
			return null;
		}
		return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(g.Id + "@" + version)));
	}

	public CommunityEntry? FindPack(string packKey)
	{
		return packs.GetValueOrDefault(HashOnly.PackHash(packKey));
	}

	public CommunityEntry? FindDraws(string contentHash)
	{
		if (!ShaderIndex.IsEmpty(contentHash))
		{
			return draws.GetValueOrDefault(HashOnly.PackHash(HashOnly.DrawsKey(contentHash)));
		}
		return null;
	}

	public CommunityEntry? Find(Game g, string? contentHash, bool full = false)
	{
		return (full ? Lookup(fulls, g, contentHash) : null) ?? Lookup(entries, g, contentHash);
	}

	public OfficialEntry? FindOfficial(Game g, string? contentHash)
	{
		return Lookup(officials, g, contentHash);
	}

	private T? Lookup<T>(Dictionary<string, T> by, Game g, string? contentHash) where T : class
	{
		object obj = contentHash;
		if (obj == null)
		{
			string text = AliasKey(g);
			obj = ((text != null && aliases.TryGetValue(text, out string value)) ? value : null);
		}
		string text2 = (string)obj;
		if (text2 == null || ShaderIndex.IsEmpty(text2))
		{
			return null;
		}
		return by.GetValueOrDefault(text2);
	}
}
