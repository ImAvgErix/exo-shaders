using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>
/// Ubisoft Connect games: Ubisoft\Launcher\Installs in HKLM and HKCU, 64- and 32-bit views (as Exo Launcher reads them),
/// titled from the launcher's "Uplay Install &lt;id&gt;" uninstall entry.
/// </summary>
public sealed class UbisoftSource : IGameSource
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
			string[] array2 = new string[2] { "SOFTWARE\\WOW6432Node\\Ubisoft\\Launcher\\Installs", "SOFTWARE\\Ubisoft\\Launcher\\Installs" };
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
						if (!(registryKey3?.GetValue("InstallDir") is string { Length: >0 } text2) || !Directory.Exists(text2) || ids.Contains(text))
						{
							continue;
						}
						string text3 = GameFiles.DirKey(text2);
						if (!dirs.Contains(text3))
						{
							string text4 = GameFiles.FindExe(text2);
							if (text4 != null)
							{
								ids.Add(text);
								dirs.Add(text3);
								list.Add(new Game("ubisoft:" + text, Title(text) ?? GameFiles.FolderName(text2), Store.Other, text2, text4));
							}
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

	private static string? Title(string id)
	{
		string text = EaSource.ReadRegistry("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Uplay Install " + id + "\\DisplayName");
		if (text == null || string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		return text.Trim();
	}
}
