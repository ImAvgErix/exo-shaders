using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace SCSKiller.Core.App;

/// <summary>A real-time ETW session on D3D12's own pipeline events (what Microsoft's MIT D3D12CacheListener reads):
/// - Microsoft-Windows-Direct3D12 (5d8087dd-3a9b-4f56-90df-49196cdc4f11), keyword APIs (0x400), which is exactly events
///   155 to 163: the begin and end of each CreatePipelineState, CreateStateObject and AddToStateObject, and the lookups of
///   each (<see cref="CreateLookups"/>). Switched off while a compile runs and no game plays (<see cref="Creates"/>): the
///   warm's own creates are ignored anyway (they run from the data folder).
/// - the runtime's TraceLogging provider (82fe78cc-ff52-4e2f-a7bb-5c90636d14ba), for ASDInit (<see cref="AsdIdentity"/>):
///   always on, since a game sends it once, when it makes its device.
/// Each process's events are summed (<see cref="PipelineCacheTally"/>) under its image path, read once at its first event.
/// Starting the session needs an administrator's token, the Performance Log Users group, or the access
/// <see cref="GrantAccess"/> gives an account once (on <see cref="SessionGuid"/> and the two providers).</summary>
public sealed unsafe class PipelineCacheWatch : IDisposable
{
    public const string SessionName = "ExoShaders-PipelineCache";
    /// <summary>The session's control GUID: what <see cref="GrantAccess"/> puts the account's rights on.</summary>
    public static readonly Guid SessionGuid = new("6f3c5e2a-8d41-4b7e-9a0c-2e5b7d1f4c93");
    public static readonly Guid D3D12Provider = new("5d8087dd-3a9b-4f56-90df-49196cdc4f11");
    public static readonly Guid D3D12TraceLogging = new("82fe78cc-ff52-4e2f-a7bb-5c90636d14ba");
    const ulong ApisKeyword = 0x400;
    const int ErrorAlreadyExists = 183, ErrorAccessDenied = 5;

    readonly Func<int, string?> _imagePath;
    readonly string _ignored;   // the data folder: the warm's staged copies
    readonly ConcurrentDictionary<uint, Proc> _procs = new();
    readonly nint _props;
    readonly GCHandle _self;
    ulong _session, _trace;
    Thread? _thread;
    bool _creates, _disposed;
    long _events;

    sealed class Proc(string? path)
    {
        public readonly string? Path = path;
        public readonly PipelineCacheTally Tally = new();
        public volatile AsdIdentity? Identity;
    }

    /// <summary>Events the session handed over so far (a live check).</summary>
    public long Events => Interlocked.Read(ref _events);

