using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.RedEngine;

public static class RedRayTracing
{
	public sealed record Shape(string Global, uint Payload, uint Attributes, uint Depth, uint PipelineFlags);

	public sealed record ItemFields(string ClosestHit, string? AnyHit, string Local, Shape Shape, RtCollections.Nv? Nv);

	public static Shape? Learn(IEnumerable<PsoDb.Rec> stateObjects)
	{
		List<Shape> list = stateObjects.Where((PsoDb.Rec r) => r.Tag == 'A').Select(Read).OfType<Shape>()
			.ToList();
		if (list.Count != 0)
		{
			return (from s in list
				group s by s).MaxBy((IGrouping<Shape, Shape> g) => g.Count()).Key;
		}
		return null;
	}

	private static Shape? Read(PsoDb.Rec r)
	{
		byte[] p = r.Payload;
		int pos = 20;
		string text = null;
		uint payload = 0u;
		uint attributes = 0u;
		uint depth = 0u;
		uint pipelineFlags = 0u;
		try
		{
			U();
			for (uint num = U(); num != 0; num--)
			{
				switch (U())
				{
				case 1u:
					text = PsoDb.Hex(p.AsSpan(pos, 20));
					pos += 20;
					break;
				case 2u:
					pos += 20;
					break;
				case 9u:
					payload = U();
					attributes = U();
					break;
				case 12u:
					depth = U();
					pipelineFlags = U();
					break;
				case 10u:
					depth = U();
					break;
				case 0u:
				case 3u:
					U();
					break;
				case 5u:
				case 6u:
				{
					pos += 20;
					for (uint num2 = U(); num2 != 0; num2--)
					{
						Str();
						Str();
						U();
					}
					break;
				}
				case 7u:
					U();
					Strs();
					break;
				case 8u:
					Str();
					Strs();
					break;
				case 11u:
					Str();
					U();
					Str();
					Str();
					Str();
					break;
				default:
					return null;
				}
			}
			return (text == null) ? null : new Shape(text, payload, attributes, depth, pipelineFlags);
		}
		catch (ArgumentOutOfRangeException)
		{
			return null;
		}
		void Str()
		{
			uint num3 = U();
			if (num3 != uint.MaxValue)
			{
				pos += (int)(2 * num3);
			}
		}
		void Strs()
		{
			for (uint num3 = U(); num3 != 0; num3--)
			{
				Str();
			}
		}
		uint U()
		{
			uint result = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(pos));
			pos += 4;
			return result;
		}
	}

	public static RootSig.Desc? Local(IEnumerable<ShaderInfo> libs, RootSig.Ranges global)
	{
		Dictionary<string, List<(ulong, ulong)>> dictionary = new Dictionary<string, List<(ulong, ulong)>>
		{
			["srv"] = new List<(ulong, ulong)>(),
			["cbv"] = new List<(ulong, ulong)>()
		};
		foreach (Binding item2 in from b in libs.SelectMany((ShaderInfo l) => l.Bindings)
			where b.Space == 0
			select b)
		{
			if (item2.Count < 0 || item2.Lower < 0)
			{
				return null;
			}
			ulong num = (ulong)item2.Lower;
			ulong num2 = (ulong)item2.Lower + (ulong)Math.Max(1, item2.Count);
			ulong hi = num2;
			ulong lo = num;
			uint type = item2.Class switch
			{
				"srv" => 0u, 
				"uav" => 1u, 
				"cbv" => 2u, 
				_ => 3u, 
			};
			List<(ulong, ulong)> list = (from g in global.Slots
				where g.Space == 0 && g.Type == type
				select ((ulong Lo, ulong Hi))(Lo: g.Base, Hi: (g.Count == uint.MaxValue) ? 4294967296uL : ((ulong)g.Base + (ulong)g.Count)) into g
				where g.Lo < hi && lo < g.Hi
				select g).ToList();
			if (!list.Any(((ulong Lo, ulong Hi) g) => g.Lo <= lo && hi <= g.Hi))
			{
				if (list.Count > 0)
				{
					return null;
				}
				dictionary.GetValueOrDefault(item2.Class)?.Add((lo, hi));
			}
		}
		int num3 = 3;
		List<uint[]> list2 = new List<uint[]>(num3);
		CollectionsMarshal.SetCount(list2, num3);
		Span<uint[]> span = CollectionsMarshal.AsSpan(list2);
		span[0] = new uint[5] { 3u, 0u, 1u, 1u, 2u };
		span[1] = new uint[5] { 3u, 0u, 2u, 1u, 2u };
		span[2] = new uint[5] { 1u, 0u, 3u, 1u, 22u };
		List<uint[]> list3 = list2;
		(uint, string)[] array = new (uint, string)[2]
		{
			(0u, "srv"),
			(2u, "cbv")
		};
		for (num3 = 0; num3 < array.Length; num3++)
		{
			(uint, string) tuple = array[num3];
			uint type2 = tuple.Item1;
			string item = tuple.Item2;
			List<(ulong, ulong)> list4 = Runs(dictionary[item]);
			if (list4.Count > 0 || type2 == 0)
			{
				List<uint> list5 = new List<uint>();
				list5.Add(0u);
				list5.Add(0u);
				list5.AddRange(list4.SelectMany(((ulong Lo, ulong Hi) r) => new uint[5]
				{
					type2,
					(uint)(r.Hi - r.Lo),
					(uint)r.Lo,
					0u,
					3u
				}));
				list3.Add(list5.ToArray());
			}
		}
		uint[] array2 = list3[list3.Count - 1];
		if (array2 == null || array2.Length != 2 || array2[0] != 0 || array2[1] != 0)
		{
			return new RootSig.Desc(128u, list3, AppendRanges: true);
		}
		return null;
	}

	private static List<(ulong Lo, ulong Hi)> Runs(IEnumerable<(ulong Lo, ulong Hi)> spans)
	{
		List<(ulong, ulong)> list = new List<(ulong, ulong)>();
		foreach (var item in spans.OrderBy(((ulong Lo, ulong Hi) r) => r.Lo))
		{
			if (list.Count > 0)
			{
				if (item.Lo <= list[list.Count - 1].Item2)
				{
					list[list.Count - 1] = (list[list.Count - 1].Item1, Math.Max(list[list.Count - 1].Item2, item.Hi));
					continue;
				}
			}
			list.Add(item);
		}
		return list;
	}

	public static byte[] Item(string closestHit, string? anyHit, string local, Shape s, RtCollections.Nv? nv)
	{
		byte[] array = new byte[108];
		foreach (var (s2, num) in new string[4]
		{
			closestHit,
			anyHit ?? PsoDb.Zero,
			s.Global,
			local
		}.Select((string h, int i) => (h: h, i: i)))
		{
			Convert.FromHexString(s2).CopyTo(array, 20 * num);
		}
		uint[] array2 = new uint[7]
		{
			s.Payload,
			s.Attributes,
			s.Depth,
			s.PipelineFlags,
			(uint)(((int?)nv?.Slot) ?? (-1)),
			nv?.Space ?? 0,
			nv?.Options ?? 0
		};
		for (int num2 = 0; num2 < array2.Length; num2++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(80 + 4 * num2), array2[num2]);
		}
		return array;
	}

	public static ItemFields ParseItem(byte[] p)
	{
		if (p.Length != 108)
		{
			throw new InvalidDataException($"an 'H' record is 108 bytes, not {p.Length}");
		}
		return new ItemFields(H(0), (H(1) == PsoDb.Zero) ? null : H(1), H(3), new Shape(H(2), U(0), U(1), U(2), U(3)), (U(4) == uint.MaxValue) ? ((RtCollections.Nv?)null) : new RtCollections.Nv?(new RtCollections.Nv(U(4), U(5), U(6))));
		string H(int i)
		{
			return PsoDb.Hex(p.AsSpan(20 * i, 20));
		}
		uint U(int i)
		{
			return BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(80 + 4 * i));
		}
	}

	public static byte[]? Collection(ReadOnlySpan<byte> closestHit, ItemFields f, ReadOnlySpan<byte> anyHit)
	{
		List<string> list = (from x in ShaderContainer.Rdat(closestHit).Functions
			where x.Kind == 10
			select x.Name).ToList();
		List<string> list2 = ((f.AnyHit == null) ? new List<string>() : (from x in ShaderContainer.Rdat(anyHit).Functions
			where x.Kind == 9
			select x.Name).ToList());
		if (list.Count != 1 || (f.AnyHit != null && list2.Count != 1))
		{
			return null;
		}
		MemoryStream w = new MemoryStream();
		string s = "HitGroup_" + f.ClosestHit.Substring(0, 16);
		uint subs = 0u;
		U(0u);
		U(0u);
		Sub(1u);
		Hash(f.Shape.Global);
		Sub(9u);
		U(f.Shape.Payload);
		U(f.Shape.Attributes);
		Sub(12u);
		U(f.Shape.Depth);
		U(f.Shape.PipelineFlags);
		Sub(5u);
		Hash(f.ClosestHit);
		U(1u);
		Str(list[0]);
		Str(null);
		U(0u);
		if (f.AnyHit != null)
		{
			Sub(5u);
			Hash(f.AnyHit);
			U(1u);
			Str(list2[0]);
			Str(null);
			U(0u);
		}
		Sub(11u);
		Str(s);
		U(0u);
		Str(list2.FirstOrDefault());
		Str(list[0]);
		Str(null);
		uint v = subs;
		Sub(2u);
		Hash(f.Local);
		Sub(7u);
		U(v);
		U(1u);
		Str(s);
		byte[] array = w.ToArray();
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(4), subs);
		return array;
		void Hash(string h)
		{
			w.Write(Convert.FromHexString(h));
		}
		void Str(string? text)
		{
			if (text == null)
			{
				U(uint.MaxValue);
			}
			else
			{
				U((uint)text.Length);
				w.Write(Encoding.Unicode.GetBytes(text));
			}
		}
		void Sub(uint t)
		{
			subs++;
			U(t);
		}
		void U(uint value)
		{
			Span<byte> span = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(span, value);
			w.Write(span);
		}
	}
}
