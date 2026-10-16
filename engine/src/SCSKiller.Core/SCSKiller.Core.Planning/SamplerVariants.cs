using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Planning;

public static class SamplerVariants
{
	public static string? Pipeline(PsoDb.Rec r, Func<string, byte[]?> blob)
	{
		bool flag;
		switch (r.Tag)
		{
		case 'C':
		case 'G':
		case 'P':
		case 'S':
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			return null;
		}
		string s;
		byte[] array2;
		try
		{
			byte[] array = blob(s = ((r.Tag == 'P') ? PsoDb.ParseItem(r.Payload).Rs : PsoDb.Parse(r).Rs));
			array2 = ((array != null) ? RootSig.WithoutSamplerSettings(array) : null);
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is KeyNotFoundException || ex is IndexOutOfRangeException) ? true : false)
		{
			return null;
		}
		if (array2 == null)
		{
			return null;
		}
		byte[] array3 = r.Payload.ToArray();
		int num = ((ReadOnlySpan<byte>)array3.AsSpan()).IndexOf((ReadOnlySpan<byte>)Convert.FromHexString(s));
		if (num < 0)
		{
			return null;
		}
		SHA1.HashData(array2).CopyTo(array3, num);
		byte b = (byte)r.Tag;
		byte[] array4 = array3;
		int num2 = 0;
		byte[] array5 = new byte[1 + array4.Length];
		array5[num2] = b;
		num2++;
		ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(array4);
		readOnlySpan.CopyTo(new Span<byte>(array5).Slice(num2, readOnlySpan.Length));
		num2 += readOnlySpan.Length;
		return PsoDb.Hex(SHA1.HashData(new ReadOnlySpan<byte>(array5)));
	}

	public static (string RootSignature, string Samplers)? Setting(byte[] rs)
	{
		try
		{
			byte[] array = RootSig.WithoutSamplerSettings(rs);
			return (array != null) ? new (string, string)?((PsoDb.Hex(SHA1.HashData(array)), Convert.ToHexStringLower(RootSig.Samplers(rs)))) : (((string, string)?)null);
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
		{
			return null;
		}
	}

	public static HashSet<string> OtherSettings(IReadOnlySet<string> mine, IEnumerable<HashSet<string>> settings)
	{
		HashSet<string> hashSet = new HashSet<string>();
		foreach (string item in from s in settings.Where((HashSet<string> s) => s.Overlaps(mine)).SelectMany((HashSet<string> s) => s)
			where !mine.Contains(s)
			select s)
		{
			hashSet.Add(item);
		}
		return hashSet;
	}

	public static HashSet<string> Foreign(string own, IEnumerable<string> others, bool every = false, int? anisotropy = null)
	{
		(List<PsoDb.Rec>, Dictionary<string, byte[]>) db = (File.Exists(own) ? Read(own) : (new List<PsoDb.Rec>(), new Dictionary<string, byte[]>()));
		List<(List<PsoDb.Rec>, Dictionary<string, byte[]>)> list = others.Where(File.Exists).Select(Read).ToList();
		List<(string, string)> list2 = Settings(db).ToList();
		if (list2.Count == 0)
		{
			if (!every)
			{
				object obj;
				if (anisotropy.HasValue)
				{
					int a = anisotropy.GetValueOrDefault();
					obj = MostCommon(list, (string s) => Anisotropy(s) == Math.Clamp(a, 1, 16));
				}
				else
				{
					obj = null;
				}
				if (obj == null)
				{
					obj = MostCommon(list);
				}
				string top = (string)obj;
				if (top != null)
				{
					list2 = (from x in list.SelectMany(Settings)
						where x.Samplers == top
						select x).ToList();
					goto IL_0117;
				}
			}
			return new HashSet<string>();
		}
		goto IL_0117;
		IL_0117:
		HashSet<string> sets = list2.Select(((string RootSignature, string Samplers) x) => x.Samplers).ToHashSet();
		HashSet<string> recorded = list2.Select(((string RootSignature, string Samplers) x) => x.RootSignature).ToHashSet();
		HashSet<string> other = (from x in list.SelectMany(Settings)
			where recorded.Contains(x.RootSignature) && !sets.Contains(x.Samplers)
			select x.Samplers).ToHashSet();
		if (other.Count == 0)
		{
			return new HashSet<string>();
		}
		HashSet<string> hashSet = db.Item1.Select((PsoDb.Rec r) => r.Key).ToHashSet();
		HashSet<string> hashSet2 = new HashSet<string>();
		foreach (var item2 in list)
		{
			List<PsoDb.Rec> item = item2.Item1;
			Dictionary<string, byte[]> blobs = item2.Item2;
			Dictionary<string, bool> at = new Dictionary<string, bool>();
			foreach (PsoDb.Rec item3 in item)
			{
				if (!hashSet.Contains(item3.Key) && (RootSignatures(item3).Any(Other) || (PsoDb.IsStateObject(item3.Tag) && Depends(item3).Any(hashSet2.Contains))))
				{
					hashSet2.Add(item3.Key);
				}
			}
			bool Other(string h)
			{
				if (at.TryGetValue(h, out var value))
				{
					return value;
				}
				Dictionary<string, bool> dictionary = at;
				byte[] valueOrDefault = blobs.GetValueOrDefault(h);
				int num;
				if (valueOrDefault != null)
				{
					(string, string)? tuple = Setting(valueOrDefault);
					if (tuple.HasValue)
					{
						(string, string) valueOrDefault2 = tuple.GetValueOrDefault();
						num = (other.Contains(valueOrDefault2.Item2) ? 1 : 0);
						goto IL_0059;
					}
				}
				num = 0;
				goto IL_0059;
				IL_0059:
				bool result = (byte)num != 0;
				dictionary[h] = (byte)num != 0;
				return result;
			}
		}
		return hashSet2;
	}

	private static IEnumerable<(string RootSignature, string Samplers)> Settings((List<PsoDb.Rec> Records, Dictionary<string, byte[]> Blobs) db)
	{
		return db.Records.SelectMany(RootSignatures).Distinct().Select((string h) =>
		{
			byte[] valueOrDefault = db.Blobs.GetValueOrDefault(h);
			return (valueOrDefault == null) ? (((string, string)?)null) : Setting(valueOrDefault);
		})
			.OfType<(string, string)>();
	}

	public static int Anisotropy(string samplers)
	{
		byte[] array = Convert.FromHexString(samplers);
		int val = 0;
		for (int i = 20; i + 4 <= array.Length; i += 52)
		{
			val = Math.Max(val, (int)Math.Min(BitConverter.ToUInt32(array, i), 16u));
		}
		return Math.Max(val, 1);
	}

	private static string? MostCommon(List<(List<PsoDb.Rec> Records, Dictionary<string, byte[]> Blobs)> dbs, Func<string, bool>? take = null)
	{
		HashSet<string> varied = (from x in dbs.SelectMany(Settings).Distinct()
			group x by x.RootSignature into g
			where g.Count() > 1
			select g.Key).ToHashSet();
		Dictionary<string, int> dictionary = new Dictionary<string, int>();
		foreach (var db in dbs)
		{
			List<PsoDb.Rec> item = db.Records;
			Dictionary<string, byte[]> blobs = db.Blobs;
			Dictionary<string, string?> of = new Dictionary<string, string>();
			foreach (PsoDb.Rec item2 in item)
			{
				foreach (string item3 in RootSignatures(item2).Select((string h) =>
				{
					if (of.TryGetValue(h, out string value))
					{
						return value;
					}
					Dictionary<string, string?> dictionary2 = of;
					byte[] valueOrDefault = blobs.GetValueOrDefault(h);
					object obj;
					if (valueOrDefault != null)
					{
						(string, string)? tuple = Setting(valueOrDefault);
						if (tuple.HasValue)
						{
							(string, string) valueOrDefault2 = tuple.GetValueOrDefault();
							if (varied.Contains(valueOrDefault2.Item1))
							{
								obj = valueOrDefault2.Item2;
								goto IL_0061;
							}
						}
					}
					obj = null;
					goto IL_0061;
					IL_0061:
					string result = (string)obj;
					dictionary2[h] = (string)obj;
					return result;
				}).OfType<string>().Distinct())
				{
					dictionary[item3] = dictionary.GetValueOrDefault(item3) + 1;
				}
			}
		}
		return (from c in (from c in dictionary
				where take?.Invoke(c.Key) ?? true
				orderby c.Value descending
				select c).ThenBy((KeyValuePair<string, int> c) => c.Key, StringComparer.Ordinal)
			select c.Key).FirstOrDefault();
	}

	private static (List<PsoDb.Rec> Records, Dictionary<string, byte[]> Blobs) Read(string db)
	{
		List<PsoDb.Rec> list = new List<PsoDb.Rec>();
		Dictionary<string, byte[]> dictionary = new Dictionary<string, byte[]>();
		foreach (PsoDb.Rec item in PsoDb.Read(db))
		{
			if (item.Tag == 'B')
			{
				if (item.Payload.Length > 20 && Dxbc.IsRootSignatureOnly(item.Payload.AsSpan(20)))
				{
					dictionary[PsoDb.Hex(item.Payload.AsSpan(0, 20))] = item.Payload[20..];
				}
				continue;
			}
			char tag = item.Tag;
			bool flag = ((tag == 'C' || tag == 'G' || tag == 'S') ? true : false);
			if (flag || PsoDb.IsStateObject(item.Tag))
			{
				list.Add(item);
			}
		}
		return (Records: list, Blobs: dictionary);
	}

	private static IEnumerable<string> RootSignatures(PsoDb.Rec r)
	{
		try
		{
			List<string> list;
			if (!PsoDb.IsStateObject(r.Tag))
			{
				int num = 1;
				list = new List<string>(num);
				CollectionsMarshal.SetCount(list, num);
				CollectionsMarshal.AsSpan(list)[0] = PsoDb.Parse(r).Rs;
			}
			else
			{
				list = PsoDb.ParseStateObject(r).RootSignatures;
			}
			return list;
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is KeyNotFoundException || ex is IndexOutOfRangeException) ? true : false)
		{
			return Array.Empty<string>();
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
}
