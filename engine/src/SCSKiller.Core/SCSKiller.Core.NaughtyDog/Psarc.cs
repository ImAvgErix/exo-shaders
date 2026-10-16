using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.Compression;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.App;
using SCSKiller.Core.Nixxes;

namespace SCSKiller.Core.NaughtyDog;

public sealed class Psarc : IDisposable
{
	private readonly SafeFileHandle h;

	private readonly Dsar? dsar;

	private readonly bool oodle;

	private readonly int blockSize;

	private readonly (int Block, long Size, long Offset)[] entries;

	private readonly byte[] toc;

	private readonly int table;

	private readonly int width;

	public const int MaxToc = 33554432;

	private const int MaxNames = 67108864;

	public bool Oodle => oodle;

	public string?[] Names { get; }

	public int Count => entries.Length;

	public bool Wrapped => dsar != null;

	private long Length => dsar?.Length ?? RandomAccess.GetLength(h);

	public long Size(int entry)
	{
		return entries[entry].Size;
	}

	private Psarc(SafeFileHandle h, Dsar? dsar, bool oodle, int blockSize, (int, long, long)[] entries, byte[] toc, int table, int width)
	{
		bool flag = oodle;
		this.h = h;
		this.dsar = dsar;
		this.oodle = flag;
		this.blockSize = blockSize;
		this.entries = entries;
		this.toc = toc;
		this.table = table;
		this.width = width;
		Names = new string[entries.Length];
	}

