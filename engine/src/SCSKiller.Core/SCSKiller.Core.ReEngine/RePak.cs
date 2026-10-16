using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace SCSKiller.Core.ReEngine;

public sealed class RePak : IDisposable
{
	public readonly record struct Entry(ulong Hash, long Offset, long Packed, long Size, ulong Attr)
	{
		public int Compression => (int)(Attr & 0xF);

		public int Encryption => (int)((Attr >> 16) & 0xFF);

		public bool Chunked => (Attr & 0x1000000) != 0;
	}

	private sealed class Region(SafeFileHandle h, long start, long length) : Source
	{
		private long pos;

		public override int Read(Span<byte> buffer)
		{
			int num = (int)Math.Min(buffer.Length, length - pos);
			if (num <= 0)
			{
				return 0;
			}
			num = RandomAccess.Read(h, buffer.Slice(0, num), start + pos);
			pos += num;
			return num;
		}
	}

	private sealed class Chunks(RePak pak, long next) : Source
	{
		private readonly Decompressor zstd = new Decompressor();

		private byte[] block = Array.Empty<byte>();

		private int pos;

		public override int Read(Span<byte> buffer)
		{
			if (buffer.IsEmpty)
			{
				return 0;
			}
			if (pos == block.Length)
			{
				if (next >= pak.chunks.Length)
				{
					throw new InvalidDataException($"chunk {next} past the chunk table");
				}
				if (block.Length == 0)
				{
					block = new byte[pak.blockSize];
				}
				(long Start, int Packed) tuple = pak.chunks[next];
				long item = tuple.Start;
				int item2 = tuple.Packed;
				byte[] array = pak.Bytes(item, item2);
				if (item2 == pak.blockSize)
				{
					array.CopyTo(block, 0);
				}
				else if (zstd.Unwrap(array, block) != pak.blockSize)
				{
					throw new InvalidDataException($"chunk {next} doesn't decode to the block size");
				}
				long num = next + 1;
				next = num;
				pos = 0;
			}
			int num2 = Math.Min(buffer.Length, block.Length - pos);
			block.AsSpan(pos, num2).CopyTo(buffer);
			pos += num2;
			return num2;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				zstd.Dispose();
			}
			base.Dispose(disposing);
		}
	}

	private abstract class Source : Stream
	{
		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length
		{
			get
			{
				throw new NotSupportedException();
			}
		}

		public override long Position
		{
			get
			{
				throw new NotSupportedException();
			}
			set
			{
				throw new NotSupportedException();
			}
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			return Read(buffer.AsSpan(offset, count));
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}
	}

	public const ushort ExtraU32 = 16;

	public const ushort ExtraData = 4;

	public const ushort EncryptedTable = 8;

	public const ushort ChunkTable = 32;

	public const ushort RemapTable = 64;

	public const int MaxTable = 67108864;

	public const int MaxChunkTable = 33554432;

	public const int MaxBlock = 16777216;

	public const int MaxEntry = 268435456;

	public const int MaxWindowLog = 25;

	private readonly SafeFileHandle h;

	private readonly long length;

	private readonly int blockSize;

	private readonly (long Start, int Packed)[] chunks = Array.Empty<(long, int)>();

	public const string NoModulus = "the entry table is encrypted and no table-key modulus opens it";

	public string Path { get; }

	public int Major { get; }

	public int Minor { get; }

	public ushort Features { get; }

	public IReadOnlyList<Entry> Entries { get; }

	private RePak(string path, IReadOnlyList<BigInteger> moduli)
	{
		Path = path;
		h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
		try
		{
			long num = (length = RandomAccess.GetLength(h));
			byte[] array = Bytes(0L, 16);
			if (!((ReadOnlySpan<byte>)array.AsSpan(0, 4)).SequenceEqual("KPKA"u8))
			{
				throw new InvalidDataException("not a KPKA package");
			}
			int num2 = array[4];
			int num3 = array[5];
			ushort num4 = BinaryPrimitives.ReadUInt16LittleEndian(array.AsSpan(6));
			Major = num2;
			Minor = num3;
			Features = num4;
			int count = BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(8));
			num3 = Major;
			bool flag = ((num3 == 2 || num3 == 4) ? true : false);
			if (!flag || Minor > 2)
			{
				throw new InvalidDataException($"package version {Major}.{Minor} is not supported");
			}
			if ((Features & -125) != 0)
			{
				throw new InvalidDataException($"unknown package features 0x{Features:x}");
			}
			int size = ((Major == 2 && Minor == 0) ? 24 : 48);
			if ((uint)count * size > 67108864)
			{
				throw new InvalidDataException($"entry table over {64} MB");
			}
			if (count * size > num - 16)
			{
				throw new InvalidDataException("entry table past the end of the file");
			}
			byte[] stored = Bytes(16L, count * size);
			byte[] table = stored;
			long num5 = 16 + stored.Length + (((Features & 0x10) != 0) ? 4 : 0) + (((Features & 4) != 0) ? 9 : 0);
			if ((Features & 0x40) != 0)
			{
				uint num6 = BinaryPrimitives.ReadUInt32LittleEndian(Bytes(num5, 8));
				if (num6 > (num - num5 - 8) / 16)
				{
					throw new InvalidDataException("bad remap table");
				}
				num5 += 8 + 16L * (long)num6;
			}
			if ((Features & 8) != 0)
			{
				byte[] raw = Bytes(num5, 128);
				num5 += 128;
				table = moduli.Select((BigInteger m) =>
				{
					byte[] array5 = stored.ToArray();
					Xor(array5, Key(raw, m));
					return array5;
				}).FirstOrDefault((byte[] t) => Enumerable.Range(0, count).All((int i) => BinaryPrimitives.ReadUInt64LittleEndian(t.AsSpan(i * size + ((size != 24) ? 16 : 0))) < 1099511627776L && BinaryPrimitives.ReadUInt64LittleEndian(t.AsSpan(i * size + ((size == 24) ? 8 : 24))) < 1099511627776L)) ?? throw new InvalidDataException("the entry table is encrypted and no table-key modulus opens it");
			}
			if ((Features & 0x20) != 0)
			{
				byte[] array2 = Bytes(num5, 8);
				blockSize = BinaryPrimitives.ReadInt32LittleEndian(array2);
				int num7 = BinaryPrimitives.ReadInt32LittleEndian(array2.AsSpan(4));
				if ((long)(uint)num7 * 8L > 33554432)
				{
					throw new InvalidDataException($"chunk table over {32} MB");
				}
				num3 = blockSize;
				flag = ((num3 <= 0 || num3 > 16777216) ? true : false);
				if (flag || 8L * (long)num7 > num - num5 - 8)
				{
					throw new InvalidDataException("bad chunk table");
				}
				byte[] array3 = Bytes(num5 + 8, num7 * 8);
				chunks = new (long, int)[num7];
				long num8 = 0L;
				uint num9 = ((num7 > 0) ? BinaryPrimitives.ReadUInt32LittleEndian(array3) : 0u);
				for (int num10 = 0; num10 < num7; num10++)
				{
					uint num11 = BinaryPrimitives.ReadUInt32LittleEndian(array3.AsSpan(8 * num10));
					if (num11 < num9)
					{
						num8 += 4294967296L;
					}
					chunks[num10] = (Start: num8 | num11, Packed: (int)(BinaryPrimitives.ReadUInt32LittleEndian(array3.AsSpan(8 * num10 + 4)) >> 10));
					num9 = num11;
				}
			}
			Entry[] array4 = new Entry[count];
			for (int num12 = 0; num12 < count; num12++)
			{
				int at0 = num12 * size;
				array4[num12] = ((size == 24) ? new Entry(U(16), (long)U(0), (long)U(8), (long)U(8), 0uL) : new Entry(U(0), (long)U(8), (long)U(16), (long)U(24), U(32)));
				ulong U(int o)
				{
					return BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(at0 + o));
				}
			}
			Entries = array4;
		}
		catch
		{
			h.Dispose();
			throw;
		}
	}

	public static RePak Open(string path, IReadOnlyList<BigInteger>? moduli = null)
	{
		return new RePak(path, moduli ?? Array.Empty<BigInteger>());
	}

	public void Dispose()
	{
		h.Dispose();
	}

	public static byte[] Key(ReadOnlySpan<byte> raw, BigInteger modulus)
	{
		byte[] array = BigInteger.ModPow(new BigInteger(raw, isUnsigned: true), 65537, modulus).ToByteArray(isUnsigned: true);
		if (array.Length < 32)
		{
			Array.Resize(ref array, 32);
		}
		return array;
	}

	public static void Xor(Span<byte> b, byte[] k)
	{
		for (int i = 0; i < b.Length; i++)
		{
			b[i] ^= (byte)(i + k[i % 32] * k[i % 29]);
		}
	}

	public byte[] Read(Entry e, int max = int.MaxValue)
	{
		if (e.Encryption != 0)
		{
			throw new InvalidDataException($"resource-encrypted entry (type {e.Encryption})");
		}
		long num = ((e.Chunked && e.Size == 0L) ? e.Packed : e.Size);
		if (e.Offset < 0 || e.Packed < 0 || num < 0 || (!e.Chunked && e.Packed > length - e.Offset))
		{
			throw new InvalidDataException("entry outside the file");
		}
		if (num > Array.MaxLength || (e.Chunked && num > Math.Max(chunks.Length - e.Offset, 0L) * blockSize))
		{
			throw new InvalidDataException("entry too large");
		}
		bool flag = e.Compression == 0 && !e.Chunked;
		int num2 = (int)Math.Min(Math.Min(num, max), flag ? e.Packed : num);
		if (num2 > 268435456)
		{
			throw new InvalidDataException($"entry over {256} MB");
		}
		Stream stream;
		if (e.Chunked)
		{
			stream = new Chunks(this, e.Offset);
		}
		else
		{
			int compression = e.Compression;
			stream = compression switch
			{
				0 => (Stream)new Region(h, e.Offset, e.Packed), 
				1 => new DeflateStream(new Region(h, e.Offset, e.Packed), CompressionMode.Decompress), 
				2 => Zstd(new Region(h, e.Offset, e.Packed)), 
				_ => throw new InvalidDataException($"unknown compression {compression}"), 
			};
		}
		using Stream stream2 = stream;
		byte[] array = new byte[flag ? num2 : Math.Min(num2, 1048576)];
		int num3 = 0;
		while ((num3 += stream2.ReadAtLeast(array.AsSpan(num3), array.Length - num3, throwOnEndOfStream: false)) == array.Length && num3 < num2)
		{
			Array.Resize(ref array, (int)Math.Min(num2, 2L * (long)array.Length));
		}
		return (num3 == array.Length) ? array : array[..num3];
	}

	private static DecompressionStream Zstd(Stream s)
	{
		DecompressionStream decompressionStream = new DecompressionStream(s);
		decompressionStream.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 25);
		return decompressionStream;
	}

	private byte[] Bytes(long at, int n)
	{
		byte[] array = new byte[n];
		if (RandomAccess.Read(h, array, at) != n)
		{
			throw new InvalidDataException($"{System.IO.Path.GetFileName(Path)}: truncated at {at}");
		}
		return array;
	}
}
