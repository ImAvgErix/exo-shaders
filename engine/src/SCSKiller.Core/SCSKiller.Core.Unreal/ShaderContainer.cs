using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Unreal;

public static class ShaderContainer
{
	public sealed class WideCounts
	{
		private readonly ConcurrentDictionary<string, ResourceCounts> wide = new ConcurrentDictionary<string, ResourceCounts>();

		private int votes;

		public void See(byte[] code, ShaderInfo info)
		{
			if (OptionalData(code).TryGetValue('p', out byte[] value) && value.Length == 10)
			{
				ResourceCounts resourceCounts = new ResourceCounts(value[4], value[2] | (value[3] << 8), value[5], value[1]);
				wide[info.Sha1] = resourceCounts;
				int num = (from b in info.Bindings
					where b.Class == "cbv" && b.Space == 0
					select b.Lower + Math.Max(1, b.Count)).DefaultIfEmpty(0).Max();
				if (num == resourceCounts.Cb != (num == info.Counts.Cb))
				{
					Interlocked.Add(ref votes, (num == resourceCounts.Cb) ? 1 : (-1));
				}
			}
		}

		public bool Apply(ConcurrentDictionary<string, ShaderInfo> shaders)
		{
			if (votes <= 0)
			{
				return false;
			}
			foreach (var (key, counts) in wide)
			{
				shaders[key] = shaders[key]with
				{
					Counts = counts
				};
			}
			return true;
		}
	}

	public static class UeFlags
	{
		public const int BindlessResources = 1;

		public const int BindlessSamplers = 2;

		public const int RootConstants = 4;

		public const int DiagnosticBuffer = 8;

		public const int AmdIntrinsics = 16;

		public const int NvIntrinsics = 32;

		internal static int Of(byte usage, Dictionary<char, byte[]> d)
		{
			int num = ((d.TryGetValue('x', out byte[] value) && value.Length != 0) ? (value[0] | ((value.Length > 1) ? (value[1] << 8) : 0)) : 0);
			int num2 = 0;
			if ((usage & 2) != 0 || (num & 0x20) != 0)
			{
				num2 |= 1;
			}
			if ((usage & 4) != 0 || (num & 0x40) != 0)
			{
				num2 |= 2;
			}
			if ((usage & 8) != 0)
			{
				num2 |= 4;
			}
			if ((usage & 0x40) != 0 || (num & 0x10) != 0)
			{
				num2 |= 8;
			}
			if (d.TryGetValue('v', out byte[] value2) && value2.Length >= 8)
			{
				for (int i = 4; i + 4 <= value2.Length && i < 4 + 15 * BitConverter.ToInt32(value2, 0); i += 15)
				{
					int num3 = num2;
					num2 = num3 | (BitConverter.ToUInt32(value2, i) switch
					{
						4098u => 16, 
						4318u => 32, 
						_ => 0, 
					});
				}
			}
			return num2;
		}
	}

	private sealed class SemanticKey : IEqualityComparer<(string Semantic, int Index)>
	{
		public static readonly SemanticKey Instance = new SemanticKey();

		public bool Equals((string Semantic, int Index) a, (string Semantic, int Index) b)
		{
			if (a.Index == b.Index)
			{
				return StringComparer.OrdinalIgnoreCase.Equals(a.Semantic, b.Semantic);
			}
			return false;
		}

