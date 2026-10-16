using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Dagor;

namespace SCSKiller.Core.Planning;

public static class RootSig
{
	public sealed record Desc(uint Flags, List<uint[]> Rows, bool AppendRanges = false, bool Version10 = false, bool RangeOffsets = false)
	{
		public string Key => $"{Flags}|{string.Join(';', Rows.Select((uint[] r) => string.Join(',', r)))}{(AppendRanges ? "|append" : "")}{(Version10 ? "|1.0" : "")}{(RangeOffsets ? "|offsets" : "")}";
	}

	public sealed record Ranges(uint Flags, (uint Vis, uint Type, uint Base, uint Count, uint Space, bool Table)[] Slots);

	public sealed class SerializeException : InvalidOperationException
	{
		public SerializeException(string message)
			: base(message)
		{
		}
	}

	public static readonly byte[] Ue426Samplers = UeSamplers(0u, 1000u);

	internal const uint Unbounded = uint.MaxValue;

	private const uint Append = uint.MaxValue;

	private static readonly Stage[] Order = new Stage[7]
	{
		Stage.Pixel,
		Stage.Vertex,
		Stage.Mesh,
		Stage.Amplification,
		Stage.Geometry,
		Stage.Hull,
		Stage.Domain
	};

	private static readonly (Stage, uint)[] Deny = new (Stage, uint)[5]
	{
		(Stage.Vertex, 2u),
		(Stage.Geometry, 16u),
		(Stage.Pixel, 32u),
		(Stage.Amplification, 256u),
		(Stage.Mesh, 512u)
	};

	private static readonly ConditionalWeakTable<PlanRule, HashSet<int>> StageMasks = new ConditionalWeakTable<PlanRule, HashSet<int>>();

	private static readonly Stage[] ReOrder = new Stage[7]
	{
		Stage.Pixel,
		Stage.Vertex,
		Stage.Hull,
		Stage.Domain,
		Stage.Geometry,
		Stage.Amplification,
		Stage.Mesh
	};

