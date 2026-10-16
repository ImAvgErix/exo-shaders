using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.App;

public static class KeyFiles
{
	private sealed class Entry(string stamp, HashSet<string> keys)
	{
		public readonly string Stamp = stamp;

		public readonly HashSet<string> Keys = keys;

		public long Used;
	}

	private struct BasicInfo
	{
		public long Created;

		public long Accessed;

		public long Written;

		public long Changed;

		public uint Attributes;
	}

	private struct IdInfo
	{
		public ulong Volume;

		public ulong Low;

		public ulong High;
	}

	private const int Sample = 4096;

	private static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

	private static readonly Lock gate = new Lock();

	private static long count;

	private static long uses;

	private static long lastUse;

	private static readonly HashSet<string> Damaged = new HashSet<string>();

	internal static Action<string>? PruneListed;

	public static long MaxKeys { get; internal set; } = 250000L;

	public static long CachedKeys
	{
		get
		{
			using (gate.EnterScope())
			{
				return count;
			}
		}
	}

	public static HashSet<string> Keys(string path, Func<string, HashSet<string>> read, string? variant = null, bool failOpen = true)
	{
		string full = Path.GetFullPath(path);
		try
		{
			string text = Stamp(full, !failOpen);
			return (text != null) ? Cached((variant == null) ? full : (full + "|" + variant), text, () => read(full)) : new HashSet<string>();
		}
		catch (Exception ex) when (((Func<bool>)delegate
		{
			// Could not convert BlockContainer to single expression
			bool flag = failOpen;
			if (flag)
			{
				flag = ((ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) ? true : false);
			}
			return flag;
		}).Invoke())
		{
			return new HashSet<string>();
		}
	}

	public static HashSet<string> Derived(string name, IEnumerable<string?> inputs, string extra, Func<HashSet<string>> compute)
	{
		try
		{
			return Cached("derived|" + name, DerivedStamp(inputs, extra), compute);
		}
		catch (Exception ex) when ((ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) ? true : false)
		{
			return new HashSet<string>();
		}
	}

	public static string DerivedStamp(IEnumerable<string?> inputs, string extra)
	{
		return string.Join("|", inputs.Select((string i) =>
		{
			string text;
			if (i != null)
			{
				text = Stamp(Path.GetFullPath(i));
				if (text == null)
				{
					return "missing";
				}
			}
			else
			{
				text = "-";
			}
			return text;
		}).Append(extra));
	}

	private static HashSet<string> Cached(string name, string stamp, Func<HashSet<string>> read)
	{
		using (gate.EnterScope())
		{
			lastUse = Environment.TickCount64;
			if (cache.TryGetValue(name, out Entry value) && value.Stamp == stamp)
			{
				value.Used = ++uses;
				Evict();
				return value.Keys;
			}
		}
		HashSet<string> hashSet = read();
		if (hashSet.Count > MaxKeys)
		{
			return hashSet;
		}
		using (gate.EnterScope())
		{
			if (cache.Remove(name, out Entry value2))
			{
				count -= value2.Keys.Count;
			}
			cache[name] = new Entry(stamp, hashSet)
			{
				Used = ++uses
			};
			count += hashSet.Count;
			Evict();
			return hashSet;
		}
	}

	private static void Evict()
	{
		while (count > MaxKeys && cache.Count > 0)
		{
			KeyValuePair<string, Entry> keyValuePair = cache.MinBy((KeyValuePair<string, Entry> e) => e.Value.Used);
			cache.Remove(keyValuePair.Key);
			count -= keyValuePair.Value.Keys.Count;
		}
	}

	public static bool DropIdle(TimeSpan idle)
	{
		using (gate.EnterScope())
		{
			if (cache.Count == 0 || !Idle(lastUse, Environment.TickCount64, idle))
			{
				return false;
			}
			cache.Clear();
			count = 0L;
			return true;
		}
	}

	internal static bool Idle(long lastUse, long now, TimeSpan idle)
	{
		return (double)(now - lastUse) >= idle.TotalMilliseconds;
	}

	public static void Forget(string path)
	{
		string full = Path.GetFullPath(path);
		using (gate.EnterScope())
		{
			foreach (string item in cache.Keys.Where((string k) => k.Equals(full, StringComparison.OrdinalIgnoreCase) || k.StartsWith(full + "|", StringComparison.OrdinalIgnoreCase)).ToList())
			{
				count -= cache[item].Keys.Count;
				cache.Remove(item);
			}
		}
	}

