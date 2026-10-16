using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

public static class StoreLaunch
{
	public static bool Supported(Game g)
	{
		bool flag = g.Store == Store.Manual;
		if (!flag)
		{
			bool flag2;
			switch (Prefix(g))
			{
			case "steam":
			case "epic":
			case "xbox":
			case "ubisoft":
				flag2 = true;
				break;
			case "gog":
				flag2 = GalaxyExe() != null;
				break;
			case "purple":
				flag2 = PurpleSource.LauncherExe() != null;
				break;
			case "amazon":
				flag2 = g.Id.StartsWith("amazon:amzn1.", StringComparison.Ordinal) && AmazonSource.ClientInstalled();
				break;
			default:
				flag2 = false;
				break;
			}
			flag = flag2;
		}
		return flag;
	}

	public static bool OwnLauncherOnly(GameState s)
	{
		if (s.Game.Store == Store.Manual)
		{
			return s.AntiCheat != AntiCheat.None;
		}
		return false;
	}

	public static ProcessStartInfo? Command(Game g, string? epicManifests = null, string? galaxyExe = null, string? purpleLauncher = null, string? epicLauncher = null)
	{
		string text = g.Id.Substring(g.Id.IndexOf(':') + 1);
		string text2;
		switch (Prefix(g))
		{
		case "steam":
		{
			uint? num = SteamSource.AppId(g.Id);
			if (num.HasValue)
			{
				uint valueOrDefault = num.GetValueOrDefault();
				text2 = $"steam://rungameid/{valueOrDefault}";
				break;
			}
			goto default;
		}
		case "epic":
			text2 = EpicUri(text, epicManifests ?? EpicSource.DefaultManifestsDir, epicLauncher ?? ((epicManifests == null) ? EpicSource.DefaultLauncherDir : null));
			break;
		case "amazon":
			text2 = ((text.StartsWith("amzn1.", StringComparison.Ordinal) && Regex.IsMatch(text, "^[\\w.-]+$") && AmazonSource.ClientInstalled()) ? ("amazon-games://play/" + text) : null);
			break;
		case "xbox":
		{
			string text3 = XboxSource.AppUserModelId(g);
			text2 = ((text3 != null) ? ("shell:AppsFolder\\" + text3) : null);
			break;
		}
		case "ubisoft":
		{
			if (uint.TryParse(text, out var _))
			{
				text2 = "uplay://launch/" + text + "/0";
				break;
			}
			goto default;
		}
		default:
			text2 = null;
			break;
		}
		string text4 = text2;
		if (text4 != null)
		{
			return new ProcessStartInfo(text4)
			{
				UseShellExecute = true
			};
		}
		if (g.Store == Store.Manual)
		{
			return new ProcessStartInfo(g.ExePath)
			{
				UseShellExecute = true,
				WorkingDirectory = Path.GetDirectoryName(g.ExePath)
			};
		}
		if (Prefix(g) == "gog" && ulong.TryParse(text, out var _))
		{
			string text5 = galaxyExe ?? GalaxyExe();
			if (text5 != null)
			{
				return new ProcessStartInfo(text5, $"/command=runGame /gameId={text} /path=\"{g.InstallDir.TrimEnd('\\')}\"");
			}
		}
		if (Prefix(g) == "purple" && Regex.IsMatch(text, "^[\\w.-]+$"))
		{
			string text6 = purpleLauncher ?? PurpleSource.LauncherExe();
			if (text6 != null)
			{
				return new ProcessStartInfo(text6, "--game-id " + text)
				{
					WorkingDirectory = Path.GetDirectoryName(text6)
				};
			}
		}
		return null;
	}

	private static string Prefix(Game g)
	{
		return g.Id.Substring(0, Math.Max(0, g.Id.IndexOf(':')));
	}

	private static string? EpicUri(string appName, string manifests, string? launcher)
	{
		try
		{
			EpicSource.Install install = EpicSource.Installs(launcher).FirstOrDefault((EpicSource.Install i) => i.App == appName && i.Namespace != "" && i.ItemId != "");
			if ((object)install != null)
			{
				return Uri(install.Namespace, install.ItemId, appName);
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
		if (!Directory.Exists(manifests))
		{
			return null;
		}
		foreach (string item in Directory.EnumerateFiles(manifests, "*.item"))
		{
			try
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(item));
				JsonElement r = jsonDocument.RootElement;
				if (S("AppName") != appName)
				{
					continue;
				}
				string text = S("CatalogNamespace");
				if (text != null && text.Length > 0)
				{
					string text2 = S("CatalogItemId");
					if (text2 != null && text2.Length > 0)
					{
						return Uri(text, text2, appName);
					}
				}
				string? S(string k)
				{
					if (!r.TryGetProperty(k, out var value) || value.ValueKind != JsonValueKind.String)
					{
						return null;
					}
					return value.GetString();
				}
			}
			catch (Exception ex2) when ((ex2 is JsonException || ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
			{
			}
		}
		return null;
		static string Uri(string ns, string catalog, string app)
		{
			return $"com.epicgames.launcher://apps/{System.Uri.EscapeDataString(ns)}%3A{System.Uri.EscapeDataString(catalog)}%3A{System.Uri.EscapeDataString(app)}?action=launch&silent=true";
		}
	}

	private static string? GalaxyExe()
	{
		if (!(Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\GOG.com\\GalaxyClient\\paths", "client", null) is string path))
		{
			return null;
		}
		string text = Path.Combine(path, (Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\GOG.com\\GalaxyClient", "clientExecutable", null) as string) ?? "GalaxyClient.exe");
		if (!File.Exists(text))
		{
			return null;
		}
		return text;
	}
}
