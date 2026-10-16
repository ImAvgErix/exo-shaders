using System;
using System.IO;
using Microsoft.Win32;

namespace SCSKiller.Core.App;

public static class WindowsStartup
{
	public const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	public const string ValueName = "SCSKiller";

	public const string TrayArg = "--tray";

	public static string Command(string appExe)
	{
		return "\"" + appExe + "\" --tray";
	}

	public static bool Set(bool on, string appExe, Func<string?> read, Action<string?> write)
	{
		string text = read();
		string text2 = (on ? Command(appExe) : null);
		if (text == text2 || (!on && !PointsAt(text, appExe)))
		{
			return false;
		}
		write(text2);
		return true;
	}

	public static bool Reconcile(bool on, string appExe, bool installed, Func<string?> read, Action<string?> write, Func<string, bool> exists)
	{
		string text = read();
		string text2 = (on ? Command(appExe) : null);
		int num;
		if (on & installed)
		{
			if (text != null)
			{
				string text3 = Target(text);
				num = ((text3 != null && !exists(text3)) ? 1 : 0);
			}
			else
			{
				num = 1;
			}
		}
		else
		{
			num = 0;
		}
		bool flag = (byte)num != 0;
		if (text == text2 || (!flag && !PointsAt(text, appExe)))
		{
			return false;
		}
		write(text2);
		return true;
	}

	public static bool PointsAt(string? command, string exe)
	{
		string text = Target(command);
		if (text != null)
		{
			string text2 = Normalize(exe);
			if (text2 != null)
			{
				return string.Equals(text, text2, StringComparison.OrdinalIgnoreCase);
			}
		}
		return false;
	}

	private static string? Target(string? command)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return null;
		}
		string text = command.Trim();
		int num = text.IndexOf('"', 1);
		int num2 = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
		string path;
		if (text[0] != '"')
		{
			path = ((num2 >= 0) ? text.Substring(0, num2 + 4) : text.Split(' ')[0]);
		}
		else
		{
			path = ((num > 0) ? text.Substring(1, num - 1) : text.Substring(1));
		}
		return Normalize(path);
	}

	private static string? Normalize(string path)
	{
		path = path.Trim();
		if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
		{
			path = "\\\\" + path.Substring(8);
		}
		else if (path.StartsWith("\\\\?\\"))
		{
			path = path.Substring(4);
		}
		if (!Path.IsPathFullyQualified(path))
		{
			return null;
		}
		try
		{
			return Path.GetFullPath(path);
		}
		catch (Exception ex) when ((ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) ? true : false)
		{
			return null;
		}
	}

	public static string? ReadRegistry()
	{
		using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
		object obj = registryKey?.GetValue("SCSKiller");
		if (obj != null)
		{
			if (obj is string result)
			{
				return result;
			}
			return obj.ToString() ?? "";
		}
		return null;
	}

	public static void WriteRegistry(string? value)
	{
		using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
		if (value == null)
		{
			registryKey.DeleteValue("SCSKiller", throwOnMissingValue: false);
		}
		else
		{
			registryKey.SetValue("SCSKiller", value);
		}
	}
}
