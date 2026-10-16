using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Planning;

public static class RtCollections
{
	public sealed record Rule(string GlobalRs, uint Flags, uint Depth, uint Payload, uint Attributes, bool Verified);

	public sealed record Choice(RootSig.Desc Global, byte[] Samplers, RootSig.Desc? Local, uint Flags, uint Depth, uint Payload, uint Attributes, string How);

	public readonly record struct Nv(uint Slot, uint Space, uint Options);

	public sealed record ItemFields(string Library, string Global, string LocalRayGen, string LocalOther, uint Payload, uint Attributes, uint Depth, uint Flags, Nv? Nv = null);

	public sealed record Recorded(string Library, string Global, string Local, uint Payload, uint Attributes, uint Depth, uint Flags, string NameHash);

	private sealed class W
	{
		private readonly MemoryStream s = new MemoryStream();

		public W U32(uint v)
		{
			Span<byte> span = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(span, v);
			s.Write(span);
			return this;
		}

		public W Hash(string hex)
		{
			s.Write(Convert.FromHexString(hex));
			return this;
		}

		public W Str(string? v)
		{
			if (v == null)
			{
				return U32(uint.MaxValue);
			}
			U32((uint)v.Length);
			s.Write(Encoding.Unicode.GetBytes(v));
			return this;
		}

		public W Names(IEnumerable<string> names)
		{
			List<string> list = names.ToList();
			U32((uint)list.Count);
			foreach (string item in list)
			{
				Str(item);
			}
			return this;
		}

		public byte[] ToArray()
		{
			return s.ToArray();
		}
	}

	public const uint NoConfig = uint.MaxValue;

	private static readonly (int Kind, string Prefix)[] Kinds = new (int, string)[6]
	{
		(7, "RayGen"),
		(11, "Miss"),
		(10, "CHS"),
		(9, "AHS"),
		(8, "IS"),
		(12, "Callable")
	};

	public static string? Ue5ShapeMismatch(IReadOnlyCollection<ShaderInfo> libs)
	{
		List<Binding> b = libs.SelectMany((ShaderInfo l) => l.Bindings).ToList();
		if (libs.Any((ShaderInfo l) => (l.Counts.Flags & 3) != 0))
		{
			return "bindless descriptor heap access";
		}
		if (b.Any((Binding x) => (object)x != null && x.Class == "cbv" && x.Space == 4))
		{
			return "shared uniform buffers in space 4";
		}
		if (!b.Any((Binding x) => (object)x != null && x.Class == "cbv" && x.Space == 1))
		{
			return "no uniform buffers in space 1";
		}
		if (!Binds("srv", 2, 0) || !Binds("srv", 2, 1))
		{
			return "no hit group index and vertex buffers (t0/t1 space 2)";
		}
		return null;
		bool Binds(string cls, int space, int reg)
		{
			return b.Any((Binding x) => x.Class == cls && x.Space == space && x.Lower <= reg && (x.Count < 0 || reg < x.Lower + x.Count));
		}
	}

	public static RootSig.Desc GlobalFor(IEnumerable<ShaderInfo> libs, RootSig.Desc stock)
	{
		uint num = (from b in libs.SelectMany((ShaderInfo l) => l.Bindings).Where(IsSpaceBindless)
			select (uint)b.Space).DefaultIfEmpty(0u).Max();
		if (num == 0)
		{
			return stock;
		}
		List<uint[]> list = new List<uint[]>();
		list.Add(new uint[7] { 0u, 0u, 0u, 128u, 0u, 1u, 5u });
		list.AddRange(from k in Enumerable.Range(5, Math.Max(0, (int)(num - 4)))
			select new uint[7]
			{
				0u,
				0u,
				0u,
				4294967295u,
				0u,
				(uint)k,
				5u
			});
		list.Add(new uint[7] { 0u, 0u, 3u, 16u, 0u, 1u, 1u });
		list.Add(new uint[7] { 0u, 0u, 1u, 16u, 0u, 1u, 3u });
		list.AddRange(Enumerable.Range(0, 16).Select((int b) =>
		{
			uint[] array = new uint[5] { 2u, 0u, 0u, 1u, 8u };
			array[2] = (uint)b;
			return array;
		}));
		list.Add(new uint[8] { 0u, 0u, 1u, 1u, 0u, 1001u, 0u, 0u });
		return new RootSig.Desc(0u, list);
	}

