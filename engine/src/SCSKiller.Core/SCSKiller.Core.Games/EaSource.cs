using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

public sealed class EaSource(IEnumerable<string>? installDirs = null, Func<string, string?>? registry = null) : IGameSource
{
	public Store Store => Store.EA;

	public IReadOnlyList<Game> Discover()
	{
		List<Game> list = new List<Game>();
		foreach (string item in (installDirs ?? InstallDirs()).Select(GameFiles.DirKey).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			string text = Path.Combine(item, "__Installer", "installerdata.xml");
			if (!File.Exists(text))
			{
				continue;
			}
			try
			{
				Game game = Parse(item, XDocument.Load(text));
				if ((object)game != null)
				{
					list.Add(game);
				}
			}
			catch (Exception ex) when ((ex is XmlException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
		}
		return list;
	}

	private Game? Parse(string dir, XDocument doc)
	{
		XElement root = doc.Root;
		XElement xElement = root.Descendants("launcher").FirstOrDefault((XElement l) => (string?)l.Element("trial") != "1");
		Match match = Regex.Match(((string?)xElement?.Element("filePath"))?.Trim() ?? "", "^(?:\\[(?<reg>[^\\]]+)\\])?(?<rel>.*)$");
		if (match.Groups["reg"].Success)
		{
			string text = Read(match.Groups["reg"].Value);
			if (text == null || text.Length <= 0 || !GameFiles.DirKey(text).Equals(dir, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
		}
		string text2 = match.Groups["rel"].Value.TrimStart(new char[2] { '\\', '/' });
		string text3 = GameFiles.FindExe(dir, (text2.Length > 0) ? text2 : null);
		if (text3 == null)
		{
			return null;
		}
		string text4 = (En(root.Descendants("gameTitle")) ?? En(xElement?.Elements("name")))?.Replace("™", "").Replace("®", "").Trim();
		string text5 = (from c in root.Descendants("contentID")
			select c.Value.Trim()).FirstOrDefault((string c) => c.Length > 0) ?? Path.GetFileName(dir);
		return new Game("ea:" + text5, string.IsNullOrEmpty(text4) ? Path.GetFileName(dir) : text4, Store.EA, dir, text3, (string?)root.Descendants("gameVersion").FirstOrDefault()?.Attribute("version"));
		static string? En(IEnumerable<XElement>? e)
		{
			return e?.OrderBy((XElement t) => (!((string?)t.Attribute("locale") == "en_US")) ? 1 : 0).Select((XElement t) => t.Value).FirstOrDefault();
		}
	}

	private string? Read(string path)
	{
		return (registry ?? new Func<string, string>(ReadRegistry))(path);
	}

	public static string? ReadRegistry(string path)
	{
		string[] array = path.Split('\\', 2);
		int num = ((array.Length == 2) ? array[1].LastIndexOf('\\') : (-1));
		RegistryHive? registryHive;
		switch (array[0].ToUpperInvariant())
		{
		case "HKEY_LOCAL_MACHINE":
		case "HKLM":
			registryHive = RegistryHive.LocalMachine;
			break;
		case "HKEY_CURRENT_USER":
		case "HKCU":
			registryHive = RegistryHive.CurrentUser;
			break;
		default:
			registryHive = null;
			break;
		}
		RegistryHive? registryHive2 = registryHive;
		if (!registryHive2.HasValue || num < 0)
		{
			return null;
		}
		RegistryView[] array2 = new RegistryView[2]
		{
			RegistryView.Registry64,
			RegistryView.Registry32
		};
		foreach (RegistryView view in array2)
		{
			using RegistryKey registryKey = RegistryKey.OpenBaseKey(registryHive2.Value, view);
			using RegistryKey registryKey2 = registryKey.OpenSubKey(array[1].Substring(0, num));
			if (registryKey2?.GetValue(array[1].Substring(num + 1)) is string result)
			{
				return result;
			}
		}
		return null;
	}

	public static IEnumerable<string> InstallDirs()
	{
		List<string> list = new List<string>();
		string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Electronic Arts", "EA Desktop");
		List<string> list2 = new List<string>
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EA Games"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Origin Games"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Origin Games")
		};
		try
		{
			if (Directory.Exists(path))
			{
				foreach (string item in Directory.EnumerateFiles(path, "user_*.ini"))
				{
					list2.AddRange(from l in File.ReadLines(item)
						where l.StartsWith("user.downloadinplacedir=", StringComparison.OrdinalIgnoreCase)
						select l.Substring(l.IndexOf('=') + 1).Trim());
				}
			}
		}
		catch (IOException)
		{
		}
		foreach (string item2 in list2.Where((string l) => l.Length > 0 && Directory.Exists(l)))
		{
			list.AddRange(Directory.EnumerateDirectories(item2));
		}
		RegistryView[] array = new RegistryView[2]
		{
			RegistryView.Registry64,
			RegistryView.Registry32
		};
		foreach (var (hive, view) in new (RegistryHive, RegistryView)[3]
		{
			(RegistryHive.LocalMachine, array[0]),
			(RegistryHive.LocalMachine, array[1]),
			(RegistryHive.CurrentUser, RegistryView.Default)
		})
		{
			using RegistryKey registryKey = RegistryKey.OpenBaseKey(hive, view);
			using RegistryKey registryKey2 = registryKey.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall");
			string[] array2 = registryKey2?.GetSubKeyNames() ?? Array.Empty<string>();
			foreach (string name in array2)
			{
				using RegistryKey registryKey3 = registryKey2.OpenSubKey(name);
				if (registryKey3?.GetValue("InstallLocation") is string { Length: >0 } text && Directory.Exists(text))
				{
					list.Add(text);
				}
			}
		}
		return list;
	}
}
