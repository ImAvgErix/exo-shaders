using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Planning;

public sealed class ExactLayouts
{
	public sealed record LinkState(byte[] Blend, byte[] DepthStencil, uint Dsv)
	{
		public string Key => $"{PsoDb.Hex(Blend)}|{PsoDb.Hex(DepthStencil)}|{Dsv}";
	}

	private sealed class InList : IEqualityComparer<(List<List<PsoDb.LayoutElem>> List, List<PsoDb.LayoutElem> Layout)>
	{
		public bool Equals((List<List<PsoDb.LayoutElem>> List, List<PsoDb.LayoutElem> Layout) a, (List<List<PsoDb.LayoutElem>> List, List<PsoDb.LayoutElem> Layout) b)
		{
			if (a.List == b.List)
			{
				return a.Layout.SequenceEqual(b.Layout);
			}
			return false;
		}

		public int GetHashCode((List<List<PsoDb.LayoutElem>> List, List<PsoDb.LayoutElem> Layout) x)
		{
			HashCode hashCode = default;
			hashCode.Add(RuntimeHelpers.GetHashCode(x.List));
			foreach (PsoDb.LayoutElem item in x.Layout)
			{
				hashCode.Add(item);
			}
			return hashCode.ToHashCode();
		}
	}

	private readonly Dictionary<string, LinkState> linkByPs = new Dictionary<string, LinkState>();

	private readonly Dictionary<string, Dictionary<string, (LinkState State, int N)>> linkCounts = new Dictionary<string, Dictionary<string, (LinkState, int)>>();

	private readonly Dictionary<string, string> rsKeys = new Dictionary<string, string>();

	private readonly Dictionary<(string, int, int), Dictionary<PsoDb.LayoutElem, int>> elemCounts = new Dictionary<(string, int, int), Dictionary<PsoDb.LayoutElem, int>>();

	private readonly HashSet<(List<List<PsoDb.LayoutElem>> List, List<PsoDb.LayoutElem> Layout)> added = new HashSet<(List<List<PsoDb.LayoutElem>>, List<PsoDb.LayoutElem>)>(new InList());

	public const uint AppendAligned = uint.MaxValue;

	private readonly Dictionary<string, List<List<PsoDb.LayoutElem>>?> covering = new Dictionary<string, List<List<PsoDb.LayoutElem>>>();

	public const uint InputSlots = 32u;

	public UnitPolicy Policy { get; }

	public IReadOnlyDictionary<string, ShaderInfo> Shaders { get; }

	public Dictionary<string, string> PartnerKeys { get; } = new Dictionary<string, string>();

	public Dictionary<string, List<List<PsoDb.LayoutElem>>> ReadLayouts { get; } = new Dictionary<string, List<List<PsoDb.LayoutElem>>>();

	public Dictionary<string, List<List<PsoDb.LayoutElem>>> LayoutsBySig { get; } = new Dictionary<string, List<List<PsoDb.LayoutElem>>>();

	public Dictionary<string, SortedSet<uint>> TopoOf { get; } = new Dictionary<string, SortedSet<uint>>();

	public Dictionary<string, List<string>> ShapesByPs { get; } = new Dictionary<string, List<string>>();

	public Dictionary<string, Dictionary<string, int>> ShapesByOutSig { get; } = new Dictionary<string, Dictionary<string, int>>();

	public Dictionary<string, (uint[] Formats, byte[] Masks, int[] LogicOps)> ShapeExample { get; } = new Dictionary<string, (uint[], byte[], int[])>();

	public List<List<PsoDb.LayoutElem>> FullLayouts { get; } = new List<List<PsoDb.LayoutElem>>();

	public List<PsoDb.PsoState> Graphics { get; } = new List<PsoDb.PsoState>();

	public List<PsoDb.Pso> Compute { get; } = new List<PsoDb.Pso>();

	public SortedDictionary<string, int> ClassFixes { get; } = new SortedDictionary<string, int>();

	private ExactLayouts(UnitPolicy policy, IReadOnlyDictionary<string, ShaderInfo> shaders)
	{
		Policy = policy;
		Shaders = shaders;
	}