	public static Psarc? Open(string path, NixxesReader.Meter? meter = null)
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
		Dsar dsar = null;
		try
		{
			dsar = Dsar.Open(safeFileHandle, meter);
			Psarc psarc = Parse(safeFileHandle, dsar, meter);
			if (psarc != null)
			{
				return psarc;
			}
		}
		catch (Exception ex2) when ((ex2 is InvalidDataException || ex2 is IOException || ex2 is ArgumentException || ex2 is OverflowException) ? true : false)
		{
		}
		if (dsar != null)
		{
			dsar.Dispose();
		}
		else
		{
			safeFileHandle.Dispose();
		}
		return null;
	}

	private int ReadAt(Span<byte> b, long at)
	{
		if (dsar == null)
		{
			return RandomAccess.Read(h, b, at);
		}
		if (at < 0 || at > dsar.Length - b.Length)
		{
			return 0;
		}
		dsar.Read(at, b);
		return b.Length;
	}

	private static Psarc? Parse(SafeFileHandle h, Dsar? dsar, NixxesReader.Meter? meter)
	{
		long num = dsar?.Length ?? RandomAccess.GetLength(h);
		byte[] head = new byte[32];
		Psarc psarc = new Psarc(h, dsar, oodle: false, 1, Array.Empty<(int, long, long)>(), Array.Empty<byte>(), 0, 2);
		if (psarc.ReadAt(head, 0L) != 32 || !((ReadOnlySpan<byte>)head.AsSpan(0, 4)).SequenceEqual("PSAR"u8))
		{
			return null;
		}
		string text = Encoding.ASCII.GetString(head, 8, 4);
		uint num2 = U(12);
		uint num3 = U(16);
		uint num4 = U(20);
		uint num5 = U(24);
		bool flag = ((text == "zlib" || text == "oodl") ? true : false);
		bool flag2 = !flag;
		if (!flag2)
		{
			bool flag3 = ((num5 < 1024 || num5 > 16777216) ? true : false);
			flag2 = flag3;
		}
		bool flag4 = flag2 || num3 < 30;
		if (!flag4)
		{
			bool flag3 = ((num4 > 16777216 || num4 == 0) ? true : false);
			flag4 = flag3;
		}
		if (flag4 || num2 > num || num2 > 33554432 || 32 + (long)num4 * (long)num3 > num2 || (meter != null && !meter.Take(num2)))
		{
			return null;
		}
		byte[] array = new byte[num2 - 32];
		if (psarc.ReadAt(array, 32L) != array.Length)
		{
			return null;
		}
		int num6 = ((num5 <= 65536) ? 2 : 3);
		int num7 = (int)(num4 * num3);
		int num8 = (array.Length - num7) / num6;
		(int, long, long)[] array2 = new (int, long, long)[num4];
		Dictionary<UInt128, int> dictionary = new Dictionary<UInt128, int>();
		for (int i = 0; i < num4; i++)
		{
			Span<byte> span = array.AsSpan((int)(i * num3));
			uint num9 = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(16));
			ulong num10 = ((ulong)span[20] << 32) | BinaryPrimitives.ReadUInt32BigEndian(span.Slice(21));
			long num11 = (long)(((ulong)span[25] << 32) | BinaryPrimitives.ReadUInt32BigEndian(span.Slice(26)));
			long num12 = (long)num10;
			uint num13 = num9;
			if (num13 + (num12 + num5 - 1) / num5 > num8 || num11 > num)
			{
				return null;
			}
			array2[i] = ((int)num13, num12, num11);
			dictionary.TryAdd(BinaryPrimitives.ReadUInt128BigEndian(span), i);
		}
		Psarc psarc2 = new Psarc(h, dsar, text == "oodl", (int)num5, array2, array, num7, num6);
		if (psarc2.Size(0) > 67108864 || (meter != null && !meter.Take(psarc2.Size(0))))
		{
			return null;
		}
		byte[] array3 = psarc2.Read(0, 0L, (int)psarc2.Size(0));
		Span<byte> span2 = stackalloc byte[16];
		foreach (Range current in array3.AsSpan().SplitAny("\n\r\0"u8))
		{
			Span<byte> span3 = array3.AsSpan(current);
			if (span3.IsEmpty)
			{
				continue;
			}
			MD5.HashData(span3, span2);
			if (dictionary.TryGetValue(BinaryPrimitives.ReadUInt128BigEndian(span2), out var value) && value > 0)
			{
				ref string reference = ref psarc2.Names[value];
				if (reference == null)
				{
					reference = Encoding.UTF8.GetString(span3);
				}
			}
		}
		return psarc2;
		uint U(int at)
		{
			return BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(at));
		}
	}

	public byte[] Read(int entry, long from, int count)
	{
		(int First, long Size, long Offset) tuple = Range(entry, from, count);
		int item = tuple.First;
		long num = tuple.Offset;
		for (int i = 0; i < (int)((from + count + blockSize - 1) / blockSize); i++)
		{
			num += Stored(item + i);
		}
		if (num > Length)
		{
			throw new InvalidDataException("archive truncated");
		}
		byte[] array = new byte[count];
		Read(entry, from, array);
		return array;
	}

	public void Read(int entry, long from, Span<byte> output)
	{
		(int First, long Size, long Offset) tuple = Range(entry, from, output.Length);
		int item = tuple.First;
		long item2 = tuple.Size;
		long item3 = tuple.Offset;
		int length = output.Length;
		int num = (int)(from / blockSize);
		long length2 = Length;
		long num2 = item3;
		for (int i = 0; i < num; i++)
		{
			num2 += Stored(item + i);
		}
		long num3 = (long)num * (long)blockSize;
		while (num3 < from + length)
		{
			int num4 = (int)Math.Min(blockSize, item2 - num3);
			int num5 = Stored(item + num);
			if (num2 > length2 - num5)
			{
				throw new InvalidDataException("archive truncated");
			}
			byte[] array = new byte[num5];
			if (ReadAt(array, num2) != array.Length)
			{
				throw new InvalidDataException("archive truncated");
			}
			num2 += array.Length;
			byte[] array2 = ((array.Length == num4) ? array : Decompress(array, num4));
			int num6 = (int)Math.Max(0L, from - num3);
			int num7 = (int)Math.Min(num4, from + length - num3);
			array2.AsSpan(num6, num7 - num6).CopyTo(output.Slice((int)(num3 + num6 - from)));
			num3 += blockSize;
			num++;
		}
	}

	private (int First, long Size, long Offset) Range(int entry, long from, int count)
	{
		if ((uint)entry >= (uint)entries.Length)
		{
			throw new InvalidDataException("no such entry");
		}
		(int, long, long) result = entries[entry];
		if (from < 0 || count < 0 || from > result.Item2 - count)
		{
			throw new InvalidDataException("past the entry's end");
		}
		return result;
	}

	private int Stored(int block)
	{
		int num = table + block * width;
		int num2 = ((width == 2) ? BinaryPrimitives.ReadUInt16BigEndian(toc.AsSpan(num)) : ((toc[num] << 16) | (toc[num + 1] << 8) | toc[num + 2]));
		if (num2 != 0)
		{
			return num2;
		}
		return blockSize;
	}

	private byte[] Decompress(byte[] raw, int want)
	{
		if (!oodle || raw[0] == 120)
		{
			byte[] array = new byte[want];
			using ZLibStream zLibStream = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
			try
			{
				zLibStream.ReadExactly(array);
			}
			catch (EndOfStreamException innerException)
			{
				throw new InvalidDataException("a zlib block is short", innerException);
			}
			return array;
		}
		Codecs.Load(zlib: false);
		byte[] array2;
		try
		{
			array2 = Compression.Decompress(raw, want, CompressionMethod.Oodle);
		}
		catch (Exception ex) when (!(ex is InvalidDataException))
		{
			throw new InvalidDataException("an Oodle block doesn't decompress", ex);
		}
		if (array2.Length != want)
		{
			throw new InvalidDataException("an Oodle block didn't decompress to its size");
		}
		return array2;
	}

	public void Dispose()
	{
		if (dsar != null)
		{
			dsar.Dispose();
		}
		else
		{
			h.Dispose();
		}
	}
}
