using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SCSKiller.Core.App;

public static class Elevated
{
	public sealed record Result(bool Ok, string Message);

	public const string YesArg = "--yes";

	public const string ResultArg = "--result";

	public const string ForUserArg = "--for-user";

	private const int ErrorCancelled = 1223;

	public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

	public static string CurrentUserSid => WindowsIdentity.GetCurrent().User.Value;

	public static Result? Run(params string[] args)
	{
		string? fileName = CliExe() ?? throw new FileNotFoundException("the command-line tool (cli\\scskiller.exe) is not next to the app");
		string text = Path.Combine(Path.GetTempPath(), $"scskiller-elevated-{Guid.NewGuid():N}.json");
		int num = 0;
		string[] array = new string[3 + args.Length];
		ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(args);
		readOnlySpan.CopyTo(new Span<string>(array).Slice(num, readOnlySpan.Length));
		num += readOnlySpan.Length;
		array[num] = "--yes";
		num++;
		array[num] = "--result";
		num++;
		array[num] = text;
		ProcessStartInfo startInfo = new ProcessStartInfo(fileName, CommandLine(new _003C_003Ez__ReadOnlyArray<string>(array)))
		{
			Verb = "runas",
			UseShellExecute = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		try
		{
			Process process;
			try
			{
				process = Process.Start(startInfo);
			}
			catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
			{
				return null;
			}
			using (process)
			{
				process.WaitForExit();
				return ReadResult(text, process.ExitCode);
			}
		}
		finally
		{
			File.Delete(text);
		}
	}

	public static Result ReadResult(string file, int exitCode)
	{
		try
		{
			if (File.Exists(file))
			{
				Result result = JsonSerializer.Deserialize<Result>(File.ReadAllText(file));
				if ((object)result != null)
				{
					return result;
				}
			}
		}
		catch (JsonException)
		{
		}
		return new Result(exitCode == 0, (exitCode == 0) ? "" : $"the command-line tool failed (exit code {exitCode})");
	}

	public static void WriteResult(string file, bool ok, string message)
	{
		File.WriteAllText(file, JsonSerializer.Serialize(new Result(ok, message)) + Environment.NewLine);
	}

	public static string? ReadAnswer(Stream input)
	{
		return new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true).ReadLine()?.Trim();
	}

	public static string CommandLine(IEnumerable<string> args)
	{
		return string.Join(' ', args.Select((string a) => (a.Length != 0 && !a.Any(char.IsWhiteSpace)) ? a : ("\"" + a + "\"")));
	}

	public static string? CliExe(string? baseDir = null)
	{
		if (baseDir == null)
		{
			baseDir = AppContext.BaseDirectory;
		}
		string text = Path.Combine(baseDir, "cli", "scskiller.exe");
		if (File.Exists(text))
		{
			return text;
		}
		for (DirectoryInfo directoryInfo = new DirectoryInfo(baseDir); directoryInfo != null; directoryInfo = directoryInfo.Parent)
		{
			string path = Path.Combine(directoryInfo.FullName, "src", "SCSKiller.Cli", "bin");
			if (Directory.Exists(path))
			{
				return (from f in new DirectoryInfo(path).EnumerateFiles("scskiller.exe", SearchOption.AllDirectories)
					orderby f.LastWriteTimeUtc descending
					select f).FirstOrDefault()?.FullName;
			}
		}
		return null;
	}
}
