using System;
using System.Buffers.Binary;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.RegularExpressions.Generated;

namespace SCSKiller.Core.Planning;

public static class PsoDb
{
	public readonly record struct Rec(char Tag, byte[] Payload)
	{
		public string Key
		{
			get
			{
				byte b = (byte)Tag;
				byte[] payload = Payload;
				int num = 0;
				byte[] array = new byte[1 + payload.Length];
				array[num] = b;
				num++;
				ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(payload);
				readOnlySpan.CopyTo(new Span<byte>(array).Slice(num, readOnlySpan.Length));
				num += readOnlySpan.Length;
				return Hex(SHA1.HashData(new ReadOnlySpan<byte>(array)));
			}
		}
	}

	public readonly record struct LayoutElem(string Semantic, int Index, uint Format, uint Offset, uint Slot = 0u, uint Class = 0u, uint Step = 0u);

	public sealed record Pso(string Rs, SortedDictionary<int, string> Stages, bool HasLayout, uint Topology)
	{
		public string Tuple => Tuple(Rs, Stages);
	}

	public sealed record StateObject(uint Type, string? Base, List<string> Libraries, List<string> RootSignatures, List<string> Depends);

	public readonly record struct NvState(string Target, uint Slot, uint Space, uint Scope, uint Options)
	{
		public bool Bare
		{
			get
			{
				if (Slot == uint.MaxValue)
				{
					return Options == 0;
				}
				return false;
			}
		}

		public const int Size = 36;

		public const uint GlobalOptions = 256u;

		public static NvState Parse(Rec r)
		{
			if (r.Tag != 'N' || r.Payload.Length != 36)
			{
				throw new InvalidDataException($"not an 'N' record ('{r.Tag}', {r.Payload.Length} bytes)");
			}
			byte[] payload = r.Payload;
			return new NvState(Hex(payload.AsSpan(0, 20)), U(payload, 20), U(payload, 24), U(payload, 28), U(payload, 32));
		}

		public Rec ToRec()
		{
			byte[] array = new byte[36];
			Convert.FromHexString(Target).CopyTo(array, 0);
			foreach (var (value, num) in new uint[4] { Slot, Space, Scope, Options }.Select((uint v, int i) => (v: v, i: i)))
			{
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(20 + 4 * num), value);
			}
			return new Rec('N', array);
		}
	}

	public sealed record PsoState(string Rs, SortedDictionary<int, string> Stages, List<LayoutElem> Layout, uint Topology, uint[] RtFormats, byte[] RtWriteMasks, int[] LogicOps, uint Dsv, uint SampleCount, byte[]? Blend = null, byte[]? DepthStencil = null)
	{
		public string Tuple => Tuple(Rs, Stages);

		public bool DualSource => DualSource(Blend);
	}

	public sealed record Draw11(string Vs, string Gs, string Hs, string Ds, uint Topology, bool Ps, List<LayoutElem> Layout);

	private sealed class ArrayBufferWriter
	{
		private readonly MemoryStream s = new MemoryStream();

		public ArrayBufferWriter U32(uint v)
		{
			Span<byte> span = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(span, v);
			s.Write(span);
			return this;
		}

		public ArrayBufferWriter Hash(string hex)
		{
			s.Write(Convert.FromHexString(hex));
			return this;
		}

		public ArrayBufferWriter Bytes(byte[] b)
		{
			s.Write(b);
			return this;
		}

		public ArrayBufferWriter Layout(IReadOnlyList<LayoutElem> elems)
		{
			U32((uint)elems.Count);
			foreach (LayoutElem elem in elems)
			{
				byte[] bytes = Encoding.ASCII.GetBytes(elem.Semantic);
				U32((uint)bytes.Length);
				s.Write(bytes);
				U32((uint)elem.Index).U32(elem.Format).U32(elem.Slot).U32(elem.Offset)
					.U32(elem.Class)
					.U32(elem.Step);
			}
			return this;
		}

		public byte[] ToArray()
		{
			return s.ToArray();
		}
	}

	public static readonly string Zero = new string('0', 40);

	private const byte CompactVersion = 1;

	private const int CompactHead = 17;

	private const int GfxLayoutAt = 548;

	private static readonly Dictionary<uint, int> SubPod = new Dictionary<uint, int>
	{
		[8u] = 328,
		[9u] = 4,
		[10u] = 44,
		[11u] = 52,
		[13u] = 4,
		[14u] = 4,
		[15u] = 36,
		[16u] = 4,
		[17u] = 8,
		[18u] = 4,
		[20u] = 4,
		[21u] = 56,
		[26u] = 60,
		[27u] = 44,
		[28u] = 40
	};

	public const uint SoDecl = 65543u;

	public const uint MaxSubobjects = 8192u;

	public const int BlendDescSize = 328;

	public const int DepthStencilDesc1Size = 56;

	public const uint R16G16B16A16Float = 10u;

	public const uint R32G32B32A32Uint = 3u;

	public const uint R32G32B32A32Sint = 4u;

	public const uint D32Float = 40u;

	private static ReadOnlySpan<byte> CompactMagic => new byte[8] { 0, 83, 67, 83, 75, 82, 69, 67 };

	public static string Hex(ReadOnlySpan<byte> b)
	{
		return Convert.ToHexStringLower(b);
	}

	public static IEnumerable<Rec> Read(string path)
	{
		using FileStream f = File.OpenRead(path);
		byte[] array = new byte[17];
		int num = f.ReadAtLeast(array, array.Length, throwOnEndOfStream: false);
		if (!IsCompact(array.AsSpan(0, num)))
		{
			f.Position = 0L;
			foreach (Rec item in Read(new BufferedStream(f, 1048576)))
			{
				yield return item;
			}
			yield break;
		}
		if (num < 17 || array[CompactMagic.Length] != 1)
		{
			throw new InvalidDataException(path + ": a compact recording of an unknown version");
		}
		long raw = BinaryPrimitives.ReadInt64LittleEndian(array.AsSpan(CompactMagic.Length + 1));
		using BrotliStream b = new BrotliStream(new BufferedStream(f, 1048576), CompressionMode.Decompress);
		long got = 0L;
		foreach (Rec item2 in Read(b))
		{
			got += 5 + item2.Payload.Length;
			yield return item2;
		}
		if (got != raw)
		{
			throw new InvalidDataException($"{path}: a compact recording that ends short ({got} of {raw} bytes)");
		}
	}

	private static bool IsCompact(ReadOnlySpan<byte> head)
	{
		if (head.Length >= CompactMagic.Length)
		{
			return head.Slice(0, CompactMagic.Length).SequenceEqual(CompactMagic);
		}
		return false;
	}

	public static bool IsCompact(string path)
	{
		using FileStream fileStream = File.OpenRead(path);
		Span<byte> buffer = stackalloc byte[CompactMagic.Length];
		return IsCompact(buffer.Slice(0, fileStream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)));
	}

	public static void WriteCompact(string path, IEnumerable<Rec> records)
	{
		string text = $"{path}.{Guid.NewGuid():N}.tmp";
		try
		{
			byte[] hashAndReset;
			using (FileStream fileStream = File.Create(text))
			{
				fileStream.Write(new byte[17]);
				using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
				long num = 0L;
				using (BufferedStream s = new BufferedStream(new BrotliStream(fileStream, new BrotliCompressionOptions
				{
					Quality = 6
				}, leaveOpen: true), 1048576))
				{
					foreach (Rec record in records)
					{
						Write(s, record.Tag, record.Payload);
						Framed(incrementalHash, record);
						num += 5 + record.Payload.Length;
					}
				}
				hashAndReset = incrementalHash.GetHashAndReset();
				fileStream.Position = 0L;
				fileStream.Write(CompactMagic);
				fileStream.WriteByte(1);
				Span<byte> span = stackalloc byte[8];
				BinaryPrimitives.WriteInt64LittleEndian(span, num);
				fileStream.Write(span);
			}
			using (IncrementalHash incrementalHash2 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
			{
				foreach (Rec item in Read(text))
				{
					Framed(incrementalHash2, item);
				}
				if (!((ReadOnlySpan<byte>)incrementalHash2.GetHashAndReset().AsSpan()).SequenceEqual((ReadOnlySpan<byte>)hashAndReset))
				{
					throw new InvalidDataException(text + " doesn't read back as written");
				}
			}
			File.Move(text, path, overwrite: true);
		}
		finally
		{
			File.Delete(text);
		}
		static void Framed(IncrementalHash h, Rec r)
		{
			Span<byte> span2 = stackalloc byte[5];
			span2[0] = (byte)r.Tag;
			BinaryPrimitives.WriteUInt32LittleEndian(span2.Slice(1), (uint)r.Payload.Length);
			h.AppendData(span2);
			h.AppendData(r.Payload);
		}
	}

	public static byte[] Compact(long raw, ReadOnlySpan<byte> brotli)
	{
		byte[] array = new byte[17 + brotli.Length];
		CompactMagic.CopyTo(array);
		array[CompactMagic.Length] = 1;
		BinaryPrimitives.WriteInt64LittleEndian(array.AsSpan(CompactMagic.Length + 1), raw);
		brotli.CopyTo(array.AsSpan(17));
		return array;
	}

	public static void CopyRaw(string from, string to)
	{
		if (!IsCompact(from))
		{
			File.Copy(from, to, overwrite: true);
			return;
		}
		using BufferedStream s = new BufferedStream(File.Create(to), 1048576);
		foreach (Rec item in Read(from))
		{
			Write(s, item.Tag, item.Payload);
		}
	}

	public static IEnumerable<Rec> Read(Stream f)
	{
		byte[] head = new byte[5];
		while (f.ReadAtLeast(head, 5, throwOnEndOfStream: false) == 5)
		{
			uint num = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1));
			if (head[0] == 66 && num < 20)
			{
				throw new InvalidDataException($"a 'B' record of {num} bytes, short of its SHA-1");
			}
			if (f.CanSeek && num > f.Length - f.Position)
			{
				break;
			}
			byte[] array = Body(f, num);
			if (array == null)
			{
				break;
			}
			yield return new Rec((char)head[0], array);
		}
	}

	internal static byte[]? Body(Stream f, uint len)
	{
		if (f.CanSeek || len <= 1048576)
		{
			byte[] array = new byte[len];
			if (f.ReadAtLeast(array, array.Length, throwOnEndOfStream: false) >= array.Length)
			{
				return array;
			}
			return null;
		}
		using MemoryStream memoryStream = new MemoryStream();
		byte[] array2 = new byte[1048576];
		int count;
		while (memoryStream.Length < len && (count = f.Read(array2, 0, (int)Math.Min(array2.Length, len - memoryStream.Length))) > 0)
		{
			memoryStream.Write(array2, 0, count);
		}
		return (memoryStream.Length < len) ? null : memoryStream.ToArray();
	}

	public static void Write(Stream s, char tag, ReadOnlySpan<byte> payload)
	{
		Span<byte> span = stackalloc byte[5];
		span[0] = (byte)tag;
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1), (uint)payload.Length);
		s.Write(span);
		s.Write(payload);
	}

	public static void WriteBlob(Stream s, string sha1, ReadOnlySpan<byte> bytes)
	{
		Span<byte> span = stackalloc byte[25];
		span[0] = 66;
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1), (uint)(20 + bytes.Length));
		Convert.FromHexString(sha1).CopyTo(span.Slice(5));
		s.Write(span);
		s.Write(bytes);
	}

	public static string Tuple(string rs, IEnumerable<KeyValuePair<int, string>> stages)
	{
		return rs + "|" + string.Join(',', from s in stages
			orderby s.Key
			select $"{s.Key}:{s.Value}");
	}

	private static bool IsShader(uint t)
	{
		switch (t)
		{
		case 1u:
		case 2u:
		case 3u:
		case 4u:
		case 5u:
		case 6u:
		case 24u:
		case 25u:
			return true;
		default:
			return false;
		}
	}

	public static Pso Parse(Rec r)
	{
		byte[] p = r.Payload;
		if (r.Tag == 'C')
		{
			return new Pso(H(0), new SortedDictionary<int, string> { [6] = H(20) }, HasLayout: false, 0u);
		}
		SortedDictionary<int, string> sortedDictionary = new SortedDictionary<int, string>();
		if (r.Tag == 'G')
		{
			int[] array = new int[5] { 1, 2, 3, 4, 5 };
			for (int i = 0; i < 5; i++)
			{
				if (H(20 + 20 * i) != Zero)
				{
					sortedDictionary[array[i]] = H(20 + 20 * i);
				}
			}
			return new Pso(H(0), sortedDictionary, U(p, 548) != 0, U(p, 552 + LayoutSize(p, 548) + 4));
		}
		string rs = Zero;
		bool hasLayout = false;
		uint topology = 0u;
		int num = 4;
		foreach (var (num2, o, num3) in Subobjects(p))
		{
			if (num2 == 0)
			{
				rs = H(o);
			}
			else if (IsShader(num2))
			{
				if (H(o) != Zero)
				{
					sortedDictionary[(int)num2] = H(o);
				}
			}
			else
			{
				switch (num2)
				{
				case 12u:
					hasLayout = U(p, o) != 0;
					break;
				case 14u:
					topology = U(p, o);
					break;
				}
			}
			num = num3;
		}
		if (num != p.Length)
		{
			throw new InvalidDataException($"stream record parse mismatch ({num} != {p.Length})");
		}
		return new Pso(rs, sortedDictionary, hasLayout, topology);
		string H(int start)
		{
			return Hex(p.AsSpan(start, 20));
		}
	}

	private static IEnumerable<(uint Type, int At, int End)> Subobjects(byte[] p)
	{
		int pos = 4;
		for (uint i = 0u; i < U(p, 0); i++)
		{
			uint num = U(p, pos);
			pos += 4;
			int size = ((num == 0 || IsShader(num)) ? 20 : (num switch
			{
				65543u => SoSize(p, pos), 
				7u => 0, 
				22u => (int)(4 + 8 * U(p, pos) + 4), 
				12u => 4 + LayoutSize(p, pos), 
				_ => SubPod[num], 
			}));
			yield return (Type: num, At: pos, End: pos + size);
			pos += size;
		}
	}

	private static uint U(byte[] p, int o)
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(o));
	}

	public static void Check(Rec r)
	{
		byte[] payload = r.Payload;
		try
		{
			char tag = r.Tag;
			if (tag != 'C')
			{
				if (tag != 'G')
				{
					if (tag != 'S' || U(payload, 0) > 64)
					{
						goto IL_0083;
					}
				}
				else
				{
					int num = 552 + LayoutSize(payload, 548) + 4 + 4 + 36 + 4 + 8 + 4 + 4;
					if (num > payload.Length || (num < payload.Length && num + SoSize(payload, num) != payload.Length))
					{
						throw new InvalidDataException("'G' record size mismatch");
					}
				}
			}
			else if (payload.Length != 48)
			{
				goto IL_0083;
			}
			Parse(r);
			ParseState(r);
			return;
			IL_0083:
			throw new InvalidDataException($"not a PSO record: '{r.Tag}' ({payload.Length} bytes)");
		}
		catch (Exception ex) when ((ex is ArgumentException || ex is IndexOutOfRangeException || ex is KeyNotFoundException || ex is OverflowException) ? true : false)
		{
			throw new InvalidDataException($"malformed '{r.Tag}' record", ex);
		}
	}

	private static int SoSize(byte[] p, int pos)
	{
		int num = pos + 4;
		for (uint num2 = 0u; num2 < U(p, pos); num2++)
		{
			int num3 = num;
			uint num4 = U(p, num + 4);
			num = num3 + (8 + ((num4 != uint.MaxValue) ? checked((int)num4) : 0) + 16);
		}
		return num + 4 + (int)(4 * U(p, num)) + 4 - pos;
	}

	public static byte[]? StreamOutputOf(Rec r)
	{
		byte[] payload = r.Payload;
		if (r.Tag == 'G')
		{
			int num = 552 + LayoutSize(payload, 548) + 4 + 4 + 36 + 4 + 8 + 4 + 4;
			if (num >= payload.Length)
			{
				return null;
			}
			return payload[num..];
		}
		if (r.Tag != 'S')
		{
			return null;
		}
		foreach (var (num2, num3, num4) in Subobjects(payload))
		{
			if (num2 == 65543)
			{
				return payload[num3..num4];
			}
		}
		return null;
	}

	public static bool IsStateObject(char tag)
	{
		if (tag == 'A' || tag == 'R')
		{
			return true;
		}
		return false;
	}

	public static StateObject ParseStateObject(Rec r)
	{
		if (!IsStateObject(r.Tag))
		{
			throw new ArgumentException($"'{r.Tag}' is not a state object record");
		}
		byte[] p = r.Payload;
		int pos = 0;
		string text = ((r.Tag == 'A') ? H() : null);
		uint type = Next();
		List<string> libraries = new List<string>();
		List<string> rootSignatures = new List<string>();
		List<string> list;
		if (text == null)
		{
			list = new List<string>();
		}
		else
		{
			int num = 1;
			list = new List<string>(num);
			CollectionsMarshal.SetCount(list, num);
			CollectionsMarshal.AsSpan(list)[0] = text;
		}
		StateObject stateObject = new StateObject(type, text, libraries, rootSignatures, list);
		uint num2 = Next();
		if (num2 > 8192)
		{
			throw new InvalidDataException($"state object of {num2} subobjects, over {8192u}");
		}
		for (uint num3 = num2; num3 != 0; num3--)
		{
			uint num4 = Next();
			switch (num4)
			{
			case 0u:
			case 3u:
			case 10u:
				Next();
				break;
			case 9u:
			case 12u:
				Next();
				Next();
				break;
			case 1u:
			case 2u:
			{
				string text2 = H();
				if (text2 != Zero)
				{
					stateObject.RootSignatures.Add(text2);
				}
				break;
			}
			case 5u:
				stateObject.Libraries.Add(H());
				Exports();
				break;
			case 6u:
				stateObject.Depends.Add(H());
				Exports();
				break;
			case 7u:
				Next();
				Strs();
				break;
			case 8u:
				Str();
				Strs();
				break;
			case 11u:
				Str();
				Next();
				Str();
				Str();
				Str();
				break;
			default:
				throw new InvalidDataException($"unknown state subobject type {num4}");
			}
		}
		if (pos != p.Length)
		{
			throw new InvalidDataException($"state object record parse mismatch ({pos} != {p.Length})");
		}
		return stateObject;
		void Exports()
		{
			for (uint num5 = Next(); num5 != 0; num5--)
			{
				Str();
				Str();
				Next();
			}
		}
		string H()
		{
			if (pos + 20 > p.Length)
			{
				throw new InvalidDataException("state object record truncated");
			}
			string result = Hex(p.AsSpan(pos, 20));
			pos += 20;
			return result;
		}
		uint Next()
		{
			if (pos + 4 > p.Length)
			{
				throw new InvalidDataException("state object record truncated");
			}
			uint result = U(p, pos);
			pos += 4;
			return result;
		}
		void Str()
		{
			uint num5 = Next();
			if (num5 != uint.MaxValue)
			{
				if (2L * (long)num5 > p.Length - pos)
				{
					throw new InvalidDataException("malformed string");
				}
				pos += (int)(2 * num5);
			}
		}
		void Strs()
		{
			for (uint num5 = Next(); num5 != 0; num5--)
			{
				Str();
			}
		}
	}

	public static string StateObjectIdentity(Rec r)
	{
		if (!IsStateObject(r.Tag))
		{
			return r.Key;
		}
		try
		{
			Dictionary<string, string> aliases = new Dictionary<string, string>();
			HashSet<string> names = new HashSet<string>();
			if (!WalkStateObject(r, (string name, string? renames) =>
			{
				names.Add(name);
				if (renames != null)
				{
					string text = LaunchSuffix().Replace(name, "");
					if (text != name)
					{
						aliases[name] = text;
					}
				}
			}, null))
			{
				return r.Key;
			}
			if (names.Select((string n) => aliases.GetValueOrDefault(n, n)).Distinct().Count() < names.Count)
			{
				return r.Key;
			}
			MemoryStream w = new MemoryStream();
			WalkStateObject(r, null, (ReadOnlyMemory<byte> bytes, string? name) =>
			{
				if (name == null)
				{
					w.Write(bytes.Span);
				}
				else
				{
					string valueOrDefault = aliases.GetValueOrDefault(name, name);
					w.Write(BitConverter.GetBytes(valueOrDefault.Length));
					w.Write(Encoding.Unicode.GetBytes(valueOrDefault));
				}
			});
			byte b = (byte)r.Tag;
			byte[] array = w.ToArray();
			int num = 0;
			byte[] array2 = new byte[1 + array.Length];
			array2[num] = b;
			num++;
			ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(array);
			readOnlySpan.CopyTo(new Span<byte>(array2).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			return Hex(SHA1.HashData(new ReadOnlySpan<byte>(array2)));
		}
		catch (InvalidDataException)
		{
			return r.Key;
		}
	}

	public static bool HasLaunchAlias(Rec r)
	{
		if (!IsStateObject(r.Tag))
		{
			return false;
		}
		bool found = false;
		try
		{
			WalkStateObject(r, (string name, string? renames) =>
			{
				found |= renames != null && LaunchSuffix().IsMatch(name);
			}, null);
		}
		catch (InvalidDataException)
		{
			return false;
		}
		return found;
	}

	private static bool WalkStateObject(Rec r, Action<string, string?>? export, Action<ReadOnlyMemory<byte>, string?>? write)
	{
		byte[] p = r.Payload;
		int pos = ((r.Tag == 'A') ? 20 : 0);
		if (pos > p.Length)
		{
			throw new InvalidDataException("state object record truncated");
		}
		bool listed = true;
		Next();
		uint num = Next();
		if (num > 8192)
		{
			throw new InvalidDataException($"state object of {num} subobjects, over {8192u}");
		}
		for (uint num2 = num; num2 != 0; num2--)
		{
			uint num3 = Next();
			switch (num3)
			{
			case 0u:
			case 3u:
			case 10u:
				Next();
				break;
			case 9u:
			case 12u:
				Next();
				Next();
				break;
			case 1u:
			case 2u:
				H();
				break;
			case 5u:
			case 6u:
				H();
				Exports();
				break;
			case 7u:
				Next();
				Names();
				break;
			case 8u:
				Str(name: false);
				Names();
				break;
			case 11u:
			{
				int num4 = pos;
				string text = Read();
				if (text != null)
				{
					export?.Invoke(text, null);
				}
				pos = num4;
				Str(name: true);
				Next();
				Str(name: true);
				Str(name: true);
				Str(name: true);
				break;
			}
			default:
				throw new InvalidDataException($"unknown state subobject type {num3}");
			}
		}
		if (pos != p.Length)
		{
			throw new InvalidDataException("state object record parse mismatch");
		}
		return listed;
		void Exports()
		{
			uint num5 = Next();
			listed &= num5 != 0;
			for (uint num6 = num5; num6 != 0; num6--)
			{
				int num7 = pos;
				string text2 = Read();
				int num8 = pos;
				string arg = Read();
				if (text2 != null)
				{
					export?.Invoke(text2, arg);
				}
				write?.Invoke(p.AsMemory(num7, num8 - num7), text2);
				write?.Invoke(p.AsMemory(num8, pos - num8), null);
				Next();
			}
		}
		void H()
		{
			if (pos + 20 > p.Length)
			{
				throw new InvalidDataException("state object record truncated");
			}
			write?.Invoke(p.AsMemory(pos, 20), null);
			pos += 20;
		}
		void Names()
		{
			for (uint num5 = Next(); num5 != 0; num5--)
			{
				Str(name: true);
			}
		}
		uint Next()
		{
			if (pos + 4 > p.Length)
			{
				throw new InvalidDataException("state object record truncated");
			}
			uint result = U(p, pos);
			write?.Invoke(p.AsMemory(pos, 4), null);
			pos += 4;
			return result;
		}
		string? Read()
		{
			if (pos + 4 > p.Length)
			{
				throw new InvalidDataException("state object record truncated");
			}
			uint num5 = U(p, pos);
			if (num5 != uint.MaxValue)
			{
				if (2L * (long)num5 > p.Length - pos - 4)
				{
					throw new InvalidDataException("malformed string");
				}
				string result = Encoding.Unicode.GetString(p, pos + 4, (int)(2 * num5));
				pos += (int)(4 + 2 * num5);
				return result;
			}
			pos += 4;
			return null;
		}
		void Str(bool name)
		{
			int num5 = pos;
			string text2 = Read();
			write?.Invoke(p.AsMemory(num5, pos - num5), name ? text2 : null);
		}
	}

	[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.42308")]
	private static Regex LaunchSuffix()
	{
		return _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__LaunchSuffix_4.Instance;
	}

	public static bool DualSource(byte[]? blend)
	{
		if (blend == null || blend.Length != 328 || U(blend, 8) == 0)
		{
			return false;
		}
		int[] array = new int[4] { 16, 20, 28, 32 };
		foreach (int o in array)
		{
			uint num = U(blend, o);
			if (num >= 16 && num <= 19)
			{
				return true;
			}
		}
		return false;
	}

	public static PsoState? ParseState(Rec r)
	{
		char tag = r.Tag;
		if ((tag != 'G' && tag != 'S') || 1 == 0)
		{
			return null;
		}
		byte[] payload = r.Payload;
		Pso pso = Parse(r);
		List<LayoutElem> layout = new List<LayoutElem>();
		uint topology = 0u;
		uint dsv = 0u;
		uint sampleCount = 1u;
		uint[] array = Array.Empty<uint>();
		int num = -1;
		byte[] depthStencil = null;
		if (r.Tag == 'G')
		{
			num = 120;
			Span<byte> span = payload.AsSpan(496, 52);
			int num2 = 0;
			byte[] array2 = new byte[4 + span.Length];
			span.CopyTo(new Span<byte>(array2).Slice(num2, span.Length));
			num2 += span.Length;
			array2[num2] = 0;
			num2++;
			array2[num2] = 0;
			num2++;
			array2[num2] = 0;
			num2++;
			array2[num2] = 0;
			depthStencil = array2;
			layout = ReadLayout(payload, 548);
			int num3 = 552 + LayoutSize(payload, 548) + 4;
			topology = U(payload, num3);
			array = RtFormats(payload, num3 + 4);
			dsv = U(payload, num3 + 40);
			sampleCount = U(payload, num3 + 44);
		}
		else
		{
			foreach (var (num4, num5, _) in Subobjects(payload))
			{
				switch (num4)
				{
				case 12u:
					layout = ReadLayout(payload, num5);
					break;
				case 8u:
					num = num5;
					break;
				case 14u:
					topology = U(payload, num5);
					break;
				case 15u:
					array = RtFormats(payload, num5);
					break;
				case 16u:
					dsv = U(payload, num5);
					break;
				case 21u:
					depthStencil = payload.AsSpan(num5, 56).ToArray();
					break;
				case 11u:
				{
					Span<byte> span = payload.AsSpan(num5, 52);
					int num2 = 0;
					byte[] array2 = new byte[4 + span.Length];
					span.CopyTo(new Span<byte>(array2).Slice(num2, span.Length));
					num2 += span.Length;
					array2[num2] = 0;
					num2++;
					array2[num2] = 0;
					num2++;
					array2[num2] = 0;
					num2++;
					array2[num2] = 0;
					depthStencil = array2;
					break;
				}
				case 17u:
					sampleCount = U(payload, num5);
					break;
				}
			}
		}
		byte[] array3 = new byte[array.Length];
		int[] array4 = new int[array.Length];
		for (int i = 0; i < array.Length; i++)
		{
			if (num < 0)
			{
				array3[i] = 15;
				array4[i] = -1;
			}
			else
			{
				int num6 = num + 8 + 40 * ((U(payload, num + 4) != 0) ? i : 0);
				array3[i] = payload[num6 + 36];
				array4[i] = ((U(payload, num6 + 4) != 0) ? ((int)U(payload, num6 + 32)) : (-1));
			}
		}
		return new PsoState(pso.Rs, pso.Stages, layout, topology, array, array3, array4, dsv, sampleCount, (num < 0) ? null : payload.AsSpan(num, 328).ToArray(), depthStencil);
		static uint[] RtFormats(byte[] p, int o)
		{
			return (from num7 in Enumerable.Range(0, (int)Math.Min(8u, U(p, o)))
				select U(p, o + 4 + 4 * num7)).ToArray();
		}
	}

	public static byte[] LayoutBytes(IReadOnlyList<LayoutElem> layout)
	{
		return new ArrayBufferWriter().Layout(layout).ToArray();
	}

	private static List<LayoutElem> ReadLayout(byte[] p, int pos)
	{
		List<LayoutElem> list = new List<LayoutElem>();
		int num = pos + 4;
		for (uint num2 = 0u; num2 < U(p, pos); num2++)
		{
			int num3 = (int)U(p, num);
			string semantic = Encoding.ASCII.GetString(p, num + 4, num3);
			num += 4 + num3;
			list.Add(new LayoutElem(semantic, (int)U(p, num), U(p, num + 4), U(p, num + 12), U(p, num + 8), U(p, num + 16), U(p, num + 20)));
			num += 24;
		}
		return list;
	}

	private static int LayoutSize(byte[] p, int pos)
	{
		int num = pos + 4;
		for (uint num2 = 0u; num2 < U(p, pos); num2++)
		{
			num += 4 + checked((int)U(p, num)) + 24;
		}
		return num - pos - 4;
	}

	public static byte[] Item(string template, string rs, IReadOnlyDictionary<int, string> stages, IReadOnlyList<LayoutElem>? layout)
	{
		ArrayBufferWriter arrayBufferWriter = new ArrayBufferWriter();
		arrayBufferWriter.Hash(template).Hash(rs).U32((uint)stages.Count);
		foreach (var (v, hex) in stages.OrderBy((KeyValuePair<int, string> s) => s.Key))
		{
			arrayBufferWriter.U32((uint)v).Hash(hex);
		}
		if (layout == null)
		{
			arrayBufferWriter.U32(uint.MaxValue);
		}
		else
		{
			arrayBufferWriter.Layout(layout);
		}
		return arrayBufferWriter.ToArray();
	}

	public static byte[] Compute(string rs, string cs)
	{
		return new ArrayBufferWriter().Hash(rs).Hash(cs).U32(0u)
			.U32(0u)
			.ToArray();
	}

	public static byte[] D3D11Item(Stage stage, string sha1)
	{
		return new ArrayBufferWriter().U32((uint)stage).Hash(sha1).ToArray();
	}

	public static Draw11 ParseDraw11(byte[] p)
	{
		if (p.Length >= 92)
		{
			return new Draw11(Hex(p.AsSpan(0, 20)), Hex(p.AsSpan(20, 20)), Hex(p.AsSpan(40, 20)), Hex(p.AsSpan(60, 20)), U(p, 80), (U(p, 84) & 1) != 0, ReadLayout(p, 88));
		}
		throw new InvalidDataException($"a D3D11 draw of {p.Length} bytes");
	}

	public static byte[] Draw11Bytes(Draw11 d)
	{
		return new ArrayBufferWriter().Hash(d.Vs).Hash(d.Gs).Hash(d.Hs)
			.Hash(d.Ds)
			.U32(d.Topology)
			.U32(d.Ps ? 1u : 0u)
			.Layout(d.Layout)
			.ToArray();
	}

	public static byte[] D3D11Pair(string hs, string ds)
	{
		return new ArrayBufferWriter().Hash(hs).Hash(ds).ToArray();
	}

	public static (string Template, string Rs, SortedDictionary<int, string> Stages) ParseItem(byte[] p)
	{
		SortedDictionary<int, string> sortedDictionary = new SortedDictionary<int, string>();
		for (int i = 0; i < (int)U(p, 40); i++)
		{
			sortedDictionary[(int)U(p, 44 + 24 * i)] = Hex(p.AsSpan(48 + 24 * i, 20));
		}
		return (Template: Hex(p.AsSpan(0, 20)), Rs: Hex(p.AsSpan(20, 20)), Stages: sortedDictionary);
	}

	public static byte[] Stream(string rs, IReadOnlyDictionary<int, string> stages, IReadOnlyList<LayoutElem>? layout, uint topology, uint[] rtFormats, uint dsv, byte[]? rtWriteMasks = null, int logicOp = -1, byte[]? blend = null, byte[]? depthStencil = null)
	{
		if ((blend != null && blend.Length != 328) || (depthStencil != null && depthStencil.Length != 56))
		{
			throw new ArgumentException("not a canonical blend / depth-stencil desc");
		}
		bool flag = logicOp < 0 && Enumerable.Range(0, rtFormats.Length).Any((int i) => Mask(i) != Mask(0));
		uint v;
		uint v2;
		if (logicOp < 0)
		{
			v = 0u;
			v2 = 4u;
		}
		else
		{
			v2 = (uint)logicOp;
			v = 1u;
		}
		ArrayBufferWriter w = new ArrayBufferWriter();
		uint count = 0u;
		w.U32(0u);
		Sub(18u).U32(1u);
		Sub(0u).Hash(rs);
		if (stages.ContainsKey(6))
		{
			Sub(6u).Hash(Sh(Stage.Compute));
		}
		else
		{
			bool flag2 = stages.ContainsKey(25);
			if (!flag2)
			{
				Sub(12u).Layout(layout ?? Array.Empty<LayoutElem>());
			}
			if (!flag2)
			{
				Sub(13u).U32(0u);
			}
			if (!flag2)
			{
				Sub(14u).U32(topology);
			}
			if (flag2)
			{
				if (stages.ContainsKey(24))
				{
					Sub(24u).Hash(Sh(Stage.Amplification));
				}
				Sub(25u).Hash(Sh(Stage.Mesh));
			}
			else
			{
				Stage[] array = new Stage[4]
				{
					Stage.Vertex,
					Stage.Geometry,
					Stage.Hull,
					Stage.Domain
				};
				foreach (Stage stage in array)
				{
					Sub((uint)stage).Hash(Sh(stage));
				}
			}
			Sub(2u).Hash(Sh(Stage.Pixel));
			if (blend != null)
			{
				Sub(8u).Bytes(blend);
			}
			else
			{
				Sub(8u).U32(0u).U32(flag ? 1u : 0u);
				for (int num2 = 0; num2 < 8; num2++)
				{
					w.U32(0u).U32(v).U32(2u)
						.U32(1u)
						.U32(1u)
						.U32(2u)
						.U32(1u)
						.U32(1u)
						.U32(v2)
						.U32(flag ? Mask(num2) : Mask(0));
				}
			}
			if (depthStencil != null)
			{
				Sub(21u).Bytes(depthStencil);
			}
			else
			{
				uint v3 = ((dsv != 0) ? 1u : 0u);
				Sub(21u).U32(v3).U32(v3).U32(8u)
					.U32(0u)
					.U32(65535u);
				for (int num3 = 0; num3 < 2; num3++)
				{
					w.U32(1u).U32(1u).U32(1u)
						.U32(8u);
				}
				w.U32(0u);
			}
			Sub(16u).U32(dsv);
			Sub(10u).U32(3u).U32(1u).U32(0u)
				.U32(0u)
				.U32(0u)
				.U32(0u)
				.U32(1u)
				.U32(0u)
				.U32(0u)
				.U32(0u)
				.U32(0u);
			Sub(15u).U32((uint)rtFormats.Length);
			for (int num4 = 0; num4 < 8; num4++)
			{
				w.U32((num4 < rtFormats.Length) ? rtFormats[num4] : 0u);
			}
			Sub(17u).U32(1u).U32(0u);
			Sub(9u).U32(uint.MaxValue);
		}
		byte[] array2 = w.ToArray();
		BinaryPrimitives.WriteUInt32LittleEndian(array2, count);
		return array2;
		byte Mask(int i)
		{
			if (rtWriteMasks == null || i >= rtWriteMasks.Length)
			{
				return 15;
			}
			return rtWriteMasks[i];
		}
		string Sh(Stage s)
		{
			return stages.GetValueOrDefault((int)s, Zero);
		}
		ArrayBufferWriter Sub(uint type)
		{
			count++;
			return w.U32(type);
		}
	}
}
