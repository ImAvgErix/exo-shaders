using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Planning;

public static class HashOnly
{
	public sealed record Counts(int Psos, int GraphicsPsos, int DistinctVs);

	public sealed record Dropped(int ShaderBlobs, int StateObjects, int UnusedRootSignatures, int Duplicates);

	private readonly record struct Sha(ulong A, ulong B, uint C)
	{
		public static Sha Of(ReadOnlySpan<byte> h)
		{
			return new Sha(BitConverter.ToUInt64(h), BitConverter.ToUInt64(h.Slice(8)), BitConverter.ToUInt32(h.Slice(16)));
		}
	}

	private readonly record struct Size(long N, long Raw, long Refs)
	{
		public static Size operator +(Size a, Size b)
		{
			return new Size(a.N + b.N, a.Raw + b.Raw, a.Refs + b.Refs);
		}
	}

	public const int MaxRecords = 200000;

	public const int MaxEntryRecords = 250000;

	public const int MaxFullRecords = 500000;

	public const int MaxRootSignature = 65536;

	public const int MaxStateObjectRefs = 262144;

	public const int MaxEntryStateObjectRefs = 1048576;

	public const int MaxAutoStateObjectRefs = 786432;

	public const string TooManyReferences = "state objects naming more than ";

	public const long MaxRaw = 268435456L;

	public const string Tags = "BGCSRANLD";

	public const int RemoteLimit = 250001;

