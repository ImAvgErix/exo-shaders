using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Games;

public sealed class ManualSource(AppStore store) : IGameSource
{
	private readonly string _mutex = "Local\\SCSKiller.manual-games." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(store.DataDir).ToUpperInvariant()))).Substring(0, 16);

	private volatile Dictionary<string, (string Root, bool Confirmed)> _roots = new Dictionary<string, (string, bool)>();

	private volatile HashSet<string> _found = new HashSet<string>();

	private static readonly string[] Libraries = new string[22]
	{
		"steamapps", "SteamLibrary", "XboxGames", "WindowsApps", "ModifiableWindowsApps", "Epic Games", "EpicGames", "GOG Games", "GOG Galaxy", "EA Games",
		"Origin Games", "Ubisoft Game Launcher", "Battle.net", "Program Files", "Program Files (x86)", "ProgramData", "Windows", "Users", "Desktop", "Downloads",
		"Documents", "OneDrive"
	};

	private static readonly Environment.SpecialFolder[] SpecialFolders = new Environment.SpecialFolder[4]
	{
		Environment.SpecialFolder.UserProfile,
		Environment.SpecialFolder.Windows,
		Environment.SpecialFolder.LocalApplicationData,
		Environment.SpecialFolder.ApplicationData
	};

	private static readonly string[] NotAGame = new string[10] { "redist", "directx", "crash", "setup", "install", "support", "tools", "launcher", "anticheat", "battleye" };

	private static readonly string[] Generic = new string[4] { "UnrealGame", "UE4Game", "UE5Game", "Unity" };

	public Store Store => Store.Manual;

	public IReadOnlyDictionary<string, Game>? Known { get; set; }

	public IReadOnlyList<string> Folders => store.LoadAddedLibrary().Folders ?? new List<string>();

	internal Action? Read { get; set; }

	public IReadOnlyList<Game> Discover()
	{
		List<ManualEntry> source = Entries();
		AddedLibrary added = store.LoadAddedLibrary();
		List<Game> list = source.Where((ManualEntry e) => File.Exists(e.Exe)).Select(ToGame).ToList();
		HashSet<string> hashSet = source.Select((ManualEntry e) => IdOf(e.Exe)).Concat(added.Removed ?? new List<string>()).ToHashSet();
		Dictionary<string, Game> dictionary = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
		foreach (Game item in Known?.Values ?? Array.Empty<Game>())
		{
			if (item.Store == Store.Manual)
			{
				dictionary.TryAdd(GameFiles.DirKey(item.InstallDir), item);
			}
		}
		List<string> source2 = (from g in Known?.Values ?? Array.Empty<Game>()
			where g.Store != Store.Manual && g.InstallDir.Length > 0
			select GameFiles.DirKey(g.InstallDir)).ToList();
		HashSet<string> hashSet2 = new HashSet<string>();
		foreach (string root in (added.Folders ?? new List<string>()).SelectMany(GameRoots).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (!source.Any((ManualEntry e) => GameFiles.Inside(root, e.Exe)) && !source2.Any((string s) => Overlaps(s, root)))
			{
				Game valueOrDefault = dictionary.GetValueOrDefault(root);
				Game game = (((object)valueOrDefault != null && File.Exists(valueOrDefault.ExePath)) ? valueOrDefault : Found(root));
				if ((object)game != null && hashSet.Add(game.Id))
				{
					list.Add(game);
					hashSet2.Add(game.Id);
				}
			}
		}
		_found = hashSet2;
		return list.Select((Game g) => Named(g, added)).ToList();
		static bool Overlaps(string a, string b)
		{
			if (!a.Equals(b, StringComparison.OrdinalIgnoreCase) && !GameFiles.Inside(a, b))
			{
				return GameFiles.Inside(b, a);
			}
			return true;
		}
	}

	public bool InFolder(string gameId)
	{
		return _found.Contains(gameId);
	}

	private static Game Named(Game g, AddedLibrary added)
	{
		string text = added.Names?.GetValueOrDefault(g.Id);
		if (text == null || text.Length <= 0)
		{
			return g;
		}
		return g with
		{
			Name = text
		};
	}

	internal static List<string> GameRoots(string folder)
	{
		try
		{
			string text = GameFiles.DirKey(folder);
			List<string> source = (from d in Directory.EnumerateDirectories(text, "*", new EnumerationOptions
				{
					AttributesToSkip = FileAttributes.None
				}).Select(GameFiles.DirKey)
				where !IsLibrary(d) && !GameFiles.WindowsFolder(Path.GetFileName(d))
				select d).ToList();
			if (Directory.Exists(Path.Combine(text, "Engine")) || (Directory.EnumerateFiles(text, "*.exe").Any() && source.Where(Programs).Take(2).Count() < 2))
			{
				int num = 1;
				List<string> list = new List<string>(num);
				CollectionsMarshal.SetCount(list, num);
				CollectionsMarshal.AsSpan(list)[0] = text;
				return list;
			}
			return source.Select((string d) => (!File.Exists(Path.Combine(d, "Content", "MicrosoftGame.config"))) ? d : Path.Combine(d, "Content")).ToList();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) ? true : false)
		{
			return new List<string>();
		}
		static bool Programs(string d)
		{
			if (!NotAGame.Any((string n) => Path.GetFileName(d).Contains(n, StringComparison.OrdinalIgnoreCase)))
			{
				return Directory.EnumerateFiles(d, "*.exe", new EnumerationOptions
				{
					RecurseSubdirectories = true,
					MaxRecursionDepth = 2,
					AttributesToSkip = FileAttributes.None
				}).Any();
			}
			return false;
		}
	}

	internal static Game? Found(string root)
	{
		try
		{
			string text = GameFiles.FindExe(root);
			if (text == null)
			{
				return null;
			}
			ManualEntry manualEntry = Resolve(text);
			string text2 = GameFiles.FolderName(Path.GetFileName(root).Equals("Content", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(root) : root);
			manualEntry = manualEntry with
			{
				InstallDir = root,
				Name = ((manualEntry.Name == GameFiles.FolderName(manualEntry.InstallDir)) ? text2 : manualEntry.Name)
			};
			return (RootProblem(root, manualEntry.Exe, Array.Empty<Game>()) == null) ? ToGame(manualEntry) : null;
		}
		catch (Exception ex) when ((ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public void AddFolder(string folder)
	{
		string dir = GameFiles.DirKey(folder);
		if (!Directory.Exists(dir))
		{
			throw new ArgumentException(dir + " doesn't exist.");
		}
		Locked(() =>
		{
			AddedLibrary addedLibrary = store.LoadAddedLibrary();
			if ((addedLibrary.Folders ?? new List<string>()).Contains(dir, StringComparer.OrdinalIgnoreCase))
			{
				return false;
			}
			AppStore appStore = store;
			List<string> list = addedLibrary.Folders ?? new List<string>();
			int num = 1 + list.Count;
			List<string> list2 = new List<string>(num);
			CollectionsMarshal.SetCount(list2, num);
			Span<string> span = CollectionsMarshal.AsSpan(list2);
			int num2 = 0;
			Span<string> span2 = CollectionsMarshal.AsSpan(list);
			span2.CopyTo(span.Slice(num2, span2.Length));
			num2 += span2.Length;
			span[num2] = dir;
			appStore.SaveAddedLibrary(addedLibrary with
			{
				Folders = list2
			});
			return true;
		});
	}

	public void RemoveFolder(string folder)
	{
		Locked(() =>
		{
			AddedLibrary addedLibrary = store.LoadAddedLibrary();
			store.SaveAddedLibrary(addedLibrary with
			{
				Folders = (addedLibrary.Folders ?? new List<string>()).Where((string f) => !f.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList()
			});
			return true;
		});
	}

	public void Rename(string gameId, string name)
	{
		name = name.Trim();
		if (name.Length == 0)
		{
			throw new ArgumentException("Type a name for the game.");
		}
		Locked(() =>
		{
			AddedLibrary addedLibrary = store.LoadAddedLibrary();
			store.SaveAddedLibrary(addedLibrary with
			{
				Names = new Dictionary<string, string>(addedLibrary.Names ?? new Dictionary<string, string>()) { [gameId] = name }
			});
			return true;
		});
	}

	public List<ManualEntry> Entries()
	{
		return Locked(() =>
		{
			List<ManualEntry> list = store.LoadManualGames().Where(Valid).ToList();
			Read?.Invoke();
			_roots = (from e in list
				group e by IdOf(e.Exe)).ToDictionary((IGrouping<string, ManualEntry> x) => x.Key, (IGrouping<string, ManualEntry> x) => (GameFiles.DirKey(x.First().InstallDir), Confirmed: x.First().Confirmed));
			return list;
		});
	}

	public bool Confirmed(Game g)
	{
		if (_roots.TryGetValue(g.Id, out (string, bool) value) && value.Item2)
		{
			return Same(value.Item1, GameFiles.DirKey(g.InstallDir));
		}
		return false;
	}

	public bool ConfirmedNow(Game g)
	{
		Entries();
		return Confirmed(g);
	}

	public bool Stale(Game g)
	{
		if (_roots.TryGetValue(g.Id, out (string, bool) value))
		{
			return !Same(value.Item1, GameFiles.DirKey(g.InstallDir));
		}
		return false;
	}

	private static bool Valid(ManualEntry e)
	{
		if ((object)e != null)
		{
			string exe = e.Exe;
			if (exe != null && exe.Length > 0)
			{
				string installDir = e.InstallDir;
				if (installDir != null && installDir.Length > 0 && e.Exe.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(e.Exe) && e.InstallDir.IndexOfAny(Path.GetInvalidPathChars()) < 0)
				{
					return Path.IsPathFullyQualified(e.InstallDir);
				}
			}
		}
		return false;
	}

	public static Game ToGame(ManualEntry e)
	{
		string id = IdOf(e.Exe);
		string name = e.Name;
		return new Game(id, (name != null && name.Length > 0) ? name : Path.GetFileName(Path.TrimEndingDirectorySeparator(e.InstallDir)), Store.Manual, e.InstallDir, e.Exe);
	}

	public static string IdOf(string exe)
	{
		return "manual:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant()))).Substring(0, 16);
	}

	public (Game Game, bool Existed) Add(ManualEntry entry)
	{
		return Locked(() =>
		{
			List<ManualEntry> list = Entries();
			string id = IdOf(entry.Exe);
			int num = list.FindIndex((ManualEntry e) => IdOf(e.Exe) == id);
			AddedLibrary addedLibrary = store.LoadAddedLibrary();
			if (num >= 0 && !entry.Confirmed)
			{
				return (Named(ToGame(list[num]), addedLibrary), true);
			}
			if (num >= 0)
			{
				list[num] = list[num]with
				{
					InstallDir = entry.InstallDir,
					Confirmed = true
				};
			}
			else
			{
				list.Add(entry);
			}
			store.SaveManualGames(list);
			Entries();
			List<string>? removed = addedLibrary.Removed;
			if (removed != null && removed.Remove(id))
			{
				store.SaveAddedLibrary(addedLibrary);
			}
			return (Named(ToGame((num >= 0) ? list[num] : entry), addedLibrary), num >= 0);
		});
	}

	public bool Remove(string gameId)
	{
		return Locked(() =>
		{
			List<ManualEntry> list = Entries();
			bool flag = list.RemoveAll((ManualEntry e) => IdOf(e.Exe) == gameId) > 0;
			if (flag)
			{
				store.SaveManualGames(list);
			}
			Entries();
			AddedLibrary addedLibrary = store.LoadAddedLibrary();
			addedLibrary.Names?.Remove(gameId);
			AppStore appStore = store;
			List<string> list2 = new List<string>();
			list2.AddRange((addedLibrary.Removed ?? new List<string>()).Where((string r) => r != gameId));
			list2.Add(gameId);
			appStore.SaveAddedLibrary(addedLibrary with
			{
				Removed = list2
			});
			return flag;
		});
	}

	private T Locked<T>(Func<T> change)
	{
		using Mutex mutex = new Mutex(initiallyOwned: false, _mutex);
		try
		{
			mutex.WaitOne();
		}
		catch (AbandonedMutexException)
		{
		}
		try
		{
			return change();
		}
		finally
		{
			mutex.ReleaseMutex();
		}
	}

	public static ManualEntry Resolve(string picked)
	{
		string pick = Path.GetFullPath(picked);
		string fileName = Path.GetFileName(pick);
		if (!pick.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException(fileName + " isn't a program: pick the game's .exe file.");
		}
		if (!File.Exists(pick))
		{
			throw new ArgumentException(pick + " doesn't exist.");
		}
		CheckX64(pick, fileName);
		string text = InstallRoot(pick);
		if (Path.GetPathRoot(text) == text)
		{
			throw new ArgumentException(fileName + " is at the root of a drive: SCSKiller needs the game in a folder of its own.");
		}
		bool? flag = ((GameFiles.DetectAntiCheat(new Game("", "", Store.Manual, text, pick)) == AntiCheat.None) ? GameFiles.ImportsGraphics(pick) : ((bool?)null));
		string text2 = ((flag == true || GameFiles.IsShipping(pick)) ? GameFiles.GameExe(text, pick) : (GameFiles.FindExe(text, Path.GetRelativePath(text, pick)) ?? pick));
		if (Same(text2, pick) && flag == false && !GameFiles.IsShipping(pick))
		{
			List<string> list = (from f in GameFiles.GraphicsExes(text)
				where !Same(f, pick) && X64(f)
				select f).ToList();
			if (list != null)
			{
				int count = list.Count;
				if (count > 1)
				{
					throw new ArgumentException(fileName + " looks like a launcher, and SCSKiller can't tell which program it starts: " + string.Join(", ", list.Take(4).Select(Path.GetFileName)) + ((list.Count > 4) ? "…" : "") + ". Pick the one that opens the game's window.");
				}
				if (count == 1)
				{
					text2 = list[0];
				}
			}
		}
		if (!Same(text2, pick))
		{
			CheckX64(text2, Path.GetFileName(text2));
		}
		return new ManualEntry(text2, text, NameOf(text2, pick, text));
	}

	private static bool Same(string a, string b)
	{
		return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
	}

	private static bool X64(string path)
	{
		try
		{
			CheckX64(path, Path.GetFileName(path));
			return true;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	private static void CheckX64(string path, string file)
	{
		PEHeaders pEHeaders;
		try
		{
			using PEReader pEReader = PeFile.Open(path);
			pEHeaders = pEReader.PEHeaders;
		}
		catch (BadImageFormatException)
		{
			throw new ArgumentException(file + " isn't a Windows program.");
		}
		catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
		{
			throw new ArgumentException("Couldn't read " + file + ": " + ex2.Message);
		}
		if (pEHeaders.IsDll || pEHeaders.PEHeader == null)
		{
			throw new ArgumentException(file + " isn't a Windows program.");
		}
		if (pEHeaders.CoffHeader.Machine != Machine.Amd64 || pEHeaders.PEHeader.Magic != PEMagic.PE32Plus)
		{
			throw new ArgumentException((pEHeaders.CoffHeader.Machine == Machine.I386) ? (file + " is a 32-bit program: SCSKiller compiles 64-bit DirectX 12 games.") : (file + " isn't an x64 program: SCSKiller compiles 64-bit DirectX 12 games."));
		}
	}

	private static string InstallRoot(string exe)
	{
		string directoryName = Path.GetDirectoryName(exe);
		string result = directoryName;
		string directoryName2 = Path.GetDirectoryName(directoryName);
		if (directoryName2 != null)
		{
			if (Path.GetFileName(directoryName2).Equals("Binaries", StringComparison.OrdinalIgnoreCase))
			{
				string directoryName3 = Path.GetDirectoryName(directoryName2);
				if (directoryName3 != null)
				{
					string directoryName4 = Path.GetDirectoryName(directoryName3);
					result = ((directoryName4 != null && Directory.Exists(Path.Combine(directoryName4, "Engine"))) ? directoryName4 : directoryName3);
					goto IL_0090;
				}
			}
			if (Path.GetFileName(directoryName2).Equals("bin", StringComparison.OrdinalIgnoreCase))
			{
				string directoryName5 = Path.GetDirectoryName(directoryName2);
				if (directoryName5 != null)
				{
					result = directoryName5;
					goto IL_0090;
				}
			}
			if (Path.GetFileName(directoryName).StartsWith("bin", StringComparison.OrdinalIgnoreCase))
			{
				result = directoryName2;
			}
		}
		goto IL_0090;
		IL_0090:
		return result;
	}

	public static string? RootProblem(string root, string exe, IEnumerable<Game> listed)
	{
		string full;
		try
		{
			full = GameFiles.DirKey(root);
		}
		catch (Exception ex) when ((ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) ? true : false)
		{
			return root + " isn't a folder.";
		}
		if (!Directory.Exists(full))
		{
			return full + " doesn't exist.";
		}
		string pathRoot = Path.GetPathRoot(full);
		if (pathRoot != null && GameFiles.DirKey(pathRoot).Equals(full, StringComparison.OrdinalIgnoreCase))
		{
			return full + " is a whole drive: pick the game's own folder.";
		}
		if (IsLibrary(full))
		{
			return full + " holds many programs or games: pick the game's own folder.";
		}
		Game game = listed.FirstOrDefault((Game g) => !Same(g.ExePath, exe) && (GameFiles.DirKey(g.InstallDir).Equals(full, StringComparison.OrdinalIgnoreCase) || GameFiles.Inside(full, g.InstallDir)));
		if ((object)game != null)
		{
			return full + " holds " + game.Name + " too: pick this game's own folder.";
		}
		try
		{
			string name = Path.GetFileName(exe);
			if ((from d in Directory.EnumerateDirectories(full)
				where !NotAGame.Any((string n) => Path.GetFileName(d).Contains(n, StringComparison.OrdinalIgnoreCase)) && Directory.EnumerateFiles(d, "*.exe").Any() && !File.Exists(Path.Combine(d, name))
				select d).Take(2).Count() >= 2)
			{
				return full + " looks like a folder of several games: pick this game's own folder.";
			}
		}
		catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
		{
			return "Couldn't read " + full + ": " + ex2.Message;
		}
		return null;
	}

	internal static bool IsLibrary(string full)
	{
		string fileName = Path.GetFileName(full);
		if (!Libraries.Contains(fileName, StringComparer.OrdinalIgnoreCase) && (!fileName.Equals("common", StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(Path.GetDirectoryName(full)).Equals("steamapps", StringComparison.OrdinalIgnoreCase)))
		{
			return SpecialFolders.Select(Environment.GetFolderPath).Append(Path.GetTempPath()).Any((string f) => f.Length > 0 && GameFiles.DirKey(f).Equals(full, StringComparison.OrdinalIgnoreCase));
		}
		return true;
	}

	private static string NameOf(string exe, string pick, string root)
	{
		foreach (string item in new string[2] { exe, pick }.Distinct())
		{
			FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(item);
			string[] array = new string[2] { versionInfo.ProductName, versionInfo.FileDescription };
			for (int i = 0; i < array.Length; i++)
			{
				string text = array[i]?.Trim();
				if (text != null && text.Length > 0 && !Generic.Contains(text, StringComparer.OrdinalIgnoreCase) && !text.Contains("Bootstrap", StringComparison.OrdinalIgnoreCase) && !text.Contains("launcher", StringComparison.OrdinalIgnoreCase))
				{
					return text;
				}
			}
		}
		return Path.GetFileName(root);
	}
}
