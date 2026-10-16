using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using CUE4Parse.Compression;
using Microsoft.Win32.SafeHandles;
using OodleDotNet;
using SCSKiller.Core.App;
using SCSKiller.Core.Nixxes;

namespace SCSKiller.Core.Decima;

public sealed class DecimaArchive : IDisposable
{
	public record struct Header(uint Magic, long FileSize, long DataSize, long Files, uint Chunks, uint MaxChunk);

	public sealed class Salts
	{
		public byte[] Iv { get; }

		public byte[] Data { get; }

		public Salts(byte[] iv, byte[] data)
		{
			if (iv.Length != 12 || data.Length != 16)
			{
				throw new ArgumentException("salts are 12 and 16 bytes");
			}
			Iv = iv;
			Data = data;
		}

		public void Xor(Span<byte> b16, uint key)
		{
			Span<byte> span = stackalloc byte[16];
			BinaryPrimitives.WriteUInt32LittleEndian(span, key);
			Iv.CopyTo(span.Slice(4));
			Span<byte> @out = stackalloc byte[16];
			Murmur3(span, @out);
			for (int i = 0; i < 16; i++)
			{
				b16[i] ^= @out[i];
			}
		}

		public void XorEntry(Span<byte> e32)
		{
			for (int i = 0; i < 32; i += 16)
			{
				Span<byte> b = e32.Slice(i, 16);
				uint num = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(12));
				Xor(b, num);
				BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(12), num);
			}
		}

		public void XorChunk(Span<byte> data, ReadOnlySpan<byte> plainEntry16)
		{
			Span<byte> span = stackalloc byte[16];
			ChunkKey(plainEntry16, span);
			XorRepeated(data, span);
		}

		internal void ChunkKey(ReadOnlySpan<byte> plainEntry16, Span<byte> key16)
		{
			Span<byte> span = stackalloc byte[16];
			Murmur3(plainEntry16, span);
			for (int i = 0; i < 16; i++)
			{
				span[i] ^= Data[i];
			}
			MD5.HashData(span, key16);
		}

		internal static void XorRepeated(Span<byte> data, ReadOnlySpan<byte> key16)
		{
			for (int i = 0; i < data.Length; i++)
			{
				data[i] ^= key16[i & 0xF];
			}
		}
	}

	public const uint Plain = 540033104u;

	public const uint Encrypted = 556810320u;

	public const int HeaderSize = 40;

	public const int EntrySize = 32;

	public const int MaxChunk = 4194304;

	public const int MaxChunks = 2097152;

	public const int MaxRatio = 64;

	private readonly SafeFileHandle h;

	private readonly Salts? salts;

	private readonly long[] start;

	private readonly long[] at;

	private readonly int[] size;

	private readonly int[] packed;

	private readonly uint[] key;

	private int cached = -1;

	private readonly byte[] block;

	private readonly byte[] raw;

	public const string WrongSize = "its header gives another file size";

	public long Length { get; }

	public int Chunks => start.Length;

	public NixxesReader.Readable Readable => new NixxesReader.Readable(Length, Read);

	private DecimaArchive(SafeFileHandle h, Salts? salts, int maxChunk, long[] start, long[] at, int[] size, int[] packed, uint[] key)
	{
		this.h = h;
		this.salts = salts;
		this.start = start;
		this.at = at;
		this.size = size;
		this.packed = packed;
		this.key = key;
		Length = ((start.Length == 0) ? 0 : (start[^1] + size[^1]));
		block = new byte[maxChunk];
		raw = new byte[maxChunk];
	}

	public static uint Magic(string path)
	{
		try
		{
			using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
			Span<byte> span = stackalloc byte[4];
			return (RandomAccess.Read(handle, span, 0L) == 4) ? BinaryPrimitives.ReadUInt32LittleEndian(span) : 0u;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return 0u;
		}
	}

	public static DecimaArchive? Open(string path, Salts? salts, NixxesReader.Meter? meter = null)
	{
		string why;
		return Open(path, salts, out why, meter);
	}

	public static DecimaArchive? Open(string path, Salts? salts, out string why, NixxesReader.Meter? meter = null)
	{
		SafeFileHandle safeFileHandle;
		try
		{
			safeFileHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			why = "can't be read";
			return null;
		}
		try
		{
			DecimaArchive decimaArchive = Parse(safeFileHandle, salts, meter, out why);
			if (decimaArchive != null)
			{
				return decimaArchive;
			}
		}
		catch (Exception ex2) when ((ex2 is IOException || ex2 is ArgumentException || ex2 is OverflowException) ? true : false)
		{
			why = "can't be read";
		}
		safeFileHandle.Dispose();
		return null;
	}

	public static Header? ReadHeader(ReadOnlySpan<byte> head, Salts? salts)
	{
		if (head.Length < 40)
		{
			return null;
		}
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(head);
		bool flag = ((num == 540033104 || num == 556810320) ? true : false);
		if (!flag || (num == 556810320 && salts == null))
		{
			return null;
		}
		Span<byte> span = stackalloc byte[32];
		head.Slice(8, 32).CopyTo(span);
		if (num == 556810320)
		{
			uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(4));
			salts.Xor(span.Slice(0, 16), num2);
			salts.Xor(span.Slice(16), num2 + 1);
		}
		return new Header(num, BinaryPrimitives.ReadInt64LittleEndian(span), BinaryPrimitives.ReadInt64LittleEndian(span.Slice(8)), BinaryPrimitives.ReadInt64LittleEndian(span.Slice(16)), BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24)), BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(28)));
	}

	private static DecimaArchive? Parse(SafeFileHandle h, Salts? salts, NixxesReader.Meter? meter, out string why)
	{
		long length = RandomAccess.GetLength(h);
		byte[] array = new byte[40];
		why = "not a Decima archive";
		if (RandomAccess.Read(h, array, 0L) == 40)
		{
			Header? header = ReadHeader(array, salts);
			if (header.HasValue)
			{
				Header valueOrDefault = header.GetValueOrDefault();
				bool flag = valueOrDefault.Magic == 556810320;
				why = "its header gives another file size";
				if (valueOrDefault.FileSize != length)
				{
					return null;
				}
				why = "its header's counts don't fit the file";
				bool flag2 = valueOrDefault.Chunks > 2097152;
				if (!flag2)
				{
					uint maxChunk = valueOrDefault.MaxChunk;
					bool flag3 = ((maxChunk > 4194304 || maxChunk == 0) ? true : false);
					flag2 = flag3;
				}
				if (flag2 || valueOrDefault.Files < 0 || valueOrDefault.Files > (length - 40) / 32 - valueOrDefault.Chunks)
				{
					return null;
				}
				why = "over the read budget";
				if (meter != null && !meter.Take(32L * (long)valueOrDefault.Chunks))
				{
					return null;
				}
				why = "its chunk table is truncated or out of bounds";
				long num = 40 + 32 * valueOrDefault.Files;
				int chunks = (int)valueOrDefault.Chunks;
				List<long> list = new List<long>();
				List<long> list2 = new List<long>();
				List<int> list3 = new List<int>();
				List<int> list4 = new List<int>();
				List<uint> list5 = new List<uint>();
				List<uint> key = list5;
				List<int> packed = list4;
				List<int> size = list3;
				List<long> at = list2;
				List<long> start = list;
				byte[] array2 = new byte[32 * Math.Min(4096, chunks)];
				int num2 = 0;
				while (num2 < chunks)
				{
					int num3 = Math.Min(4096, chunks - num2);
					if (RandomAccess.Read(h, array2.AsSpan(0, 32 * num3), num + 32L * (long)num2) != 32 * num3)
					{
						return null;
					}
					int num4 = 0;
					while (num4 < num3)
					{
						Span<byte> span = array2.AsSpan(32 * num4, 32);
						if (flag)
						{
							salts.XorEntry(span);
						}
						long num5 = BinaryPrimitives.ReadInt64LittleEndian(span);
						long num6 = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(16));
						uint num7 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8));
						uint num8 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24));
						flag2 = num5 < 0 || num5 / 64 > length || num7 == 0 || num7 > valueOrDefault.MaxChunk || num5 + num7 - 4194304 > 64 * length;
						if (!flag2)
						{
							bool flag3 = ((num8 > 4194304 || num8 == 0) ? true : false);
							flag2 = flag3;
						}
						if (flag2 || num6 < 40 || num6 > length - num8)
						{
							return null;
						}
						start.Add(num5);
						at.Add(num6);
						size.Add((int)num7);
						packed.Add((int)num8);
						key.Add(BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12)));
						num4++;
						num2++;
					}
				}
				int[] array3 = (from i in Enumerable.Range(0, chunks)
					orderby start[i]
					select i).ToArray();
				for (int num9 = 1; num9 < chunks; num9++)
				{
					if (start[array3[num9]] < start[array3[num9 - 1]] + size[array3[num9 - 1]])
					{
						why = "its chunks overlap";
						return null;
					}
				}
				why = "";
				return new DecimaArchive(h, flag ? salts : null, Math.Max(size.DefaultIfEmpty().Max(), packed.DefaultIfEmpty().Max()), array3.Select((int i) => start[i]).ToArray(), array3.Select((int i) => at[i]).ToArray(), array3.Select((int i) => size[i]).ToArray(), array3.Select((int i) => packed[i]).ToArray(), array3.Select((int i) => key[i]).ToArray());
			}
		}
		return null;
	}

	public void Read(long from, Span<byte> output)
	{
		if (from < 0 || from > Length || output.Length > Length - from)
		{
			throw new InvalidDataException("past the archive's end");
		}
		int num = Array.BinarySearch(start, from);
		if (num < 0)
		{
			num = ~num - 1;
		}
		long num2 = from;
		while (output.Length > 0)
		{
			if (num < 0 || num2 >= start[num] + size[num])
			{
				num++;
			}
			if (num2 < start[num])
			{
				int num3 = (int)Math.Min(output.Length, start[num] - num2);
				output.Slice(0, num3).Clear();
				output = output.Slice(num3);
				num2 += num3;
			}
			else
			{
				int num4 = (int)(num2 - start[num]);
				int num5 = Math.Min(output.Length, size[num] - num4);
				Chunk(num).AsSpan(num4, num5).CopyTo(output);
				output = output.Slice(num5);
				num2 += num5;
				num++;
			}
		}
	}

	private byte[] Chunk(int i)
	{
		if (cached == i)
		{
			return block;
		}
		cached = -1;
		Span<byte> span = raw.AsSpan(0, packed[i]);
		ReadRaw(span, at[i]);
		if (salts != null)
		{
			salts.XorChunk(span, PlainEntry(start[i], size[i], key[i]));
		}
		Decompress(raw, packed[i], block, size[i]);
		cached = i;
		return block;
	}

	internal (int Chunk, int Packed)? Compressed()
	{
		(int, int)? result = null;
		for (int i = 0; i < Chunks; i++)
		{
			if (packed[i] < size[i] && (!result.HasValue || packed[i] < result.Value.Item2))
			{
				result = (i, packed[i]);
			}
		}
		return result;
	}

	internal (byte[] Packed, int Size, byte[] Entry) Raw(int i)
	{
		if ((uint)i >= (uint)Chunks)
		{
			throw new InvalidDataException("no such chunk");
		}
		byte[] array = new byte[packed[i]];
		ReadRaw(array, at[i]);
		return (Packed: array, Size: size[i], Entry: PlainEntry(start[i], size[i], key[i]));
	}

	private void ReadRaw(Span<byte> b, long offset)
	{
		int num;
		try
		{
			num = RandomAccess.Read(h, b, offset);
		}
		catch (IOException innerException)
		{
			throw new InvalidDataException("archive unreadable", innerException);
		}
		if (num != b.Length)
		{
			throw new InvalidDataException("archive truncated");
		}
	}

	internal static byte[] PlainEntry(long start, int size, uint key)
	{
		byte[] array = new byte[16];
		BinaryPrimitives.WriteInt64LittleEndian(array, start);
		BinaryPrimitives.WriteInt32LittleEndian(array.AsSpan(8), size);
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(12), key);
		return array;
	}

	internal static void Decompress(byte[] src, int count, byte[] into, int want)
	{
		long num = want;
		try
		{
			Codecs.Load(zlib: false);
			Oodle instance = OodleHelper.Instance;
			if (instance != null)
			{
				num = instance.Decompress(src.AsSpan(0, count), into.AsSpan(0, want));
			}
			else
			{
				Compression.Decompress(src, 0, count, into, 0, want, CompressionMethod.Oodle);
			}
		}
		catch (Exception ex) when (!(ex is InvalidDataException))
		{
			throw new InvalidDataException("an Oodle chunk doesn't decompress", ex);
		}
		if (num != want)
		{
			throw new InvalidDataException("an Oodle chunk doesn't decompress to its size");
		}
	}

	public void Dispose()
	{
		h.Dispose();
	}

	public static void Murmur3(ReadOnlySpan<byte> b16, Span<byte> out16)
	{
		ulong num = 42uL;
		ulong num2 = 42uL;
		ulong num3 = BinaryPrimitives.ReadUInt64LittleEndian(b16);
		ulong num4 = BinaryPrimitives.ReadUInt64LittleEndian(b16.Slice(8));
		num3 *= 9782798678568883157uL;
		num3 = BitOperations.RotateLeft(num3, 31);
		num3 *= 5545529020109919103L;
		num ^= num3;
		num = BitOperations.RotateLeft(num, 27);
		num += num2;
		num = num * 5 + 1390208809;
		num4 *= 5545529020109919103L;
		num4 = BitOperations.RotateLeft(num4, 33);
		num4 *= 9782798678568883157uL;
		num2 ^= num4;
		num2 = BitOperations.RotateLeft(num2, 31);
		num2 += num;
		num2 = num2 * 5 + 944331445;
		num ^= 0x10;
		num2 ^= 0x10;
		num += num2;
		num2 += num;
		num = Fmix(num);
		num2 = Fmix(num2);
		num += num2;
		num2 += num;
		BinaryPrimitives.WriteUInt64LittleEndian(out16, num);
		BinaryPrimitives.WriteUInt64LittleEndian(out16.Slice(8), num2);
	}

	private static ulong Fmix(ulong k)
	{
		k ^= k >> 33;
		k *= 18397679294719823053uL;
		k ^= k >> 33;
		k *= 14181476777654086739uL;
		return k ^ (k >> 33);
	}
}
