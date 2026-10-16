using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Games;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.Vendors;

public sealed class AmdKept(string dxcDir, string keptDir)
{
	public sealed record State(DateTimeOffset? PutBackAt = null, bool Late = false, int Unkept = 0, string? HeldBy = null, string? Driver = null, Dictionary<string, string>? Links = null, bool Dropping = false);

	public const string StateFile = "kept.json";

	public string Dir => keptDir;

	public bool Any => Mine(Read()).Count > 0;

	private string StatePath => Path.Combine(keptDir, "kept.json");

	public static bool Usable(string dxcDir, string keptRoot)
	{
		if (Directory.Exists(dxcDir) && !Redirected(dxcDir))
		{
			string text = GameFiles.FinalPath(dxcDir);
			if (text != null && !Redirected(keptRoot))
			{
				Directory.CreateDirectory(keptRoot);
				string text2 = GameFiles.FinalPath(keptRoot);
				if (text2 != null)
				{
					return string.Equals(Path.GetPathRoot(text), Path.GetPathRoot(text2), StringComparison.OrdinalIgnoreCase);
				}
				return false;
			}
		}
		return false;
	}

	public State Keep(IEnumerable<string> names, IReadOnlySet<string> keys, string? driver)
	{
		State state = Read();
		if (state.Dropping && !Drop())
		{
			return state;
		}
		if (state.Dropping)
		{
			state = new State();
		}
		if (Redirected(keptDir))
		{
			throw new IOException(keptDir + " is reached through a link");
		}
		Directory.CreateDirectory(keptDir);
		Dictionary<string, string> dictionary = Mine(state);
		int num = 0;
		string text = null;
		foreach (string item in dictionary.Keys.Where((string n) => !keys.Contains(AmdAppCache.Key(n, d3d12: true))).ToList())
		{
			if (TryDelete(Path.Combine(keptDir, item)))
			{
				dictionary.Remove(item);
				continue;
			}
			num++;
			if (text == null)
			{
				text = Holder(Path.Combine(keptDir, item));
			}
		}
		foreach (string item2 in names.Where((string n) => Named(n) && keys.Contains(AmdAppCache.Key(n, d3d12: true))))
		{
			string text2 = Path.Combine(dxcDir, item2);
			string text3 = Path.Combine(keptDir, item2);
			string text4 = Id(text2);
			if (text4 == null)
			{
				if (File.Exists(text2))
				{
					num++;
				}
				continue;
			}
			string text5 = Id(text3);
			if ((text5 != null && !dictionary.ContainsKey(item2)) || (text5 == null && File.Exists(text3)))
			{
				num++;
			}
			else
			{
				if (text5 == text4)
				{
					continue;
				}
				if ((text5 == null || TryDelete(text3)) && CreateHardLinkW(text3, text2, 0))
				{
					dictionary[item2] = text4;
					continue;
				}
				bool flag = text5 != null && File.Exists(text3);
				if (text5 != null && !flag)
				{
					dictionary.Remove(item2);
				}
				num++;
				if (text == null)
				{
					text = Holder(flag ? text3 : text2);
				}
			}
		}
		foreach (var (text8, value) in state.Links ?? new Dictionary<string, string>())
		{
			if (!dictionary.ContainsKey(text8) && Named(text8) && File.Exists(Path.Combine(keptDir, text8)) && Id(Path.Combine(keptDir, text8)) == null)
			{
				dictionary[text8] = value;
			}
		}
		State state2 = state with
		{
			Unkept = num,
			HeldBy = text,
			Driver = (driver ?? state.Driver),
			Links = dictionary
		};
		Write(state2);
		return state2;
	}

	public int PutBack(Func<string, bool> ours, bool late, DateTimeOffset now)
	{
		int num = 0;
		foreach (string key in Mine(Read()).Keys)
		{
			if (ours(AmdAppCache.Key(key, d3d12: true)))
			{
				string text = Path.Combine(keptDir, key);
				string text2 = Path.Combine(dxcDir, key);
				if (!File.Exists(text2) && CreateHardLinkW(text2, text, 0))
				{
					num++;
				}
				Touch(text, now);
			}
		}
		if (num > 0)
		{
			Write(Read()with
			{
				PutBackAt = now,
				Late = late
			});
		}
		return num;
	}

