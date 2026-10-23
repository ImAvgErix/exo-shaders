using System.Buffers.Binary;

namespace SCSKiller.Core.App;

/// <summary>How a game's pipelines were served during one run, as the D3D12 runtime itself reported them
/// (Microsoft-Windows-Direct3D12 events 155 to 163, read by <see cref="PipelineCacheWatch"/>). A pipeline is one create
/// (CreatePipelineState, CreateStateObject, AddToStateObject) that needed lookups; it came from the cache when every
/// required lookup hit the precompiled database (PSDB, Advanced Shader Delivery) or the driver's dynamic cache, which is
/// where a compile puts it. <paramref name="CreateMs"/>: the time all creates took, lookups or not.</summary>
public sealed record PipelineCacheUse(DateTimeOffset At, long Pipelines, long FromCache, long FromPsdb, long Lookups,
    long PsdbHits, long CacheHits, long Stores, long Creates, double CreateMs)
{
    /// <summary>Rounded down: only all of them reads 100%. Null: no pipeline needed a lookup.</summary>
    public int? Percent => Pipelines > 0 ? (int)(100 * FromCache / Pipelines) : null;

    /// <summary>"92% of pipelines came from the cache".</summary>
    public string Verdict => Percent is { } p ? $"{p}% of pipelines came from the cache" : "No pipeline needed the cache";

    /// <summary>The counts behind <see cref="Verdict"/>, for the page and the log.</summary>
    public string Detail =>
        $"{FromCache:N0} of {Pipelines:N0} pipelines from the cache" + (FromPsdb > 0 ? $" ({FromPsdb:N0} precompiled by Windows)" : "")
        + $", {Pipelines - FromCache:N0} compiled. Lookups: {Lookups:N0}, {CacheHits:N0} in the driver cache, {PsdbHits:N0} in precompiled databases; "
        + $"{Stores:N0} stored. Creating {Creates:N0} pipelines took {Format.Ms(CreateMs)}";
}

/// <summary>Event 161, 162 or 163's payload: eight UInt32s (the provider's manifest, <c>Get-WinEvent -ListProvider
/// Microsoft-Windows-Direct3D12</c>).</summary>
public readonly record struct CreateLookups(uint RequiredLookups, uint RequiredPsdbHits, uint RequiredCacheHits, uint IgnoredHits,
    uint OptionalLookups, uint OptionalPsdbHits, uint OptionalCacheHits, uint CacheStores)
{
    public const int Size = 32;

    /// <summary>Null when the payload is too short (another event version).</summary>
    public static CreateLookups? Read(ReadOnlySpan<byte> p)
    {
        if (p.Length < Size) return null;
        Span<uint> u = stackalloc uint[8];
        for (var i = 0; i < 8; i++) u[i] = BinaryPrimitives.ReadUInt32LittleEndian(p[(i * 4)..]);
        return new(u[0], u[1], u[2], u[3], u[4], u[5], u[6], u[7]);
    }
}

/// <summary>One process's D3D12 pipeline creates, summed as their events arrive (one thread adds, any thread reads).</summary>
public sealed class PipelineCacheTally
{
    // the provider's events: 155/156 CreatePipelineState begin/end, 157/158 CreateStateObject, 159/160 AddToStateObject;
    // 161, 162, 163 the lookups of each
    public const ushort FirstTimed = 155, LastTimed = 160, FirstLookups = 161, LastLookups = 163;

    readonly object _lock = new();
    readonly Dictionary<(int Kind, uint Thread), long> _begun = [];
    long _pipelines, _fromCache, _fromPsdb, _lookups, _psdbHits, _cacheHits, _stores, _creates, _ticks;

    /// <summary>Last event's time (UTC ticks); for dropping a process that has gone.</summary>
    public long LastSeen { get; private set; }

    /// <summary>One event of the D3D12 provider. <paramref name="timestamp"/>: the event's time in 100 ns units (ETW's
    /// FILETIME stamps); a begin and its end are on one thread.</summary>
    public void Add(ushort id, uint thread, long timestamp, ReadOnlySpan<byte> payload)
    {
        lock (_lock)
        {
            LastSeen = DateTime.UtcNow.Ticks;
            if (id is >= FirstTimed and <= LastTimed)
            {
                var key = ((id - FirstTimed) / 2, thread);
                if ((id - FirstTimed) % 2 == 0) _begun[key] = timestamp;
                else if (_begun.Remove(key, out var begun) && timestamp >= begun)
                {
                    _creates++;
                    _ticks += timestamp - begun;
                }
            }
            else if (id is >= FirstLookups and <= LastLookups && CreateLookups.Read(payload) is { RequiredLookups: > 0 } l)
            {
                _pipelines++;
                if (l.RequiredPsdbHits + (long)l.RequiredCacheHits >= l.RequiredLookups) _fromCache++;
                if (l.RequiredPsdbHits >= l.RequiredLookups) _fromPsdb++;
                _lookups += l.RequiredLookups;
                _psdbHits += l.RequiredPsdbHits;
                _cacheHits += l.RequiredCacheHits;
                _stores += l.CacheStores;
            }
        }
    }

