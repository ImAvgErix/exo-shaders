namespace SCSKiller.Core.App;

/// <summary>A before and after report as text: its subtitle, the headline tiles, the rows and the notes under them.</summary>
public sealed record ReportText(string Subtitle, IReadOnlyList<(string Label, string Value)> Tiles, IReadOnlyList<ReportRow> Rows, IReadOnlyList<string> Notes);
