using System;
using System.Collections.Generic;

namespace SCSKiller.Core;

public sealed record SessionSnapshot(int V, long LaunchUnixMs, double DurationMs, double StartupMs, long Frames, double Low1PctFps, double PlaySeconds, double AvgFps, IReadOnlyList<Hitch> Hitches, IReadOnlyList<float> Peaks, CreateStats? Creates, string? DriverId, string? DriverVersion, string? Build, DateTimeOffset? WarmedAt, string? SwapChain, bool CleanExit = false, bool Overlay = false, bool Pinned = false, string? Tag = null, bool D3D11 = false, bool? FrameGen = false)
{
	public const int Version = 1;
}
