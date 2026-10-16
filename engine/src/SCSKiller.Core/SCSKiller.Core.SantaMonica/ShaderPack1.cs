using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.SantaMonica;

public static class ShaderPack1
{
	public sealed record Shader(ulong Hash, int Type, long Offset, int Packed, int Size);

	public const int Version = 1;

	public const int HeaderSize = 96;

	public const int Record = 112;

	private const long MaxCount = 1048576L;

	public static bool IsPack(ReadOnlySpan<byte> head)
	{
		if (head.Length >= 96 && head.StartsWith("SHDRPACK"u8))
		{
			return BinaryPrimitives.ReadUInt64LittleEndian(head.Slice(8)) == 1;
		}
		return false;
	}

	public static SafeFileHandle? Open(string path)
	{
		try
		{
			return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public static byte[]? Head(string path, ref long read)
	{
		using SafeFileHandle safeFileHandle = Open(path);
		if (safeFileHandle == null)
		{
			return null;
		}
		byte[] array = new byte[96];
		try
		{
			int num = RandomAccess.Read(safeFileHandle, array, 0L);
			read += num;
			return array[..num];
		}
		catch (IOException)
		{
			return null;
		}
	}

	public static List<Shader>? Read(SafeFileHandle h)
	{
		try
		{
			long length = RandomAccess.GetLength(h);
			byte[] array = new byte[96];
			if (RandomAccess.Read(h, array, 0L) != 96 || !IsPack(array))
			{
				return null;
			}
			ulong num = BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(32));
			if (num > 1048576 || (long)(96 + num * 112) > length)
			{
				return null;
			}
			byte[] array2 = new byte[(int)num * 112];
			if (RandomAccess.Read(h, array2, 96L) != array2.Length)
			{
				return null;
			}
			long num2 = 96 + array2.LongLength;
			List<Shader> list = new List<Shader>();
			for (int i = 0; i < (int)num; i++)
			{
				Span<byte> span = array2.AsSpan(i * 112);
				uint num3 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));
				uint num4 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16));
				long num5 = num2 + BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20));
				bool flag = ((num3 > 67108864 || num3 == 0) ? true : false);
				bool flag2 = flag;
				if (!flag2)
				{
					bool flag3 = ((num4 < 32 || num4 > 67108864) ? true : false);
					flag2 = flag3;
				}
				if (flag2 || num5 + num3 > length)
				{
					return null;
				}
				list.Add(new Shader(BinaryPrimitives.ReadUInt64LittleEndian(span), BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8)), num5, (int)num3, (int)num4));
			}
			return list;
		}
		catch (IOException)
		{
			return null;
		}
	}

	public static byte[]? Decode(SafeFileHandle h, Shader s)
	{
		byte[] array = new byte[s.Packed];
		try
		{
			if (RandomAccess.Read(h, array, s.Offset) != array.Length)
			{
				return null;
			}
		}
		catch (IOException)
		{
			return null;
		}
		byte[] array2 = new byte[s.Size];
		if (LZ4Codec.Decode(array, array2) != s.Size)
		{
			return null;
		}
		return array2;
	}
}
