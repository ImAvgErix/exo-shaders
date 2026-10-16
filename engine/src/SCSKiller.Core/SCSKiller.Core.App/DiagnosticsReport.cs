using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.App;

public sealed record DiagnosticsReport(string ContentHash, string? Engine, string Vendor, string? GpuModel, string? DriverVersion, string AppVersion, string Outcome, DiagnosticRejection[] Rejections, DiagnosticKey[] Keys, string[] Crashed, string[] Exceptions, DiagnosticFailure? Failure)
{
	public const int MaxKeys = 500;

	public const int MaxCodes = 64;

	public const int MaxCrashed = 100;

	public const int MaxExceptions = 16;

	private static readonly Regex Rejected = new Regex("warm: rejected '([GCSRA])' hr=0x([0-9a-f]{8}) key ([0-9a-f]{40})", RegexOptions.CultureInvariant);

	private static readonly Regex Tally = new Regex("warm: failed x(\\d+): (?:replay of|plan item on) '([GCSRA])' \\([^)]*\\) hr=0x([0-9a-f]{8})", RegexOptions.CultureInvariant);

	private static readonly Regex Exception = new Regex("warm: exception 0x([0-9a-f]{8})", RegexOptions.CultureInvariant);

	public static DiagnosticsReport? Build(IEnumerable<string> logLines, DiagnosticFailure? failure, IEnumerable<string>? crashed, string contentHash, string? engine, string vendor, string? gpuModel, string? driverVersion, string appVersion)
	{
		Dictionary<(string, string), long> dictionary = new Dictionary<(string, string), long>();
		Dictionary<string, DiagnosticKey> dictionary2 = new Dictionary<string, DiagnosticKey>();
		SortedSet<string> sortedSet = new SortedSet<string>(StringComparer.Ordinal);
		foreach (string logLine in logLines)
		{
			Match match = Tally.Match(logLine);
			if (match != null && match.Success && long.TryParse(match.Groups[1].Value, out var result))
			{
				dictionary[(match.Groups[2].Value, match.Groups[3].Value)] = dictionary.GetValueOrDefault((match.Groups[2].Value, match.Groups[3].Value)) + result;
				continue;
			}
			Match match2 = Rejected.Match(logLine);
			if (match2 != null && match2.Success && dictionary2.Count < 500)
			{
				dictionary2.TryAdd(match2.Groups[3].Value, new DiagnosticKey(match2.Groups[3].Value, match2.Groups[1].Value, match2.Groups[2].Value));
				continue;
			}
			Match match3 = Exception.Match(logLine);
			if (match3 != null && match3.Success && sortedSet.Count < 16)
			{
				sortedSet.Add(match3.Groups[1].Value);
			}
		}
		if (dictionary.Count == 0)
		{
			foreach (IGrouping<(string, string), DiagnosticKey> item in from k in dictionary2.Values
				group k by (Kind: k.Kind, Hr: k.Hr))
			{
				dictionary[item.Key] = item.Count();
			}
		}
		string[] array = (from k in crashed ?? Array.Empty<string>()
			select k.ToLowerInvariant() into k
			where k.Length == 40 && k.All(char.IsAsciiHexDigit)
			select k).Distinct().Order(StringComparer.Ordinal).Take(100)
			.ToArray();
		if (failure == null && dictionary.Count == 0 && dictionary2.Count == 0 && array.Length == 0 && sortedSet.Count == 0)
		{
			return null;
		}
		return new DiagnosticsReport(contentHash, engine, vendor, gpuModel, driverVersion, appVersion, (failure != null) ? "failed" : "completed", dictionary.OrderByDescending((KeyValuePair<(string Kind, string Hr), long> c) => c.Value).ThenBy((KeyValuePair<(string Kind, string Hr), long> c) => c.Key.Kind, StringComparer.Ordinal).ThenBy((KeyValuePair<(string Kind, string Hr), long> c) => c.Key.Hr, StringComparer.Ordinal)
			.Take(64)
			.Select((KeyValuePair<(string Kind, string Hr), long> c) => new DiagnosticRejection(c.Key.Kind, c.Key.Hr, Math.Min(c.Value, 10000000L)))
			.ToArray(), dictionary2.Values.ToArray(), array, sortedSet.ToArray(), failure);
	}
}
