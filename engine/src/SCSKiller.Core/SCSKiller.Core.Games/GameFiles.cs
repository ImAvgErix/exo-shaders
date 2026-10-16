using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Games;

public static class GameFiles
{
	private static readonly EnumerationOptions Deep = new EnumerationOptions
	{
		RecurseSubdirectories = true,
		MaxRecursionDepth = 3,
		IgnoreInaccessible = true,
		AttributesToSkip = FileAttributes.None
	};

	private static readonly EnumerationOptions Flat = new EnumerationOptions
	{
		IgnoreInaccessible = true,
		AttributesToSkip = FileAttributes.None
	};

	private static readonly string[] NotTheGameBuild = new string[5] { "Server", "Editor", "Tool", "Benchmark", "CrashReport" };

	private static readonly string[] GraphicsDlls = new string[4] { "d3d12.dll", "d3d11.dll", "dxgi.dll", "sl.interposer.dll" };

	private static readonly string[] NotTheGame = new string[6] { "redist", "directx", "crash", "unins", "setup", "vconsole" };

	private static readonly string[] Helpers = new string[11]
	{
		"EpicOnlineServices", "EOSBootstrapper", "EpicWebHelper", "CrashReport", "UnrealCEFSubProcess", "Prereq", "setup", "redist", "installer", "unins",
		"ffmpeg"
	};

	private static readonly string[] Staging = new string[5] { "PatchData", "__Installer", "Backup", "Backups", "Staging" };

	private static readonly string[] WindowsFolders = new string[2] { "$RECYCLE.BIN", "System Volume Information" };

	internal static readonly (string Name, AntiCheat Kind)[] Markers = new (string, AntiCheat)[93]
	{
		("EasyAntiCheat", AntiCheat.EasyAntiCheat),
		("EasyAntiCheat_EOS", AntiCheat.EasyAntiCheat),
		("start_protected_game.exe", AntiCheat.EasyAntiCheat),
		("EasyAntiCheat_EOS_Setup.exe", AntiCheat.EasyAntiCheat),
		("EasyAntiCheat_Setup.exe", AntiCheat.EasyAntiCheat),
		("BattlEye", AntiCheat.BattlEye),
		("BEService.exe", AntiCheat.BattlEye),
		("BEService_x64.exe", AntiCheat.BattlEye),
		("BELauncher.exe", AntiCheat.BattlEye),
		("BEClient_x64.dll", AntiCheat.BattlEye),
		("BEClient.dll", AntiCheat.BattlEye),
		("EAAntiCheat.Installer.exe", AntiCheat.Other),
		("GameGuard", AntiCheat.Other),
		("XIGNCODE", AntiCheat.Other),
		("nProtect", AntiCheat.Other),
		("randgrid.sys", AntiCheat.Other),
		("NCGuardSDK", AntiCheat.Other),
		("NCGuard", AntiCheat.Other),
		("AntiCheatExpert", AntiCheat.Other),
		("AceAntibotClient", AntiCheat.Other),
		("TP3Helper.exe", AntiCheat.Other),
		("SGuard", AntiCheat.Other),
		("SGuard64.exe", AntiCheat.Other),
		("SGuardSvc64.exe", AntiCheat.Other),
		("ACE-Base64.dll", AntiCheat.Other),
		("ACE-Base.dat", AntiCheat.Other),
		("ACE-Setup64.exe", AntiCheat.Other),
		("ACE-Service64.exe", AntiCheat.Other),
		("ACE-ATS64.dll", AntiCheat.Other),
		("ACE-CSI64.dll", AntiCheat.Other),
		("ACE-DFS64.dll", AntiCheat.Other),
		("TenProtect", AntiCheat.Other),
		("TesSafe.sys", AntiCheat.Other),
		("HoYoKProtect.sys", AntiCheat.Other),
		("mhypbase.dll", AntiCheat.Other),
		("mhyprot.sys", AntiCheat.Other),
		("mhyprot2.sys", AntiCheat.Other),
		("mhyprot3.sys", AntiCheat.Other),
		("ACE-BASE.sys", AntiCheat.Other),
		("GenshinImpact.exe", AntiCheat.Other),
		("YuanShen.exe", AntiCheat.Other),
		("StarRail.exe", AntiCheat.Other),
		("ZenlessZoneZero.exe", AntiCheat.Other),
		("BH3.exe", AntiCheat.Other),
		("HYP.exe", AntiCheat.Other),
		("HYPHelper.exe", AntiCheat.Other),
		("HYPWorker.exe", AntiCheat.Other),
		("EAAntiCheat.GameServiceLauncher.exe", AntiCheat.Other),
		("EAAntiCheat.GameServiceLauncher.dll", AntiCheat.Other),
		("vgk.sys", AntiCheat.Other),
		("vgc.exe", AntiCheat.Other),
		("VALORANT.exe", AntiCheat.Other),
		("VALORANT-Win64-Shipping.exe", AntiCheat.Other),
		("League of Legends.exe", AntiCheat.Other),
		("LeagueClient.exe", AntiCheat.Other),
		("LeagueClientUx.exe", AntiCheat.Other),
		("LeagueClientUxRender.exe", AntiCheat.Other),
		("LoR.exe", AntiCheat.Other),
		("Lion-Win64-Shipping.exe", AntiCheat.Other),
		("RiotClientServices.exe", AntiCheat.Other),
		("RiotClientUx.exe", AntiCheat.Other),
		("RiotClientUxRender.exe", AntiCheat.Other),
		("NeacClient.exe", AntiCheat.Other),
		("NeacSafe64.sys", AntiCheat.Other),
		("NeacSafe64_ex.sys", AntiCheat.Other),
		("NEP2.dll", AntiCheat.Other),
		("NEPCleaner.exe", AntiCheat.Other),
		("BlackCall.aes", AntiCheat.Other),
		("BlackCall64.aes", AntiCheat.Other),
		("BlackCat64.sys", AntiCheat.Other),
		("HShield", AntiCheat.Other),
		("PunkBuster", AntiCheat.Other),
		("PnkBstrA.exe", AntiCheat.Other),
		("pbsvc.exe", AntiCheat.Other),
		("pbsv.dll", AntiCheat.Other),
		("equ8_conf.json", AntiCheat.Other),
		(".build.info", AntiCheat.Other),
		(".product.db", AntiCheat.Other),
		("Warframe.x64.exe", AntiCheat.Other),
		("ONCE_HUMAN.exe", AntiCheat.Other),
		("forzahorizon6.exe", AntiCheat.Other),
		("arbiter.dll", AntiCheat.Other),
		("gameguard.des", AntiCheat.Other),
		("DenuvoAC", AntiCheat.Other),
		("denuvo-anti-cheat.sys", AntiCheat.Other),
		("denuvo-anti-cheat-runtime.dll", AntiCheat.Other),
		("denuvo-anti-cheat-update-service.exe", AntiCheat.Other),
		("Denuvo Anti-Cheat Installer.exe", AntiCheat.Other),
		("Elytra", AntiCheat.Other),
		("Elytra.exe", AntiCheat.Other),
		("Elytra-Setup.exe", AntiCheat.Other),
		("*.xem", AntiCheat.Other),
		("*_BE.exe", AntiCheat.BattlEye)
	};