	public static List<PsoDb.Rec> Canonical(IEnumerable<PsoDb.Rec> records, bool local, out Dropped dropped, int maxRefs = 262144, IReadOnlySet<string>? layerMade = null, int maxRecords = 200000)
	{
		SortedDictionary<string, PsoDb.Rec> blobs = new SortedDictionary<string, PsoDb.Rec>(StringComparer.Ordinal);
		SortedDictionary<string, PsoDb.Rec> sortedDictionary = new SortedDictionary<string, PsoDb.Rec>(StringComparer.Ordinal);
		SortedDictionary<string, (PsoDb.Rec Rec, PsoDb.StateObject So)> states = new SortedDictionary<string, (PsoDb.Rec, PsoDb.StateObject)>(StringComparer.Ordinal);
		SortedDictionary<string, PsoDb.Rec> sortedDictionary2 = new SortedDictionary<string, PsoDb.Rec>(StringComparer.Ordinal);
		SortedDictionary<string, PsoDb.Rec> sortedDictionary3 = new SortedDictionary<string, PsoDb.Rec>(StringComparer.Ordinal);
		HashSet<string> hashSet = new HashSet<string>();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		foreach (PsoDb.Rec record in records)
		{
			switch (record.Tag)
			{
			case 'B':
				if (ShaderBlob(record, local))
				{
					num++;
				}
				else if (!blobs.TryAdd(PsoDb.Hex(record.Payload.AsSpan(0, 20)), record))
				{
					num3++;
				}
				goto IL_0308;
			case 'A':
			case 'R':
			{
				PsoDb.StateObject stateObject = PsoDb.ParseStateObject(record);
				if (!states.TryAdd(record.Key, (record, stateObject)))
				{
					num3++;
				}
				else if ((num5 += stateObject.Libraries.Count + stateObject.RootSignatures.Count + stateObject.Depends.Count) > maxRefs)
				{
					throw new InvalidDataException($"{"state objects naming more than "}{maxRefs} root signatures, libraries and collections");
				}
				goto IL_0308;
			}
			case 'N':
				PsoDb.NvState.Parse(record);
				if (!sortedDictionary2.TryAdd(record.Key, record))
				{
					num3++;
				}
				goto IL_0308;
			case 'L':
				if (record.Payload.Length != 20)
				{
					throw new InvalidDataException($"'L' record of {record.Payload.Length} bytes");
				}
				if (!sortedDictionary3.TryAdd(record.Key, record))
				{
					num3++;
				}
				goto IL_0308;
			case 'W':
				if (!local)
				{
					break;
				}
				if (record.Payload.Length != 40)
				{
					throw new InvalidDataException($"'W' record of {record.Payload.Length} bytes");
				}
				hashSet.Add(PsoDb.Hex(record.Payload.AsSpan(0, 20)));
				num2++;
				goto IL_0308;
			case 'D':
				CheckDraw11(record);
				if (!sortedDictionary.TryAdd(record.Key, record))
				{
					num3++;
				}
				goto IL_0308;
			}
			PsoDb.Check(record);
			if (!sortedDictionary.TryAdd(record.Key, record))
			{
				num3++;
			}
			goto IL_0308;
			IL_0308:
			if (++num4 - num - num2 > maxRecords)
			{
				throw new InvalidDataException($"more than {maxRecords} records");
			}
		}
		if (local && layerMade != null)
		{
			hashSet.UnionWith(layerMade);
		}
		foreach (string item3 in hashSet)
		{
			sortedDictionary.Remove(item3);
			states.Remove(item3);
		}
		List<PsoDb.Rec> list = new List<PsoDb.Rec>();
		Dictionary<string, bool> done = new Dictionary<string, bool>();
		List<(string Key, int Next)> stack = new List<(string, int)>();
		foreach (string key in states.Keys)
		{
			bool? flag = Enter(key);
			while (stack.Count > 0)
			{
				List<(string Key, int Next)> list2 = stack;
				(string Key, int Next) tuple = list2[list2.Count - 1];
				string item = tuple.Key;
				int item2 = tuple.Next;
				(PsoDb.Rec, PsoDb.StateObject) tuple2 = states[item];
				if (flag == false)
				{
					stack.RemoveAt(stack.Count - 1);
					flag = Fail(item, "the record it builds on (" + tuple2.Item2.Depends[item2 - 1] + ") isn't in the recording or can't be replayed");
				}
				else if (item2 == tuple2.Item2.Depends.Count)
				{
					stack.RemoveAt(stack.Count - 1);
					list.Add(tuple2.Item1);
					bool value = (done[item] = true);
					flag = value;
				}
				else
				{
					List<(string Key, int Next)> list3 = stack;
					list3[list3.Count - 1] = (item, item2 + 1);
					flag = Enter(tuple2.Item2.Depends[item2]);
				}
			}
		}
		if (sortedDictionary.Count == 0 && list.Count == 0)
		{
			throw new InvalidDataException("no PSO or state object records");
		}
		HashSet<string> used = (from r in sortedDictionary.Values
			where r.Tag != 'D'
			select PsoDb.Parse(r).Rs).Concat(list.SelectMany((PsoDb.Rec r) => PsoDb.ParseStateObject(r).RootSignatures)).ToHashSet();
		List<PsoDb.Rec> list4 = (from b in blobs
			where used.Contains(b.Key)
			select b.Value).ToList();
		dropped = new Dropped(num, states.Count - list.Count, blobs.Count - list4.Count, num3);
		HashSet<string> kept = sortedDictionary.Keys.Concat(list.Select((PsoDb.Rec r) => r.Key)).ToHashSet();
		List<PsoDb.Rec> list5 = new List<PsoDb.Rec>();
		list5.AddRange(list4);
		list5.AddRange(sortedDictionary.Values);
		list5.AddRange(list);
		list5.AddRange(sortedDictionary2.Values.Where((PsoDb.Rec r) => kept.Contains(Target(r))));
		list5.AddRange(sortedDictionary3.Values.Where((PsoDb.Rec r) => kept.Contains(Target(r))));
		return list5;
		bool? Enter(string key)
		{
			if (done.TryGetValue(key, out var value2))
			{
				return value2;
			}
			if (!states.TryGetValue(key, out (PsoDb.Rec, PsoDb.StateObject) value3))
			{
				return false;
			}
			done[key] = false;
			if (value3.Item2.Libraries.Count == 0 && value3.Item2.Depends.Count == 0)
			{
				return Fail(key, "no DXIL library, collection or base: nothing to create");
			}
			string text = value3.Item2.RootSignatures.FirstOrDefault((string h) => !blobs.ContainsKey(h));
			if (text != null)
			{
				return Fail(key, "root signature " + text + " isn't in the recording");
			}
			stack.Add((key, 0));
			return null;
		}
		bool Fail(string key, string why)
		{
			if (!local)
			{
				throw new InvalidDataException("state object " + key + ": " + why);
			}
			return false;
		}
	}

