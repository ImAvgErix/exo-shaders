using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Planning;

/// <summary>Ray tracing state object records ('R' CreateStateObject, 'A' AddToStateObject; format in proxy.cpp write_so):
/// parse, rehydrate and materialize. They replay exactly as recorded, so the Core only has to carry them through.</summary>
public class StateObjectTests
{
    static readonly EngineInfo Engine = new("Fake", "1", null, "D3D12", false, null);
    static readonly Game Game = new("test:rt", "Fake", Store.Other, @"C:\nowhere", @"C:\nowhere\fake.exe");

    /// <summary>Writes the proxy's canonical state object body.</summary>
    sealed class Body
    {
        readonly MemoryStream s = new();
        public Body U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); return this; }
        public Body H(string hex) { s.Write(Convert.FromHexString(hex)); return this; }
        public Body Str(string? x) => x == null ? U32(uint.MaxValue) : U32((uint)x.Length).Raw(Encoding.Unicode.GetBytes(x));
        Body Raw(byte[] b) { s.Write(b); return this; }
        public byte[] Bytes => s.ToArray();
    }

    static (string Sha, byte[] Bytes) Blob(string s) { var b = Encoding.ASCII.GetBytes(s); return (PsoDb.Hex(SHA1.HashData(b)), b); }
    static readonly (string Sha, byte[] Bytes) Rs = Blob("global root signature"), Lrs = Blob("local root signature"),
        Lib = Blob("DXIL library: closest hit"), Rg = Blob("DXIL library: raygen"), Gone = Blob("DXIL library of another build");

    /// <summary>A collection: global RS, a library with one renamed export, a hit group, the local RS associated with it
    /// (subobject 3), shader + pipeline config.</summary>
    static PsoDb.Rec Collection(string lib, string hg) => new('R', new Body().U32(0).U32(7)
        .U32(1).H(Rs.Sha)
        .U32(5).H(lib).U32(1).Str(hg + "_ch").Str("ch").U32(0)
        .U32(11).Str(hg).U32(0).Str(null).Str(hg + "_ch").Str(null)
        .U32(2).H(Lrs.Sha)
        .U32(7).U32(3).U32(1).Str(hg)
        .U32(9).U32(16).U32(8)
        .U32(10).U32(1).Bytes);

    /// <summary>A pipeline linking collections: state object config (additions), global RS, a raygen library, the
    /// collections, shader config, pipeline config 1, node mask, a DXIL association.</summary>
    static PsoDb.Rec Link(params PsoDb.Rec[] colls)
    {
        var b = new Body().U32(3).U32(7 + (uint)colls.Length).U32(0).U32(4).U32(1).H(Rs.Sha).U32(5).H(Rg.Sha).U32(0);
        foreach (var c in colls) b.U32(6).H(c.Key).U32(0);
        return new('R', b.U32(9).U32(16).U32(8).U32(12).U32(1).U32(0).U32(3).U32(1).U32(8).Str("cfg").U32(1).Str("rg").Bytes);
    }

    static PsoDb.Rec Add(PsoDb.Rec baseRec, string lib) => new('A', [.. Convert.FromHexString(baseRec.Key), .. new Body().U32(3).U32(3)
        .U32(0).U32(4)
        .U32(5).H(lib).U32(0)
        .U32(11).Str("hgA").U32(0).Str(null).Str("chA").Str(null).Bytes]);

    [Fact]
    public void ParsesWhatAStateObjectNamesAndBuildsOn()
    {
        var c1 = Collection(Lib.Sha, "hg1");
        var so = PsoDb.ParseStateObject(c1);
        Assert.Equal(0u, so.Type);
        Assert.Null(so.Base);
        Assert.Equal([Lib.Sha], so.Libraries);
        Assert.Equal([Rs.Sha, Lrs.Sha], so.RootSignatures);
        Assert.Empty(so.Depends);

        var c2 = Collection(Gone.Sha, "hg2");
        var link = PsoDb.ParseStateObject(Link(c1, c2));
        Assert.Equal(3u, link.Type);
        Assert.Equal([Rg.Sha], link.Libraries);
        Assert.Equal([c1.Key, c2.Key], link.Depends);

        var add = Add(Link(c1, c2), Lib.Sha);
        var a = PsoDb.ParseStateObject(add);
        Assert.Equal(Link(c1, c2).Key, a.Base);
        Assert.Equal([Link(c1, c2).Key], a.Depends);
        Assert.Equal([Lib.Sha], a.Libraries);

        Assert.Throws<InvalidDataException>(() => PsoDb.ParseStateObject(c1 with { Payload = c1.Payload[..^2] })); // truncated
        Assert.Throws<InvalidDataException>(() => PsoDb.ParseStateObject(new('R', new Body().U32(3).U32(1).U32(13).Bytes))); // a work graph
        Assert.Throws<InvalidDataException>(() => PsoDb.ParseStateObject(c1 with { Payload = [.. c1.Payload, 0] })); // trailing bytes
        Assert.Equal(new[] { Lib.Sha, Rs.Sha, Lrs.Sha, Rg.Sha }.Order(), Rehydrate.References([c1, Link(c1), add]).Order()); // blobs, never record keys
    }

    /// <summary>A long AddToStateObject chain splits into uploads in one walk of it (no recursion as deep as the chain, no
    /// closure rebuilt per link); the links whose closure is over an upload's records are left out.</summary>
    [Fact]
    public void ChunksTakeALongAdditionChain()
    {
        var chain = new List<PsoDb.Rec> { new('R', new Body().U32(3).U32(1).U32(5).H(Lib.Sha).U32(0).Bytes) };
        while (chain.Count < Sharing.ChunkRecords + 10_000) chain.Add(Add(chain[^1], Lib.Sha));
        var whole = HashOnly.Canonical(chain, local: false, out _);
        var chunk = Assert.Single(HashOnly.Chunks(whole, Sharing.ChunkRecords, Sharing.ChunkRaw, out var skipped));
        Assert.Equal(10_000, skipped);
        Assert.Equal(whole.Take(Sharing.ChunkRecords).Select(r => r.Key), chunk.Select(r => r.Key));
    }

    /// <summary>A chunk's bytes count the root signatures its records name, not the recording's others; a PSO that is over
    /// the bytes with its own is left out.</summary>
    [Fact]
    public void ChunkBytesCountTheirOwnRootSignatures()
    {
        var recs = new List<PsoDb.Rec>();
        for (var i = 0u; i < 3; i++)
        {
            var rs = RootSig.Serialize(new RootSig.Desc(0, [.. Enumerable.Range(0, 64).Select(k => new uint[] { 1, 0, (uint)k, i, 1 })]), []);
            var sha = PsoDb.Hex(SHA1.HashData(rs));
            recs.Add(new('B', [.. Convert.FromHexString(sha), .. rs]));
            recs.Add(new('C', PsoDb.Compute(sha, new string((char)('a' + i), 40))));
        }
        var whole = HashOnly.Canonical(recs, local: false, out _);
        var one = 5L + whole.First(r => r.Tag == 'B').Payload.Length + 5L + whole.First(r => r.Tag == 'C').Payload.Length;
        var chunks = HashOnly.Chunks(whole, 100, one);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Equal("BC", string.Concat(c.Select(r => r.Tag))));
        Assert.Empty(HashOnly.Chunks(whole, 100, one - 1, out var skipped));
        Assert.Equal(3, skipped);
    }

    sealed class Install(params (string Sha, byte[] Bytes)[] blobs) : IEngineReader
    {
        public EngineInfo? Detect(Game game) => Engine;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => new("c", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
        {
            foreach (var (h, b) in blobs) if (sha1s.Contains(h)) sink(h, b);
        }
    }

    [Fact]
    public void RehydratesDxilLibrariesLikeShaders()
    {
        var dir = Ff7.TempDir("rt-rehydrate");
        var input = Path.Combine(dir, "hashonly.db");
        var c1 = Collection(Lib.Sha, "hg1");
        var recs = new[] { c1, Link(c1), Add(Link(c1), Gone.Sha) };
        using (var f = File.Create(input))
        {
            PsoDb.WriteBlob(f, Rs.Sha, Rs.Bytes); // root signatures stay in a hash-only recording
            PsoDb.WriteBlob(f, Lrs.Sha, Lrs.Bytes);
            foreach (var r in recs) PsoDb.Write(f, r.Tag, r.Payload);
        }
        var outDb = Path.Combine(dir, "recording.db");
        var r2 = Rehydrate.Run(input, outDb, Game, Engine, new Install(Lib, Rg));
        Assert.Equal(5, r2.Referenced);
        Assert.Equal(2, r2.AlreadyPresent);
        Assert.Equal(2, r2.Found);
        Assert.Equal([Gone.Sha], r2.Missing);
        Assert.Equal(recs.Select(r => r.Key), PsoDb.Read(outDb).Where(r => r.Tag != 'B').Select(r => r.Key)); // same records, same order
        Assert.Equal([Gone.Sha], RehydrateTests.Unresolved(outDb));
    }

    [Fact]
    public void MaterializeDropsAStateObjectWhoseBlobOrDependencyIsMissing()
    {
        var dir = Ff7.TempDir("rt-materialize");
        var c1 = Collection(Lib.Sha, "hg1");
        var c2 = Collection(Gone.Sha, "hg2");                // its library isn't in this install
        var link = Link(c1, c2);                             // builds on c2: dropped with it
        var flat = new PsoDb.Rec('R', Link().Payload);       // no collections: kept
        var grow = Add(flat, Lib.Sha);                       // kept (its base is)
        var growLink = Add(link, Lib.Sha);                   // its base was dropped
        var recording = Path.Combine(dir, "recording.db");
        using (var f = File.Create(recording))
        {
            foreach (var b in new[] { Rs, Lrs, Lib, Rg }) PsoDb.WriteBlob(f, b.Sha, b.Bytes);
            foreach (var r in new[] { c1, c2, link, flat, grow, growLink }) PsoDb.Write(f, r.Tag, r.Payload);
        }
        var plan = new Plan(Game.Id, "c", "PCD3D_SM6", "nvidia-1", new PlanStats(6, 0, 0, 0, false), Path.Combine(dir, "plan.bin"));
        PlanFile.Write(plan, []);
        var work = Path.Combine(dir, "work");
        new Planner().Materialize(plan, Game, Engine, new Install(), new Recording(recording), work, default);

        Assert.Equal(new[] { c1, flat, grow }.Select(r => r.Key), PsoDb.Read(Path.Combine(work, "scskiller.db")).Where(r => r.Tag != 'B').Select(r => r.Key));
        Assert.Equal(3, Planner.SkippedIn(work)); // skipped, not failed: not in this install
    }

    [Fact]
    public void SessionLogCountsStateObjectsApartFromPsos()
    {
        var csv = Path.Combine(Ff7.TempDir("rt-session"), "creates.csv");
        // a cached state object still takes well over a PSO's 3 ms: compiled only from StateObjectCompileMs, and never the worst PSO compile
        File.WriteAllText(csv, "1.0,R,0,0,80.0\n2.0,A,1,1,14.0\n3.0,R,1,1,24.9\n4.0,A,0,0,25.0\n5.0,G,0,0,9.0\n");
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(5), 5, 0, 0, 1, 9.0, StateObjectsReady: 2, StateObjectsCompiled: 2), SessionLog.Read(csv).Last);
    }
}
