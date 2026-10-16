using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.SantaMonica;

public sealed class Lz4Frame : IDisposable
{
	private const uint Magic = 407708164u;

	private const long MaxLength = 17179869184L;

	private const int MaxRatio = 300;

	private readonly SafeFileHandle h;

	private readonly int blockSize;

	private readonly long[] at;

	private readonly int[] stored;

	private (int Block, byte[] Data) last = (Block: -1, Data: Array.Empty<byte>());

	public long Length { get; private set; }

	private Lz4Frame(SafeFileHandle h, int blockSize, long[] at, int[] stored)
	{
		this.h = h;
		this.blockSize = blockSize;
		this.at = at;
		this.stored = stored;
	}

	public static Lz4Frame? Open(string path)
	{
		SafeFileHandle safeFileHandle;
		try
		{
			safeFileHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
		try
		{
			Lz4Frame lz4Frame = Parse(safeFileHandle);
			if (lz4Frame != null)
			{
				return lz4Frame;
			}
		}
		catch (Exception ex2) when ((ex2 is InvalidDataException || ex2 is IOException || ex2 is ArgumentException || ex2 is OverflowException) ? true : false)
		{
		}
		safeFileHandle.Dispose();
		return null;
	}

	private static (int First, int BlockSize, bool Checksums, long? ContentSize)? Header(ReadOnlySpan<byte> b)
	{
		if (b.Length < 7 || BinaryPrimitives.ReadUInt32LittleEndian(b) != 407708164)
		{
			return null;
		}
		byte b2 = b[4];
		byte b3 = b[5];
		if (b2 >> 6 != 1 || (b2 & 0x20) == 0 || (b2 & 1) != 0 || ((b3 >> 4) & 7) < 4)
		{
			return null;
		}
		bool flag = (b2 & 8) != 0;
		int num = 6 + (flag ? 8 : 0) + 1;
		if (b.Length < num + 4)
		{
			return null;
		}
		return (num, 1 << 2 * ((b3 >> 4) & 7) + 8, (b2 & 0x10) != 0, flag ? new long?((long)BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(6))) : ((long?)null));
	}

	private static Lz4Frame? Parse(SafeFileHandle h)
	{
		long length = RandomAccess.GetLength(h);
		byte[] array = new byte[19];
		int length2 = RandomAccess.Read(h, array, 0L);
		(int, int, bool, long?)? tuple = Header(array.AsSpan(0, length2));
		if (tuple.HasValue)
		{
			(int, int, bool, long?) valueOrDefault = tuple.GetValueOrDefault();
			int item = valueOrDefault.Item1;
			int item2 = valueOrDefault.Item2;
			bool item3 = valueOrDefault.Item3;
			long? item4 = valueOrDefault.Item4;
			List<long> list = new List<long>();
			List<int> list2 = new List<int>();
			Span<byte> span = stackalloc byte[4];
			long num = item;
			while (true)
			{
				if (RandomAccess.Read(h, span, num) != 4)
				{
					return null;
				}
				uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(span);
				if (num2 == 0)
				{
					break;
				}
				int num3 = (int)(num2 & 0x7FFFFFFF);
				if (num3 == 0 || num3 > item2 || num + 4 + num3 > length || (long)(list.Count + 1) * (long)item2 > 17179869184L)
				{
					return null;
				}
				list.Add(num + 4);
				list2.Add(((num2 & 0x80000000u) != 0) ? (-num3) : num3);
				num += 4 + num3 + (item3 ? 4 : 0);
			}
			if (list.Count == 0)
			{
				return null;
			}
			for (int i = 0; i < list.Count - 1; i++)
			{
				if ((list2[i] < 0) ? (-list2[i] != item2) : ((long)list2[i] * 300L < item2))
				{
					return null;
				}
			}
			Lz4Frame lz4Frame = new Lz4Frame(h, item2, list.ToArray(), list2.ToArray());
			lz4Frame.Length = (long)(list.Count - 1) * (long)item2 + lz4Frame.Block(list.Count - 1, exact: false).Length;
			if (item4.HasValue)
			{
				long valueOrDefault2 = item4.GetValueOrDefault();
				if (valueOrDefault2 != lz4Frame.Length)
				{
					return null;
				}
			}
			return lz4Frame;
		}
		return null;
	}

	public static byte[]? First(string path, long budget, ref long read)
	{
		try
		{
			using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
			byte[] array = new byte[23];
			int num = RandomAccess.Read(handle, array, 0L);
			read += num;
			(int, int, bool, long?)? tuple = Header(array.AsSpan(0, num));
			if (tuple.HasValue)
			{
				var (num2, num3, _, _) = tuple.GetValueOrDefault();
				if (num >= num2 + 4)
				{
					uint num4 = BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(num2));
					int num5 = (int)(num4 & 0x7FFFFFFF);
					if (num4 == 0 || num5 > num3 || num5 > budget - num)
					{
						return null;
					}
					byte[] array2 = new byte[num5];
					if (RandomAccess.Read(handle, array2, num2 + 4) != num5)
					{
						return null;
					}
					read += num5;
					if ((num4 & 0x80000000u) != 0)
					{
						return array2;
					}
					byte[] array3 = new byte[num3];
					int num6 = LZ4Codec.Decode(array2, array3);
					return (num6 > 0) ? array3[..num6] : null;
				}
			}
			return null;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public byte[] Read(long from, int count)
	{
		if (from < 0 || count < 0 || from + count > Length)
		{
			throw new InvalidDataException("past the frame's end");
		}
		byte[] array = new byte[count];
		int num4;
		for (long num = from; num < from + count; num += num4)
		{
			int num2 = (int)(num / blockSize);
			byte[] array2 = Block(num2, exact: true);
			int num3 = (int)(num - (long)num2 * (long)blockSize);
			num4 = (int)Math.Min(array2.Length - num3, from + count - num);
			array2.AsSpan(num3, num4).CopyTo(array.AsSpan((int)(num - from)));
		}
		return array;
	}

	private byte[] Block(int k, bool exact)
	{
		if (last.Block == k)
		{
			return last.Data;
		}
		byte[] array = new byte[Math.Abs(stored[k])];
		if (RandomAccess.Read(h, array, at[k]) != array.Length)
		{
			throw new InvalidDataException("frame truncated");
		}
		byte[] array2;
		if (stored[k] < 0)
		{
			array2 = array;
		}
		else
		{
			byte[] array3 = new byte[blockSize];
			int num = LZ4Codec.Decode(array, array3);
			if (num <= 0)
			{
				throw new InvalidDataException("an LZ4 block doesn't decompress");
			}
			array2 = ((num == blockSize) ? array3 : array3[..num]);
		}
		if (exact && k < at.Length - 1 && array2.Length != blockSize)
		{
			throw new InvalidDataException("an LZ4 block before the last isn't full");
		}
		last = (Block: k, Data: array2);
		return array2;
	}

	public void Dispose()
	{
		h.Dispose();
	}
}
