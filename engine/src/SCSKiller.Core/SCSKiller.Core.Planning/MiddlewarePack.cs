using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Planning;

public sealed class MiddlewarePack
{
	public sealed record PackHeader(string Vendor, string Dll, string ContentHash, long Size, List<string> Sources, string? Object = null, string? Gpu = null);

	private const int Version = 1;

	public PackHeader Header { get; private set; }

	public List<PsoDb.Rec> Entries { get; } = new List<PsoDb.Rec>();

	public Dictionary<string, byte[]> RootSignatures { get; } = new Dictionary<string, byte[]>();

	public MiddlewarePack(string vendor, string dll, string contentHash, long size, string? obj = null, string? gpu = null)
	{
		Header = new PackHeader(vendor, dll, contentHash, size, new List<string>(), obj, gpu);
	}

	public void Adopt(string gpu)
	{
		Header = Header with
		{
			Gpu = gpu
		};
	}

	public static string FileName(string dll, string contentHash)
	{
		return dll.ToLowerInvariant() + "-" + contentHash + ".pack";
	}

	public bool Add(PsoDb.Rec r, string source)
	{
		if (Entries.Any((PsoDb.Rec e) => e.Key == r.Key))
		{
			return false;
		}
		Entries.Add(r);
		if (!Header.Sources.Contains(source))
		{
			Header.Sources.Add(source);
		}
		return true;
	}

	public void Write(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		string text = $"{path}.{Guid.NewGuid():N}.tmp";
		using (FileStream fileStream = File.Create(text))
		{
			PlanFile.WriteHeader(fileStream, "SCSKPACK"u8, 1, Header);
			foreach (var (sha, array2) in RootSignatures.OrderBy((KeyValuePair<string, byte[]> r) => r.Key, StringComparer.Ordinal))
			{
				PsoDb.WriteBlob(fileStream, sha, array2);
			}
			foreach (PsoDb.Rec entry in Entries)
			{
				PsoDb.Write(fileStream, entry.Tag, entry.Payload);
			}
		}
		File.Move(text, path, overwrite: true);
		KeyFiles.Forget(path);
	}

	public static PackHeader? ReadHeader(string path)
	{
		try
		{
			using FileStream f = File.OpenRead(path);
			return PlanFile.ReadHeader<PackHeader>(f, "SCSKPACK"u8, 1, $"{path}: not a v{1} pack");
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public static MiddlewarePack Read(string path)
	{
		using FileStream f = File.OpenRead(path);
		PackHeader packHeader = PlanFile.ReadHeader<PackHeader>(f, "SCSKPACK"u8, 1, $"{path}: not a v{1} pack");
		MiddlewarePack middlewarePack = new MiddlewarePack(packHeader.Vendor, packHeader.Dll, packHeader.ContentHash, packHeader.Size, packHeader.Object, packHeader.Gpu);
		middlewarePack.Header.Sources.AddRange(packHeader.Sources);
		foreach (PsoDb.Rec item in PsoDb.Read(f))
		{
			if (item.Tag == 'B')
			{
				middlewarePack.RootSignatures[PsoDb.Hex(item.Payload.AsSpan(0, 20))] = item.Payload[20..];
				continue;
			}
			char tag = item.Tag;
			if ((tag == 'C' || tag == 'G' || tag == 'S') ? true : false)
			{
				middlewarePack.Entries.Add(item);
			}
		}
		return middlewarePack;
	}
}
