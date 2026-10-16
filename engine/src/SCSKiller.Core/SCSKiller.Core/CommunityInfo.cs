using System;

namespace SCSKiller.Core;

public sealed record CommunityInfo(int Psos, DateTimeOffset DownloadedAt, bool WithLocalRecording, bool OtherVersion = false);