	public static Choice? Choose(PlanRule? rule, EngineInfo engine, IReadOnlyCollection<ShaderInfo> libs, out string why)
	{
		why = $"no collection rule for {engine.Family} {engine.Version} without a recording";
		RtRule rt = rule?.Rt;
		if ((object)rt == null)
		{
			return null;
		}
		if (Selects("ue5Shape"))
		{
			string text = Ue5ShapeMismatch(libs);
			if (text != null)
			{
				why = "not stock UE 5.0-5.4's binding shape (" + text + "): no collection rule without a recording";
				return null;
			}
		}
		RootSig.Desc desc = rt.Global?.ToDesc();
		if ((object)desc == null)
		{
			return null;
		}
		byte[] samplers = RulesFile.RtSamplers(rt);
		uint flags = (rt.Config ? rt.Flags : uint.MaxValue);
		if (Selects("avalancheGlobal"))
		{
			RootSig.Desc desc2 = GlobalFor(libs, desc);
			if ((object)desc2 != desc)
			{
				return new Choice(desc2, samplers, null, flags, rt.Depth, 0u, rt.Attributes, "Avalanche's UE 4.27 fork (its bindless spaces; Hogwarts Legacy's recording: 981/981 collections rebuilt from its files)");
			}
		}
		return new Choice(desc, samplers, rt.Local?.ToDesc(), flags, rt.Depth, rt.Payload, rt.Attributes, rt.Note);
		bool Selects(string shape)
		{
			string[]? array = rt.Select;
			if (array == null)
			{
				return false;
			}
			return array.Contains(shape);
		}
	}

	private static bool IsSpaceBindless(Binding b)
	{
		if ((object)b != null && b.Class == "srv" && b.Count == -1)
		{
			int space = b.Space;
			if (space >= 4)
			{
				return space < 10;
			}
		}
		return false;
	}

	public static RootSig.Desc LocalRs(ResourceCounts c, bool rayGen, IReadOnlyList<Binding>? bindings = null, uint systemConstants = 4u)
	{
		if (rayGen)
		{
			return new RootSig.Desc(128u, new List<uint[]>());
		}
		int num = 3;
		List<uint[]> list = new List<uint[]>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<uint[]> span = CollectionsMarshal.AsSpan(list);
		span[0] = new uint[5] { 3u, 0u, 0u, 2u, 0u };
		span[1] = new uint[5] { 3u, 0u, 1u, 2u, 0u };
		span[2] = new uint[5] { 1u, 0u, 0u, 2u, systemConstants };
		List<uint[]> list2 = list;
		if (c.Srv > 0)
		{
			list2.Add(new uint[7]
			{
				0u,
				0u,
				0u,
				(uint)c.Srv,
				0u,
				0u,
				5u
			});
		}
		if (bindings != null && bindings.Any((Binding b) => (object)b != null && b.Class == "srv" && b.Count == -1 && b.Space == 4))
		{
			list2.Add(new uint[7] { 0u, 0u, 0u, 4294967295u, 0u, 4u, 5u });
		}
		if (c.Sampler > 0)
		{
			list2.Add(new uint[7]
			{
				0u,
				0u,
				3u,
				(uint)c.Sampler,
				0u,
				0u,
				1u
			});
		}
		if (c.Uav > 0)
		{
			list2.Add(new uint[7]
			{
				0u,
				0u,
				1u,
				(uint)c.Uav,
				0u,
				0u,
				3u
			});
		}
		for (uint num2 = 0u; num2 < c.Cb; num2++)
		{
			list2.Add(new uint[5] { 2u, 0u, num2, 0u, 8u });
		}
		return new RootSig.Desc(128u, list2);
	}

