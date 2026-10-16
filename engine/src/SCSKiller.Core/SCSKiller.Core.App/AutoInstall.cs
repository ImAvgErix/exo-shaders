using System.Collections.Generic;

namespace SCSKiller.Core.App;

public static class AutoInstall
{
	public const string Off = "Automatic install is off.";

	public const string Internal = "Updates on the internal channel install only through Restart to update.";

	public const string Compiling = "A compile is running.";

	public const string Offline = "A recording session's game or cleanup is running. The update installs once it has ended.";

	public const string Playing = "A game is running, or the games aren't known yet.";

	public static bool On(Settings s, string channel)
	{
		if (s.InstallUpdatesAutomatically)
		{
			return channel != "internal";
		}
		return false;
	}

	public static string? HeldBack(Settings s, string channel, bool compiling, bool offline, bool playing)
	{
		if (!(channel == "internal"))
		{
			if (s.InstallUpdatesAutomatically)
			{
				if (!compiling)
				{
					if (!offline)
					{
						if (!playing)
						{
							return null;
						}
						return "A game is running, or the games aren't known yet.";
					}
					return "A recording session's game or cleanup is running. The update installs once it has ended.";
				}
				return "A compile is running.";
			}
			return "Automatic install is off.";
		}
		return "Updates on the internal channel install only through Restart to update.";
	}

	public static bool OffersRestart(StagedUpdate? staged, string? chosen, AppVersion current, IReadOnlyCollection<string>? ent, bool backToStable = false)
	{
		if (staged != null && UpdateChannels.Installs(staged.Channel, chosen, current.Channel, ent))
		{
			AppVersion appVersion = AppVersion.Parse(staged.Version);
			if ((object)appVersion == null || appVersion.CompareTo(current) <= 0)
			{
				if (backToStable)
				{
					return staged.Channel == "stable";
				}
				return false;
			}
			return true;
		}
		return false;
	}

	public static string ReadyNote(string version, bool auto)
	{
		if (!auto)
		{
			return "SCSKiller " + version + " is ready: use Restart to update at the top.";
		}
		return "SCSKiller " + version + " is ready: it installs the next time SCSKiller starts or quits, or use Restart to update at the top.";
	}
}
