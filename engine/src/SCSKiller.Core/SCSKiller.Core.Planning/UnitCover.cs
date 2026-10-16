using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SCSKiller.Core.Planning;

public sealed class UnitCover(ExactLayouts facts)
{
	public sealed record Candidate(IReadOnlyDictionary<int, string> Stages, string Rs, IReadOnlyList<List<PsoDb.LayoutElem>> Layouts, IReadOnlyList<uint> Topologies, IReadOnlyList<string> Shapes);

	private static readonly ConditionalWeakTable<IReadOnlyList<PsoDb.LayoutElem>, string> LayoutKeys = new ConditionalWeakTable<IReadOnlyList<PsoDb.LayoutElem>, string>();

	public ExactLayouts Facts { get; } = facts;

	public UnitPolicy Policy => Facts.Policy;

	public HashSet<Unit> Covered { get; } = new HashSet<Unit>();

	public static UnitCover Seeded(ExactLayouts facts, Func<string, bool>? replays = null)
	{
		UnitCover unitCover = new UnitCover(facts);
		unitCover.Covered.UnionWith(RecordedUnits(facts, replays));
		return unitCover;
	}

	public static IEnumerable<Unit> RecordedUnits(ExactLayouts x, Func<string, bool>? replays = null)
	{
		return x.Graphics.Where((PsoDb.PsoState s) => replays == null || s.Stages.Values.All(replays)).SelectMany((PsoDb.PsoState s) => UnitsOf(x, s)).Concat(from c in x.Compute
			where replays == null || c.Stages.Values.All(replays)
			select new Unit(Stage.Compute, c.Stages[6], x.RsKey(c.Rs)));
	}

	public static IEnumerable<Unit> UnitsOf(ExactLayouts x, PsoDb.PsoState s)
	{
		List<PsoDb.LayoutElem> readLayout = ((s.Stages.TryGetValue(1, out string value) && x.Shaders.TryGetValue(value, out ShaderInfo value2)) ? ExactLayouts.ReadLayout(ExactLayouts.Explicit(s.Layout), value2) : ExactLayouts.Explicit(s.Layout));
		return Units(x, s.Stages, s.Rs, readLayout, s.Topology, x.ShapeOf(s));
	}

	public static IEnumerable<Unit> Units(ExactLayouts x, IReadOnlyDictionary<int, string> stages, string rs, IReadOnlyList<PsoDb.LayoutElem> readLayout, uint topology, string shape)
	{
		foreach (var (num2, text2) in stages)
		{
			Stage stage = (Stage)num2;
			yield return stage switch
			{
				Stage.Pixel => PsUnit(x, text2, rs, shape), 
				Stage.Vertex => VsUnit(x, stages, text2, rs, readLayout, topology, shape), 
				_ => new Unit(stage, text2, x.RsKey(rs)), 
			};
		}
	}

	private static Unit VsUnit(ExactLayouts x, IReadOnlyDictionary<int, string> stages, string vs, string rs, IReadOnlyList<PsoDb.LayoutElem> readLayout, uint topology, string shape)
	{
		UnitPolicy policy = x.Policy;
		string text = x.RsKey(rs);
		if (policy.ReadLayout)
		{
			text = text + "|" + LayoutKey(readLayout);
		}
		if (policy.Topology)
		{
			text = text + "|" + topology;
		}
		if (policy.NextStage)
		{
			text = text + "|" + Next(stages, shape);
		}
		if (policy.PartnerReads && stages.TryGetValue(2, out string value) && x.PartnerKeys.TryGetValue(value, out string value2))
		{
			text = text + "|r:" + value2;
		}
		return new Unit(Stage.Vertex, vs, text);
	}

	private static Unit PsUnit(ExactLayouts x, string ps, string rs, string shape)
	{
		return new Unit(Stage.Pixel, ps, x.Policy.ExportShape ? (x.RsKey(rs) + "|" + shape) : x.RsKey(rs));
	}

	private static string LayoutKey(IReadOnlyList<PsoDb.LayoutElem> layout)
	{
		return LayoutKeys.GetValue(layout, (IReadOnlyList<PsoDb.LayoutElem> l) => string.Join(';', l.Select((PsoDb.LayoutElem e) => $"{e.Semantic}/{e.Index}/{e.Format}/{e.Slot}/{e.Offset}/{e.Class}/{e.Step}")));
	}

