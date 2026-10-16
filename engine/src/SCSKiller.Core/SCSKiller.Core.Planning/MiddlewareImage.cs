using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Planning;

public sealed record MiddlewareImage(string Path, string ContentHash, long Size, IReadOnlyDictionary<string, (long Offset, int Size)> Containers)
{
	public IReadOnlyDictionary<string, (uint Min, uint Max)>? Lanes { get; init; }

	public byte[]? Read(string sha1)
	{
		if (!Containers.TryGetValue(sha1, out (long, int) value))
		{
			return null;
		}
		byte[] array = new byte[value.Item2];
		using SafeFileHandle handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
		return (RandomAccess.Read(handle, array, value.Item1) == array.Length && PsoDb.Hex(SHA1.HashData(array)) == sha1) ? array : null;
	}

	public bool Runs(string sha1, bool amd)
	{
		(uint, uint)? tuple;
		if (Lanes != null)
		{
			tuple = (Lanes.TryGetValue(sha1, out (uint, uint) value) ? new (uint, uint)?(value) : (((uint, uint)?)null));
		}
		else
		{
			byte[] array = Read(sha1);
			tuple = ((array != null) ? Dxbc.WaveLanes(array) : (((uint, uint)?)null));
		}
		(uint, uint)? tuple2 = tuple;
		if (tuple2.HasValue)
		{
			(uint, uint) valueOrDefault = tuple2.GetValueOrDefault();
			if (valueOrDefault.Item1 > 32 || valueOrDefault.Item2 < 32)
			{
				if (amd && valueOrDefault.Item1 <= 64)
				{
					return valueOrDefault.Item2 >= 64;
				}
				return false;
			}
		}
		return true;
	}
}