	private static bool ShaderBlob(PsoDb.Rec r, bool local)
	{
		if (r.Payload.Length <= 20)
		{
			throw new InvalidDataException("empty 'B' record");
		}
		Span<byte> span = r.Payload.AsSpan(20);
		string text = PsoDb.Hex(r.Payload.AsSpan(0, 20));
		if (PsoDb.Hex(SHA1.HashData(span)) != text)
		{
			throw new InvalidDataException("blob " + text + " doesn't match its SHA-1");
		}
		if (!Dxbc.IsRootSignatureOnly(span))
		{
			if (!local)
			{
				throw new InvalidDataException("blob " + text + " is not a root signature (shader bytes are never shared)");
			}
			return true;
		}
		if (span.Length > 65536)
		{
			throw new InvalidDataException($"root signature {text} is over {65536} bytes");
		}
		if (!Dxbc.RootSignatureValid(span))
		{
			throw new InvalidDataException("root signature " + text + " is malformed");
		}
		return false;
	}

	public static (int Psos, long Raw) Verify(IEnumerable<PsoDb.Rec> records, int maxRefs, int maxRecords, bool flagsApart = false)
	{
		HashSet<Sha> blobs = new HashSet<Sha>();
		HashSet<Sha> hashSet = new HashSet<Sha>();
		Dictionary<Sha, PsoDb.StateObject> states = new Dictionary<Sha, PsoDb.StateObject>();
		IncrementalHash sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
		byte[] key;
		try
		{
			key = new byte[20];
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			long num4 = 0L;
			long num5 = 0L;
			foreach (PsoDb.Rec record in records)
			{
				num5 += 5L + (long)record.Payload.Length;
				switch (record.Tag)
				{
				case 'B':
					ShaderBlob(record, local: false);
					blobs.Add(Sha.Of(record.Payload));
					break;
				case 'A':
				case 'R':
				{
					PsoDb.StateObject stateObject = PsoDb.ParseStateObject(record);
					if (states.TryAdd(KeyOf(record), stateObject) && (num4 += stateObject.Libraries.Count + stateObject.RootSignatures.Count + stateObject.Depends.Count) > maxRefs)
					{
						throw new InvalidDataException($"{"state objects naming more than "}{maxRefs} root signatures, libraries and collections");
					}
					break;
				}
				case 'N':
					PsoDb.NvState.Parse(record);
					break;
				case 'L':
					if (record.Payload.Length != 20)
					{
						throw new InvalidDataException($"'L' record of {record.Payload.Length} bytes");
					}
					if (flagsApart)
					{
						if (++num2 <= maxRecords)
						{
							continue;
						}
						throw new InvalidDataException($"more than {maxRecords} 'L' records");
					}
					break;
				case 'D':
					CheckDraw11(record);
					num3++;
					break;
				default:
					PsoDb.Check(record);
					hashSet.Add(KeyOf(record));
					break;
				}
				if (++num > maxRecords)
				{
					throw new InvalidDataException($"more than {maxRecords} records");
				}
			}
			foreach (PsoDb.StateObject value in states.Values)
			{
				if (value.Libraries.Count == 0 && value.Depends.Count == 0)
				{
					throw new InvalidDataException("a state object with no DXIL library, collection or base: nothing to create");
				}
				if (value.RootSignatures.Any((string h) => !blobs.Contains(Sha.Of(Convert.FromHexString(h)))))
				{
					throw new InvalidDataException("a state object's root signature isn't in the recording");
				}
				if (value.Depends.Any((string h) => !states.ContainsKey(Sha.Of(Convert.FromHexString(h)))))
				{
					throw new InvalidDataException("the record a state object builds on isn't in the recording");
				}
			}
			if (hashSet.Count == 0 && states.Count == 0 && num3 == 0)
			{
				throw new InvalidDataException("no PSO or state object records");
			}
			if (num3 > 0)
			{
				throw new InvalidDataException("a game build's entry holds no D3D11 draws ('D' records): they go to its draws entry");
			}
			if (num2 > hashSet.Count + states.Count)
			{
				throw new InvalidDataException("more 'L' records than PSOs and state objects");
			}
			return (Psos: hashSet.Count, Raw: num5);
		}
		finally
		{
			if (sha1 != null)
			{
				((IDisposable)sha1).Dispose();
			}
		}
		Sha KeyOf(PsoDb.Rec r)
		{
			sha1.AppendData(new ReadOnlySpan<byte>((byte)r.Tag));
			sha1.AppendData(r.Payload);
			sha1.GetHashAndReset(key);
			return Sha.Of(key);
		}
	}

