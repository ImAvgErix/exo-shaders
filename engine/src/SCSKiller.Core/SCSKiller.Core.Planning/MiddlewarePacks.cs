using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Planning;

public sealed class MiddlewarePacks(string dir)
{
	public sealed record Promoted(MiddlewareDll Dll, string ContentHash, int Records, int New, string PackPath);

	public sealed record Seeded(MiddlewareDll Dll, string ContentHash, MiddlewarePack Pack);

	public sealed record MaterializeResult(int Entries, int Written, int AlreadyRecorded, int Skipped, IReadOnlyDictionary<string, int> SkippedByDll, int FilledFromDll = 0, IReadOnlySet<string>? DropFromMain = null);

	public string Dir { get; } = dir;

	private string LayerFile => Path.Combine(Dir, "layer-made.keys");

	public string PathOf(string vendor, string dll, string contentHash)
	{
		return Path.Combine(Dir, vendor, MiddlewarePack.FileName(dll, contentHash));
	}

	public MiddlewarePack? Load(MiddlewareDll dll, MiddlewareImage image)
	{
		string path = PathOf(dll.Vendor, dll.Name, image.ContentHash);
		try
		{
			return File.Exists(path) ? MiddlewarePack.Read(path) : null;
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is JsonException) ? true : false)
		{
			return null;
		}
	}

	public int Pipelines(MiddlewareDll dll)
	{
		return Keys(dll).Count();
	}

	public IEnumerable<string> Keys(MiddlewareDll dll, bool? amd = null)
	{
		return Seeds(dll, amd).Entries.Select((PsoDb.Rec e) => e.Key);
	}

	public (List<PsoDb.Rec> Entries, List<string> RootSignatures) Seeds(MiddlewareDll dll, bool? amd = null)
	{
		string path = Path.Combine(Dir, dll.Vendor);
		try
		{
			if (!dll.Packable || !Directory.Exists(path) || !Directory.EnumerateFiles(path, MiddlewarePack.FileName(dll.Name, "*")).Any())
			{
				return (Entries: new List<PsoDb.Rec>(), RootSignatures: new List<string>());
			}
			MiddlewareImage image = Middleware.Scan(dll.Path);
			MiddlewarePack middlewarePack = Load(dll, image);
			if (middlewarePack == null)
			{
				return (Entries: new List<PsoDb.Rec>(), RootSignatures: new List<string>());
			}
			List<PsoDb.Rec> list = middlewarePack.Entries.Where((PsoDb.Rec e) =>
			{
				if (amd.HasValue)
				{
					bool valueOrDefault = amd == true;
					return Runs(e, image, valueOrDefault);
				}
				return true;
			}).ToList();
			return (Entries: list, RootSignatures: list.Select((PsoDb.Rec e) => PsoDb.Parse(e).Rs).Where(middlewarePack.RootSignatures.ContainsKey).Distinct()
				.ToList());
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return (Entries: new List<PsoDb.Rec>(), RootSignatures: new List<string>());
		}
	}

	public static bool Runs(PsoDb.Rec entry, MiddlewareImage image, bool amd, Dictionary<string, bool>? memo = null)
	{
		PsoDb.Pso pso;
		try
		{
			pso = PsoDb.Parse(entry);
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is KeyNotFoundException) ? true : false)
		{
			return false;
		}
		return pso.Stages.Values.All((string h) =>
		{
			bool result = ((memo != null) ? ((!memo.TryGetValue(h, out var value)) ? (memo[h] = image.Runs(h, amd)) : value) : image.Runs(h, amd));
			return result;
		});
	}

	public static MiddlewarePack FromShared(List<PsoDb.Rec> records, MiddlewareDll dll, MiddlewareImage image, bool amd, string obj, out int dropped)
	{
		Dictionary<string, byte[]> dictionary = records.Where((PsoDb.Rec r) => r.Tag == 'B').ToDictionary((PsoDb.Rec r) => PsoDb.Hex(r.Payload.AsSpan(0, 20)), (PsoDb.Rec r) => r.Payload[20..]);
		MiddlewarePack middlewarePack = new MiddlewarePack(dll.Vendor, dll.Name, image.ContentHash, image.Size, obj);
		middlewarePack.Header.Sources.Add("community");
		dropped = 0;
		foreach (PsoDb.Rec item in records.Where((PsoDb.Rec r) => r.Tag != 'B'))
		{
			PsoDb.Pso pso = PsoDb.Parse(item);
			if (pso.Stages.Count == 0 || !pso.Stages.Values.All((string h) => image.Containers.ContainsKey(h) && image.Runs(h, amd)) || (pso.Rs != PsoDb.Zero && !image.Containers.ContainsKey(pso.Rs) && !dictionary.ContainsKey(pso.Rs)))
			{
				dropped++;
				continue;
			}
			if (dictionary.TryGetValue(pso.Rs, out var value))
			{
				middlewarePack.RootSignatures[pso.Rs] = value;
			}
			middlewarePack.Entries.Add(item);
		}
		return middlewarePack;
	}

	public static List<PsoDb.Rec> Records(MiddlewarePack pack, IReadOnlySet<string>? layered = null)
	{
		List<PsoDb.Rec> list = new List<PsoDb.Rec>();
		list.AddRange(pack.RootSignatures.Select((KeyValuePair<string, byte[]> r) =>
		{
			byte[] array = Convert.FromHexString(r.Key);
			byte[] value = r.Value;
			int num = 0;
			byte[] array2 = new byte[array.Length + value.Length];
			ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(array);
			readOnlySpan.CopyTo(new Span<byte>(array2).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			ReadOnlySpan<byte> readOnlySpan2 = new ReadOnlySpan<byte>(value);
			readOnlySpan2.CopyTo(new Span<byte>(array2).Slice(num, readOnlySpan2.Length));
			num += readOnlySpan2.Length;
			return new PsoDb.Rec('B', array2);
		}));
		list.AddRange(pack.Entries.Where((PsoDb.Rec e) =>
		{
			IReadOnlySet<string>? readOnlySet = layered;
			return readOnlySet == null || !readOnlySet.Contains(e.Key);
		}));
		HashOnly.Dropped dropped;
		return HashOnly.CheckPack(HashOnly.Canonical(new _003C_003Ez__ReadOnlyList<PsoDb.Rec>(list), local: false, out dropped));
	}

	private IDisposable Lock()
	{
		return Recordings.Lock(Path.Combine(Dir, "packs"));
	}

	public HashSet<string> LayerMade()
	{
		string[] array;
		try
		{
			array = File.ReadAllLines(LayerFile);
		}
		catch (Exception ex) when ((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? true : false)
		{
			return new HashSet<string>();
		}
		string text = array.FirstOrDefault((string l) => l.Length != 40 || !l.All(char.IsAsciiHexDigitLower));
		if (text != null)
		{
			throw new InvalidDataException(LayerFile + ": not a record key: " + text);
		}
		HashSet<string> hashSet = new HashSet<string>();
		string[] array2 = array;
		foreach (string item in array2)
		{
			hashSet.Add(item);
		}
		return hashSet;
	}

	public void Exclude(IEnumerable<string> keys)
	{
		using (Lock())
		{
			HashSet<string> hashSet = LayerMade();
			if (Persist(hashSet, keys))
			{
				PruneLocked(hashSet);
			}
		}
	}

	private bool Persist(HashSet<string> all, IEnumerable<string> keys)
	{
		int count = all.Count;
		all.UnionWith(keys);
		if (all.Count == count)
		{
			return false;
		}
		Directory.CreateDirectory(Dir);
		string text = $"{LayerFile}.{Guid.NewGuid():N}.tmp";
		File.WriteAllLines(text, all.Order(StringComparer.Ordinal));
		File.Move(text, LayerFile, overwrite: true);
		return true;
	}

	public List<Promoted> Promote(IEnumerable<PsoDb.Rec> records, IReadOnlyDictionary<string, byte[]> blobs, IReadOnlyDictionary<string, ShaderInfo> index, IEnumerable<MiddlewareDll> dlls, string source, string? gpu = null, IReadOnlySet<string>? layered = null)
	{
		using (Lock())
		{
			HashSet<string> hashSet;
			try
			{
				hashSet = LayerMade();
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) ? true : false)
			{
				return new List<Promoted>();
			}
			Persist(hashSet, layered ?? new HashSet<string>());
			PruneLocked(hashSet);
			bool? amd = ((gpu == null) ? ((bool?)null) : new bool?(gpu == "amd"));
			List<(PsoDb.Rec, PsoDb.Pso)> list = new List<(PsoDb.Rec, PsoDb.Pso)>();
			foreach (PsoDb.Rec record in records)
			{
				char tag = record.Tag;
				bool flag = ((tag == 'C' || tag == 'G' || tag == 'S') ? true : false);
				if (flag && !hashSet.Contains(record.Key))
				{
					PsoDb.Pso pso;
					try
					{
						pso = PsoDb.Parse(record);
					}
					catch (Exception ex2) when ((ex2 is InvalidDataException || ex2 is ArgumentException || ex2 is KeyNotFoundException) ? true : false)
					{
						continue;
					}
					if (pso.Stages.Count > 0 && !pso.Stages.Values.Any(index.ContainsKey))
					{
						list.Add((record, pso));
					}
				}
			}
			List<Promoted> list2 = new List<Promoted>();
			if (list.Count == 0)
			{
				return list2;
			}
			foreach (MiddlewareDll item in dlls.Where((MiddlewareDll d) => d.Packable))
			{
				MiddlewareImage image = Middleware.Scan(item.Path);
				List<(PsoDb.Rec, PsoDb.Pso)> list3 = list.Where(((PsoDb.Rec R, PsoDb.Pso P) c) => c.P.Stages.Values.All((string h) =>
				{
					if (image.Containers.ContainsKey(h))
					{
						if (amd.HasValue)
						{
							bool valueOrDefault = amd == true;
							return image.Runs(h, valueOrDefault);
						}
						return true;
					}
					return false;
				})).ToList();
				if (list3.Count == 0)
				{
					continue;
				}
				MiddlewarePack middlewarePack = Load(item, image);
				string text = PathOf(item.Vendor, item.Name, image.ContentHash);
				bool flag2 = false;
				if (gpu != null && middlewarePack != null && middlewarePack.Header.Gpu != gpu)
				{
					if (middlewarePack.Header.Gpu == null)
					{
						middlewarePack.Entries.RemoveAll((PsoDb.Rec e) => !PsoDb.Parse(e).Stages.Values.All((string h) => image.Runs(h, amd.Value)));
						middlewarePack.Adopt(gpu);
					}
					else
					{
						middlewarePack = null;
					}
					flag2 = true;
				}
				if (middlewarePack == null)
				{
					middlewarePack = new MiddlewarePack(item.Vendor, item.Name, image.ContentHash, image.Size, null, gpu);
				}
				int num = 0;
				foreach (var (r, pso2) in list3)
				{
					if (pso2.Rs != PsoDb.Zero && !image.Containers.ContainsKey(pso2.Rs))
					{
						if (!blobs.TryGetValue(pso2.Rs, out byte[] value) || !Dxbc.IsRootSignatureOnly(value))
						{
							continue;
						}
						middlewarePack.RootSignatures[pso2.Rs] = value;
					}
					if (middlewarePack.Add(r, source))
					{
						num++;
					}
				}
				if ((num > 0) | flag2)
				{
					middlewarePack.Write(text);
				}
				list2.Add(new Promoted(item, image.ContentHash, list3.Count, num, text));
			}
			return list2;
		}
	}

	public int Prune(IReadOnlySet<string> layered)
	{
		using (Lock())
		{
			return PruneLocked(layered);
		}
	}

	private int PruneLocked(IReadOnlySet<string> layered)
	{
		if (layered.Count == 0 || !Directory.Exists(Dir))
		{
			return 0;
		}
		int num = 0;
		foreach (string item in Directory.EnumerateFiles(Dir, "*.pack", SearchOption.AllDirectories).ToList())
		{
			MiddlewarePack middlewarePack;
			try
			{
				middlewarePack = MiddlewarePack.Read(item);
			}
			catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) ? true : false)
			{
				continue;
			}
			if (middlewarePack.Entries.RemoveAll((PsoDb.Rec e) => layered.Contains(e.Key)) == 0)
			{
				continue;
			}
			HashSet<string> named = middlewarePack.Entries.Select((PsoDb.Rec e) => PsoDb.Parse(e).Rs).ToHashSet();
			foreach (string item2 in middlewarePack.RootSignatures.Keys.Where((string h) => !named.Contains(h)).ToList())
			{
				middlewarePack.RootSignatures.Remove(item2);
			}
			middlewarePack.Write(item);
			num++;
		}
		return num;
	}

	public List<Seeded> Seed(IEnumerable<MiddlewareDll> dlls, out string fingerprint)
	{
		List<Seeded> list = new List<Seeded>();
		List<string> list2 = new List<string>();
		foreach (MiddlewareDll item in dlls.Where((MiddlewareDll d) => d.Packable))
		{
			if (!Directory.Exists(Path.Combine(Dir, item.Vendor)))
			{
				continue;
			}
			MiddlewareImage middlewareImage = Middleware.Scan(item.Path);
			list2.Add(Part(item, middlewareImage.ContentHash));
			MiddlewarePack middlewarePack = Load(item, middlewareImage);
			if (middlewarePack != null)
			{
				List<PsoDb.Rec> entries = middlewarePack.Entries;
				if (entries != null && entries.Count > 0)
				{
					list.Add(new Seeded(item, middlewareImage.ContentHash, middlewarePack));
				}
			}
		}
		fingerprint = string.Join('|', list2);
		return list;
	}

	public string Fingerprint(Game game)
	{
		return string.Join('|', from d in Middleware.Detect(game)
			where d.Packable && Directory.Exists(Path.Combine(Dir, d.Vendor))
			select Part(d, Middleware.Scan(d.Path).ContentHash));
	}

	private string Part(MiddlewareDll d, string hash)
	{
		string text = PathOf(d.Vendor, d.Name, hash);
		string text2 = $"{d.Name}:{hash}:{(File.Exists(text) ? new FileInfo(text).Length : 0)}";
		string text4;
		if (File.Exists(text))
		{
			string text3 = MiddlewarePack.ReadHeader(text)?.Object;
			if (text3 != null)
			{
				text4 = ":" + text3;
				goto IL_009f;
			}
		}
		text4 = "";
		goto IL_009f;
		IL_009f:
		return text2 + text4;
	}

	public static PsoDb.Rec Wrap(PsoDb.Rec entry, string dll, string contentHash)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(dll);
		List<byte> list = new List<byte>();
		list.Add((byte)entry.Tag);
		list.AddRange(Convert.FromHexString(contentHash));
		list.Add((byte)bytes.Length);
		list.AddRange(bytes);
		list.AddRange(entry.Payload);
		return new PsoDb.Rec('M', list.ToArray());
	}

	public static (string Dll, string ContentHash, PsoDb.Rec Entry) Unwrap(PsoDb.Rec m)
	{
		byte[] payload = m.Payload;
		byte b = payload[21];
		return (Dll: Encoding.UTF8.GetString(payload, 22, b), ContentHash: PsoDb.Hex(payload.AsSpan(1, 20)), Entry: new PsoDb.Rec((char)payload[0], payload[(22 + b)..]));
	}

	public static MaterializeResult Materialize(IEnumerable<PsoDb.Rec> mRecords, Game game, IReadOnlySet<string> planBlobs, ISet<string> written, IReadOnlySet<string> inMain, Stream gen)
	{
		List<MiddlewareDll> source = Middleware.Detect(game);
		Dictionary<string, MiddlewareImage> dictionary = new Dictionary<string, MiddlewareImage>(StringComparer.OrdinalIgnoreCase);
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		SortedDictionary<string, int> sortedDictionary = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> hashSet = new HashSet<string>();
		List<PsoDb.Rec> list = new List<PsoDb.Rec>();
		foreach (PsoDb.Rec mRecord in mRecords)
		{
			num++;
			(string, string, PsoDb.Rec) tuple = Unwrap(mRecord);
			string dll = tuple.Item1;
			PsoDb.Rec item = tuple.Item3;
			bool flag = inMain.Contains(item.Key);
			PsoDb.Pso pso = PsoDb.Parse(item);
			List<string> list2 = (from h in pso.Stages.Values.Append(pso.Rs)
				where h != PsoDb.Zero && !written.Contains(h) && !planBlobs.Contains(h)
				select h).Distinct().ToList();
			if (flag && list2.Count == 0)
			{
				num3++;
				continue;
			}
			if (!dictionary.TryGetValue(dll, out var value))
			{
				string key = dll;
				MiddlewareDll middlewareDll = source.FirstOrDefault((MiddlewareDll d) => d.Packable && d.Name.Equals(dll, StringComparison.OrdinalIgnoreCase));
				value = (dictionary[key] = (((object)middlewareDll != null) ? Middleware.TryScan(middlewareDll.Path) : null));
			}
			List<(string, byte[])> list3 = new List<(string, byte[])>();
			foreach (string item2 in list2)
			{
				byte[] array = value?.Read(item2);
				if (array != null)
				{
					list3.Add((item2, array));
					continue;
				}
				list3 = null;
				break;
			}
			if (list3 == null)
			{
				sortedDictionary[dll] = sortedDictionary.GetValueOrDefault(dll) + 1;
				if (flag)
				{
					hashSet.Add(item.Key);
				}
				continue;
			}
			foreach (var (text, array2) in list3)
			{
				if (written.Add(text))
				{
					PsoDb.WriteBlob(gen, text, array2);
				}
			}
			if (flag)
			{
				num3++;
				num4++;
			}
			else
			{
				list.Add(item);
				num2++;
			}
		}
		foreach (PsoDb.Rec item3 in list)
		{
			PsoDb.Write(gen, item3.Tag, item3.Payload);
		}
		return new MaterializeResult(num, num2, num3, sortedDictionary.Values.Sum(), sortedDictionary, num4, hashSet);
	}
}
