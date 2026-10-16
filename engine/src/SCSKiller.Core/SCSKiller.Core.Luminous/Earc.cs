using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.Luminous;

public static class Earc
{
	public sealed record Entry(string Uri, uint Flags, long Offset, int Size, int Stored, ushort Key, int ChunkSize);

	internal sealed class Mask(ulong archiveHash)
	{
		private const ulong Basis = 14695981039346656037uL;

		private const ulong Prime = 1099511628211uL;

		private ulong sub = 0xCBF29CE484222325uL ^ archiveHash;

		public void Apply(ulong hash, ref uint size, ref uint stored, ref ulong offset)
		{
			ulong num = (sub * 1099511628211L) ^ hash;
			size ^= (uint)(int)(num >> 32);
			stored ^= (uint)(int)num;
			sub = (num * 1099511628211L) ^ ~hash;
			offset ^= sub;
		}
	}

	public const uint Magic = 1178686019u;

	public const uint Version = 196628u;

	private const uint ProtectedBit = 2147483648u;

	private const uint Compressed = 2u;

	private const int HeaderSize = 64;

	private const int EntrySize = 40;

	private const int MaxEntries = 4194304;

	private const int MaxUris = 268435456;

	private const int MaxChunk = 67108864;

	private const int MaxUri = 1024;

	private const int MaxFoundUris = 67108864;

	public const int MaxEntry = 67108864;

	public static uint? VersionOf(string path)
	{
		try
		{
			using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
			Span<byte> span = stackalloc byte[8];
			return (RandomAccess.Read(handle, span, 0L) == 8 && BinaryPrimitives.ReadUInt32LittleEndian(span) == 1178686019) ? new uint?(BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4)) & 0x7FFFFFFF) : ((uint?)null);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public static List<Entry>? Entries(SafeFileHandle h, string suffix)
	{
		long length = RandomAccess.GetLength(h);
		byte[] array = new byte[64];
		if (length < 64 || RandomAccess.Read(h, array, 0L) != 64 || U32(array, 0) != 1178686019)
		{
			return null;
		}
		uint num = U32(array, 4);
		if ((num & 0x7FFFFFFF) != 196628)
		{
			return null;
		}
		uint num2 = U32(array, 8);
		uint num3 = U32(array, 16);
		uint num4 = U32(array, 20);
		uint num5 = U32(array, 24);
		long num6 = (long)U32(array, 36) * 1024L;
		bool flag = num2 > 4194304 || num3 + (long)num2 * 40L > length || num4 > num5 || num5 > length || num5 - num4 > 268435456;
		if (!flag)
		{
			bool flag2 = ((num6 > 67108864 || num6 == 0L) ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			throw new InvalidDataException("archive header out of range");
		}
		byte[] array2 = new byte[num2 * 40];
		byte[] array3 = new byte[num5 - num4];
		if (RandomAccess.Read(h, array2, num3) != array2.Length || RandomAccess.Read(h, array3, num4) != array3.Length)
		{
			throw new InvalidDataException("archive tables cut short");
		}
		Mask mask = (((num & 0x80000000u) != 0) ? new Mask(BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(40))) : null);
		ReadOnlySpan<byte> value = Encoding.UTF8.GetBytes(suffix);
		List<Entry> list = new List<Entry>();
		long num7 = 0L;
		for (int i = 0; i < num2; i++)
		{
			Span<byte> span = array2.AsSpan(i * 40, 40);
			ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(span);
			uint size = U32(span, 8);
			uint stored = U32(span, 12);
			uint num8 = U32(span, 20);
			ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(24));
			mask?.Apply(hash, ref size, ref stored, ref offset);
			if (num8 < num4 || num8 >= num5)
			{
				continue;
			}
			int num9 = (int)(num8 - num4);
			ReadOnlySpan<byte> span2 = array3.AsSpan(num9, Math.Min(1024, array3.Length - num9));
			int num10 = span2.IndexOf((byte)0);
			if (num10 >= 0 && span2.Slice(0, num10).EndsWith(value))
			{
				if (size > 67108864 || stored > 67108864 || stored > length || offset > (ulong)(length - stored))
				{
					throw new InvalidDataException($"entry {i} out of range");
				}
				if ((num7 += num10) > 67108864)
				{
					throw new InvalidDataException("archive URIs over budget");
				}
				list.Add(new Entry(Encoding.UTF8.GetString(span2.Slice(0, num10)), U32(span, 16), (long)offset, (int)size, (int)stored, BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(38)), (int)num6));
			}
		}
		return list;
	}

	public static byte[] Read(SafeFileHandle h, Entry e)
	{
		byte[] array = new byte[e.Stored];
		if (RandomAccess.Read(h, array, e.Offset) != array.Length)
		{
			throw new InvalidDataException(e.Uri + ": cut short");
		}
		if ((e.Flags & 2) == 0)
		{
			if (e.Size > array.Length)
			{
				throw new InvalidDataException(e.Uri + ": cut short");
			}
			return array[..e.Size];
		}
		byte[] array2 = new byte[e.Size];
		int num = 0;
		int num2 = 0;
		while (num < array2.Length)
		{
			if (num2 > array.Length - 8)
			{
				throw new InvalidDataException(e.Uri + ": chunk header past the end");
			}
			uint num3 = U32(array, num2);
			uint num4 = U32(array, num2 + 4);
			if (num2 == 0 && e.Key != 0)
			{
				ulong num5 = ChunkKey(e.Key);
				num3 ^= (uint)(int)(num5 >> 32);
				num4 ^= (uint)(int)num5;
			}
			num2 += 8;
			if (num4 == 0 || num4 > e.ChunkSize || num4 > array2.Length - num || num3 > array.Length - num2)
			{
				throw new InvalidDataException(e.Uri + ": chunk out of range");
			}
			try
			{
				using ZLibStream zLibStream = new ZLibStream(new MemoryStream(array, num2, (int)num3), CompressionMode.Decompress);
				zLibStream.ReadExactly(array2.AsSpan(num, (int)num4));
				if (zLibStream.ReadByte() >= 0)
				{
					throw new InvalidDataException(e.Uri + ": chunk decompresses past its length");
				}
			}
			catch (EndOfStreamException)
			{
				throw new InvalidDataException(e.Uri + ": chunk decompresses short");
			}
			num += (int)num4;
			num2 += (int)num3;
			num2 = (num2 + 3) & -4;
		}
		return array2;
	}

	internal static ulong ChunkKey(ushort key)
	{
		return Step(Step(key));
		static ulong Step(ulong x)
		{
			return x * 1103515245 + 12345;
		}
	}

	private static uint U32(ReadOnlySpan<byte> b, int at)
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at));
	}
}