	public (long Bytes, long Frees) Size()
	{
		long num = 0L;
		long num2 = 0L;
		foreach (var (path, text3) in Mine(Read()))
		{
			FileInfo fileInfo = new FileInfo(Path.Combine(keptDir, path));
			if (fileInfo.Exists)
			{
				num += fileInfo.Length;
				if (Id(Path.Combine(dxcDir, path)) != text3)
				{
					num2 += fileInfo.Length;
				}
			}
		}
		return (Bytes: num, Frees: num2);
	}

	public string? KeptId(string name)
	{
		return Mine(Read()).GetValueOrDefault(name);
	}

	public State Read()
	{
		try
		{
			return File.Exists(StatePath) ? (JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State()) : new State();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
		{
			return new State();
		}
	}

	public bool Drop()
	{
		if (!Directory.Exists(keptDir) || Redirected(keptDir))
		{
			return true;
		}
		State state = Read();
		foreach (string key in Owned(state).Keys)
		{
			TryDelete(Path.Combine(keptDir, key));
		}
		Dictionary<string, string> dictionary = (state.Links ?? new Dictionary<string, string>()).Where((KeyValuePair<string, string> l) => Named(l.Key) && File.Exists(Path.Combine(keptDir, l.Key))).ToDictionary(StringComparer.OrdinalIgnoreCase);
		if (dictionary.Count == 0)
		{
			TryDelete(StatePath);
		}
		else
		{
			Write(state with
			{
				Links = dictionary,
				Dropping = true
			});
		}
		try
		{
			Directory.Delete(keptDir);
		}
		catch (IOException)
		{
		}
		return dictionary.Count == 0;
	}

	private static bool Named(string name)
	{
		if (name == Path.GetFileName(name) && name.EndsWith(".parc", StringComparison.OrdinalIgnoreCase))
		{
			return AmdAppCache.Key(name, d3d12: true) != null;
		}
		return false;
	}

	private Dictionary<string, string> Mine(State s)
	{
		if (!s.Dropping)
		{
			return Owned(s);
		}
		return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	private Dictionary<string, string> Owned(State s)
	{
		if (Directory.Exists(keptDir) && !Redirected(keptDir) && s.Links != null)
		{
			return s.Links.Where((KeyValuePair<string, string> l) => Named(l.Key) && Id(Path.Combine(keptDir, l.Key)) == l.Value).ToDictionary(StringComparer.OrdinalIgnoreCase);
		}
		return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	private void Write(State s)
	{
		string text = StatePath + ".tmp";
		File.Delete(text);
		using (FileStream utf8Json = new FileStream(text, FileMode.CreateNew, FileAccess.Write))
		{
			JsonSerializer.Serialize(utf8Json, s);
		}
		File.Move(text, StatePath, overwrite: true);
	}

	private static bool TryDelete(string path)
	{
		try
		{
			File.Delete(path);
			return true;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return false;
		}
	}

	private static bool Redirected(string dir)
	{
		for (DirectoryInfo directoryInfo = new DirectoryInfo(Path.GetFullPath(dir)); directoryInfo != null; directoryInfo = directoryInfo.Parent)
		{
			if (directoryInfo.Exists && directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				return true;
			}
		}
		return false;
	}

	public static string? Id(string path)
	{
		(byte[], string)? tuple = GameFiles.Identity(path);
		if (tuple.HasValue)
		{
			byte[] item = tuple.GetValueOrDefault().Item1;
			if (item != null)
			{
				return Convert.ToHexString(item);
			}
		}
		return null;
	}

	private static string? Holder(string path)
	{
		HashSet<nuint> pids = AppCacheFiles.ProcessesUsing(path);
		string item = ProcessTree.Snapshot().FirstOrDefault(((int Pid, int Parent, string Exe) p) => pids.Contains((nuint)p.Pid)).Exe;
		if (item != null)
		{
			if (!item.Equals("AMDRSSrcExt.exe", StringComparison.OrdinalIgnoreCase))
			{
				return item;
			}
			return "the Radeon overlay";
		}
		return null;
	}

	private static void Touch(string path, DateTimeOffset now)
	{
		using SafeFileHandle safeFileHandle = CreateFileW(path, 256u, 7u, 0, 3u, 0u, 0);
		if (!safeFileHandle.IsInvalid)
		{
			long lastAccess = now.UtcDateTime.ToFileTimeUtc();
			SetFileTime(safeFileHandle, 0, ref lastAccess, 0);
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CreateHardLinkW(string newName, string existing, nint security);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetFileTime(SafeFileHandle h, nint creation, ref long lastAccess, nint lastWrite);
}
