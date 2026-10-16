using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace SCSKiller.Core.App;

public static class ScheduledTask
{
	public const string Name = "SCSKiller\\RewarmStale";

	public const string DriverUpdatedArg = "--driver-updated";

	public static bool Registered => Schtasks(false, "/Query", "/TN", "SCSKiller\\RewarmStale");

	public static string Xml(string cliExe, string user)
	{
		XNamespace xNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
		XDocument xDocument = new XDocument(new XDeclaration("1.0", "UTF-16", null), new XElement(xNamespace + "Task", new XAttribute("version", "1.2"), new XElement(xNamespace + "RegistrationInfo", new XElement(xNamespace + "Description", "SCSKiller: re-warm game shader caches after a GPU driver update")), new XElement(xNamespace + "Triggers", new XElement(xNamespace + "LogonTrigger", new XElement(xNamespace + "Enabled", "true"), new XElement(xNamespace + "UserId", user), new XElement(xNamespace + "Delay", "PT2M")), new XElement(xNamespace + "IdleTrigger", new XElement(xNamespace + "Enabled", "true"))), new XElement(xNamespace + "Principals", new XElement(xNamespace + "Principal", new XAttribute("id", "Author"), new XElement(xNamespace + "UserId", user), new XElement(xNamespace + "LogonType", "InteractiveToken"), new XElement(xNamespace + "RunLevel", "LeastPrivilege"))), new XElement(xNamespace + "Settings", new XElement(xNamespace + "MultipleInstancesPolicy", "IgnoreNew"), new XElement(xNamespace + "DisallowStartIfOnBatteries", "true"), new XElement(xNamespace + "StopIfGoingOnBatteries", "true"), new XElement(xNamespace + "IdleSettings", new XElement(xNamespace + "Duration", "PT10M"), new XElement(xNamespace + "WaitTimeout", "PT1H"), new XElement(xNamespace + "StopOnIdleEnd", "false"), new XElement(xNamespace + "RestartOnIdle", "false")), new XElement(xNamespace + "ExecutionTimeLimit", "PT0S"), new XElement(xNamespace + "Priority", "7")), new XElement(xNamespace + "Actions", new XAttribute("Context", "Author"), new XElement(xNamespace + "Exec", new XElement(xNamespace + "Command", cliExe), new XElement(xNamespace + "Arguments", "rewarm-stale --if-driver-changed")))));
		return xDocument.Declaration?.ToString() + Environment.NewLine + xDocument;
	}

	public static void Register(string cliExe)
	{
		string text = Path.Combine(Path.GetTempPath(), "scskiller_task.xml");
		File.WriteAllText(text, Xml(cliExe, Environment.UserDomainName + "\\" + Environment.UserName), Encoding.Unicode);
		try
		{
			Schtasks(true, "/Create", "/TN", "SCSKiller\\RewarmStale", "/XML", text, "/F");
		}
		finally
		{
			File.Delete(text);
		}
	}

	public static void Unregister()
	{
		if (Registered)
		{
			Schtasks(true, "/Delete", "/TN", "SCSKiller\\RewarmStale", "/F");
		}
	}

	private static bool Schtasks(bool throwOnError, params string[] args)
	{
		ProcessStartInfo processStartInfo = new ProcessStartInfo("schtasks.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true
		};
		foreach (string item in args)
		{
			processStartInfo.ArgumentList.Add(item);
		}
		using Process process = Process.Start(processStartInfo);
		process.StandardOutput.ReadToEndAsync();
		string text = process.StandardError.ReadToEnd();
		process.WaitForExit();
		if ((process.ExitCode != 0) & throwOnError)
		{
			throw new InvalidOperationException($"schtasks failed ({process.ExitCode}): {text.Trim()}");
		}
		return process.ExitCode == 0;
	}

	public static string? TaskExe(string? baseDir = null)
	{
		if (baseDir == null)
		{
			baseDir = AppContext.BaseDirectory;
		}
		return new string[4] { "cli\\scskillerw.exe", "cli\\scskiller.exe", "scskillerw.exe", "scskiller.exe" }.Select((string f) => Path.Combine(baseDir, f)).FirstOrDefault(File.Exists);
	}

	public static string? AppExe(string? baseDir = null)
	{
		string fullPath = Path.GetFullPath(Path.Combine(baseDir ?? AppContext.BaseDirectory, "..", "SCSKiller.exe"));
		if (!File.Exists(fullPath))
		{
			return null;
		}
		return fullPath;
	}
}
