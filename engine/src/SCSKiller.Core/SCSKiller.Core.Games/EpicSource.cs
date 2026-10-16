using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SCSKiller.Core.Games;

/// <summary>
/// Epic games, from every record of an install on this PC: the new launcher's (2026 "eglv2") installations.jsonl
/// (SCSKiller 1.3.0-beta.2's reader, which wins over a leftover .item for the same app), the old Epic Games
/// Launcher's .item manifests, and Legendary's installed.json (Exo Launcher, Heroic). One game per install folder,
/// ids epic:&lt;AppName&gt; whichever record it came from.
/// </summary>
public sealed class EpicSource(string? manifestsDir = null, string? launcherDir = null, IEnumerable<string>? legendaryFiles = null) : IGameSource
{
	internal sealed record Install(string App, string Namespace, string ItemId, string Dir, string? Exe, string Title, string? Version, bool Complete);

	public Store Store => Store.Epic;

	public static string DefaultManifestsDir
	{
		get
		{
			InlineArray5<string> buffer = default;
			buffer[0] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
			buffer[1] = "Epic";
			buffer[2] = "EpicGamesLauncher";
			buffer[3] = "Data";
			buffer[4] = "Manifests";
			return Path.Combine(buffer);
		}
	}

	/// <summary>
	/// The new launcher's data folder: the one HKCU\Software\Epic Games\EpicGamesLauncher AppDataPath names when it
	/// exists (Exo), else %LOCALAPPDATA%\com.epicgames.eglv2 (SCSKiller beta.2).
	/// </summary>
	public static string DefaultLauncherDir => EpicInstalls.RegistryDataDir() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "com.epicgames.eglv2");

	public IReadOnlyList<Game> Discover()
	{
		List<Game> list = new List<Game>();
		HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		void Add(string appName, string? title, string installDir, string? launchExe, string? version)
		{
			string key;
			try
			{
				key = GameFiles.DirKey(installDir);
			}
			catch (Exception ex) when ((ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) ? true : false)
			{
				return;
			}
			if (dirs.Contains(key) || ids.Contains("epic:" + appName))
			{
				return;
			}
			string text = GameFiles.FindExe(installDir, string.IsNullOrEmpty(launchExe) ? null : launchExe);
			if (text != null)
			{
				dirs.Add(key);
				ids.Add("epic:" + appName);
				list.Add(new Game("epic:" + appName, string.IsNullOrWhiteSpace(title) ? appName : title.Trim(), Store.Epic, installDir, text, version));
			}
		}
		List<Install> list2 = Installs(launcherDir ?? ((manifestsDir == null) ? DefaultLauncherDir : null));
		foreach (Install item in list2)
		{
			// complete=false is a first download still running (no version yet) or an update being applied (the
			// game is on disk at its last version, so it stays listed).
			if (item.Complete || item.Version != null)
			{
				Add(item.App, item.Title, item.Dir, item.Exe, item.Version);
			}
		}
		HashSet<string> hashSet = list2.Select((Install i) => i.App).ToHashSet();
		string path = manifestsDir ?? DefaultManifestsDir;
		if (Directory.Exists(path))
		{
			foreach (string item2 in Directory.EnumerateFiles(path, "*.item"))
			{
				try
				{
					using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(item2));
					JsonElement r = jsonDocument.RootElement;
					if (r.TryGetProperty("bIsIncompleteInstall", out var value) && value.ValueKind == JsonValueKind.True)
					{
						continue;
					}
					string text2 = Str(r, "AppName");
					if (text2 == null)
					{
						continue;
					}
					string text3 = Str(r, "InstallLocation");
					if (text3 == null || hashSet.Contains(text2))
					{
						continue;
					}
					string text4 = Str(r, "MainGameAppName");
					if (text4 == null || text4.Length <= 0 || !(text4 != text2))
					{
						Add(text2, Str(r, "DisplayName"), text3, Str(r, "LaunchExecutable"), Str(r, "AppVersionString"));
					}
				}
				catch (Exception ex) when ((ex is JsonException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
				}
			}
		}
		foreach (EpicInstalls.LegendaryInstall install in EpicInstalls.LegendaryInstalls((manifestsDir == null) ? (legendaryFiles ?? EpicInstalls.LegendaryInstalledFiles()) : (legendaryFiles ?? Array.Empty<string>())))
		{
			Add(install.AppName, install.Title, install.Dir, install.Executable, install.Version);
		}
		return list;
	}

	/// <summary>
	/// installations.jsonl is a transaction log: the newest line for an installationId wins; one marked removed or
	/// deleted is dropped (Exo). DLC (a parentInstallationId, or a catalog item with a mainGameItem) is left out.
	/// </summary>
	internal static List<Install> Installs(string? launcherDir)
	{
		string text = ((launcherDir == null) ? null : Path.Combine(launcherDir, "installations.jsonl"));
		if (text == null || !File.Exists(text))
		{
			return new List<Install>();
		}
		Dictionary<string, JsonElement> dictionary = new Dictionary<string, JsonElement>();
		try
		{
			using StreamReader streamReader = new StreamReader(new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
			while (true)
			{
				string text2 = streamReader.ReadLine();
				if (text2 == null)
				{
					break;
				}
				try
				{
					using JsonDocument jsonDocument = JsonDocument.Parse(text2);
					JsonElement rootElement = jsonDocument.RootElement;
					if (rootElement.ValueKind == JsonValueKind.Object)
					{
						string text3 = Str(rootElement, "installationId");
						if (text3 != null)
						{
							string op = Str(rootElement, "op");
							if (Flag(rootElement, "removed") || Flag(rootElement, "deleted") || op == "remove" || op == "delete")
							{
								dictionary.Remove(text3);
							}
							else
							{
								dictionary[text3] = rootElement.Clone();
							}
						}
					}
				}
				catch (JsonException)
				{
				}
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return new List<Install>();
		}
		Dictionary<string, JsonElement> dictionary2 = Catalog(launcherDir);
		List<Install> list = new List<Install>();
		foreach (JsonElement value4 in dictionary.Values)
		{
			string text4 = Str(value4, "artifactId");
			if (text4 == null || text4.Length <= 0)
			{
				continue;
			}
			string text5 = Str(value4, "dir");
			if (text5 == null || text5.Length <= 0)
			{
				continue;
			}
			string text6 = Str(value4, "parentInstallationId");
			if (text6 != null && text6.Length > 0)
			{
				continue;
			}
			string text7 = Str(value4, "itemId") ?? "";
			bool flag = dictionary2.TryGetValue(text7, out var value);
			if (flag && value.TryGetProperty("mainGameItem", out var value2) && value2.ValueKind != JsonValueKind.Null)
			{
				continue;
			}
			text5 = Path.TrimEndingDirectorySeparator(text5);
			string text8 = (flag && Str(value, "title") is { Length: >0 } title) ? title : null;
			string obj2 = Str(value4, "namespace") ?? "";
			string text10 = Str(value4, "launchExecutable");
			string exe = ((text10 != null && text10.Length > 0) ? text10 : null);
			string text11 = text8;
			if (text11 == null)
			{
				string fileName = Path.GetFileName(text5);
				text11 = ((fileName != null && fileName.Length > 0) ? fileName : text4);
			}
			string text12 = Str(value4, "version");
			list.Add(new Install(text4, obj2, text7, text5, exe, text11, (text12 != null && text12.Length > 0) ? text12 : null, value4.TryGetProperty("complete", out var value3) && value3.ValueKind == JsonValueKind.True));
		}
		return list;
	}

	private static Dictionary<string, JsonElement> Catalog(string launcherDir)
	{
		Dictionary<string, JsonElement> dictionary = new Dictionary<string, JsonElement>();
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(launcherDir, "cache", "prod", "catalog-cache.json")));
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.ValueKind == JsonValueKind.Object && rootElement.TryGetProperty("items", out var value) && value.ValueKind == JsonValueKind.Object)
			{
				foreach (JsonProperty item in value.EnumerateObject())
				{
					if (item.Value.ValueKind == JsonValueKind.Object && item.Value.TryGetProperty("item", out var value2) && value2.ValueKind == JsonValueKind.Object)
					{
						dictionary[item.Name] = value2.Clone();
					}
				}
			}
		}
		catch (Exception ex) when ((ex is JsonException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
		return dictionary;
	}

	private static string? Str(JsonElement e, string k)
	{
		if (!e.TryGetProperty(k, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return value.GetString();
	}

	private static bool Flag(JsonElement e, string name)
	{
		if (e.TryGetProperty(name, out var value))
		{
			return value.ValueKind == JsonValueKind.True;
		}
		return false;
	}
}

/// <summary>
/// Epic install records the SCSKiller launchers don't read, read the way Exo Launcher reads them
/// (ExoLauncher\Adapters\EpicAdapter.cs, EpicLauncherV2.cs): Legendary's installed.json, and the registry's name for
/// the new launcher's data folder.
/// </summary>
public static class EpicInstalls
{
	public sealed record LegendaryInstall(string AppName, string? Title, string Dir, string? Executable, string? Version);

	/// <summary>The folder HKCU\Software\Epic Games\EpicGamesLauncher AppDataPath names, when it exists.</summary>
	public static string? RegistryDataDir()
	{
		try
		{
			using Microsoft.Win32.RegistryKey registryKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Epic Games\\EpicGamesLauncher");
			if (registryKey?.GetValue("AppDataPath") is string { Length: >0 } text && Directory.Exists(text))
			{
				return text;
			}
		}
		catch (Exception ex) when ((ex is System.Security.SecurityException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
		return null;
	}

	/// <summary>
	/// Legendary's installed.json candidates, in the order Exo Launcher looks for its user.json: LEGENDARY_CONFIG_PATH,
	/// XDG_CONFIG_HOME, %USERPROFILE%\.config, %APPDATA%, %LOCALAPPDATA% (each also under heroic\legendaryConfig), and
	/// %LOCALAPPDATA%\ExoLauncher (\tools).
	/// </summary>
	public static IEnumerable<string> LegendaryInstalledFiles()
	{
		List<string> list = new List<string>();
		string environmentVariable = Environment.GetEnvironmentVariable("LEGENDARY_CONFIG_PATH");
		if (!string.IsNullOrWhiteSpace(environmentVariable))
		{
			list.Add(environmentVariable);
		}
		string environmentVariable2 = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		if (!string.IsNullOrWhiteSpace(environmentVariable2))
		{
			list.Add(Path.Combine(environmentVariable2, "legendary"));
			list.Add(Path.Combine(environmentVariable2, "heroic", "legendaryConfig", "legendary"));
		}
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (folderPath.Length > 0)
		{
			list.Add(Path.Combine(folderPath, ".config", "legendary"));
			list.Add(Path.Combine(folderPath, ".config", "heroic", "legendaryConfig", "legendary"));
		}
		Environment.SpecialFolder[] array = new Environment.SpecialFolder[2]
		{
			Environment.SpecialFolder.ApplicationData,
			Environment.SpecialFolder.LocalApplicationData
		};
		foreach (Environment.SpecialFolder folder in array)
		{
			string folderPath2 = Environment.GetFolderPath(folder);
			if (folderPath2.Length > 0)
			{
				list.Add(Path.Combine(folderPath2, "legendary"));
				list.Add(Path.Combine(folderPath2, "heroic", "legendaryConfig", "legendary"));
			}
		}
		string folderPath3 = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (folderPath3.Length > 0)
		{
			list.Add(Path.Combine(folderPath3, "ExoLauncher", "legendary"));
			list.Add(Path.Combine(folderPath3, "ExoLauncher", "tools", "legendary"));
		}
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string item in list)
		{
			string text = Path.Combine(item, "installed.json");
			if (seen.Add(text))
			{
				yield return text;
			}
		}
	}

	/// <summary>Legendary's installed.json: {appName: {app_name, title, install_path, executable, version, is_dlc}}. DLC is left out.</summary>
	public static IReadOnlyList<LegendaryInstall> LegendaryInstalls(IEnumerable<string> files)
	{
		List<LegendaryInstall> list = new List<LegendaryInstall>();
		foreach (string file in files)
		{
			try
			{
				if (!File.Exists(file))
				{
					continue;
				}
				using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(file));
				if (jsonDocument.RootElement.ValueKind != JsonValueKind.Object)
				{
					continue;
				}
				foreach (JsonProperty item in jsonDocument.RootElement.EnumerateObject())
				{
					JsonElement value = item.Value;
					if (value.ValueKind != JsonValueKind.Object || (value.TryGetProperty("is_dlc", out var value2) && value2.ValueKind == JsonValueKind.True))
					{
						continue;
					}
					string text = Text(value, "app_name") ?? item.Name;
					string text2 = Text(value, "install_path");
					string text3 = Text(value, "platform");
					if (text.Length > 0 && text2 != null && text2.Length > 0 && (text3 == null || text3.StartsWith("Win", StringComparison.OrdinalIgnoreCase)))
					{
						list.Add(new LegendaryInstall(text, Text(value, "title"), text2, Text(value, "executable"), Text(value, "version")));
					}
				}
			}
			catch (Exception ex) when ((ex is JsonException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
		}
		return list;
	}

	internal static string? Text(JsonElement e, string name)
	{
		if (!e.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return value.GetString();
	}
}