	public static string PackKey(string gpu, string vendor, string dll, string sha1)
	{
		return $"{gpu}:{vendor}:{dll.ToLowerInvariant()}:{sha1}";
	}

	public static string PackHash(string key)
	{
		return PsoDb.Hex(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
	}

	public static bool IsPackKey(string? key)
	{
		string[] array = key?.Split(':');
		string text = default;
		string text2 = default;
		string text3 = default;
		string text4 = default;
		int num;
		if (array != null && array.Length == 4)
		{
			text = array[0];
			text2 = array[1];
			text3 = array[2];
			text4 = array[3];
			num = 1;
		}
		else
		{
			num = 0;
		}
		bool flag = (byte)num != 0;
		if (flag)
		{
			bool flag2 = ((text == "nvidia" || text == "amd") ? true : false);
			flag = flag2;
		}
		if (flag && text2 != null)
		{
			int length = text2.Length;
			if (length >= 1 && length <= 16 && text2.All((char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)) && text3 != null)
			{
				length = text3.Length;
				if (length >= 5 && length <= 96 && text3.EndsWith(".dll", StringComparison.Ordinal) && text3.All((char c) =>
				{
					bool flag3 = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c);
					if (!flag3)
					{
						bool flag4 = ((c == '-' || c == '.' || c == '_') ? true : false);
						flag3 = flag4;
					}
					return flag3;
				}) && text4.Length == 40)
				{
					return text4.All(char.IsAsciiHexDigitLower);
				}
			}
		}
		return false;
	}

	public static void CheckDraw11(PsoDb.Rec r)
	{
		byte[] payload = r.Payload;
		bool flag = payload.Length >= 92 && !Zero(payload, 0) && Zero(payload, 40) == Zero(payload, 60);
		if (flag)
		{
			bool flag2;
			switch (U(payload, 80))
			{
			case 1u:
			case 2u:
			case 3u:
			case 4u:
			case 5u:
			case 10u:
			case 11u:
			case 12u:
			case 13u:
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
			case 44u:
			case 45u:
			case 46u:
			case 47u:
			case 48u:
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
			case 60u:
			case 61u:
			case 62u:
			case 63u:
			case 64u:
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			flag = flag2;
		}
		bool flag3 = flag && U(payload, 84) <= 1 && U(payload, 88) <= 32;
		int num = 92;
		uint num2 = 0u;
		uint num3 = (flag3 ? U(payload, 88) : 0u);
		while (flag3 && num2 < num3)
		{
			uint num4 = ((num + 4 <= payload.Length) ? U(payload, num) : 0u);
			flag3 = num4 >= 1 && num4 <= 64 && num + 4 + num4 + 24 <= payload.Length && ((ReadOnlySpan<byte>)payload.AsSpan(num + 4, (int)num4)).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_"u8) < 0;
			if (!flag3)
			{
				break;
			}
			num += (int)(4 + num4);
			flag3 = U(payload, num + 8) < 32 && U(payload, num + 16) <= 1;
			num += 24;
			num2++;
		}
		if (!flag3 || num != payload.Length)
		{
			throw new InvalidDataException("malformed 'D' record");
		}
		static uint U(byte[] p, int o)
		{
			return BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(o));
		}
		static bool Zero(byte[] p, int o)
		{
			return !((ReadOnlySpan<byte>)p.AsSpan(o, 20)).ContainsAnyExcept((byte)0);
		}
	}

	public static string DrawsKey(string contentHash)
	{
		return "d3d11:" + contentHash;
	}

