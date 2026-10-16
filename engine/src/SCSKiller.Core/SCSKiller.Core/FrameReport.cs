using System;
using System.Collections.Generic;

namespace SCSKiller.Core;

public sealed record FrameReport(TimeSpan Duration, TimeSpan Startup, long Frames, double Low1PctFps, IReadOnlyList<Hitch> Hitches, IReadOnlyList<float> Peaks, long LaunchUnixMs = 0L, double PlaySeconds = 0.0, double AvgFps = 0.0, CreateStats? Creates = null, long DisplayedFrames = 0L, IReadOnlyList<double>? Displayed = null);