	public static ExactLayouts FromDb(string db, UnitPolicy policy, IReadOnlyDictionary<string, ShaderInfo>? index = null)
	{
		List<PsoDb.Rec> list = new List<PsoDb.Rec>();
		Dictionary<string, byte[]> dictionary = new Dictionary<string, byte[]>();
		foreach (PsoDb.Rec item in PsoDb.Read(db))
		{
			if (item.Tag == 'B')
			{
				dictionary[PsoDb.Hex(item.Payload.AsSpan(0, 20))] = item.Payload[20..];
			}
			else
			{
				list.Add(item);
			}
		}
		return Build(list, dictionary, policy, index);
	}

	public static ExactLayouts Build(IEnumerable<PsoDb.Rec> records, IReadOnlyDictionary<string, byte[]> blobs, UnitPolicy policy, IReadOnlyDictionary<string, ShaderInfo>? index = null, Action? tick = null)
	{
		Dictionary<string, ShaderInfo> dictionary = new Dictionary<string, ShaderInfo>();
		string key;
		if (index != null)
		{
			foreach (KeyValuePair<string, ShaderInfo> item in index)
			{
				item.Deconstruct(out key, out var value);
				string key2 = key;
				ShaderInfo value2 = value;
				dictionary[key2] = value2;
			}
		}
		ExactLayouts exactLayouts = new ExactLayouts(policy, dictionary);
		byte[] value3;
		foreach (KeyValuePair<string, byte[]> blob2 in blobs)
		{
			blob2.Deconstruct(out key, out value3);
			string text = key;
			byte[] array = value3;
			if (array.Length < 32 || !((ReadOnlySpan<byte>)array.AsSpan(0, 4)).SequenceEqual("DXBC"u8))
			{
				continue;
			}
			ShaderInfo shaderInfo = null;
			try
			{
				shaderInfo = ShaderContainer.Parse(array, text, new ResourceCounts(0, 0, 0, 0));
			}
			catch (Exception)
			{
			}
			if (shaderInfo == null)
			{
				continue;
			}
			dictionary.TryAdd(text, shaderInfo);
			if (shaderInfo.Stage == Stage.Pixel)
			{
				string text2 = PartnerKey(shaderInfo);
				if (text2 != null)
				{
					exactLayouts.PartnerKeys[text] = text2;
				}
			}
		}
		foreach (ShaderInfo value4 in dictionary.Values)
		{
			if (value4.Stage == Stage.Pixel && !exactLayouts.PartnerKeys.ContainsKey(value4.Sha1))
			{
				string text3 = PartnerKey(value4);
				if (text3 != null)
				{
					exactLayouts.PartnerKeys[value4.Sha1] = text3;
				}
			}
		}
		foreach (KeyValuePair<string, byte[]> blob3 in blobs)
		{
			blob3.Deconstruct(out key, out value3);
			string key3 = key;
			byte[] blob = value3;
			if (!dictionary.ContainsKey(key3))
			{
				try
				{
					exactLayouts.rsKeys[key3] = RsKeyOf(blob, policy.FreeRsFlags);
				}
				catch (Exception)
				{
				}
			}
		}
		foreach (PsoDb.Rec record in records)
		{
			tick?.Invoke();
			char tag = record.Tag;
			if ((tag == 'C' || tag == 'G' || tag == 'S') ? true : false)
			{
				PsoDb.Pso pso = PsoDb.Parse(record);
				if (pso.Stages.ContainsKey(6))
				{
					exactLayouts.Compute.Add(pso);
				}
				else
				{
					exactLayouts.Add(PsoDb.ParseState(record));
				}
			}
		}
		return exactLayouts;
	}

