using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>
/// GOG games: GOG Galaxy's SOFTWARE\GOG.com\Games keys (HKLM and HKCU, 64- and 32-bit views, as Exo Launcher reads
/// them), the installs Exo Launcher makes itself through gogdl (its installed-games.json catalog) and Heroic's
/// gog_store\installed.json. One game per install folder, ids gog:&lt;product id&gt;.
/// </summary>
public sealed class GogSource(IEnumerable<(string Id, string? Name, string Dir, string? Exe)>? entries = null) : IGameSource
{
	public Store Store => Store.Other;

	public IReadOnlyList<Game> Discover()
	{
		List<Game> list = new List<Game>();
		HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (text, text2, dir, text3) in entries ?? Entries())
		{
			string text4;
			try
			{
				text4 = GameFiles.DirKey(dir);
			}
			catch (Exception ex) when ((ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) ? true : false)
			{
				continue;
			}
			if (text.Length == 0 || !Directory.Exists(text4) || dirs.Contains(text4) || ids.Contains(text))
			{
				continue;
			}
			Game game = Read(text, text2 ?? GameFiles.FolderName(text4), text4, text3);
			if ((object)game != null)
			{
				dirs.Add(text4);
				ids.Add(text);
				list.Add(game);
			}
		}
		return list;
	}

	private static Game? Read(string id, string name, string dir, string? exeHint)
	{
		string path = Path.Combine(dir, "goggame-" + id + ".info");
		string text = null;
		string version = null;
		if (File.Exists(path))
		{
			try
			{
				(name, text, version) = ParseInfo(File.ReadAllText(path), dir, name);
			}
			catch (Exception ex) when ((ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) ? true : false)
			{
			}
			if (text == null)
			{
				return null;
			}
		}
		else
		{
			try
			{
				text = ((exeHint is { Length: >0 }) ? Path.GetFullPath(Path.Combine(dir, exeHint)) : null);
			}
			catch (Exception ex2) when ((ex2 is ArgumentException || ex2 is NotSupportedException) ? true : false)
			{
				text = null;
			}
			if (text == null || !File.Exists(text) || !GameFiles.Inside(dir, text))
			{
				text = GameFiles.FindExe(dir);
			}
		}
		if (text == null || !File.Exists(text))
		{
			return null;
		}
		return new Game("gog:" + id, name, Store.Other, dir, text, version);
	}

	private static IEnumerable<(string, string?, string, string?)> Entries()
	{
		List<(string, string?, string, string?)> list = new List<(string, string?, string, string?)>();
		RegistryKey[] array = new RegistryKey[2]
		{
			Registry.LocalMachine,
			Registry.CurrentUser
		};
		foreach (RegistryKey registryKey in array)
		{
			string[] array2 = new string[2] { "SOFTWARE\\WOW6432Node\\GOG.com\\Games", "SOFTWARE\\GOG.com\\Games" };
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
					foreach (string text in subKeyNames)
					{
						using RegistryKey registryKey3 = registryKey2.OpenSubKey(text);
						if (registryKey3?.GetValue("path") is string { Length: >0 } text2)
						{
							list.Add((text, registryKey3.GetValue("gameName") as string, text2, registryKey3.GetValue("exe") as string));
						}
					}
				}
				catch (Exception ex) when ((ex is System.Security.SecurityException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
				}
			}
		}
		list.AddRange(ExoCatalog());
		list.AddRange(Heroic());
		return list;
	}

	/// <summary>
	/// Exo Launcher's durable record of the installs it made itself (gogdl): %LOCALAPPDATA%\ExoLauncher\installed-games.json
	/// (EXO_LAUNCHER_DATA_DIR overrides the folder), {version, games: [{id: "gog:&lt;product id&gt;", title, store: "gog", installPath}]}.
	/// </summary>
	internal static IEnumerable<(string, string?, string, string?)> ExoCatalog(string? path = null)
	{
		List<(string, string?, string, string?)> list = new List<(string, string?, string, string?)>();
		if (path == null)
		{
			string environmentVariable = Environment.GetEnvironmentVariable("EXO_LAUNCHER_DATA_DIR");
			path = Path.Combine((!string.IsNullOrWhiteSpace(environmentVariable)) ? environmentVariable : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher"), "installed-games.json");
		}
		try
		{
			if (!File.Exists(path))
			{
				return list;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(path));
			JsonElement? jsonElement = Prop(jsonDocument.RootElement, "games");
			if (!jsonElement.HasValue || jsonElement.Value.ValueKind != JsonValueKind.Array)
			{
				return list;
			}
			foreach (JsonElement item in jsonElement.Value.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object)
				{
					string text = Str(item, "store");
					string text2 = Str(item, "id");
					string text3 = Str(item, "installPath");
					if (text != null && text.Equals("gog", StringComparison.OrdinalIgnoreCase) && text2 != null && text2.StartsWith("gog:", StringComparison.OrdinalIgnoreCase) && text3 != null && Path.IsPathFullyQualified(text3))
					{
						list.Add((text2.Substring(4), Str(item, "title"), text3, null));
					}
				}
			}
		}
		catch (Exception ex) when ((ex is JsonException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
		return list;
	}

	/// <summary>Heroic's gog_store\installed.json: {installed: [{appName, install_path, platform}]}.</summary>
	private static IEnumerable<(string, string?, string, string?)> Heroic()
	{
		List<(string, string?, string, string?)> list = new List<(string, string?, string, string?)>();
		string[] array = new string[2]
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "heroic", "gog_store", "installed.json"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "heroic", "gog_store", "installed.json")
		};
		foreach (string path in array)
		{
			try
			{
				if (!File.Exists(path))
				{
					continue;
				}
				using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(path));
				JsonElement? jsonElement = Prop(jsonDocument.RootElement, "installed");
				if (!jsonElement.HasValue || jsonElement.Value.ValueKind != JsonValueKind.Array)
				{
					continue;
				}
				foreach (JsonElement item in jsonElement.Value.EnumerateArray())
				{
					if (item.ValueKind == JsonValueKind.Object && Str(item, "appName") is { Length: >0 } text && Str(item, "install_path") is { Length: >0 } text2)
					{
						string text3 = Str(item, "platform");
						if (text3 == null || text3.Equals("windows", StringComparison.OrdinalIgnoreCase))
						{
							list.Add((text, null, text2, null));
						}
					}
				}
			}
			catch (Exception ex) when ((ex is JsonException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
		}
		return list;
	}

	private static JsonElement? Prop(JsonElement e, string name)
	{
		if (e.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		foreach (JsonProperty item in e.EnumerateObject())
		{
			if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				return item.Value;
			}
		}
		return null;
	}

	private static string? Str(JsonElement e, string name)
	{
		JsonElement? jsonElement = Prop(e, name);
		if (!jsonElement.HasValue || jsonElement.Value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return jsonElement.Value.GetString();
	}

	private static (string Name, string? Exe, string? Version) ParseInfo(string json, string install, string fallbackName)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(json);
		JsonElement rootElement = jsonDocument.RootElement;
		string item = ((rootElement.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String) ? value.GetString() : fallbackName);
		string item2 = ((rootElement.TryGetProperty("buildId", out var value2) && value2.ValueKind == JsonValueKind.String) ? value2.GetString() : null);
		string item3 = null;
		if (rootElement.TryGetProperty("playTasks", out var value3) && value3.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item4 in value3.EnumerateArray())
			{
				if (item4.TryGetProperty("category", out var value4) && value4.ValueKind == JsonValueKind.String && value4.GetString() == "game" && item4.TryGetProperty("path", out var value5) && value5.ValueKind == JsonValueKind.String)
				{
					item3 = Path.GetFullPath(Path.Combine(install, value5.GetString()));
					break;
				}
			}
		}
		return (Name: item, Exe: item3, Version: item2);
	}
}
