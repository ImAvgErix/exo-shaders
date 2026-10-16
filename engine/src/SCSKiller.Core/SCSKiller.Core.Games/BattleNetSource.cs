using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>
/// Battle.net games: the uninstall entries Battle.net writes for each game (publisher Blizzard Entertainment or a
/// "Battle.net Uninstaller" UninstallString with --uid=), in HKLM and HKCU, 64- and 32-bit views, as Exo Launcher reads them.
/// </summary>
public sealed class BattleNetSource : IGameSource
{
	public Store Store => Store.Other;

	public IReadOnlyList<Game> Discover()
	{
		List<Game> list = new List<Game>();
		HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		RegistryKey[] array = new RegistryKey[2]
		{
			Registry.LocalMachine,
			Registry.CurrentUser
		};
		foreach (RegistryKey registryKey in array)
		{
			string[] array2 = new string[2] { "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall" };
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
						if (registryKey3 == null)
						{
							continue;
						}
						string text = registryKey3.GetValue("UninstallString") as string;
						bool flag = text != null && text.Contains("Battle.net Uninstaller", StringComparison.OrdinalIgnoreCase);
						if ((registryKey3.GetValue("Publisher") as string != "Blizzard Entertainment" && !flag) || !(registryKey3.GetValue("DisplayName") is string text2) || text2 == "Battle.net" || !(registryKey3.GetValue("InstallLocation") is string { Length: >0 } text3) || !Directory.Exists(text3))
						{
							continue;
						}
						string text4 = ((text != null) ? Regex.Match(text, "--uid=(\\S+)").Groups[1].Value.Trim('"') : "");
						if (text4.Length == 0)
						{
							text4 = Regex.Replace(text2, "\\W+", "").ToLowerInvariant();
						}
						string text5 = GameFiles.DirKey(text3);
						if (ids.Contains(text4) || dirs.Contains(text5))
						{
							continue;
						}
						string text6 = GameFiles.FindExe(text3);
						if (text6 != null)
						{
							ids.Add(text4);
							dirs.Add(text5);
							list.Add(new Game("battlenet:" + text4, text2, Store.Other, text3, text6));
						}
					}
				}
				catch (Exception ex) when ((ex is System.Security.SecurityException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) ? true : false)
				{
				}
			}
		}
		return list;
	}
}
