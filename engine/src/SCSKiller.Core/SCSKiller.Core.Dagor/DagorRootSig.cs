using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Dagor;

public static class DagorRootSig
{
	private readonly record struct Usage(uint T, uint S, uint Bindless, uint B, uint U, uint RootConstants, uint Special, uint InOut, uint Features)
	{
		public bool Any => (T | S | Bindless | B | U | RootConstants | Special) != 0;

		public static Usage Of(ShaderInfo? s)
		{
			byte[] h = s?.EngineHeader;
			if (h == null || h.Length < 96)
			{
				return default;
			}
			return new Usage(U(8), U(12), U(16), U16(20), U16(22), h[24], h[25], U(32), U(84));
			uint U(int at)
			{
				return BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(at));
			}
			uint U16(int at)
			{
				return BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(at));
			}
		}
	}

	public const int HeaderSize = 96;

	private static readonly (Stage Stage, uint Vis, uint Deny)[] Graphics = new (Stage, uint, uint)[5]
	{
		(Stage.Pixel, 5u, 32u),
		(Stage.Vertex, 1u, 2u),
		(Stage.Hull, 2u, 4u),
		(Stage.Domain, 3u, 8u),
		(Stage.Geometry, 4u, 16u)
	};

	private const uint Srv = 0u;

	private const uint Uav = 1u;

	private const uint Cbv = 2u;

	private const uint Sampler = 3u;

	private const uint Unbounded = uint.MaxValue;

	private const uint DrawId = 1u;

	private const uint NvidiaExtension = 2u;

	private const uint AmdExtension = 4u;

	private const uint ResourceHeapIndexing = 33554432u;

	private const uint SamplerHeapIndexing = 67108864u;

	public static RootSig.Desc Build(IReadOnlyDictionary<Stage, ShaderInfo> stages, bool cbvRanges = false)
	{
		if (stages.ContainsKey(Stage.Mesh) || stages.ContainsKey(Stage.Amplification))
		{
			throw new RootSig.SerializeException("Dagor mesh pipelines aren't rebuilt");
		}
		bool compute = stages.TryGetValue(Stage.Compute, out ShaderInfo value);
		(uint Vis, Usage)[] used = ((!compute) ? Graphics.Select(((Stage Stage, uint Vis, uint Deny) g) => (Vis: g.Vis, Usage.Of(stages.GetValueOrDefault(g.Stage)))).ToArray() : new (uint, Usage)[1] { (0u, Usage.Of(value)) });
		uint num = used.Aggregate(0u, (uint f, (uint Vis, Usage) x) => f | x.Item2.Features);
		uint num2 = (uint)((((num & 0x2000000) != 0) ? 1024 : 0) | (((num & 0x4000000) != 0) ? 2048 : 0));
		if (!compute)
		{
			if (Usage.Of(stages.GetValueOrDefault(Stage.Vertex)).InOut != 0)
			{
				num2 |= 1;
			}
			for (int num3 = 0; num3 < Graphics.Length; num3++)
			{
				if (!used[num3].Item2.Any)
				{
					num2 |= Graphics[num3].Deny;
				}
			}
		}
		List<uint[]> list = new List<uint[]>();
		uint num4 = used.Aggregate(0u, (uint m, (uint Vis, Usage) x) => m | x.Item2.Special);
		if ((num4 & 1) != 0)
		{
			list.Add(new uint[5] { 1u, 0u, 7u, 1u, 1u });
		}
		(uint, Usage)[] array = used;
		for (int num5 = 0; num5 < array.Length; num5++)
		{
			var (num6, usage) = array[num5];
			if (usage.RootConstants != 0)
			{
				list.Add(new uint[5]
				{
					1u,
					num6,
					8u,
					1 + usage.RootConstants,
					usage.RootConstants
				});
			}
		}
		for (int num7 = 0; num7 < used.Length; num7++)
		{
			if (!cbvRanges)
			{
				for (uint num8 = 0u; num8 < 14; num8++)
				{
					if (((used[num7].Item2.B >> (int)num8) & 1) != 0)
					{
						list.Add(new uint[5]
						{
							2u,
							used[num7].Vis,
							num8,
							0u,
							0u
						});
					}
				}
				continue;
			}
			List<uint> list2 = Singles(2u, used[num7].Item2.B, Combo(num7, (Usage x) => x.B));
			if (list2 != null && list2.Count > 0)
			{
				uint num9 = 0u;
				uint item = used[num7].Vis;
				List<uint> list3 = list2;
				int num5 = 0;
				uint[] array2 = new uint[2 + list3.Count];
				array2[num5] = num9;
				num5++;
				array2[num5] = item;
				num5++;
				Span<uint> span = CollectionsMarshal.AsSpan(list3);
				span.CopyTo(new Span<uint>(array2).Slice(num5, span.Length));
				num5 += span.Length;
				list.Add(array2);
			}
		}
		for (int num10 = 0; num10 < used.Length; num10++)
		{
			List<uint> list4 = Singles(3u, used[num10].Item2.S, Combo(num10, (Usage x) => x.S));
			if (list4 != null && list4.Count > 0)
			{
				uint item = 0u;
				uint num9 = used[num10].Vis;
				List<uint> list3 = list4;
				int num5 = 0;
				uint[] array2 = new uint[2 + list3.Count];
				array2[num5] = item;
				num5++;
				array2[num5] = num9;
				num5++;
				Span<uint> span = CollectionsMarshal.AsSpan(list3);
				span.CopyTo(new Span<uint>(array2).Slice(num5, span.Length));
				num5 += span.Length;
				list.Add(array2);
			}
		}
		Bindless(list, used, 3u, (Usage u) => u.Bindless & 3, 2);
		for (int num11 = 0; num11 < used.Length; num11++)
		{
			List<uint> list5 = Runs(0u, used[num11].Item2.T, Combo(num11, (Usage x) => x.T));
			if (list5 != null && list5.Count > 0)
			{
				uint num9 = 0u;
				uint item = used[num11].Vis;
				List<uint> list3 = list5;
				int num5 = 0;
				uint[] array2 = new uint[2 + list3.Count];
				array2[num5] = num9;
				num5++;
				array2[num5] = item;
				num5++;
				Span<uint> span = CollectionsMarshal.AsSpan(list3);
				span.CopyTo(new Span<uint>(array2).Slice(num5, span.Length));
				num5 += span.Length;
				list.Add(array2);
			}
		}
		Bindless(list, used, 0u, (Usage u) => (u.Bindless >> 2) & 0x3FFFFFFF, 30);
		for (int num12 = 0; num12 < used.Length; num12++)
		{
			List<uint> list6 = Runs(1u, used[num12].Item2.U, Combo(num12, (Usage x) => x.U));
			if (list6 != null && list6.Count > 0)
			{
				uint item = 0u;
				uint num9 = used[num12].Vis;
				List<uint> list3 = list6;
				int num5 = 0;
				uint[] array2 = new uint[2 + list3.Count];
				array2[num5] = item;
				num5++;
				array2[num5] = num9;
				num5++;
				Span<uint> span = CollectionsMarshal.AsSpan(list3);
				span.CopyTo(new Span<uint>(array2).Slice(num5, span.Length));
				num5 += span.Length;
				list.Add(array2);
			}
		}
		if ((num4 & 2) != 0)
		{
			list.Add(new uint[7] { 0u, 0u, 1u, 1u, 0u, 99u, 0u });
		}
		else if ((num4 & 4) != 0)
		{
			list.Add(new uint[7] { 0u, 0u, 1u, 1u, 0u, 2147420894u, 0u });
		}
		return new RootSig.Desc(num2, list, AppendRanges: false, Version10: true, RangeOffsets: true);
		uint Combo(int i, Func<Usage, uint> mask)
		{
			if (!compute && i != 0)
			{
				return used.Skip(1).Aggregate(0u, (uint m, (uint Vis, Usage) x) => m | mask(x.Item2));
			}
			return mask(used[i].Item2);
		}
	}

	private static List<uint> Singles(uint type, uint mask, uint combo)
	{
		List<uint> list = new List<uint>();
		int i = 0;
		int num = 0;
		for (; i < 32 && mask >> i != 0; i++)
		{
			if (((mask >> i) & 1) != 0)
			{
				list.AddRange(new _003C_003Ez__ReadOnlyArray<uint>(new uint[5]
				{
					type,
					1u,
					(uint)i,
					0u,
					(uint)num
				}));
			}
			num += (int)((combo >> i) & 1);
		}
		return list;
	}

	private static List<uint> Runs(uint type, uint mask, uint combo)
	{
		List<uint> list = new List<uint>();
		int i = 0;
		int num = 0;
		for (; i < 32 && mask >> i != 0; i++)
		{
			if (((mask >> i) & 1) != 0 && (i == 0 || ((mask >> i - 1) & 1) == 0))
			{
				int j;
				for (j = 1; i + j < 32 && ((mask >> i + j) & 1) != 0; j++)
				{
				}
				list.AddRange(new _003C_003Ez__ReadOnlyArray<uint>(new uint[5]
				{
					type,
					(uint)j,
					(uint)i,
					0u,
					(uint)num
				}));
			}
			num += (int)((combo >> i) & 1);
		}
		return list;
	}

	private static void Bindless(List<uint[]> rows, (uint Vis, Usage U)[] used, uint type, Func<Usage, uint> bits, int count)
	{
		List<uint> list = new List<uint>();
		uint num = 0u;
		int num2 = 0;
		for (int i = 0; i < used.Length; i++)
		{
			(uint Vis, Usage U) tuple = used[i];
			uint item = tuple.Vis;
			Usage item2 = tuple.U;
			uint num3 = bits(item2);
			if (num3 == 0)
			{
				continue;
			}
			num = ((num2++ == 0) ? item : 0u);
			for (int j = 0; j < count; j++)
			{
				if (((num3 >> j) & 1) != 0 && !Contains(list, (uint)(1 + j)))
				{
					list.AddRange(new _003C_003Ez__ReadOnlyArray<uint>(new uint[5]
					{
						type,
						4294967295u,
						0u,
						(uint)(1 + j),
						0u
					}));
				}
			}
		}
		if (num2 > 0)
		{
			uint num4 = 0u;
			uint num5 = num;
			List<uint> list2 = list;
			int i = 0;
			uint[] array = new uint[2 + list2.Count];
			array[i] = num4;
			i++;
			array[i] = num5;
			i++;
			Span<uint> span = CollectionsMarshal.AsSpan(list2);
			span.CopyTo(new Span<uint>(array).Slice(i, span.Length));
			i += span.Length;
			rows.Add(array);
		}
		static bool Contains(List<uint> ranges, uint space)
		{
			for (int k = 3; k < ranges.Count; k += 5)
			{
				if (ranges[k] == space)
				{
					return true;
				}
			}
			return false;
		}
	}
}
