using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CUE4Parse.Compression;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.App;

namespace SCSKiller.Core.FromSoft;

public static class Souls
{
	public sealed record Entry(ulong Hash, long Offset, long Size, byte[]? AesKey, (long Start, long End)[] AesRanges);

	public static byte[] DecryptBhd(ReadOnlySpan<byte> file, string key)
	{
		using RSA rSA = RSA.Create();
		rSA.ImportRSAPublicKey(Convert.FromBase64String(key), out var _);
		RSAParameters rSAParameters = rSA.ExportParameters(includePrivateParameters: false);
		BigInteger n = new BigInteger(rSAParameters.Modulus, isUnsigned: true, isBigEndian: true);
		BigInteger e = new BigInteger(rSAParameters.Exponent, isUnsigned: true, isBigEndian: true);
		int inSize = rSAParameters.Modulus.Length;
		int outSize = inSize - 1;
		byte[] input = file.ToArray();
		byte[] output = new byte[input.Length / inSize * outSize];
		Parallel.For(0, input.Length / inSize, new ParallelOptions
		{
			TaskScheduler = TaskScheduler.Current
		}, (int k) =>
		{
			byte[] array = BigInteger.ModPow(new BigInteger(input.AsSpan(k * inSize, inSize), isUnsigned: true, isBigEndian: true), e, n).ToByteArray(isUnsigned: true, isBigEndian: true);
			if (array.Length > outSize)
			{
				throw new InvalidDataException("not this archive's key");
			}
			array.CopyTo(output, k * outSize + outSize - array.Length);
		});
		return output;
	}

	public static List<Entry> ParseBhd(ReadOnlySpan<byte> b, bool hash64)
	{
		if (!b.StartsWith("BHD5"u8) || b[4] != byte.MaxValue)
		{
			throw new InvalidDataException("not a little-endian BHD5 header");
		}
		bool flag = b.Length > 40 && I32(b, 20) == 0 && I32(b, 28) == 0;
		long num = (flag ? I64(b, 16) : I32(b, 16));
		long num2 = (flag ? I64(b, 24) : I32(b, 20));
		List<Entry> list = new List<Entry>();
		int num3 = 0;
		while (num3 < num)
		{
			int num4 = I32(b, (int)num2);
			int num5 = (int)(flag ? I64(b, (int)num2 + 8) : I32(b, (int)num2 + 4));
			int num6 = 0;
			while (num6 < num4)
			{
				long num10;
				long offset;
				long num11;
				ulong hash65;
				if (hash64)
				{
					ulong num7 = U64(b, num5);
					long num8 = I32(b, num5 + 12);
					long num9 = I64(b, num5 + 16);
					num10 = I64(b, num5 + 32);
					offset = num9;
					num11 = num8;
					hash65 = num7;
				}
				else
				{
					ulong num12 = U32(b, num5);
					long num13 = I64(b, num5 + 32);
					long num14 = I64(b, num5 + 8);
					num10 = I64(b, num5 + 24);
					offset = num14;
					num11 = num13;
					hash65 = num12;
				}
				if (num11 <= 0)
				{
					num11 = I32(b, num5 + (hash64 ? 8 : 4));
				}
				byte[] aesKey = null;
				(long, long)[] array = Array.Empty<(long, long)>();
				if (num10 != 0L)
				{
					aesKey = b.Slice((int)num10, 16).ToArray();
					array = new (long, long)[I32(b, (int)num10 + 16)];
					for (int i = 0; i < array.Length; i++)
					{
						array[i] = (I64(b, (int)num10 + 20 + 16 * i), I64(b, (int)num10 + 28 + 16 * i));
					}
				}
				list.Add(new Entry(hash65, offset, num11, aesKey, array));
				num6++;
				num5 += 40;
			}
			num3++;
			num2 += (flag ? 16 : 8);
		}
		return list;
	}

	public static ulong NameHash(string path, bool hash64)
	{
		string text = path.Trim().Replace('\\', '/').ToLowerInvariant();
		if (!text.StartsWith('/'))
		{
			text = "/" + text;
		}
		if (hash64)
		{
			return text.Aggregate(0uL, (ulong h, char c) => h * 133 + c);
		}
		return text.Aggregate(0u, (uint h, char c) => h * 37 + c);
	}

	public static byte[] Read(SafeFileHandle bdt, Entry e)
	{
		byte[] array = new byte[(e.AesRanges.Length == 0) ? e.Size : Math.Max(e.Size, e.AesRanges.Max(((long Start, long End) r) => r.End))];
		if (RandomAccess.Read(bdt, array, e.Offset) != array.Length)
		{
			throw new EndOfStreamException("archive entry past the end of its .bdt");
		}
		if (e.AesKey != null)
		{
			using Aes aes = Aes.Create();
			aes.Key = e.AesKey;
			foreach (var (num, num2) in e.AesRanges.Where(((long Start, long End) r) => r.Start >= 0 && r.End > r.Start))
			{
				aes.DecryptEcb(array.AsSpan((int)num, (int)(num2 - num)), array.AsSpan((int)num), PaddingMode.None);
			}
		}
		if (array.Length != e.Size)
		{
			return array[..(int)e.Size];
		}
		return array;
	}