	private void Add(PsoDb.PsoState s)
	{
		Graphics.Add(s);
		if (s.Stages.TryGetValue(1, out string value))
		{
			if (!TopoOf.TryGetValue(value, out SortedSet<uint> value2))
			{
				value2 = (TopoOf[value] = new SortedSet<uint>());
			}
			value2.Add(s.Topology);
			List<PsoDb.LayoutElem> list = Explicit(s.Layout);
			AddOnce(FullLayouts, list);
			foreach (PsoDb.LayoutElem item in list.Where((PsoDb.LayoutElem e) => e.Offset != uint.MaxValue))
			{
				(string, int, int) key = (item.Semantic.ToUpperInvariant(), item.Index, CompClass(item.Format));
				if (!elemCounts.TryGetValue(key, out Dictionary<PsoDb.LayoutElem, int> value3))
				{
					value3 = (elemCounts[key] = new Dictionary<PsoDb.LayoutElem, int>());
				}
				PsoDb.LayoutElem key2 = item with
				{
					Semantic = key.Item1
				};
				value3[key2] = value3.GetValueOrDefault(key2) + 1;
			}
			if (Shaders.TryGetValue(value, out ShaderInfo value4))
			{
				List<PsoDb.LayoutElem> layout = ReadLayout(list, value4);
				AddOnce(Get(ReadLayouts, value), layout);
				AddOnce(Get(LayoutsBySig, SigKey(value4)), layout);
			}
		}
		if (!s.Stages.TryGetValue(2, out string value5))
		{
			return;
		}
		string text = ShapeOf(s);
		ShapeExample.TryAdd(text, (s.RtFormats, s.RtWriteMasks, s.LogicOps));
		List<string> list2 = Get(ShapesByPs, value5);
		if (!list2.Contains(text))
		{
			list2.Add(text);
		}
		if (Shaders.TryGetValue(value5, out ShaderInfo value6))
		{
			Dictionary<string, int> dictionary2 = Get(ShapesByOutSig, OutSig(value6));
			dictionary2[text] = dictionary2.GetValueOrDefault(text) + 1;
		}
		LinkState link;
		if (s.Blend != null && s.DepthStencil != null)
		{
			link = new LinkState(s.Blend, s.DepthStencil, s.Dsv);
			linkByPs.TryAdd(value5 + "|" + text, link);
			Tally(text);
			if (value6 != null)
			{
				Tally(OutSig(value6) + "|" + text);
			}
		}
		void Tally(string k)
		{
			Dictionary<string, (LinkState, int)> dictionary3 = Get(linkCounts, k);
			dictionary3[link.Key] = (link, dictionary3.GetValueOrDefault(link.Key).Item2 + 1);
		}
	}

	public static string? PartnerKey(ShaderInfo ps)
	{
		List<SigElement> source = ps.Inputs.Where((SigElement i) => i.SysValue == 0).ToList();
		if (source.Any((SigElement i) => i.ReadMask == byte.MaxValue))
		{
			return null;
		}
		return string.Join(',', source.Select((SigElement i) => $"{i.Semantic.ToUpperInvariant()}{i.Index}:{i.ReadMask}/{i.Interpolation}").Order(StringComparer.Ordinal));
	}

	private static TV Get<TV>(Dictionary<string, TV> d, string k) where TV : new()
	{
		if (!d.TryGetValue(k, out TV value))
		{
			return d[k] = new TV();
		}
		return value;
	}

	private static void AddDistinct(List<List<PsoDb.LayoutElem>> list, List<PsoDb.LayoutElem> layout)
	{
		if (!list.Any((List<PsoDb.LayoutElem> l) => l.SequenceEqual(layout)))
		{
			list.Add(layout);
		}
	}

	private void AddOnce(List<List<PsoDb.LayoutElem>> list, List<PsoDb.LayoutElem> layout)
	{
		if (added.Add((list, layout)))
		{
			list.Add(layout);
		}
	}

	public static List<PsoDb.LayoutElem> ReadLayout(IReadOnlyList<PsoDb.LayoutElem> layout, ShaderInfo vs)
	{
		HashSet<(string, int Index)> inputs = (from i in vs.Inputs
			where i.SysValue == 0
			select (i.Semantic.ToUpperInvariant(), Index: i.Index)).ToHashSet();
		return (from e in layout
			select e with
			{
				Semantic = e.Semantic.ToUpperInvariant()
			} into e
			where inputs.Contains((e.Semantic, e.Index))
			select e).OrderBy((PsoDb.LayoutElem e) => e.Semantic, StringComparer.Ordinal).ThenBy((PsoDb.LayoutElem e) => e.Index).ToList();
	}

	public static List<PsoDb.LayoutElem> Explicit(IReadOnlyList<PsoDb.LayoutElem> layout)
	{
		Dictionary<uint, uint> dictionary = new Dictionary<uint, uint>();
		List<PsoDb.LayoutElem> list = new List<PsoDb.LayoutElem>();
		foreach (PsoDb.LayoutElem item in layout)
		{
			uint num = FormatSize(item.Format);
			uint num2 = item.Offset;
			if (num2 == uint.MaxValue && num != 0)
			{
				uint num3 = Math.Min(4u, num);
				num2 = (dictionary.GetValueOrDefault(item.Slot) + num3 - 1) / num3 * num3;
			}
			if (num2 != uint.MaxValue)
			{
				dictionary[item.Slot] = num2 + num;
			}
			list.Add(item with
			{
				Offset = num2
			});
		}
		return list;
	}