	private static string Next(IReadOnlyDictionary<int, string> stages, string shape)
	{
		if (!stages.TryGetValue(5, out string value))
		{
			if (!stages.TryGetValue(4, out string value2))
			{
				if (stages.ContainsKey(2))
				{
					if (!Bound(shape))
					{
						return "ps:no-rt";
					}
					return "ps";
				}
				return "none";
			}
			return "hs:" + value2;
		}
		return "gs:" + value;
	}

	private static bool Bound(string shape)
	{
		return shape != "";
	}

	public List<UnitPick> Cover(IReadOnlyDictionary<int, string> stages, string rs, IReadOnlyList<List<PsoDb.LayoutElem>> layouts, IReadOnlyList<uint> topologies, IReadOnlyList<string> shapes, bool pairsOnly = false)
	{
		if (pairsOnly && (!stages.ContainsKey(1) || !stages.ContainsKey(2)))
		{
			return new List<UnitPick>();
		}
		List<(List<PsoDb.LayoutElem>, uint)> list;
		if (layouts.Count != 0 && stages.ContainsKey(1))
		{
			list = layouts.SelectMany((List<PsoDb.LayoutElem> l) =>
			{
				IReadOnlyList<uint> source4;
				if (topologies.Count != 0)
				{
					source4 = topologies;
				}
				else
				{
					IReadOnlyList<uint> readOnlyList = new _003C_003Ez__ReadOnlySingleElementList<uint>(3u);
					source4 = readOnlyList;
				}
				return source4.Select((uint t) => (l: l, t: t));
			}).ToList();
		}
		else
		{
			int num = 1;
			list = new List<(List<PsoDb.LayoutElem>, uint)>(num);
			CollectionsMarshal.SetCount(list, num);
			CollectionsMarshal.AsSpan(list)[0] = (new List<PsoDb.LayoutElem>(), 0u);
		}
		List<string> list2;
		if (shapes.Count != 0 && stages.ContainsKey(2))
		{
			list2 = shapes.ToList();
		}
		else
		{
			int num = 1;
			list2 = new List<string>(num);
			CollectionsMarshal.SetCount(list2, num);
			CollectionsMarshal.AsSpan(list2)[0] = "";
		}
		List<string> psShapes = list2;
		string vsSha = stages.GetValueOrDefault(1);
		string psSha = stages.GetValueOrDefault(2);
		bool keyed = Policy.NextStage && psSha != null && vsSha != null;
		List<bool> list3;
		if (!keyed)
		{
			int num = 1;
			list3 = new List<bool>(num);
			CollectionsMarshal.SetCount(list3, num);
			CollectionsMarshal.AsSpan(list3)[0] = psShapes.Any(Bound);
		}
		else
		{
			list3 = psShapes.Select(Bound).Distinct().ToList();
		}
		List<bool> bindings = list3;
		List<((List<PsoDb.LayoutElem>, uint), bool, Unit?)> source = list.SelectMany(((List<PsoDb.LayoutElem>, uint) v) => bindings.Select((bool flag) => (V: v, Bound: flag, U: (vsSha == null) ? ((Unit?)null) : new Unit?(VsUnit(Facts, stages, vsSha, rs, v.Item1, v.Item2, flag ? psShapes.First(Bound) : ""))))).DistinctBy((((List<PsoDb.LayoutElem>, uint) V, bool Bound, Unit? U) v) => v.U).ToList();
		List<(string, Unit?)> source2 = psShapes.Select((string s) => (S: s, U: (psSha == null) ? ((Unit?)null) : new Unit?(PsUnit(Facts, psSha, rs, s)))).DistinctBy(((string S, Unit? U) s) => s.U).ToList();
		List<Unit> source3 = (from s in stages.Where((KeyValuePair<int, string> s) =>
			{
				int key = s.Key;
				bool flag = (uint)(key - 1) <= 1u;
				return !flag;
			})
			select new Unit((Stage)s.Key, s.Value, Facts.RsKey(rs))).ToList();
		int num2 = source.Count((((List<PsoDb.LayoutElem>, uint) V, bool Bound, Unit? U) v) => IsNew(v.U));
		int num3 = source2.Count(((string S, Unit? U) s) => IsNew(s.U));
		if (num2 == 0 && num3 == 0 && !source3.Any((Unit u) => IsNew(u)))
		{
			return new List<UnitPick>();
		}
		List<((List<PsoDb.LayoutElem>, uint) V, bool Bound, Unit? U)> vs = source.OrderBy((((List<PsoDb.LayoutElem>, uint) V, bool Bound, Unit? U) v) => !IsNew(v.U)).ToList();
		List<(string, Unit?)> list4 = source2.OrderBy(((string S, Unit? U) s) => !IsNew(s.U)).ToList();
		List<((List<PsoDb.LayoutElem>, uint), string, Unit?, Unit?)> list5 = new List<((List<PsoDb.LayoutElem>, uint), string, Unit?, Unit?)>();
		foreach (bool b in bindings)
		{
			List<((List<PsoDb.LayoutElem>, uint), bool, Unit?)> list6 = vs.Where((((List<PsoDb.LayoutElem>, uint) V, bool Bound, Unit? U) v) => v.Bound == b).ToList();
			List<((List<PsoDb.LayoutElem>, uint), bool, Unit?)> list7 = ((list6 != null && list6.Count > 0) ? list6 : vs);
			List<(string, Unit?)> list8 = (keyed ? list4.Where(((string S, Unit? U) s) => Bound(s.S) == b).ToList() : list4);
			int val = list7.Count((((List<PsoDb.LayoutElem>, uint) V, bool Bound, Unit? U) v) => IsNew(v.U));
			int val2 = list8.Count(((string S, Unit? U) s) => IsNew(s.U));
			int num4 = (pairsOnly ? Math.Min(val, val2) : Math.Max(val, val2));
			for (int num5 = 0; num5 < num4; num5++)
			{
				((List<PsoDb.LayoutElem>, uint), bool, Unit?) tuple = list7[(num5 < list7.Count) ? num5 : 0];
				(string, Unit?) tuple2 = list8[(num5 < list8.Count) ? num5 : 0];
				list5.Add((tuple.Item1, tuple2.Item1, tuple.Item3, tuple2.Item2));
			}
		}
		if (list5.Count == 0 && !pairsOnly)
		{
			(string, Unit?) tuple3 = list4.First(((string S, Unit? U) s) => !keyed || Bound(s.S) == vs[0].Bound);
			list5.Add((vs[0].V, tuple3.Item1, vs[0].U, tuple3.Item2));
		}
		List<UnitPick> list9 = new List<UnitPick>();
		foreach (var item5 in list5)
		{
			(List<PsoDb.LayoutElem>, uint) item = item5.Item1;
			string item2 = item5.Item2;
			Unit? item3 = item5.Item3;
			Unit? item4 = item5.Item4;
			List<Unit> list10 = source3.ToList();
			if (item3.HasValue)
			{
				Unit valueOrDefault = item3.GetValueOrDefault();
				list10.Add(valueOrDefault);
			}
			if (item4.HasValue)
			{
				Unit valueOrDefault2 = item4.GetValueOrDefault();
				list10.Add(valueOrDefault2);
			}
			if (!list10.All(Covered.Contains))
			{
				Covered.UnionWith(list10);
				list9.Add(new UnitPick(item.Item1, item.Item2, item2));
			}
		}
		return list9;
		bool IsNew(Unit? u)
		{
			if (u.HasValue)
			{
				Unit valueOrDefault3 = u.GetValueOrDefault();
				return !Covered.Contains(valueOrDefault3);
			}
			return false;
		}
	}

	public List<(Candidate Candidate, UnitPick Pick)> CoverAll(IReadOnlyList<Candidate> candidates, Action? tick = null)
	{
		List<(int, Candidate, UnitPick)> list = new List<(int, Candidate, UnitPick)>();
		bool[] array = new bool[2] { true, false };
		foreach (bool pairsOnly in array)
		{
			for (int j = 0; j < candidates.Count; j++)
			{
				tick?.Invoke();
				Candidate c = candidates[j];
				list.AddRange(from p in Cover(c.Stages, c.Rs, c.Layouts, c.Topologies, c.Shapes, pairsOnly)
					select (i: j, c: c, p: p));
			}
		}
		return (from p in list
			orderby p.Item1
			select (p.Item2, p.Item3)).ToList();
	}
}
