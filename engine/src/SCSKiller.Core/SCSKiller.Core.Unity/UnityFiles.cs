using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;
using SevenZip.Compression.LZMA;

namespace SCSKiller.Core.Unity;

public static class UnityFiles
{
	public delegate byte[] ReadAt(long offset, int count);

	public sealed record Obj(long PathId, long Start, long Size, int ClassId);

	public sealed record SerializedFile(int Format, string UnityVersion, IReadOnlyList<Obj> Objects);

	public sealed record Node(long Offset, long Size, uint Flags, string Path);

	public sealed class Bundle : IDisposable
	{
		private const int CachedBlocks = 16;

		private readonly SafeFileHandle file;

		private readonly (long UOff, int USize, long COff, int CSize, int Comp)[] blocks;

		private readonly List<(int Index, byte[] Data)> cache = new List<(int, byte[])>();

		public string UnityVersion { get; }

		public IReadOnlyList<Node> Nodes { get; }

		public long BlocksDecompressed { get; private set; }

		private Bundle(SafeFileHandle file, string unity, (long, int, long, int, int)[] blocks, Node[] nodes)
		{
			this.file = file;
			UnityVersion = unity;
			this.blocks = blocks;
			Nodes = nodes;
		}

		public static bool IsBundle(ReadOnlySpan<byte> head)
		{
			return head.StartsWith("UnityFS\0"u8);
		}

		public static Bundle? Open(string path)
		{
			SafeFileHandle safeFileHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
			try
			{
				Bundle bundle = Open(safeFileHandle, RandomAccess.GetLength(safeFileHandle));
				return (bundle != null) ? bundle : Close(safeFileHandle);
			}
			catch
			{
				safeFileHandle.Dispose();
				throw;
			}
			static Bundle? Close(SafeFileHandle f)
			{
				f.Dispose();
				return null;
			}
		}

		private static Bundle? Open(SafeFileHandle f, long length)
		{
			byte[] array = new byte[Math.Min(512L, length)];
			RandomAccess.Read(f, array, 0L);
			if (!IsBundle(array))
			{
				return null;
			}
			Reader reader = new Reader(array, bigEndian: true);
			reader.Skip(8);
			uint num = reader.U32();
			string unity = reader.CString();
			reader.CString();
			reader.I64();
			int num2 = (int)reader.U32();
			int num3 = (int)reader.U32();
			uint num4 = reader.U32();
			bool flag = ((num2 < 0 || num2 > 67108864) ? true : false);
			bool flag2 = flag;
			if (!flag2)
			{
				bool flag3 = ((num3 < 0 || num3 > 67108864) ? true : false);
				flag2 = flag3;
			}
			if (flag2 || num2 > length)
			{
				throw new InvalidDataException("bad UnityFS blocks info size");
			}
			if (num >= 7)
			{
				reader.Align(16);
			}
			long num5 = reader.Pos;
			byte[] array2 = new byte[num2];
			RandomAccess.Read(f, array2, ((num4 & 0x80) != 0) ? (length - num2) : num5);
			if ((num4 & 0x80) == 0)
			{
				num5 += num2;
			}
			if ((num4 & 0x200) != 0)
			{
				num5 = (num5 + 15) & -16;
			}
			Reader reader2 = new Reader(Decompress(array2, num3, (int)(num4 & 0x3F)), bigEndian: true);
			reader2.Skip(16);
			(long, int, long, int, int)[] array3 = new (long, int, long, int, int)[reader2.Count(16777216)];
			long num6 = 0L;
			long num7 = num5;
			for (int i = 0; i < array3.Length; i++)
			{
				int num8 = (int)reader2.U32();
				int num9 = (int)reader2.U32();
				int num10 = reader2.U16();
				if (num8 < 0 || num9 < 0)
				{
					throw new InvalidDataException("UnityFS block over 2 GB");
				}
				if (num7 + num9 > length)
				{
					throw new InvalidDataException("UnityFS block past the end of the file");
				}
				int num11 = num10 & 0x3F;
				flag2 = (uint)(num11 - 2) <= 1u;
				if (flag2 && num8 > 256L * (long)num9 + 64)
				{
					throw new InvalidDataException("UnityFS block larger than LZ4 can expand to");
				}
				array3[i] = (num6, num8, num7, num9, num10 & 0x3F);
				num6 += num8;
				num7 += num9;
			}
			Node[] array4 = new Node[reader2.Count(1048576)];
			for (int j = 0; j < array4.Length; j++)
			{
				array4[j] = new Node(reader2.I64(), reader2.I64(), reader2.U32(), reader2.CString());
			}
			return new Bundle(f, unity, array3, array4);
		}

