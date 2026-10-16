using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.Games;

public sealed class RiotSource(string? metadataDir = null, Func<string, string?>? registry = null) : IGameSource
{
	private static readonly Dictionary<string, (string Name, string? Exe)> Products = new Dictionary<string, (string, string)>
	{
		["valorant"] = ("VALORANT", null),
		["league_of_legends"] = ("League of Legends", "Game\\League of Legends.exe"),
		["bacon"] = ("Legends of Runeterra", null),
		["lion"] = ("2XKO", null)
	};

	private static readonly Regex InstallPath = new Regex("^product_install_full_path:[ \\t]*[\"']?(.*?)[\"']?[ \\t\\r]*$", RegexOptions.Multiline);

	public Store Store => Store.Other;

	public static string DefaultMetadataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "Metadata");

	public IReadOnlyList<Game> Discover()
	{
		string path = metadataDir ?? DefaultMetadataDir;
		if (!Directory.Exists(path))
		{
			return Array.Empty<Game>();
		}
		List<Game> list = new List<Game>();
		foreach (string item in Directory.EnumerateDirectories(path))
		{
			string fileName = Path.GetFileName(item);
			string[] array = fileName.Split('.');
			if (array == null || array.Length != 2)
			{
				continue;
			}
			string text = array[0];
			string text2 = array[1];
			if (text == "riot_client")
			{
				continue;
			}
			(string, string) valueOrDefault = Products.GetValueOrDefault(text);
			string text3;
			string exePath;
			try
			{
				Match match = InstallPath.Match(File.ReadAllText(Path.Combine(item, fileName + ".product_settings.yaml")));
				if (match == null || !match.Success || match.Groups[1].Value.Length == 0)
				{
					continue;
				}
				text3 = GameFiles.DirKey(match.Groups[1].Value);
				if (valueOrDefault.Item2 == null || File.Exists(Path.Combine(text3, valueOrDefault.Item2)))
				{
					string text4 = GameFiles.FindExe(text3, valueOrDefault.Item2);
					if (text4 != null)
					{
						exePath = text4;
						goto IL_017c;
					}
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) ? true : false)
			{
			}
			continue;
			IL_017c:
			string text5 = (registry ?? new Func<string, string>(Uninstall))("Riot Game " + fileName);
			string text6 = ((text5 != null && !string.IsNullOrWhiteSpace(text5)) ? text5.Trim() : (valueOrDefault.Item1 ?? text));
			if (text2 != "live" && !text6.Contains(text2, StringComparison.OrdinalIgnoreCase))
			{
				text6 = text6 + " (" + text2.ToUpperInvariant() + ")";
			}
			list.Add(new Game("riot:" + fileName, text6, Store.Other, text3, exePath));
		}
		return list;
	}

	private static string? Uninstall(string entry)
	{
		return EaSource.ReadRegistry("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\" + entry + "\\DisplayName") ?? EaSource.ReadRegistry("HKEY_LOCAL_MACHINE\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\" + entry + "\\DisplayName");
	}
}
