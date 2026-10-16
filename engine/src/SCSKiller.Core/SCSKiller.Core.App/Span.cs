namespace SCSKiller.Core.App;

public sealed record Span(string Text, SpanKind Kind = SpanKind.Plain, string? Url = null);