    /// <summary>Nothing counted yet: no create finished and none needed a lookup.</summary>
    public bool Empty { get { lock (_lock) return _creates == 0 && _pipelines == 0; } }

    /// <summary>The tallies of one run's processes (a game that restarts itself, a launcher's child) as one.</summary>
    public static PipelineCacheUse? Sum(IEnumerable<PipelineCacheTally> tallies, DateTimeOffset at)
    {
        long pipelines = 0, fromCache = 0, fromPsdb = 0, lookups = 0, psdbHits = 0, cacheHits = 0, stores = 0, creates = 0, ticks = 0;
        foreach (var t in tallies)
            lock (t._lock)
            {
                pipelines += t._pipelines; fromCache += t._fromCache; fromPsdb += t._fromPsdb; lookups += t._lookups;
                psdbHits += t._psdbHits; cacheHits += t._cacheHits; stores += t._stores; creates += t._creates; ticks += t._ticks;
            }
        return creates == 0 && pipelines == 0 ? null
            : new(at, pipelines, fromCache, fromPsdb, lookups, psdbHits, cacheHits, stores, creates, ticks / (double)TimeSpan.TicksPerMillisecond);
    }
}

/// <summary>What D3D12 took the game to be when it made its device: the ASDInit event (TraceLogging provider
/// 82fe78cc-ff52-4e2f-a7bb-5c90636d14ba), fields as Microsoft's D3D12CacheListener reads them. A game that names itself
/// (ID3D12ApplicationIdentity::SetApplicationIdentity, Unreal 5.8) has the driver key its cache and app profile on it:
/// a warm that should land in that cache sets the same <see cref="Name"/>, <see cref="Engine"/>, <see cref="Version"/>
/// and <see cref="EngineVersion"/> before it makes its device. <see cref="Step"/>: where the runtime's PSDB setup ended
/// (Success, or the check that failed). Versions are a.b.c.d.</summary>
public sealed record AsdIdentity(DateTimeOffset SeenAt, string? Step, string? Name, string? Engine, string? Version, string? EngineVersion,
    string? Exe, string? DescSource, string? PsdbSource)
{
    static readonly string[] Steps = ["None", "Success", "ReadApplicationRegistration", "OpenPsdb", "IdentityCheck"];
    static readonly string[] DescSources = ["None", "PrecompiledDatabaseOverride", "SetApplicationIdentity", "ShaderCacheRegistration"];
    static readonly string[] PsdbSources = ["None", "PrecompiledDatabaseOverride", "PrecompiledDatabasePath", "ShaderCacheRegistration"];

    /// <summary>From ASDInit's fields, keyed "Struct.Field" inside a struct (ASDInit repeats names across its structs);
    /// a value is a string or a ulong. Null when it names no application and gives no step.</summary>
    public static AsdIdentity? From(IReadOnlyDictionary<string, object> f, DateTimeOffset at)
    {
        string? S(string k) => f.TryGetValue(k, out var v) && v is string s && s.Length > 0 ? s : null;
        ulong? N(string k) => f.TryGetValue(k, out var v) && v is ulong n ? n : null;
        string? V(string k) => N(k) is { } v ? $"{v >> 48 & 0xffff}.{v >> 32 & 0xffff}.{v >> 16 & 0xffff}.{v & 0xffff}" : null;
        string? E(string k, string[] names) => N(k) is { } v ? v < (ulong)names.Length ? names[v] : v.ToString() : null;
        var id = new AsdIdentity(at, E("Step", Steps), S("ApplicationDesc.Name"), S("ApplicationDesc.EngineName"), V("ApplicationDesc.Version"),
            V("ApplicationDesc.EngineVersion"), S("ApplicationDesc.ExeFilename"), E("ApplicationDescSource", DescSources), E("DefaultPsdbSource", PsdbSources));
        return id is { Step: null, Name: null, Exe: null } ? null : id;
    }

    /// <summary>For the log: "Name (Engine 5.8.0.0), from SetApplicationIdentity, PSDB setup: Success".</summary>
    public override string ToString() =>
        (Name != null ? $"\"{Name}\" {Version}" : "no application name") + (Engine != null ? $" ({Engine} {EngineVersion})" : "")
        + (DescSource != null ? $", from {DescSource}" : "") + (Step != null ? $", PSDB setup: {Step}" : "");
}