	public static byte[] Item(string library, string global, string localRayGen, string localOther, Rule r, Nv? nv = null)
	{
		byte[] array = new byte[(!nv.HasValue) ? 96 : 108];
		foreach (var (s, num) in new string[4] { library, global, localRayGen, localOther }.Select((string h, int i) => (h: h, i: i)))
		{
			Convert.FromHexString(s).CopyTo(array, 20 * num);
		}
		uint[] array2;
		if (!nv.HasValue)
		{
			array2 = new uint[4] { r.Payload, r.Attributes, r.Depth, r.Flags };
		}
		else
		{
			Nv valueOrDefault = nv.GetValueOrDefault();
			array2 = new uint[7] { r.Payload, r.Attributes, r.Depth, r.Flags, valueOrDefault.Slot, valueOrDefault.Space, valueOrDefault.Options };
		}
		uint[] array3 = array2;
		for (int num2 = 0; num2 < array3.Length; num2++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(80 + 4 * num2), array3[num2]);
		}
		return array;
	}

	public static ItemFields ParseItem(byte[] p)
	{
		return new ItemFields(H(0), H(1), H(2), H(3), U(0), U(1), U(2), U(3), (p.Length >= 108) ? new Nv?(new Nv(U(4), U(5), U(6))) : ((Nv?)null));
		string H(int i)
		{
			return PsoDb.Hex(p.AsSpan(20 * i, 20));
		}
		uint U(int i)
		{
			return BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(80 + 4 * i));
		}
	}

	public static Nv? LearnedNv(IReadOnlyCollection<PsoDb.Rec> collections, IEnumerable<PsoDb.Rec> records)
	{
		HashSet<string> keys = collections.Select((PsoDb.Rec r) => r.Key).ToHashSet();
		List<PsoDb.NvState> source = (from n in records.Where((PsoDb.Rec r) => r.Tag == 'N').Select(PsoDb.NvState.Parse)
			where keys.Contains(n.Target) && !n.Bare
			select n).ToList();
		if (keys.Count == 0 || (double)source.Select((PsoDb.NvState n) => n.Target).Distinct().Count() < 0.99 * (double)keys.Count)
		{
			return null;
		}
		return (from n in source
			group n by new Nv(n.Slot, n.Space, n.Options)).MaxBy((IGrouping<Nv, PsoDb.NvState> g) => g.Count()).Key;
	}

	public static uint RecordedOptions(IEnumerable<PsoDb.Rec> records)
	{
		List<PsoDb.Rec> stateObjects;
		return Options(LastNv(records, out stateObjects), stateObjects);
	}

	private static uint Options(Dictionary<string, PsoDb.NvState> last, List<PsoDb.Rec> stateObjects)
	{
		return (from r in stateObjects.DistinctBy((PsoDb.Rec r) => r.Key)
			where r.Tag == 'R' && last.TryGetValue(r.Key, out var value) && value.Options != 0
			group r by last[r.Key].Options).MaxBy((IGrouping<uint, PsoDb.Rec> g) => g.Count())?.Key ?? 0;
	}

	public static List<PsoDb.Rec> OptionFixes(IEnumerable<PsoDb.Rec> records)
	{
		Dictionary<string, PsoDb.NvState> last = LastNv(records, out List<PsoDb.Rec> stateObjects);
		uint options = Options(last, stateObjects);
		if (options == 0)
		{
			return new List<PsoDb.Rec>();
		}
		return (from n in (from r in stateObjects.DistinctBy((PsoDb.Rec r) => r.Key)
				where !last.TryGetValue(r.Key, out var value) || (value.Options == 0 && (value.Scope & 0x100) == 0)
				select r).Select((PsoDb.Rec r) =>
			{
				PsoDb.NvState nvState = (last.TryGetValue(r.Key, out var value) ? value : new PsoDb.NvState(r.Key, uint.MaxValue, 0u, 0u, 0u));
				return nvState with
				{
					Options = options
				};
			})
			select n.ToRec()).ToList();
	}

	public static Nv? WithOptions(Nv? nv, uint options)
	{
		if (options != 0 && (!nv.HasValue || nv.GetValueOrDefault().Options == 0))
		{
			Nv nv2 = nv ?? new Nv(uint.MaxValue, 0u, 0u);
			return nv2 with
			{
				Options = options
			};
		}
		return nv;
	}

	private static Dictionary<string, PsoDb.NvState> LastNv(IEnumerable<PsoDb.Rec> records, out List<PsoDb.Rec> stateObjects)
	{
		Dictionary<string, PsoDb.NvState> dictionary = new Dictionary<string, PsoDb.NvState>();
		stateObjects = new List<PsoDb.Rec>();
		foreach (PsoDb.Rec record in records)
		{
			if (record.Tag == 'N' && record.Payload.Length == 36)
			{
				PsoDb.NvState value = PsoDb.NvState.Parse(record);
				dictionary[value.Target] = value;
			}
			else if (PsoDb.IsStateObject(record.Tag))
			{
				stateObjects.Add(record);
			}
		}
		return dictionary;
	}

	public static uint OwnPayload(ReadOnlySpan<byte> library)
	{
		return (uint)(from f in ShaderContainer.Rdat(library).Functions
			where Kinds.Any(((int Kind, string Prefix) k) => k.Kind == f.Kind)
			select f.Payload).DefaultIfEmpty(0).Max();
	}

	public static IEnumerable<uint> Payloads(uint rule, uint own, IReadOnlyCollection<uint> pipelinePayloads)
	{
		if (rule == 0 && own == 0 && pipelinePayloads.Count != 0)
		{
			return pipelinePayloads;
		}
		return new _003C_003Ez__ReadOnlySingleElementList<uint>(Math.Max(rule, own));
	}

	public static byte[]? Collection(ReadOnlySpan<byte> library, string sha1, string global, string localRayGen, string localOther, uint payload, uint attributes, uint depth, uint flags, string? nameHash = null)
	{
		List<ShaderContainer.LibraryFunction> fns = (from f in ShaderContainer.Rdat(library).Functions
			where Kinds.Any(((int Kind, string Prefix) k) => k.Kind == f.Kind)
			orderby Array.FindIndex(Kinds, ((int Kind, string Prefix) k) => k.Kind == f.Kind)
			select f).ToList();
		if (fns.Count == 0)
		{
			return null;
		}
		if (nameHash == null)
		{
			nameHash = Convert.ToHexStringLower(new ReadOnlySpan<byte>(Convert.FromHexString(sha1).AsSpan(0, 8).ToArray()
				.Reverse()
				.ToArray()));
		}
		List<string> names = fns.Select((ShaderContainer.LibraryFunction f, int i) =>
		{
			string item = Kinds.First(((int Kind, string Prefix) k) => k.Kind == f.Kind).Prefix;
			string? text = nameHash;
			int num = fns.Take(i).Count((ShaderContainer.LibraryFunction libraryFunction) => libraryFunction.Kind == f.Kind);
			return item + "_" + text + ((num > 0) ? $"_{num}" : "");
		}).ToList();
		List<ShaderContainer.LibraryFunction> hit = fns.Where((ShaderContainer.LibraryFunction f) =>
		{
			int kind = f.Kind;
			return (uint)(kind - 8) <= 2u;
		}).ToList();
		bool flag = fns.Any((ShaderContainer.LibraryFunction f) => f.Kind == 7);
		W w = new W();
		uint subs = 0u;
		w.U32(0u).U32(0u);
		Sub(5u).Hash(sha1).U32((uint)fns.Count);
		foreach (ShaderContainer.LibraryFunction item2 in fns)
		{
			w.Str(Export(item2)).Str(item2.Name).U32(0u);
		}
		Sub(9u).U32(Math.Max(payload, (uint)fns.Max((ShaderContainer.LibraryFunction f) => f.Payload))).U32(Math.Max(attributes, (uint)fns.Max((ShaderContainer.LibraryFunction f) => f.Attributes)));
		Sub(7u).U32(1u).Names(fns.Select(Export));
		int g;
		for (g = 0; g < (from f in hit
			group f by f.Kind into k
			select k.Count()).DefaultIfEmpty(0).Max(); g++)
		{
			Sub(11u).Str("HitGroup_" + nameHash + ((g > 0) ? $"_{g}" : "")).U32((Of(8) != null) ? 1u : 0u).Str(Of(9))
				.Str(Of(10))
				.Str(Of(8));
		}
		Sub(10u).U32(depth);
		if (flags != uint.MaxValue)
		{
			Sub(0u).U32(flags);
		}
		Sub(1u).Hash(global);
		uint v = subs;
		Sub(2u).Hash(flag ? localRayGen : localOther);
		foreach (ShaderContainer.LibraryFunction item3 in fns)
		{
			Sub(7u).U32(v).Names(new _003C_003Ez__ReadOnlySingleElementList<string>(Export(item3)));
		}
		byte[] array = w.ToArray();
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(4), subs);
		return array;
		string Export(ShaderContainer.LibraryFunction f)
		{
			return names[fns.IndexOf(f)];
		}
		string? Of(int kind)
		{
			ShaderContainer.LibraryFunction libraryFunction = hit.Where((ShaderContainer.LibraryFunction f) => f.Kind == kind).ElementAtOrDefault(g);
			if ((object)libraryFunction == null)
			{
				return null;
			}
			return Export(libraryFunction);
		}
		W Sub(uint t)
		{
			subs++;
			return w.U32(t);
		}
	}

	public static Recorded? Read(PsoDb.Rec r)
	{
		if (r.Tag != 'R')
		{
			return null;
		}
		byte[] p = r.Payload;
		int pos = 0;
		try
		{
			if (U() != 0)
			{
				return null;
			}
			string text = null;
			string text2 = null;
			string text3 = null;
			string text4 = null;
			uint payload = 0u;
			uint attributes = 0u;
			uint depth = 0u;
			uint flags = uint.MaxValue;
			for (uint num = U(); num != 0; num--)
			{
				switch (U())
				{
				case 5u:
				{
					if (text != null)
					{
						return null;
					}
					text = H();
					for (uint num3 = U(); num3 != 0; num3--)
					{
						string text5 = S();
						if (text4 == null)
						{
							text4 = text5;
						}
						S();
						U();
					}
					break;
				}
				case 9u:
					payload = U();
					attributes = U();
					break;
				case 10u:
					depth = U();
					break;
				case 0u:
					flags = U();
					break;
				case 1u:
					text2 = H();
					break;
				case 2u:
					text3 = H();
					break;
				case 7u:
				{
					U();
					for (uint num2 = U(); num2 != 0; num2--)
					{
						S();
					}
					break;
				}
				case 11u:
					S();
					U();
					S();
					S();
					S();
					break;
				default:
					return null;
				}
			}
			if (pos == p.Length && text != null && text2 != null && text3 != null)
			{
				int? num4 = text4?.LastIndexOf('_');
				if (num4.HasValue)
				{
					int valueOrDefault = num4.GetValueOrDefault();
					if (valueOrDefault >= 0 && text4.Length - valueOrDefault == 17)
					{
						return new Recorded(text, text2, text3, payload, attributes, depth, flags, text4.Substring(valueOrDefault + 1));
					}
				}
			}
			return null;
		}
		catch (ArgumentOutOfRangeException)
		{
			return null;
		}
		string H()
		{
			string result = PsoDb.Hex(p.AsSpan(pos, 20));
			pos += 20;
			return result;
		}
		string? S()
		{
			uint num5 = U();
			if (num5 != uint.MaxValue)
			{
				string result = Encoding.Unicode.GetString(p, pos, (int)(2 * num5));
				pos += (int)(2 * num5);
				return result;
			}
			return null;
		}
		uint U()
		{
			uint result = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(pos));
			pos += 4;
			return result;
		}
	}

	public static (string Hash, byte[] Blob) Serialize(RootSig.Desc d, byte[] samplers)
	{
		byte[] array = RootSig.Serialize(d, samplers);
		return (Hash: PsoDb.Hex(SHA1.HashData(array)), Blob: array);
	}
}
