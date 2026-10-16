// Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 Exo Shaders contributors. GPL-3.0-or-later (see LICENSE, LICENSE-EXCEPTION.txt).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>
/// Amazon Games: the Amazon Games app's uninstall entries (UninstallString "Amazon Game Remover.exe ... -p
/// &lt;product id&gt;"), Nile's installed.json (Exo Launcher, Heroic) and fuel.json game folders in the places Exo Launcher
/// scans (ExoLauncher\Adapters\AmazonAdapter.cs, OfficialInstalledLibraries.cs). One game per install folder, ids
/// amazon:&lt;product id&gt; as Exo's.
/// </summary>
public sealed class AmazonSource(IEnumerable<(string Id, string? Title, string Dir)>? entries = null) : IGameSource
{
	public Store Store => Store.Other;

	public IReadOnlyList<Game> Discover()
	{
		List<Game> list = new List<Game>();
		HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (text, title, dir) in entries ?? Entries())
		{
			try
			{
				string key = GameFiles.DirKey(dir);
				string id = "amazon:" + SanitizeId(text);
				if (id.Length <= "amazon:".Length || !Directory.Exists(key) || dirs.Contains(key) || ids.Contains(id))
				{
					continue;
				}
				string exe = FuelExe(key) ?? GameFiles.FindExe(key);
				if (exe != null)
				{
					dirs.Add(key);
					ids.Add(id);
					list.Add(new Game(id, string.IsNullOrWhiteSpace(title) ? GameFiles.FolderName(key) : title.Trim(), Store.Other, key, exe));
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) ? true : false)
			{
			}
		}
		return list;
	}

	/// <summary>The Amazon Games app is there to launch through (its amazon-games:// protocol).</summary>
	public static bool ClientInstalled()
	{
		try
		{
			using RegistryKey registryKey = Registry.ClassesRoot.OpenSubKey("amazon-games");
			return registryKey != null;
		}
		catch (Exception ex) when ((ex is System.Security.SecurityException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return false;
		}
	}

	/// <summary>fuel.json's Main.Command: the exe the Amazon Games app starts.</summary>
	internal static string? FuelExe(string installDir)
	{
		string path = Path.Combine(installDir, "fuel.json");
		if (!File.Exists(path))
		{
			return null;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip
			});
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.ValueKind != JsonValueKind.Object)
			{
				return null;
			}
			JsonElement? jsonElement = Prop(rootElement, "Main");
			if (!jsonElement.HasValue || jsonElement.Value.ValueKind != JsonValueKind.Object)
			{
				return null;
			}
			JsonElement? jsonElement2 = Prop(jsonElement.Value, "Command");
			string text = ((jsonElement2.HasValue && jsonElement2.Value.ValueKind == JsonValueKind.String) ? jsonElement2.Value.GetString() : null)?.Trim().Trim('"');
			if (string.IsNullOrEmpty(text) || Path.IsPathRooted(text))
			{
				return null;
			}
			if (text.StartsWith("./", StringComparison.Ordinal) || text.StartsWith(".\\", StringComparison.Ordinal))
			{
				text = text.Substring(2);
			}
			string fullPath = Path.GetFullPath(Path.Combine(installDir, text));
			if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !GameFiles.Inside(installDir, fullPath) || !File.Exists(fullPath))
			{
				return null;
			}
			return GameFiles.GameExe(installDir, fullPath);
		}
		catch (Exception ex) when ((ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) ? true : false)
		{
			return null;
		}
		static JsonElement? Prop(JsonElement e, string name)
		{
			foreach (JsonProperty item in e.EnumerateObject())
			{
				if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
				{
					return item.Value;
				}
			}
			return null;
		}
	}

	internal static string SanitizeId(string value)
	{
		char[] array = value.Trim().ToLowerInvariant().ToCharArray();
		for (int i = 0; i < array.Length; i++)
		{
			if (!char.IsAsciiLetterOrDigit(array[i]) && array[i] != '-' && array[i] != '_' && array[i] != '.')
			{
				array[i] = '-';
			}
		}
		return new string(array).Trim('-');
	}

	private static IEnumerable<(string, string?, string)> Entries()
	{
		List<(string, string?, string)> list = new List<(string, string?, string)>();
		RegistryKey[] array = new RegistryKey[2]
		{
			Registry.CurrentUser,
			Registry.LocalMachine
		};
		foreach (RegistryKey registryKey in array)
		{
			string[] array2 = new string[2] { "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall" };
			foreach (string name in array2)
			{
				try
				{
					using RegistryKey registryKey2 = registryKey.OpenSubKey(name);
					if (registryKey2 == null)
					{
						continue;
					}
					string[] subKeyNames = registryKey2.GetSubKeyNames();
					foreach (string name2 in subKeyNames)
					{
						using RegistryKey registryKey3 = registryKey2.OpenSubKey(name2);
						if (registryKey3?.GetValue("UninstallString") is string text && text.Contains("Amazon Game Remover", StringComparison.OrdinalIgnoreCase) && registryKey3.GetValue("InstallLocation") is string { Length: >0 } text2)
						{
							Match match = Regex.Match(text, "(?:^|\\s)-p\\s+\"?([\\w.-]+)");
							if (match.Success)
							{
								list.Add((match.Groups[1].Value, registryKey3.GetValue("DisplayName") as string, text2));
							}
						}
					}
				}
				catch (Exception ex) when ((ex is System.Security.SecurityException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
				}
			}
		}
		foreach (string item in NileConfigRoots())
		{
			list.AddRange(NileInstalls(item));
		}
		foreach (string item2 in FuelRoots())
		{
			try
			{
				if (!Directory.Exists(item2))
				{
					continue;
				}
				foreach (string item3 in Directory.EnumerateDirectories(item2))
				{
					if (File.Exists(Path.Combine(item3, "fuel.json")))
					{
						list.Add((Path.GetFileName(item3), null, item3));
					}
				}
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
			{
			}
		}
		return list;
	}

	/// <summary>Nile's config folders, as Exo Launcher's NileCli.ConfigRoots.</summary>
	internal static IEnumerable<string> NileConfigRoots()
	{
		string environmentVariable = Environment.GetEnvironmentVariable("NILE_CONFIG_PATH");
		if (!string.IsNullOrWhiteSpace(environmentVariable))
		{
			yield return Path.Combine(environmentVariable, "nile");
		}
		string environmentVariable2 = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		if (!string.IsNullOrWhiteSpace(environmentVariable2))
		{
			yield return Path.Combine(environmentVariable2, "nile");
		}
		string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		yield return Path.Combine(roaming, "nile");
		yield return Path.Combine(roaming, "heroic", "nile_config", "nile");
		yield return Path.Combine(local, "heroic", "nile_config", "nile");
		yield return Path.Combine(user, ".config", "nile");
		yield return Path.Combine(user, ".config", "heroic", "nile_config", "nile");
	}

	/// <summary>installed.json [{id, path, version}], titled from library.json [{id, product: {id, title}}] beside it.</summary>
	internal static List<(string, string?, string)> NileInstalls(string root)
	{
		List<(string, string?, string)> list = new List<(string, string?, string)>();
		try
		{
			string path = Path.Combine(root, "installed.json");
			if (!File.Exists(path))
			{
				return list;
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			string path2 = Path.Combine(root, "library.json");
			if (File.Exists(path2))
			{
				try
				{
					using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(path2));
					if (jsonDocument.RootElement.ValueKind == JsonValueKind.Array)
					{
						foreach (JsonElement item in jsonDocument.RootElement.EnumerateArray())
						{
							if (item.ValueKind == JsonValueKind.Object)
							{
								JsonElement e = ((item.TryGetProperty("product", out var value) && value.ValueKind == JsonValueKind.Object) ? value : item);
								string text = EpicInstalls.Text(e, "id") ?? EpicInstalls.Text(item, "id");
								string text2 = EpicInstalls.Text(e, "title") ?? EpicInstalls.Text(item, "title");
								if (text != null && text2 != null)
								{
									dictionary[text] = text2;
								}
							}
						}
					}
				}
				catch (JsonException)
				{
				}
			}
			using JsonDocument jsonDocument2 = JsonDocument.Parse(File.ReadAllText(path));
			if (jsonDocument2.RootElement.ValueKind != JsonValueKind.Array)
			{
				return list;
			}
			foreach (JsonElement item2 in jsonDocument2.RootElement.EnumerateArray())
			{
				if (item2.ValueKind == JsonValueKind.Object && EpicInstalls.Text(item2, "id") is { Length: >0 } text3 && EpicInstalls.Text(item2, "path") is { Length: >0 } text4)
				{
					list.Add((text3, dictionary.GetValueOrDefault(text3), text4));
				}
			}
		}
		catch (Exception ex) when ((ex is JsonException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
		return list;
	}

	/// <summary>Folders of fuel.json game folders: Exo Launcher's AmazonGameRoots, and each fixed drive's Amazon Games\Library (the app's default).</summary>
	private static IEnumerable<string> FuelRoots()
	{
		string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		List<string> list = new List<string>
		{
			Path.Combine(local, "Amazon Games", "Data", "Games"),
			Path.Combine(local, "Amazon Games", "Installed"),
			Path.Combine(programData, "Amazon Games", "Installed"),
			Path.Combine(programData, "Amazon Games", "Data", "Games"),
			Path.Combine(user, "Amazon Games"),
			Path.Combine(user, "Amazon Games", "Library")
		};
		try
		{
			list.AddRange(from d in DriveInfo.GetDrives()
				where d.DriveType == DriveType.Fixed && d.IsReady
				select Path.Combine(d.RootDirectory.FullName, "Amazon Games", "Library"));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
		return list;
	}
}
