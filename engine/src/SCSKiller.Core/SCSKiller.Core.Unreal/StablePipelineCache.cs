using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace SCSKiller.Core.Unreal;

public static class StablePipelineCache
{
	public enum PsoType : uint
	{
		Compute,
		Graphics,
		RayTracing
	}

	public sealed record Pso(ulong Key, PsoType Type, string[] Shaders);

	public const uint NewestVersion = 31u;

	private const ulong Magic = 5785243454745428808uL;

	private const ulong TocMagic = 6075148447521002034uL;

	private const ulong TocMagic17 = 6075148447521002068uL;

	private const ulong EofMagic = 4994287672160768587uL;

	public static List<Pso>? Read(ReadOnlySpan<byte> b)
	{
		List<Pso>? list = Read(b, 4);
		if (list == null)
		{
			uint? num = Version(b);
			if (num.HasValue && num == 17)
			{
				return null;
			}
			list = Read(b, 8);
		}
		return list;
	}

	private static List<Pso>? Read(ReadOnlySpan<byte> b, int keyWidth)
	{
		try
		{
			bool flag = b.Length < 57 || U64(b, 0) != 5785243454745428808L;
			if (!flag)
			{
				bool flag2;
				switch (U32(b, 8))
				{
				case 17u:
				case 22u:
				case 23u:
				case 24u:
				case 25u:
				case 26u:
				case 27u:
				case 28u:
				case 29u:
				case 30u:
				case 31u:
					flag2 = true;
					break;
				default:
					flag2 = false;
					break;
				}
				flag = !flag2;
			}
			if (flag || U64(b, b.Length - 8) != 4994287672160768587L)
			{
				return null;
			}
			int num = checked((int)U64(b, 33));
			bool flag3 = U32(b, 8) == 17;
			int num2 = ((U32(b, 8) >= 31) ? 8 : 20);
			if (U64(b, num) != (ulong)(flag3 ? 6075148447521002068L : 6075148447521002034L))
			{
				return null;
			}
			num += 8;
			if (!flag3)
			{
				num += ((b[num] == 0) ? 1 : 17);
			}
			num += 4;
			int num3 = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(num));
			num += 4;
			if (num3 < 0 || num3 > b.Length / (flag3 ? 86 : 94))
			{
				return null;
			}
			List<Pso> list = new List<Pso>(num3);
			for (int i = 0; i < num3; i++)
			{
				ulong key = ((keyWidth == 8) ? U64(b, num) : U32(b, num));
				int o = checked((int)U64(b, num + keyWidth));
				num += keyWidth + 16 + 16 + 32 + keyWidth;
				int num4 = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(num));
				num += 4;
				if ((num4 < 0 || num4 > 8) ? true : false)
				{
					return null;
				}
				string[] array = new string[num4];
				for (int j = 0; j < num4; j++)
				{
					array[j] = Convert.ToHexStringLower(b.Slice(num + num2 * j, num2)).PadRight(40, '0');
				}
				num += num2 * num4 + 8 + 2 + ((!flag3) ? 8 : 0);
				list.Add(new Pso(key, (PsoType)U32(b, o), array));
			}
			return (num == b.Length - 8) ? list : null;
		}
		catch (Exception ex) when ((ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException || ex is OverflowException) ? true : false)
		{
			return null;
		}
	}

	public static uint? Version(ReadOnlySpan<byte> b)
	{
		if (b.Length < 12 || U64(b, 0) != 5785243454745428808L)
		{
			return null;
		}
		return U32(b, 8);
	}

	private static ulong U64(ReadOnlySpan<byte> b, int o)
	{
		return BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(o));
	}

	private static uint U32(ReadOnlySpan<byte> b, int o)
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o));
	}
}
