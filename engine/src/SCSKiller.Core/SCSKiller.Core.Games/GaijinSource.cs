using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

public sealed class GaijinSource : IGameSource
{
	public Store Store => Store.Other;

	public IReadOnlyList<Game> Discover()
	{
		List<Game> list = new List<Game>();
		using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Gaijin");
		if (registryKey == null)
		{
			return list;
		}
		string[] subKeyNames = registryKey.GetSubKeyNames();
		foreach (string text in subKeyNames)
		{
			using RegistryKey registryKey2 = registryKey.OpenSubKey(text);
			if (registryKey2?.GetValue("InstallDir") is string text2 && Directory.Exists(text2))
			{
				string text3 = Exe(text2);
				if (text3 != null)
				{
					list.Add(new Game("gaijin:" + text.ToLowerInvariant(), Regex.Replace(text, "(?<=[a-z])(?=[A-Z])", " "), Store.Other, text2, text3));
				}
			}
		}
		return list;
	}

	internal static string? Exe(string install)
	{
		string path = Path.Combine(install, "BattlEye", "BELauncher.ini");
		string[] source;
		try
		{
			source = File.ReadAllLines(path);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
		string[]? array = source.Select((string l) => l.Split('=', 2)).FirstOrDefault((string[] p) => p.Length == 2 && p[0].Trim().Equals("64BitExe", StringComparison.OrdinalIgnoreCase));
		string text = ((array != null) ? array[1].Trim() : null);
		if (string.IsNullOrEmpty(text) || Path.IsPathRooted(text))
		{
			return null;
		}
		string fullPath = Path.GetFullPath(Path.Combine(install, text));
		if (!GameFiles.Inside(install, fullPath) || !File.Exists(fullPath))
		{
			return null;
		}
		return fullPath;
	}
}
