using System;
using System.Text.Json.Serialization;

namespace SCSKiller.Core.App;

public sealed record CommunityDownload(string Object, string ContentHash, int Psos, DateTimeOffset DownloadedAt, OfficialVisibility? Visibility = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Full = false, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? MatchesNothingIn = null);
