using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

public static class WarmInputs
{
	public sealed record Recorded(List<(string Key, List<List<string>> Missing)> Records, HashSet<string> Blobs)
	{
		public Dictionary<string, List<string>> Members { get; init; } = new Dictionary<string, List<string>>();

		public int LaunchOnly { get; init; }

		public bool PerStage { get; init; }

		public bool RayTracing { get; init; } = true;

		public static Recorded Read(IReadOnlyList<string> recordings, Func<string, bool> elsewhere, bool nvidia = false, IReadOnlySet<string>? leaveOut = null, bool rayTracing = true)
		{
			HashSet<string> blobs = new HashSet<string>();
			Dictionary<string, PsoDb.NvState> nv = new Dictionary<string, PsoDb.NvState>();
			HashSet<string> union = new HashSet<string>();
			if (nvidia)
			{
				List<PsoDb.Rec> list = (from r in recordings.Where(File.Exists).SelectMany(PsoDb.Read)
					where r.Tag != 'B' && (r.Tag != 'N' || union.Add(r.Key))
					select r).ToList();
				foreach (PsoDb.Rec item4 in list.Concat(RtCollections.OptionFixes(list)))
				{
					if (item4.Tag == 'N' && item4.Payload.Length == 36)
					{
						PsoDb.NvState value = PsoDb.NvState.Parse(item4);
						nv[value.Target] = value;
					}
				}
			}
			HashSet<string> hashSet = ((leaveOut == null) ? new HashSet<string>() : new HashSet<string>(leaveOut));
			HashSet<string> hashSet2 = new HashSet<string>();
			Dictionary<string, List<string>> lacking = new Dictionary<string, List<string>>();
			Dictionary<string, List<string>> dictionary = new Dictionary<string, List<string>>();
			List<(string Key, List<List<string>> Missing)> records = new List<(string, List<List<string>>)>();
			Dictionary<string, int> at = new Dictionary<string, int>();
			foreach (string item5 in recordings.Where(File.Exists))
			{
				foreach (PsoDb.Rec item6 in PsoDb.Read(item5))
				{
					if (item6.Tag == 'B')
					{
						if (item6.Payload.Length >= 20)
						{
							blobs.Add(PsoDb.Hex(item6.Payload.AsSpan(0, 20)));
						}
						continue;
					}
					char tag = item6.Tag;
					bool flag = ((tag == 'L' || tag == 'N' || tag == 'W') ? true : false);
					if (flag || (!rayTracing && Planner.LeftOutWithoutRt(item6)) || !hashSet.Add(item6.Key))
					{
						continue;
					}
					if (!nvidia && PsoDb.IsStateObject(item6.Tag) && (PsoDb.HasLaunchAlias(item6) || Depends(item6).Any(hashSet2.Contains)))
					{
						hashSet2.Add(item6.Key);
						continue;
					}
					if (nvidia)
					{
						List<(int, string, string)> list2 = Stages(item6);
						if (list2 != null && list2.Count > 0)
						{
							List<string> gone = Missing(item6, (string h) => blobs.Contains(h) || elsewhere(h));
							foreach (var item7 in list2)
							{
								int item = item7.Item1;
								string item2 = item7.Item2;
								string item3 = item7.Item3;
								string key = Unit(item, item2, item3, State(item6));
								int num = 1;
								List<string> list3 = new List<string>(num);
								CollectionsMarshal.SetCount(list3, num);
								CollectionsMarshal.AsSpan(list3)[0] = item6.Key;
								if (!dictionary.TryAdd(key, list3))
								{
									dictionary[key].Add(item6.Key);
								}
								Add(key, gone);
							}
							continue;
						}
					}
					List<string> list4 = Missing(item6, (string h) => blobs.Contains(h) || elsewhere(h));
					if (PsoDb.IsStateObject(item6.Tag))
					{
						list4 = list4.Union(Depends(item6).SelectMany((string d) => lacking.GetValueOrDefault(d) ?? new List<string>())).ToList();
						if (list4.Count > 0)
						{
							lacking[item6.Key] = list4;
						}
					}
					string text = ((nvidia && PsoDb.IsStateObject(item6.Tag)) ? Identity(item6) : item6.Key);
					if (text != item6.Key)
					{
						int num = 1;
						List<string> list5 = new List<string>(num);
						CollectionsMarshal.SetCount(list5, num);
						CollectionsMarshal.AsSpan(list5)[0] = item6.Key;
						if (!dictionary.TryAdd(text, list5))
						{
							dictionary[text].Add(item6.Key);
						}
					}
					Add(text, list4);
				}
			}
			return new Recorded(records.Select(((string Key, List<List<string>> Missing) r) => (Key: r.Key, Left(r.Missing))).ToList(), blobs)
			{
				Members = dictionary,
				LaunchOnly = hashSet2.Count,
				PerStage = nvidia,
				RayTracing = rayTracing
			};
			void Add(string text2, List<string> list6)
			{
				if (at.TryGetValue(text2, out var value2))
				{
					records[value2].Missing.Add(list6);
				}
				else
				{
					at[text2] = records.Count;
					List<(string Key, List<List<string>> Missing)> list7 = records;
					int num2 = 1;
					List<List<string>> list8 = new List<List<string>>(num2);
					CollectionsMarshal.SetCount(list8, num2);
					CollectionsMarshal.AsSpan(list8)[0] = list6;
					list7.Add((text2, list8));
				}
			}
			string Identity(PsoDb.Rec r)
			{
				return PsoDb.Hex(SHA1.HashData(Encoding.UTF8.GetBytes("so" + PsoDb.StateObjectIdentity(r) + "|" + State(r))));
			}
			List<List<string>> Left(List<List<string>> each)
			{
				List<List<string>> list6 = each.Select((List<string> m) => (m.Count != 0) ? m.Where((string h) => !blobs.Contains(h)).ToList() : m).ToList();
				if (list6.Any((List<string> m) => m.Count == 0))
				{
					int num2 = 1;
					List<List<string>> list7 = new List<List<string>>(num2);
					CollectionsMarshal.SetCount(list7, num2);
					CollectionsMarshal.AsSpan(list7)[0] = new List<string>();
					return list7;
				}
				return list6;
			}
			string State(PsoDb.Rec r)
			{
				return StateOf(nv, r);
			}
		}
	}

