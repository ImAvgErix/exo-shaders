using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Planning;

internal sealed class RecordedBlobs : IReadOnlyDictionary<string, byte[]>, IEnumerable<KeyValuePair<string, byte[]>>, IEnumerable, IReadOnlyCollection<KeyValuePair<string, byte[]>>, IDisposable
{
	private readonly Dictionary<string, (byte[]? Bytes, long At, int Length)> blobs = new Dictionary<string, (byte[], long, int)>();

	private FileStream? file;

	public byte[] this[string key] => Bytes(blobs[key]);

	public IEnumerable<string> Keys => blobs.Keys;

	public IEnumerable<byte[]> Values => blobs.Values.Select(Bytes);

	public int Count => blobs.Count;

	public IEnumerable<PsoDb.Rec> Read(string path)
	{
		if (PsoDb.IsCompact(path))
		{
			foreach (PsoDb.Rec item in PsoDb.Read(path))
			{
				if (item.Tag == 'B')
				{
					blobs[PsoDb.Hex(item.Payload.AsSpan(0, 20))] = (item.Payload[20..], 0L, 0);
				}
				else
				{
					yield return item;
				}
			}
			yield break;
		}
		file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576);
		byte[] head = new byte[5];
		while (file.ReadAtLeast(head, 5, throwOnEndOfStream: false) == 5)
		{
			uint num = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1));
			if (head[0] == 66 && num < 20)
			{
				throw new InvalidDataException($"a 'B' record of {num} bytes, short of its SHA-1");
			}
			if (num > file.Length - file.Position)
			{
				break;
			}
			long position = file.Position;
			if (head[0] == 66 && num > 65556)
			{
				byte[] array = new byte[20];
				file.ReadExactly(array);
				file.Seek(num - 20, SeekOrigin.Current);
				blobs[PsoDb.Hex(array)] = (null, position + 20, (int)(num - 20));
				continue;
			}
			byte[] array2 = PsoDb.Body(file, num);
			if (array2 != null)
			{
				if (head[0] != 66)
				{
					yield return new PsoDb.Rec((char)head[0], array2);
				}
				else if (num > 20 && Dxbc.IsRootSignatureOnly(array2.AsSpan(20)))
				{
					blobs[PsoDb.Hex(array2.AsSpan(0, 20))] = (array2[20..], 0L, 0);
				}
				else
				{
					blobs[PsoDb.Hex(array2.AsSpan(0, 20))] = (null, position + 20, (int)(num - 20));
				}
				continue;
			}
			break;
		}
	}

	private byte[] Bytes((byte[]? Bytes, long At, int Length) b)
	{
		if (b.Bytes != null)
		{
			return b.Bytes;
		}
		byte[] array = new byte[b.Length];
		int num;
		for (int i = 0; i < array.Length; i += num)
		{
			num = RandomAccess.Read(file.SafeFileHandle, array.AsSpan(i), b.At + i);
			if (num == 0)
			{
				throw new InvalidDataException(file.Name + " ends inside a blob");
			}
		}
		return array;
	}

	public bool Remove(string sha1)
	{
		return blobs.Remove(sha1);
	}

	public bool ContainsKey(string key)
	{
		return blobs.ContainsKey(key);
	}

	public bool TryGetValue(string key, [MaybeNullWhen(false)] out byte[] value)
	{
		value = (blobs.TryGetValue(key, out (byte[], long, int) value2) ? Bytes(value2) : null);
		return value != null;
	}

	public IEnumerator<KeyValuePair<string, byte[]>> GetEnumerator()
	{
		return blobs.Select((KeyValuePair<string, (byte[] Bytes, long At, int Length)> b) => KeyValuePair.Create(b.Key, Bytes(b.Value))).GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}

	public void Dispose()
	{
		file?.Dispose();
	}
}