    PipelineCacheWatch(Func<int, string?> imagePath, string ignoredDir)
    {
        _imagePath = imagePath;
        _ignored = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ignoredDir)) + Path.DirectorySeparatorChar;
        _props = Properties();
        _self = GCHandle.Alloc(this);
    }

    /// <summary>The session, started with both providers on; null with the Win32 error when Windows refused it.
    /// <paramref name="imagePath"/>: a process's image path (null: gone or unreadable); its creates count for no game.</summary>
    public static PipelineCacheWatch? Start(Func<int, string?> imagePath, string ignoredDir, out int error)
    {
        var w = new PipelineCacheWatch(imagePath, ignoredDir);
        error = w.Open();
        if (error == 0) return w;
        w.Dispose();
        return null;
    }

    /// <summary>For the log: why <see cref="Start"/> failed.</summary>
    public static string Why(int error) => error == ErrorAccessDenied
        ? "Windows lets only administrators, the Performance Log Users group and accounts given access (scskiller pipeline-cache-access) watch D3D12's events"
        : $"the ETW session didn't start (error {error})";

    int Open()
    {
        var e = StartTraceW(out _session, SessionName, _props);
        if (e == ErrorAlreadyExists)   // left by a process that ended without stopping it
        {
            Stop(0);
            e = StartTraceW(out _session, SessionName, Properties(_props));
        }
        if (e != 0) return (int)e;
        AppDomain.CurrentDomain.ProcessExit += OnExit;   // a session outlives its process
        var tl = D3D12TraceLogging;
        e = EnableTraceEx2(_session, ref tl, 1, 5, 0, 0, 0, 0);
        if (e != 0) return (int)e;
        if (Creates(true) is var c and not 0) return c;
        var log = new EventTraceLogfile
        {
            LoggerName = Marshal.StringToHGlobalUni(SessionName),
            ProcessTraceMode = 0x100 /* PROCESS_TRACE_MODE_REAL_TIME */ | 0x10000000 /* PROCESS_TRACE_MODE_EVENT_RECORD */,
            EventRecordCallback = (nint)(delegate* unmanaged<EventRecord*, void>)&OnEvent,
            Context = GCHandle.ToIntPtr(_self),
        };
        try { _trace = OpenTraceW(ref log); }
        finally { Marshal.FreeHGlobal(log.LoggerName); }
        if (_trace == ulong.MaxValue) { _trace = 0; return Marshal.GetLastPInvokeError() is var le and not 0 ? le : 1; }
        _thread = new Thread(() =>
        {
            var handles = stackalloc ulong[1];
            handles[0] = _trace;
            _ = ProcessTrace(handles, 1, 0, 0);   // returns once the session stops
        }) { IsBackground = true, Name = "pipeline cache events" };
        _thread.Start();
        return 0;
    }

    /// <summary>The creates' events on or off; 0 or the Win32 error.</summary>
    public int Creates(bool on)
    {
        if (on == _creates || _session == 0) return 0;
        var p = D3D12Provider;
        var e = (int)EnableTraceEx2(_session, ref p, on ? 1u : 0u, 4, ApisKeyword, 0, 0, 0);
        if (e == 0) _creates = on;
        return e;
    }

    /// <summary>The summed creates and the last identity of the processes whose image <paramref name="ofGame"/> takes,
    /// taken out of the session's books.</summary>
    public (PipelineCacheUse? Use, AsdIdentity? Identity) Take(Func<string, bool> ofGame, DateTimeOffset at)
    {
        var taken = new List<Proc>();
        foreach (var (pid, p) in _procs)
            if (p.Path != null && ofGame(p.Path) && _procs.TryRemove(pid, out _)) taken.Add(p);
        return (PipelineCacheTally.Sum(taken.Select(p => p.Tally), at),
            taken.Select(p => p.Identity).OfType<AsdIdentity>().MaxBy(i => i.SeenAt));
    }

    /// <summary>Drops processes quiet for <paramref name="quiet"/> whose pid is gone or another image now (no game took them).</summary>
    public void Prune(TimeSpan quiet)
    {
        var before = DateTime.UtcNow.Ticks - quiet.Ticks;
        foreach (var (pid, p) in _procs)
            if (p.Tally.LastSeen < before && (p.Path == null || !string.Equals(_imagePath((int)pid), p.Path, StringComparison.OrdinalIgnoreCase)))
                _procs.TryRemove(pid, out _);
    }

    [UnmanagedCallersOnly]
    static void OnEvent(EventRecord* e)
    {
        try
        {
            if (GCHandle.FromIntPtr(e->UserContext).Target is PipelineCacheWatch w) w.Handle(e);
        }
        catch (Exception) { }   // never into ETW's thread
    }

    void Handle(EventRecord* e)
    {
        Interlocked.Increment(ref _events);
        var id = e->Id;
        if (e->ProviderId == D3D12Provider)
        {
            if (id is < PipelineCacheTally.FirstTimed or > PipelineCacheTally.LastLookups || Of(e->ProcessId) is not { } p) return;
            p.Tally.Add(id, e->ThreadId, e->TimeStamp, new ReadOnlySpan<byte>((void*)e->UserData, e->UserDataLength));
        }
        else if (e->ProviderId == D3D12TraceLogging && EventName(e) == "ASDInit" && Of(e->ProcessId) is { } p
                 && AsdIdentity.From(Fields(e), DateTimeOffset.FromFileTime(e->TimeStamp)) is { } identity)
            p.Identity = identity;
    }

    /// <summary>The process's books; null for one of ours (the data folder's staged warms).</summary>
    Proc? Of(uint pid)
    {
        var p = _procs.GetOrAdd(pid, id => new Proc(_imagePath((int)id)));
        return p.Path?.StartsWith(_ignored, StringComparison.OrdinalIgnoreCase) == true ? null : p;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        if (_session != 0) Stop(_session);
        _session = 0;
        if (_trace != 0) _ = CloseTrace(_trace);
        _thread?.Join(TimeSpan.FromSeconds(5));
        Marshal.FreeHGlobal(_props);
        if (_self.IsAllocated) _self.Free();
    }

    void OnExit(object? sender, EventArgs e) => Dispose();

    /// <summary>Stops the session by handle, or by name (0).</summary>
    static void Stop(ulong session)
    {
        var props = Properties();
        try { _ = ControlTraceW(session, session == 0 ? SessionName : null, props, 1 /* EVENT_TRACE_CONTROL_STOP */); }
        finally { Marshal.FreeHGlobal(props); }
    }

    /// <summary>EVENT_TRACE_PROPERTIES (120 bytes on x64) and room for the session's name after it.</summary>
    const int PropsSize = 120, NameRoom = 1024;
    static nint Properties(nint reuse = 0)
    {
        var p = reuse != 0 ? reuse : Marshal.AllocHGlobal(PropsSize + NameRoom);
        new Span<byte>((void*)p, PropsSize + NameRoom).Clear();
        *(uint*)p = PropsSize + NameRoom;                     // Wnode.BufferSize
        *(Guid*)(p + 24) = SessionGuid;                       // Wnode.Guid: the session's security is this GUID's
        *(uint*)(p + 40) = 1;                                 // Wnode.ClientContext: QPC stamps
        *(uint*)(p + 44) = 0x00020000;                        // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        *(uint*)(p + 64) = 0x100;                             // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        *(uint*)(p + 116) = PropsSize;                        // LoggerNameOffset
        return p;
    }

    // ------------------------------------------------------------------ ASDInit (TraceLogging, decoded with TDH)

    static string? EventName(EventRecord* e)
    {
        var info = Info(e);
        if (info == null) return null;
        var off = BitConverter.ToInt32(info, 92);   // TRACE_EVENT_INFO.EventNameOffset
        return off > 0 ? Utf16At(info, off) : null;
    }

    static byte[]? Info(EventRecord* e)
    {
        uint size = 0;
        if (TdhGetEventInformation(e, 0, 0, null, ref size) != 122 /* ERROR_INSUFFICIENT_BUFFER */) return null;
        var buf = new byte[size];
        fixed (byte* b = buf)
            return TdhGetEventInformation(e, 0, 0, b, ref size) == 0 ? buf : null;
    }

    /// <summary>The event's fields, one struct level deep, keyed "Struct.Field" inside a struct: strings and unsigned
    /// integers (<see cref="AsdIdentity.From"/>).</summary>
    static Dictionary<string, object> Fields(EventRecord* e)
    {
        var fields = new Dictionary<string, object>();
        if (Info(e) is not { } info) return fields;
        int count = BitConverter.ToInt32(info, 100), top = BitConverter.ToInt32(info, 104);
        const int Array = 112, PropSize = 24;   // TRACE_EVENT_INFO.EventPropertyInfoArray, sizeof(EVENT_PROPERTY_INFO)
        string Name(int i) => Utf16At(info, BitConverter.ToInt32(info, Array + i * PropSize + 4));
        for (var i = 0; i < top && i < count; i++)
        {
            var at = Array + i * PropSize;
            var name = Name(i);
            if ((BitConverter.ToInt32(info, at) & 1) != 0)   // PropertyStruct
            {
                int start = BitConverter.ToUInt16(info, at + 8), members = BitConverter.ToUInt16(info, at + 10);
                for (var m = start; m < start + members && m < count; m++)
                    Read(e, info, Array + m * PropSize, name, Name(m), fields);
            }
            else Read(e, info, at, null, name, fields);
        }
        return fields;
    }

    static void Read(EventRecord* e, byte[] info, int at, string? structName, string name, Dictionary<string, object> fields)
    {
        var inType = BitConverter.ToUInt16(info, at + 8);
        var key = structName != null ? $"{structName}.{name}" : name;
        var names = new List<nint>();
        try
        {
            var desc = stackalloc PropertyDataDescriptor[2];
            uint n = 0;
            if (structName != null)
            {
                names.Add(Marshal.StringToHGlobalUni(structName));
                desc[n++] = new() { PropertyName = (ulong)names[^1], ArrayIndex = 0, Reserved = 0 };
            }
            names.Add(Marshal.StringToHGlobalUni(name));
            desc[n++] = new() { PropertyName = (ulong)names[^1], ArrayIndex = uint.MaxValue, Reserved = 0 };
            if (TdhGetPropertySize(e, 0, 0, n, desc, out var size) != 0 || size is 0 or > 4096) return;
            var buf = stackalloc byte[(int)size];
            if (TdhGetProperty(e, 0, 0, n, desc, size, buf) != 0) return;
            var bytes = new ReadOnlySpan<byte>(buf, (int)size);
            switch (inType)
            {
                case 1: fields[key] = Encoding.Unicode.GetString(bytes).TrimEnd('\0'); break;   // UNICODESTRING
                case 2: fields[key] = Encoding.UTF8.GetString(bytes).TrimEnd('\0'); break;      // ANSISTRING (TraceLogging: UTF-8)
                case (>= 3 and <= 10) or 13 or 20 or 21 when size <= 8:                             // integers, BOOLEAN, HEXINT32/64
                    ulong v = 0;
                    for (var i = (int)size - 1; i >= 0; i--) v = v << 8 | bytes[i];
                    fields[key] = v;
                    break;
            }
        }
        finally { foreach (var p in names) Marshal.FreeHGlobal(p); }
    }

    static string Utf16At(byte[] b, int off)
    {
        if (off <= 0 || off >= b.Length) return "";
        var end = off;
        while (end + 1 < b.Length && (b[end] | b[end + 1]) != 0) end += 2;
        return Encoding.Unicode.GetString(b, off, end - off);
    }

    // ------------------------------------------------------------------ one-time access (elevated)

    /// <summary>Lets <paramref name="sid"/> run this session without an administrator's token: start, query and read it in
    /// real time (its <see cref="SessionGuid"/>) and enable the two providers on it. Narrower than the Performance Log
    /// Users group (every session and provider). Needs an administrator; stays until Windows is reinstalled. 0 or the
    /// first Win32 error.</summary>
    public static int GrantAccess(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        const uint Query = 0x1, CreateRealtime = 0x20, GuidEnable = 0x80, AccessRealtime = 0x400;
        const uint AddDacl = 2;   // EventSecurityAddDACL
        fixed (byte* s = bytes)
            foreach (var (guid, rights) in new[] { (SessionGuid, Query | CreateRealtime | AccessRealtime), (D3D12Provider, GuidEnable), (D3D12TraceLogging, GuidEnable) })
            {
                var g = guid;
                if (EventAccessControl(ref g, AddDacl, s, rights, 1) is var e and not 0) return (int)e;
            }
        return 0;
    }

    // ------------------------------------------------------------------ interop (x64 layouts)

    [StructLayout(LayoutKind.Explicit)]
    struct EventRecord
    {
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(16)] public long TimeStamp;      // FILETIME: ProcessTrace converts the QPC stamps
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort Id;
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public nint UserData;
        [FieldOffset(104)] public nint UserContext;
    }

    [StructLayout(LayoutKind.Explicit, Size = 448)]   // EVENT_TRACE_LOGFILEW
    struct EventTraceLogfile
    {
        [FieldOffset(8)] public nint LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public nint EventRecordCallback;
        [FieldOffset(440)] public nint Context;
    }

    struct PropertyDataDescriptor
    {
        public ulong PropertyName;
        public uint ArrayIndex, Reserved;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTraceW(out ulong session, string name, nint props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTraceW(ulong session, string? name, nint props, uint code);
    [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong session, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, nint parameters);
    [DllImport("advapi32.dll", SetLastError = true)] static extern ulong OpenTraceW(ref EventTraceLogfile log);
    [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong* handles, uint count, nint start, nint end);
    [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong trace);
    [DllImport("advapi32.dll")] static extern uint EventAccessControl(ref Guid guid, uint operation, byte* sid, uint rights, byte allow);
    [DllImport("tdh.dll")] static extern uint TdhGetEventInformation(EventRecord* e, uint contexts, nint context, byte* info, ref uint size);
    [DllImport("tdh.dll")] static extern uint TdhGetPropertySize(EventRecord* e, uint contexts, nint context, uint count, PropertyDataDescriptor* desc, out uint size);
    [DllImport("tdh.dll")] static extern uint TdhGetProperty(EventRecord* e, uint contexts, nint context, uint count, PropertyDataDescriptor* desc, uint size, byte* buffer);
}
