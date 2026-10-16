using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public static class ProblemReport
{
	public sealed class Masker
	{
		private readonly (Regex? Profile, Regex? User) pc;

		private readonly Regex? machine;

		private static readonly Regex Email = new Regex("(?:\"[^\"\\r\\n]*\"|[^\\s\"'<>()\\[\\],;]+)@[^\\s\"'<>(),;]+", RegexOptions.CultureInvariant);

		private static readonly Regex Token = new Regex("\\bv\\d\\.(?:local|public)\\.[A-Za-z0-9_\\-.]+|\\bBearer\\s+\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		public static Masker ThisPc { get; } = new Masker(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

		public Masker(string? profile, string? user, string? machine)
		{
			pc = AppLog.Maskers(new _003C_003Ez__ReadOnlySingleElementList<string>(profile), user);
			object obj;
			if (machine != null && machine.Length > 0)
			{
				obj = ((machine.Length >= 4) ? new Regex(Regex.Escape(machine), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : new Regex("(?<![\\p{L}\\p{N}_])" + Regex.Escape(machine) + "(?![\\p{L}\\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
			}
			else
			{
				obj = null;
			}
			this.machine = (Regex?)obj;
		}

		public string Mask(string s)
		{
			s = AppLog.Mask(Token.Replace(Email.Replace(s, "<email>"), "<token>"), pc.Profile, null);
			if (machine != null)
			{
				s = machine.Replace(s, "<pc>");
			}
			return AppLog.Mask(s, null, pc.User);
		}
	}

	public const int MaxLogLines = 50;

	public const int MaxLineChars = 300;

	public const int MaxMessageChars = 4000;

	private static readonly Regex Auth = new Regex("token|patreon|account|device[ _]?id|refresh|secret|password|sign(?:ed)?[ -]?in", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	private static readonly Regex Id = new Regex("^[0-9a-f]{8}$", RegexOptions.CultureInvariant);

	private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(() => new HttpClient(RouteFailover.Default, disposeHandler: false)
	{
		Timeout = TimeSpan.FromSeconds(30L)
	});

	public static string Build(string appVersion, string game, string store, string? vendor, string? gpu, string? driver, string windows, IEnumerable<(string Label, string Value)> page, IEnumerable<string>? log, Masker mask)
	{
		StringBuilder stringBuilder = new StringBuilder();
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder2);
		handler.AppendLiteral("SCSKiller ");
		handler.AppendFormatted(appVersion);
		stringBuilder3.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder4 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(9, 2, stringBuilder2);
		handler.AppendLiteral("Game: ");
		handler.AppendFormatted(game);
		handler.AppendLiteral(" (");
		handler.AppendFormatted(store);
		handler.AppendLiteral(")");
		stringBuilder4.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder5 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(17, 3, stringBuilder2);
		handler.AppendLiteral("GPU: ");
		handler.AppendFormatted(gpu ?? "unknown");
		handler.AppendLiteral(" (");
		handler.AppendFormatted(vendor ?? "unknown vendor");
		handler.AppendLiteral("), driver ");
		handler.AppendFormatted(driver ?? "unknown");
		stringBuilder5.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder6 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder2);
		handler.AppendLiteral("Windows: ");
		handler.AppendFormatted(windows);
		stringBuilder6.AppendLine(ref handler);
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("On the game's page:");
		foreach (var (value, text) in page)
		{
			if (!string.IsNullOrWhiteSpace(text))
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder7 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(2, 2, stringBuilder2);
				handler.AppendFormatted(value);
				handler.AppendLiteral(": ");
				handler.AppendFormatted(text.ReplaceLineEndings(" "));
				stringBuilder7.AppendLine(ref handler);
			}
		}
		if (log != null)
		{
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("app.log, the latest lines about this game:");
			bool flag = false;
			foreach (string item in log)
			{
				string text2 = mask.Mask(item);
				stringBuilder.AppendLine((text2.Length > 300) ? (text2.Substring(0, 300) + "…") : text2);
				flag = true;
			}
			if (!flag)
			{
				stringBuilder.AppendLine("(none)");
			}
		}
		return mask.Mask(stringBuilder.ToString().TrimEnd());
	}

	public static IReadOnlyList<string> LogLines(string dataDir, IEnumerable<string> about)
	{
		string[] source = about.Where((string t) => t != null && t.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		Queue<string> queue = new Queue<string>();
		string[] array = new string[2] { "app.log.1", "app.log" };
		foreach (string path in array)
		{
			foreach (string line in Read(Path.Combine(dataDir, path)))
			{
				if (source.Any((string t) => line.Contains(t, StringComparison.OrdinalIgnoreCase)) && !Auth.IsMatch(line))
				{
					queue.Enqueue(line);
					if (queue.Count > 50)
					{
						queue.Dequeue();
					}
				}
			}
		}
		int num = 0;
		array = new string[queue.Count];
		foreach (string item in queue)
		{
			array[num] = item;
			num++;
		}
		return new _003C_003Ez__ReadOnlyArray<string>(array);
		static IEnumerable<string> Read(string path2)
		{
			string text;
			try
			{
				using FileStream stream = new FileStream(path2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				using StreamReader streamReader = new StreamReader(stream);
				text = streamReader.ReadToEnd();
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				return Array.Empty<string>();
			}
			return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		}
	}

	public static byte[] Body(string message, string report)
	{
		return JsonSerializer.SerializeToUtf8Bytes(new { message, report }, Json);
	}

	public static async Task<(string? Id, string? Problem)> SendAsync(string message, string report, CancellationToken ct = default(CancellationToken))
	{
		_ = 1;
		try
		{
			using ByteArrayContent content = new ByteArrayContent(Body(message, report));
			content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			using HttpResponseMessage r = await Http.Value.PostAsync(new Uri(RouteFailover.Default.Primary, "v1/problems"), content, ct);
			bool flag = r.StatusCode == HttpStatusCode.OK;
			JsonElement id = default;
			if (flag)
			{
				flag = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.TryGetProperty("id", out id);
			}
			if (flag)
			{
				string text = id.GetString();
				if (text != null && Id.IsMatch(text))
				{
					return (Id: text, Problem: null);
				}
			}
			string item;
			switch (r.StatusCode)
			{
			case HttpStatusCode.NotFound:
			case HttpStatusCode.NotImplemented:
				item = "Problem reports aren't open yet.";
				break;
			case HttpStatusCode.RequestEntityTooLarge:
				item = "The report is too long. Leave the log lines out and try again.";
				break;
			case HttpStatusCode.TooManyRequests:
				item = "Too many reports today. Try again tomorrow.";
				break;
			case HttpStatusCode.BadRequest:
				item = "The server didn't take this report.";
				break;
			default:
				item = "Not sent. Try again later.";
				break;
			}
			return (Id: null, Problem: item);
		}
		catch (Exception) when (!ct.IsCancellationRequested)
		{
			return (Id: null, Problem: "Not sent. Try again later.");
		}
	}
}
