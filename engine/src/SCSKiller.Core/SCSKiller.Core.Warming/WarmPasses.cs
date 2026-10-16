using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Warming;

public sealed class WarmPasses
{
	public const string FileName = "scskiller_pass.bin";

	public const int MaxPasses = 32;

	public const byte FastPass = byte.MaxValue;

	private readonly byte[] _numbers;

	private readonly long[][] _items;

	private readonly long[] _before;

	public int Count => _items.Length;

	public long Total { get; }

	public int CarefulCount => _numbers.Count((byte n) => n != byte.MaxValue);

	private WarmPasses(byte[] passOf)
	{
		_numbers = passOf.Distinct().Order().ToArray();
		int[] array = new int[256];
		for (int i = 0; i < _numbers.Length; i++)
		{
			array[_numbers[i]] = i;
		}
		List<long>[] array2 = _numbers.Select((byte _) => new List<long>()).ToArray();
		for (long num = 0L; num < passOf.Length; num++)
		{
			array2[array[passOf[num]]].Add(num);
		}
		_items = array2.Select((List<long> l) => l.ToArray()).ToArray();
		_before = new long[_numbers.Length];
		for (int num2 = 1; num2 < _numbers.Length; num2++)
		{
			_before[num2] = _before[num2 - 1] + _items[num2 - 1].Length;
		}
		Total = passOf.Length;
	}

	public IReadOnlyList<long> ItemsOf(int pass)
	{
		return _items[pass];
	}

	public byte Number(int pass)
	{
		return _numbers[pass];
	}

	public bool IsFast(int pass)
	{
		return _numbers[pass] == byte.MaxValue;
	}

	public static byte[] Split(IReadOnlyList<string?> sets, long recorded)
	{
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
		byte[] array = new byte[sets.Count];
		for (int i = 0; i < sets.Count; i++)
		{
			if (i >= recorded)
			{
				array[i] = byte.MaxValue;
				continue;
			}
			string text = sets[i];
			int num = ((text != null) ? dictionary.GetValueOrDefault(text) : 0);
			string text2 = sets[i];
			if (text2 != null)
			{
				dictionary[text2] = num + 1;
			}
			array[i] = (byte)(1 + num % 32);
		}
		return array;
	}

	public static int Write(string workDir)
	{
		string path = Path.Combine(workDir, "scskiller_pass.bin");
		File.Delete(path);
		var (sets, num) = ItemSets(workDir);
		if (num == 0L)
		{
			return 0;
		}
		byte[] array = Split(sets, num);
		File.WriteAllBytes(path, array);
		return array.Where((byte p) => p != byte.MaxValue).Distinct().Count();
	}

	public static WarmPasses? Read(string workDir)
	{
		string path = Path.Combine(workDir, "scskiller_pass.bin");
		if (File.Exists(path))
		{
			byte[] array = File.ReadAllBytes(path);
			if (array != null && array.Length > 0)
			{
				return new WarmPasses(array);
			}
		}
		return null;
	}

	public long Overall(int pass, long done)
	{
		int num = Array.BinarySearch(_items[pass], done);
		return _before[pass] + ((num >= 0) ? num : (~num));
	}

	public (int Pass, long Start) Locate(long overall)
	{
		for (int i = 0; i < Count; i++)
		{
			if (overall < _before[i] + _items[i].Length)
			{
				return (Pass: i, Start: _items[i][overall - _before[i]]);
			}
		}
		return (Pass: Count - 1, Start: Total);
	}

