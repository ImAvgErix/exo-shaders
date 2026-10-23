namespace SCSKiller.Core.App;

/// <summary>One line of a before and after report: a measure in both runs, and a note on it.</summary>
public sealed record ReportRow(string Label, string Before, string After, string? Note = null);
