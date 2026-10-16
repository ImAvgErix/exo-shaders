using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace SCSKiller.Core.App;

public static class Busy
{
	internal static string Scope = ScopeFrom(Environment.GetEnvironmentVariable("SCSKILLER_TEST_SCOPE"));

	public static readonly TimeSpan ApplyingFor = TimeSpan.FromMinutes(2L);

	public static string Name => "Local\\SCSKiller.Busy" + Scope;

	internal static string ScopeFrom(string? value)
	{
		if (value == null || value.Length != 38 || !value.StartsWith(".test-", StringComparison.Ordinal) || !Guid.TryParseExact(value.Substring(6), "N", out var _))
		{
			return "";
		}
		return value;
	}

	public static IDisposable Hold(string? name = null)
	{
		return new Mutex(initiallyOwned: false, name ?? Name);
	}

	public static IDisposable? TryHold(string dataDir, DateTimeOffset now, string? name = null)
	{
		IDisposable disposable = Hold(name);
		if (!Applying(dataDir, now))
		{
			return disposable;
		}
		disposable.Dispose();
		return null;
	}

	public static bool IsHeld(string? name = null)
	{
		if (!Mutex.TryOpenExisting(name ?? Name, out Mutex result))
		{
			return false;
		}
		result.Dispose();
		return true;
	}

	private static string Marker(string dataDir)
	{
		return Path.Combine(dataDir, "applying");
	}

	public static void MarkApplying(string dataDir, DateTimeOffset now)
	{
		Directory.CreateDirectory(dataDir);
		File.WriteAllText(Marker(dataDir), now.ToString("O", CultureInfo.InvariantCulture));
	}

	public static void ClearApplying(string dataDir)
	{
		File.Delete(Marker(dataDir));
	}

	public static bool Marked(string dataDir)
	{
		return File.Exists(Marker(dataDir));
	}

	public static bool Applying(string dataDir, DateTimeOffset now)
	{
		try
		{
			using FileStream stream = new FileStream(Marker(dataDir), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			DateTimeOffset result;
			return DateTimeOffset.TryParse(new StreamReader(stream).ReadToEnd(), CultureInfo.InvariantCulture, DateTimeStyles.None, out result) && now - result < ApplyingFor && result - now < ApplyingFor;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return false;
		}
	}
}