	public static (List<string?> Sets, long Recorded) ItemSets(string workDir)
	{
		List<(char, byte[])> list = new List<(char, byte[])>();
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
		List<byte[]> list2 = new List<byte[]>();
		long num = 0L;
		long item = 0L;
		string[] array = new string[2] { "scskiller.db", "scskiller_gen.db" };
		int i;
		for (i = 0; i < array.Length; i++)
		{
			string text = array[i];
			if (text == "scskiller_gen.db")
			{
				item = list.Count;
			}
			foreach (var (c, array2) in Records(Path.Combine(workDir, text)))
			{
				switch (c)
				{
				case '1':
				case '2':
					num++;
					continue;
				case 'P':
					list2.Add(array2);
					continue;
				case 'B':
				case 'N':
				case 'W':
					continue;
				}
				if (dictionary.TryAdd(Key(c, array2), list.Count))
				{
					list.Add((c, array2));
				}
			}
		}
		List<string> list3 = new List<string>(list.Count + list2.Count + (int)num);
		foreach (var (tag, payload) in list)
		{
			list3.Add(PsoDb.IsStateObject(tag) ? null : SetOf(Stages(tag, payload)));
		}
		foreach (byte[] item4 in list2)
		{
			SortedDictionary<int, string> sortedDictionary;
			try
			{
				(string Template, string Rs, SortedDictionary<int, string> Stages) tuple3 = PsoDb.ParseItem(item4);
				string item2 = tuple3.Template;
				SortedDictionary<int, string> item3 = tuple3.Stages;
				sortedDictionary = (dictionary.TryGetValue(item2, out var value) ? (Stages(list[value].Item1, list[value].Item2) ?? new SortedDictionary<int, string>()) : new SortedDictionary<int, string>());
				foreach (KeyValuePair<int, string> item5 in item3)
				{
					item5.Deconstruct(out i, out var value2);
					int key = i;
					string text2 = value2;
					if (text2 != PsoDb.Zero)
					{
						sortedDictionary[key] = text2;
					}
				}
			}
			catch (Exception ex) when ((ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
			{
				sortedDictionary = null;
			}
			list3.Add(SetOf(sortedDictionary));
		}
		for (long num2 = 0L; num2 < num; num2++)
		{
			list3.Add(null);
		}
		return (Sets: list3, Recorded: item);
	}

	private static SortedDictionary<int, string>? Stages(char tag, byte[] payload)
	{
		try
		{
			return new SortedDictionary<int, string>(PsoDb.Parse(new PsoDb.Rec(tag, payload)).Stages);
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException || ex is KeyNotFoundException || ex is OverflowException) ? true : false)
		{
			return null;
		}
	}

	private static string? SetOf(SortedDictionary<int, string>? stages)
	{
		if (stages == null || stages.Count <= 0)
		{
			return null;
		}
		return string.Join(',', stages.Select((KeyValuePair<int, string> x) => $"{x.Key}:{x.Value}"));
	}

	private static string Key(char tag, byte[] payload)
	{
		byte b = (byte)tag;
		int num = 0;
		byte[] array = new byte[1 + payload.Length];
		array[num] = b;
		num++;
		ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(payload);
		readOnlySpan.CopyTo(new Span<byte>(array).Slice(num, readOnlySpan.Length));
		num += readOnlySpan.Length;
		return PsoDb.Hex(SHA1.HashData(new ReadOnlySpan<byte>(array)));
	}

	private static IEnumerable<(char Tag, byte[]? Payload)> Records(string path)
	{
		if (!File.Exists(path))
		{
			yield break;
		}
		using BufferedStream f = new BufferedStream(File.OpenRead(path), 1048576);
		byte[] head = new byte[5];
		while (f.ReadAtLeast(head, 5, throwOnEndOfStream: false) == 5)
		{
			uint num = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1));
			if (head[0] == 66)
			{
				if (f.Position + num > f.Length)
				{
					break;
				}
				f.Seek(num, SeekOrigin.Current);
				yield return (Tag: 'B', Payload: null);
			}
			else
			{
				byte[] array = new byte[num];
				if (f.ReadAtLeast(array, array.Length, throwOnEndOfStream: false) < array.Length)
				{
					break;
				}
				yield return (Tag: (char)head[0], Payload: array);
			}
		}
	}
}
