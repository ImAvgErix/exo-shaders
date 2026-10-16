using System;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.App;

public sealed record DiagnosticFailure(string Stage, string Reason, string? Hr = null, string? Exception = null)
{
	private static readonly Regex ExitCode = new Regex("^scskiller_warm exited with code (-?\\d+)", RegexOptions.CultureInvariant);

	private static readonly Regex TypeName = new Regex("^[A-Za-z_][A-Za-z0-9_.]{0,127}$", RegexOptions.CultureInvariant);

	public static DiagnosticFailure FromWarm(string? error)
	{
		switch (error)
		{
		default:
		{
			Match match = ExitCode.Match(error);
			if (match != null && match.Success && long.TryParse(match.Groups[1].Value, out var result))
			{
				return new DiagnosticFailure("warm", "exit_code", ((uint)result).ToString("x8"));
			}
			goto case null;
		}
		case null:
			return new DiagnosticFailure("warm", "reported");
		case "scskiller_warm exited without a done event":
			return new DiagnosticFailure("warm", "no_done_event");
		case "the compile didn't stop cleanly (the driver may be stuck), so it was ended":
			return new DiagnosticFailure("warm", "stuck");
		}
	}

	public static DiagnosticFailure FromException(QueueStage stage, Exception e)
	{
		string stage2 = stage switch
		{
			QueueStage.Indexing => "index", 
			QueueStage.Planning => "plan", 
			QueueStage.Materializing => "materialize", 
			_ => "warm", 
		};
		string hr = ((uint)e.HResult).ToString("x8");
		string fullName = e.GetType().FullName;
		return new DiagnosticFailure(stage2, "exception", hr, (fullName != null && TypeName.IsMatch(fullName)) ? fullName : null);
	}
}
