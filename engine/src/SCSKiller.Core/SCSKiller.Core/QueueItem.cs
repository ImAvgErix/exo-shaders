namespace SCSKiller.Core;

public sealed record QueueItem(string GameId, QueueStage Stage, WarmProgress? Progress, string? Error, string? Note = null, bool PlanCheck = false);
