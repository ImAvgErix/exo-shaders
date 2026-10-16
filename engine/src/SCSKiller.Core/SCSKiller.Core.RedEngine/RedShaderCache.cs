using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace SCSKiller.Core.RedEngine;

public static class RedShaderCache
{
	public readonly record struct Entry(long At, int Stored, bool Zlib, int Inflated);

	public sealed record Materials(Dictionary<ulong, Entry> Shaders, List<ulong[]> Techniques);

	public readonly record struct Footer(uint Shaders, uint Techniques, long At, long Size);

	private const uint Magic = 1397245010u;

	private const uint Version = 5u;

	public const int MaxShaders = 200000;

	public const int MaxTechniques = 2000000;

	public const int MaxStatic = 4000;

	public const int MaxPayload = 4194304;

	public const int MaxPipelines = 250000;

	private const int ShaderHeader = 17;

	private const int MinTechnique = 117;

	public static Footer? ReadFooter(Stream f)
	{
		long length = f.Length;
		if (length < 56)
		{
			return null;
		}
		byte[] array = new byte[48];
		f.Position = length - 48;
		f.ReadExactly(array);
		if (U32(array, 40) != 1397245010 || U32(array, 44) != 5)
		{
			return null;
		}
		uint num = U32(array, 0);
		uint num2 = U32(array, 4);
		ulong num3 = U64(array, 16);
		ulong num4 = U64(array, 24);
		ulong num5 = num3;
		uint num6 = num2;
		uint num7 = num;
		bool flag = ((num7 > 200000 || num7 == 0) ? true : false);
		bool flag2 = flag;
		if (!flag2)
		{
			bool flag3 = ((num6 > 2000000 || num6 == 0) ? true : false);
			flag2 = flag3;
		}
		if (flag2 || num5 > (ulong)(length - 56) || num4 > (ulong)(length - 56 - (long)num5) || (ulong)((long)num7 * 17L) > num5 || (ulong)((long)num6 * 117L) > num4)
		{
			return null;
		}
		byte[] array2 = new byte[8];
		f.Position = (long)(num5 + num4);
		f.ReadExactly(array2);
		if ((long)(num5 + num4 + 8) + 12 * ((long)U32(array2, 0) + (long)U32(array2, 4)) != length - 48)
		{
			return null;
		}
		return new Footer(num7, num6, (long)num5, (long)num4);
	}

	public static Materials? ReadMaterials(Stream f, CancellationToken ct = default(CancellationToken))
	{
		Footer? footer = ReadFooter(f);
		if (footer.HasValue)
		{
			Footer valueOrDefault = footer.GetValueOrDefault();
			BinaryReader binaryReader = new BinaryReader(f);
			try
			{
				f.Position = 0L;
				Dictionary<ulong, Entry> dictionary = new Dictionary<ulong, Entry>((int)valueOrDefault.Shaders);
				for (int i = 0; i < valueOrDefault.Shaders; i++)
				{
					if (i % 4096 == 0)
					{
						ct.ThrowIfCancellationRequested();
					}
					ulong key = binaryReader.ReadUInt64();
					bool zlib = binaryReader.ReadByte() != 0;
					int num = binaryReader.ReadInt32();
					int num2 = binaryReader.ReadInt32();
					bool flag = num2 < 0 || num2 > valueOrDefault.At - f.Position;
					if (!flag)
					{
						bool flag2 = ((num < 0 || num > 4194304) ? true : false);
						flag = flag2;
					}
					if (flag)
					{
						return null;
					}
					dictionary[key] = new Entry(f.Position, num2, zlib, num);
					f.Position += num2;
				}
				if (f.Position != valueOrDefault.At)
				{
					return null;
				}
				long num3 = valueOrDefault.At + valueOrDefault.Size;
				List<ulong[]> list = new List<ulong[]>((int)valueOrDefault.Techniques);
				for (int j = 0; j < valueOrDefault.Techniques; j++)
				{
					if (j % 4096 == 0)
					{
						ct.ThrowIfCancellationRequested();
					}
					long position = f.Position;
					list.Add(ReadTechnique(binaryReader, num3, ct));
					if (f.Position <= position)
					{
						return null;
					}
				}
				return (f.Position == num3) ? new Materials(dictionary, list) : null;
			}
			catch (Exception ex) when ((ex is EndOfStreamException || ex is InvalidDataException) ? true : false)
			{
				return null;
			}
		}
		return null;
	}

