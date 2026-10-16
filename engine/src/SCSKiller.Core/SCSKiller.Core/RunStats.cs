namespace SCSKiller.Core;

public sealed record RunStats(double PlaySeconds, int ShaderStutters, double FrozenSeconds, double WorstStutterMs, double? ReadyShare, long PlayCompiles, double Low1PctFps, double AvgFps, int OtherHitches, int Hitches = 0, double HitchSeconds = 0.0, double WorstHitchMs = 0.0);
