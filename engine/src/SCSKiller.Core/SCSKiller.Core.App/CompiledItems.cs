using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

public static class CompiledItems
{
	public const string File = "compiled.keys";

	private static ReadOnlySpan<byte> Magic => "SCSKCMP1"u8;

	private static bool IsItem(char tag)
	{
		bool flag;
		switch (tag)
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
			return PsoDb.IsStateObject(tag);
		}
		return true;
	}

	private static Dictionary<string, string> LastNv(IEnumerable<PsoDb.Rec> records)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>();
		foreach (PsoDb.Rec record in records)
		{
			if (record.Tag == 'N' && record.Payload.Length == 36)
			{
				dictionary[PsoDb.NvState.Parse(record).Target] = record.Key;
			}
		}
		return dictionary;
	}

	public static HashSet<string> Created(IReadOnlyList<string> dbs, IEnumerable<string> failed)
	{
		HashSet<string> notCreated = new HashSet<string>(StringComparer.Ordinal);
		foreach (string item in failed.Where(System.IO.File.Exists))
		{
			foreach (byte[] item2 in from c in System.IO.File.ReadAllBytes(item).Chunk(20)
				where c.Length == 20
				select c)
			{
				notCreated.Add(Convert.ToHexStringLower(item2));
			}
		}
		List<string> source = dbs.Where(System.IO.File.Exists).ToList();
		Dictionary<string, string> nv = LastNv(source.SelectMany((string db) => from r in PsoDb.Read(db)
			where r.Tag == 'N'
			select r));
		return (from r in source.SelectMany((string db) => from r in PsoDb.Read(db)
				where r.Tag != 'B'
				select r)
			where IsItem(r.Tag) && !notCreated.Contains(r.Key)
			select nv.GetValueOrDefault(r.Key, r.Key)).ToHashSet();
	}

	public static HashSet<string>? Load(string path)
	{
		try
		{
			byte[] array = System.IO.File.ReadAllBytes(path);
			if (array.Length < Magic.Length || !((ReadOnlySpan<byte>)array.AsSpan(0, Magic.Length)).SequenceEqual(Magic) || (array.Length - Magic.Length) % 20 != 0)
			{
				return null;
			}
			return array[Magic.Length..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public static byte[] Bytes(IEnumerable<string> keys)
	{
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.Write(Magic);
		foreach (string item in keys.Order(StringComparer.Ordinal))
		{
			memoryStream.Write(Convert.FromHexString(item));
		}
		return memoryStream.ToArray();
	}

	public static long LeaveOut(IReadOnlyList<string> dbs, IReadOnlySet<string> compiled)
	{
		List<string> list = dbs.Where(System.IO.File.Exists).ToList();
		List<(string, bool, List<string>)> list2 = new List<(string, bool, List<string>)>();
		Dictionary<string, string> dictionary = LastNv(list.SelectMany((string db) => from r in PsoDb.Read(db)
			where r.Tag == 'N'
			select r));
		foreach (PsoDb.Rec item in list.SelectMany((string db) => PsoDb.Read(db)))
		{
			if (IsItem(item.Tag))
			{
				List<string> list3 = Needs(item);
				list2.Add((item.Key, list3 != null && compiled.Contains(dictionary.GetValueOrDefault(item.Key, item.Key)), list3 ?? new List<string>()));
			}
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		HashSet<string> drop = new HashSet<string>(StringComparer.Ordinal);
		for (int num = list2.Count - 1; num >= 0; num--)
		{
			if (list2[num].Item2 && !hashSet.Contains(list2[num].Item1))
			{
				drop.Add(list2[num].Item1);
			}
			else
			{
				hashSet.UnionWith(list2[num].Item3);
			}
		}
		if (drop.Count == 0)
		{
			return 0L;
		}
		foreach (string item2 in list)
		{
			string text = item2 + ".tmp";
			try
			{
				using (BufferedStream s = new BufferedStream(System.IO.File.Create(text), 1048576))
				{
					foreach (PsoDb.Rec item3 in PsoDb.Read(item2))
					{
						if (!IsItem(item3.Tag) || !drop.Contains(item3.Key))
						{
							PsoDb.Write(s, item3.Tag, item3.Payload);
						}
					}
				}
				System.IO.File.Move(text, item2, overwrite: true);
			}
			finally
			{
				System.IO.File.Delete(text);
			}
		}
		return list2.Count(((string Key, bool Compiled, List<string> Needs) x) => drop.Contains(x.Key));
	}

	private static List<string>? Needs(PsoDb.Rec r)
	{
		try
		{
			object obj;
			if (r.Tag != 'P')
			{
				obj = (PsoDb.IsStateObject(r.Tag) ? PsoDb.ParseStateObject(r).Depends : new List<string>());
			}
			else
			{
				int num = 1;
				obj = new List<string>(num);
				CollectionsMarshal.SetCount((List<string>)obj, num);
				CollectionsMarshal.AsSpan((List<string>?)obj)[0] = PsoDb.ParseItem(r.Payload).Template;
			}
			return (List<string>?)obj;
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException || ex is OverflowException) ? true : false)
		{
			return null;
		}
	}
}