	public static string SigKey(ShaderInfo vs)
	{
		return string.Join(';', (from e in vs.Inputs
			where e.SysValue == 0
			select $"{e.Semantic.ToUpperInvariant()}/{e.Index}/{e.Register}/{e.Mask}/{e.CompType}").Order(StringComparer.Ordinal));
	}

	public static string OutSig(ShaderInfo ps)
	{
		return Planner.Sig(ps.Outputs, dropSys: false);
	}

	public static string ExportClass(uint fmt)
	{
		bool flag;
		switch (fmt)
		{
		case 10u:
		case 24u:
		case 26u:
		case 28u:
		case 29u:
		case 87u:
		case 91u:
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			return fmt.ToString();
		}
		return "fp16";
	}

	public static string ExportShape(PsoDb.PsoState s)
	{
		return string.Join(',', s.RtFormats.Select((uint f, int i) => ExportClass(f) + ((s.RtWriteMasks[i] == 0) ? "/m0" : "") + ((s.LogicOps[i] >= 0) ? $"/op{s.LogicOps[i]}" : "") + ((i == 0 && s.DualSource) ? "/ds" : "")));
	}

	public static string ExactShape(PsoDb.PsoState s)
	{
		if (s.RtFormats.Length != 0)
		{
			return string.Join(',', s.RtFormats.Select((uint f, int i) => f + ((s.RtWriteMasks[i] == 0) ? "/m0" : "") + ((s.LogicOps[i] >= 0) ? $"/op{s.LogicOps[i]}" : ""))) + ((s.Blend != null) ? ("/b" + PsoDb.Hex(SHA1.HashData(s.Blend)).Substring(0, 16)) : "");
		}
		return "";
	}

	public string ShapeOf(PsoDb.PsoState s)
	{
		if (!Policy.ExactExport)
		{
			return ExportShape(s);
		}
		return ExactShape(s);
	}

	public LinkState? Link(ShaderInfo ps, string shape)
	{
		if (linkByPs.TryGetValue(ps.Sha1 + "|" + shape, out LinkState value))
		{
			return value;
		}
		string[] array = new string[2]
		{
			OutSig(ps) + "|" + shape,
			shape
		};
		foreach (string key in array)
		{
			if (linkCounts.TryGetValue(key, out Dictionary<string, (LinkState, int)> value2))
			{
				return value2.Values.OrderByDescending(((LinkState State, int N) v) => v.N).ThenBy(((LinkState State, int N) v) => v.State.Key, StringComparer.Ordinal).First()
					.Item1;
			}
		}
		return null;
	}

	public string RsKey(string rs)
	{
		return rsKeys.GetValueOrDefault(rs, rs);
	}

	public void AddRootSignature(string sha1, byte[] blob)
	{
		if (!rsKeys.ContainsKey(sha1))
		{
			rsKeys[sha1] = RsKeyOf(blob, Policy.FreeRsFlags);
		}
	}

	private static string RsKeyOf(byte[] blob, uint free)
	{
		byte[] array = RootSig.Rts0(blob).ToArray();
		BitConverter.TryWriteBytes(array.AsSpan(20, 4), BitConverter.ToUInt32(array, 20) & ~free);
		return PsoDb.Hex(SHA1.HashData(array));
	}

	public Resolved<List<List<PsoDb.LayoutElem>>> Layouts(ShaderInfo vs)
	{
		Resolved<List<List<PsoDb.LayoutElem>>> result = LayoutsCore(vs);
		if (!Policy.ReadLayout || Policy.UvClamp == UvClampLayouts.None || result.Provenance != Provenance.Exact)
		{
			return result;
		}
		List<List<PsoDb.LayoutElem>> list = new List<List<PsoDb.LayoutElem>>();
		foreach (List<PsoDb.LayoutElem> item in result.Value)
		{
			AddOnce(list, item);
		}
		foreach (List<PsoDb.LayoutElem> item2 in result.Value)
		{
			foreach (List<PsoDb.LayoutElem> item3 in UvClampVariants(item2))
			{
				AddOnce(list, item3);
			}
		}
		return new Resolved<List<List<PsoDb.LayoutElem>>>(list, result.Provenance);
	}

