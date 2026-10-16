namespace SCSKiller.Core.App;

public sealed record NewsItem(string Id, string Date, string Title, string Body, string? Link = null, string? Game = null, string? Expires = null, string[]? Channels = null, string[]? Ent = null, string? MinVersion = null, string? MaxVersion = null, string[]? Gpu = null);