	private static ulong[] ReadTechnique(BinaryReader r, long end, CancellationToken ct)
	{
		Stream baseStream = r.BaseStream;
		Advance(baseStream, 24L, end);
		Skip(r, name: false, end);
		Advance(baseStream, 4L, end);
		ulong[] array = new ulong[8];
		for (int i = 0; i < 8; i++)
		{
			array[i] = r.ReadUInt64();
		}
		Advance(baseStream, 8L, end);
		for (int j = 0; j < 2; j++)
		{
			uint num = r.ReadUInt32();
			Advance(baseStream, 32L * (long)num, end);
		}
		for (int k = 0; k < 2; k++)
		{
			uint num2 = r.ReadUInt32();
			if (2L * (long)num2 > end - baseStream.Position)
			{
				throw new InvalidDataException("more names than bytes left");
			}
			while (num2 != 0)
			{
				if (num2 % 4096 == 0)
				{
					ct.ThrowIfCancellationRequested();
				}
				Skip(r, name: true, end);
				Advance(baseStream, 1L, end);
				num2--;
			}
		}
		return array;
	}

	private static void Skip(BinaryReader r, bool name, long end)
	{
		byte b = r.ReadByte();
		long num = b & 0x3F;
		int i = 6;
		for (int num2 = b; (num2 & ((i == 6) ? 64 : 128)) != 0; i += 7)
		{
			if (i > 27)
			{
				throw new InvalidDataException("compact length over 34 bits");
			}
			num2 = r.ReadByte();
			num |= (long)(num2 & 0x7F) << i;
		}
		Advance(r.BaseStream, (name || (b & 0x80) != 0) ? num : (2 * num), end);
	}

	private static void Advance(Stream f, long n, long end)
	{
		if (n < 0 || n > end - f.Position)
		{
			throw new InvalidDataException("runs past the techniques");
		}
		f.Position += n;
	}

	public static List<Entry>? ReadStatic(Stream f)
	{
		long length = f.Length;
		if (length < 28)
		{
			return null;
		}
		byte[] array = new byte[28];
		f.Position = length - 28;
		f.ReadExactly(array);
		if (U32(array, 20) != 1397245010 || U32(array, 24) != 5)
		{
			return null;
		}
		uint num = U32(array, 0);
		if (num > 4000 || (long)num * 28L > length)
		{
			return null;
		}
		BinaryReader binaryReader = new BinaryReader(f);
		f.Position = 0L;
		List<Entry> list = new List<Entry>((int)num);
		for (int i = 0; i < num; i++)
		{
			f.Position += 16L;
			int num2 = binaryReader.ReadInt32();
			bool flag = ((num2 < 8 || num2 > 4194304) ? true : false);
			if (flag || num2 > length - 28 - f.Position)
			{
				return null;
			}
			list.Add(new Entry(f.Position, num2, Zlib: false, num2));
			f.Position += num2;
		}
		if (f.Position != length - 28)
		{
			return null;
		}
		return list;
	}

	public static byte[]? Container(Stream f, Entry e)
	{
		int inflated = e.Inflated;
		bool flag = ((inflated < 0 || inflated > 4194304) ? true : false);
		bool flag2 = flag;
		if (!flag2)
		{
			int stored = e.Stored;
			bool flag3 = ((stored < 0 || stored > 4194304) ? true : false);
			flag2 = flag3;
		}
		if (flag2 || e.At < 0 || e.At > f.Length - e.Stored)
		{
			return null;
		}
		byte[] array = new byte[e.Stored];
		f.Position = e.At;
		if (f.ReadAtLeast(array, array.Length, throwOnEndOfStream: false) != array.Length)
		{
			return null;
		}
		byte[] array2;
		if (!e.Zlib)
		{
			array2 = array;
		}
		else
		{
			try
			{
				using ZLibStream zLibStream = new ZLibStream(new MemoryStream(array), CompressionMode.Decompress);
				array2 = new byte[e.Inflated];
				if (zLibStream.ReadAtLeast(array2, array2.Length, throwOnEndOfStream: false) != array2.Length || zLibStream.ReadByte() != -1)
				{
					return null;
				}
			}
			catch (InvalidDataException)
			{
				return null;
			}
		}
		if (array2.Length < 40 || !((ReadOnlySpan<byte>)array2.AsSpan(8, 4)).SequenceEqual("DXBC"u8))
		{
			return null;
		}
		int num = BinaryPrimitives.ReadInt32LittleEndian(array2);
		if (num < 32 || num > array2.Length - 8)
		{
			return null;
		}
		return array2[8..(8 + num)];
	}

	private static uint U32(byte[] b, int o)
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
	}

	private static ulong U64(byte[] b, int o)
	{
		return BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
	}
}
