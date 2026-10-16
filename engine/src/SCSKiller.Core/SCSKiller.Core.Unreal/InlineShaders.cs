using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SCSKiller.Core.Unreal;

public static class InlineShaders
{
	public readonly record struct Entry(int Map, int Offset, char Format, byte[] Code);

	private static readonly int[] HashSizes = new int[2] { 20, 8 };

	private const int MaxCode = 67108864;

	private const int MaxRatio = 64;

	public static int Carve(IReadOnlyList<byte[]> parts, Action<Entry> found)
	{
		int num = 0;
		int map = 0;
		int num2 = 0;
		foreach (byte[] part in parts)
		{
			num += Carve(part, num2, ref map, found);
			num2 += part.Length;
		}
		return num;
	}

	private static int Carve(byte[] d, int bias, ref int map, Action<Entry> found)
	{
		List<(int, int)> list = new List<(int, int)>();
		int num = 0;
		for (int i = 0; i + 8 <= d.Length; i++)
		{
			int num2 = I32(d, i);
			if ((num2 < 1 || num2 > 65535) ? true : false)
			{
				continue;
			}
			int[] hashSizes = HashSizes;
			foreach (int num3 in hashSizes)
			{
				long num4 = i + 4 + (long)num3 * (long)num2;
				if (num4 + 4 > d.Length || I32(d, (int)num4) != num2)
				{
					continue;
				}
				List<(int, char)> list2 = Chain(d, (int)num4 + 4, num2);
				if (list2 == null)
				{
					continue;
				}
				map++;
				int num5 = 0;
				foreach (var item3 in list2)
				{
					int item = item3.Item1;
					char item2 = item3.Item2;
					byte[] array = Decode(d, item, item2);
					if (array != null)
					{
						num5++;
						found(new Entry(map, bias + item, item2, array));
					}
				}
				if (num5 != 0)
				{
					num += list2.Count - num5;
					int num6 = Layout(d, list2[list2.Count - 1].Item1, list2[list2.Count - 1].Item2).Len + list2[list2.Count - 1].Item1;
					list.Add((i, num6));
					i = num6 - 1;
					break;
				}
			}
		}
		int i2 = ((ReadOnlySpan<byte>)d.AsSpan()).IndexOf((byte)120);
		while (i2 >= 0)
		{
			if (i2 >= 4 && Layout(d, i2 - 4, 'Z').Len > 0 && !list.Any(((int Start, int End) b) => i2 >= b.Start && i2 < b.End))
			{
				byte[] array2 = Decode(d, i2 - 4, 'Z');
				if (array2 != null)
				{
					found(new Entry(0, bias + i2 - 4, 'Z', array2));
				}
			}
			int num7 = ((ReadOnlySpan<byte>)d.AsSpan(i2 + 1)).IndexOf((byte)120);
			i2 = ((num7 >= 0) ? (i2 + 1 + num7) : (-1));
		}
		return num;
	}

	public static byte[]? Decode(byte[] d, int offset, char format)
	{
		var (num, num2, num3, num4) = Layout(d, offset, format);
		if (num == 0)
		{
			return null;
		}
		try
		{
			byte[] array;
			if (format == 'Z')
			{
				array = Inflate(d, num2, num3);
			}
			else
			{
				array = ((num3 == num4) ? d[num2..(num2 + num3)] : UnrealReader.Decompress(d[num2..(num2 + num3)], num4));
			}
			int len;
			return (ShaderContainer.Offset(array, out len) >= 0) ? array : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static List<(int Off, char Fmt)>? Chain(byte[] d, int e, int k)
	{
		char c = ((e + 8 <= d.Length && I64(d, e) == 12) ? 'B' : 'A');
		List<(int, char)> list = new List<(int, char)>();
		for (int i = 0; i < k; i++)
		{
			int item = Layout(d, e, c).Len;
			if (item == 0)
			{
				return null;
			}
			list.Add((e, c));
			e += item;
		}
		return list;
	}

	private static (int Len, int At, int N, int Size) Layout(byte[] d, int e, char fmt)
	{
		long num = d.Length;
		switch (fmt)
		{
		case 'A':
		{
			if (e + 9 > num)
			{
				break;
			}
			int num8 = I32(d, e);
			if (num8 > 0 && (long)e + 9L + num8 <= num)
			{
				int num9 = I32(d, e + 4 + num8);
				if (num9 >= num8 && num9 <= 67108864 && num9 <= 64L * (long)num8 && d[e + 8 + num8] < 16)
				{
					return (Len: 9 + num8, At: e + 4, N: num8, Size: num9);
				}
			}
			break;
		}
		case 'B':
		{
			if (e + 28 > num || I64(d, e) != 12)
			{
				break;
			}
			int num3 = I32(d, e + 8);
			int num4 = I32(d, e + 12);
			long num5 = I64(d, e + 20);
			if (num5 <= 0 || num5 > num3 || num3 > 67108864 || num3 > 64 * num5 || num4 > num3 || d[e + 16] >= 16 || e + 28 + num5 > num)
			{
				break;
			}
			int num6 = 28 + (int)num5;
			if (e + num6 + 32 <= num && BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(e + num6)) == 3077923682u)
			{
				ulong num7 = BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(e + num6 + 24));
				if (num7 < 64 || (ulong)((long)(e + num6) + (long)num7) > (ulong)num)
				{
					break;
				}
				num6 += (int)num7;
			}
			return (Len: num6, At: e + 28, N: (int)num5, Size: num3);
		}
		case 'Z':
		{
			if (e + 6 > num)
			{
				break;
			}
			int num2 = I32(d, e);
			bool flag = num2 < 3 || (long)e + 4L + num2 > num || d[e + 4] != 120;
			if (!flag)
			{
				bool flag2;
				switch (d[e + 5])
				{
				case 1:
				case 94:
				case 156:
				case 218:
					flag2 = true;
					break;
				default:
					flag2 = false;
					break;
				}
				flag = !flag2;
			}
			if (!flag)
			{
				return (Len: 4 + num2, At: e + 4, N: num2, Size: -1);
			}
			break;
		}
		}
		return default;
	}

	private static byte[] Inflate(byte[] d, int at, int n)
	{
		using ZLibStream zLibStream = new ZLibStream(new MemoryStream(d, at, n), CompressionMode.Decompress);
		using MemoryStream memoryStream = new MemoryStream();
		long num = Math.Min(67108864L, 64L * (long)n);
		Span<byte> buffer = stackalloc byte[16384];
		int num2;
		while ((num2 = zLibStream.Read(buffer)) > 0)
		{
			if (memoryStream.Length + num2 > num)
			{
				throw new InvalidDataException("inflates past the code bound");
			}
			memoryStream.Write(buffer.Slice(0, num2));
		}
		return memoryStream.ToArray();
	}

	private static int I32(byte[] d, int o)
	{
		return BitConverter.ToInt32(d, o);
	}

	private static long I64(byte[] d, int o)
	{
		return BitConverter.ToInt64(d, o);
	}
}
