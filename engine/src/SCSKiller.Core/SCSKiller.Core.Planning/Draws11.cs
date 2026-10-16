using System;
using System.Collections.Generic;
using System.Linq;

namespace SCSKiller.Core.Planning;

public static class Draws11
{
	public sealed record Result(List<PsoDb.Draw11> Items, int Exact, int Projected, int VssProjected);

	public const int MaxGuesses = 4;

	public const uint TriangleList = 4u;

	public static Result Plan(IEnumerable<PsoDb.Draw11> recorded, Func<string, ShaderInfo?> info, IEnumerable<string> vss)
	{
		List<PsoDb.Draw11> items = new List<PsoDb.Draw11>();
		HashSet<string> keys = new HashSet<string>();
		Dictionary<string, (List<PsoDb.LayoutElem>, int)> dictionary = new Dictionary<string, (List<PsoDb.LayoutElem>, int)>();
		HashSet<string> drawn = new HashSet<string>();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		foreach (PsoDb.Draw11 item in recorded)
		{
			List<PsoDb.LayoutElem> list = ExactLayouts.Explicit(item.Layout);
			bool flag = list.All((PsoDb.LayoutElem e) => e.Offset != uint.MaxValue);
			if ((list.Count > 0) & flag)
			{
				string key = Key(list);
				dictionary[key] = (list, (!dictionary.TryGetValue(key, out var value)) ? 1 : (value.Item2 + 1));
			}
			ShaderInfo shaderInfo = info(item.Vs);
			if ((object)shaderInfo != null && shaderInfo.Stage == Stage.Vertex)
			{
				drawn.Add(item.Vs);
				num += (Add(flag ? item with
				{
					Layout = ExactLayouts.ReadLayout(list, shaderInfo)
				} : item, shaderInfo) ? 1 : 0);
			}
		}
		List<List<PsoDb.LayoutElem>> source = dictionary.Values.OrderByDescending(((List<PsoDb.LayoutElem> Layout, int N) c) => c.N).ThenBy(((List<PsoDb.LayoutElem> Layout, int N) c) => Key(c.Layout), StringComparer.Ordinal).Select(((List<PsoDb.LayoutElem> Layout, int N) c) => c.Layout)
			.ToList();
		foreach (string item2 in (from h in vss.Distinct()
			where !drawn.Contains(h)
			select h).Order(StringComparer.Ordinal))
		{
			ShaderInfo shaderInfo2 = info(item2);
			if ((object)shaderInfo2 == null)
			{
				continue;
			}
			List<SigElement> inputs = shaderInfo2.Inputs.Where((SigElement i) => i.SysValue == 0).ToList();
			if (inputs.Count == 0)
			{
				continue;
			}
			int num4 = 0;
			foreach (List<PsoDb.LayoutElem> item3 in source.Where((List<PsoDb.LayoutElem> l) => Fits(l, inputs)))
			{
				if (Add(new PsoDb.Draw11(item2, PsoDb.Zero, PsoDb.Zero, PsoDb.Zero, 4u, Ps: true, ExactLayouts.ReadLayout(item3, shaderInfo2)), shaderInfo2) && ++num4 == 4)
				{
					break;
				}
			}
			num2 += num4;
			num3 += ((num4 > 0) ? 1 : 0);
		}
		return new Result(items, num, num2, num3);
		bool Add(PsoDb.Draw11 d, ShaderInfo vs)
		{
			if (!keys.Add(AmdKey(d, vs)))
			{
				return false;
			}
			items.Add(d);
			return true;
		}
	}

	private static bool Fits(List<PsoDb.LayoutElem> layout, List<SigElement> inputs)
	{
		return inputs.All((SigElement i) => layout.Any((PsoDb.LayoutElem e) => e.Semantic.Equals(i.Semantic, StringComparison.OrdinalIgnoreCase) && e.Index == i.Index && ExactLayouts.CompClass(e.Format) == i.CompType));
	}

	public static string AmdKey(PsoDb.Draw11 d, ShaderInfo vs)
	{
		HashSet<(string, int Index)> unread = (from i in vs.Inputs
			where i.SysValue == 0 && i.ReadMask == 0
			select (i.Semantic.ToUpperInvariant(), Index: i.Index)).ToHashSet();
		return $"{d.Vs}|{d.Gs}|{d.Hs}|{d.Ds}|{d.Topology}|{d.Ps}|" + Key(d.Layout.Where((PsoDb.LayoutElem e) => !unread.Contains((e.Semantic.ToUpperInvariant(), e.Index))));
	}

	private static string Key(IEnumerable<PsoDb.LayoutElem> layout)
	{
		return string.Join(';', layout.Select((PsoDb.LayoutElem e) => $"{e.Semantic.ToUpperInvariant()}{e.Index}:{e.Format}@{e.Slot}+{e.Offset}/{e.Class}/{e.Step}").Order(StringComparer.Ordinal));
	}
}