		public int GetHashCode((string Semantic, int Index) k)
		{
			return HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Semantic), k.Index);
		}
	}

	public sealed record LibraryFunction(int Kind, string Name, int Payload, int Attributes);

	public const int PrimitiveRow = 65536;

	public const int MaxSigElements = 1024;

	public static int Offset(byte[] code, out int len)
	{
		len = code.Length;
		int num = ((ReadOnlySpan<byte>)code.AsSpan()).IndexOf("DXBC"u8);
		while (num >= 0 && num + 28 <= code.Length)
		{
			uint num2 = BitConverter.ToUInt32(code, num + 24);
			if (num2 >= 32 && num + num2 <= code.Length)
			{
				len = (int)num2;
				return num;
			}
			int num3 = ((ReadOnlySpan<byte>)code.AsSpan(num + 1)).IndexOf("DXBC"u8);
			num = ((num3 < 0) ? (-1) : (num + 1 + num3));
		}
		return -1;
	}

	public static Dictionary<char, byte[]> OptionalData(byte[] code)
	{
		Dictionary<char, byte[]> dictionary = new Dictionary<char, byte[]>();
		int num = ((code.Length >= 4) ? BitConverter.ToInt32(code, code.Length - 4) : 0);
		if (num < 4 || num > code.Length)
		{
			return dictionary;
		}
		int i = code.Length - num;
		int num3;
		for (int num2 = code.Length - 4; i + 5 <= num2; i += 5 + num3)
		{
			num3 = BitConverter.ToInt32(code, i + 1);
			if (num3 < 0 || i + 5 + num3 > num2)
			{
				break;
			}
			dictionary[(char)code[i]] = code[(i + 5)..(i + 5 + num3)];
		}
		return dictionary;
	}

	public static ResourceCounts UeCounts(byte[] code, bool ue5 = false, bool nvExtension = false)
	{
		Dictionary<char, byte[]> dictionary = OptionalData(code);
		if (!dictionary.TryGetValue('p', out var value) || value.Length < 5)
		{
			return new ResourceCounts(0, 0, 0, 0);
		}
		return new ResourceCounts(value[3], value[2], value[4], value[1], ue5 ? (UeFlags.Of(value[0], dictionary) & (nvExtension ? (-1) : (-33))) : 0);
	}

	internal static long NameBudget(int partLength)
	{
		return 2L * (long)partLength + 65536;
	}

	private static string Name(ReadOnlySpan<byte> d, int at, ref long budget)
	{
		if ((uint)at > (uint)d.Length)
		{
			throw new ArgumentException("a name outside its string table");
		}
		int num = d.Slice(at).IndexOf((byte)0);
		if (num < 0)
		{
			throw new ArgumentException("a name without its end");
		}
		if ((budget -= 2L * (long)num) < 0)
		{
			throw new ArgumentException("names over the part's budget");
		}
		return Encoding.ASCII.GetString(d.Slice(at, num));
	}

	public static ShaderInfo? Parse(ReadOnlySpan<byte> c, string sha1, ResourceCounts counts)
	{
		Dictionary<string, List<SigElement>> dictionary = new Dictionary<string, List<SigElement>>();
		(string, int)[] array = new (string, int)[6]
		{
			("ISGN", 24),
			("OSGN", 24),
			("OSG5", 28),
			("ISG1", 32),
			("OSG1", 32),
			("PSG1", 32)
		};
		for (int i = 0; i < array.Length; i++)
		{
			(string, int) tuple = array[i];
			string item = tuple.Item1;
			int item2 = tuple.Item2;
			ReadOnlySpan<byte> readOnlySpan = Dxbc.Part(c, Encoding.ASCII.GetBytes(item));
			if (readOnlySpan.IsEmpty)
			{
				continue;
			}
			uint num = U(readOnlySpan, 0);
			if (num > 1024 || U(readOnlySpan, 4) + num * item2 > readOnlySpan.Length)
			{
				throw new ArgumentException($"{item}: {num} elements don't fit the part");
			}
			List<SigElement> list = new List<SigElement>();
			Dictionary<int, string> dictionary2 = new Dictionary<int, string>();
			long budget = NameBudget(readOnlySpan.Length);
			for (int j = 0; j < (int)num; j++)
			{
				int num2 = (int)U(readOnlySpan, 4) + j * item2;
				int num3 = ((item2 == 24) ? (num2 - 4) : num2);
				int num4 = (int)U(readOnlySpan, num3 + 4);
				if (!dictionary2.TryGetValue(num4, out var value))
				{
					value = (dictionary2[num4] = Name(readOnlySpan, num4, ref budget));
				}
				byte readMask;
				if (item[0] == 'I')
				{
					readMask = readOnlySpan[num3 + 25];
				}
				else
				{
					readMask = ((item == "PSG1") ? byte.MaxValue : ((byte)(readOnlySpan[num3 + 24] & ~readOnlySpan[num3 + 25])));
				}
				list.Add(new SigElement(value, (int)U(readOnlySpan, num3 + 8), (int)U(readOnlySpan, num3 + 20), readOnlySpan[num3 + 24], (int)U(readOnlySpan, num3 + 12), (int)U(readOnlySpan, num3 + 16), readMask));
			}
			dictionary[item.Substring(0, 2)] = list;
		}
		int num5 = 0;
		int payload = 0;
		List<Binding> list2 = new List<Binding>();
		ReadOnlySpan<byte> s = Dxbc.Part(c, "DXIL"u8);
		uint num6;
		if (!s.IsEmpty)
		{
			num6 = U(s, 0);
			ReadOnlySpan<byte> s2 = Dxbc.Part(c, "PSV0"u8);
			if (!s2.IsEmpty)
			{
				if (num6 >> 16 == 2 && U(s2, 0) >= 4)
				{
					num5 = (int)U(s2, 4);
				}
				if (num6 >> 16 == 14 && U(s2, 0) >= 4)
				{
					payload = (int)U(s2, 4);
				}
				if (num6 >> 16 == 13 && U(s2, 0) >= 12)
				{
					payload = (int)U(s2, 12);
				}
				int num7 = (int)(4 + U(s2, 0));
				int num8 = (int)U(s2, num7);
				int num9 = (int)((num8 > 0) ? U(s2, num7 + 4) : 0);
				for (int k = 0; k < num8; k++)
				{
					if (num9 < 16)
					{
						break;
					}
					int num10 = num7 + 8 + k * num9;
					string text2;
					switch (U(s2, num10))
					{
					case 1u:
						text2 = "sampler";
						break;
					case 2u:
						text2 = "cbv";
						break;
					case 3u:
					case 4u:
					case 5u:
						text2 = "srv";
						break;
					case 6u:
					case 7u:
					case 8u:
					case 9u:
						text2 = "uav";
						break;
					default:
						text2 = "unknown";
						break;
					}
					string text3 = text2;
					uint num11 = U(s2, num10 + 8);
					uint num12 = U(s2, num10 + 12);
					uint num13 = num11;
					list2.Add(new Binding(text3, (int)U(s2, num10 + 4), (int)num13, (num12 == uint.MaxValue) ? (-1) : ((int)(num12 - num13 + 1))));
				}
			}
		}
		else
		{
			ReadOnlySpan<byte> readOnlySpan2 = Dxbc.Part(c, "SHEX"u8);
			ReadOnlySpan<byte> span = ((!readOnlySpan2.IsEmpty) ? readOnlySpan2 : Dxbc.Part(c, "SHDR"u8));
			if (span.IsEmpty)
			{
				return null;
			}
			ReadOnlySpan<uint> readOnlySpan3 = MemoryMarshal.Cast<byte, uint>(span);
			num6 = readOnlySpan3[0];
			bool flag = ((num6 >> 4) & 0xF) == 5 && (num6 & 0xF) >= 1;
			int num15;
			for (int l = 2; l < readOnlySpan3.Length; l += num15)
			{
				uint num14 = readOnlySpan3[l] & 0x7FF;
				num15 = (int)((num14 == 53) ? readOnlySpan3[l + 1] : ((readOnlySpan3[l] >> 24) & 0x7F));
				if (num15 <= 0 || l + num15 > readOnlySpan3.Length)
				{
					break;
				}
				if (num14 == 93)
				{
					num5 = (int)((readOnlySpan3[l] >> 11) & 0x3F);
				}
				string text2;
				switch (num14)
				{
				case 89u:
					text2 = "cbv";
					break;
				case 90u:
					text2 = "sampler";
					break;
				case 88u:
				case 161u:
				case 162u:
					text2 = "srv";
					break;
				case 156u:
				case 157u:
				case 158u:
					text2 = "uav";
					break;
				default:
					text2 = null;
					break;
				}
				string text4 = text2;
				if (text4 == null)
				{
					continue;
				}
				int num16 = l + 1;
				int num17 = l + num15;
				int num18 = num16;
				if ((readOnlySpan3[l] & 0x80000000u) != 0)
				{
					while (num18 < num17 && (readOnlySpan3[num18++] & 0x80000000u) != 0)
					{
					}
				}
				if ((((num18 < num17) ? readOnlySpan3[num18++] : 0) & 0x80000000u) != 0)
				{
					while (num18 < num17 && (readOnlySpan3[num18++] & 0x80000000u) != 0)
					{
					}
				}
				if (num18 + (flag ? 2 : 0) < num17)
				{
					list2.Add(flag ? new Binding(text4, (int)readOnlySpan3[num17 - 1], (int)readOnlySpan3[num18 + 1], (readOnlySpan3[num18 + 2] == uint.MaxValue) ? (-1) : ((int)(readOnlySpan3[num18 + 2] - readOnlySpan3[num18 + 1] + 1))) : new Binding(text4, 0, (int)readOnlySpan3[num18], 1));
				}
			}
		}
		int num19 = (int)(num6 >> 16);
		if (num19 == 6 && list2.Count == 0)
		{
			list2.AddRange(Rdat(c).Resources);
		}
		Stage? stage = num19 switch
		{
			0 => (Stage?)Stage.Pixel, 
			1 => Stage.Vertex, 
			2 => Stage.Geometry, 
			3 => Stage.Hull, 
			4 => Stage.Domain, 
			5 => Stage.Compute, 
			6 => Stage.Library, 
			13 => Stage.Mesh, 
			14 => Stage.Amplification, 
			_ => null, 
		};
		if (!stage.HasValue)
		{
			return null;
		}
		if (dictionary.Remove("PS", out var value2) && stage == Stage.Mesh)
		{
			List<SigElement> list3 = new List<SigElement>();
			list3.AddRange(dictionary.GetValueOrDefault("OS") ?? new List<SigElement>());
			list3.AddRange(value2.Select((SigElement e) => e with
			{
				Register = (0x10000 | (e.Register & 0xFFFF))
			}));
			dictionary["OS"] = list3;
		}
		string[] array2 = new string[15]
		{
			"ps", "vs", "gs", "hs", "ds", "cs", "lib", "", "", "",
			"", "", "", "ms", "as"
		};
		if (stage == Stage.Pixel && dictionary.TryGetValue("IS", out var value3))
		{
			Dictionary<(string Semantic, int Index), int> modes = InputInterpolation(c);
			if (modes != null && modes.Count > 0)
			{
				dictionary["IS"] = value3.Select((SigElement e) => (!modes.TryGetValue((e.Semantic, e.Index), out var value4)) ? e : e with
				{
					Interpolation = value4
				}).ToList();
			}
		}
		return new ShaderInfo(sha1, stage.Value, $"{array2[num19]}_{(num6 >> 4) & 0xF}_{num6 & 0xF}", c.Length, counts, list2, dictionary.GetValueOrDefault("IS") ?? new List<SigElement>(), dictionary.GetValueOrDefault("OS") ?? new List<SigElement>(), (stage == Stage.Geometry) ? num5 : 0, null, Dxbc.InlineRayTracing(c), null, payload);
	}

	public static Dictionary<(string Semantic, int Index), int> InputInterpolation(ReadOnlySpan<byte> c)
	{
		Dictionary<(string, int), int> dictionary = new Dictionary<(string, int), int>(SemanticKey.Instance);
		ReadOnlySpan<byte> s = Dxbc.Part(c, "PSV0"u8);
		if (34 > s.Length)
		{
			return dictionary;
		}
		byte b = s[32];
		if (b == 0)
		{
			return dictionary;
		}
		int num = (int)(4 + U(s, 0));
		int num2 = (int)U(s, num);
		num += 4;
		if (num2 > 0)
		{
			num += 4 + num2 * (int)U(s, num);
		}
		int num3 = num + 4;
		int num4 = (int)U(s, num);
		int num5 = num3;
		num = num5 + num4;
		if (num > s.Length)
		{
			return dictionary;
		}
		ReadOnlySpan<byte> d = s.Slice(num5, num4);
		long budget = NameBudget(num4);
		Dictionary<int, string> dictionary2 = new Dictionary<int, string>();
		int num6 = num + 4;
		num = num6 + (int)(4 * U(s, num));
		int num7 = (int)U(s, num);
		num += 4;
		for (int i = 0; i < b; i++)
		{
			if (num7 <= 0)
			{
				break;
			}
			if (num + (i + 1) * num7 > s.Length)
			{
				break;
			}
			int num8 = num + i * num7;
			int num9 = (int)U(s, num8);
			if (!dictionary2.TryGetValue(num9, out var value))
			{
				value = (dictionary2[num9] = Name(d, num9, ref budget));
			}
			if (value.Length != 0)
			{
				for (int j = 0; j < s[num8 + 8]; j++)
				{
					dictionary[(value, (int)U(s, num6 + 4 * ((int)U(s, num8 + 4) + j)))] = s[num8 + 13];
				}
			}
		}
		return dictionary;
	}

	public static (List<LibraryFunction> Functions, List<Binding> Resources) Rdat(ReadOnlySpan<byte> c)
	{
		List<LibraryFunction> list = new List<LibraryFunction>();
		List<Binding> list2 = new List<Binding>();
		byte[] r = Dxbc.Part(c, "RDAT"u8).ToArray();
		if (r.Length == 0)
		{
			return (Functions: list, Resources: list2);
		}
		Dictionary<uint, int> dictionary = new Dictionary<uint, int>();
		for (int i = 0; i < (int)U(r, 4); i++)
		{
			int num = (int)U(r, 8 + 4 * i);
			dictionary[U(r, num)] = num + 8;
		}
		if (!dictionary.TryGetValue(1u, out var strings))
		{
			return (Functions: list, Resources: list2);
		}
		int stringsSize = (int)U(r, strings - 4);
		if ((uint)stringsSize > (uint)(r.Length - strings))
		{
			throw new ArgumentException("RDAT: the string table runs past the part");
		}
		long budget = NameBudget(stringsSize);
		Dictionary<uint, string> names = new Dictionary<uint, string>();
		if (dictionary.TryGetValue(3u, out var value))
		{
			for (int j = 0; j < (int)U(r, value); j++)
			{
				if ((int)U(r, value + 4) < 24)
				{
					break;
				}
				int num2 = value + 8 + j * (int)U(r, value + 4);
				string text = U(r, num2) switch
				{
					0u => "srv", 
					1u => "uav", 
					2u => "cbv", 
					3u => "sampler", 
					_ => "unknown", 
				};
				uint num3 = U(r, num2 + 16);
				uint num4 = U(r, num2 + 20);
				uint num5 = num3;
				list2.Add(new Binding(text, (int)U(r, num2 + 12), (int)num5, (num4 == uint.MaxValue) ? (-1) : ((int)(num4 - num5 + 1))));
			}
		}
		if (dictionary.TryGetValue(4u, out var value2))
		{
			for (int k = 0; k < (int)U(r, value2); k++)
			{
				if ((int)U(r, value2 + 4) < 28)
				{
					break;
				}
				int num6 = value2 + 8 + k * (int)U(r, value2 + 4);
				list.Add(new LibraryFunction((int)U(r, num6 + 16), Str(U(r, num6 + 4)), (int)U(r, num6 + 20), (int)U(r, num6 + 24)));
			}
		}
		return (Functions: list, Resources: list2);
		string Str(uint off)
		{
			if (!names.TryGetValue(off, out string value3))
			{
				return names[off] = Name(r.AsSpan(strings, stringsSize), (int)off, ref budget);
			}
			return value3;
		}
	}

	private static uint U(ReadOnlySpan<byte> s, int o)
	{
		return BitConverter.ToUInt32(s.Slice(o, 4));
	}
}
