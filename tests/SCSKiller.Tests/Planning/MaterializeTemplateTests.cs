using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Planning;

/// <summary>Materialize keeps a recorded template while a plan item that uses it is kept, even when the template's own
/// shader isn't in this install: the item swaps that stage, and the warm resolves items against their templates.</summary>
public class MaterializeTemplateTests
{
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

    sealed class Serves(Dictionary<string, byte[]> shaders) : IEngineReader
    {
        public EngineInfo? Detect(Game game) => Engine;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => new("c", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
        {
            foreach (var h in sha1s) if (shaders.TryGetValue(h, out var b)) sink(h, b);
        }
    }

    [Fact]
    public void A_recorded_template_an_item_overrides_the_missing_stage_of_is_kept()
    {
        var root = Path.Combine(Path.GetTempPath(), "scskiller-template-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var (a, b) = (Container("DXIL", "shader a, not here"), Container("DXIL", "shader b"));
        var template = new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, Sha(a)));
        var db = Path.Combine(root, "recording.db");
        using (var f = File.Create(db)) PsoDb.Write(f, template.Tag, template.Payload);   // recorded; its shader A is in no file here
        var item = new PsoDb.Rec('P', PsoDb.Item(template.Key, PsoDb.Zero, new Dictionary<int, string> { [(int)Stage.Compute] = Sha(b) }, null));
        var plan = new Plan("test", "c", "PCD3D_SM6", "nvidia-1", new PlanStats(0, 0, 0, 0, false), Path.Combine(root, "plan.bin"));
        PlanFile.Write(plan, [item]);
        var game = new Game("test:template", "test", Store.Other, root, Path.Combine(root, "game.exe"));
        var work = Path.Combine(root, "work");

        new Planner().Materialize(plan, game, Engine, new Serves(new() { [Sha(b)] = b }), new Recording(db), work, default);

        var records = PsoDb.Read(Path.Combine(work, "scskiller.db")).Concat(PsoDb.Read(Path.Combine(work, "scskiller_gen.db"))).ToList();
        Assert.Contains(records, r => r.Tag == 'P' && r.Key == item.Key);
        Assert.Contains(records, r => r.Key == template.Key);   // the item's template is in the db the warm loads
    }
}
