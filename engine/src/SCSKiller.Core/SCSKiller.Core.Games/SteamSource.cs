using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.Games;

public sealed class SteamSource(string? steamRoot = null) : IGameSource
{
	private (string Stamp, HashSet<uint> Asked, Dictionary<uint, string> Types)? _types;

	private readonly object _gate = new object();

	private static (string Stamp, Dictionary<uint, (long Offset, uint Size)> Index, string[]? Keys)? indexed;

	private static readonly Lock indexGate = new Lock();

	public Store Store => Store.Steam;

	public IReadOnlyDictionary<string, Game>? Known { get; set; }

	public IProgress<string>? Log { get; set; }

	public IReadOnlyList<Game> Discover()
	{
		string text = steamRoot ?? Root();
		List<(uint, Game)> list = new List<(uint, Game)>();
		foreach (string item in Libraries(text))
		{
			try
			{
				string text2 = Path.Combine(item, "steamapps");
				if (!Directory.Exists(text2))
				{
					continue;
				}
				foreach (string item2 in Directory.EnumerateFiles(text2, "appmanifest_*.acf"))
				{
					try
					{
						string text3 = File.ReadAllText(item2);
						if (!uint.TryParse(V("appid"), out var id))
						{
							continue;
						}
						string text4 = V("installdir");
						if (text4 == null || !int.TryParse(V("StateFlags"), out var result) || (result & 4) == 0)
						{
							continue;
						}
						string text5 = Path.Combine(text2, "common", text4);
						string text6 = V("buildid");
						List<Game> list2 = Collections.Expand(new Game($"steam:{id}", V("name") ?? text4, Store.Steam, text5, "", text6));
						if (list2 != null && list2.Count > 0)
						{
							list.AddRange(list2.Select((Game g) => (id: id, g: g)));
							continue;
						}
						Game game = Known?.GetValueOrDefault($"steam:{id}");
						string? text7;
						if ((object)game != null)
						{
							string version = game.Version;
							if (version != null && version == text6 && game.InstallDir.Equals(text5, StringComparison.OrdinalIgnoreCase) && File.Exists(game.ExePath) && !GameFiles.NotTheGameExe(text5, game.ExePath))
							{
								text7 = game.ExePath;
								goto IL_0210;
							}
						}
						text7 = GameFiles.FindExe(text5);
						goto IL_0210;
						IL_0210:
						string text8 = text7;
						if (text8 != null)
						{
							list.Add((id, new Game($"steam:{id}", V("name") ?? text4, Store.Steam, text5, text8, text6)));
						}
						string? V(string key)
						{
							return Values(text3, key).FirstOrDefault();
						}
					}
					catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
					{
						Log?.Report("Steam: " + item2 + " skipped: " + ex.Message);
					}
				}
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
			{
				Log?.Report("Steam: library " + item + " skipped: " + ex2.Message);
			}
		}
		Dictionary<uint, string> types = TypesOf(Path.Combine(text, "appcache", "appinfo.vdf"), list.Select(((uint Id, Game Game) a) => a.Id).ToHashSet());
		return list.Where(((uint Id, Game Game) a) =>
		{
			Dictionary<uint, string> dictionary = types;
			string value;
			return dictionary == null || !dictionary.TryGetValue(a.Id, out value) || IsGameType(value);
		}).Select(((uint Id, Game Game) a) => a.Game).ToList();
	}

	public static uint? AppId(string gameId)
	{
		if (gameId.StartsWith("steam:", StringComparison.Ordinal))
		{
			ReadOnlySpan<char> span = gameId.AsSpan(6);
			int num = span.IndexOf(':');
			if (uint.TryParse(span.Slice(0, (num >= 0) ? num : span.Length), NumberStyles.None, null, out var result))
			{
				return result;
			}
		}
		return null;
	}

	public static bool IsGameType(string type)
	{
		if (!type.Equals("game", StringComparison.OrdinalIgnoreCase))
		{
			return type.Equals("demo", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private Dictionary<uint, string>? TypesOf(string appinfo, HashSet<uint> ids)
	{
		FileInfo fileInfo = new FileInfo(appinfo);
		string text = (fileInfo.Exists ? $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}" : "");
		lock (_gate)
		{
			(string, HashSet<uint>, Dictionary<uint, string>)? types = _types;
			if (types.HasValue)
			{
				(string, HashSet<uint>, Dictionary<uint, string>) valueOrDefault = types.GetValueOrDefault();
				if (valueOrDefault.Item1 == text && ids.IsSubsetOf(valueOrDefault.Item2))
				{
					return valueOrDefault.Item3;
				}
			}
			Dictionary<uint, string> dictionary = AppTypes(appinfo, ids);
			_types = ((dictionary == null) ? (((string, HashSet<uint>, Dictionary<uint, string>)?)null) : new (string, HashSet<uint>, Dictionary<uint, string>)?((text, ids, dictionary)));
			return dictionary;
		}
	}

	public static Dictionary<uint, string>? AppTypes(string path, IReadOnlySet<uint> ids)
	{
		Dictionary<uint, Dictionary<string, object>> dictionary = Apps(path, ids);
		if (dictionary == null)
		{
			return null;
		}
		Dictionary<uint, string> dictionary2 = new Dictionary<uint, string>();
		foreach (var (key, dictionary4) in dictionary)
		{
			if (dictionary4.GetValueOrDefault("common") is Dictionary<string, object> dictionary5 && dictionary5.GetValueOrDefault("type") is string value)
			{
				dictionary2[key] = value;
			}
		}
		return dictionary2;
	}

	public static IReadOnlyList<string> Libraries(string root)
	{
		string path = Path.Combine(root, "steamapps", "libraryfolders.vdf");
		List<string> list = (File.Exists(path) ? Values(File.ReadAllText(path), "path").ToList() : new List<string>());
		if (list.Count == 0)
		{
			list.Add(root);
		}
		return list.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	public static (uint? Id, bool InLibrary) AppAt(string root, string path)
	{
		string full = Path.GetFullPath(path);
		int num = full.IndexOf("\\steamapps\\common\\", StringComparison.OrdinalIgnoreCase);
		if (num < 0)
		{
			return (Id: null, InLibrary: Libraries(root).Any((string lib) => full.StartsWith(Path.TrimEndingDirectorySeparator(lib) + "\\", StringComparison.OrdinalIgnoreCase)));
		}
		string path2 = full.Substring(0, num + "\\steamapps".Length);
		string value = full.Substring(num + "\\steamapps\\common\\".Length).Split('\\')[0];
		IEnumerable<string> enumerable2;
		if (!Directory.Exists(path2))
		{
			IEnumerable<string> enumerable = Array.Empty<string>();
			enumerable2 = enumerable;
		}
		else
		{
			enumerable2 = Directory.EnumerateFiles(path2, "appmanifest_*.acf");
		}
		foreach (string item in enumerable2)
		{
			string vdf = File.ReadAllText(item);
			string text = Values(vdf, "installdir").FirstOrDefault();
			if (text != null && text.Equals(value, StringComparison.OrdinalIgnoreCase) && uint.TryParse(Values(vdf, "appid").FirstOrDefault(), out var result))
			{
				return (Id: result, InLibrary: true);
			}
		}
		return (Id: null, InLibrary: true);
	}

	public static string Root()
	{
		return (Registry.GetValue("HKEY_CURRENT_USER\\Software\\Valve\\Steam", "SteamPath", null) as string) ?? "C:\\Program Files (x86)\\Steam";
	}

	public static string AppInfoPath(string root)
	{
		return AppInfoPath(root, Wine.Running, Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH"), Wine.DosPath);
	}

	internal static string AppInfoPath(string root, bool wine, string? client, Func<string, string?> dos)
	{
		if (wine && !string.IsNullOrEmpty(client))
		{
			string text = dos(client);
			if (text == null || text.Length <= 0)
			{
				return "";
			}
			return Path.Combine(text, "appcache", "appinfo.vdf");
		}
		return Path.Combine(root, "appcache", "appinfo.vdf");
	}

	public static Dictionary<uint, Dictionary<string, object>>? Apps(string path, IReadOnlySet<uint>? ids)
	{
		return Apps(path, ids, null);
	}

	public static Dictionary<uint, Dictionary<string, object>>? Apps(string path, IReadOnlySet<uint>? ids, Dictionary<uint, uint>? states, Action<string>? failed = null)
	{
		try
		{
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536);
			BinaryReader binaryReader = new BinaryReader(fileStream);
			string text = $"{Path.GetFullPath(path)}|{fileStream.Length}|{File.GetLastWriteTimeUtc(fileStream.SafeFileHandle).Ticks}";
			Dictionary<uint, Dictionary<string, object>> apps = new Dictionary<uint, Dictionary<string, object>>();
			using (indexGate.EnterScope())
			{
				(string, Dictionary<uint, (long, uint)>, string[])? tuple = indexed;
				if (tuple.HasValue)
				{
					(string, Dictionary<uint, (long, uint)>, string[]) valueOrDefault = tuple.GetValueOrDefault();
					if (valueOrDefault.Item1 == text)
					{
						foreach (uint item in (IEnumerable<uint>)(((object)ids) ?? ((object)valueOrDefault.Item2.Keys)))
						{
							if (valueOrDefault.Item2.TryGetValue(item, out var value))
							{
								(fileStream.Position, _) = value;
								Add(item, binaryReader.ReadBytes((int)value.Item2), valueOrDefault.Item3);
							}
						}
						return apps;
					}
				}
			}
			uint num = binaryReader.ReadUInt32();
			binaryReader.ReadUInt32();
			string[] array = null;
			switch (num)
			{
			case 123094057u:
			{
				long position = binaryReader.ReadInt64();
				long position2 = fileStream.Position;
				fileStream.Position = position;
				array = new string[binaryReader.ReadUInt32()];
				for (int i = 0; i < array.Length; i++)
				{
					array[i] = CString(binaryReader);
				}
				fileStream.Position = position2;
				break;
			}
			default:
				failed?.Invoke($"unknown format 0x{num:x8}");
				return null;
			case 123094056u:
				break;
			}
			Dictionary<uint, (long, uint)> dictionary = new Dictionary<uint, (long, uint)>();
			uint num2;
			while ((num2 = binaryReader.ReadUInt32()) != 0)
			{
				uint num3 = binaryReader.ReadUInt32();
				dictionary[num2] = (fileStream.Position, num3);
				if (ids != null && !ids.Contains(num2))
				{
					fileStream.Seek(num3, SeekOrigin.Current);
				}
				else
				{
					Add(num2, binaryReader.ReadBytes((int)num3), array);
				}
			}
			using (indexGate.EnterScope())
			{
				indexed = (text, dictionary, array);
			}
			return apps;
			void Add(uint id, byte[] bytes, string[]? keys)
			{
				BinaryReader binaryReader2 = new BinaryReader(new MemoryStream(bytes, writable: false));
				if (states != null)
				{
					states[id] = binaryReader2.ReadUInt32();
				}
				binaryReader2.BaseStream.Position = 60L;
				try
				{
					Dictionary<string, object> dictionary2 = Object(binaryReader2, keys);
					apps[id] = (dictionary2.GetValueOrDefault("appinfo") as Dictionary<string, object>) ?? dictionary2;
				}
				catch (Exception ex2) when ((ex2 is InvalidDataException || ex2 is EndOfStreamException || ex2 is IndexOutOfRangeException) ? true : false)
				{
				}
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
		{
			failed?.Invoke(ex.Message);
			return null;
		}
	}

	private static Dictionary<string, object> Object(BinaryReader r, string[]? keys, int depth = 0)
	{
		if (depth > 64)
		{
			throw new InvalidDataException("KeyValues nested deeper than 64");
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
		byte b;
		while ((b = r.ReadByte()) != 8)
		{
			string key = ((keys == null) ? CString(r) : keys[r.ReadInt32()]);
			switch (b)
			{
			case 0:
				dictionary[key] = Object(r, keys, depth + 1);
				break;
			case 1:
				dictionary[key] = CString(r);
				break;
			case 2:
				dictionary[key] = r.ReadInt32();
				break;
			case 3:
			case 4:
			case 6:
				r.ReadInt32();
				break;
			case 7:
			case 10:
				r.ReadInt64();
				break;
			default:
				throw new InvalidDataException($"KeyValues type {b}");
			}
		}
		return dictionary;
	}

	private static string CString(BinaryReader r)
	{
		List<byte> list = new List<byte>();
		byte item;
		while ((item = r.ReadByte()) != 0)
		{
			list.Add(item);
		}
		return Encoding.UTF8.GetString(list.ToArray());
	}

	public static IEnumerable<string> Values(string vdf, string key)
	{
		return from m in Regex.Matches(vdf, "\"" + Regex.Escape(key) + "\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)
			select Regex.Replace(m.Groups[1].Value, "\\\\(.)", "$1");
	}
}
