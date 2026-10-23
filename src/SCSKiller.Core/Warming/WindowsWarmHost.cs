using System.Diagnostics;

namespace SCSKiller.Core.Warming;

/// <summary>The warm's processes run here, in <see cref="WarmJob.Current"/>, stopped by a named event every one of them
/// waits on (scskiller_warm --stop-event). Paths are the same on both sides.</summary>
sealed class WindowsWarmHost : IWarmHost
{
    sealed class Child(Process p) : IWarmProcess
    {
        public int Id => p.Id;
        public TextReader Output => p.StandardOutput;
        public Task<int?> Exited { get; } = Exit(p);
        public bool HasExited => p.HasExited;
        public void Suspend(bool suspend) => ProcessTree.Suspend(p.Id, suspend);

        public void Kill()
        {
            try { p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }   // exited meanwhile
        }

        static async Task<int?> Exit(Process p)
        {
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
    }

    readonly string _workDir;
    readonly EventWaitHandle _stop;
    bool _disposed;

    public IReadOnlyList<string> StopArgs { get; }

    public WindowsWarmHost(string workDir)
    {
        _workDir = workDir;
        var name = $"Local\\SCSKiller.Stop.{Guid.NewGuid():N}";
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        StopArgs = ["--stop-event", name];
    }

    public string WarmPath(string host) => host;
    public string HostPath(string warm) => warm;
    public IReadOnlyList<string> AdapterArgs(GpuInfo gpu) => ["--adapter-luid", gpu.AdapterLuid.ToString("X16")];
    public void Stop() => _stop.Set();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Dispose();
    }

    public IWarmProcess Start(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, Action<string> errorLine)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8, WorkingDirectory = _workDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        try { WarmJob.Current.Add(p.Id); }   // dies with this SCSKiller process, whatever ends it (WarmJob)
        catch (Exception) { p.Kill(entireProcessTree: true); throw; }
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) errorLine(l); };
        p.BeginErrorReadLine();
        return new Child(p);
    }
}