	public static IEnumerable<List<PsoDb.LayoutElem>> UvClampVariants(List<PsoDb.LayoutElem> layout)
	{
		int start = 0;
		while (start < layout.Count)
		{
			int end = start + 1;
			uint size = FormatSize(layout[start].Format);
			for (; end < layout.Count; end++)
			{
				if (size == 0)
				{
					break;
				}
				PsoDb.LayoutElem layoutElem = layout[end];
				PsoDb.LayoutElem layoutElem2 = layout[end - 1];
				if (!(layoutElem.Semantic == layoutElem2.Semantic) || layoutElem.Index != layoutElem2.Index + 1 || layoutElem.Slot != layoutElem2.Slot || layoutElem.Format != layoutElem2.Format || layoutElem.Class != layoutElem2.Class || (layoutElem.Offset != layoutElem2.Offset && layoutElem.Offset != layoutElem2.Offset + size))
				{
					break;
				}
			}
			if (end - start >= 2)
			{
				for (int n = 1; n <= end - start; n++)
				{
					List<PsoDb.LayoutElem> list = layout.ToList();
					for (int i = start; i < end; i++)
					{
						list[i] = list[i]with
						{
							Offset = layout[start].Offset + (uint)((int)size * Math.Min(i - start, n - 1))
						};
					}
					yield return list;
				}
			}
			start = end;
		}
	}

	private Resolved<List<List<PsoDb.LayoutElem>>> LayoutsCore(ShaderInfo vs)
	{
		if (ReadLayouts.TryGetValue(vs.Sha1, out List<List<PsoDb.LayoutElem>> value))
		{
			return new Resolved<List<List<PsoDb.LayoutElem>>>(value, Provenance.Exact);
		}
		List<SigElement> list = vs.Inputs.Where((SigElement i) => i.SysValue == 0).ToList();
		int num;
		if (list.Count == 0)
		{
			num = 1;
			List<List<PsoDb.LayoutElem>> list2 = new List<List<PsoDb.LayoutElem>>(num);
			CollectionsMarshal.SetCount(list2, num);
			CollectionsMarshal.AsSpan(list2)[0] = new List<PsoDb.LayoutElem>();
			return new Resolved<List<List<PsoDb.LayoutElem>>>(list2, Provenance.Exact);
		}
		if (LayoutsBySig.TryGetValue(SigKey(vs), out List<List<PsoDb.LayoutElem>> value2))
		{
			return new Resolved<List<List<PsoDb.LayoutElem>>>(value2, Provenance.Inferred);
		}
		List<(string S, int Index, int CompType)> want = list.Select((SigElement i) => (S: i.Semantic.ToUpperInvariant(), Index: i.Index, CompType: i.CompType)).ToList();
		string key = string.Join(';', want.Select(((string S, int Index, int CompType) w) => $"{w.S}/{w.Index}/{w.CompType}").Order(StringComparer.Ordinal));
		if (!covering.TryGetValue(key, out List<List<PsoDb.LayoutElem>> value3))
		{
			List<List<PsoDb.LayoutElem>> list3 = FullLayouts.Where((List<PsoDb.LayoutElem> l) => want.All(((string S, int Index, int CompType) w) => l.Any((PsoDb.LayoutElem e) => e.Semantic.Equals(w.S, StringComparison.OrdinalIgnoreCase) && e.Index == w.Index && e.Offset != uint.MaxValue && CompClass(e.Format) == w.CompType))).ToList();
			value3 = (covering[key] = ((list3.Count == 0) ? null : new List<List<PsoDb.LayoutElem>>()));
			if (list3.Count > 0)
			{
				int min = list3.Min((List<PsoDb.LayoutElem> l) => l.Count);
				foreach (List<PsoDb.LayoutElem> item in list3.Where((List<PsoDb.LayoutElem> l) => l.Count == min))
				{
					AddDistinct(value3, ReadLayout(item, vs));
					if (value3.Count == 2)
					{
						break;
					}
				}
			}
		}
		if (value3 != null)
		{
			return new Resolved<List<List<PsoDb.LayoutElem>>>(value3.ToList(), Provenance.Inferred);
		}
		List<PsoDb.LayoutElem?> source = want.Select(((string S, int Index, int CompType) w) => (!elemCounts.TryGetValue((w.S, w.Index, w.CompType), out Dictionary<PsoDb.LayoutElem, int> value4)) ? ((PsoDb.LayoutElem?)null) : new PsoDb.LayoutElem?(value4.MaxBy((KeyValuePair<PsoDb.LayoutElem, int> x) => x.Value).Key)).ToList();
		List<PsoDb.LayoutElem> list5 = (source.All((PsoDb.LayoutElem? g) => g.HasValue) ? OneClassPerSlot(ReadLayout(source.Select((PsoDb.LayoutElem? g) => g.Value).ToList(), vs)) : null);
		num = 1;
		List<List<PsoDb.LayoutElem>> list6 = new List<List<PsoDb.LayoutElem>>(num);
		CollectionsMarshal.SetCount(list6, num);
		CollectionsMarshal.AsSpan(list6)[0] = list5 ?? ReadLayout(Planner.VsLayout(vs), vs);
		return new Resolved<List<List<PsoDb.LayoutElem>>>(list6, Provenance.Guessed);
	}

