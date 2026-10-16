using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Vendors;

public static class AmdAgs
{
	private sealed class Run
	{
		private readonly char[] chars = new char[64];

		private int length;

		private bool over;

		private uint hash = 2166136261u;

		public void Add(byte c)
		{
			if (length == 64)
			{
				over = true;
				return;
			}
			chars[length++] = (char)c;
			hash = (hash ^ c) * 16777619 * 16777619;
		}

		public bool Ends(IReadOnlySet<uint> hashes, out string s)
		{
			bool flag = !over && length >= 2 && (hashes.Contains(hash) || hash == CreateDeviceHash);
			s = (flag ? new string(chars, 0, length) : "");
			Reset();
			return flag;
		}

		public void Reset()
		{
			length = 0;
			over = false;
			hash = 2166136261u;
		}
	}

	public const string DllName = "amd_ags_x64.dll";

	private const string CreateDevice = "agsDriverExtensionsDX12_CreateDevice";

	private static readonly ConcurrentDictionary<(string, long, DateTime, string), string?> names = new ConcurrentDictionary<(string, long, DateTime, string), string>();

	private const int MaxName = 64;

	private static readonly uint CreateDeviceHash = AmdAppCache.Fnv1a("agsDriverExtensionsDX12_CreateDevice");

	private static readonly ConcurrentDictionary<(string, long, DateTime), bool?> exports = new ConcurrentDictionary<(string, long, DateTime), bool?>();

	public static AgsRegistration? Of(Game g, EngineInfo? engine)
	{
		if (!(engine?.Family != "Unreal"))
		{
			Match match = Regex.Match(engine.Version, "^(\\d+)\\.(\\d+)");
			if (match != null && match.Success)
			{
				int num = int.Parse(match.Groups[1].Value);
				int num2 = int.Parse(match.Groups[2].Value);
				int num3 = num;
				if (num3 >= 4 && (num3 != 4 || num2 >= 25))
				{
					string text = ProjectName(g);
					if (text != null)
					{
						bool? flag = UsesAgs(g.ExePath);
						if (flag != false)
						{
							return new AgsRegistration(text, $"UnrealEngine{num3}.{num2}", !flag.HasValue);
						}
						return null;
					}
				}
				return null;
			}
		}
		return null;
	}

	public static AgsRegistration? Recovered(Game g, EngineInfo? engine, string exeName, IEnumerable<string> gameKeys)
	{
		if (engine == null || engine.Family == "Unreal")
		{
			return null;
		}
		List<string> list = gameKeys.Where((string k) => k.StartsWith("dxc:", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
		if (list.Count == 0 || list.Contains(AmdAppCache.HintKey(exeName)))
		{
			return null;
		}
		FileInfo fileInfo = new FileInfo(g.ExePath);
		if (!fileInfo.Exists)
		{
			return null;
		}
		(string, long, DateTime, string) key = (fileInfo.FullName.ToLowerInvariant(), fileInfo.Length, fileInfo.LastWriteTimeUtc, string.Join(',', list));
		if (!names.TryGetValue(key, out string value))
		{
			HashSet<uint> hashes = list.Select((string k) => uint.TryParse(k.AsSpan("dxc:".Length), NumberStyles.HexNumber, null, out var result) ? result : 0u).ToHashSet();
			try
			{
				using FileStream s = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
				ConcurrentDictionary<(string, long, DateTime, string), string?> concurrentDictionary = names;
				List<string> list2 = StringsHashing(s, hashes);
				object obj;
				if (list2 != null && list2.Count == 1)
				{
					string text = list2[0];
					obj = text;
				}
				else
				{
					obj = null;
				}
				value = (string)obj;
				concurrentDictionary[key] = (string)obj;
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				return null;
			}
		}
		if (value != null && !value.Equals(exeName, StringComparison.OrdinalIgnoreCase))
		{
			return new AgsRegistration(value, engine.Family);
		}
		return null;
	}

	public static AgsRegistration? Learned(string? app, string? engine, string exeName, IEnumerable<string> gameKeys)
	{
		if (app == null || app.Length <= 0 || !gameKeys.Contains(AmdAppCache.AgsKey(exeName, app)))
		{
			return null;
		}
		return new AgsRegistration(app, engine ?? "");
	}

	public static List<string> StringsHashing(Stream s, IReadOnlySet<uint> hashes)
	{
		HashSet<string> found = new HashSet<string>(StringComparer.Ordinal);
		bool creates = false;
		Run run = new Run();
		Run run2 = new Run();
		Run run3 = new Run();
		byte[] array = new byte[1048576];
		long num = 0L;
		int num2 = -1;
		int num3;
		while ((num3 = s.Read(array)) > 0)
		{
			int num4 = 0;
			while (num4 < num3)
			{
				byte b = array[num4];
				if (b >= 32 && b <= 126)
				{
					run.Add(b);
				}
				else if (b == 0)
				{
					End(run);
				}
				else
				{
					run.Reset();
				}
				if (num2 >= 0)
				{
					Run run4 = (((num & 1) == 1) ? run2 : run3);
					if (b == 0 && num2 >= 32 && num2 <= 126)
					{
						run4.Add((byte)num2);
					}
					else if (b == 0 && num2 == 0)
					{
						End(run4);
					}
					else
					{
						run4.Reset();
					}
				}
				num2 = b;
				num4++;
				num++;
			}
		}
		if (!creates)
		{
			return new List<string>();
		}
		return found.Order(StringComparer.Ordinal).ToList();
		void End(Run r)
		{
			if (r.Ends(hashes, out string s2))
			{
				if (s2 == "agsDriverExtensionsDX12_CreateDevice")
				{
					creates = true;
				}
				else if (found.Count < 2)
				{
					found.Add(s2);
				}
			}
		}
	}

	public static string? ProjectName(Game g)
	{
		string text = UnrealUserCache.Project(g.ExePath);
		if (text != null)
		{
			return text;
		}
		if (!Directory.Exists(g.InstallDir))
		{
			return null;
		}
		List<string> list = (from d in Directory.EnumerateDirectories(g.InstallDir)
			where !Path.GetFileName(d).Equals("Engine", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(d, "Content", "Paks"))
			select d).Select(Path.GetFileName).Take(2).ToList();
		if (list.Count != 1)
		{
			return null;
		}
		return list[0];
	}

	public static string? DllFor(Game g, string? bundled)
	{
		string text = Path.Combine(Path.GetDirectoryName(g.ExePath) ?? "", "amd_ags_x64.dll");
		try
		{
			return (File.Exists(text) && FileVersionInfo.GetVersionInfo(text).FileMajorPart == 6) ? text : bundled;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return bundled;
		}
	}

	public static bool? UsesAgs(string exePath)
	{
		if (File.Exists(Path.Combine(Path.GetDirectoryName(exePath) ?? "", "amd_ags_x64.dll")))
		{
			return true;
		}
		FileInfo fi = new FileInfo(exePath);
		if (!fi.Exists)
		{
			return false;
		}
		return exports.GetOrAdd((fi.FullName.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc), ((string, long, DateTime) _) =>
		{
			try
			{
				using PEReader pe = PeFile.Open(fi.FullName);
				return PeFile.ExportNames(pe).Contains("agsDriverExtensionsDX12_CreateDevice");
			}
			catch (UnauthorizedAccessException)
			{
				return (bool?)null;
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is BadImageFormatException || ex2 is InvalidOperationException) ? true : false)
			{
				return false;
			}
		});
	}
}
