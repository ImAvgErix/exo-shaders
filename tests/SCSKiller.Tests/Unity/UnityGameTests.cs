using System.Diagnostics;
using System.Runtime.InteropServices;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unity;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Unity;

/// <summary>The Unity reader on installed games (read-only; each skipped when absent): detect, index, check, and an
/// offline NVIDIA plan into a temp folder (no warm).</summary>
[Trait("Needs", "Game")]
public class UnityGameTests(ITestOutputHelper output)
{
    static Game G(string id, string name, Store store, string dir, string exe) => new(id, name, store, dir, Path.Combine(dir, exe));

    public static readonly Dictionary<string, Game> Games = new()
    {
        ["Morta"] = G("steam:330020", "Children of Morta", Store.Steam, TestEnv.GameDir(@"ChildrenOfMorta"), "ChildrenOfMorta.exe"),
        ["Cocoon"] = G("xbox:Cocoon", "Cocoon", Store.Xbox, TestEnv.GameDir(@"Cocoon\Content"), "universe.exe"),
        ["Lamb"] = G("steam:1313140", "Cult of the Lamb", Store.Steam, TestEnv.GameDir(@"Cult of the Lamb"), "Cult Of The Lamb.exe"),
        ["LisBtS"] = G("steam:554620", "Life is Strange: Before the Storm Remastered", Store.Steam, TestEnv.GameDir(@"LIS - Before the Storm Remastered"), "Life is Strange - Before the Storm.exe"),
        ["EternalDie"] = G("steam:2426030", "Lost in Random: The Eternal Die Demo", Store.Steam, TestEnv.GameDir(@"TheEternalDieDemo"), "TheEternalDieDemo.exe"),
        ["Valheim"] = G("steam:892970", "Valheim", Store.Steam, TestEnv.GameDir(@"Valheim"), "valheim.exe"),
        ["Golf"] = G("steam:golf", "Normal Golf Game Demo", Store.Steam, TestEnv.GameDir(@"Normal Golf Game Demo"), @"Normal\Normal Golf Game.exe"),
        ["Office"] = G("steam:office", "Office Leveling Demo", Store.Steam, TestEnv.GameDir(@"Office Leveling Demo"), "Office Leveling.exe"),
    };

    [Theory]
    [InlineData("Morta")]
    [InlineData("Cocoon")]
    [InlineData("Lamb")]
    [InlineData("LisBtS")]
    [InlineData("EternalDie")]
    [InlineData("Valheim")]
    [InlineData("Golf")]
    [InlineData("Office")]
    public void DetectsAndIndexesInstalledGame(string key)
    {
        var game = Games[key];
        if (!Directory.Exists(game.InstallDir)) return;
        var reader = new UnityReader();
        var sw = Stopwatch.StartNew();
        var engine = reader.Detect(game) ?? throw new Xunit.Sdk.XunitException($"{game.InstallDir}: not detected as Unity");
        var detect = sw.Elapsed.TotalSeconds;
        var check = new Planner().Check(game, engine, null, Ff7.Nvidia);
        output.WriteLine($"{game.Name}: detect {detect:F2}s: {engine}; check: {check}");
        Assert.Null(engine.Unsupported);

        sw.Restart();
        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"  index {sw.Elapsed.TotalSeconds:F1}s: {index.Shaders.Count} shaders, models "
            + string.Join(", ", index.Shaders.Values.GroupBy(s => s.ShaderModel).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")));
        Assert.True(index.Shaders.Count > 0);

        // ReadShaders serves the indexed bytes back (a sample), and the D3D11 runtime takes them (WARP: CPU only, no GPU cache)
        var want = index.Shaders.Keys.Where((_, i) => i % 23 == 0).ToHashSet();
        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, want, (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(want.Count, got.Count);
        var rejected = got.Where(g => !Warp11.Creates(index.Shaders[g.Key].Stage, g.Value)).Select(g => $"{index.Shaders[g.Key].Stage} {g.Key}").ToList();
        output.WriteLine($"  WARP D3D11: {got.Count - rejected.Count}/{got.Count} created{(rejected.Count > 0 ? ": rejected " + string.Join(", ", rejected.Take(5)) : "")}");
        Assert.Empty(rejected);
        var (sha, bytes) = got.First(g => index.Shaders[g.Key].Stage == Stage.Pixel);
        var bad = bytes.ToArray();
        bad[^8] ^= 0x55; // negative control: the container checksum no longer matches
        Assert.False(Warp11.Creates(Stage.Pixel, bad), sha);

        if (check.Readiness != Readiness.Ready) return;
        var dir = Ff7.TempDir($"unity-{key}");
        sw.Restart();
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia, dir, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"  plan: {plan.Stats} ({sw.Elapsed.TotalSeconds:F1}s)");
        Assert.True(plan.Stats.D3D11Shaders + plan.Stats.Generated > 0);
        Directory.Delete(dir, true);
    }
}

/// <summary>A D3D11 WARP device (software: no GPU, no driver cache) that says whether the runtime accepts a shader's bytecode
/// (container checksum, program validity).</summary>
static unsafe class Warp11
{
    static readonly nint Device = Create();

    static nint Create()
    {
        var lib = NativeLibrary.Load("d3d11.dll");
        var create = (delegate* unmanaged<nint, int, nint, uint, int*, uint, uint, nint*, int*, nint*, int>)NativeLibrary.GetExport(lib, "D3D11CreateDevice");
        int level = 0xb000;
        nint dev, ctx;
        int got;
        var hr = create(0, 5 /* D3D_DRIVER_TYPE_WARP */, 0, 0, &level, 1, 7 /* D3D11_SDK_VERSION */, &dev, &got, &ctx);
        if (hr < 0) throw new InvalidOperationException($"D3D11CreateDevice(WARP) 0x{hr:x8}");
        return dev;
    }

    /// <summary>ID3D11Device::Create{Vertex,Geometry,Pixel,Hull,Domain,Compute}Shader (vtable 12, 13, 15, 16, 17, 18).</summary>
    public static bool Creates(Stage stage, byte[] code)
    {
        var slot = stage switch { Stage.Vertex => 12, Stage.Geometry => 13, Stage.Pixel => 15, Stage.Hull => 16, Stage.Domain => 17, Stage.Compute => 18, _ => -1 };
        if (slot < 0) return false;
        nint shader = 0;
        int hr;
        fixed (byte* p = code)
            hr = ((delegate* unmanaged<nint, void*, nuint, nint, nint*, int>)(*(nint**)Device)[slot])(Device, p, (nuint)code.Length, 0, &shader);
        if (shader != 0) ((delegate* unmanaged<nint, uint>)(*(nint**)shader)[2])(shader);
        return hr >= 0;
    }
}
