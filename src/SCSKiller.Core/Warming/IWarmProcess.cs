namespace SCSKiller.Core.Warming;

/// <summary>One scskiller_warm process an <see cref="IWarmHost"/> started.</summary>
interface IWarmProcess
{
    /// <summary>Its process id on this PC (the memory guard's, <see cref="ProcessTree.Cap"/>).</summary>
    int Id { get; }
    /// <summary>Its stdout (the protocol's lines).</summary>
    TextReader Output { get; }
    /// <summary>Its exit code once it exited; null when it died without one.</summary>
    Task<int?> Exited { get; }
    bool HasExited { get; }
    /// <summary>Suspends or resumes it and its descendants.</summary>
    void Suspend(bool suspend);
    /// <summary>Ends it and its descendants; nothing when it already exited.</summary>
    void Kill();
}