	private static (uint Class, uint Step) SlotClass(PsoDb.LayoutElem e)
	{
		return (Class: e.Class, Step: (e.Class == 1) ? e.Step : 0u);
	}

	public static bool ValidSlots(IReadOnlyList<PsoDb.LayoutElem> layout)
	{
		if (layout.All((PsoDb.LayoutElem e) => e.Slot < 32))
		{
			return (from e in layout
				group e by e.Slot).All((IGrouping<uint, PsoDb.LayoutElem> g) => g.Select(SlotClass).Distinct().Count() == 1);
		}
		return false;
	}

	public List<PsoDb.LayoutElem>? OneClassPerSlot(List<PsoDb.LayoutElem> layout)
	{
		if (ValidSlots(layout))
		{
			return layout;
		}
		Dictionary<uint, (uint Class, uint Step)> keep = (from layoutElem2 in layout
			group layoutElem2 by layoutElem2.Slot).ToDictionary((IGrouping<uint, PsoDb.LayoutElem> g) => g.Key, (IGrouping<uint, PsoDb.LayoutElem> g) => (from c in g.GroupBy(SlotClass)
			orderby c.Count() descending, c.Key.Class, c.Key.Step
			select c).First().Key);
		List<int> move = Enumerable.Range(0, layout.Count).Where((int index) =>
		{
			(uint, uint) tuple = SlotClass(layout[index]);
			(uint, uint) tuple2 = keep[layout[index].Slot];
			return tuple.Item1 != tuple2.Item1 || tuple.Item2 != tuple2.Item2;
		}).ToList();
		foreach (List<PsoDb.LayoutElem> full in FullLayouts)
		{
			List<PsoDb.LayoutElem> found = move.Select((int index) => full.FirstOrDefault((PsoDb.LayoutElem r) =>
			{
				(string, int, int) tuple = K(r);
				(string, int, int) tuple2 = K(layout[index]);
				if (tuple.Item1 == tuple2.Item1 && tuple.Item2 == tuple2.Item2 && tuple.Item3 == tuple2.Item3)
				{
					(uint, uint) tuple3 = SlotClass(r);
					(uint, uint) tuple4 = SlotClass(layout[index]);
					if (tuple3.Item1 == tuple4.Item1 && tuple3.Item2 == tuple4.Item2)
					{
						return r.Offset != uint.MaxValue;
					}
				}
				return false;
			})).ToList();
			if (found.All((PsoDb.LayoutElem r) => r.Semantic != null))
			{
				List<PsoDb.LayoutElem> list = Done("recorded layout", With((int num) => found[move.IndexOf(num)]with
				{
					Semantic = layout[num].Semantic
				}));
				if (list != null)
				{
					return list;
				}
			}
		}
		List<PsoDb.LayoutElem> result = layout.ToList();
		bool flag = true;
		foreach (int i in move)
		{
			PsoDb.LayoutElem e = layout[i];
			IEnumerable<PsoDb.LayoutElem> source;
			if (!elemCounts.TryGetValue(K(e), out Dictionary<PsoDb.LayoutElem, int> value))
			{
				IEnumerable<PsoDb.LayoutElem> enumerable = Array.Empty<PsoDb.LayoutElem>();
				source = enumerable;
			}
			else
			{
				source = from x in value.Where((KeyValuePair<PsoDb.LayoutElem, int> x) =>
					{
						(uint, uint) tuple = SlotClass(x.Key);
						(uint, uint) tuple2 = SlotClass(e);
						return tuple.Item1 == tuple2.Item1 && tuple.Item2 == tuple2.Item2 && x.Key.Slot < 32;
					})
					orderby x.Value descending
					select x.Key;
			}
			PsoDb.LayoutElem layoutElem = source.FirstOrDefault((PsoDb.LayoutElem r) => result.Where((PsoDb.LayoutElem o, int j) => j != i && o.Slot == r.Slot).All((PsoDb.LayoutElem o) =>
			{
				(uint, uint) tuple = SlotClass(o);
				(uint, uint) tuple2 = SlotClass(e);
				return tuple.Item1 == tuple2.Item1 && tuple.Item2 == tuple2.Item2;
			}));
			if (layoutElem.Semantic == null)
			{
				flag = false;
				break;
			}
			result[i] = layoutElem with
			{
				Semantic = e.Semantic
			};
		}
		if (flag)
		{
			List<PsoDb.LayoutElem> list2 = Done("recorded slot", result);
			if (list2 != null)
			{
				return list2;
			}
		}
		uint free = layout.Max((PsoDb.LayoutElem layoutElem2) => layoutElem2.Slot) + 1;
		Dictionary<(uint, (uint, uint)), uint> fresh = new Dictionary<(uint, (uint, uint)), uint>();
		List<PsoDb.LayoutElem> l = With((int index) =>
		{
			PsoDb.LayoutElem result2 = layout[index];
			uint slot;
			if (!fresh.TryGetValue((layout[index].Slot, SlotClass(layout[index])), out var value2))
			{
				uint num = (fresh[(layout[index].Slot, SlotClass(layout[index]))] = free++);
				slot = num;
			}
			else
			{
				slot = value2;
			}
			return result2 with
			{
				Slot = slot
			};
		});
		List<PsoDb.LayoutElem> list3 = Done("fresh slot", l);
		if (list3 != null)
		{
			return list3;
		}
		ClassFixes["skipped"] = ClassFixes.GetValueOrDefault("skipped") + 1;
		return null;
		List<PsoDb.LayoutElem>? Done(string path, List<PsoDb.LayoutElem> list4)
		{
			if (!ValidSlots(list4))
			{
				return null;
			}
			ClassFixes[path] = ClassFixes.GetValueOrDefault(path) + 1;
			return list4;
		}
		static (string, int, int) K(PsoDb.LayoutElem layoutElem2)
		{
			return (layoutElem2.Semantic.ToUpperInvariant(), layoutElem2.Index, CompClass(layoutElem2.Format));
		}
		List<PsoDb.LayoutElem> With(Func<int, PsoDb.LayoutElem> moved)
		{
			return layout.Select((PsoDb.LayoutElem layoutElem2, int num) => (!move.Contains(num)) ? layoutElem2 : moved(num)).ToList();
		}
	}

