using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

public sealed class PurpleSource(IEnumerable<(string Code, string Name, string InstallDir)>? entries = null) : IGameSource
{
	public Store Store => Store.Other;

	public IReadOnlyList<Game> Discover()
	{
		List<Game> list = new List<Game>();
		foreach (var (text, name, text2) in entries ?? Entries())
		{
			if (!Directory.Exists(text2))
			{
				continue;
			}
			string text3 = Path.Combine(text2, "VersionInfo_" + text + ".xml");
			if (File.Exists(text3))
			{
				string text4;
				try
				{
					text4 = XDocument.Load(text3).Root?.Element("Version")?.Value.Trim();
				}
				catch (Exception ex) when ((ex is XmlException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
					continue;
				}
				string text5 = GameFiles.FindExe(text2);
				if (text5 != null)
				{
					list.Add(new Game("purple:" + text, name, Store.Other, text2, text5, string.IsNullOrEmpty(text4) ? null : text4));
				}
			}
		}
		return list;
	}

	public static string? LauncherExe()
	{
		return LauncherIn(Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\nc-purple\\shell\\open\\command", null, null) as string);
	}

	internal static string? LauncherIn(string? command)
	{
		if (command == null)
		{
			return null;
		}
		Match match = Regex.Match(command, "^\\s*(?:\"(?<exe>[^\"]+)\"|(?<exe>.+?\\.exe)(?=\\s|$))", RegexOptions.IgnoreCase);
		if (!match.Success)
		{
			return null;
		}
		string value = match.Groups["exe"].Value;
		if (!Path.GetFileName(value).Equals("PurpleLauncher.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(value))
		{
			return null;
		}
		return value;
	}

	private static IEnumerable<(string, string, string)> Entries()
	{
		using RegistryKey root = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall");
		if (root == null)
		{
			yield break;
		}
		string[] subKeyNames = root.GetSubKeyNames();
		foreach (string name in subKeyNames)
		{
			using RegistryKey key = root.OpenSubKey(name);
			if (key?.GetValue("UninstallString") is string input)
			{
				Match match = Regex.Match(input, "PurpleLauncher\\.exe\"?\\s+--uninstall\\s+\"?([\\w.-]+)", RegexOptions.IgnoreCase);
				if (match != null && match.Success && key.GetValue("InstallLocation") is string { Length: not 0 } text)
				{
					string value = match.Groups[1].Value;
					yield return (value, (key.GetValue("DisplayName") as string) ?? value, text);
				}
			}
		}
	}
}
