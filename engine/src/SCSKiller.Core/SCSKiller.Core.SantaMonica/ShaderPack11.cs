using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.SantaMonica;

public sealed class ShaderPack11
{
	public sealed record Shader(ulong Hash, int Type, long Offset, int Size);

	public sealed record Root(ulong Hash, long Offset, int Size);

	public sealed record Program(ulong Hash, ulong RootSignature, ulong[] Slots);

	public const int Version = 11;

	public const int HeaderSize = 128;

	public const int ShaderRecord = 104;

	public const int RootRecord = 16;

	public const int ProgramRecord = 3744;

	public const int PassRecord = 904;

	public const int SlotsAt = 1672;

	private const long MaxCount = 1048576L;

	private const int Chunk = 1048576;

	public required IReadOnlyList<Shader> Shaders { get; init; }

	public required IReadOnlyList<Root> Roots { get; init; }

	public required IReadOnlyList<Program> Programs { get; init; }

	public static bool IsPack(ReadOnlySpan<byte> head)
	{
		if (head.Length >= 128 && head.StartsWith("SHDRPACK"u8))
		{
			return BinaryPrimitives.ReadUInt64LittleEndian(head.Slice(8)) == 11;
		}
		return false;
	}

	public static ShaderPack11? Read(Lz4Frame f, ref long budget)
	{
		if (f.Length < 128)
		{
			return null;
		}
		byte[] h = f.Read(0L, 128);
		if (!IsPack(h))
		{
			return null;
		}
		long num = U(16);
		long num2 = U(24);
		long num3 = U(32);
		long n = U(40);
		long at = U(48);
		long at2 = U(56);
		long at3 = U(64);
		long at4 = U(72);
		long num4 = U(80);
		long num5 = U(88);
		long length = f.Length;
		if (!Fits(at, num, 104L) || !Fits(at2, num2, 16L) || !Fits(at3, num3, 3744L) || !Fits(at4, n, 904L) || num4 > length || num5 > length)
		{
			return null;
		}
		if ((budget -= num + num2 + num3) < 0)
		{
			return null;
		}
		List<Shader> list = new List<Shader>();
		foreach (ReadOnlyMemory<byte> item in Records(f, at, num, 104))
		{
			uint num6 = BinaryPrimitives.ReadUInt32LittleEndian(item.Span.Slice(12));
			long num7 = num4 + BinaryPrimitives.ReadUInt32LittleEndian(item.Span.Slice(16));
			uint num8 = num6;
			bool flag = ((num8 < 32 || num8 > 67108864) ? true : false);
			if (flag || num7 + num8 > length)
			{
				return null;
			}
			list.Add(new Shader(BinaryPrimitives.ReadUInt64LittleEndian(item.Span), BinaryPrimitives.ReadInt32LittleEndian(item.Span.Slice(8)), num7, (int)num8));
		}
		List<Root> list2 = new List<Root>();
		foreach (ReadOnlyMemory<byte> item2 in Records(f, at2, num2, 16))
		{
			long num9 = num5 + BinaryPrimitives.ReadUInt32LittleEndian(item2.Span.Slice(8));
			uint num10 = BinaryPrimitives.ReadUInt32LittleEndian(item2.Span.Slice(12));
			long num11 = num9;
			bool flag = ((num10 < 32 || num10 > 67108864) ? true : false);
			if (flag || num11 + num10 > length)
			{
				return null;
			}
			list2.Add(new Root(BinaryPrimitives.ReadUInt64LittleEndian(item2.Span), num11, (int)num10));
		}
		List<Program> list3 = new List<Program>();
		foreach (ReadOnlyMemory<byte> r in Records(f, at3, num3, 3744))
		{
			list3.Add(new Program(BinaryPrimitives.ReadUInt64LittleEndian(r.Span), BinaryPrimitives.ReadUInt64LittleEndian(r.Span.Slice(8)), (from k in Enumerable.Range(0, 6)
				select BinaryPrimitives.ReadUInt64LittleEndian(r.Span.Slice(1672 + 8 * k))).ToArray()));
		}
		return new ShaderPack11
		{
			Shaders = list,
			Roots = list2,
			Programs = list3
		};
		bool Fits(long num13, long num12, long size)
		{
			if (num12 <= 1048576 && num13 >= 128 && num13 <= length)
			{
				return num12 * size <= length - num13;
			}
			return false;
		}
		long U(int start)
		{
			return (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(start)), 9223372036854775807uL);
		}
	}

	private static IEnumerable<ReadOnlyMemory<byte>> Records(Lz4Frame f, long at, long n, int size)
	{
		int per = Math.Max(1, 1048576 / size);
		for (long i = 0L; i < n; i += per)
		{
			int k = (int)Math.Min(per, n - i);
			byte[] b = f.Read(at + i * size, k * size);
			for (int j = 0; j < k; j++)
			{
				yield return b.AsMemory(j * size, size);
			}
		}
	}
}
