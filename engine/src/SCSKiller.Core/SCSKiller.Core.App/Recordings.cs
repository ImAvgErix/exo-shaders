using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

public static class Recordings
{
	public sealed class IncompleteLayerList : IOException
	{
		public IncompleteLayerList(string message)
			: base(message)
		{
		}
	}

	public const string KeysFile = "scskiller.keys";

	public const string WarmedFile = "scskiller.warmed";

	public static readonly string[] AppFiles = new string[4] { "scskiller.keys", "scskiller.warmed", "scskiller.keys.tmp", "scskiller.warmed.tmp" };

	public const string FailedFile = "scskiller_failed.keys";

	internal static Action? BeforeKeysPublished;

	private static readonly object DiskGate = new object();

	private static (string Fingerprint, HashSet<string> Keys)? onDisk;

	private static readonly Dictionary<string, (string? Stamp, string[] Inboxes)> InboxesOf = new Dictionary<string, (string, string[])>();

	private static ReadOnlySpan<byte> WarmedMagic => "SCSKWRM1"u8;

	private static ReadOnlySpan<byte> KeysMagic => "SCSKKEY1"u8;

	public static byte[] Warmed(IEnumerable<string> dbs, IEnumerable<string> failed, byte[]? previous = null)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (string item in failed.Where(File.Exists))
		{
			foreach (byte[] item2 in from c in File.ReadAllBytes(item).Chunk(20)
				where c.Length == 20
				select c)
			{
				hashSet.Add(Convert.ToHexStringLower(item2));
			}
		}
		SortedSet<string> sortedSet = new SortedSet<string>(StringComparer.Ordinal);
		SortedSet<string> sortedSet2 = new SortedSet<string>(StringComparer.Ordinal);
		Dictionary<string, PsoDb.Rec> dictionary = new Dictionary<string, PsoDb.Rec>();
		List<byte[]> list = new List<byte[]>();
		if (previous != null && previous.Length >= 12)
		{
			if (((ReadOnlySpan<byte>)previous.AsSpan(0, 8)).SequenceEqual(WarmedMagic))
			{
				uint num = BitConverter.ToUInt32(previous, 8);
				if (12 + 20L * (long)num <= previous.Length)
				{
					List<string> source = (from c in previous[12..].Chunk(20)
						where c.Length == 20
						select c).Select(Convert.ToHexStringLower).ToList();
					sortedSet.UnionWith(source.Take((int)num));
					sortedSet2.UnionWith(source.Skip((int)num));
				}
			}
		}
		foreach (string item3 in dbs.Where(File.Exists))
		{
			foreach (PsoDb.Rec item4 in PsoDb.Read(item3))
			{
				char tag = item4.Tag;
				if ((tag == 'C' || tag == 'G' || tag == 'S') ? true : false)
				{
					dictionary[item4.Key] = item4;
				}
				if (hashSet.Contains(item4.Key))
				{
					continue;
				}
				tag = item4.Tag;
				if ((tag == 'C' || tag == 'G' || tag == 'S') ? true : false)
				{
					sortedSet.Add(item4.Key);
					PsoDb.Pso pso = TryParse(item4);
					if ((object)pso != null)
					{
						sortedSet2.Add(TupleHash(pso.Rs, pso.Stages));
					}
				}
				else if (PsoDb.IsStateObject(item4.Tag))
				{
					sortedSet.Add(item4.Key);
				}
				else if (item4.Tag == 'P')
				{
					list.Add(item4.Payload);
				}
			}
		}
		foreach (byte[] item5 in list)
		{
			var (key, text, sortedDictionary) = PsoDb.ParseItem(item5);
			if (!dictionary.TryGetValue(key, out var value))
			{
				continue;
			}
			PsoDb.Pso pso2 = TryParse(value);
			if ((object)pso2 == null)
			{
				continue;
			}
			SortedDictionary<int, string> sortedDictionary2 = new SortedDictionary<int, string>(pso2.Stages);
			foreach (var (num3, text3) in sortedDictionary)
			{
				if (text3 != PsoDb.Zero && (sortedDictionary2.ContainsKey(num3) || (value.Tag == 'G' && num3 >= 1 && num3 <= 5)))
				{
					sortedDictionary2[num3] = text3;
				}
			}
			sortedSet2.Add(TupleHash((text != PsoDb.Zero) ? text : pso2.Rs, sortedDictionary2));
		}
		return WarmedBytes(sortedSet, sortedSet2);
		static PsoDb.Pso? TryParse(PsoDb.Rec r)
		{
			try
			{
				return PsoDb.Parse(r);
			}
			catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is KeyNotFoundException || ex is IndexOutOfRangeException || ex is OverflowException) ? true : false)
			{
				return null;
			}
		}
	}

	private static byte[] WarmedBytes(SortedSet<string> keys, SortedSet<string> tuples)
	{
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.Write(WarmedMagic);
		memoryStream.Write(BitConverter.GetBytes((uint)keys.Count));
		foreach (string item in keys.Concat(tuples))
		{
			memoryStream.Write(Convert.FromHexString(item));
		}
		return memoryStream.ToArray();
	}

	public static byte[] UnionWarmed(IEnumerable<string> files)
	{
		SortedSet<string> sortedSet = new SortedSet<string>(StringComparer.Ordinal);
		SortedSet<string> sortedSet2 = new SortedSet<string>(StringComparer.Ordinal);
		foreach (string file in files)
		{
			byte[] array = File.ReadAllBytes(file);
			if (array.Length >= 12 && ((ReadOnlySpan<byte>)array.AsSpan(0, 8)).SequenceEqual(WarmedMagic) && (array.Length - 12) % 20 == 0 && BitConverter.ToUInt32(array, 8) <= (array.Length - 12) / 20)
			{
				uint num = BitConverter.ToUInt32(array, 8);
				for (int i = 0; 12 + 20 * i < array.Length; i++)
				{
					((i < num) ? sortedSet : sortedSet2).Add(PsoDb.Hex(array.AsSpan(12 + 20 * i, 20)));
				}
			}
		}
		return WarmedBytes(sortedSet, sortedSet2);
	}

	public static string TupleHash(string rs, IReadOnlyDictionary<int, string> stages)
	{
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.Write(Convert.FromHexString(rs));
		foreach (var (num2, s) in stages.OrderBy((KeyValuePair<int, string> x) => x.Key).ThenBy((KeyValuePair<int, string> x) => x.Value, StringComparer.Ordinal))
		{
			memoryStream.WriteByte((byte)num2);
			memoryStream.Write(Convert.FromHexString(s));
		}
		return PsoDb.Hex(SHA1.HashData(memoryStream.ToArray()));
	}

	public static IDisposable Lock(string store, TimeSpan? wait = null, CancellationToken ct = default(CancellationToken))
	{
		AppStore.PathGate result = new AppStore.PathGate(store, wait, ct);
		string directoryName = Path.GetDirectoryName(Path.GetFullPath(store));
		DateTime dateTime = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
		try
		{
			if (Directory.Exists(directoryName))
			{
				foreach (FileInfo item in new DirectoryInfo(directoryName).EnumerateFiles(Path.GetFileName(store) + ".*tmp"))
				{
					try
					{
						if (item.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1.0) || item.LastWriteTimeUtc < dateTime)
						{
							item.Delete();
						}
					}
					catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
					{
					}
				}
			}
		}
		catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
		{
		}
		return result;
	}

	public static HashSet<string> Merge(string store, string? inbox, Func<string, bool>? shipped)
	{
		bool nvAdded;
		return Merge(store, inbox, shipped, out nvAdded);
	}

	public static HashSet<string> Merge(string store, string? inbox, Func<string, bool>? shipped, out bool nvAdded)
	{
		bool hasStore = File.Exists(store);
		bool hasInbox = inbox != null && File.Exists(inbox);
		bool nv = false;
		HashSet<string> named = ((shipped == null) ? new HashSet<string>() : Rehydrate.References(from r in Sources()
			where r.Tag != 'B'
			select r));
		HashSet<string> seen = new HashSet<string>();
		HashSet<string> added = new HashSet<string>();
		PsoDb.WriteCompact(store, from r in Union()
			where !Dropped(r)
			select r);
		KeyFiles.Forget(store);
		nvAdded = nv;
		return added;
		bool Dropped(PsoDb.Rec r)
		{
			if (r.Tag == 'B' && shipped != null)
			{
				string text = PsoDb.Hex(r.Payload.AsSpan(0, 20));
				if (named.Contains(text) && shipped(text))
				{
					return !Dxbc.IsRootSignatureOnly(r.Payload.AsSpan(20));
				}
			}
			return false;
		}
		IEnumerable<PsoDb.Rec> Sources()
		{
			IEnumerable<PsoDb.Rec> first;
			if (!hasStore)
			{
				IEnumerable<PsoDb.Rec> enumerable = Array.Empty<PsoDb.Rec>();
				first = enumerable;
			}
			else
			{
				first = PsoDb.Read(store);
			}
			IEnumerable<PsoDb.Rec> second;
			if (!hasInbox)
			{
				IEnumerable<PsoDb.Rec> enumerable = Array.Empty<PsoDb.Rec>();
				second = enumerable;
			}
			else
			{
				second = PsoDb.Read(inbox);
			}
			return first.Concat(second);
		}
		IEnumerable<PsoDb.Rec> Union()
		{
			if (hasStore)
			{
				foreach (PsoDb.Rec item2 in PsoDb.Read(store))
				{
					if (seen.Add(Id(item2)))
					{
						yield return item2;
					}
				}
			}
			if (hasInbox)
			{
				foreach (PsoDb.Rec item3 in PsoDb.Read(inbox))
				{
					string item = Id(item3);
					if (seen.Add(item))
					{
						if (item3.Tag == 'N')
						{
							nv = true;
						}
						else
						{
							char tag = item3.Tag;
							if ((tag != 'B' && tag != 'W') || 1 == 0)
							{
								added.Add(item);
							}
						}
						yield return item3;
					}
				}
			}
		}
	}

	private static string Id(PsoDb.Rec r)
	{
		if (r.Tag != 'B')
		{
			return r.Key;
		}
		return "B" + PsoDb.Hex(r.Payload.AsSpan(0, 20));
	}

	public static int WriteKeys(string store, IReadOnlySet<string>? shipped, string path, bool nameShipped = true, Func<bool>? publish = null)
	{
		IEnumerable<string> collection;
		if (!nameShipped || shipped == null)
		{
			IEnumerable<string> enumerable = Array.Empty<string>();
			collection = enumerable;
		}
		else
		{
			IEnumerable<string> enumerable = shipped;
			collection = enumerable;
		}
		HashSet<string> blobs = new HashSet<string>(collection);
		bool stored = File.Exists(store);
		if (stored)
		{
			foreach (PsoDb.Rec item in PsoDb.Read(store))
			{
				if (item.Tag == 'B')
				{
					blobs.Add(PsoDb.Hex(item.Payload.AsSpan(0, 20)));
				}
			}
		}
		if (publish != null && !publish())
		{
			return Records().Count((PsoDb.Rec r) => !Kept(r));
		}
		int num = 0;
		int num2 = 0;
		string text = path + ".tmp";
		try
		{
			using (BufferedStream bufferedStream = new BufferedStream(File.Create(text), 65536))
			{
				bufferedStream.Write(KeysMagic);
				foreach (string item2 in blobs)
				{
					bufferedStream.Write(Convert.FromHexString(item2));
				}
				foreach (PsoDb.Rec item3 in Records())
				{
					if (!Kept(item3))
					{
						num2++;
						continue;
					}
					bufferedStream.Write(Convert.FromHexString(item3.Key));
					num++;
				}
			}
			if (blobs.Count + num == 0)
			{
				if (publish == null || publish())
				{
					File.Delete(path);
				}
			}
			else
			{
				BeforeKeysPublished?.Invoke();
				if (publish == null || publish())
				{
					File.Move(text, path, overwrite: true);
				}
			}
		}
		finally
		{
			File.Delete(text);
		}
		return num2;
		bool Kept(PsoDb.Rec r)
		{
			if (r.Tag != 'N')
			{
				return Rehydrate.References(new _003C_003Ez__ReadOnlySingleElementList<PsoDb.Rec>(r)).All((string h) => blobs.Contains(h) || (shipped?.Contains(h) ?? false));
			}
			return true;
		}
		IEnumerable<PsoDb.Rec> Records()
		{
			if (!stored)
			{
				return Array.Empty<PsoDb.Rec>();
			}
			return from r in PsoDb.Read(store)
				where r.Tag != 'B'
				select r;
		}
	}

	public static (int Changed, int Own) Layered(string store)
	{
		IReadOnlySet<string> readOnlySet = Pairs(store);
		int num = readOnlySet.Count((string p) => p.EndsWith(PsoDb.Zero, StringComparison.Ordinal));
		return (Changed: readOnlySet.Count - num, Own: num);
	}

	public static HashSet<string> LayerMade(string store)
	{
		HashSet<string> hashSet = new HashSet<string>();
		foreach (string item in from p in Pairs(store)
			select p.Substring(0, 40))
		{
			hashSet.Add(item);
		}
		return hashSet;
	}

	private static IReadOnlySet<string> Pairs(string store)
	{
		return KeyFiles.Keys(store, (string p) =>
		{
			using FileStream fileStream = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			byte[] array = new byte[8];
			int num = fileStream.ReadAtLeast(array, 8, throwOnEndOfStream: false);
			fileStream.Position = 0L;
			if (num > 0 && array[0] == 0 && !((ReadOnlySpan<byte>)array.AsSpan(0, num)).SequenceEqual((ReadOnlySpan<byte>)new byte[8] { 0, 83, 67, 83, 75, 82, 69, 67 }))
			{
				throw new IncompleteLayerList(p + ": a damaged compact header");
			}
			List<PsoDb.Rec> source;
			try
			{
				source = ((num > 0 && array[0] == 0) ? PsoDb.Read(p) : Whole(new BufferedStream(fileStream, 1048576), fileStream.Length, p)).Where((PsoDb.Rec r) => r.Tag == 'W').ToList();
			}
			catch (InvalidDataException ex)
			{
				throw new IncompleteLayerList(p + ": " + ex.Message);
			}
			int num2 = source.Select((PsoDb.Rec r) => r.Payload.Length).FirstOrDefault((int n) => n != 40, 40);
			if (num2 != 40)
			{
				throw new IncompleteLayerList($"{p}: a 'W' record of {num2} bytes");
			}
			HashSet<string> hashSet = new HashSet<string>();
			foreach (string item in source.Select((PsoDb.Rec r) => PsoDb.Hex(r.Payload)))
			{
				hashSet.Add(item);
			}
			return hashSet;
		}, "layer", failOpen: false);
	}

	private static IEnumerable<PsoDb.Rec> Whole(Stream f, long length, string path)
	{
		byte[] head = new byte[5];
		long at = 0L;
		int num;
		while ((num = f.ReadAtLeast(head, 5, throwOnEndOfStream: false)) > 0)
		{
			uint num2 = BitConverter.ToUInt32(head, 1);
			if (num < 5 || num2 > length - at - 5)
			{
				throw new IncompleteLayerList($"{path}: a torn tail at byte {at}");
			}
			byte[] array = new byte[num2];
			f.ReadExactly(array);
			at += 5 + num2;
			yield return new PsoDb.Rec((char)head[0], array);
		}
	}

	public static HashSet<string> LayerMadeOnDisk(string dataDir)
	{
		DirectoryInfo directoryInfo = new DirectoryInfo(Path.Combine(dataDir, "games"));
		List<DirectoryInfo> list;
		try
		{
			list = directoryInfo.EnumerateDirectories().ToList();
		}
		catch (DirectoryNotFoundException)
		{
			return new HashSet<string>();
		}
		List<string> sources = new List<string>();
		StringBuilder fingerprint = new StringBuilder();
		foreach (DirectoryInfo item in list)
		{
			foreach (FileInfo item2 in item.EnumerateFiles())
			{
				if (item2.Name.Equals("recording.db", StringComparison.OrdinalIgnoreCase))
				{
					Add(item2.FullName, item2.Length, item2.LastWriteTimeUtc.Ticks);
				}
				else
				{
					if (!item2.Name.Equals("state.json", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					fingerprint.Append(item2.FullName).Append('|').Append(KeyFiles.Stamp(item2.FullName, strict: true))
						.Append('\n');
					string[] array = Inboxes(item2);
					foreach (string path in array)
					{
						(long, long)? tuple = Stat(path);
						if (tuple.HasValue)
						{
							(long, long) valueOrDefault = tuple.GetValueOrDefault();
							Add(path, valueOrDefault.Item1, valueOrDefault.Item2);
						}
					}
				}
			}
		}
		string text = fingerprint.ToString();
		HashSet<string> hashSet;
		lock (DiskGate)
		{
			(string, HashSet<string>)? tuple2 = onDisk;
			if (tuple2.HasValue)
			{
				(string, HashSet<string>) valueOrDefault2 = tuple2.GetValueOrDefault();
				if (valueOrDefault2.Item1 == text)
				{
					hashSet = new HashSet<string>();
					foreach (string item3 in valueOrDefault2.Item2)
					{
						hashSet.Add(item3);
					}
					return hashSet;
				}
			}
		}
		HashSet<string> hashSet2 = new HashSet<string>();
		foreach (string item4 in sources)
		{
			hashSet2.UnionWith(LayerMade(item4));
		}
		lock (DiskGate)
		{
			onDisk = (text, hashSet2);
		}
		hashSet = new HashSet<string>();
		foreach (string item5 in hashSet2)
		{
			hashSet.Add(item5);
		}
		return hashSet;
		void Add(string text2, long length, long written)
		{
			sources.Add(text2);
			fingerprint.Append(text2).Append('|').Append(length)
				.Append('|')
				.Append(written)
				.Append('|')
				.Append(KeyFiles.Stamp(text2, strict: true))
				.Append('\n');
		}
	}

	private static string[] Inboxes(FileInfo state)
	{
		string text = KeyFiles.Stamp(state.FullName, strict: true);
		lock (DiskGate)
		{
			if (InboxesOf.TryGetValue(state.FullName, out (string, string[]) value) && value.Item1 == text)
			{
				return value.Item2;
			}
		}
		JsonDocument doc = JsonDocument.Parse(File.ReadAllText(state.FullName));
		try
		{
			string[] array = (from p in new string[3] { "RecorderExe", "RecorderMoveFrom", "RecorderMoveTo" }.Select((string n) => (!doc.RootElement.TryGetProperty(n, out var value2)) ? null : (value2.ValueKind switch
				{
					JsonValueKind.String => value2.GetString(), 
					JsonValueKind.Null => null, 
					_ => throw new InvalidDataException($"{state.FullName}: {n} is a {value2.ValueKind}, not a path"), 
				})).OfType<string>()
				where p.Length > 0
				select Path.Combine(Path.GetDirectoryName(p), "scskiller.db")).ToArray();
			lock (DiskGate)
			{
				InboxesOf[state.FullName] = (text, array);
			}
			return array;
		}
		finally
		{
			if (doc != null)
			{
				((IDisposable)doc).Dispose();
			}
		}
	}

	private static (long Length, long Written)? Stat(string path)
	{
		try
		{
			using SafeFileHandle safeFileHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
			return (RandomAccess.GetLength(safeFileHandle), File.GetLastWriteTimeUtc(safeFileHandle).Ticks);
		}
		catch (Exception ex) when ((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? true : false)
		{
			return null;
		}
	}

	public static bool Rotate(string inbox, long imported)
	{
		try
		{
			using FileStream fileStream = new FileStream(inbox, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
			if (fileStream.Length != imported)
			{
				return false;
			}
			fileStream.SetLength(0L);
			return true;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return false;
		}
	}
}
