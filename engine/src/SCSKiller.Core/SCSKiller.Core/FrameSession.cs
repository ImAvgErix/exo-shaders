using System;

namespace SCSKiller.Core;

public sealed record FrameSession(long LaunchUnixMs, TimeSpan Duration, double Low1PctFps, int Stutters);
