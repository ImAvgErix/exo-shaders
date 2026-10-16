using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Unreal;

public static class UnrealRhi
{
	public const string Ambiguous = "D3D11 or D3D12";

	private static readonly string[] Dx12Forks = new string[1] { "GAME_FinalFantasy7Remake" };

	private static readonly string[] EngineIni = new string[8] { "Engine/Config/BaseEngine.ini", "Engine/Config/Windows/BaseWindowsEngine.ini", "Engine/Platforms/Windows/Config/BaseWindowsEngine.ini", "{P}/Config/DefaultEngine.ini", "Engine/Config/Windows/WindowsEngine.ini", "Engine/Platforms/Windows/Config/WindowsEngine.ini", "{P}/Config/Windows/WindowsEngine.ini", "{P}/Platforms/Windows/Config/WindowsEngine.ini" };

	private const string ProjectEngineIni = "{P}/Config/DefaultEngine.ini";

	private static readonly string[] WinGdkEngineIni = new string[6] { "Engine/Config/BaseEngine.ini", "Engine/Platforms/WinGDK/Config/BaseWinGDKEngine.ini", "{P}/Config/DefaultEngine.ini", "Engine/Platforms/WinGDK/Config/WinGDKEngine.ini", "{P}/Config/WinGDK/WinGDKEngine.ini", "{P}/Platforms/WinGDK/Config/WinGDKEngine.ini" };

	private static readonly string[] UserSettingsIni = new string[2] { "{P}/Config/DefaultGameUserSettings.ini", "{P}/Config/Windows/WindowsGameUserSettings.ini" };

	private static readonly string[] DeviceProfilesIni = new string[6] { "Engine/Config/BaseDeviceProfiles.ini", "Engine/Config/Windows/WindowsDeviceProfiles.ini", "Engine/Platforms/Windows/Config/WindowsDeviceProfiles.ini", "{P}/Config/DefaultDeviceProfiles.ini", "{P}/Config/Windows/WindowsDeviceProfiles.ini", "{P}/Platforms/Windows/Config/WindowsDeviceProfiles.ini" };

	private static readonly string[] ConsoleVariablesIni = new string[2] { "Engine/Config/ConsoleVariables.ini", "{P}/Config/ConsoleVariables.ini" };

	private static readonly string[] ScalabilityIni = new string[7] { "Engine/Config/BaseScalability.ini", "Engine/Config/Windows/BaseWindowsScalability.ini", "Engine/Config/Windows/WindowsScalability.ini", "Engine/Platforms/Windows/Config/WindowsScalability.ini", "{P}/Config/DefaultScalability.ini", "{P}/Config/Windows/WindowsScalability.ini", "{P}/Platforms/Windows/Config/WindowsScalability.ini" };

	private static readonly string[] EngineGroups = new string[12]
	{
		"ResolutionQuality", "ViewDistanceQuality", "AntiAliasingQuality", "ShadowQuality", "GlobalIlluminationQuality", "ReflectionQuality", "PostProcessQuality", "TextureQuality", "EffectsQuality", "FoliageQuality",
		"ShadingQuality", "LandscapeQuality"
	};

	private const string Renderer = "/Script/Engine.RendererSettings";

	private static readonly Dictionary<string, (IReadOnlyList<string>, string?)> menus = new Dictionary<string, (IReadOnlyList<string>, string)>();

	private static string? menuStamp;

	private static string SteamRoot => (Registry.GetValue("HKEY_CURRENT_USER\\Software\\Valve\\Steam", "SteamPath", null) as string) ?? "C:\\Program Files (x86)\\Steam";

	public static bool IsWinGdk(string exePath)
	{
		return Path.GetFileName(Path.GetDirectoryName(exePath))?.Equals("WinGDK", StringComparison.OrdinalIgnoreCase) ?? false;
	}

	public static bool IsConfig(string path, string project)
	{
		return EngineIni.Concat(WinGdkEngineIni).Concat(UserSettingsIni).Concat(DeviceProfilesIni)
			.Concat(ConsoleVariablesIni)
			.Concat(ScalabilityIni)
			.Any((string p) => Same(p, path, project));
	}

