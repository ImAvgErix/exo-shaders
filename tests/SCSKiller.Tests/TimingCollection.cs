namespace SCSKiller.Tests;

/// <summary>Tests that assert on wall-clock reaction times run alone: the index/carve tests saturate the thread pool.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
