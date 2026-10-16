using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Games;

public static class ReShade
{
	private sealed record KeptProbe(long Length, DateTime Written, string Identity, bool? Flag, int? Number, string? Text);

	private static readonly EnumerationOptions Flat = new EnumerationOptions
	{
		IgnoreInaccessible = true,
		AttributesToSkip = FileAttributes.None
	};

	private static readonly byte[][] Identity = new byte[1][] { Encoding.Unicode.GetBytes("ReShade post-processing injector") };

	private static readonly byte[][] Limited = new byte[1][] { "only limited add-on functionality"u8.ToArray() };

	private static readonly string[] Names = new string[10] { "dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "opengl32.dll", "dinput8.dll", "ReShade64.dll", "d3d12.scskiller-next.dll", "d3d11.scskiller-next.dll" };

	private static readonly string[] OptiScalerNames = new string[7] { "dxgi.dll", "d3d12.dll", "winmm.dll", "version.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll" };

	private static readonly byte[][] ModMarkers = new byte[1][] { "utils::shader attached."u8.ToArray() };

	private static readonly Regex Loading = new Regex("Loading add-on from '(.+)' \\.\\.\\.$");

	private static readonly Regex Registered = new Regex("Registered add-on \"(.+?)\"");

	private static readonly Regex Pipeline = new Regex("\\| \\[(.+?)\\] mods::shader::(OnCreatePipelineLayout\\(will insert|OnInitPipelineLayout\\(Cloning)");

	private const long MaxBytes = 134217728L;

	private const int NotRead = -2;

	private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Identity, object? Value)> Probed = new ConcurrentDictionary<string, (long, DateTime, string, object)>(StringComparer.OrdinalIgnoreCase);

	private static long probesVersion;

	private static readonly Dictionary<string, long> probesSaved = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private static readonly Lock probeGate = new Lock();

	private static readonly string[] OptiScalerLibraries = new string[12]
	{
		"amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_denoiser_dx12.dll", "amd_fidelityfx_radiancecache_dx12.dll", "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "libxell.dll",
		"_nvngx.dll", "nvngx.dll"
	};

	private const long MaxLibraryBytes = 134217728L;

	private const long LibraryBudget = 536870912L;

	private static readonly byte[][] DxvkVars = new byte[2][]
	{
		"DXVK_LOG_LEVEL"u8.ToArray(),
		"DXVK_HUD"u8.ToArray()
	};

	public static IReadOnlyDictionary<string, AddonKind> RenoDxTable => field ?? (field = ParseTable(ContentFile.Embedded("SCSKiller.Core.Games.shader-mods.json")));

