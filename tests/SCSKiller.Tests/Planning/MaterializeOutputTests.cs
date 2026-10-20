using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Planning;

/// <summary>What Materialize writes for the warm, byte for byte, over a seeded corpus of every kind of record (recorded
/// PSOs, flagged ones and state objects built on collections; plan templates, items, a template the recording holds,
/// collections of libraries with and without ray tracing entry points, pack entries a shader of which the DLL may lack;
/// blobs in the recording, the install, the DLL or nowhere). The fixture games' Materialize output is pinned the same
/// way (<see cref="Digest"/>): a change to what any of them writes changes a digest.</summary>
public class MaterializeOutputTests
{
    const int Cases = 200;
    const string Written = "92728aa256df72678c5a8707f16e4c7e97675384ea3cca09b9e52fe0ed4c6a2a";

    static readonly EngineInfo Engine = new("Fake", "1", null, "D3D12", false, null);

    static byte[] Container(string fourcc, string seed)
    {
        var data = Encoding.ASCII.GetBytes(seed.PadRight(16, '.'));
        var b = new byte[44 + data.Length];
        "DXBC"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), (uint)b.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), 36);
        Encoding.ASCII.GetBytes(fourcc).CopyTo(b, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), (uint)data.Length);
        data.CopyTo(b, 44);
        return b;
    }

    static string Sha(byte[] b) => PsoDb.Hex(SHA1.HashData(b));

    static byte[] U32(params uint[] v)
    {
        var b = new byte[4 * v.Length];
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 * i), v[i]);
        return b;
    }

    sealed class Install(Dictionary<string, byte[]> shaders) : IEngineReader
    {
        public EngineInfo? Detect(Game game) => Engine;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => new("c", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
        {
            foreach (var h in sha1s.Order(StringComparer.Ordinal)) if (shaders.TryGetValue(h, out var b)) sink(h, b);
        }
    }

    /// <summary>What Materialize wrote into <paramref name="work"/>: both dbs and the skipped count.</summary>
    public static string Digest(string work)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in new[] { "scskiller.db", "scskiller_gen.db", Planner.SkippedFile })
            h.AppendData(File.Exists(Path.Combine(work, file)) ? File.ReadAllBytes(Path.Combine(work, file)) : [0xFF]);
        var d = Convert.ToHexStringLower(h.GetHashAndReset());
        return d;
    }

    [Fact]
    public void Materialize_writes_the_same_records_for_every_kind_of_input()
    {
        var root = Path.Combine(Path.GetTempPath(), "scskiller-materialize-" + Guid.NewGuid().ToString("N")[..8]);
        using var all = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var n = 0; n < Cases; n++)
        {
            var random = new Random(n);
            var dir = Path.Combine(root, $"case{n}");
            var exeDir = Path.Combine(dir, "game");
            Directory.CreateDirectory(exeDir);
            File.WriteAllBytes(Path.Combine(exeDir, "game.exe"), MiddlewarePackTests.Pe(null));
            var install = new Dictionary<string, byte[]>();
            var recorded = new List<byte[]>();
            var inDll = new List<byte[]>();
            string Place(byte[] blob, int where)   // 0 the install, 1 the recording, 2 the DLL, 3 nowhere
            {
                if (where == 0) install[Sha(blob)] = blob;
                else if (where == 1) recorded.Add(blob);
                else if (where == 2) inDll.Add(blob);
                return Sha(blob);
            }
            string Shader(int i) => Place(Container("DXIL", $"case {n} shader {i}"), random.Next(4));
            var rsBlob = Container("RTS0", $"case {n} root signature");
            var rs = Sha(rsBlob);

            var main = new List<PsoDb.Rec>();
            for (var i = random.Next(6); i > 0; i--)
            {
                var pso = new PsoDb.Rec('C', PsoDb.Compute(random.Next(3) == 0 ? rs : PsoDb.Zero, Shader(100 + i)));
                main.Add(pso);
                if (random.Next(4) == 0) main.Add(new PsoDb.Rec('L', Convert.FromHexString(pso.Key)));
            }
            for (var i = random.Next(3); i > 0; i--)
            {
                var library = Place(Container("DXIL", $"case {n} library {i}"), random.Next(4));
                var collection = new PsoDb.Rec('R', [.. U32(0, 2, 1), .. Convert.FromHexString(rs), .. U32(5), .. Convert.FromHexString(library), .. U32(0)]);
                main.Add(collection);
                main.Add(new PsoDb.Rec('R', [.. U32(3, 2, 1), .. Convert.FromHexString(rs), .. U32(6), .. Convert.FromHexString(collection.Key), .. U32(0)]));
            }

            List<PsoDb.Rec> body = [new('B', [.. Convert.FromHexString(rs), .. rsBlob])];
            for (var i = random.Next(5); i > 0; i--)
            {
                var template = new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, Shader(200 + i)));
                body.Add(template);
                if (random.Next(2) == 0)
                    body.Add(new PsoDb.Rec('P', PsoDb.Item(template.Key, PsoDb.Zero, new Dictionary<int, string> { [(int)Stage.Compute] = Shader(300 + i) }, null)));
            }
            if (random.Next(3) == 0 && main.Count > 0)   // a recorded template a plan item overrides the stage of
                body.Add(new PsoDb.Rec('P', PsoDb.Item(main[0].Key, PsoDb.Zero, new Dictionary<int, string> { [(int)Stage.Compute] = Shader(400) }, null)));
            for (var i = random.Next(4); i > 0; i--)
            {
                var library = Place(random.Next(2) == 0 ? RtCollectionTests.Library((7, $"RayGen{i}", 0), (10, $"CHS{i}", 16)) : RtCollectionTests.Library(), random.Next(2) == 0 ? 0 : 3);
                body.Add(new PsoDb.Rec('Y', RtCollections.Item(library, rs, rs, rs, new RtCollections.Rule(rs, 0, 1, 16, 8, true))));
            }
            var entries = new List<PsoDb.Rec>();
            for (var i = random.Next(4); i > 0; i--)
                entries.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero,
                    random.Next(3) == 0 ? Sha(Container("DXIL", $"case {n} not in the dll {i}")) : Place(Container("DXIL", $"case {n} dll {i}"), 2))));
            var dll = Path.Combine(exeDir, "amd_fidelityfx_dx12.dll");
            File.WriteAllBytes(dll, MiddlewarePackTests.Pe("amd_fidelityfx_dx12.dll", [.. inDll, Container("DXIL", $"case {n} padding")]));
            body.AddRange(entries.Select(e => MiddlewarePacks.Wrap(e, "amd_fidelityfx_dx12.dll", Sha(File.ReadAllBytes(dll)))));

            var db = Path.Combine(dir, "recording.db");
            using (var f = File.Create(db))
            {
                foreach (var b in recorded) PsoDb.WriteBlob(f, Sha(b), b);
                foreach (var r in main) PsoDb.Write(f, r.Tag, r.Payload);
            }
            var plan = new Plan("test", "c", "PCD3D_SM6", "nvidia-1", new PlanStats(0, 0, 0, 0, false), Path.Combine(dir, "plan.bin"));
            PlanFile.Write(plan, body);
            var work = Path.Combine(dir, "work");
            new Planner().Materialize(plan, new Game("test:materialize", "test", Store.Other, exeDir, Path.Combine(exeDir, "game.exe")), Engine,
                new Install(install), new Recording(db), work, default);
            all.AppendData(Encoding.ASCII.GetBytes(Digest(work)));
        }
        Assert.Equal(Written, Convert.ToHexStringLower(all.GetHashAndReset()));
    }
}
