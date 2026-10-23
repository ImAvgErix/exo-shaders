namespace SCSKiller.Core.App;

/// <summary>One "Compile" of a set of games: <see cref="Stop"/> takes out the ones still waiting and stops the queue, and
/// once it has, nothing more of this run is queued or started (a Stop between two enqueues can't leave the rest queued).</summary>
public sealed class QueueRun(ScsKiller k)
{
    readonly object _gate = new();
    bool _stopped;

    public bool Stopped
    {
        get { lock (_gate) return _stopped; }
    }

    /// <summary>Queues the games and starts the queue (or leaves them for idle); false when stopped meanwhile.</summary>
    public bool Start(IEnumerable<string> gameIds, bool whenIdle)
    {
        foreach (var id in gameIds)
            lock (_gate)
            {
                if (_stopped) return false;
                if (whenIdle) k.EnqueueWhenIdle(id);
                else k.Enqueue(id);
            }
        lock (_gate)
        {
            if (_stopped) return false;
            if (!whenIdle) k.StartQueue();
            return true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            foreach (var q in k.Queue.Where(q => q.Stage == QueueStage.Waiting)) k.Remove(q.GameId);
            k.StopQueue();
        }
    }
}