		public byte[] Read(long offset, int count)
		{
			byte[] array = new byte[count];
			int num = -1;
			int num2 = 0;
			int num3 = blocks.Length - 1;
			while (num2 <= num3)
			{
				int num4 = (num2 + num3) / 2;
				if (blocks[num4].UOff <= offset)
				{
					num2 = num4 + 1;
					num = num4;
				}
				else
				{
					num3 = num4 - 1;
				}
			}
			int num5 = 0;
			while (num5 < count && num >= 0 && num < blocks.Length)
			{
				byte[] array2 = Block(num);
				int num6 = (int)(offset + num5 - blocks[num].UOff);
				int num7 = Math.Min(count - num5, array2.Length - num6);
				if (num7 <= 0)
				{
					break;
				}
				array2.AsSpan(num6, num7).CopyTo(array.AsSpan(num5));
				num5 += num7;
				num++;
			}
			return array;
		}

		public ReadAt Reader(Node node)
		{
			return (long off, int count) => Read(node.Offset + off, (int)Math.Max(0L, Math.Min(count, node.Size - off)));
		}

		private byte[] Block(int i)
		{
			int num = cache.FindIndex(((int Index, byte[] Data) c) => c.Index == i);
			if (num >= 0)
			{
				(int, byte[]) item = cache[num];
				cache.RemoveAt(num);
				cache.Add(item);
				return item.Item2;
			}
			(long UOff, int USize, long COff, int CSize, int Comp) tuple = blocks[i];
			int item2 = tuple.USize;
			long item3 = tuple.COff;
			int item4 = tuple.CSize;
			int item5 = tuple.Comp;
			byte[] array = new byte[item4];
			RandomAccess.Read(file, array, item3);
			BlocksDecompressed++;
			byte[] array2 = Decompress(array, item2, item5);
			if (cache.Count == 16)
			{
				cache.RemoveAt(0);
			}
			cache.Add((i, array2));
			return array2;
		}

		public void Dispose()
		{
			file.Dispose();
			cache.Clear();
		}
	}

	public sealed record Segment(uint Platform, int Offset, int CompressedLength, int DecompressedLength);

	private sealed class Reader(byte[] b, bool bigEndian)
	{
		public int Pos;

		private ReadOnlySpan<byte> Take(int n)
		{
			if (n < 0 || Pos + n > b.Length)
			{
				throw new ArgumentOutOfRangeException("n");
			}
			Pos += n;
			return b.AsSpan(Pos - n, n);
		}

		public byte U8()
		{
			return Take(1)[0];
		}

		public short I16()
		{
			return (short)U16();
		}

		public ushort U16()
		{
			if (!bigEndian)
			{
				return BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
			}
			return BinaryPrimitives.ReadUInt16BigEndian(Take(2));
		}

		public uint U32()
		{
			if (!bigEndian)
			{
				return BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
			}
			return BinaryPrimitives.ReadUInt32BigEndian(Take(4));
		}

		public int I32()
		{
			return (int)U32();
		}

		public long I64()
		{
			if (!bigEndian)
			{
				return BinaryPrimitives.ReadInt64LittleEndian(Take(8));
			}
			return BinaryPrimitives.ReadInt64BigEndian(Take(8));
		}

		public int Count(int max)
		{
			int num = I32();
			if (num < 0 || num > max)
			{
				throw new ArgumentOutOfRangeException("max");
			}
			return num;
		}

		public void Skip(int n)
		{
			Take(n);
		}

		public void Align4()
		{
			Align(4);
		}

		public void Align(int a)
		{
			Pos = Math.Min(b.Length, (Pos + a - 1) / a * a);
		}

		public string CString()
		{
			int num = ((ReadOnlySpan<byte>)b.AsSpan(Pos)).IndexOf((byte)0);
			if (num < 0)
			{
				throw new ArgumentOutOfRangeException("n");
			}
			string result = Encoding.UTF8.GetString(Take(num));
			Pos++;
			return result;
		}
	}

	public const int ShaderClass = 48;

	public const int ComputeShaderClass = 72;

	public const int BuildSettingsClass = 141;

	public const uint D3D11Platform = 4u;

