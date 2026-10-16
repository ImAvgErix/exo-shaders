using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.Carved;

public static class Dxbc
{
	public const int MaxSize = 67108864;

	private static readonly byte[][] Programs = new byte[3][]
	{
		"DXIL"u8.ToArray(),
		"SHEX"u8.ToArray(),
		"SHDR"u8.ToArray()
	};

	public const int MaxRootSignatureEntries = 4096;

	public static int HeaderSize(ReadOnlySpan<byte> h)
	{
		if (h.Length < 32 || !h.StartsWith("DXBC"u8))
		{
			return 0;
		}
		uint num = U(h, 20);
		uint num2 = U(h, 24);
		uint num3 = U(h, 28);
		if (num != 1 || num2 <= 32 || num2 > 67108864 || num3 < 1 || num3 > 64 || 32 + 4 * num3 > num2)
		{
			return 0;
		}
		return (int)num2;
	}

	public static bool Valid(ReadOnlySpan<byte> c)
	{
		if (c.IsEmpty || HeaderSize(c) != c.Length)
		{
			return false;
		}
		for (int i = 0; i < (int)U(c, 28); i++)
		{
			long num = U(c, 32 + 4 * i);
			if (num + 8 > c.Length)
			{
				return false;
			}
			ReadOnlySpan<byte> readOnlySpan = c.Slice((int)num, 4);
			for (int j = 0; j < readOnlySpan.Length; j++)
			{
				bool flag;
				switch (readOnlySpan[j])
				{
				case 48:
				case 49:
				case 50:
				case 51:
				case 52:
				case 53:
				case 54:
				case 55:
				case 56:
				case 57:
				case 65:
				case 66:
				case 67:
				case 68:
				case 69:
				case 70:
				case 71:
				case 72:
				case 73:
				case 74:
				case 75:
				case 76:
				case 77:
				case 78:
				case 79:
				case 80:
				case 81:
				case 82:
				case 83:
				case 84:
				case 85:
				case 86:
				case 87:
				case 88:
				case 89:
				case 90:
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				if (!flag)
				{
					return false;
				}
			}
			if (num + 8 + U(c, (int)num + 4) > c.Length)
			{
				return false;
			}
		}
		return true;
	}

	public static IEnumerable<(int Offset, byte[] Container)> Containers(byte[] b)
	{
		int num = 0;
		while (true)
		{
			int num2 = ((ReadOnlySpan<byte>)b.AsSpan(num)).IndexOf("DXBC"u8);
			if (num2 >= 0)
			{
				int at = num + num2;
				int size = HeaderSize(b.AsSpan(at));
				if (size > 0 && at + size <= b.Length && Valid(b.AsSpan(at, size)))
				{
					yield return (Offset: at, Container: b[at..(at + size)]);
					num = at + size;
				}
				else
				{
					num = at + 1;
				}
				continue;
			}
			break;
		}
	}

	public static ReadOnlySpan<byte> Part(ReadOnlySpan<byte> c, ReadOnlySpan<byte> fourcc)
	{
		for (int i = 0; i < (int)U(c, 28); i++)
		{
			int num = (int)U(c, 32 + 4 * i);
			if (c.Slice(num, 4).SequenceEqual(fourcc))
			{
				return c.Slice(num + 8, (int)U(c, num + 4));
			}
		}
		return default;
	}

	public static int Kind(ReadOnlySpan<byte> c)
	{
		byte[][] programs = Programs;
		foreach (byte[] array in programs)
		{
			ReadOnlySpan<byte> s = Part(c, array);
			if (s.Length >= 4)
			{
				return (int)(U(s, 0) >> 16);
			}
		}
		return -1;
	}

	public static (uint Min, uint Max)? WaveLanes(ReadOnlySpan<byte> c)
	{
		ReadOnlySpan<byte> s = Part(c, "PSV0"u8);
		if (s.Length < 28 || U(s, 0) < 24)
		{
			return null;
		}
		uint num = U(s, 20);
		uint num2 = U(s, 24);
		uint num3 = num;
		if (num3 != 0 || num2 != uint.MaxValue)
		{
			return (num3, num2);
		}
		return null;
	}

	public static bool InlineRayTracing(ReadOnlySpan<byte> c)
	{
		if (Valid(c))
		{
			ReadOnlySpan<byte> source = Part(c, "SFI0"u8);
			if (source.Length >= 8)
			{
				return (BinaryPrimitives.ReadUInt64LittleEndian(source) & 0x100000) != 0;
			}
		}
		return false;
	}

	public static bool IsRootSignatureOnly(ReadOnlySpan<byte> blob)
	{
		if (!Valid(blob))
		{
			return false;
		}
		int num = (int)U(blob, 28);
		for (int i = 0; i < num; i++)
		{
			if (!blob.Slice((int)U(blob, 32 + 4 * i), 4).SequenceEqual("RTS0"u8))
			{
				return false;
			}
		}
		return num > 0;
	}

	public static bool RootSignatureValid(ReadOnlySpan<byte> blob)
	{
		ReadOnlySpan<byte> s;
		if (blob.StartsWith("DXBC"u8))
		{
			s = (Valid(blob) ? Part(blob, "RTS0"u8) : default(ReadOnlySpan<byte>));
		}
		else
		{
			s = blob;
		}
		if (s.Length < 24)
		{
			return false;
		}
		uint num = U(s, 0);
		bool flag = ((num < 1 || num > 3) ? true : false);
		if (flag || (U(s, 20) & 0x7FFFF000) != 0)
		{
			return false;
		}
		bool flag2 = (U(s, 20) & 0x80) != 0;
		if (flag2 && (U(s, 20) & 0xFFFFFF7Fu) != 0)
		{
			return false;
		}
		ulong n = (ulong)s.Length;
		List<(uint Vis, uint Type, uint Space, uint Lb, uint Ub)> regs = new List<(uint, uint, uint, uint, uint)>();
		uint num2 = U(s, 4);
		uint num3 = U(s, 8);
		if (!Fits(num3, num2, 12uL))
		{
			return false;
		}
		ulong num4 = (ulong)num2 + (ulong)U(s, 12);
		if (num4 > 4096)
		{
			return false;
		}
		for (uint num5 = 0u; num5 < num2; num5++)
		{
			uint num6 = U(s, (int)(num3 + 12 * num5));
			uint num7 = U(s, (int)(num3 + 12 * num5 + 4));
			uint num8 = U(s, (int)(num3 + 12 * num5 + 8));
			if (num7 > 7 || (flag2 && num7 != 0))
			{
				return false;
			}
			switch (num6)
			{
			case 0u:
			{
				if (!Fits(num8, 1uL, 8uL))
				{
					return false;
				}
				uint num10 = U(s, (int)num8);
				uint num11 = U(s, (int)(num8 + 4));
				uint num12 = ((num == 1) ? 20u : 24u);
				if ((num4 += num10) > 4096 || !Fits(num11, num10, num12))
				{
					return false;
				}
				bool flag3 = false;
				bool flag4 = false;
				ulong num13 = 0uL;
				uint num14 = 0u;
				while (num14 < num10)
				{
					int num15 = (int)(num11 + num12 * num14);
					uint num16 = U(s, num15);
					uint num17 = U(s, num15 + 4);
					uint num18 = U(s, num15 + 8);
					uint space = U(s, num15 + 12);
					uint num19 = ((num != 1) ? U(s, num15 + 16) : 0u);
					uint num20 = U(s, num15 + ((num == 1) ? 16 : 20));
					if (num16 > 3 || num17 == 0)
					{
						return false;
					}
					if (num16 == 3)
					{
						flag3 = true;
					}
					else
					{
						flag4 = true;
					}
					if (flag3 & flag4)
					{
						return false;
					}
					ulong num21 = ((num20 == uint.MaxValue) ? num13 : num20);
					if (num21 > uint.MaxValue)
					{
						return false;
					}
					if (num17 == uint.MaxValue)
					{
						num13 = 4294967296uL;
					}
					else
					{
						if ((ulong)((long)num18 + (long)num17 - 1) > 4294967295uL || num21 + num17 - 1 > uint.MaxValue)
						{
							return false;
						}
						num13 = num21 + num17;
					}
					if ((num19 & 0xFFFEFFF0u) == 0)
					{
						bool num22;
						if (num16 != 3)
						{
							if (!OneData(num19))
							{
								goto IL_02c8;
							}
							num22 = (num19 & 9) == 9;
						}
						else
						{
							num22 = (num19 & 0x1000E) != 0;
						}
						if (!num22 && (num19 & 0x10001) != 65537)
						{
							Reg(num7, num16, space, num18, num17);
							num14++;
							continue;
						}
					}
					goto IL_02c8;
					IL_02c8:
					return false;
				}
				break;
			}
			case 1u:
				if (!Fits(num8, 1uL, 12uL))
				{
					return false;
				}
				Reg(num7, 2u, U(s, (int)(num8 + 4)), U(s, (int)num8), 1u);
				break;
			case 2u:
			case 3u:
			case 4u:
			{
				if (!Fits(num8, 1uL, (num == 1) ? 8u : 12u))
				{
					return false;
				}
				uint num9 = ((num != 1) ? U(s, (int)(num8 + 8)) : 0u);
				if ((num9 & 0xFFFFFFF1u) != 0 || !OneData(num9))
				{
					return false;
				}
				Reg(num7, (num6 == 2) ? 2u : ((num6 != 3) ? 1u : 0u), U(s, (int)(num8 + 4)), U(s, (int)num8), 1u);
				break;
			}
			default:
				return false;
			}
		}
		uint num23 = U(s, 12);
		uint num24 = U(s, 16);
		uint num25 = ((num == 3) ? 56u : 52u);
		if (!Fits(num24, num23, num25))
		{
			return false;
		}
		for (uint num26 = 0u; num26 < num23; num26++)
		{
			int num27 = (int)(num24 + num25 * num26);
			uint num28 = U(s, num27);
			uint num29 = num28 >> 7;
			flag = num29 > 3;
			bool flag5;
			if (!flag)
			{
				switch (num28 & 0x7F)
				{
				case 0u:
				case 1u:
				case 4u:
				case 5u:
				case 16u:
				case 17u:
				case 20u:
				case 21u:
				case 85u:
					flag5 = true;
					break;
				default:
					flag5 = false;
					break;
				}
				flag = !flag5;
			}
			if (flag)
			{
				return false;
			}
			for (int i = 4; i <= 12; i += 4)
			{
				uint num30 = U(s, num27 + i);
				if ((num30 < 1 || num30 > 5) ? true : false)
				{
					return false;
				}
			}
			float num31 = F(s, num27 + 16);
			float num32 = F(s, num27 + 32);
			float num33 = F(s, num27 + 36);
			flag = float.IsNaN(num31) || num31 < -16f || num31 > 15.99f || U(s, num27 + 20) > 16;
			if (!flag)
			{
				flag5 = num29 == 1;
				if (flag5)
				{
					uint num30 = U(s, num27 + 24);
					bool flag6 = ((num30 < 1 || num30 > 8) ? true : false);
					flag5 = flag6;
				}
				flag = flag5;
			}
			if (flag || float.IsNaN(num32) || float.IsNaN(num33) || U(s, num27 + 48) > 7 || (flag2 && U(s, num27 + 48) != 0))
			{
				return false;
			}
			uint num34 = ((num == 3) ? U(s, num27 + 52) : 0u);
			flag = (num34 & 0xFFFFFFFCu) != 0;
			if (!flag)
			{
				bool flag6 = (num34 & 1) != 0;
				if (flag6)
				{
					uint num30 = U(s, num27 + 28);
					bool flag7 = ((num30 == 0 || num30 - 3 <= 1) ? true : false);
					flag6 = !flag7;
				}
				flag = flag6;
			}
			flag5 = flag;
			if (!flag5)
			{
				bool flag6 = (num34 & 2) != 0;
				if (flag6)
				{
					uint num30 = num28 & 0x7F;
					bool flag7 = ((num30 == 0 || num30 == 20) ? true : false);
					bool flag8 = !flag7 || num29 == 1;
					if (!flag8)
					{
						uint num35 = U(s, num27 + 4);
						bool flag9 = num35 - 3 <= 1;
						flag8 = !flag9;
					}
					bool flag10 = flag8;
					if (!flag10)
					{
						uint num35 = U(s, num27 + 8);
						bool flag9 = num35 - 3 <= 1;
						flag10 = !flag9;
					}
					flag6 = flag10 || U(s, num27 + 28) > 4 || num31 != 0f || num32 != 0f || num33 != 0f;
				}
				flag5 = flag6;
			}
			if (flag5)
			{
				return false;
			}
			Reg(U(s, num27 + 48), 3u, U(s, num27 + 44), U(s, num27 + 40), 1u);
		}
		uint v;
		for (v = 0u; v <= 7; v++)
		{
			(uint, uint, ulong)? tuple = null;
			foreach (var item in from r in regs
				where r.Vis == v || r.Vis == 0
				orderby r.Type, r.Space, r.Lb
				select r)
			{
				if (tuple.HasValue)
				{
					(uint, uint, ulong) valueOrDefault = tuple.GetValueOrDefault();
					if (valueOrDefault.Item1 == item.Type && valueOrDefault.Item2 == item.Space)
					{
						if (item.Lb <= valueOrDefault.Item3)
						{
							return false;
						}
						tuple = (item.Type, item.Space, Math.Max(valueOrDefault.Item3, item.Ub));
						continue;
					}
				}
				tuple = (item.Type, item.Space, item.Ub);
			}
		}
		return true;
		bool Fits(ulong at, ulong count, ulong size)
		{
			return at + count * size <= n;
		}
		static bool OneData(uint flags)
		{
			return (flags & 0xE & ((flags & 0xE) - 1)) == 0;
		}
		void Reg(uint vis, uint type, uint item, uint lb, uint num36)
		{
			regs.Add((vis, type, item, lb, (num36 == uint.MaxValue) ? uint.MaxValue : (lb + (num36 - 1))));
		}
	}

	public static bool IsGraphics(int kind)
	{
		switch (kind)
		{
		case 0:
		case 1:
		case 2:
		case 3:
		case 4:
		case 13:
		case 14:
			return true;
		default:
			return false;
		}
	}

	private static uint U(ReadOnlySpan<byte> s, int o)
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o));
	}

	private static float F(ReadOnlySpan<byte> s, int o)
	{
		return BinaryPrimitives.ReadSingleLittleEndian(s.Slice(o));
	}
}