	internal static string? Stamp(string path, bool strict = false)
	{
		if (!strict && !File.Exists(path))
		{
			return null;
		}
		FileStream fileStream;
		try
		{
			fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		}
		catch (Exception ex) when ((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? true : false)
		{
			return null;
		}
		using (fileStream)
		{
			byte[] array = new byte[(int)Math.Min(4096L, fileStream.Length)];
			fileStream.ReadExactly(array);
			byte[] array2 = new byte[(int)Math.Min(4096L, Math.Max(0L, fileStream.Length - array.Length))];
			fileStream.Seek(-array2.Length, SeekOrigin.End);
			fileStream.ReadExactly(array2);
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(3, 4);
			defaultInterpolatedStringHandler.AppendFormatted(fileStream.Length);
			defaultInterpolatedStringHandler.AppendLiteral(":");
			defaultInterpolatedStringHandler.AppendFormatted(File.GetLastWriteTimeUtc(fileStream.SafeFileHandle).Ticks);
			defaultInterpolatedStringHandler.AppendLiteral(":");
			defaultInterpolatedStringHandler.AppendFormatted(Identity(fileStream.SafeFileHandle));
			defaultInterpolatedStringHandler.AppendLiteral(":");
			byte[] array3 = array;
			byte[] array4 = array2;
			int num = 0;
			byte[] array5 = new byte[array3.Length + array4.Length];
			ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(array3);
			readOnlySpan.CopyTo(new Span<byte>(array5).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			ReadOnlySpan<byte> readOnlySpan2 = new ReadOnlySpan<byte>(array4);
			readOnlySpan2.CopyTo(new Span<byte>(array5).Slice(num, readOnlySpan2.Length));
			num += readOnlySpan2.Length;
			defaultInterpolatedStringHandler.AppendFormatted(Convert.ToHexStringLower(SHA1.HashData(new ReadOnlySpan<byte>(array5))));
			return defaultInterpolatedStringHandler.ToStringAndClear();
		}
	}

	public static string Identity(string path)
	{
		using SafeFileHandle safeFileHandle = CreateFileW(path, 128u, 7u, 0, 3u, 0u, 0);
		return safeFileHandle.IsInvalid ? "" : Identity(safeFileHandle);
	}

	private static string Identity(SafeFileHandle h)
	{
		string obj = (GetFileInformationByHandleEx(h, 0, out BasicInfo info, Marshal.SizeOf<BasicInfo>()) ? info.Changed.ToString() : "");
		string text = (GetFileInformationByHandleEx(h, 18, out IdInfo info2, Marshal.SizeOf<IdInfo>()) ? $"{info2.Volume:x}-{info2.High:x16}{info2.Low:x16}" : "");
		return obj + "/" + text;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out BasicInfo info, int size);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out IdInfo info, int size);

	public static HashSet<string>? Set(string path)
	{
		string full = Path.GetFullPath(path);
		try
		{
			string text = Stamp(full);
			object result;
			if (text != null)
			{
				HashSet<string> hashSet = Cached(full, text, () =>
				{
					if (Valid(full, out byte[] bytes))
					{
						HashSet<string> hashSet2 = new HashSet<string>();
						foreach (string item in from k in bytes.Chunk(20)
							select Convert.ToHexStringLower(k))
						{
							hashSet2.Add(item);
						}
						return hashSet2;
					}
					return Damaged;
				});
				if (hashSet != Damaged)
				{
					result = hashSet;
					goto IL_0047;
				}
			}
			result = null;
			goto IL_0047;
			IL_0047:
			return (HashSet<string>?)result;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	private static string Name(string prefix, byte[] bytes)
	{
		return prefix + "-" + Convert.ToHexStringLower(SHA256.HashData(bytes)).Substring(0, 16) + ".keys";
	}

	private static bool Valid(string path, out byte[] bytes)
	{
		bytes = File.ReadAllBytes(path);
		string fileName = Path.GetFileName(path);
		if (bytes.Length % 20 == 0)
		{
			int num = fileName.LastIndexOf('-');
			if (num > 0)
			{
				return fileName.Equals(Name(fileName.Substring(0, num), bytes), StringComparison.OrdinalIgnoreCase);
			}
		}
		return false;
	}

	public static string Write(string dir, string prefix, IEnumerable<string> keys)
	{
		byte[] bytes = keys.Order(StringComparer.Ordinal).SelectMany(Convert.FromHexString).ToArray();
		string path = Path.Combine(dir, Name(prefix, bytes));
		AppStore.Locked(Path.Combine(dir, "state.json"), () =>
		{
			if (!File.Exists(path) || !Valid(path, out byte[] _))
			{
				AppStore.WriteAtomic(path, bytes);
			}
			else
			{
				File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
				Forget(path);
			}
		});
		return Path.GetFileName(path);
	}

	internal static void Prune(string dir, TimeSpan age, params string?[] keep)
	{
		if (!Directory.Exists(dir))
		{
			return;
		}
		DateTime before = DateTime.UtcNow - age;
		foreach (string item in (from f in new string[4] { "plan-*.keys", "warm-*.keys", "plan-*.keys.*.tmp", "warm-*.keys.*.tmp" }.SelectMany((string p) => Directory.EnumerateFiles(dir, p))
			where !keep.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(f) < before
			select f).ToList().Also(() =>
		{
			PruneListed?.Invoke(dir);
		}))
		{
			try
			{
				if (!(File.GetLastWriteTimeUtc(item) >= before))
				{
					File.Delete(item);
					Forget(item);
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
		}
	}
}
