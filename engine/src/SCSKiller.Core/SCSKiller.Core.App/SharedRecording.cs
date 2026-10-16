using System;

namespace SCSKiller.Core.App;

public sealed record SharedRecording(string Stamp, DateTimeOffset? At = null, string? UploadId = null, int Psos = 0, int NewPsos = 0, string[]? Sent = null, string? Meta = null);
