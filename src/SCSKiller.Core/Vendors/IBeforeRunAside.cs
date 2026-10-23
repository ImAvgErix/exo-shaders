namespace SCSKiller.Core.Vendors;

/// <summary>Driver cache files set aside for a before run (<see cref="DxQuarantine"/>) and put back after it.</summary>
public interface IBeforeRunAside
{
    /// <summary>The game whose run the set-aside files wait for; null = none pending.</summary>
    string? PendingGame { get; }
    /// <summary>Where the files wait.</summary>
    string Folder { get; }
    /// <summary>The run's end reads the cache itself to learn what the game made.</summary>
    bool AfterRunReads { get; }
    /// <summary>Puts the files back, except <paramref name="old"/>'s (the game's old cache); returns those that couldn't be.</summary>
    List<string> Restore(string? old = null);
}