	internal static readonly string[] RootMarkers = new string[1] { "data\\.build.config" };

	public static readonly string MarkersStamp = string.Join('|', MarkerNames.Concat(RootMarkers));

	private static readonly EnumerationOptions AllNames = new EnumerationOptions
	{
		IgnoreInaccessible = false,
		AttributesToSkip = FileAttributes.None
	};

	public const int MaxEntries = 2000000;

	public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30L);

	internal static readonly string[] TruckersMpExes = new string[2] { "eurotrucks2.exe", "amtrucks.exe" };

	internal static readonly string[] AntiCheatIds = new string[8] { "steam:2139460", "steam:2767030", "steam:2943650", "steam:2782500", "steam:1203220", "steam:2483190", "steam:2073850", "xbox:Microsoft.ForteBaseGame_8wekyb3d8bbwe" };

	internal static string SystemDir { get; set; } = Environment.SystemDirectory;

	internal static HashSet<string> WrappableAgs { get; set; } = new HashSet<string>(new _003C_003Ez__ReadOnlySingleElementList<string>("C1F0BD493B470FA369D681C370BBD46E019A7196DBE03D91272EA0BE214B3384"), StringComparer.OrdinalIgnoreCase);

	public static IEnumerable<string> MarkerNames => Markers.Select(((string Name, AntiCheat Kind) m) => m.Name);

	public static string? FindExe(string installDir, string? launcherExe = null)
	{
		if (!Directory.Exists(installDir))
		{
			return null;
		}
		string text = ShippingExe(installDir, launcherExe);
		if (text != null)
		{
			return text;
		}
		HashSet<string> excluded = XboxSource.NotTheGame(installDir);
		List<FileInfo> list = (from same in (from f in UnrealFolders(installDir).SelectMany((string d) => Directory.EnumerateFiles(d, "*.exe", Flat))
				where !IsEngineFolder(installDir, f) && !InStaging(installDir, f) && !IsHelper(f) && !excluded.Contains(Path.GetFullPath(f))
				select new FileInfo(f)).GroupBy((FileInfo f) => f.Name, StringComparer.OrdinalIgnoreCase)
			select same.MinBy((FileInfo f) => Path.GetRelativePath(installDir, f.FullName).Count((char c) => c == Path.DirectorySeparatorChar)) into f
			orderby IsShipping(f.FullName) && !NotTheGameBuild.Any((string n) => BuildName(f.FullName).Contains(n, StringComparison.OrdinalIgnoreCase)) descending, f.Length descending
			select f).ToList();
		if (list.Count > 0)
		{
			return UnrealExe(installDir, list);
		}
		string text2 = BattlEyeTarget(installDir);
		if (text2 != null)
		{
			return text2;
		}
		if (launcherExe != null)
		{
			string fullPath = Path.GetFullPath(Path.Combine(installDir, launcherExe));
			if (File.Exists(fullPath))
			{
				return RedLauncherTarget(installDir, fullPath) ?? fullPath;
			}
		}
		List<FileInfo> list2 = (from f in Directory.EnumerateFiles(installDir, "*.exe", Deep)
			where !NotTheGameExe(installDir, f, excluded)
			select new FileInfo(f)).ToList();
		List<string> list3 = (from f in list2
			where f.Name.Equals("start_protected_game.exe", StringComparison.OrdinalIgnoreCase)
			select EacExecutable(installDir, f.FullName)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (list3 != null && list3.Count == 1)
		{
			string text3 = list3[0];
			if (text3 != null)
			{
				return text3;
			}
		}
		FileInfo fileInfo = list2.FirstOrDefault((FileInfo f) => Directory.Exists(Path.ChangeExtension(f.FullName, null) + "_Data"));
		if (fileInfo != null)
		{
			return fileInfo.FullName;
		}
		FileInfo fileInfo2 = (from f in list2
			orderby (f.DirectoryName.Length > installDir.TrimEnd('\\').Length) ? 1 : 0, f.Length descending
			select f).FirstOrDefault();
		if (fileInfo2 != null)
		{
			return NotPlus(LaunchedExe(installDir, fileInfo2, list2), list2);
		}
		return null;
	}

	private static string NotPlus(string exe, IReadOnlyList<FileInfo> exes)
	{
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(exe);
		if (!fileNameWithoutExtension.EndsWith("_Plus", StringComparison.OrdinalIgnoreCase))
		{
			return exe;
		}
		string? directoryName = Path.GetDirectoryName(exe);
		string text = fileNameWithoutExtension;
		int length = "_Plus".Length;
		string plain = Path.Combine(directoryName, text.Substring(0, text.Length - length) + ".exe");
		return exes.FirstOrDefault((FileInfo f) => f.FullName.Equals(plain, StringComparison.OrdinalIgnoreCase))?.FullName ?? exe;
	}

	internal static bool NotTheGameExe(string installDir, string exe, ISet<string>? excluded = null)
	{
		if (!NotTheGame.Any((string s) => Path.GetRelativePath(installDir, exe).Contains(s, StringComparison.OrdinalIgnoreCase)) && !InStaging(installDir, exe) && !IsHelper(exe))
		{
			return (excluded ?? XboxSource.NotTheGame(installDir)).Contains(Path.GetFullPath(exe));
		}
		return true;
	}

	private static string UnrealExe(string installDir, List<FileInfo> bySize)
	{
		string fullName = bySize[0].FullName;
		if (bySize.Count == 1 || DetectAntiCheat(new Game("", "", Store.Other, installDir, fullName), quick: true) != AntiCheat.None || !Is32Bit(fullName))
		{
			return fullName;
		}
		return bySize.Skip(1).FirstOrDefault((FileInfo f) => !Is32Bit(f.FullName))?.FullName ?? fullName;
	}

	public static string GameExe(string installDir, string exe, Action<string>? ambiguous = null)
	{
		if (IsShipping(exe))
		{
			return exe;
		}
		exe = EacExecutable(installDir, exe) ?? exe;
		string text;
		if (!IsShipping(exe))
		{
			text = ShippingExe(installDir, exe, ambiguous);
			if (text == null)
			{
				return exe;
			}
		}
		else
		{
			text = exe;
		}
		return text;
	}

	internal static string? EacExecutable(string installDir, string exe)
	{
		try
		{
			string fullName = EacSettings(exe).Directory.Parent.FullName;
			JsonElement? jsonElement = ReadEacSettings(exe);
			if (!jsonElement.HasValue || !jsonElement.GetValueOrDefault().TryGetProperty("executable", out var value) || value.ValueKind != JsonValueKind.String)
			{
				return null;
			}
			string target = Path.GetFullPath(Path.Combine(fullName, value.GetString()));
			if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || target.Equals(Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase) || Path.GetFileName(target).IndexOfAny(new char[2] { '*', '?' }) >= 0 || !InsideNoLinks(installDir, target) || NotTheGameExe(installDir, target) || XboxSource.LauncherOrUtility(Path.GetFileName(target)))
			{
				return null;
			}
			if (Path.GetDirectoryName(target).Equals(fullName, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(exe).Equals("start_protected_game.exe", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			return Directory.EnumerateFiles(Path.GetDirectoryName(target), Path.GetFileName(target), Flat).FirstOrDefault((string f) => f.Equals(target, StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException || ex is NotSupportedException || ex is InvalidOperationException) ? true : false)
		{
			return null;
		}
	}

	internal static string? EacProduct(string exe)
	{
		try
		{
			JsonElement? jsonElement = ReadEacSettings(exe);
			object result;
			if (jsonElement.HasValue)
			{
				JsonElement valueOrDefault = jsonElement.GetValueOrDefault();
				if (valueOrDefault.TryGetProperty("productid", out var value) && value.ValueKind == JsonValueKind.String && valueOrDefault.TryGetProperty("sandboxid", out var value2) && value2.ValueKind == JsonValueKind.String)
				{
					result = value.GetString() + "/" + value2.GetString();
					goto IL_0067;
				}
			}
			result = null;
			goto IL_0067;
			IL_0067:
			return (string?)result;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException || ex is NotSupportedException || ex is InvalidOperationException) ? true : false)
		{
			return null;
		}
	}

	private static JsonElement? ReadEacSettings(string exe)
	{
		FileInfo fileInfo = EacSettings(exe);
		if (!fileInfo.Exists || fileInfo.Length > 65536)
		{
			return null;
		}
		using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(fileInfo.FullName), new JsonDocumentOptions
		{
			AllowTrailingCommas = true,
			CommentHandling = JsonCommentHandling.Skip
		});
		return (jsonDocument.RootElement.ValueKind == JsonValueKind.Object) ? new JsonElement?(jsonDocument.RootElement.Clone()) : ((JsonElement?)null);
	}

	private static FileInfo EacSettings(string exe)
	{
		return new FileInfo(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe)), "EasyAntiCheat", "Settings.json"));
	}

	internal static string EacSettingsStamp(string exe)
	{
		try
		{
			FileInfo fileInfo = EacSettings(exe);
			return (fileInfo != null && fileInfo.Exists) ? $"|eac:{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}" : "";
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) ? true : false)
		{
			return "";
		}
	}

	public static string? ShippingExe(string installDir, string? named = null, Action<string>? ambiguous = null)
	{
		if (!Directory.Exists(installDir))
		{
			return null;
		}
		List<string> list2;
		try
		{
			bool unrealRoot = Directory.Exists(Path.Combine(installDir, "Engine"));
			HashSet<string> excluded = XboxSource.NotTheGame(installDir);
			List<string> list = Candidates(UnrealFolders(installDir));
			if (list.Count == 0)
			{
				list = Candidates(DeepUnrealFolders(installDir));
			}
			list2 = (from same in list.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
				select same.MinBy((string f) => Path.GetRelativePath(installDir, f).Count((char c) => c == Path.DirectorySeparatorChar))).ToList();
			List<string> Candidates(IEnumerable<string> folders)
			{
				return (from f in folders.SelectMany((string d) => Directory.EnumerateFiles(d, "*-Shipping.exe", Flat))
					where IsShipping(f) && !excluded.Contains(Path.GetFullPath(f)) && !IsEngineFolder(installDir, f) && !InStaging(installDir, f) && !IsHelper(f) && !NotTheGameBuild.Any((string n) => BuildName(f).Contains(n, StringComparison.OrdinalIgnoreCase)) && (unrealRoot || Directory.Exists(Path.Combine(Path.GetDirectoryName(ProjectDir(f)), "Engine")))
					select f).ToList();
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
		if (list2.Count <= 1)
		{
			return list2.FirstOrDefault();
		}
		List<string> names = new string[2]
		{
			(named == null) ? null : Path.GetFileNameWithoutExtension(named),
			Path.GetFileName(DirKey(installDir))
		}.OfType<string>().ToList();
		List<string> list3 = list2.Where((string f) => names.Any((string n) => n.Equals(BuildName(f), StringComparison.OrdinalIgnoreCase) || n.Equals(Path.GetFileName(ProjectDir(f)), StringComparison.OrdinalIgnoreCase))).ToList();
		if (list3 != null && list3.Count == 1)
		{
			return list3[0];
		}
		ambiguous?.Invoke(installDir + ": several Shipping exes and none tied to " + (Path.GetFileName(named) ?? "the install") + ", kept as named: " + string.Join(", ", list2.Select((string f) => Path.GetRelativePath(installDir, f))));
		return null;
	}

	private static string BuildName(string shipping)
	{
		string fileName = Path.GetFileName(shipping);
		int length = ("-" + Path.GetFileName(Path.GetDirectoryName(shipping)) + "-Shipping.exe").Length;
		return fileName.Substring(0, fileName.Length - length);
	}

	private static string ProjectDir(string shipping)
	{
		return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(shipping)));
	}

	public static bool IsShipping(string exe)
	{
		string directoryName = Path.GetDirectoryName(exe);
		if (directoryName != null && string.Equals(Path.GetFileName(Path.GetDirectoryName(directoryName)), "Binaries", StringComparison.OrdinalIgnoreCase))
		{
			return Path.GetFileName(exe).EndsWith("-" + Path.GetFileName(directoryName) + "-Shipping.exe", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	internal static bool InBinaries(string exe)
	{
		string directoryName = Path.GetDirectoryName(exe);
		if (directoryName != null && Path.GetFileName(directoryName).StartsWith("Win", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(Path.GetFileName(Path.GetDirectoryName(directoryName)), "Binaries", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static IEnumerable<string> UnrealFolders(string installDir)
	{
		return PlatformFolders(Directory.EnumerateDirectories(installDir, "*", Flat).SelectMany((string d) => Directory.EnumerateDirectories(d, "*", Flat).SelectMany(Packaged).Prepend(d)).Prepend(installDir));
	}

	private static IEnumerable<string> DeepUnrealFolders(string installDir)
	{
		return PlatformFolders(from p in (from d in Directory.EnumerateDirectories(installDir, "*", Flat).SelectMany((string d) => Directory.EnumerateDirectories(d, "*", Flat))
				where Directory.Exists(Path.Combine(d, "Engine"))
				select d).SelectMany((string d) => Directory.EnumerateDirectories(d, "*", Flat))
			where !Path.GetFileName(p).Equals("Engine", StringComparison.OrdinalIgnoreCase)
			select p);
	}

	private static IEnumerable<string> PlatformFolders(IEnumerable<string> projects)
	{
		return from d in projects.Select((string p) => Path.Combine(p, "Binaries")).Where(Directory.Exists).SelectMany((string b) => Directory.EnumerateDirectories(b, "Win*", Flat))
			where !Path.GetFileName(d).Equals("Win32", StringComparison.OrdinalIgnoreCase)
			select d;
	}

	private static IEnumerable<string> Packaged(string dir)
	{
		if (!Path.GetFileName(dir).Equals("WindowsNoEditor", StringComparison.OrdinalIgnoreCase))
		{
			return new _003C_003Ez__ReadOnlySingleElementList<string>(dir);
		}
		return Directory.EnumerateDirectories(dir, "*", Flat).Prepend(dir);
	}

	internal static bool Is32Bit(string exe)
	{
		try
		{
			using PEReader pEReader = PeFile.Open(exe);
			return pEReader.PEHeaders.CoffHeader.Machine == Machine.I386;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException) ? true : false)
		{
			return false;
		}
	}

	private static string LaunchedExe(string installDir, FileInfo guess, IReadOnlyList<FileInfo> exes)
	{
		string text = RedLauncherTarget(installDir, guess.FullName);
		if (text != null)
		{
			return text;
		}
		List<FileInfo> list = exes.Where((FileInfo f) => f.Length > guess.Length).ToList();
		if (list.Count == 0 || ImportsGraphics(guess.FullName) != false || DetectAntiCheat(new Game("", "", Store.Other, installDir, guess.FullName)) != AntiCheat.None)
		{
			return guess.FullName;
		}
		List<(string, bool?)> source = list.Select((FileInfo f) => (FullName: f.FullName, Imports: ImportsGraphics(f.FullName))).ToList();
		if (source.All(((string FullName, bool? Imports) x) => x.Imports.HasValue))
		{
			List<(string, bool?)> list2 = source.Where(((string FullName, bool? Imports) x) => x.Imports == true).ToList();
			if (list2 != null && list2.Count == 1)
			{
				return list2[0].Item1;
			}
		}
		return guess.FullName;
	}

	internal static string? BattlEyeTarget(string installDir)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string item in Directory.EnumerateFiles(installDir, "BELauncher.ini", Deep))
		{
			string directoryName = Path.GetDirectoryName(item);
			if (!Path.GetFileName(directoryName).Equals("BattlEye", StringComparison.OrdinalIgnoreCase) || InStaging(installDir, item))
			{
				continue;
			}
			string[] source;
			try
			{
				source = File.ReadAllLines(item);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				continue;
			}
			string[]? array = source.Select((string l) => l.Split('=', 2)).FirstOrDefault((string[] p) => p.Length == 2 && p[0].Trim().Equals("64BitExe", StringComparison.OrdinalIgnoreCase));
			string text = ((array != null) ? array[1].Trim() : null);
			if (string.IsNullOrEmpty(text) || Path.IsPathRooted(text) || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
			{
				continue;
			}
			try
			{
				string fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(directoryName), text));
				if (InsideNoLinks(installDir, fullPath) && !InStaging(installDir, fullPath))
				{
					hashSet.Add(fullPath);
				}
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException || ex2 is ArgumentException || ex2 is NotSupportedException) ? true : false)
			{
			}
		}
		if (hashSet.Count != 1)
		{
			return null;
		}
		return hashSet.First();
	}

	private static bool InsideNoLinks(string installDir, string target)
	{
		string text = DirKey(installDir);
		if (!target.StartsWith(text + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
		{
			return false;
		}
		string text2 = target;
		while (text2.Length > text.Length)
		{
			FileSystemInfo fileSystemInfo = ((text2 == target) ? ((FileSystemInfo)new FileInfo(text2)) : ((FileSystemInfo)new DirectoryInfo(text2)));
			if (fileSystemInfo != null && fileSystemInfo.LinkTarget != null)
			{
				return false;
			}
			text2 = Path.GetDirectoryName(text2);
		}
		return true;
	}

	private static string? RedLauncherTarget(string installDir, string exe)
	{
		FileInfo config = new FileInfo(Path.Combine(Path.GetDirectoryName(exe), "launcher-configuration.json"));
		if (!config.Exists || config.Length > 65536)
		{
			return null;
		}
		try
		{
			JsonElement x;
			using (JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(config.FullName)))
			{
				JsonElement rootElement = jsonDocument.RootElement;
				if (rootElement.ValueKind != JsonValueKind.Object)
				{
					return null;
				}
				if (rootElement.TryGetProperty("executablePath", out var value) && value.ValueKind == JsonValueKind.String)
				{
					string text = Target(value.GetString());
					if (text != null)
					{
						return text;
					}
				}
				if (!rootElement.TryGetProperty("executables", out var value2) || value2.ValueKind != JsonValueKind.Array)
				{
					return null;
				}
				string fallback = ((rootElement.TryGetProperty("fallback", out var value3) && value3.ValueKind == JsonValueKind.String) ? value3.GetString() : null);
				List<JsonElement> list = (from e in value2.EnumerateArray()
					where e.ValueKind == JsonValueKind.Object
					select e).ToList();
				if (list.Count == 0)
				{
					return null;
				}
				if (!list.FirstOrDefault((JsonElement e) => e.TryGetProperty("description", out var value4) && value4.ValueKind == JsonValueKind.String && value4.GetString() == fallback, list[0]).TryGetProperty("executable", out x) || x.ValueKind != JsonValueKind.Object)
				{
					return null;
				}
				string text2 = S("fileName");
				return (text2 != null && text2.Length > 0) ? Target(Path.Combine(S("directoryPath") ?? "", text2)) : null;
			}
			string? S(string k)
			{
				if (!x.TryGetProperty(k, out var value4) || value4.ValueKind != JsonValueKind.String)
				{
					return null;
				}
				return value4.GetString();
			}
		}
		catch (Exception ex) when ((ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException) ? true : false)
		{
			return null;
		}
		string? Target(string rel)
		{
			if (!Path.IsPathRooted(rel) && rel.IndexOfAny(new char[3] { ':', '~', '\0' }) < 0)
			{
				string fullPath = Path.GetFullPath(Path.Combine(config.DirectoryName, rel));
				if (fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !NotTheGameExe(installDir, fullPath) && InsideNoLinks(installDir, fullPath))
				{
					return fullPath;
				}
			}
			return null;
		}
	}

	internal static bool? ImportsGraphics(string exe)
	{
		try
		{
			bool agility;
			return CarvedReader.PeImports(exe, out agility).Any((string d) => GraphicsDlls.Contains(d, StringComparer.OrdinalIgnoreCase) || d.Contains("-RHI-", StringComparison.OrdinalIgnoreCase) || d.EndsWith("-RHI.dll", StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException || ex is InvalidOperationException) ? true : false)
		{
			return null;
		}
	}

	internal static bool? StreamlineFirst(string exe, List<string> files)
	{
		string directoryName = Path.GetDirectoryName(exe);
		string text = Path.Combine(directoryName, "sl.interposer.dll");
		files.Add(text);
		if (!File.Exists(text))
		{
			return false;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		Stack<(string, bool)> stack = new Stack<(string, bool)>(new _003C_003Ez__ReadOnlySingleElementList<(string, bool)>((exe, true)));
		try
		{
			(string, bool) result;
			while (stack.TryPop(out result))
			{
				using PEReader pe = PeFile.Open(result.Item1);
				if (result.Item2 && PeFile.Forwarders(pe).Any((string f) => f.StartsWith("d3d12.", StringComparison.OrdinalIgnoreCase)))
				{
					return false;
				}
				foreach (string item in CarvedReader.PeImports(pe, delayed: false))
				{
					if (item.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
					if (item.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) || item.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase) || !hashSet.Add(item))
					{
						continue;
					}
					string text2 = Path.Combine(directoryName, item);
					files.Add(text2);
					if (File.Exists(text2))
					{
						stack.Push((text2, true));
						continue;
					}
					string text3 = Path.Combine(SystemDir, item);
					if (File.Exists(text3))
					{
						stack.Push((text3, false));
						continue;
					}
					return null;
				}
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException || ex is InvalidOperationException) ? true : false)
		{
			return null;
		}
		return true;
	}

	internal static bool AgsMakesTheDevice(string exe)
	{
		string directoryName = Path.GetDirectoryName(exe);
		string text = Path.Combine(directoryName, "amd_ags_x64.dll");
		try
		{
			if (ScsKiller.IsOurProxy(text))
			{
				text = Path.Combine(directoryName, "amd_ags_real.dll");
			}
			if (!File.Exists(text) || !WrappableAgs.Contains(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(text)))))
			{
				return false;
			}
			using PEReader pe = PeFile.Open(exe);
			return PeFile.ImportedFunctions(pe, "amd_ags_x64.dll").Contains("agsDriverExtensionsDX12_CreateDevice");
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException || ex is InvalidOperationException) ? true : false)
		{
			return false;
		}
	}

	internal static string Stamp(IEnumerable<string> files)
	{
		return string.Join('|', files.Select((string f) =>
		{
			FileInfo fileInfo = new FileInfo(f);
			return (fileInfo != null && fileInfo.Exists) ? $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}" : "-";
		}));
	}

	internal static IEnumerable<string> GraphicsExes(string installDir)
	{
		return from f in Directory.EnumerateFiles(installDir, "*.exe", Deep)
			where !NotTheGame.Any((string s) => Path.GetRelativePath(installDir, f).Contains(s, StringComparison.OrdinalIgnoreCase)) && !IsEngineFolder(installDir, f) && !InStaging(installDir, f) && !IsHelper(f)
			where ImportsGraphics(f) == true
			select f;
	}

	public static bool Inside(string dir, string path)
	{
		if (dir.Length > 0)
		{
			return Path.GetFullPath(path).StartsWith(DirKey(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	public static string? FinalPath(string dir)
	{
		using SafeFileHandle safeFileHandle = CreateFileW(dir, 0u, 7u, 0, 3u, 33554432u, 0);
		return safeFileHandle.IsInvalid ? null : FinalPath(safeFileHandle);
	}

	public static (byte[]? Id, string? Final)? Identity(string path)
	{
		using SafeFileHandle safeFileHandle = CreateFileW(path, 128u, 7u, 0, 3u, 33554432u, 0);
		if (safeFileHandle.IsInvalid)
		{
			return null;
		}
		byte[] array = new byte[24];
		return (GetFileInformationByHandleEx(safeFileHandle, 18, array, array.Length) ? array : null, FinalPath(safeFileHandle));
	}

	private static string? FinalPath(SafeFileHandle h)
	{
		char[] array = new char[32768];
		uint finalPathNameByHandleW = GetFinalPathNameByHandleW(h, array, array.Length, 0u);
		if (finalPathNameByHandleW == 0 || finalPathNameByHandleW >= array.Length)
		{
			return null;
		}
		string text = new string(array, 0, (int)finalPathNameByHandleW);
		if (!text.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal))
		{
			if (!text.StartsWith("\\\\?\\", StringComparison.Ordinal))
			{
				return text;
			}
			return text.Substring(4);
		}
		return "\\\\" + text.Substring(8);
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, char[] name, int size, uint flags);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, byte[] info, int size);

	public static string FolderName(string dir)
	{
		return Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
	}

	public static string DirKey(string dir)
	{
		if (dir.Length != 0)
		{
			return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
		}
		return dir;
	}

	internal static bool IsHelper(string exe)
	{
		if (!Helpers.Any((string h) => Path.GetFileName(exe).Contains(h, StringComparison.OrdinalIgnoreCase)))
		{
			return TrialBeside(exe);
		}
		return true;
	}

	private static bool TrialBeside(string exe)
	{
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(exe);
		if (fileNameWithoutExtension.EndsWith("_Trial", StringComparison.OrdinalIgnoreCase))
		{
			string? path = Path.GetDirectoryName(exe) ?? "";
			string text = fileNameWithoutExtension;
			return File.Exists(Path.Combine(path, text.Substring(0, text.Length - 6) + ".exe"));
		}
		return false;
	}

	internal static bool WindowsFolder(string name)
	{
		return WindowsFolders.Contains(name, StringComparer.OrdinalIgnoreCase);
	}

	private static bool InStaging(string installDir, string path)
	{
		return Path.GetRelativePath(installDir, Path.GetDirectoryName(path)).Split(Path.DirectorySeparatorChar).Any((string seg) => Staging.Contains(seg, StringComparer.OrdinalIgnoreCase) || WindowsFolder(seg));
	}

	private static bool IsEngineFolder(string installDir, string path)
	{
		return Path.GetRelativePath(installDir, path).StartsWith("Engine" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	private static string? RootMarker(string dir)
	{
		return RootMarkers.Select((string m) => Path.Combine(dir, m)).FirstOrDefault(File.Exists);
	}

	public static AntiCheat DetectAntiCheat(Game game, bool quick = false, TimeSpan? budget = null, AntiCheat ignore = AntiCheat.None, Action<string>? at = null, string? steamRoot = null)
	{
		if (game.Id.StartsWith("battlenet:", StringComparison.Ordinal) || game.Id.StartsWith("hoyoplay:", StringComparison.Ordinal))
		{
			return AntiCheat.Other;
		}
		if (game.Id.StartsWith("riot:", StringComparison.Ordinal) || RiotGames(game.InstallDir) || RiotGames(game.ExePath))
		{
			return AntiCheat.Other;
		}
		if (AntiCheatIds.Contains(game.Id, StringComparer.OrdinalIgnoreCase))
		{
			return AntiCheat.Other;
		}
		string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.InstallDir));
		string directoryName = Path.GetDirectoryName(Path.GetFullPath(game.ExePath));
		HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		int seen = 0;
		Stopwatch clock = Stopwatch.StartNew();
		try
		{
			if (TruckersMpExes.Contains(Path.GetFileName(game.ExePath), StringComparer.OrdinalIgnoreCase) && TruckersMpHere())
			{
				return AntiCheat.Other;
			}
			string text = directoryName;
			while (text != null && Inside(text) && text.Length > install.Length)
			{
				DirectoryInfo directoryInfo = new DirectoryInfo(text);
				if (directoryInfo != null && directoryInfo.Exists && (directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0 && directoryInfo.LinkTarget != null)
				{
					return AntiCheat.Other;
				}
				text = Path.GetDirectoryName(text);
			}
			string[] array = ((!quick && Inside(directoryName)) ? new string[1] { install } : new string[2] { install, directoryName });
			foreach (string text2 in array)
			{
				if (ignore != AntiCheat.Other)
				{
					string text3 = RootMarker(text2);
					if (text3 != null)
					{
						at?.Invoke(text3);
						return AntiCheat.Other;
					}
				}
				AntiCheat antiCheat = Walk(text2);
				if (antiCheat != AntiCheat.None)
				{
					return antiCheat;
				}
			}
			string text4 = directoryName;
			while (text4 != null && Inside(text4))
			{
				AntiCheat antiCheat2 = Marker(Path.GetFileName(text4));
				if (antiCheat2 != AntiCheat.None)
				{
					at?.Invoke(text4);
					return antiCheat2;
				}
				text4 = Path.GetDirectoryName(text4);
			}
			if (game.Store == Store.Manual)
			{
				string directoryName2 = Path.GetDirectoryName(install);
				while (directoryName2 != null && Path.GetPathRoot(directoryName2) != directoryName2 && !ManualSource.IsLibrary(directoryName2))
				{
					if (ignore != AntiCheat.Other)
					{
						string text5 = RootMarker(directoryName2);
						if (text5 != null)
						{
							at?.Invoke(text5);
							return AntiCheat.Other;
						}
					}
					AntiCheat antiCheat3 = Walk(directoryName2, deep: false);
					if (antiCheat3 != AntiCheat.None)
					{
						return antiCheat3;
					}
					directoryName2 = Path.GetDirectoryName(directoryName2);
				}
			}
			if (Vac.LaunchExes(game, steamRoot).Any((string e) => Marker(e) != AntiCheat.None))
			{
				return AntiCheat.Other;
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException) ? true : false)
		{
			return AntiCheat.Other;
		}
		return AntiCheat.None;
		bool Inside(string d)
		{
			if (!d.Equals(install, StringComparison.OrdinalIgnoreCase))
			{
				return d.StartsWith(install + "\\", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
		AntiCheat Marker(string name)
		{
			AntiCheat antiCheat4 = GameFiles.Marker(name);
			if (antiCheat4 != ignore)
			{
				return antiCheat4;
			}
			return AntiCheat.None;
		}
		AntiCheat Walk(string root, bool deep = true)
		{
			Stack<string> stack = new Stack<string>(new _003C_003Ez__ReadOnlySingleElementList<string>(root));
			string result;
			while (stack.TryPop(out result))
			{
				if (visited.Add(result))
				{
					if (clock.Elapsed > (budget ?? Budget))
					{
						return AntiCheat.Other;
					}
					IEnumerator<FileSystemInfo> enumerator;
					try
					{
						enumerator = new DirectoryInfo(result).EnumerateFileSystemInfos("*", AllNames).GetEnumerator();
					}
					catch (DirectoryNotFoundException) when (result == root)
					{
						continue;
					}
					using (enumerator)
					{
						while (enumerator.MoveNext())
						{
							FileSystemInfo current = enumerator.Current;
							if (++seen > 2000000 || clock.Elapsed > (budget ?? Budget))
							{
								return AntiCheat.Other;
							}
							AntiCheat antiCheat4 = Marker(current.Name);
							if (antiCheat4 != AntiCheat.None)
							{
								at?.Invoke(current.FullName);
								return antiCheat4;
							}
							if (deep && !quick && current is DirectoryInfo directoryInfo2 && ((directoryInfo2.Attributes & FileAttributes.ReparsePoint) == 0 || directoryInfo2.LinkTarget == null))
							{
								stack.Push(directoryInfo2.FullName);
							}
						}
					}
				}
			}
			return AntiCheat.None;
		}
	}

	internal static bool RiotGames(string path)
	{
		return Path.GetFullPath(path).Split(Path.DirectorySeparatorChar).Contains("Riot Games", StringComparer.OrdinalIgnoreCase);
	}

	internal static bool TruckersMp(IEnumerable<string> folders, IEnumerable<string> programs)
	{
		if (!folders.Any(Directory.Exists))
		{
			return programs.Any((string p) => p.Contains("TruckersMP", StringComparison.OrdinalIgnoreCase));
		}
		return true;
	}

	internal static bool TruckersMpHere()
	{
		return TruckersMp(TruckersMpFolders(), InstalledPrograms());
	}

	private static IEnumerable<string> TruckersMpFolders()
	{
		return from f in new (Environment.SpecialFolder, string)[5]
			{
				(Environment.SpecialFolder.ApplicationData, "TruckersMP"),
				(Environment.SpecialFolder.LocalApplicationData, "TruckersMP"),
				(Environment.SpecialFolder.CommonApplicationData, "TruckersMP"),
				(Environment.SpecialFolder.Personal, "ETS2MP"),
				(Environment.SpecialFolder.Personal, "ATSMP")
			}.Select(((Environment.SpecialFolder Root, string Name) f) => (Root: Environment.GetFolderPath(f.Root), Name: f.Name))
			where f.Root.Length > 0
			select Path.Combine(f.Root, f.Name);
	}

	private static IEnumerable<string> InstalledPrograms()
	{
		RegistryHive[] array = new RegistryHive[2]
		{
			RegistryHive.CurrentUser,
			RegistryHive.LocalMachine
		};
		foreach (RegistryHive hive in array)
		{
			RegistryView[] array2 = new RegistryView[2]
			{
				RegistryView.Registry64,
				RegistryView.Registry32
			};
			foreach (RegistryView view in array2)
			{
				using RegistryKey b = RegistryKey.OpenBaseKey(hive, view);
				using RegistryKey root = b.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall");
				string[] array3 = root?.GetSubKeyNames() ?? Array.Empty<string>();
				foreach (string name in array3)
				{
					using RegistryKey key = root.OpenSubKey(name);
					if (key?.GetValue("DisplayName") is string { Length: >0 } text)
					{
						yield return text;
					}
				}
			}
		}
	}

	internal static AntiCheat Marker(string name)
	{
		(string, AntiCheat)[] markers = Markers;
		for (int i = 0; i < markers.Length; i++)
		{
			var (text, result) = markers[i];
			if ((text[0] == '*') ? name.EndsWith(text.Substring(1), StringComparison.OrdinalIgnoreCase) : name.Equals(text, StringComparison.OrdinalIgnoreCase))
			{
				return result;
			}
		}
		return AntiCheat.None;
	}
}
