namespace SCSKiller.Core.Warming;

/// <summary>What a host made of a process's end (<see cref="IWarmHost.AfterProcess"/>): the cache keys it saw added
/// (<see cref="WarmResult.CacheKeysAdded"/>); <paramref name="Segment"/>: it ended the process at a segment's end, a new one
/// goes on from where it stopped; <paramref name="Halt"/>: why the run can't go on, null = it can.</summary>
sealed record ProcessEnd(IReadOnlyCollection<string> Added, bool Segment = false, string? Halt = null)
{
    public static readonly ProcessEnd None = new([]);
}
