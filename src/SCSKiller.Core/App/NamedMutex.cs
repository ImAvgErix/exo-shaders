namespace SCSKiller.Core.App;

/// <summary>Named mutexes whose existence, not ownership, is the signal: one exists while any process has it open.</summary>
internal static class NamedMutex
{
    internal static Mutex Open(string name) => new(false, name);

    internal static bool Held(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var m)) return false;
        m.Dispose();
        return true;
    }

    /// <summary>Why the named objects can't be trusted here; null on Windows, where the kernel keeps them.</summary>
    internal static string? Unsafe(string? root = null) => null;
}