	public Resolved<List<string>> Shapes(ShaderInfo ps)
	{
		if (ShapesByPs.TryGetValue(ps.Sha1, out List<string> value))
		{
			return new Resolved<List<string>>(value, Provenance.Exact);
		}
		if (ShapesByOutSig.TryGetValue(OutSig(ps), out Dictionary<string, int> value2))
		{
			int num = value2.Values.Sum();
			List<string> list = new List<string>();
			int num2 = 0;
			foreach (var (item, num4) in value2.OrderByDescending((KeyValuePair<string, int> x) => x.Value).ThenBy((KeyValuePair<string, int> x) => x.Key, StringComparer.Ordinal))
			{
				list.Add(item);
				if ((double)(num2 += num4) >= 0.9 * (double)num)
				{
					break;
				}
			}
			return new Resolved<List<string>>(list, Provenance.Inferred);
		}
		uint[] item2 = Planner.Targets(ps).Rt;
		PsoDb.PsoState psoState = new PsoDb.PsoState("", new SortedDictionary<int, string>(), new List<PsoDb.LayoutElem>(), 3u, item2, ((IEnumerable<uint>)item2).Select((Func<uint, byte>)((uint _) => 15)).ToArray(), item2.Select((uint _) => -1).ToArray(), 0u, 1u);
		string text2 = ShapeOf(psoState);
		ShapeExample.TryAdd(text2, (psoState.RtFormats, psoState.RtWriteMasks, psoState.LogicOps));
		int num5 = 1;
		List<string> list2 = new List<string>(num5);
		CollectionsMarshal.SetCount(list2, num5);
		CollectionsMarshal.AsSpan(list2)[0] = text2;
		return new Resolved<List<string>>(list2, Provenance.Guessed);
	}