	public static void LoadProbes(string file)
	{
		try
		{
			if (!File.Exists(file))
			{
				return;
			}
			Dictionary<string, KeptProbe> dictionary = JsonSerializer.Deserialize<Dictionary<string, KeptProbe>>(File.ReadAllBytes(file));
			if (dictionary == null)
			{
				return;
			}
			foreach (KeyValuePair<string, KeptProbe> item in dictionary)
			{
				item.Deconstruct(out var key, out var value);
				string key2 = key;
				KeptProbe keptProbe = value;
				if ((object)keptProbe != null)
				{
					key = keptProbe.Identity;
					if (key != null && key.Length > 1)
					{
						Probed.TryAdd(key2, (keptProbe.Length, keptProbe.Written, keptProbe.Identity, ((object)keptProbe.Flag) ?? ((object)keptProbe.Number) ?? keptProbe.Text));
					}
				}
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
		{
		}
	}

	public static void SaveProbes(string file)
	{
		using (probeGate.EnterScope())
		{
			long num = Interlocked.Read(in probesVersion);
			if (probesSaved.GetValueOrDefault(file) != num)
			{
				Dictionary<string, KeptProbe> value = Probed.Where((KeyValuePair<string, (long Length, DateTime Written, string Identity, object Value)> p) =>
				{
					object item = p.Value.Value;
					return (item is bool || item is int || item is string) ? true : false;
				}).ToDictionary((KeyValuePair<string, (long Length, DateTime Written, string Identity, object Value)> p) => p.Key, (KeyValuePair<string, (long Length, DateTime Written, string Identity, object Value)> p) => new KeptProbe(p.Value.Length, p.Value.Written, p.Value.Identity, p.Value.Value as bool?, p.Value.Value as int?, p.Value.Value as string));
				AppStore.WriteAtomic(file, JsonSerializer.SerializeToUtf8Bytes(value));
				probesSaved[file] = num;
			}
		}
	}

	public static void ForgetProbes()
	{
		Probed.Clear();
	}

	private static Dictionary<string, AddonKind> ParseTable(string json)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(json);
		Dictionary<string, AddonKind> dictionary = new Dictionary<string, AddonKind>(StringComparer.OrdinalIgnoreCase);
		(string, AddonKind)[] array = new (string, AddonKind)[2]
		{
			("layout_injecting", AddonKind.LayoutInjecting),
			("replaces_shaders", AddonKind.ReplacesShaders)
		};
		for (int i = 0; i < array.Length; i++)
		{
			var (propertyName, value) = array[i];
			foreach (JsonElement item in jsonDocument.RootElement.GetProperty(propertyName).EnumerateArray())
			{
				dictionary[item.GetString()] = value;
			}
		}
		return dictionary;
	}

	public static string? RenoDxFolder(string fileName)
	{
		if (!fileName.StartsWith("renodx-", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return Path.GetFileNameWithoutExtension(fileName).Substring("renodx-".Length);
	}

	public static ReShadeInstall? Detect(Game game)
	{
		string text = GameFiles.DirKey(Path.GetDirectoryName(game.ExePath));
		string text2 = PackageRoot(game);
		if (text2 != null)
		{
			ReShadeInstall reShadeInstall = At(text2, game, text, text2);
			if ((object)reShadeInstall != null && ReShadeInstall.Loaded.Contains(Path.GetFileName(reShadeInstall.Dll), StringComparer.OrdinalIgnoreCase))
			{
				return reShadeInstall;
			}
		}
		foreach (string item in new string[2]
		{
			text,
			GameFiles.DirKey(game.InstallDir)
		}.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			ReShadeInstall reShadeInstall2 = At(item, game, text, text2);
			if ((object)reShadeInstall2 != null)
			{
				return reShadeInstall2;
			}
		}
		return null;
	}

	public static string? PackageRoot(Game game)
	{
		if (game.Store != Store.Xbox)
		{
			return null;
		}
		return GameFiles.DirKey(game.InstallDir);
	}

	private static ReShadeInstall? At(string dir, Game game, string exeDir, string? root)
	{
		List<FileInfo> files = List(dir);
		bool flag = dir.Equals(exeDir, StringComparison.OrdinalIgnoreCase);
		FileInfo dll = files?.FirstOrDefault((FileInfo f) => (Ext(f, ".dll") || Ext(f, ".asi")) && IsReShade(f));
		FileInfo fileInfo = ((!flag && !dir.Equals(root, StringComparison.OrdinalIgnoreCase)) ? null : files?.FirstOrDefault((FileInfo f) =>
		{
			if (f != dll)
			{
				int num = Array.FindIndex(ReShadeInstall.Loaded, (string n) => n.Equals(f.Name, StringComparison.OrdinalIgnoreCase));
				if (num >= 0 && (num < 2 || Imports(game.ExePath).Contains(f.Name, StringComparer.OrdinalIgnoreCase)))
				{
					return IsSpecialK(f);
				}
			}
			return false;
		}));
		List<FileInfo> list = ((fileInfo == null) ? new List<FileInfo>() : (flag ? files : (List(exeDir) ?? new List<FileInfo>())).Where((FileInfo f) => Ext(f, ".addon") || Ext(f, ".addon64")).ToList());
		if (dll == null)
		{
			dll = ((list.Count > 0) ? fileInfo : null);
		}
		if (dll == null)
		{
			return null;
		}
		string? text = Named("ReShade.ini");
		string text2 = Named("ReShade.log");
		string log = text2;
		string text3 = text;
		string text4;
		List<string> disabled;
		bool proxyLibrary;
		if (text3 != null)
		{
			(text4, disabled, proxyLibrary) = Config(new FileInfo(text3));
		}
		else
		{
			List<string> list2 = new List<string>();
			proxyLibrary = false;
			disabled = list2;
			text4 = null;
		}
		string text5 = ((text4 != null) ? GameFiles.DirKey(Path.Combine(dir, text4)) : dir);
		IReadOnlyDictionary<string, AddonKind> verdicts = ((log != null) ? LogVerdicts(new FileInfo(log)) : null);
		List<ReShadeAddon> addons = ((text5 == dir) ? files : (List(text5) ?? new List<FileInfo>())).Where((FileInfo f) => Ext(f, ".addon") || Ext(f, ".addon64")).Concat((text5 == exeDir) ? new List<FileInfo>() : list).Select((FileInfo f) =>
		{
			ReShadeAddon a = Classify(f, verdicts, log);
			return a with
			{
				Disabled = disabled.Any((string d) => Disables(d, a, f))
			};
		})
			.ToList();
		bool? optiScalerLoads;
		FileInfo fileInfo2;
		bool optiScalerIniOdd;
		if (flag && dll.Name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase))
		{
			(optiScalerLoads, fileInfo2, optiScalerIniOdd) = OptiScalerLoads(files, game.ExePath);
		}
		else
		{
			optiScalerLoads = null;
			fileInfo2 = null;
			optiScalerIniOdd = false;
		}
		return new ReShadeInstall(dll.FullName, Find(dll, "limited", Limited) != 0, text3, log, addons, flag || dir.Equals(root, StringComparison.OrdinalIgnoreCase), optiScalerLoads, fileInfo2?.FullName, optiScalerIniOdd, fileInfo != null, proxyLibrary);
		string? Named(string name)
		{
			return files.FirstOrDefault((FileInfo f) => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.FullName;
		}
	}

	private static (bool? Loads, FileInfo? Dll, bool Odd) OptiScalerLoads(List<FileInfo> files, string exe)
	{
		FileInfo fileInfo = files.FirstOrDefault((FileInfo f) =>
		{
			int num = Array.FindIndex(OptiScalerNames, (string n) => n.Equals(f.Name, StringComparison.OrdinalIgnoreCase));
			return num >= 0 && (num < 2 || Imports(exe).Contains(f.Name, StringComparer.OrdinalIgnoreCase)) && IsOptiScaler(f);
		});
		if (fileInfo == null)
		{
			return (Loads: null, Dll: null, Odd: false);
		}
		FileInfo fileInfo2 = files.FirstOrDefault((FileInfo f) => f.Name.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase));
		return (Loads: fileInfo2 != null && (OptiScalerValue(fileInfo2, "Plugins", "LoadReshade")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false), Dll: fileInfo, Odd: fileInfo2 != null && IniAll(fileInfo2).Any(((string Section, bool Closed, string Key, string Value, string Line) e) => !e.Closed));
	}

	private static string? OptiScalerValue(FileInfo ini, string section, string key)
	{
		string item = IniEntries(ini).LastOrDefault(((string Section, bool Closed, string Key, string Value, string Line) e) => e.Closed && e.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
		if (item == null || item.Length <= 0 || item.Equals("auto", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return item;
	}

	private static IEnumerable<string> LibrariesOf(string opti)
	{
		string exeDir = Path.GetFullPath(Path.GetDirectoryName(opti));
		string main = Inside(exeDir, OptiScalerValue(new FileInfo(Path.Combine(exeDir, "OptiScaler.ini")), "Libraries", "OptiDllPath")) ?? Path.Combine(exeDir, "OptiScaler");
		if (!Directory.Exists(main))
		{
			main = exeDir;
		}
		return OptiScalerLibraries.Select((string n) => new string[2]
		{
			Path.Combine(main, n),
			Path.Combine(exeDir, n)
		}.FirstOrDefault(File.Exists)).OfType<string>();
	}

	private static string? Inside(string dir, string? relative)
	{
		if (relative == null || Path.IsPathRooted(relative))
		{
			return null;
		}
		try
		{
			string fullPath = Path.GetFullPath(Path.Combine(dir, relative));
			return fullPath.StartsWith(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
		}
		catch (Exception ex) when ((ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) ? true : false)
		{
			return null;
		}
	}

	internal static bool IsPe(string path, long length)
	{
		if (length < 64)
		{
			return false;
		}
		using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		Span<byte> buffer = stackalloc byte[64];
		fileStream.ReadExactly(buffer);
		if (buffer[0] != 77 || buffer[1] != 90)
		{
			return false;
		}
		int num = BitConverter.ToInt32(buffer.Slice(60));
		if (num < 64 || num > length - 4)
		{
			return false;
		}
		Span<byte> span = stackalloc byte[4];
		fileStream.Position = num;
		fileStream.ReadExactly(span);
		return ((ReadOnlySpan<byte>)span).SequenceEqual((ReadOnlySpan<byte>)new byte[4] { 80, 69, 0, 0 });
	}

	private static List<string> ForStage(IEnumerable<string> ini)
	{
		List<(string Section, bool Closed, string? Key, string Value, string Line)> list = (from e in IniLines(ini)
			where e.Key == null || (!e.Section.Equals("Libraries", StringComparison.OrdinalIgnoreCase) && (!e.Section.Equals("Plugins", StringComparison.OrdinalIgnoreCase) || !e.Key.Equals("Path", StringComparison.OrdinalIgnoreCase)) && (!e.Section.Equals("Log", StringComparison.OrdinalIgnoreCase) || !e.Key.Equals("LogFileName", StringComparison.OrdinalIgnoreCase)) && (!e.Section.Equals("Hotfix", StringComparison.OrdinalIgnoreCase) || !e.Key.Equals("CheckForUpdate", StringComparison.OrdinalIgnoreCase)))
			select e).ToList();
		List<string> list2 = list.Select(((string Section, bool Closed, string Key, string Value, string Line) e) => e.Line).ToList();
		int num = list.FindIndex(((string Section, bool Closed, string Key, string Value, string Line) e) => e.Key == null && e.Section.Equals("Hotfix", StringComparison.OrdinalIgnoreCase) && e.Line.TrimStart().StartsWith('['));
		if (num < 0)
		{
			list2.AddRange(new _003C_003Ez__ReadOnlyArray<string>(new string[2] { "[Hotfix]", "CheckForUpdate=false" }));
		}
		else
		{
			list2.Insert(num + 1, "CheckForUpdate=false");
		}
		return list2;
	}

	private static IReadOnlyList<string> Imports(string exe)
	{
		return (Cached(new FileInfo(exe), "imports", () =>
		{
			try
			{
				bool agility;
				return CarvedReader.PeImports(exe, out agility);
			}
			catch (Exception ex) when ((ex is BadImageFormatException || ex is InvalidOperationException) ? true : false)
			{
				return new List<string>();
			}
		}) as IReadOnlyList<string>) ?? Array.Empty<string>();
	}

	private static bool IsOptiScaler(FileInfo f)
	{
		object obj = Cached(f, "optiscaler", () => new string[2]
		{
			Middleware.ExportName(f.FullName),
			FileVersionInfo.GetVersionInfo(f.FullName).ProductName
		}.Any((string n) => n?.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase) ?? false));
		if (obj is bool)
		{
			return (bool)obj;
		}
		return false;
	}

	private static bool IsSpecialK(FileInfo f)
	{
		object obj = Cached(f, "specialk", () => new string[2]
		{
			Middleware.ExportName(f.FullName),
			FileVersionInfo.GetVersionInfo(f.FullName).ProductName
		}.Any((string n) => n != null && (n.StartsWith("SpecialK", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Special K", StringComparison.OrdinalIgnoreCase))));
		if (obj is bool)
		{
			return (bool)obj;
		}
		return false;
	}

	private static bool ExportsAddonApi(FileInfo f)
	{
		object obj = Cached(f, "addonexport", () =>
		{
			try
			{
				using PEReader pe = PeFile.Open(f.FullName);
				return PeFile.ExportNames(pe).Contains("ReShadeRegisterAddon");
			}
			catch (Exception ex) when ((ex is BadImageFormatException || ex is InvalidOperationException) ? true : false)
			{
				return false;
			}
		});
		if (obj is bool)
		{
			return (bool)obj;
		}
		return false;
	}

	private static List<FileInfo>? List(string dir)
	{
		try
		{
			return new DirectoryInfo(dir).EnumerateFiles("*", Flat).ToList();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) ? true : false)
		{
			return null;
		}
	}

	public static string Stage(ReShadeInstall r, string dest, long maxFile = 134217728L, long budget = 536870912L)
	{
		if (Directory.Exists(dest))
		{
			Directory.Delete(dest, recursive: true);
		}
		Directory.CreateDirectory(dest);
		File.Copy(r.Loader ?? r.Dll, Path.Combine(dest, "dxgi.dll"));
		string loader = r.Loader;
		if (loader != null)
		{
			File.Copy(r.Dll, Path.Combine(dest, "ReShade64.dll"));
			File.WriteAllLines(Path.Combine(dest, "OptiScaler.ini"), ForStage(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(loader), "OptiScaler.ini"))));
			foreach (string item in LibrariesOf(loader))
			{
				long length = new FileInfo(item).Length;
				if (length <= maxFile && length <= budget && IsPe(item, length))
				{
					File.Copy(item, Path.Combine(dest, Path.GetFileName(item)));
					budget -= length;
				}
			}
		}
		foreach (ReShadeAddon item2 in r.Addons.Where((ReShadeAddon a) => (object)a != null && !a.Disabled && a.Kind != AddonKind.NotPipeline))
		{
			File.Copy(item2.Path, Path.Combine(dest, Path.GetFileName(item2.Path)));
		}
		if (r.Ini != null)
		{
			File.WriteAllLines(Path.Combine(dest, "ReShade.ini"), WithoutPaths(File.ReadAllLines(r.Ini)));
		}
		return dest;
	}

	private static IEnumerable<string> WithoutPaths(IEnumerable<string> ini)
	{
		return from e in IniLines(ini).Where(((string Section, bool Closed, string Key, string Value, string Line) e) =>
			{
				if (e.Section.Equals("ADDON", StringComparison.OrdinalIgnoreCase))
				{
					string item = e.Key;
					if (item != null && item.Equals("AddonPath", StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
				}
				return !e.Section.Equals("INSTALL", StringComparison.OrdinalIgnoreCase) || !(e.Key?.Equals("BasePath", StringComparison.OrdinalIgnoreCase) ?? false);
			})
			select e.Line;
	}

	private static bool Ext(FileInfo f, string ext)
	{
		return f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase);
	}

	public static bool Dxvk(string exeDir)
	{
		return new string[2] { "d3d11.dll", "dxgi.dll" }.Any((string n) => IsDxvk(Path.Combine(exeDir, n)));
	}

	public static bool IsDxvk(string dll)
	{
		FileInfo f = new FileInfo(dll);
		object obj = Cached(f, "dxvk", () => !IsReShade(f) && Find(f, "dxvkvars", DxvkVars) >= 0 && ScsKiller.ChainBlocker(f.FullName) == "vkd3d-proton or DXVK runs the game on Vulkan, whose pipelines a DirectX warm doesn't compile");
		if (obj is bool)
		{
			return (bool)obj;
		}
		return false;
	}

	private static bool IsReShade(FileInfo f)
	{
		object obj = Cached(f, "product", () => FileVersionInfo.GetVersionInfo(f.FullName).ProductName == "ReShade");
		if (!(obj is bool) || !(bool)obj)
		{
			if (Names.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
			{
				if (Find(f, "description", Identity) < 0)
				{
					return ExportsAddonApi(f);
				}
				return true;
			}
			return false;
		}
		return true;
	}

	private static ReShadeAddon Classify(FileInfo f, IReadOnlyDictionary<string, AddonKind>? verdicts, string? log)
	{
		int num = Find(f, "mods", ModMarkers);
		if (num == -1)
		{
			return new ReShadeAddon(f.FullName, null, AddonKind.NotPipeline);
		}
		AddonKind? addonKind = ((verdicts != null && verdicts.TryGetValue(f.FullName, out var value) && File.GetLastWriteTimeUtc(log) > f.LastWriteTimeUtc) ? new AddonKind?(value) : ((AddonKind?)null)) ?? Listed(f.Name) ?? Listed(OriginalName(f.FullName));
		return new ReShadeAddon(f.FullName, (num >= 0 || addonKind.HasValue) ? "RenoDX" : Path.GetFileNameWithoutExtension(f.Name), addonKind ?? AddonKind.ReplacesShaders);
	}

	private static bool Disables(string entry, ReShadeAddon a, FileInfo f)
	{
		int num = entry.IndexOf('@');
		if (num < 0)
		{
			string text = entry;
			string? text2 = ((a.Mod == "RenoDX") ? "RenoDX" : ((Cached(f, "productname", () => FileVersionInfo.GetVersionInfo(f.FullName).ProductName ?? "") is string { Length: >0 } text3) ? text3 : Path.GetFileNameWithoutExtension(f.Name)));
			return text == text2;
		}
		return entry.Substring(num + 1) == f.Name;
	}

	private static AddonKind? Listed(string fileName)
	{
		string text = RenoDxFolder(fileName);
		if (text == null || !RenoDxTable.TryGetValue(text, out var value))
		{
			return null;
		}
		return value;
	}

	private static string OriginalName(string path)
	{
		return (Cached(new FileInfo(path), "original", () => FileVersionInfo.GetVersionInfo(path).OriginalFilename ?? "") as string) ?? "";
	}

	private static (string? AddonPath, List<string> Disabled, bool ProxyLibrary) Config(FileInfo ini)
	{
		string item = null;
		List<string> list = new List<string>();
		List<string> lines = (from e in IniAll(ini)
			select e.Line).ToList();
		string text = ReShadeFirst(lines, "PROXY", "EnableProxyLibrary");
		bool flag = text != null;
		if (flag)
		{
			bool flag2;
			switch (text)
			{
			case "true":
			case "True":
			case "TRUE":
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			flag = flag2 || Regex.IsMatch(text, "^[ \\t\\n\\v\\f\\r]*[+-]?0*[1-9]");
		}
		bool item2 = flag && ReShadeFirst(lines, "PROXY", "ProxyLibrary") != null;
		foreach (var (text2, _, text3, text4, _) in IniEntries(ini))
		{
			if (!text2.Equals("ADDON", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (text3.Equals("AddonPath", StringComparison.OrdinalIgnoreCase))
			{
				string text5 = text4.Trim('"');
				if (text5 != null && text5.Length > 0)
				{
					item = text5;
					continue;
				}
			}
			if (text3.Equals("DisabledAddons", StringComparison.OrdinalIgnoreCase))
			{
				list.AddRange(text4.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
			}
		}
		return (AddonPath: item, Disabled: list, ProxyLibrary: item2);
	}

	private static string? ReShadeFirst(IEnumerable<string> lines, string section, string key)
	{
		string text = "";
		bool flag = false;
		foreach (string line in lines)
		{
			string text2 = line.Split('\0')[0].Trim(new char[4] { ' ', '\t', '\r', '\n' });
			bool flag2 = text2.Length == 0;
			if (!flag2)
			{
				char c = text2[0];
				bool flag3 = ((c == '#' || c == '/' || c == ';') ? true : false);
				flag2 = flag3;
			}
			if (flag2)
			{
				continue;
			}
			if (text2[0] == '[')
			{
				int num = text2.IndexOf(']');
				text = ((num >= 0) ? text2.Substring(0, num) : text2).Trim(new char[4] { ' ', '\t', '[', ']' });
				continue;
			}
			int num2 = text2.IndexOf('=');
			if (text != section || ((num2 < 0) ? text2 : text2.Substring(0, num2).Trim(new char[2] { ' ', '\t' })) != key)
			{
				continue;
			}
			flag = true;
			string text3 = ((num2 < 0) ? "" : text2.Substring(num2 + 1).Trim(new char[2] { ' ', '\t' }));
			if (text3.Length == 0)
			{
				continue;
			}
			StringBuilder stringBuilder = new StringBuilder();
			for (int i = 0; i < text3.Length && (text3[i] != ',' || (i + 1 < text3.Length && text3[i + 1] == ',')); i++)
			{
				stringBuilder.Append((text3[i] == ',') ? text3[i++] : text3[i]);
			}
			return stringBuilder.ToString();
		}
		if (!flag)
		{
			return null;
		}
		return "";
	}

	private static IEnumerable<(string Section, bool Closed, string? Key, string Value, string Line)> IniEntries(FileInfo ini)
	{
		return from e in IniAll(ini)
			where e.Key != null
			select e;
	}

	private static IEnumerable<(string Section, bool Closed, string? Key, string Value, string Line)> IniAll(FileInfo ini)
	{
		return IniLines(((Cached(ini, "ini", () => File.ReadAllText(ini.FullName)) as string) ?? "").Split('\n'));
	}

	private static IEnumerable<(string Section, bool Closed, string? Key, string Value, string Line)> IniLines(IEnumerable<string> lines)
	{
		string section = "";
		bool closed = true;
		foreach (string line in lines)
		{
			string text = line.Trim();
			if (text.StartsWith('['))
			{
				int num = text.IndexOf(']');
				string text2 = text.Substring(1, ((num > 0) ? num : text.Length) - 1).Trim();
				bool flag = num > 0;
				closed = flag;
				section = text2;
				yield return (Section: section, Closed: closed, Key: null, Value: "", Line: line);
			}
			else
			{
				int num2 = text.IndexOf('=');
				if (num2 > 0)
				{
					yield return (Section: section, Closed: closed, Key: text.Substring(0, num2).Trim(), Value: text.Substring(num2 + 1).Trim(), Line: line);
				}
				else
				{
					yield return (Section: section, Closed: closed, Key: null, Value: "", Line: line);
				}
			}
		}
	}

	private static IReadOnlyDictionary<string, AddonKind> LogVerdicts(FileInfo log)
	{
		return (Cached(log, "log", () =>
		{
			Dictionary<string, string> dictionary = new Dictionary<string, string>();
			Dictionary<string, AddonKind> dictionary2 = new Dictionary<string, AddonKind>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string> dictionary3 = dictionary;
			string text = null;
			using StreamReader streamReader = new StreamReader(new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
			string input;
			while ((input = streamReader.ReadLine()) != null)
			{
				Match match = Loading.Match(input);
				if (match != null && match.Success)
				{
					text = match.Groups[1].Value;
				}
				else
				{
					Match match2 = Registered.Match(input);
					if (match2 != null && match2.Success && text != null)
					{
						string value = match2.Groups[1].Value;
						string value2 = FullPath(text);
						dictionary3[value] = value2;
						text = null;
					}
					else
					{
						Match match3 = Pipeline.Match(input);
						if (match3 != null && match3.Success && dictionary3.TryGetValue(match3.Groups[1].Value, out var value3) && value3 != null)
						{
							dictionary2[value3] = ((!match3.Groups[2].Value.StartsWith("OnCreate", StringComparison.Ordinal)) ? AddonKind.ReplacesShaders : AddonKind.LayoutInjecting);
						}
					}
				}
			}
			return dictionary2;
		}) as IReadOnlyDictionary<string, AddonKind>) ?? new Dictionary<string, AddonKind>();
	}

	private static string? FullPath(string path)
	{
		try
		{
			return Path.GetFullPath(path);
		}
		catch (Exception ex) when ((ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) ? true : false)
		{
			return null;
		}
	}

	private static int Find(FileInfo f, string what, byte[][] markers)
	{
		return (Cached(f, what, () =>
		{
			using FileStream fileStream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			byte[] bytes = new byte[fileStream.Length];
			fileStream.ReadExactly(bytes);
			return Array.FindIndex(markers, (byte[] m) => ((ReadOnlySpan<byte>)bytes.AsSpan()).IndexOf((ReadOnlySpan<byte>)m) >= 0);
		}) as int?) ?? (-2);
	}

	private static object? Cached(FileInfo f, string what, Func<object> read)
	{
		f.Refresh();
		if (!f.Exists || f.Length > 134217728)
		{
			return null;
		}
		string key = f.FullName + "|" + what;
		string text = KeyFiles.Identity(f.FullName);
		if (Probed.TryGetValue(key, out (long, DateTime, string, object) value))
		{
			long item = value.Item1;
			DateTime item2 = value.Item2;
			string item3 = value.Item3;
			long length = f.Length;
			DateTime lastWriteTimeUtc = f.LastWriteTimeUtc;
			string text2 = text;
			if (item == length && item2 == lastWriteTimeUtc && item3 == text2)
			{
				return value.Item4;
			}
		}
		try
		{
			object obj = read();
			Probed[key] = (f.Length, f.LastWriteTimeUtc, text, obj);
			if ((obj is bool || obj is int || obj is string) ? true : false)
			{
				Interlocked.Increment(ref probesVersion);
			}
			return obj;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}
}
