namespace SCSKiller.Core;

public sealed record BeforeAfterInfo(BeforeAfterStage Stage, int Minutes, string? Problem, double PlayedSeconds, bool ClearPending = false);
