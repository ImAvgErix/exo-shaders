using System.Buffers.Binary;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Dagor;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;
using ZstdSharp;

namespace SCSKiller.Tests.Dagor;

/// <summary>A synthetic version 11.3 shader dump (the layout of War Thunder's compiledShaders\game.ps50.shdump.bin): a vertex
/// entry holding a VS and the GS it is drawn with, then a pixel and a compute entry, each a zstd frame of the dump's dictionary.</summary>
public class DagorReaderTests
{
    internal static readonly byte[] Vs = Hlsl.Compile("float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }", "main", "vs_5_0");
    internal static readonly byte[] Gs = Hlsl.Compile("""
        struct V { float4 p : SV_Position; };
        [maxvertexcount(3)] void main(triangle V i[3], inout TriangleStream<V> o) { for (int k = 0; k < 3; k++) o.Append(i[k]); }
        """, "main", "gs_5_0");
    internal static readonly byte[] Ps = Hlsl.Compile("""
        Texture2D t : register(t0); SamplerState s : register(s0);
        float4 main(float4 p : SV_Position) : SV_Target { return t.Sample(s, p.xy); }
        """, "main", "ps_5_0");
    internal static readonly byte[] Cs = Hlsl.Compile("RWBuffer<float> b : register(u0); [numthreads(8, 1, 1)] void main(uint i : SV_DispatchThreadID) { b[i] = i; }", "main", "cs_5_0");

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    /// <summary>The dump: header, the compressed body's size and list, the body (a ScriptedShadersBinDump with only the fields
    /// the reader uses), each entry compressed with the body's dictionary. <paramref name="metadata"/>: each entry's (DX11's
    /// 12 zero bytes by default); <paramref name="passes"/>: the (vprId, fshId) of one shader class's passes.</summary>
    internal static byte[] Dump(byte[][] entries, int vertexEntries, string version = "11.3", byte[][]? metadata = null, (int Vpr, int Fsh)[]? passes = null)
    {
        var dict = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();   // a raw-content dictionary
        using var c = new Compressor();
        c.LoadDictionary(dict);
        var frames = entries.Select(e => c.Wrap(e).ToArray()).ToList();
        var body = new MemoryStream();
        body.Write(new byte[204]);
        int Append(byte[] b)
        {
            var at = (int)body.Position;
            body.Write(b);
            body.Write(new byte[(4 - b.Length % 4) % 4]);
            return at;
        }
        var sizes = Append(entries.SelectMany(e => BitConverter.GetBytes(e.Length)).ToArray());
        var codes = Append(new byte[8 * entries.Length]);
        var at = frames.Select(Append).ToList();
        var dictAt = Append(dict);
        metadata ??= entries.Select(_ => new byte[12]).ToArray();
        var metas = Append(new byte[8 * entries.Length]);
        var metaAt = metadata.Select(Append).ToList();
        passes ??= [];
        // one ShaderClass (108 bytes, its codes first), one ShaderCode (72, its passes at 20), Pass records (20, a ShRef pointer first)
        var cls = Append(new byte[108]);
        var code = Append(new byte[72]);
        var pass = Append(new byte[20 * passes.Length]);
        var refs = passes.Select(p => Append([.. BitConverter.GetBytes((ushort)p.Vpr), .. BitConverter.GetBytes((ushort)p.Fsh), .. new byte[20]])).ToList();
        var b = body.ToArray();
        List(b, 140, cls, 1);
        List(b, cls, code, 1);
        List(b, code + 20, pass, passes.Length);
        for (var i = 0; i < passes.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(pass + 20 * i), refs[i] - (pass + 20 * i));
        List(b, 180, metas, entries.Length);
        for (var i = 0; i < metadata.Length; i++) List(b, metas + 8 * i, metaAt[i], metadata[i].Length);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), vertexEntries);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), entries.Length - vertexEntries);
        List(b, 172, sizes, entries.Length);
        List(b, 188, codes, entries.Length);
        for (var i = 0; i < frames.Count; i++) List(b, codes + 8 * i, at[i], frames[i].Length);
        List(b, 196, dictAt, dict.Length);
        var packed = new Compressor().Wrap(b).ToArray();
        var file = new byte[84 + packed.Length];
        "VSPSdump"u8.CopyTo(file);
        System.Text.Encoding.ASCII.GetBytes(version).CopyTo(file, 8);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(64), b.Length);
        List(file, 68, 84, packed.Length);
        packed.CopyTo(file, 84);
        return file;
    }

    static void List(byte[] b, int field, int to, int count)
    {
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(field), to - field);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(field + 4), count);
    }

    internal static Game Install(string name, byte[] dump, string? config, string file = "game.ps50.shdump.bin")
    {
        var dir = Ff7.TempDir(name);
        Directory.CreateDirectory(Path.Combine(dir, "compiledShaders"));
        File.WriteAllBytes(Path.Combine(dir, "compiledShaders", file), dump);
        if (config != null) File.WriteAllText(Path.Combine(dir, "config.blk"), config);
        return new Game("gaijin:test", "Test", Store.Other, dir, Path.Combine(dir, "win64", "aces.exe"));
    }

    [Fact]
    public void IndexesEveryShaderAndPairsAVertexEntrysStages()
    {
        var game = Install("dagor-index", Dump([[.. Vs, .. Gs], Ps, Cs], 1), "video{\r\n  driver:t=\"dx11\"\r\n}\r\n");
        var reader = new DagorReader();
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(DagorReader.Family, "11.3", DagorReader.CbvRangesFork, "D3D11", false, null), engine);
        var index = reader.Index(game, engine, null, CancellationToken.None);
        Assert.Equal([Stage.Vertex, Stage.Geometry, Stage.Pixel, Stage.Compute], index.Shaders.Values.Select(s => s.Stage));
        Assert.Equal([[Sha(Vs), Sha(Gs)], [Sha(Ps)], [Sha(Cs)]], index.Maps.Select(m => m.Shaders.ToList()));
        Assert.All(index.Maps, m => Assert.Equal(DagorReader.Platform, m.Platform));

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, new HashSet<string> { Sha(Gs), Sha(Cs), new('0', 40) }, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(Gs, got[Sha(Gs)]);
        Assert.Equal(Cs, got[Sha(Cs)]);
        Assert.Equal(2, got.Count);

        var check = new Planner().Check(game, engine, null, Ff7.Nvidia);
        Assert.Equal(Readiness.Ready, check.Readiness);
    }

    [Theory]
    [InlineData(null, "D3D11 or D3D12", "game.ps50.shdump.bin")]
    [InlineData("video{\n  driver:t=\"auto\"\n}", "D3D11 or D3D12", "game.ps50.shdump.bin")]
    [InlineData("graphics{\n  driver:t=\"dx11\"\n}\nvideo{\n  vsync:b=no\n  driver:t=\"dx12\"\n}", "D3D12", "game.ps50.shdump.bin")]
    [InlineData("video{\n  sub{\n    driver:t=\"dx11\"\n  }\n  driver:t=\"vulkan\"\n}", "Vulkan", "game.ps50.shdump.bin")]
    [InlineData("video{\n  compatibilityMode:b=yes\n  driver:t=\"dx11\"\n}", "D3D11", "game.compatibility.ps50.shdump.bin")]
    public void TheApiAndDumpComeFromConfigBlk(string? config, string api, string file)
    {
        var game = Install("dagor-config", Dump([Ps], 0), config, file);
        Assert.Equal(api, new DagorReader().Detect(game)?.GraphicsApi);
    }

    [Fact]
    public void TheIndexStampFollowsTheDumpCompatibilityModeSelects()
    {
        var game = Install("dagor-stamp", Dump([Ps], 0), "video{\n  compatibilityMode:b=no\n}");
        File.WriteAllBytes(Path.Combine(game.InstallDir, "compiledShaders", "game.compatibility.ps50.shdump.bin"), Dump([Cs], 0));
        var reader = new DagorReader();
        var normal = reader.IndexStamp(game);
        Assert.StartsWith("game.ps50.shdump.bin|", normal);
        File.WriteAllText(Path.Combine(game.InstallDir, "config.blk"), "video{\n  compatibilityMode:b=yes\n}");
        Assert.StartsWith("game.compatibility.ps50.shdump.bin|", reader.IndexStamp(game));
        Assert.Equal("", reader.IndexStamp(game with { InstallDir = Path.Combine(game.InstallDir, "none") }));
    }

    [Fact]
    public void AnotherDumpVersionIsUnsupportedAndABrokenDumpThrows()
    {
        var game = Install("dagor-version", Dump([Ps], 0, "11.2"), null);
        Assert.Contains("version 11.2", new DagorReader().Detect(game)!.Unsupported);
        Assert.Null(new DagorReader().Detect(Install("dagor-none", [1, 2, 3], null)));

        var dump = Dump([Ps, Cs], 0);
        Assert.Throws<InvalidDataException>(() => DagorReader.Entries(DagorReader.Body(dump[..^8]), CancellationToken.None).ToList());
        BinaryPrimitives.WriteInt32LittleEndian(dump.AsSpan(72), int.MaxValue);   // a body list past the end
        Assert.Throws<InvalidDataException>(() => DagorReader.Entries(DagorReader.Body(dump), CancellationToken.None).ToList());
    }

    [Fact]
    public void AGameListedWithAnotherExeThanBattlEyesLauncherStartsIsUnsupported()
    {
        var game = Install("dagor-exe", Dump([Ps], 0), null);
        Directory.CreateDirectory(Path.Combine(game.InstallDir, "BattlEye"));
        Directory.CreateDirectory(Path.Combine(game.InstallDir, "win64"));
        File.WriteAllBytes(game.ExePath, [0]);
        File.WriteAllText(Path.Combine(game.InstallDir, "BattlEye", "BELauncher.ini"), "[Launcher]\r\n64BitExe=win64\\aces.exe\r\n");
        Assert.Null(new DagorReader().Detect(game)!.Unsupported);
        var launcher = game with { ExePath = Path.Combine(game.InstallDir, "launcher.exe") };
        Assert.Contains(@"the game runs win64\aces.exe", new DagorReader().Detect(launcher)!.Unsupported);
    }

    [Fact]
    public void GaijinGamesStartTheExeBattlEyesLauncherNames()
    {
        var dir = Ff7.TempDir("gaijin-exe");
        Directory.CreateDirectory(Path.Combine(dir, "BattlEye"));
        Directory.CreateDirectory(Path.Combine(dir, "win64"));
        File.WriteAllBytes(Path.Combine(dir, "win64", "aces.exe"), [0]);
        var ini = Path.Combine(dir, "BattlEye", "BELauncher.ini");
        File.WriteAllText(ini, "[Launcher]\r\nGameID=wt\r\n32BitExe=win32\\aces.exe\r\n64BitExe=win64\\aces.exe\r\n");
        Assert.Equal(Path.Combine(dir, "win64", "aces.exe"), GaijinSource.Exe(dir));
        File.WriteAllText(ini, "[Launcher]\r\n64BitExe=..\\other\\aces.exe\r\n");
        Assert.Null(GaijinSource.Exe(dir));
        File.Delete(ini);
        Assert.Null(GaijinSource.Exe(dir));
    }
}