	public static int? MaxAnisotropy(int engineMajor, IReadOnlyDictionary<string, string> configs, string project, string? userDir, string launch = "")
	{
		if (Regex.IsMatch(launch, "r\\.MaxAnisotropy(?![.\\w])", RegexOptions.IgnoreCase))
		{
			return null;
		}
		string user = UserConfig(userDir);
		List<string> list = Ordered(EngineIni, configs, project).Append(User("Engine.ini")).ToList();
		List<string> list2 = Ordered(ScalabilityIni, configs, project).Append(User("Scalability.ini")).ToList();
		List<string> list3 = Ordered(UserSettingsIni, configs, project).Append(User("GameUserSettings.ini")).ToList();
		Dictionary<string, string> dictionary = ProfileCVars(Ordered(DeviceProfilesIni, configs, project).Append(User("DeviceProfiles.ini")).ToList(), Last(list, "SystemSettings", "dp.Override") ?? Last(list, "ConsoleVariables", "dp.Override") ?? ((engineMajor >= 5) ? "Windows" : "WindowsNoEditor"));
		HashSet<string> sg = (from e in (from e in list3.SelectMany(Entries)
				where e.Section.Equals("ScalabilityGroups", StringComparison.OrdinalIgnoreCase) && e.Key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase)
				select e).Concat(from c in dictionary
				where c.Key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase)
				select ("", Key: c.Key, Value: c.Value))
			select e.Key.Substring(3)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		string value = null;
		foreach (string item in Groups(list2, "r.MaxAnisotropy").Where(ByEngine))
		{
			int? num = Int(Last(list3, "ScalabilityGroups", "sg." + item) ?? dictionary.GetValueOrDefault("sg." + item) ?? "3");
			string higher;
			if (num.HasValue)
			{
				int valueOrDefault = num.GetValueOrDefault();
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(1, 2);
				defaultInterpolatedStringHandler.AppendFormatted(item);
				defaultInterpolatedStringHandler.AppendLiteral("@");
				defaultInterpolatedStringHandler.AppendFormatted((Math.Clamp(valueOrDefault, 0, 4) == 4) ? "Cine" : ((object)valueOrDefault));
				higher = Last(list2, defaultInterpolatedStringHandler.ToStringAndClear(), "r.MaxAnisotropy");
			}
			else
			{
				higher = "?";
			}
			Pick(higher);
		}
		Pick(Last(list, "/Script/Engine.RendererSettings", "r.MaxAnisotropy"));
		string text = Last(list, "SystemSettings", "r.MaxAnisotropy");
		object higher2;
		if (text != null)
		{
			string text2 = Last(list, "ConsoleVariables", "r.MaxAnisotropy");
			if (text2 != null && text != text2)
			{
				higher2 = "?";
				goto IL_0338;
			}
		}
		higher2 = Last(list, "SystemSettings", "r.MaxAnisotropy") ?? Last(list, "ConsoleVariables", "r.MaxAnisotropy");
		goto IL_0338;
		IL_0338:
		Pick((string?)higher2);
		Pick(dictionary.GetValueOrDefault("r.MaxAnisotropy"));
		List<string> list4 = list3.Concat(list2).Concat(list).ToList();
		foreach (string group in Groups(list3.Concat(list), "r.MaxAnisotropy").Concat(from g in Groups(list2, "r.MaxAnisotropy")
			where !ByEngine(g)
			select g).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			List<string> source = (from e in list4.SelectMany(Entries)
				select e.Section into x
				where x.StartsWith(@group + "@", StringComparison.OrdinalIgnoreCase)
				select x.Substring(@group.Length + 1)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			string saved = (from e in list3.SelectMany(Entries)
				where e.Key.Equals(@group, StringComparison.OrdinalIgnoreCase)
				select e.Value).LastOrDefault();
			string text3 = ((saved == null) ? null : source.FirstOrDefault((string l) => saved.Equals(l, StringComparison.OrdinalIgnoreCase) || saved.EndsWith("_" + l, StringComparison.OrdinalIgnoreCase)));
			if (text3 == null)
			{
				return null;
			}
			Pick(Last(list4, group + "@" + text3, "r.MaxAnisotropy"));
		}
		if (!(value == "?"))
		{
			return Int(value);
		}
		return null;
		bool ByEngine(string text4)
		{
			if (!EngineGroups.Contains(text4, StringComparer.OrdinalIgnoreCase))
			{
				return sg.Contains(text4);
			}
			return true;
		}
		string? Pick(string? text4)
		{
			string? obj = text4 ?? value;
			string result = obj;
			value = obj;
			return result;
		}
		string User(string file)
		{
			if (user == null || !File.Exists(Path.Combine(user, file)))
			{
				return "";
			}
			return File.ReadAllText(Path.Combine(user, file)).TrimStart('\ufeff');
		}
	}

	private static int? Int(string? v)
	{
		if (v != null)
		{
			Match match = Regex.Match(v, "^\\s*-?\\d+");
			if (match != null && match.Success && int.TryParse(match.Value, out var result))
			{
				return result;
			}
		}
		return null;
	}

	private static IEnumerable<string> Groups(IEnumerable<string> inis, string key)
	{
		return (from e in inis.SelectMany(Entries)
			where e.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && e.Section.IndexOf('@') > 0
			select e.Section.Substring(0, e.Section.IndexOf('@'))).Distinct(StringComparer.OrdinalIgnoreCase);
	}