	private unsafe static readonly delegate* unmanaged<void*, nint*, nint*, int> SerializeFn = (delegate* unmanaged<void*, nint*, nint*, int>)NativeLibrary.GetExport(NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "d3d12.dll")), "D3D12SerializeVersionedRootSignature");

	public static PlanRule? RuleFor(EngineInfo e)
	{
		return RulesFile.Current.RuleFor(e);
	}

	public static bool Verified(EngineInfo e)
	{
		return Verified(RuleFor(e), e);
	}

	internal static bool Verified(PlanRule? r, EngineInfo e)
	{
		if (r != null)
		{
			if (!r.Verified)
			{
				return ConfirmedEngines.Current.Contains(e);
			}
			return true;
		}
		return false;
	}

	public static byte[] StaticSamplers(PlanRule r)
	{
		uint[][] array = r.RootSignature?.Samplers;
		if (array == null)
		{
			SamplerSet samplerSet = r.RootSignature?.StaticSamplers;
			if ((object)samplerSet == null)
			{
				return Array.Empty<byte>();
			}
			return UeSamplers(samplerSet.Register, samplerSet.Space);
		}
		return MemoryMarshal.AsBytes(array.SelectMany((uint[] x) => x).ToArray().AsSpan()).ToArray();
	}

	internal static byte[] UeSamplers(uint reg, uint space)
	{
		List<uint> list = new List<uint>();
		uint[] array = new uint[3] { 0u, 20u, 21u };
		foreach (uint num in array)
		{
			uint[] array2 = new uint[2] { 1u, 3u };
			foreach (uint num2 in array2)
			{
				list.AddRange(new _003C_003Ez__ReadOnlyArray<uint>(new uint[13]
				{
					num,
					num2,
					num2,
					num2,
					0u,
					1u,
					1u,
					0u,
					0u,
					BitConverter.SingleToUInt32Bits(3.4028235E+38f),
					reg++,
					space,
					0u
				}));
			}
		}
		return MemoryMarshal.AsBytes(list.ToArray().AsSpan()).ToArray();
	}

	private static uint Vis(Stage s)
	{
		return s switch
		{
			Stage.Vertex => 1u, 
			Stage.Hull => 2u, 
			Stage.Domain => 3u, 
			Stage.Geometry => 4u, 
			Stage.Pixel => 5u, 
			Stage.Amplification => 6u, 
			Stage.Mesh => 7u, 
			_ => 0u, 
		};
	}

	private static uint DenyBit(Stage s)
	{
		return s switch
		{
			Stage.Vertex => 2u, 
			Stage.Hull => 4u, 
			Stage.Domain => 8u, 
			Stage.Geometry => 16u, 
			Stage.Pixel => 32u, 
			Stage.Amplification => 256u, 
			_ => 512u, 
		};
	}

	public static Desc Build(PlanRule r, IReadOnlyDictionary<Stage, ShaderInfo> stages, bool meshTier, uint maxSrvs = 0u)
	{
		RootSigRule rootSignature = r.RootSignature;
		StagesDesc[] byStages = rootSignature.ByStages;
		if (byStages != null)
		{
			return ByStages(r, byStages, stages);
		}
		ReRule re = rootSignature.Re;
		if ((object)re != null)
		{
			return BuildRe(re, stages);
		}
		if (Selects(r, "dagor"))
		{
			return DagorRootSig.Build(stages);
		}
		if (Selects(r, "dagorCbvRanges"))
		{
			return DagorRootSig.Build(stages, cbvRanges: true);
		}
		if (!Selects(r, "ff7"))
		{
			return BuildStock(r, rootSignature.Stock, stages, meshTier, maxSrvs);
		}
		return BuildUe(stages);
	}

	private static bool Selects(PlanRule r, string shape)
	{
		RootSigRule? rootSignature = r.RootSignature;
		if ((object)rootSignature == null)
		{
			return false;
		}
		string[]? array = rootSignature.Select;
		return ((array != null) ? new bool?(array.Contains(shape)) : ((bool?)null)) == true;
	}

	public static bool InStageSets(PlanRule r, IEnumerable<Stage> stages)
	{
		if (r.StageSets != null)
		{
			return StageMasks.GetValue(r, (PlanRule x) =>
			{
				HashSet<int> hashSet = new HashSet<int>();
				foreach (int item in x.StageSets.Select((string[] set) => Mask(set.Select(RulesFile.StageOf))))
				{
					hashSet.Add(item);
				}
				return hashSet;
			}).Contains(Mask(stages));
		}
		return true;
	}

	private static int Mask(IEnumerable<Stage> stages)
	{
		return stages.Aggregate(0, (int m, Stage s) => m | (1 << (int)s));
	}

	private static Desc ByStages(PlanRule r, StagesDesc[] by, IReadOnlyDictionary<Stage, ShaderInfo> stages)
	{
		if (!InStageSets(r, stages.Keys))
		{
			throw new SerializeException("no " + r.Id + " root signature confirmed for " + string.Join('+', stages.Keys));
		}
		return by.FirstOrDefault((StagesDesc b) => (b.Any == null || b.Any.Any((string n) => stages.ContainsKey(RulesFile.StageOf(n)))) && (!b.BindsNothing || stages.Values.All((ShaderInfo s) => s.Bindings.Count == 0)) && (!b.Dxbc || stages.Values.Any((ShaderInfo s) =>
		{
			string shaderModel = s.ShaderModel;
			return shaderModel[shaderModel.Length - 3] < '6';
		})))?.Desc?.ToDesc() ?? throw new SerializeException("no " + r.Id + " root signature for " + string.Join('+', stages.Keys));
	}

	public static Desc BuildRe(ReRule re, IReadOnlyDictionary<Stage, ShaderInfo> stages)
	{
		if (stages.ContainsKey(Stage.Compute))
		{
			return BuildRe(re, new _003C_003Ez__ReadOnlySingleElementList<Stage>(Stage.Compute), re.CbvSizes[^1], re.SrvSizes[^1], 0u);
		}
		int cbvUavAbove = (re.CbvUavAbove ? 1 : 0);
		ShaderInfo value;
		return BuildRe(re, stages.Keys, Size(re.CbvSizes, End(stages.Values, "cbv") - re.CbvBase + cbvUavAbove, "CBV"), Size(re.SrvSizes, End(stages.Values, "srv") + 1, "SRV"), Size(re.UavSizes, (stages.TryGetValue(Stage.Pixel, out value) ? End(new _003C_003Ez__ReadOnlySingleElementList<ShaderInfo>(value), "uav") : 0) + cbvUavAbove, "UAV"));
		static long End(IEnumerable<ShaderInfo> sh, string cls)
		{
			return (from b in sh.SelectMany((ShaderInfo s) => s.Bindings)
				where b.Class == cls && b.Space == 0
				select (b.Count >= 0) ? ((long)b.Lower + (long)b.Count) : 4611686018427387903L).DefaultIfEmpty(0L).Max();
		}
		static uint Size(uint[] sizes, long need, string what)
		{
			uint num = sizes.FirstOrDefault((uint s) => s >= need);
			if (num == 0)
			{
				throw new SerializeException("no " + what + " table size holds " + ((need > 1000000) ? "an unbounded range" : $"{need} registers"));
			}
			return num;
		}
	}

	internal static Desc BuildRe(ReRule re, IEnumerable<Stage> stages, uint cbv, uint srv, uint uav)
	{
		List<uint[]> rows = new List<uint[]> { new uint[5] { 1u, 0u, 0u, 32u, 1u } };
		uint num = (re.HeapIndexed ? 1024u : 0u);
		HashSet<Stage> set = stages.ToHashSet();
		if (set.Contains(Stage.Compute))
		{
			rows.AddRange(new _003C_003Ez__ReadOnlyArray<uint[]>(new uint[4][]
			{
				new uint[7] { 0u, 0u, 2u, cbv, re.CbvBase, 0u, 0u },
				new uint[7] { 0u, 0u, 0u, srv, 0u, 0u, 0u },
				new uint[7] { 0u, 0u, 1u, re.ComputeUavs, 0u, 0u, 0u },
				new uint[7] { 0u, 0u, 3u, 16u, 0u, 0u, 0u }
			}));
			for (uint num2 = 0u; num2 < re.CbvBase; num2++)
			{
				rows.Add(new uint[5] { 2u, 0u, num2, 0u, 0u });
			}
			AddBindless();
			return new Desc(num | (uint)(re.DenyMesh ? 830 : 62), rows, AppendRanges: true, Version10: true);
		}
		foreach (Stage item in ReOrder.Where(set.Contains))
		{
			rows.Add((item != Stage.Pixel) ? new uint[12]
			{
				0u,
				Vis(item),
				2u,
				cbv,
				re.CbvBase,
				0u,
				0u,
				0u,
				srv,
				0u,
				0u,
				0u
			} : new uint[17]
			{
				0u,
				Vis(item),
				2u,
				cbv,
				re.CbvBase,
				0u,
				0u,
				0u,
				srv,
				0u,
				0u,
				0u,
				1u,
				uav,
				0u,
				0u,
				0u
			});
			rows.Add(new uint[7]
			{
				0u,
				Vis(item),
				3u,
				16u,
				0u,
				0u,
				0u
			});
			for (uint num3 = 0u; num3 < re.CbvBase; num3++)
			{
				rows.Add(new uint[5]
				{
					2u,
					Vis(item),
					num3,
					0u,
					0u
				});
			}
		}
		AddBindless();
		if (set.Contains(Stage.Vertex))
		{
			num |= 1;
		}
		foreach (Stage item2 in ReOrder.Where((Stage s) =>
		{
			bool flag = !set.Contains(s);
			if (flag)
			{
				bool flag2 = re.DenyMesh;
				if (!flag2)
				{
					bool flag3 = (uint)(s - 24) <= 1u;
					flag2 = !flag3;
				}
				flag = flag2;
			}
			return flag;
		}))
		{
			num |= DenyBit(item2);
		}
		return new Desc(num, rows, AppendRanges: true, Version10: true);
		void AddBindless()
		{
			for (uint num4 = 1u; num4 <= re.BindlessSpaces; num4++)
			{
				rows.Add(new uint[8] { 0u, 0u, 0u, 4294967295u, 0u, num4, 0u, 0u });
			}
		}
	}

	private static Desc BuildStock(PlanRule r, StockRule p, IReadOnlyDictionary<Stage, ShaderInfo> stages, bool meshTier, uint maxSrvs)
	{
		Stage[] array = p.Stages.Select(RulesFile.StageOf).ToArray();
		Stage[] array2 = ((!stages.ContainsKey(Stage.Compute)) ? array.Where(stages.ContainsKey).ToArray() : new Stage[1] { Stage.Compute });
		Dictionary<Stage, (uint, uint, uint, uint)> dictionary = array2.ToDictionary((Stage s) => s, (Stage s) => Quantize(p, s, stages[s].Counts, maxSrvs));
		bool flag = Selects(r, "spaceBindless");
		bool flag2 = Selects(r, "bindless");
		bool flag3 = flag;
		List<uint[]> list = new List<uint[]>();
		Stage[] array3 = array2;
		foreach (Stage stage in array3)
		{
			var (num2, num3, num4, _) = dictionary[stage];
			if (num2 != 0)
			{
				list.Add(new uint[7]
				{
					0u,
					Vis(stage),
					0u,
					num2,
					0u,
					0u,
					5u
				});
			}
			if (flag3)
			{
				list.AddRange(SpaceBindlessTables(stage, stages[stage]));
			}
			if (num3 != 0)
			{
				list.Add(new uint[7]
				{
					0u,
					Vis(stage),
					3u,
					num3,
					0u,
					0u,
					1u
				});
			}
			if (num4 != 0)
			{
				list.Add(new uint[7]
				{
					0u,
					Vis(stage),
					1u,
					num4,
					0u,
					0u,
					3u
				});
			}
			if (flag2)
			{
				list.AddRange(BindlessTables(stage, stages[stage]));
			}
		}
		array3 = array2;
		foreach (Stage stage2 in array3)
		{
			for (uint num5 = 0u; num5 < dictionary[stage2].Item4; num5++)
			{
				list.Add(new uint[5]
				{
					2u,
					Vis(stage2),
					num5,
					0u,
					8u
				});
			}
		}
		int num6 = stages.Values.Aggregate(0, (int f, ShaderInfo i) => f | i.Counts.Flags);
		uint num7 = (stages.TryGetValue(Stage.Vertex, out ShaderInfo value) ? ((!p.LayoutNeedsVertexInput || value.Inputs.Any((SigElement i) => i.SysValue == 0)) ? 1u : 0u) : 0u);
		if (p.Ags && (num6 & 0x10) != 0)
		{
			list.Add(new uint[5] { 4u, 0u, 0u, 2147420894u, 2u });
		}
		if (p.RootConstants && (num6 & 4) != 0)
		{
			list.Add(new uint[5] { 1u, 0u, 0u, 3u, 4u });
		}
		if (p.NvExtension && (num6 & 0x20) != 0)
		{
			list.Add(new uint[8] { 0u, 0u, 1u, 1u, 0u, 1001u, 0u, 0u });
		}
		if (p.Diagnostic && (num6 & 8) != 0)
		{
			list.Add(new uint[5] { 4u, 0u, 0u, 999u, 2u });
		}
		if (p.HeapFlags && (num6 & 1) != 0)
		{
			num7 |= 0x400;
		}
		if (p.HeapFlags && (num6 & 2) != 0)
		{
			num7 |= 0x800;
		}
		array3 = array;
		foreach (Stage stage3 in array3)
		{
			int num8;
			if (dictionary.TryGetValue(stage3, out var value2))
			{
				(uint, uint, uint, uint) tuple2 = value2;
				num8 = ((tuple2.Item1 == 0 && tuple2.Item2 == 0 && tuple2.Item3 == 0 && tuple2.Item4 == 0) ? 1 : 0);
			}
			else
			{
				num8 = 1;
			}
			bool flag4 = (byte)num8 != 0;
			if (flag4)
			{
				bool flag5 = meshTier;
				if (!flag5)
				{
					bool flag6 = (uint)(stage3 - 24) <= 1u;
					flag5 = !flag6;
				}
				flag4 = flag5;
			}
			if (flag4)
			{
				num7 |= DenyBit(stage3);
			}
		}
		if (p.DenyMesh)
		{
			num7 |= DenyBit(Stage.Mesh) | DenyBit(Stage.Amplification);
		}
		return new Desc(num7, list);
	}

	private static IEnumerable<uint[]> BindlessTables(Stage s, ShaderInfo sh)
	{
		return (from b in sh.Bindings
			where b.Class == "srv" && b.Count == -1 && b.Space >= 10
			select (uint)b.Space / 10u).Distinct().Order().Select((uint g) =>
		{
			List<uint> list = new List<uint>();
			list.Add(0u);
			list.Add(Vis(s));
			list.AddRange(Enumerable.Range(0, 5).SelectMany((int k) => new uint[5]
			{
				0u,
				4294967295u,
				0u,
				10 * g + (uint)k,
				5u
			}));
			return list.ToArray();
		});
	}

	private static IEnumerable<uint[]> SpaceBindlessTables(Stage s, ShaderInfo sh)
	{
		int top = (from b in sh.Bindings.Where(IsSpaceBindless)
			select b.Space).DefaultIfEmpty(3).Max();
		for (uint k = 4u; k <= top; k++)
		{
			yield return new uint[7]
			{
				0u,
				Vis(s),
				0u,
				4294967295u,
				0u,
				k,
				5u
			};
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

	public static uint MaxSrvsFor(PlanRule r, IEnumerable<ShaderInfo> shaders)
	{
		if (Selects(r, "avalancheSrvs") && shaders.Any((ShaderInfo s) => s.Bindings.Any(IsSpaceBindless)))
		{
			return 128u;
		}
		StockRule stockRule = r.RootSignature?.Stock;
		if ((object)stockRule == null || !stockRule.GrowSrvs)
		{
			return 0u;
		}
		int num = shaders.Select((ShaderInfo s) => s.Counts.Srv).DefaultIfEmpty(0).Max();
		if (num > stockRule.MaxSrvs)
		{
			if (num > 48)
			{
				return 64u;
			}
			return 48u;
		}
		return 0u;
	}

	private static (uint Srv, uint Sampler, uint Uav, uint Cb) Quantize(StockRule p, Stage s, ResourceCounts c, uint maxSrvs)
	{
		bool flag = p.UavStages.Any((string n) => RulesFile.StageOf(n) == s);
		uint num = ((maxSrvs != 0) ? maxSrvs : p.MaxSrvs);
		uint maxSamplers = p.MaxSamplers;
		uint item = (uint)Math.Min(c.Cb, 16);
		if (p.TablesWhenUsed)
		{
			return (Srv: (c.Srv > 0) ? num : 0u, Sampler: (c.Sampler > 0) ? maxSamplers : 0u, Uav: ((c.Uav > 0) & flag) ? 16u : 0u, Cb: item);
		}
		return (Srv: num, Sampler: maxSamplers, Uav: flag ? 16u : 0u, Cb: item);
	}

	public static Desc BuildUe(IReadOnlyDictionary<Stage, ShaderInfo> stages)
	{
		Stage[] array = ((!stages.ContainsKey(Stage.Compute)) ? Order.Where(stages.ContainsKey).ToArray() : new Stage[1] { Stage.Compute });
		List<uint[]> list = new List<uint[]>();
		Stage[] array2 = array;
		foreach (Stage stage in array2)
		{
			ResourceCounts counts = stages[stage].Counts;
			if (counts.Srv > 0)
			{
				list.Add(new uint[7]
				{
					0u,
					Vis(stage),
					0u,
					64u,
					0u,
					0u,
					5u
				});
			}
			if (counts.Sampler > 0)
			{
				list.Add(new uint[7]
				{
					0u,
					Vis(stage),
					3u,
					(uint)counts.Sampler,
					0u,
					0u,
					1u
				});
			}
			if (counts.Uav > 0)
			{
				uint[] array3 = new uint[7] { 0u, 0u, 1u, 16u, 0u, 0u, 3u };
				array3[1] = Vis(stage);
				list.Add(array3);
			}
		}
		array2 = array;
		foreach (Stage stage2 in array2)
		{
			for (uint num = 0u; num < stages[stage2].Counts.Cb; num++)
			{
				list.Add(new uint[5]
				{
					2u,
					Vis(stage2),
					num,
					0u,
					8u
				});
			}
		}
		array2 = array;
		foreach (Stage stage3 in array2)
		{
			foreach (Binding item in stages[stage3].Bindings.Where((Binding b) => b.Class == "srv" && b.Count == -1))
			{
				list.Add(new uint[7]
				{
					0u,
					Vis(stage3),
					0u,
					4294967295u,
					(uint)item.Lower,
					(uint)item.Space,
					5u
				});
			}
		}
		uint num2 = (stages.ContainsKey(Stage.Vertex) ? 1u : 0u);
		(Stage, uint)[] deny = Deny;
		for (int i = 0; i < deny.Length; i++)
		{
			var (stage4, num3) = deny[i];
			if (array.Contains(stage4, null))
			{
				ResourceCounts counts2 = stages[stage4].Counts;
				if ((object)counts2 == null || counts2.Cb != 0 || counts2.Srv != 0 || counts2.Uav != 0 || counts2.Sampler != 0)
				{
					continue;
				}
			}
			num2 |= num3;
		}
		return new Desc(num2, list);
	}

	public static Ranges Parse(byte[] blob)
	{
		byte[] b = Rts0(blob);
		uint num = U(0u);
		List<(uint, uint, uint, uint, uint, bool)> list = new List<(uint, uint, uint, uint, uint, bool)>();
		uint num2 = num;
		for (uint num3 = 0u; num3 < U(4u); num3++)
		{
			uint num4 = U(U(8u) + 12 * num3);
			uint num5 = U(U(8u) + 12 * num3 + 4);
			uint num6 = U(U(8u) + 12 * num3 + 8);
			uint num7 = num5;
			uint num8 = num4;
			if (num8 == 0)
			{
				for (uint num9 = 0u; num9 < U(num6); num9++)
				{
					uint num10 = U(num6 + 4) + (uint)(((num2 >= 2) ? 24 : 20) * (int)num9);
					list.Add((num7, U(num10), U(num10 + 8), U(num10 + 4), U(num10 + 12), true));
				}
			}
			else
			{
				List<(uint, uint, uint, uint, uint, bool)> list2 = list;
				uint item = num7;
				list2.Add((item, num8 switch
				{
					3u => 0u, 
					4u => 1u, 
					_ => 2u, 
				}, U(num6), 1u, U(num6 + 4), false));
			}
		}
		for (uint num11 = 0u; num11 < U(12u); num11++)
		{
			uint num12 = U(16u) + SamplerSize(num2) * num11;
			list.Add((U(num12 + 48), 3u, U(num12 + 40), 1u, U(num12 + 44), false));
		}
		return new Ranges(U(20u), list.ToArray());
		uint U(uint o)
		{
			return BitConverter.ToUInt32(b, (int)o);
		}
	}

	public static string? Uncovered(Ranges rs, Stage stage, ShaderInfo sh)
	{
		uint num = stage switch
		{
			Stage.Vertex => 2u, 
			Stage.Hull => 4u, 
			Stage.Domain => 8u, 
			Stage.Geometry => 16u, 
			Stage.Pixel => 32u, 
			Stage.Amplification => 256u, 
			Stage.Mesh => 512u, 
			_ => 0u, 
		};
		if (sh.Bindings.Count > 0 && (rs.Flags & num) != 0)
		{
			return $"{stage} denied";
		}
		foreach (Binding b in sh.Bindings)
		{
			uint type = b.Class switch
			{
				"srv" => 0u, 
				"uav" => 1u, 
				"cbv" => 2u, 
				"sampler" => 3u, 
				_ => 9u, 
			};
			if (type != 9 && !rs.Slots.Any(((uint Vis, uint Type, uint Base, uint Count, uint Space, bool Table) s) => (s.Vis == 0 || s.Vis == Vis(stage)) && s.Type == type && s.Space == (uint)b.Space && s.Base <= (uint)b.Lower && (s.Count == uint.MaxValue || ((b.Count < 0) ? (s.Table && (ulong)b.Lower < (ulong)((long)s.Base + (long)s.Count)) : ((ulong)((long)b.Lower + (long)b.Count) <= (ulong)((long)s.Base + (long)s.Count))))))
			{
				return $"{stage} {b.Class} space {b.Space}{((b.Count < 0) ? " unbounded" : "")}";
			}
		}
		return null;
	}

	public static byte[] Samplers(byte[] blob)
	{
		byte[] b = Rts0(blob);
		uint num = BitConverter.ToUInt32(b, 0);
		int num2 = BitConverter.ToInt32(b, 12);
		int num3 = BitConverter.ToInt32(b, 16);
		int off = num3;
		int count = num2;
		uint version = num;
		int size = (int)SamplerSize(version);
		return Enumerable.Range(0, count).SelectMany((int i) => b[(off + size * i)..(off + size * i + 52)]).ToArray();
	}

	internal static byte[]? WithoutSamplerSettings(byte[] blob)
	{
		byte[] array = Rts0(blob).ToArray();
		uint num = BitConverter.ToUInt32(array, 0);
		int num2 = BitConverter.ToInt32(array, 12);
		int num3 = BitConverter.ToInt32(array, 16);
		int num4 = num2;
		uint version = num;
		if (num4 == 0)
		{
			return null;
		}
		int num5 = (int)SamplerSize(version);
		for (int i = 0; i < num4; i++)
		{
			array.AsSpan(num3 + num5 * i, 40).Clear();
		}
		return array;
	}

	private static uint SamplerSize(uint version)
	{
		if (version != 3)
		{
			return 52u;
		}
		return 56u;
	}

	internal static byte[] Rts0(byte[] blob)
	{
		if (!((ReadOnlySpan<byte>)blob.AsSpan(0, 4)).SequenceEqual("DXBC"u8))
		{
			return blob;
		}
		ReadOnlySpan<byte> readOnlySpan = Dxbc.Part(blob, "RTS0"u8);
		if (readOnlySpan.IsEmpty)
		{
			throw new InvalidDataException("no RTS0 part");
		}
		return readOnlySpan.ToArray();
	}

	public unsafe static byte[] Serialize(Desc d, byte[] samplers)
	{
		int count = d.Rows.Count;
		int num = (d.Version10 ? 5 : 6);
		uint[] array = new uint[Math.Max(1, d.Rows.Where((uint[] r) => r[0] == 0).Sum((uint[] r) => (r.Length - 2) / 5)) * num];
		byte[] array2 = new byte[Math.Max(1, count) * 32];
		byte[] array3 = new byte[48];
		byte[] array4 = ((samplers.Length != 0) ? samplers : new byte[1]);
		fixed (uint* ptr = array)
		{
			fixed (byte* ptr2 = array2)
			{
				fixed (byte* ptr3 = array3)
				{
					fixed (byte* ptr4 = array4)
					{
						uint* ptr5 = ptr;
						for (int num2 = 0; num2 < count; num2++)
						{
							uint[] array5 = d.Rows[num2];
							byte* ptr6 = ptr2 + 32 * num2;
							*(uint*)ptr6 = array5[0];
							((int*)ptr6)[6] = (int)array5[1];
							if (array5[0] == 0)
							{
								int num3 = (((int*)ptr6)[2] = (array5.Length - 2) / 5);
								*(uint**)(ptr6 + 16) = ptr5;
								int num4 = 0;
								while (num4 < num3)
								{
									for (int num5 = 0; num5 < num - 1; num5++)
									{
										ptr5[num5] = array5[2 + 5 * num4 + num5];
									}
									uint* intPtr = ptr5 + (num - 1);
									int num6;
									if (d.RangeOffsets)
									{
										num6 = (int)array5[2 + 5 * num4 + 4];
									}
									else if (num3 == 1)
									{
										num6 = ((array5.Length == 8) ? ((int)array5[7]) : (-1));
									}
									else
									{
										num6 = (d.AppendRanges ? (-1) : 0);
									}
									*intPtr = (uint)num6;
									num4++;
									ptr5 += num;
								}
							}
							else
							{
								for (int num7 = 0; num7 < ((d.Version10 && array5[0] != 1) ? 2 : 3); num7++)
								{
									((int*)(ptr6 + 8))[num7] = (int)array5[2 + num7];
								}
							}
						}
						*(int*)ptr3 = (d.Version10 ? 1 : 2);
						((int*)ptr3)[2] = count;
						*(byte**)(ptr3 + 16) = ptr2;
						((int*)ptr3)[6] = samplers.Length / 52;
						*(byte**)(ptr3 + 32) = ptr4;
						((int*)ptr3)[10] = (int)d.Flags;
						return Call(ptr3);
					}
				}
			}
		}
	}

	public unsafe static byte[] AsVersion10(byte[] blob)
	{
		byte[] b = Rts0(blob);
		uint num = U(0u);
		uint num2 = U(4u);
		uint num3 = U(8u);
		uint num4 = U(12u);
		uint num5 = U(16u);
		uint num6 = num4;
		uint num7 = num3;
		uint num8 = num2;
		uint num9 = num;
		uint num10 = ((num9 >= 2) ? 24u : 20u);
		List<uint> list = new List<uint>();
		int[] array = new int[num8];
		for (uint num11 = 0u; num11 < num8; num11++)
		{
			array[num11] = list.Count;
			if (U(num7 + 12 * num11) == 0)
			{
				uint num12 = U(num7 + 12 * num11 + 8);
				uint num13 = 0u;
				for (uint num14 = 0u; num14 < U(num12); num14++)
				{
					uint num15 = U(num12 + 4) + num10 * num14;
					uint num16 = U(num15 + 4);
					uint num17 = U(num15 + num10 - 4);
					uint num18 = ((num17 == uint.MaxValue) ? num13 : num17);
					list.AddRange(new _003C_003Ez__ReadOnlyArray<uint>(new uint[5]
					{
						U(num15),
						num16,
						U(num15 + 8),
						U(num15 + 12),
						num18
					}));
					num13 = ((num16 == uint.MaxValue || num18 == uint.MaxValue) ? uint.MaxValue : (num18 + num16));
				}
			}
		}
		byte[] array2 = new byte[Math.Max(1u, num6) * 52];
		for (uint num19 = 0u; num19 < num6; num19++)
		{
			int num20 = (int)(num5 + SamplerSize(num9) * num19);
			if (num9 == 3 && BitConverter.ToUInt32(b, num20 + 52) != 0)
			{
				throw new SerializeException("a 1.2 static sampler with flags has no 1.0 form");
			}
			b.AsSpan(num20, 52).CopyTo(array2.AsSpan((int)(52 * num19)));
		}
		uint[] obj = ((list.Count > 0) ? list.ToArray() : new uint[1]);
		byte[] array3 = new byte[Math.Max(1u, num8) * 32];
		byte[] array4 = new byte[48];
		fixed (uint* ptr = obj)
		{
			fixed (byte* ptr2 = array3)
			{
				fixed (byte* ptr3 = array4)
				{
					fixed (byte* ptr4 = array2)
					{
						for (uint num21 = 0u; num21 < num8; num21++)
						{
							uint num22 = U(num7 + 12 * num21);
							uint num23 = U(num7 + 12 * num21 + 8);
							uint num24 = num22;
							byte* ptr5 = ptr2 + 32 * num21;
							*(uint*)ptr5 = num24;
							((int*)ptr5)[6] = (int)U(num7 + 12 * num21 + 4);
							if (num24 == 0)
							{
								((int*)ptr5)[2] = (int)U(num23);
								*(uint**)(ptr5 + 16) = ptr + array[num21];
								continue;
							}
							for (uint num25 = 0u; num25 < (uint)((num24 == 1) ? 3 : 2); num25++)
							{
								((int*)(ptr5 + 8))[num25] = (int)U(num23 + 4 * num25);
							}
						}
						*(int*)ptr3 = 1;
						((int*)ptr3)[2] = (int)num8;
						*(byte**)(ptr3 + 16) = ptr2;
						((int*)ptr3)[6] = (int)num6;
						*(byte**)(ptr3 + 32) = ptr4;
						((int*)ptr3)[10] = (int)U(20u);
						return Call(ptr3);
					}
				}
			}
		}
		uint U(uint o)
		{
			return BitConverter.ToUInt32(b, (int)o);
		}
	}

	private unsafe static byte[] Call(byte* desc)
	{
		nint num = 0;
		nint num2 = 0;
		int num3 = SerializeFn(desc, &num, &num2);
		if (num2 != 0)
		{
			Release(num2);
		}
		if (num3 < 0)
		{
			throw new SerializeException($"D3D12SerializeVersionedRootSignature failed 0x{num3:x8}");
		}
		nint* ptr = *(nint**)num;
		byte[] result = new ReadOnlySpan<byte>(((delegate* unmanaged<nint, void*>)ptr[3])(num), (int)((delegate* unmanaged<nint, nuint>)ptr[4])(num)).ToArray();
		Release(num);
		return result;
		unsafe static void Release(nint unk)
		{
			((delegate* unmanaged<nint, uint>)(*(IntPtr*)((nint)(*(IntPtr*)unk) + (nint)2 * (nint)sizeof(nint))))(unk);
		}
	}
}
