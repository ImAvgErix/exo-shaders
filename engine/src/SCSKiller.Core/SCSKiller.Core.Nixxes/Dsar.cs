using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.Nixxes;

public sealed class Dsar : IDisposable
{
	public const int MaxBlock = 4194304;

	public const int MaxBlocks = 262144;

	private const int Stored = 0;

	private const int Lz4 = 3;

	private const int Gap = 254;

	private readonly SafeFileHandle h;

	private readonly long[] start;

	private readonly long[] at;

	private readonly int[] size;

	private readonly int[] packed;

	private readonly byte[] codec;

	private int cached = -1;

	private byte[] block = Array.Empty<byte>();

	public long Length { get; }

	private Dsar(SafeFileHandle h, long length, long[] start, long[] at, int[] size, int[] packed, byte[] codec)
	{
		this.h = h;
		Length = length;
		this.start = start;
		this.at = at;
		this.size = size;
		this.packed = packed;
		this.codec = codec;
	}

	public static Dsar? Open(string path, NixxesReader.Meter? meter = null)
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
		Dsar? dsar = Open(safeFileHandle, meter);
		if (dsar == null)
		{
			safeFileHandle.Dispose();
		}
		return dsar;
	}

	public static Dsar? Open(SafeFileHandle h, NixxesReader.Meter? meter = null)
	{
		try
		{
			return Parse(h, meter);
		}
		catch (Exception ex) when ((ex is IOException || ex is ArgumentException || ex is OverflowException) ? true : false)
		{
			return null;
		}
	}

	private static Dsar? Parse(SafeFileHandle h, NixxesReader.Meter? meter)
	{
		long length = RandomAccess.GetLength(h);
		byte[] array = new byte[32];
		if (RandomAccess.Read(h, array, 0L) != 32 || !((ReadOnlySpan<byte>)array.AsSpan(0, 4)).SequenceEqual("DSAR"u8))
		{
			return null;
		}
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(8));
		uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(12));
		long num3 = BinaryPrimitives.ReadInt64LittleEndian(array.AsSpan(16));
		bool flag = ((num > 262144 || num == 0) ? true : false);
		if (flag || 32 + 32L * (long)num > num2 || num2 > length || num3 <= 0 || (meter != null && !meter.Take(32L * (long)num)))
		{
			return null;
		}
		List<long> list = new List<long>();
		List<long> list2 = new List<long>();
		List<int> list3 = new List<int>();
		List<int> list4 = new List<int>();
		List<byte> list5 = new List<byte>();
		List<int> list6 = list4;
		List<int> list7 = list3;
		List<long> list8 = list2;
		List<long> list9 = list;
		byte[] array2 = new byte[32 * Math.Min(4096, (int)num)];
		long num4 = 0L;
		int num5 = 0;
		while (num5 < num)
		{
			int num6 = (int)Math.Min(4096L, num - num5);
			if (RandomAccess.Read(h, array2.AsSpan(0, 32 * num6), 32 + 32L * (long)num5) != 32 * num6)
			{
				return null;
			}
			int num7 = 0;
			while (num7 < num6)
			{
				Span<byte> span = array2.AsSpan(32 * num7);
				long num8 = BinaryPrimitives.ReadInt64LittleEndian(span);
				long num9 = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(8));
				uint num10 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16));
				uint num11 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20));
				flag = num8 < num4 || num8 > num3;
				if (!flag)
				{
					bool flag2 = ((num10 > int.MaxValue || num10 == 0) ? true : false);
					flag = flag2;
				}
				if (flag || num10 > num3 - num8 || num11 > int.MaxValue || num9 < 0 || num9 > length || num11 > length - num9 || (span[24] == 0 && num11 != num10))
				{
					return null;
				}
				list9.Add(num8);
				list8.Add(num9);
				list7.Add((int)num10);
				list6.Add((int)num11);
				list5.Add(span[24]);
				num4 = num8 + num10;
				num7++;
				num5++;
			}
		}
		return new Dsar(h, num3, list9.ToArray(), list8.ToArray(), list7.ToArray(), list6.ToArray(), list5.ToArray());
	}

	public void Read(long from, Span<byte> output)
	{
		if (from < 0 || from > Length || output.Length > Length - from)
		{
			throw new InvalidDataException("past the DSAR stream's end");
		}
		int num = Array.BinarySearch(start, from);
		if (num < 0)
		{
			num = ~num - 1;
		}
		long num2 = from;
		while (output.Length > 0)
		{
			if (num < 0 || (num < start.Length && num2 >= start[num] + size[num]))
			{
				num++;
			}
			long num3 = ((num < start.Length) ? start[num] : Length);
			if (num2 < num3)
			{
				int num4 = (int)Math.Min(output.Length, num3 - num2);
				output.Slice(0, num4).Clear();
				output = output.Slice(num4);
				num2 += num4;
				continue;
			}
			long num5 = num2 - start[num];
			int num6 = (int)Math.Min(output.Length, size[num] - num5);
			switch (codec[num])
			{
			case 254:
				output.Slice(0, num6).Clear();
				break;
			case 0:
				if (RandomAccess.Read(h, output.Slice(0, num6), at[num] + num5) != num6)
				{
					throw new InvalidDataException("DSAR file truncated");
				}
				break;
			default:
				Block(num).AsSpan((int)num5, num6).CopyTo(output);
				break;
			}
			output = output.Slice(num6);
			num2 += num6;
			num++;
		}
	}

	private byte[] Block(int i)
	{
		if (cached == i)
		{
			return block;
		}
		if (codec[i] != 3)
		{
			throw new InvalidDataException((codec[i] == 2) ? "a GDeflate block (not decoded)" : $"a block of unknown codec {codec[i]}");
		}
		if (size[i] > 4194304 || packed[i] > 4194304)
		{
			throw new InvalidDataException("an LZ4 block over " + 4 + " MB");
		}
		byte[] array = new byte[packed[i]];
		if (RandomAccess.Read(h, array, at[i]) != array.Length)
		{
			throw new InvalidDataException("DSAR file truncated");
		}
		byte[] array2 = new byte[size[i]];
		if (LZ4Codec.Decode(array, array2) != array2.Length)
		{
			throw new InvalidDataException("an LZ4 block doesn't decompress to its size");
		}
		int num = i;
		byte[] array3 = array2;
		cached = num;
		block = array3;
		return array2;
	}

	public void Dispose()
	{
		h.Dispose();
	}
}
