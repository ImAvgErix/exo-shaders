using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public sealed class AppLog : IProgress<string>
{
	public const string FileName = "app.log";

	public const long MaxBytes = 1048576L;

	private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(10000)
	{
		SingleReader = true,
		FullMode = BoundedChannelFullMode.DropWrite
	});

	private readonly Task _writer;

	private readonly Regex? _profile;

	private readonly Regex? _user;

	private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

	private const string Word = "[\\p{L}\\p{N}_]";

	public AppLog(string dataDir, string file = "app.log", long maxBytes = 1048576L, string? profile = null, string? user = null)
	{
		AppLog appLog = this;
		if (profile == null)
		{
			profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		}
		(Regex, Regex) tuple = Maskers(new _003C_003Ez__ReadOnlyArray<string>(new string[2]
		{
			profile,
			ShortPath(profile)
		}), user ?? Environment.UserName);
		_profile = tuple.Item1;
		_user = tuple.Item2;
		_writer = Task.Run(async () =>
		{
			await foreach (string item in appLog._lines.Reader.ReadAllAsync())
			{
				ScsKiller.AppendLog(dataDir, file, item, maxBytes);
			}
		});
	}

	public void Report(string value)
	{
		_lines.Writer.TryWrite(Mask(value, _profile, _user));
	}

	public Task FlushAsync(TimeSpan wait)
	{
		_lines.Writer.TryComplete();
		return _writer.WaitAsync(wait).ContinueWith((Task _) =>
		{
		}, TaskScheduler.Default);
	}

	internal static (Regex? Profile, Regex? User) Maskers(IEnumerable<string?> profiles, string? user)
	{
		List<string> list = (from p in profiles
			select (p ?? "").Split(new char[2] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries) into parts
			where parts.Length >= 2
			select string.Join("[\\\\/]", parts.Select(Regex.Escape))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		Regex item = ((list.Count == 0) ? null : new Regex("(?:[\\\\/]{2}\\?[\\\\/])?(?:" + string.Join("|", list) + ")(?![\\p{L}\\p{N}_\\-]|\\.[\\p{L}\\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
		Regex item2 = ((user != null && user.Length >= 3) ? new Regex($"(?<!{"[\\p{L}\\p{N}_]"}){Regex.Escape(user)}(?!{"[\\p{L}\\p{N}_]"})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : null);
		return (Profile: item, User: item2);
	}

	internal static string Mask(string line, Regex? profile, Regex? user)
	{
		if (profile != null)
		{
			line = profile.Replace(line, "~");
		}
		return user?.Replace(line, "<user>") ?? line;
	}

	private static string? ShortPath(string path)
	{
		StringBuilder stringBuilder = new StringBuilder(1024);
		try
		{
			int shortPathNameW = GetShortPathNameW(path, stringBuilder, stringBuilder.Capacity);
			return (shortPathNameW > 0 && shortPathNameW < 1024) ? stringBuilder.ToString() : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetShortPathNameW(string longPath, StringBuilder shortPath, int size);
}
