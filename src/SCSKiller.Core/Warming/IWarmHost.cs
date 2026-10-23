namespace SCSKiller.Core.Warming;

/// <summary>Where a warm's scskiller_warm processes run (<see cref="WarmHost.For"/>): how they are started and stopped, and
/// how paths are named on their side. Disposed when the run ends.</summary>
interface IWarmHost : IDisposable
{
    /// <summary>The arguments that let every process of the run see <see cref="Stop"/> (--stop-event).</summary>
    IReadOnlyList<string> StopArgs { get; }
    /// <summary>A path of this PC as the processes see it.</summary>
    string WarmPath(string host);
    /// <summary>A path the processes printed, as this PC sees it.</summary>
    string HostPath(string warm);
    /// <summary>The arguments that put a process on <paramref name="gpu"/>.</summary>
    IReadOnlyList<string> AdapterArgs(GpuInfo gpu);
    /// <summary>Asks every process of the run to stop.</summary>
    void Stop();
    /// <summary>Starts <paramref name="exe"/>; each line of its stderr goes to <paramref name="errorLine"/>.</summary>
    IWarmProcess Start(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, Action<string> errorLine);
    /// <summary>After each process exited: what the host made of its end; <paramref name="note"/> says what it does meanwhile.</summary>
    Task<ProcessEnd> AfterProcess(Action<string> note) => Task.FromResult(ProcessEnd.None);
}
