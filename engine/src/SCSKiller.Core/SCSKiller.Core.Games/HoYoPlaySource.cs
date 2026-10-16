using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

public sealed class HoYoPlaySource(IEnumerable<(string Biz, string InstallDir)>? entries = null) : IGameSource
{
	private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
	{
		["hk4e"] = "Genshin Impact",
		["hkrpg"] = "Honkai: Star Rail",
		["nap"] = "Zenless Zone Zero",
		["bh3"] = "Honkai Impact 3rd"
	};

	private static readonly Regex GameVersion = new Regex("^\\s*game_version\\s*=\\s*(\\S+)", RegexOptions.Multiline);

	public Store Store => Store.Other;

	public IReadOnlyList<Game> Discover()
	{
		List<Game> list = new List<Game>();
		foreach (var (text, text2) in entries ?? Entries())
		{
			if (!Directory.Exists(text2))
			{
				continue;
			}
			string input;
			try
			{
				input = File.ReadAllText(Path.Combine(text2, "config.ini"));
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				continue;
			}
			MatchCollection matchCollection = GameVersion.Matches(input);
			if (matchCollection != null && matchCollection.Count > 0)
			{
				string text3 = GameFiles.FindExe(text2);
				if (text3 != null)
				{
					string name = Names.GetValueOrDefault(text.Split('_')[0]) ?? Path.GetFileNameWithoutExtension(text3);
					list.Add(new Game("hoyoplay:" + text, name, Store.Other, text2, text3, matchCollection[matchCollection.Count - 1].Groups[1].Value));
				}
			}
		}
		return list;
	}

	private static IEnumerable<(string, string)> Entries()
	{
		string[] array = new string[2] { "Software\\Cognosphere\\HYP", "Software\\miHoYo\\HYP" };
		foreach (string name in array)
		{
			using RegistryKey hyp = Registry.CurrentUser.OpenSubKey(name);
			if (hyp == null)
			{
				continue;
			}
			string[] subKeyNames = hyp.GetSubKeyNames();
			int num = 0;
			while (true)
			{
				if (num < subKeyNames.Length)
				{
					string name2 = subKeyNames[num];
					using (RegistryKey v = hyp.OpenSubKey(name2))
					{
						if (v != null)
						{
							string[] subKeyNames2 = v.GetSubKeyNames();
							int num2 = 0;
							while (true)
							{
								if (num2 < subKeyNames2.Length)
								{
									string text = subKeyNames2[num2];
									using (RegistryKey key = v.OpenSubKey(text))
									{
										if (key?.GetValue("GameInstallPath") is string { Length: >0 } text2)
										{
											yield return (text, text2);
										}
									}
									num2++;
									continue;
								}
								goto end_IL_00b9;
							}
						}
						end_IL_00b9:;
					}
					num++;
					continue;
				}
				goto end_IL_0069;
			}
			end_IL_0069:;
		}
	}
}