	private static string StateOf(Dictionary<string, PsoDb.NvState> nv, PsoDb.Rec r)
	{
		if (!nv.TryGetValue(r.Key, out var value) || value.Bare)
		{
			return "none";
		}
		return $"{value.Slot},{value.Space},{value.Options}";
	}

	private static string Unit(int stage, string shader, string rs, string state)
	{
		return PsoDb.Hex(SHA1.HashData(Encoding.UTF8.GetBytes($"stage{stage}|{shader}|{rs}|{state}")));
	}

	private static List<(int Stage, string Shader, string Rs)> Stages(PsoDb.Rec r)
	{
		char tag = r.Tag;
		if ((tag != 'C' && tag != 'G' && tag != 'S') || 1 == 0)
		{
			return new List<(int, string, string)>();
		}
		try
		{
			List<(int, string, string)> result;
			if (PsoDb.StreamOutputOf(r) == null)
			{
				PsoDb.Pso p = PsoDb.Parse(r);
				result = p.Stages.Select((KeyValuePair<int, string> s) => (Key: s.Key, Value: s.Value, Rs: p.Rs)).ToList();
			}
			else
			{
				result = new List<(int, string, string)>();
			}
			return result;
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is KeyNotFoundException || ex is IndexOutOfRangeException || ex is OverflowException) ? true : false)
		{
			return new List<(int, string, string)>();
		}
	}

	private static List<string> Depends(PsoDb.Rec r)
	{
		try
		{
			return PsoDb.ParseStateObject(r).Depends;
		}
		catch (InvalidDataException)
		{
			return new List<string>();
		}
	}

	private static List<string> Missing(PsoDb.Rec r, Func<string, bool> has)
	{
		try
		{
			return (from h in Rehydrate.References(new _003C_003Ez__ReadOnlySingleElementList<PsoDb.Rec>(r))
				where !has(h)
				select h).ToList();
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is KeyNotFoundException || ex is IndexOutOfRangeException) ? true : false)
		{
			return new List<string>();
		}
	}

	public static HashSet<string> Of(Recorded recorded, string? planFile, IEnumerable<PsoDb.Rec> packEntries, Func<string, bool> elsewhere)
	{
		HashSet<string> blobs = new HashSet<string>(recorded.Blobs);
		List<PsoDb.Rec> list = Planner.PlanBody(planFile);
		foreach (PsoDb.Rec item6 in list)
		{
			if (item6.Tag == 'B' && item6.Payload.Length >= 20)
			{
				blobs.Add(PsoDb.Hex(item6.Payload.AsSpan(0, 20)));
			}
		}
		List<(string, PsoDb.Rec)> list2 = Planned(list, packEntries, recorded.RayTracing).ToList();
		Dictionary<string, List<string>> dictionary = new Dictionary<string, List<string>>();
		if (recorded.PerStage && recorded.Members.Count > 0)
		{
			Dictionary<string, PsoDb.NvState> dictionary2 = new Dictionary<string, PsoDb.NvState>();
			foreach (PsoDb.Rec item7 in list)
			{
				if (item7.Tag == 'N' && item7.Payload.Length == 36)
				{
					PsoDb.NvState value = PsoDb.NvState.Parse(item7);
					dictionary2[value.Target] = value;
				}
			}
			foreach (var item8 in list2)
			{
				string item = item8.Item1;
				PsoDb.Rec item2 = item8.Item2;
				List<(int, string, string)> list3 = Stages(item2);
				if (list3 == null || list3.Count <= 0)
				{
					continue;
				}
				string state = StateOf(dictionary2, item2);
				foreach (var item9 in list3)
				{
					int item3 = item9.Item1;
					string item4 = item9.Item2;
					string item5 = item9.Item3;
					string key = Unit(item3, item4, item5, state);
					if (recorded.Members.ContainsKey(key))
					{
						List<string> list6;
						if (!dictionary.TryGetValue(key, out var value2))
						{
							List<string> list4 = (dictionary[key] = new List<string>());
							list6 = list4;
						}
						else
						{
							list6 = value2;
						}
						list6.Add(item);
					}
				}
			}
		}
		HashSet<string> hashSet = new HashSet<string>();
		HashSet<string> hashSet2 = new HashSet<string>();
		foreach (var (text, source) in recorded.Records)
		{
			hashSet2.Add(text);
			List<string> valueOrDefault = recorded.Members.GetValueOrDefault(text);
			if (valueOrDefault != null)
			{
				hashSet2.UnionWith(valueOrDefault);
			}
			hashSet.Add((source.Any((List<string> x) => x.All(Has)) ? text : (text + "!")) + ((valueOrDefault != null) ? ("|" + string.Join('|', valueOrDefault)) : "") + (dictionary.TryGetValue(text, out var value3) ? ("~" + string.Join('~', value3)) : ""));
		}
		foreach (var (text2, r) in list2)
		{
			if (hashSet2.Add(text2))
			{
				hashSet.Add((Missing(r, Has).Count == 0) ? text2 : (text2 + "!"));
			}
		}
		return hashSet;
		bool Has(string h)
		{
			if (!blobs.Contains(h))
			{
				return elsewhere(h);
			}
			return true;
		}
	}

	private static IEnumerable<(string Key, PsoDb.Rec Rec)> Planned(List<PsoDb.Rec> body, IEnumerable<PsoDb.Rec> packEntries, bool rayTracing = true)
	{
		return (from x in Planner.PlanInputs(body)
			where rayTracing || !Planner.LeftOutWithoutRt(x.Rec)
			select (x.Rec.Tag != 'M') ? x : (Key: MiddlewarePacks.Unwrap(x.Rec).Entry.Key, MiddlewarePacks.Unwrap(x.Rec).Entry)).Concat(packEntries.Select((PsoDb.Rec r) => (Key: r.Key, r: r)));
	}

	private static IEnumerable<(string Key, List<string> Units)> PlannedUnits(string? planFile, IEnumerable<PsoDb.Rec> packEntries)
	{
		List<PsoDb.Rec> list = Planner.PlanBody(planFile);
		Dictionary<string, PsoDb.NvState> nv = new Dictionary<string, PsoDb.NvState>();
		foreach (PsoDb.Rec item3 in list)
		{
			if (item3.Tag == 'N' && item3.Payload.Length == 36)
			{
				PsoDb.NvState value = PsoDb.NvState.Parse(item3);
				nv[value.Target] = value;
			}
		}
		foreach (var item4 in Planned(list, packEntries))
		{
			string item = item4.Key;
			PsoDb.Rec item2 = item4.Rec;
			List<(int, string, string)> list2 = Stages(item2);
			if (list2 != null && list2.Count > 0)
			{
				string state = StateOf(nv, item2);
				yield return (Key: item, Units: list2.Select(((int Stage, string Shader, string Rs) s) => Unit(s.Stage, s.Shader, s.Rs, state)).ToList());
			}
		}
	}

	public static IEnumerable<string> PlannedStages(string? planFile, IReadOnlySet<string> inputs, Func<string, bool> crashed)
	{
		return (from p in PlannedUnits(planFile, Array.Empty<PsoDb.Rec>())
			where inputs.Contains(p.Key) && !crashed(p.Key)
			select p).SelectMany(((string Key, List<string> Units) p) => p.Units).Distinct();
	}

	public static HashSet<string> StagesTaken(string? planFile, IEnumerable<PsoDb.Rec> packEntries, IReadOnlySet<string> keys, IReadOnlySet<string> baseline)
	{
		HashSet<string> hashSet = new HashSet<string>();
		foreach (string item in from p in PlannedUnits(planFile, packEntries)
			where keys.Contains(p.Key) && p.Units.All(baseline.Contains)
			select p.Key)
		{
			hashSet.Add(item);
		}
		return hashSet;
	}

	public static HashSet<string> Of(IReadOnlyList<string> recordings, string? planFile, IEnumerable<PsoDb.Rec> packEntries, Func<string, bool> elsewhere)
	{
		return Of(Recorded.Read(recordings, elsewhere), planFile, packEntries, elsewhere);
	}

	public static string Key(string input)
	{
		return input.Substring(0, 40);
	}

	public static bool Lacks(string input)
	{
		if (input.Length > 40)
		{
			return input[40] == '!';
		}
		return false;
	}

	public static IEnumerable<string> Records(string input)
	{
		if (!input.Contains('|'))
		{
			return new _003C_003Ez__ReadOnlySingleElementList<string>(Key(input));
		}
		return input.Split('~')[0].Split('|').Skip(1);
	}

	public static IEnumerable<string> CompiledBy(string input)
	{
		return input.Split('~').Skip(1);
	}

	public static bool Taken(IReadOnlySet<string> baseline, string input, Func<string, bool>? crashed = null)
	{
		bool missing = Lacks(input);
		if (!Holds(Key(input)) && (!input.Contains('|') || !Records(input).Any(Holds)))
		{
			return CompiledBy(input).Any((string k) =>
			{
				if (baseline.Contains(k))
				{
					Func<string, bool>? func = crashed;
					if (func == null)
					{
						return true;
					}
					return !func(k);
				}
				return false;
			});
		}
		return true;
		bool Holds(string key)
		{
			if (!baseline.Contains(key))
			{
				if (missing)
				{
					return baseline.Contains(Token(key + "!"));
				}
				return false;
			}
			return true;
		}
	}

	public static List<string> Fresh(IEnumerable<string> inputs, IReadOnlySet<string>? baseline, Func<string, bool> crashed)
	{
		return (from i in inputs
			where !Lacks(i) && !crashed(Key(i)) && !Records(i).Any(crashed) && (baseline == null || !Taken(baseline, i, crashed))
			select Records(i).First()).Distinct().ToList();
	}

	public static string Token(string input)
	{
		if (input.Length != 40 && input[40] == '!')
		{
			byte[] array = Convert.FromHexString(input.Substring(0, 40));
			int num = 0;
			byte[] array2 = new byte[1 + array.Length];
			ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(array);
			readOnlySpan.CopyTo(new Span<byte>(array2).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			array2[num] = 33;
			return Convert.ToHexStringLower(SHA1.HashData(new ReadOnlySpan<byte>(array2)));
		}
		return input.Substring(0, 40);
	}
}