	public static bool IsDrawsKey(string? key)
	{
		if (key != null && key.Length == 46 && key.StartsWith("d3d11:", StringComparison.Ordinal))
		{
			return key.Substring(6).All(char.IsAsciiHexDigitLower);
		}
		return false;
	}

	public static List<PsoDb.Rec> CheckNoDraws(List<PsoDb.Rec> canonical)
	{
		if (!canonical.Any((PsoDb.Rec r) => r.Tag == 'D'))
		{
			return canonical;
		}
		throw new InvalidDataException("a game build's entry holds no D3D11 draws ('D' records): they go to its draws entry");
	}

	public static List<PsoDb.Rec> CheckDraws(List<PsoDb.Rec> canonical)
	{
		int num = canonical.FindIndex((PsoDb.Rec r) =>
		{
			char tag = r.Tag;
			bool flag = ((tag == 'D' || tag == 'L') ? true : false);
			return !flag;
		});
		if (num < 0)
		{
			return canonical;
		}
		throw new InvalidDataException($"a draws entry holds only D3D11 draws, not '{canonical[num].Tag}' records");
	}

	public static List<PsoDb.Rec> CheckPack(List<PsoDb.Rec> canonical)
	{
		int num = canonical.FindIndex((PsoDb.Rec r) =>
		{
			bool flag;
			switch (r.Tag)
			{
			case 'B':
			case 'C':
			case 'G':
			case 'S':
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			return !flag;
		});
		if (num < 0)
		{
			return canonical;
		}
		throw new InvalidDataException($"a pack holds only PSOs and root signatures, not '{canonical[num].Tag}' records");
	}

	public static string Target(PsoDb.Rec r)
	{
		if (r.Tag != 'N')
		{
			return PsoDb.Hex(r.Payload);
		}
		return PsoDb.NvState.Parse(r).Target;
	}

	public static IEnumerable<PsoDb.Rec> LocalOnly(IEnumerable<PsoDb.Rec> records, Func<string, bool> shipped)
	{
		return from r in records.Where((PsoDb.Rec r) =>
			{
				switch (r.Tag)
				{
				case 'C':
				case 'G':
				case 'S':
					return PsoDb.Parse(r).Stages.Values.Any((string h) => !shipped(h));
				case 'A':
				case 'R':
					return PsoDb.ParseStateObject(r).Libraries.Any((string h) => !shipped(h));
				case 'D':
				{
					PsoDb.Draw11 draw = PsoDb.ParseDraw11(r.Payload);
					return new string[4] { draw.Vs, draw.Gs, draw.Hs, draw.Ds }.Any((string h) => h != PsoDb.Zero && !shipped(h));
				}
				default:
					return false;
				}
			})
			select new PsoDb.Rec('L', Convert.FromHexString(r.Key));
	}

	public static Counts Count(IEnumerable<PsoDb.Rec> records)
	{
		int num = 0;
		int num2 = 0;
		HashSet<string> hashSet = new HashSet<string>();
		foreach (PsoDb.Rec item in records.Where((PsoDb.Rec r) =>
		{
			switch (r.Tag)
			{
			case 'C':
			case 'D':
			case 'G':
			case 'S':
				return true;
			default:
				return false;
			}
		}))
		{
			if (item.Tag == 'D')
			{
				int num3 = num + 1;
				num2++;
				num = num3;
				hashSet.Add(PsoDb.ParseDraw11(item.Payload).Vs);
				continue;
			}
			PsoDb.Pso pso = PsoDb.Parse(item);
			num++;
			if (!pso.Stages.ContainsKey(6))
			{
				num2++;
			}
			if (pso.Stages.TryGetValue(1, out string value))
			{
				hashSet.Add(value);
			}
		}
		return new Counts(num, num2, hashSet.Count);
	}

	public static List<List<PsoDb.Rec>> Chunks(IReadOnlyList<PsoDb.Rec> canonical, int maxRecords, long maxRaw)
	{
		int skipped;
		return Chunks(canonical, maxRecords, maxRaw, out skipped);
	}

	public static List<List<PsoDb.Rec>> Chunks(IReadOnlyList<PsoDb.Rec> canonical, int maxRecords, long maxRaw, out int skipped)
	{
		skipped = 0;
		List<PsoDb.Rec> blobs = canonical.Where((PsoDb.Rec r) => r.Tag == 'B').ToList();
		Dictionary<string, PsoDb.Rec> blobOf = blobs.ToDictionary((PsoDb.Rec b) => PsoDb.Hex(b.Payload.AsSpan(0, 20)));
		Dictionary<string, (PsoDb.Rec Rec, PsoDb.StateObject So)> states = canonical.Where((PsoDb.Rec r) =>
		{
			char tag = r.Tag;
			return (tag == 'A' || tag == 'R') ? true : false;
		}).ToDictionary((PsoDb.Rec r) => r.Key, (PsoDb.Rec r) => (Rec: r, So: PsoDb.ParseStateObject(r)));
		ILookup<string, PsoDb.Rec> about = canonical.Where((PsoDb.Rec r) =>
		{
			char tag = r.Tag;
			return (tag == 'L' || tag == 'N') ? true : false;
		}).ToLookup(Target);
		HashSet<string> tooBig = new HashSet<string>();
		List<List<PsoDb.Rec>> chunks = new List<List<PsoDb.Rec>>();
		Dictionary<string, PsoDb.Rec> chunk = new Dictionary<string, PsoDb.Rec>();
		Size size = new Size(0L, 0L, 0L);
		HashSet<string> chunkRs = new HashSet<string>();
		foreach (PsoDb.Rec item2 in canonical.Where((PsoDb.Rec r) =>
		{
			char tag = r.Tag;
			bool flag = ((tag == 'B' || tag == 'L' || tag == 'N') ? true : false);
			return !flag;
		}))
		{
			List<PsoDb.Rec> list = Close(item2, chunk);
			if (list != null && chunk.Count > 0 && Over(size + Of(list)))
			{
				Flush();
				list = Close(item2, chunk);
			}
			Size size2 = ((list == null) ? default(Size) : Of(list));
			if (list == null || (chunk.Count == 0 && Over(size2)))
			{
				tooBig.Add(item2.Key);
				skipped++;
				continue;
			}
			size += size2;
			foreach (PsoDb.Rec item3 in list)
			{
				chunk[item3.Key] = item3;
			}
			chunkRs.UnionWith(list.SelectMany(Named));
		}
		Flush();
		return chunks;
		List<PsoDb.Rec>? Close(PsoDb.Rec r, Dictionary<string, PsoDb.Rec> have)
		{
			Dictionary<string, PsoDb.Rec> dictionary = new Dictionary<string, PsoDb.Rec>();
			Stack<PsoDb.Rec> stack = new Stack<PsoDb.Rec>(new _003C_003Ez__ReadOnlySingleElementList<PsoDb.Rec>(r));
			PsoDb.Rec result;
			while (stack.TryPop(out result))
			{
				if (tooBig.Contains(result.Key))
				{
					return null;
				}
				if (!have.ContainsKey(result.Key) && dictionary.TryAdd(result.Key, result))
				{
					foreach (PsoDb.Rec item4 in about[result.Key])
					{
						dictionary.TryAdd(item4.Key, item4);
					}
					char tag = result.Tag;
					if ((tag == 'A' || tag == 'R') ? true : false)
					{
						foreach (string depend in states[result.Key].So.Depends)
						{
							stack.Push(states[depend].Rec);
						}
					}
				}
			}
			return dictionary.Values.ToList();
		}
		void Flush()
		{
			if (chunk.Count > 0)
			{
				List<List<PsoDb.Rec>> list2 = chunks;
				List<PsoDb.Rec> list3 = blobs;
				Dictionary<string, PsoDb.Rec>.ValueCollection values = chunk.Values;
				int num = 0;
				PsoDb.Rec[] array = new PsoDb.Rec[list3.Count + values.Count];
				Span<PsoDb.Rec> span = CollectionsMarshal.AsSpan(list3);
				span.CopyTo(new Span<PsoDb.Rec>(array).Slice(num, span.Length));
				num += span.Length;
				foreach (PsoDb.Rec item5 in values)
				{
					array[num] = item5;
					num++;
				}
				list2.Add(Canonical(new _003C_003Ez__ReadOnlyArray<PsoDb.Rec>(array), local: false, out Dropped _));
			}
			chunk = new Dictionary<string, PsoDb.Rec>();
			chunkRs = new HashSet<string>();
			size = new Size(0L, 0L, 0L);
		}
		IEnumerable<string> Named(PsoDb.Rec u)
		{
			char tag = u.Tag;
			if ((tag == 'A' || tag == 'R') ? true : false)
			{
				return states[u.Key].So.RootSignatures;
			}
			char tag2 = u.Tag;
			List<string> list2;
			if ((tag2 != 'C' && tag2 != 'G' && tag2 != 'S') || 1 == 0)
			{
				list2 = new List<string>();
			}
			else
			{
				int num = 1;
				list2 = new List<string>(num);
				CollectionsMarshal.SetCount(list2, num);
				CollectionsMarshal.AsSpan(list2)[0] = PsoDb.Parse(u).Rs;
			}
			return list2;
		}
		Size Of(List<PsoDb.Rec> l)
		{
			IEnumerable<string> source = (from h in l.SelectMany(Named)
				where blobOf.ContainsKey(h) && !chunkRs.Contains(h)
				select h).Distinct();
			return new Size(l.Count((PsoDb.Rec u) =>
			{
				char tag = u.Tag;
				bool flag = ((tag == 'L' || tag == 'N') ? true : false);
				return !flag;
			}), l.Sum((PsoDb.Rec u) => 5L + (long)u.Payload.Length) + source.Sum((string h) => 5L + (long)blobOf[h].Payload.Length), ((IEnumerable<PsoDb.Rec>)l).Sum((Func<PsoDb.Rec, long>)Refs));
		}
		bool Over(Size s)
		{
			if (s.Refs <= 262144 && s.N <= maxRecords)
			{
				return s.Raw > maxRaw;
			}
			return true;
		}
		long Refs(PsoDb.Rec u)
		{
			char tag = u.Tag;
			int num;
			if ((tag == 'A' || tag == 'R') ? true : false)
			{
				PsoDb.StateObject item = states[u.Key].So;
				num = item.Libraries.Count + item.RootSignatures.Count + item.Depends.Count;
			}
			else
			{
				num = 0;
			}
			return num;
		}
	}

	public static byte[] Compress(IEnumerable<PsoDb.Rec> records)
	{
		BrotliEncoder enc = new BrotliEncoder(11, 22);
		MemoryStream out_;
		byte[] buf;
		try
		{
			out_ = new MemoryStream();
			try
			{
				buf = new byte[65536];
				Span<byte> span = stackalloc byte[5];
				foreach (PsoDb.Rec record in records)
				{
					span[0] = (byte)record.Tag;
					BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1), (uint)record.Payload.Length);
					Feed(span, final: false);
					Feed(record.Payload, final: false);
				}
				Feed(default, final: true);
				return out_.ToArray();
			}
			finally
			{
				if (out_ != null)
				{
					((IDisposable)out_).Dispose();
				}
			}
		}
		finally
		{
			((IDisposable)enc/*cast due to constrained. prefix*/).Dispose();
		}
		void Feed(ReadOnlySpan<byte> src, bool final)
		{
			OperationStatus operationStatus = OperationStatus.DestinationTooSmall;
			while (operationStatus == OperationStatus.DestinationTooSmall || !src.IsEmpty)
			{
				operationStatus = enc.Compress(src, buf, out var bytesConsumed, out var bytesWritten, final);
				if (operationStatus == OperationStatus.InvalidData)
				{
					throw new InvalidOperationException("Brotli failed");
				}
				out_.Write(buf, 0, bytesWritten);
				src = src.Slice(bytesConsumed);
				switch (operationStatus)
				{
				case OperationStatus.NeedMoreData:
					if (src.IsEmpty)
					{
						return;
					}
					break;
				case OperationStatus.Done:
					return;
				}
			}
		}
	}

	public static List<PsoDb.Rec> Decompress(ReadOnlySpan<byte> compressed, long max = 268435456L, int limit = int.MaxValue)
	{
		Walk(compressed, max, null, 0);
		List<PsoDb.Rec> list = new List<PsoDb.Rec>();
		Walk(compressed, max, list, limit);
		return list;
	}

	private static void Walk(ReadOnlySpan<byte> compressed, long max, List<PsoDb.Rec>? into, int limit)
	{
		using BrotliDecoder brotliDecoder = default;
		byte[] array = new byte[65536];
		Span<byte> span = stackalloc byte[5];
		int num = 0;
		int num2 = 0;
		long num3 = 0L;
		long num4 = 0L;
		byte[] array2 = null;
		OperationStatus operationStatus = OperationStatus.DestinationTooSmall;
		while (operationStatus != OperationStatus.Done)
		{
			operationStatus = brotliDecoder.Decompress(compressed, array, out var bytesConsumed, out var bytesWritten);
			if ((uint)(operationStatus - 2) <= 1u)
			{
				throw new InvalidDataException("not a complete Brotli stream");
			}
			if (num3 + bytesWritten > max)
			{
				throw new InvalidDataException($"over {max} bytes decompressed");
			}
			compressed = compressed.Slice(bytesConsumed);
			num3 += bytesWritten;
			Span<byte> span2 = array.AsSpan(0, bytesWritten);
			while (!span2.IsEmpty)
			{
				if (num4 > 0)
				{
					int num5 = (int)Math.Min(num4, span2.Length);
					if (array2 != null)
					{
						span2.Slice(0, num5).CopyTo(array2.AsSpan(num2));
					}
					int num6 = num2 + num5;
					num4 -= num5;
					num2 = num6;
					span2 = span2.Slice(num5);
					if (num4 == 0L && array2 != null)
					{
						into.Add(new PsoDb.Rec((char)span[0], array2));
					}
					continue;
				}
				int num7 = Math.Min(5 - num, span2.Length);
				span2.Slice(0, num7).CopyTo(span.Slice(num));
				num += num7;
				span2 = span2.Slice(num7);
				if (num >= 5)
				{
					num = 0;
					num4 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(1));
					array2 = ((into != null && into.Count < limit) ? new byte[num4] : null);
					num2 = 0;
					if (num4 == 0L && array2 != null)
					{
						into.Add(new PsoDb.Rec((char)span[0], array2));
					}
				}
			}
		}
		if (!compressed.IsEmpty)
		{
			throw new InvalidDataException("bytes after the Brotli stream");
		}
		if (num != 0 || num4 != 0L)
		{
			throw new InvalidDataException("truncated record");
		}
	}

	public static List<PsoDb.Rec> Records(ReadOnlySpan<byte> raw, int limit = int.MaxValue)
	{
		for (long num = 0L; num < raw.Length; num += 5 + Len(raw, num))
		{
			if (num + 5 > raw.Length || num + 5 + Len(raw, num) > raw.Length)
			{
				throw new InvalidDataException("truncated record");
			}
		}
		List<PsoDb.Rec> list = new List<PsoDb.Rec>();
		for (long num2 = 0L; num2 < raw.Length; num2 += 5 + Len(raw, num2))
		{
			if (list.Count >= limit)
			{
				break;
			}
			list.Add(new PsoDb.Rec((char)raw[(int)num2], raw.Slice((int)num2 + 5, (int)Len(raw, num2)).ToArray()));
		}
		return list;
		static long Len(ReadOnlySpan<byte> readOnlySpan, long at)
		{
			return BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan.Slice((int)(at + 1)));
		}
	}

	public static IEnumerable<PsoDb.Rec> Records(Stream s, long max)
	{
		byte[] head = new byte[5];
		long raw = 0L;
		while (true)
		{
			int num = s.ReadAtLeast(head, 5, throwOnEndOfStream: false);
			if (num == 0)
			{
				yield break;
			}
			if (num < 5)
			{
				throw new InvalidDataException("truncated record");
			}
			uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1));
			long num3;
			raw = (num3 = raw + (5L + (long)num2));
			if (num3 > max)
			{
				throw new InvalidDataException($"over {max} bytes decompressed");
			}
			byte tag = head[0];
			byte[]? array = PsoDb.Body(s, num2);
			if (array == null)
			{
				break;
			}
			yield return new PsoDb.Rec((char)tag, array);
		}
		throw new InvalidDataException("truncated record");
	}
}