	public static SerializedFile? ReadSerialized(ReadAt read, long length)
	{
		byte[] h = read(0L, 48);
		if (h.Length < 48)
		{
			return null;
		}
		long num = Be(0);
		long num2 = Be(4);
		long num3 = Be(12);
		int num4 = (int)Be(8);
		int num5 = 20;
		if ((num4 < 9 || num4 > 64) ? true : false)
		{
			return null;
		}
		if (num4 >= 22)
		{
			num = Be(20);
			num2 = BinaryPrimitives.ReadInt64BigEndian(h.AsSpan(24));
			num3 = BinaryPrimitives.ReadInt64BigEndian(h.AsSpan(32));
			num5 = 48;
		}
		bool flag = num2 != length || num3 > length;
		if (!flag)
		{
			bool flag2 = ((num <= 0 || num > 268435456) ? true : false);
			flag = flag2;
		}
		if (flag || num5 + num > length)
		{
			return null;
		}
		try
		{
			Reader reader = new Reader(read(num5, (int)num), h[16] != 0);
			string unityVersion = reader.CString();
			reader.I32();
			bool flag3 = num4 < 13 || reader.U8() != 0;
			int[] array = new int[reader.Count(1048576)];
			for (int i = 0; i < array.Length; i++)
			{
				int num6 = (array[i] = reader.I32());
				if (num4 >= 16)
				{
					reader.U8();
				}
				if (num4 >= 17)
				{
					reader.I16();
				}
				if (num4 >= 13)
				{
					reader.Skip(((num4 < 16) ? (num6 < 0) : (num6 == 114)) ? 32 : 16);
				}
				if (flag3)
				{
					if (num4 < 12 && num4 != 10)
					{
						return null;
					}
					int num7 = reader.Count(1048576);
					int num8 = reader.Count(67108864);
					reader.Skip(num7 * ((num4 >= 19) ? 32 : 24) + num8);
					if (num4 >= 21)
					{
						reader.Skip(4 * reader.Count(1048576));
					}
				}
			}
			if (num4 >= 7 && num4 < 14)
			{
				reader.I32();
			}
			Obj[] array2 = new Obj[reader.Count(16777216)];
			for (int j = 0; j < array2.Length; j++)
			{
				if (num4 >= 14)
				{
					reader.Align4();
				}
				long pathId = ((num4 >= 14) ? reader.I64() : reader.I32());
				long num9 = ((num4 >= 22) ? reader.I64() : reader.U32());
				long size = reader.U32();
				int num10 = reader.I32();
				int classId;
				if (num4 < 16)
				{
					classId = num10;
				}
				else
				{
					classId = (((uint)num10 < array.Length) ? array[num10] : (-1));
				}
				if (num4 < 16)
				{
					reader.I16();
				}
				if (num4 < 11)
				{
					reader.I16();
				}
				if (num4 >= 11 && num4 < 17)
				{
					reader.I16();
				}
				if (num4 >= 15 && num4 < 17)
				{
					reader.U8();
				}
				array2[j] = new Obj(pathId, num3 + num9, size, classId);
			}
			return new SerializedFile(num4, unityVersion, array2);
		}
		catch (ArgumentOutOfRangeException)
		{
			return null;
		}
		uint Be(int o)
		{
			return BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(o));
		}
	}

	public static byte[] Decompress(byte[] src, int size, int comp)
	{
		switch (comp)
		{
		case 0:
			return src;
		case 2:
		case 3:
		{
			byte[] array = new byte[size];
			if (LZ4Codec.Decode(src, array) != size)
			{
				throw new InvalidDataException("bad LZ4 block");
			}
			return array;
		}
		case 1:
		{
			SevenZip.Compression.LZMA.Decoder decoder = new SevenZip.Compression.LZMA.Decoder();
			decoder.SetDecoderProperties(src[..5]);
			using MemoryStream inStream = new MemoryStream(src, 5, src.Length - 5);
			using MemoryStream memoryStream = new MemoryStream(size);
			decoder.Code(inStream, memoryStream, src.Length - 5, size, null);
			if (memoryStream.Length != size)
			{
				throw new InvalidDataException("bad LZMA block");
			}
			return memoryStream.GetBuffer()[..size];
		}
		default:
			throw new InvalidDataException($"unknown UnityFS compression {comp}");
		}
	}

	public static List<byte[]>? ShaderBlob(ReadOnlySpan<byte> o)
	{
		for (int i = 0; i + 16 <= o.Length; i += 4)
		{
			uint num = U(o, i);
			if ((num > 32 || num == 0) ? true : false)
			{
				continue;
			}
			bool[] array = new bool[2] { true, false };
			foreach (bool nested in array)
			{
				(List<Segment>, int)? tuple = Shape(o, i, (int)num, nested);
				if (tuple.HasValue)
				{
					(List<Segment>, int) valueOrDefault = tuple.GetValueOrDefault();
					List<Segment> item = valueOrDefault.Item1;
					int item2 = valueOrDefault.Item2;
					List<byte[]> list = Decode(o, item, item2);
					if (list != null)
					{
						return list;
					}
				}
			}
		}
		return null;
	}

	private static (List<Segment>, int)? Shape(ReadOnlySpan<byte> o, int p, int n, bool nested)
	{
		int num = p + 4;
		if (num + 4 * n > o.Length)
		{
			return null;
		}
		uint[] array = new uint[n];
		for (int i = 0; i < n; i++)
		{
			if ((array[i] = U(o, num + 4 * i)) > 64)
			{
				return null;
			}
		}
		num += 4 * n;
		List<uint[]>[] array2 = new List<uint[]>[3];
		for (int j = 0; j < 3; j++)
		{
			array2[j] = new List<uint[]>();
			if (nested)
			{
				if (num + 4 > o.Length || U(o, num) != n)
				{
					return null;
				}
				num += 4;
				for (int k = 0; k < n; k++)
				{
					uint[] array3 = UArray(o, ref num, 16);
					if (array3 != null && array3.Length > 0 && (j == 0 || array3.Length == array2[0][k].Length))
					{
						array2[j].Add(array3);
						continue;
					}
					return null;
				}
			}
			else
			{
				uint[] array4 = UArray(o, ref num, n);
				if (array4 == null || array4.Length != n)
				{
					return null;
				}
				array2[j].AddRange(array4.Select((uint v) => new uint[1] { v }));
			}
		}
		if (num + 4 > o.Length)
		{
			return null;
		}
		uint num2 = U(o, num);
		num += 4;
		if (num2 == 0 || num + num2 > o.Length)
		{
			return null;
		}
		List<Segment> list = new List<Segment>();
		for (int num3 = 0; num3 < n; num3++)
		{
			for (int num4 = 0; num4 < array2[0][num3].Length; num4++)
			{
				uint num5 = array2[0][num3][num4];
				uint num6 = array2[1][num3][num4];
				uint num7 = array2[2][num3][num4];
				if ((long)num5 + (long)num6 > num2 || num7 > 268435456)
				{
					return null;
				}
				if (array[num3] == 4 && num6 != 0)
				{
					list.Add(new Segment(array[num3], (int)num5, (int)num6, (int)num7));
				}
			}
		}
		return (list, num);
	}

	private static uint[]? UArray(ReadOnlySpan<byte> o, ref int q, int max)
	{
		if (q + 4 > o.Length)
		{
			return null;
		}
		uint num = U(o, q);
		if (num > max || q + 4 + 4 * num > o.Length)
		{
			return null;
		}
		uint[] array = new uint[num];
		for (int i = 0; i < num; i++)
		{
			array[i] = U(o, q + 4 + 4 * i);
		}
		q += (int)(4 + 4 * num);
		return array;
	}

	private const uint ZstdMagic = 0xFD2FB528u;

	[ThreadStatic]
	private static ZstdSharp.Decompressor? zstd;

	private static List<byte[]>? Decode(ReadOnlySpan<byte> o, List<Segment> segs, int blobAt)
	{
		if (segs.Count == 0)
		{
			return null;
		}
		List<byte[]> list = new List<byte[]>();
		foreach (Segment seg in segs)
		{
			ReadOnlySpan<byte> source = o.Slice(blobAt + seg.Offset, seg.CompressedLength);
			if (seg.CompressedLength == seg.DecompressedLength)
			{
				list.Add(source.ToArray());
				continue;
			}
			byte[] array = new byte[seg.DecompressedLength];
			if (source.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(source) == ZstdMagic)
			{
				// Custom Unity builds (Aniimo's, FunPlus) compress shader code with zstd instead of LZ4.
				try
				{
					if ((zstd ??= new ZstdSharp.Decompressor()).Unwrap(source, array) != array.Length)
					{
						return null;
					}
				}
				catch (ZstdSharp.ZstdException)
				{
					return null;
				}
			}
			else if (LZ4Codec.Decode(source, array) != array.Length)
			{
				return null;
			}
			list.Add(array);
		}
		return list;
	}

	public static int[]? GraphicsApis(ReadOnlySpan<byte> buildSettings)
	{
		for (int i = 1; i <= 8 && 4 * (i + 1) <= buildSettings.Length; i++)
		{
			int num = buildSettings.Length - 4 * (i + 1);
			if (U(buildSettings, num) == i)
			{
				int[] array = new int[i];
				for (int j = 0; j < i; j++)
				{
					array[j] = (int)U(buildSettings, num + 4 + 4 * j);
				}
				if (array.All((int a) => (a == 2 || (uint)(a - 17) <= 1u || a == 21) ? true : false))
				{
					return array;
				}
			}
		}
		return null;
	}

	private static uint U(ReadOnlySpan<byte> s, int o)
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o));
	}
}