	private static IEnumerable<(string Section, string Key, string Value)> Entries(string ini)
	{
		string section = "";
		string[] array = ini.Split('\n');
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			if (text.StartsWith('['))
			{
				section = text.Trim(new char[2] { '[', ']' });
				continue;
			}
			int num = text.IndexOf('=');
			if (num > 0 && text[0] != ';')
			{
				yield return (Section: section, Key: text.Substring(0, num).Trim(), Value: text.Substring(num + 1).Trim().Trim('"'));
			}
		}
	}

	private static Dictionary<string, string> ProfileCVars(List<string> files, string name)
	{
		List<string> list = new List<string>();
		string text = name;
		while (text.Length > 0 && !list.Contains(text, StringComparer.OrdinalIgnoreCase))
		{
			list.Add(text);
			text = Last(files, text + " DeviceProfile", "BaseProfileName") ?? "";
		}
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string n in Enumerable.Reverse(list))
		{
			List<string> list2 = new List<string>();
			foreach (var item3 in from e in files.SelectMany(Entries)
				where e.Section.Equals(n + " DeviceProfile", StringComparison.OrdinalIgnoreCase)
				select e)
			{
				string item = item3.Key;
				string item2 = item3.Value;
				if (item.TrimStart(new char[4] { '+', '.', '-', '!' }).Trim().Equals("CVars", StringComparison.OrdinalIgnoreCase))
				{
					if (item[0] == '!')
					{
						list2.Clear();
					}
					else if (item[0] == '-')
					{
						list2.Remove(item2);
					}
					else
					{
						list2.Add(item2);
					}
				}
			}
			foreach (string item4 in list2)
			{
				int num = item4.IndexOf('=');
				if (num > 0)
				{
					dictionary[item4.Substring(0, num).Trim()] = item4.Substring(num + 1).Trim();
				}
			}
		}
		return dictionary;
	}

	public static bool RayTracingOff(IReadOnlyDictionary<string, string> configs, string project, string? userDir = null, string launch = "")
	{
		string user = UserConfig(userDir);
		List<string> list = Ordered(EngineIni, configs, project).Append(User("Engine.ini")).ToList();
		string text = Last(list, "/Script/Engine.RendererSettings", "r.RayTracing");
		if (text == null || !IsOff(text))
		{
			return false;
		}
		if (Regex.IsMatch(launch, "r\\.RayTracing(?![.\\w])", RegexOptions.IgnoreCase))
		{
			return false;
		}
		return list.Concat(Ordered(ConsoleVariablesIni, configs, project)).Concat(Ordered(DeviceProfilesIni, configs, project)).Append(User("DeviceProfiles.ini"))
			.All((string ini) => RayTracingCVars(ini).All(IsOff));
		string User(string file)
		{
			if (user == null || !File.Exists(Path.Combine(user, file)))
			{
				return "";
			}
			return File.ReadAllText(Path.Combine(user, file));
		}
	}

	private static IEnumerable<string> RayTracingCVars(string ini)
	{
		string section = null;
		string[] array = ini.Split('\n');
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			if (text.StartsWith('['))
			{
				section = text.Trim(new char[2] { '[', ']' });
				continue;
			}
			int num = text.IndexOf('=');
			if (num <= 0 || text[0] == ';')
			{
				continue;
			}
			string text2 = text.Substring(0, num).TrimStart(new char[4] { '+', '.', '-', '!' }).Trim();
			string text3 = text.Substring(num + 1).Trim().Trim('"');
			if (text2.Equals("CVars", StringComparison.OrdinalIgnoreCase))
			{
				int num2 = text3.IndexOf('=');
				if (num2 > 0)
				{
					string text4 = text3.Substring(0, num2).Trim();
					text3 = text3.Substring(num2 + 1).Trim();
					text2 = text4;
					goto IL_0134;
				}
			}
			if (section?.Equals("/Script/Engine.RendererSettings", StringComparison.OrdinalIgnoreCase) ?? false)
			{
				continue;
			}
			goto IL_0134;
			IL_0134:
			if (text2.Equals("r.RayTracing", StringComparison.OrdinalIgnoreCase))
			{
				yield return text3;
			}
		}
	}

	public static bool RtPipelinesOff(IReadOnlyDictionary<string, string> configs, string project)
	{
		string text = null;
		foreach (string item in Ordered(DeviceProfilesIni, configs, project))
		{
			bool flag = false;
			string[] array = item.Split('\n');
			for (int i = 0; i < array.Length; i++)
			{
				string text2 = array[i].Trim();
				if (text2.StartsWith('['))
				{
					flag = text2.Equals("[Windows DeviceProfile]", StringComparison.OrdinalIgnoreCase);
					continue;
				}
				int num = text2.IndexOf('=');
				if (!flag || num <= 0 || !text2.Substring(0, num).TrimStart(new char[4] { '+', '.', '-', '!' }).Trim()
					.Equals("CVars", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (text2[0] == '!')
				{
					text = null;
					continue;
				}
				string text3 = text2.Substring(num + 1).Trim().Trim('"');
				int num2 = text3.IndexOf('=');
				if (num2 > 0 && text3.Substring(0, num2).Trim().Equals("r.RayTracing.AllowPipeline", StringComparison.OrdinalIgnoreCase))
				{
					string text4 = text3.Substring(num2 + 1).Trim();
					if (text2[0] != '-')
					{
						text = text4;
					}
					else if (text == text4)
					{
						text = null;
					}
				}
			}
		}
		return text == "0";
	}

	public static bool AftermathShaderDebug(IReadOnlyDictionary<string, string> configs, string project, string launch = "")
	{
		List<string> engine = Ordered(EngineIni, configs, project).ToList();
		int num;
		if (!Param("nogpucrashdebugging"))
		{
			if (!Param("gpucrashdebugging"))
			{
				string text = CVar("r.GPUCrashDebugging.Aftermath");
				num = ((text == null || !IsOff(text)) ? 1 : 0);
			}
			else
			{
				num = 1;
			}
		}
		else
		{
			num = 0;
		}
		bool flag = (byte)num != 0;
		Match match = Regex.Match(launch, "(?:^|\\s)-nvaftermath=(\\d+)", RegexOptions.IgnoreCase);
		if (match != null && match.Success)
		{
			flag = match.Groups[1].Value.Trim('0').Length > 0;
		}
		else if (Param("nvaftermath"))
		{
			flag = true;
		}
		if (flag)
		{
			if (!On("r.GPUCrashDebugging.Aftermath.DumpShaderDebugInfo") && !On("r.GPUCrashDebugging.Aftermath.TrackAll") && !Param("nvAftermathDumpShaderDebugInfo"))
			{
				return Param("nvaftermathall");
			}
			return true;
		}
		return false;
		string? CVar(string key)
		{
			return Last(engine, "ConsoleVariables", key) ?? Last(engine, "SystemSettings", key);
		}
		bool On(string key)
		{
			string text2 = CVar(key);
			if (text2 != null)
			{
				return IsOn(text2);
			}
			return false;
		}
		bool Param(string name)
		{
			return Regex.IsMatch(launch, "(?:^|\\s)-" + name + "(?![=\\w])", RegexOptions.IgnoreCase);
		}
	}

	private static bool IsOn(string v)
	{
		if (!(v == "1"))
		{
			return v.Equals("true", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool IsOff(string v)
	{
		if (!(v == "0"))
		{
			return v.Equals("false", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool Same(string pattern, string path, string project)
	{
		return string.Equals(pattern.Replace("{P}", project), path, StringComparison.OrdinalIgnoreCase);
	}

	private static IEnumerable<string> Ordered(string[] order, IReadOnlyDictionary<string, string> configs, string project)
	{
		return order.SelectMany((string p) => from c in configs
			where Same(p, c.Key, project)
			select c.Value.TrimStart('\ufeff'));
	}

	public static (string Api, string Why) Resolve(int engineMajor, IReadOnlyCollection<string> platforms, IReadOnlyDictionary<string, string> configs, string project, string? userDir, string launch, IReadOnlyList<string>? menu = null, string? menuDefault = null, string? fork = null, bool winGdk = false)
	{
		bool engineDefault;
		return Resolve(engineMajor, platforms, configs, project, userDir, launch, out engineDefault, menu, menuDefault, fork, winGdk);
	}

	public static (string Api, string Why) Resolve(int engineMajor, IReadOnlyCollection<string> platforms, IReadOnlyDictionary<string, string> configs, string project, string? userDir, string launch, out bool engineDefault, IReadOnlyList<string>? menu = null, string? menuDefault = null, string? fork = null, bool winGdk = false)
	{
		engineDefault = false;
		bool flag = platforms.Contains("PCD3D_SM5");
		bool flag2 = platforms.Contains("PCD3D_SM6");
		if (flag2 && !flag)
		{
			return (Api: "D3D12", Why: "only SM6 shaders, which run on DX12 only");
		}
		Match match = Regex.Match(launch, "(?:^|\\s)-(dx11|d3d11|dx12|d3d12|vulkan)\\b", RegexOptions.IgnoreCase);
		if (match != null && match.Success)
		{
			return (Api: Api(match.Groups[1].Value) + " (launch option)", Why: "launch option -" + match.Groups[1].Value);
		}
		if (menu != null && menu.Any((string e) => MenuApi(e) != null))
		{
			(string, string) result = Resolve(engineMajor, platforms, configs, project, userDir, launch, out var engineDefault2, null, null, fork, winGdk);
			string plain = result.Item1.Split(" (")[0];
			List<string> list = menu.Select((string e) => MenuApi(e) ?? plain).Distinct().ToList();
			if (list.Count == 1)
			{
				if (!menu.All((string e) => MenuApi(e) != null))
				{
					return result;
				}
				return (Api: list[0] + " (launch option)", Why: "the game's Steam launch menu passes " + list[0]);
			}
			(string, string)? tuple = LastRun(userDir);
			if (tuple.HasValue)
			{
				(string, string) valueOrDefault = tuple.GetValueOrDefault();
				return (Api: valueOrDefault.Item1 + " (last run)", Why: "the game's Steam launch menu offers " + string.Join(" / ", list) + "; the game's last log: " + valueOrDefault.Item2);
			}
			if (menuDefault != null && (MenuApi(menuDefault) ?? plain) == "D3D12")
			{
				if (MenuApi(menuDefault) != null)
				{
					return (Api: "D3D12 (launch option)", Why: "the default entry of the game's Steam launch menu passes D3D12, no log");
				}
				engineDefault = engineDefault2;
				return result;
			}
			return (Api: "D3D11 or D3D12", Why: "the game's Steam launch menu offers " + string.Join(" / ", list) + ", no log");
		}
		if (fork != null && Dx12Forks.Contains(fork))
		{
			return (Api: "D3D12", Why: fork + " starts DX12 unless -dx11 or -d3d11 is given");
		}
		string userConfig = UserConfig(userDir, winGdk);
		string[] order = (winGdk ? WinGdkEngineIni : EngineIni);
		List<string?> inis = Ordered(order, configs, project).Append(User("Engine.ini") ?? "").ToList();
		List<string> inis2 = Ordered(UserSettingsIni, configs, project).Append(User("GameUserSettings.ini") ?? "").ToList();
		string text = Last(inis, winGdk ? "/Script/WinGDKPlatformEditor.WinGDKTargetSettings" : "/Script/WindowsTargetPlatform.WindowsTargetSettings", "DefaultGraphicsRHI");
		if (winGdk && text == null)
		{
			(string, string)? tuple = LastRun(userDir);
			if (tuple.HasValue)
			{
				(string, string) valueOrDefault2 = tuple.GetValueOrDefault();
				return (Api: valueOrDefault2.Item1 + " (last run)", Why: "no WinGDK DefaultGraphicsRHI; the game's last log: " + valueOrDefault2.Item2);
			}
			if (engineMajor >= 5)
			{
				engineDefault = true;
				return (Api: "D3D12", Why: "no WinGDK DefaultGraphicsRHI (UE5 default)");
			}
			return (Api: "D3D11 or D3D12", Why: "no WinGDK DefaultGraphicsRHI, no log");
		}
		bool flag3 = ((text == null || text == "DefaultGraphicsRHI_Default") ? true : false);
		bool flag4 = !flag3;
		string text2 = text switch
		{
			"DefaultGraphicsRHI_DX11" => "D3D11", 
			"DefaultGraphicsRHI_DX12" => "D3D12", 
			"DefaultGraphicsRHI_Vulkan" => "Vulkan", 
			_ => (engineMajor >= 5) ? "D3D12" : "D3D11", 
		};
		string text3 = ((text != null) ? ("DefaultGraphicsRHI=" + text + ((text == "DefaultGraphicsRHI_Default") ? $" (UE{engineMajor} default)" : "")) : $"no DefaultGraphicsRHI (UE{engineMajor} default)");
		string text4 = Last(inis2, "D3DRHIPreference", "PreferredRHI");
		(string, string) valueTuple;
		if (text4 == null)
		{
			string text5 = Last(inis2, "*", "PreferredGraphicsAPI");
			if (text5 == null)
			{
				string text6 = Last(inis2, "D3DRHIPreference", "bUseD3D12InGame");
				valueTuple = ((text6 != null && text6.Equals("true", StringComparison.OrdinalIgnoreCase)) ? ("D3D12", "bUseD3D12InGame=True") : (null, ""));
			}
			else
			{
				valueTuple = (Api(text5), "PreferredGraphicsAPI=" + text5);
			}
		}
		else
		{
			valueTuple = (Api(text4), "PreferredRHI=" + text4);
		}
		(string, string) tuple2 = valueTuple;
		var (text7, _) = tuple2;
		object obj;
		if (text7 != null && text7 != "?" && (engineMajor >= 5 || !flag4))
		{
			(obj, _) = tuple2;
		}
		else
		{
			obj = null;
		}
		string text8 = (string)obj;
		if (text8 != null && text8 != text2)
		{
			return (Api: text8 + " (user setting)", Why: tuple2.Item2 + " in the user's GameUserSettings.ini over " + text3);
		}
		if (!configs.Keys.Any((string k) => Same("{P}/Config/DefaultEngine.ini", k, project)))
		{
			(string, string)? tuple = LastRun(userDir);
			if (tuple.HasValue)
			{
				(string, string) valueOrDefault3 = tuple.GetValueOrDefault();
				return (Api: valueOrDefault3.Item1 + " (last run)", Why: "project config unreadable; the game's last log: " + valueOrDefault3.Item2);
			}
			if (!(flag & flag2))
			{
				return (Api: "D3D11 or D3D12", Why: $"project config unreadable, no log (UE{engineMajor} default would be {text2})");
			}
			return (Api: "D3D11 or D3D12", Why: "project config unreadable, both SM5 and SM6 shaders ship, no log");
		}
		object obj2;
		if (!(text2 != "D3D11"))
		{
			if (!flag2)
			{
				string text9 = Last(Ordered(order, configs, project), "/Script/Engine.RendererSettings", "r.RayTracing");
				obj2 = ((text9 != null && IsOn(text9)) ? (text3 + ", but the project ships ray tracing (r.RayTracing=" + text9 + "), which runs on DX12 only: DX12 is a launch or in-game option") : null);
			}
			else
			{
				obj2 = text3 + ", but DX12-only (SM6) shaders ship: DX12 is a launch or in-game option";
			}
		}
		else
		{
			obj2 = null;
		}
		string text10 = (string)obj2;
		if (text10 != null && text8 != null)
		{
			return (Api: text8 + " (user setting)", Why: tuple2.Item2 + " in the user's GameUserSettings.ini; " + text10);
		}
		if (text10 != null)
		{
			(string, string)? tuple = LastRun(userDir);
			if (tuple.HasValue)
			{
				(string, string) valueOrDefault4 = tuple.GetValueOrDefault();
				return (Api: valueOrDefault4.Item1 + " (last run)", Why: text10 + "; the game's last log: " + valueOrDefault4.Item2);
			}
			return (Api: "D3D11 or D3D12", Why: text10 + ", no log");
		}
		engineDefault = !flag4 && text8 == null;
		return (Api: text2, Why: text3 + ((tuple2.Item1 != null) ? ("; user " + tuple2.Item2 + " agrees or is ignored") : ""));
		string? User(string file)
		{
			if (userConfig == null || !File.Exists(Path.Combine(userConfig, file)))
			{
				return null;
			}
			return File.ReadAllText(Path.Combine(userConfig, file));
		}
	}

	private static string? MenuApi(string entry)
	{
		Match match = Regex.Match(entry, "(?:^|\\s)-(dx11|d3d11|dx12|d3d12|vulkan)\\b|\\b(?:DirectX|DX)\\s*(1[12])\\b", RegexOptions.IgnoreCase);
		if (match == null || !match.Success)
		{
			return null;
		}
		return Api(match.Groups[1].Success ? match.Groups[1].Value : ("dx" + match.Groups[2].Value));
	}

	private static string Api(string v)
	{
		switch (v.Trim('"').ToLowerInvariant())
		{
		case "dx11":
		case "d3d11":
			return "D3D11";
		case "dx12":
		case "d3d12":
			return "D3D12";
		case "vulkan":
			return "Vulkan";
		default:
			return "?";
		}
	}

	private static string? Last(IEnumerable<string> inis, string section, string key)
	{
		string result = null;
		foreach (string ini in inis)
		{
			string text = null;
			string[] array = ini.Split('\n');
			for (int i = 0; i < array.Length; i++)
			{
				string text2 = array[i].Trim();
				if (text2.StartsWith('['))
				{
					text = text2.Trim(new char[2] { '[', ']' });
					continue;
				}
				int num = text2.IndexOf('=');
				if (text != null && num > 0 && (section == "*" || text.Equals(section, StringComparison.OrdinalIgnoreCase)) && text2.Substring(0, num).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
				{
					result = text2.Substring(num + 1).Trim().Trim('"');
				}
			}
		}
		return result;
	}

	private static (string Api, string Line)? LastRun(string? userDir)
	{
		if (userDir == null)
		{
			return null;
		}
		foreach (FileInfo item2 in Logs(userDir))
		{
			using StreamReader streamReader = new StreamReader(new FileStream(item2.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
			string text = streamReader.ReadLine();
			int num = 0;
			while (text != null && num < 20000)
			{
				Match match = Regex.Match(text, "LogRHI: Using (?:Default|Preferred|Forced) RHI: (D3D11|D3D12|Vulkan)|(LogD3D1[12])RHI:");
				if (match != null && match.Success)
				{
					string item = (match.Groups[1].Success ? match.Groups[1].Value : ((match.Groups[2].Value == "LogD3D12") ? "D3D12" : "D3D11"));
					return (item, $"{item2.Name} {item2.LastWriteTime:yyyy-MM-dd}: {text.Trim()}");
				}
				num++;
				text = streamReader.ReadLine();
			}
		}
		return null;
	}

	private static IEnumerable<FileInfo> Logs(string userDir)
	{
		EnumerationOptions opts = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			MaxRecursionDepth = 2,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None
		};
		return from f in new string[2] { "Logs", "Crashes" }.Select((string d) => Path.Combine(userDir, d)).Where(Directory.Exists).SelectMany((string d) => new DirectoryInfo(d).EnumerateFiles("*.log", opts))
			orderby f.LastWriteTimeUtc descending
			select f;
	}

	public static string Stamp(Game game, string project)
	{
		(IReadOnlyList<string> Entries, string? Default) tuple = LaunchMenu(game);
		IReadOnlyList<string> item = tuple.Entries;
		string item2 = tuple.Default;
		InlineArray4<string> buffer = default;
		buffer[0] = UserFiles(UserDir(game, project), IsWinGdk(game.ExePath));
		buffer[1] = LaunchOptions(game);
		buffer[2] = string.Join(';', item);
		buffer[3] = item2;
		return string.Join('|', (ReadOnlySpan<string?>)buffer);
	}

	internal static string UserFiles(string? userDir, bool winGdk = false)
	{
		if (userDir == null)
		{
			return "";
		}
		string config = UserConfig(userDir, winGdk);
		IEnumerable<string> enumerable;
		if (config != null)
		{
			enumerable = new string[4] { "Engine.ini", "GameUserSettings.ini", "Scalability.ini", "DeviceProfiles.ini" }.Select((string n) => Of(new FileInfo(Path.Combine(config, n))));
		}
		else
		{
			IEnumerable<string> enumerable2 = Array.Empty<string>();
			enumerable = enumerable2;
		}
		IEnumerable<string> source = enumerable;
		return string.Join('>', source.Prepend(userDir).Concat(Logs(userDir).Select(Of)));
		static string Of(FileInfo f)
		{
			if (f.Exists)
			{
				return $"{f.FullName}:{f.Length}:{f.LastWriteTimeUtc.Ticks}";
			}
			return f.FullName + ":-";
		}
	}

	private static string? UserConfig(string? userDir, bool winGdk = false)
	{
		if (userDir != null && Directory.Exists(Path.Combine(userDir, "Config")))
		{
			return Directory.EnumerateDirectories(Path.Combine(userDir, "Config"), ConfigFolders(winGdk)).MaxBy(Directory.GetLastWriteTimeUtc);
		}
		return null;
	}

	private static string ConfigFolders(bool winGdk)
	{
		if (!winGdk)
		{
			return "Windows*";
		}
		return "WinGDK";
	}

	public static string? UserDir(Game game, string project)
	{
		List<string> names = new string[4]
		{
			project,
			ExeStem(game),
			Path.GetFileName(game.InstallDir.TrimEnd(new char[2] { '\\', '/' })),
			game.Name
		}.Where((string n) => n.Length > 0 && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		List<string> source = new string[3]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "My Games"),
			Path.Combine(folderPath, "Saved Games")
		}.Where(Directory.Exists).ToList();
		EnumerationOptions flat = new EnumerationOptions
		{
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None
		};
		string folders = ConfigFolders(IsWinGdk(game.ExePath));
		return (from s in source.SelectMany((string r) => names.Select((string n) => Path.Combine(r, n)).Concat(Directory.EnumerateDirectories(r, "*", flat).SelectMany((string c) => names.Select((string n) => Path.Combine(c, n))))).Where(Directory.Exists).SelectMany((string d) => Directory.EnumerateDirectories(d, "Saved*", flat))
			where Directory.Exists(Path.Combine(s, "Config")) && Directory.EnumerateDirectories(Path.Combine(s, "Config"), folders, flat).Any()
			orderby Directory.GetLastWriteTimeUtc(Path.Combine(s, "Config")) descending
			select s).FirstOrDefault();
	}

	private static string ExeStem(Game game)
	{
		return Regex.Replace(Path.GetFileNameWithoutExtension(game.ExePath), "-Win(64|GDK)-Shipping$", "", RegexOptions.IgnoreCase);
	}

	public static string LaunchOptions(Game game)
	{
		uint? num = SteamSource.AppId(game.Id);
		if (num.HasValue)
		{
			uint valueOrDefault = num.GetValueOrDefault();
			string path = Path.Combine(SteamRoot, "userdata");
			FileInfo fileInfo = (Directory.Exists(path) ? (from u in Directory.EnumerateDirectories(path)
				select new FileInfo(Path.Combine(u, "config", "localconfig.vdf")) into f
				where f.Exists
				select f).MaxBy((FileInfo f) => f.LastWriteTimeUtc) : null);
			if (fileInfo == null)
			{
				return "";
			}
			string text = File.ReadAllText(fileInfo.FullName);
			foreach (Match item in Regex.Matches(text, $"\"{valueOrDefault}\"\\s*\\{{"))
			{
				int num2 = item.Index + item.Length;
				int num3 = 1;
				for (; num2 < text.Length; num2++)
				{
					if (num3 <= 0)
					{
						break;
					}
					num3 += ((text[num2] == '{') ? 1 : ((text[num2] == '}') ? (-1) : 0));
				}
				int index = item.Index;
				string text2 = SteamSource.Values(text.Substring(index, num2 - index), "LaunchOptions").FirstOrDefault();
				if (text2 != null)
				{
					return text2;
				}
			}
			return "";
		}
		return "";
	}

	public static (IReadOnlyList<string> Entries, string? Default) LaunchMenu(Game game)
	{
		uint? num = SteamSource.AppId(game.Id);
		if (num.HasValue)
		{
			uint valueOrDefault = num.GetValueOrDefault();
			FileInfo fileInfo = new FileInfo(Path.Combine(SteamRoot, "appcache", "appinfo.vdf"));
			string text = (fileInfo.Exists ? $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}" : "");
			string key = $"{game.Id}|{game.InstallDir}|{game.ExePath}";
			lock (menus)
			{
				if (menuStamp != text)
				{
					menus.Clear();
					menuStamp = text;
				}
				if (menus.TryGetValue(key, out (IReadOnlyList<string>, string) value))
				{
					return value;
				}
			}
			Dictionary<uint, Dictionary<string, object>>? dictionary = SteamSource.Apps(fileInfo.FullName, new HashSet<uint> { valueOrDefault });
			(IReadOnlyList<string>, string) tuple = LaunchMenu(dictionary?.GetValueOrDefault(valueOrDefault), game);
			if (dictionary != null)
			{
				lock (menus)
				{
					if (menuStamp == text)
					{
						menus[key] = tuple;
					}
				}
			}
			return tuple;
		}
		return (Entries: Array.Empty<string>(), Default: null);
	}

	internal static (IReadOnlyList<string> Entries, string? Default) LaunchMenu(Dictionary<string, object>? app, Game game)
	{
		if (!(app?.GetValueOrDefault("config") is Dictionary<string, object> dictionary) || !(dictionary.GetValueOrDefault("launch") is Dictionary<string, object> dictionary2))
		{
			return (Entries: Array.Empty<string>(), Default: null);
		}
		string install = Path.GetFullPath(game.InstallDir).TrimEnd(new char[2] { '\\', '/' });
		List<Dictionary<string, object>> source = (from e in dictionary2.Values.OfType<Dictionary<string, object>>()
			where e.GetValueOrDefault("executable") is string exe && StartsTheGame(exe)
			where !(e.GetValueOrDefault("config") is Dictionary<string, object> dictionary3) || (!dictionary3.ContainsKey("BetaKey") && (!(dictionary3.GetValueOrDefault("oslist") is string text) || text.Contains("windows", StringComparison.OrdinalIgnoreCase)))
			select e).ToList();
		return new ValueTuple<IReadOnlyList<string>, string>(item2: source.Where((Dictionary<string, object> e) => "default".Equals(e.GetValueOrDefault("type") as string, StringComparison.OrdinalIgnoreCase)).Select(Text).FirstOrDefault(), item1: source.Select(Text).ToList());
		bool StartsTheGame(string exe)
		{
			string fullPath = Path.GetFullPath(Path.Combine(install, exe));
			if (!fullPath.Equals(Path.GetFullPath(game.ExePath), StringComparison.OrdinalIgnoreCase))
			{
				if (Path.GetDirectoryName(fullPath).Equals(install, StringComparison.OrdinalIgnoreCase))
				{
					return Path.GetFileNameWithoutExtension(fullPath).Equals(ExeStem(game), StringComparison.OrdinalIgnoreCase);
				}
				return false;
			}
			return true;
		}
		static string Text(Dictionary<string, object> e)
		{
			return $"{e.GetValueOrDefault("arguments")} {e.GetValueOrDefault("description")}";
		}
	}
}
