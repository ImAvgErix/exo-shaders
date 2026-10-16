namespace SCSKiller.Core;

public enum QueueStage
{
	Waiting,
	Indexing,
	Planning,
	Materializing,
	Warming,
	Paused,
	Done,
	Failed,
	Stopped
}