	public static bool IsDcx(ReadOnlySpan<byte> b)
	{
		if (b.Length >= 76)
		{
			return b.StartsWith("DCX\0"u8);
		}
		return false;
	}

	public static string DcxCodec(ReadOnlySpan<byte> b)
	{
		return Encoding.ASCII.GetString(b.Slice(40, 4));
	}

	public static byte[] Dcx(ReadOnlySpan<byte> b)
	{
		if (!IsDcx(b) || !b.Slice(24, 4).SequenceEqual("DCS\0"u8) || !b.Slice(68, 4).SequenceEqual("DCA\0"u8))
		{
			throw new InvalidDataException("not a DCX file");
		}
		int num = (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(28));
		int val = (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(32));
		ReadOnlySpan<byte> readOnlySpan = b.Slice(76, Math.Min(val, b.Length - 76));
		string text = DcxCodec(b);
		switch (text)
		{
		case "DFLT":
		{
			if (num < 0 || num > 1032L * (long)readOnlySpan.Length)
			{
				throw new InvalidDataException("DCX size past what its deflate data can hold");
			}
			byte[] array = new byte[num];
			using ZLibStream zLibStream = new ZLibStream(new MemoryStream(readOnlySpan.ToArray()), CompressionMode.Decompress);
			zLibStream.ReadExactly(array);
			return array;
		}
		case "KRAK":
			InitOodle();
			return Compression.Decompress(readOnlySpan.ToArray(), num, CompressionMethod.Oodle);
		case "ZSTD":
			return Compression.Decompress(readOnlySpan.ToArray(), num, CompressionMethod.Zstd);
		default:
			throw new NotSupportedException("DCX codec " + text);
		}
	}

	private static void InitOodle()
	{
		Codecs.Load(zlib: false);
	}

	public static bool IsBnd4(ReadOnlySpan<byte> b)
	{
		if (b.Length >= 64)
		{
			return b.StartsWith("BND4"u8);
		}
		return false;
	}

	public static IEnumerable<(string Name, byte[] Data)> Bnd4(byte[] b)
	{
		if (!IsBnd4(b) || b[9] != 0)
		{
			throw new InvalidDataException("not a little-endian BND4 binder");
		}
		bool bitBigEndian = b[10] == 0;
		bool unicode = b[48] != 0;
		byte b2 = b[49];
		byte b3 = ((bitBigEndian || ((b2 & 1) != 0 && (b2 & 0x80) == 0)) ? b2 : Reverse(b2));
		bool ids = (b3 & 2) != 0;
		bool names = (b3 & 0xC) != 0;
		bool longOffsets = (b3 & 0x10) != 0;
		bool compression = (b3 & 0x20) != 0;
		int count = I32(b, 12);
		int headerSize = (int)I64(b, 32);
		for (int i = 0; i < count; i++)
		{
			int num = 64 + i * headerSize;
			byte b4 = (bitBigEndian ? b[num] : Reverse(b[num]));
			long num2 = I64(b, num + 8);
			int num3 = num + 16 + (compression ? 8 : 0);
			long num4 = (longOffsets ? I64(b, num3) : U32(b, num3));
			num3 += (longOffsets ? 8 : 4) + (ids ? 4 : 0);
			string item = (names ? Str(b, (int)U32(b, num3), unicode) : $"#{i}");
			Span<byte> span = b.AsSpan((int)num4, (int)num2);
			yield return (Name: item, Data: ((b4 & 1) != 0) ? Dcx(span) : span.ToArray());
		}
	}

	private static string Str(byte[] b, int at, bool unicode)
	{
		if (!unicode)
		{
			int num = Array.IndexOf(b, (byte)0, at);
			return Encoding.Latin1.GetString(b, at, num - at);
		}
		int i;
		for (i = 0; at + 2 * i + 1 < b.Length && (b[at + 2 * i] | b[at + 2 * i + 1]) != 0; i++)
		{
		}
		return Encoding.Unicode.GetString(b, at, 2 * i);
	}

	private static byte Reverse(byte x)
	{
		return (byte)((ulong)((x * 8623620610L) & 0x10884422010L) % 1023uL);
	}

	private static int I32(ReadOnlySpan<byte> b, int o)
	{
		return BinaryPrimitives.ReadInt32LittleEndian(b.Slice(o));
	}

	private static uint U32(ReadOnlySpan<byte> b, int o)
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o));
	}

	private static long I64(ReadOnlySpan<byte> b, int o)
	{
		return BinaryPrimitives.ReadInt64LittleEndian(b.Slice(o));
	}

	private static ulong U64(ReadOnlySpan<byte> b, int o)
	{
		return BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(o));
	}
}