	public Resolved<List<uint>> Topologies(IReadOnlyDictionary<int, string> stages)
	{
		int num;
		if (stages.ContainsKey(4))
		{
			num = 1;
			List<uint> list = new List<uint>(num);
			CollectionsMarshal.SetCount(list, num);
			CollectionsMarshal.AsSpan(list)[0] = 4u;
			return new Resolved<List<uint>>(list, Provenance.Exact);
		}
		if (stages.TryGetValue(5, out string value) && Shaders.TryGetValue(value, out ShaderInfo value2))
		{
			uint num2 = Planner.TopologyType(value2.GsInputPrimitive);
			if (num2 != 0)
			{
				num = 1;
				List<uint> list2 = new List<uint>(num);
				CollectionsMarshal.SetCount(list2, num);
				CollectionsMarshal.AsSpan(list2)[0] = num2;
				return new Resolved<List<uint>>(list2, Provenance.Exact);
			}
		}
		if (stages.TryGetValue(1, out string value3) && TopoOf.TryGetValue(value3, out SortedSet<uint> value4))
		{
			return new Resolved<List<uint>>(value4.ToList(), Provenance.Exact);
		}
		num = 1;
		List<uint> list3 = new List<uint>(num);
		CollectionsMarshal.SetCount(list3, num);
		CollectionsMarshal.AsSpan(list3)[0] = 3u;
		return new Resolved<List<uint>>(list3, Provenance.Guessed);
	}

	public static int CompClass(uint fmt)
	{
		switch (fmt)
		{
		case 3u:
		case 7u:
		case 12u:
		case 17u:
		case 25u:
		case 30u:
		case 36u:
		case 42u:
		case 50u:
		case 57u:
		case 62u:
			return 1;
		case 4u:
		case 8u:
		case 14u:
		case 18u:
		case 32u:
		case 38u:
		case 43u:
		case 52u:
		case 59u:
		case 64u:
			return 2;
		case 2u:
		case 6u:
		case 10u:
		case 11u:
		case 13u:
		case 16u:
		case 24u:
		case 26u:
		case 28u:
		case 29u:
		case 31u:
		case 34u:
		case 35u:
		case 37u:
		case 41u:
		case 49u:
		case 51u:
		case 54u:
		case 56u:
		case 58u:
		case 61u:
		case 63u:
		case 87u:
		case 88u:
		case 91u:
			return 3;
		default:
			return 0;
		}
	}

	public static uint FormatSize(uint fmt)
	{
		switch (fmt)
		{
		case 2u:
		case 3u:
		case 4u:
			return 16u;
		case 6u:
		case 7u:
		case 8u:
			return 12u;
		case 10u:
		case 11u:
		case 12u:
		case 13u:
		case 14u:
		case 15u:
		case 16u:
		case 17u:
		case 18u:
			return 8u;
		case 24u:
		case 25u:
		case 26u:
		case 27u:
		case 28u:
		case 29u:
		case 30u:
		case 31u:
		case 32u:
		case 33u:
		case 34u:
		case 35u:
		case 36u:
		case 37u:
		case 38u:
		case 39u:
		case 40u:
		case 41u:
		case 42u:
		case 43u:
		case 87u:
		case 88u:
		case 91u:
			return 4u;
		case 49u:
		case 50u:
		case 51u:
		case 52u:
		case 53u:
		case 54u:
		case 55u:
		case 56u:
		case 57u:
		case 58u:
		case 59u:
			return 2u;
		case 61u:
		case 62u:
		case 63u:
		case 64u:
			return 1u;
		default:
			return 0u;
		}
	}
}
