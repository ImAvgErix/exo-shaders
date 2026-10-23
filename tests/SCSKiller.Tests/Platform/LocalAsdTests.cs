using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Vendors;
using SCSKiller.Tests.Carved;
using Xunit;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Tests.Platform;

/// <summary>
/// Exo as a local Advanced Shader Delivery installer. It fails if Microsoft's compiler is taken from a package or file
/// that isn't the pinned one, or a bad download leaves a file behind; if the compiler's adapter list or compile summary is
/// misread; if a work folder's PSO records and plan items don't all become the database's PSOs (or ones with an NVAPI
/// state or a missing shader do); or if a registration isn't listed, found by its exe, removed, or mistaken for a store's.
/// The GPU tests need the package's x64 bin folder in SCSKILLER_TEST_D3D12_TOOLS (they never download it) and return
/// early without it; each registration is a throwaway exe's, under its own installer name, and is removed.
/// </summary>
public sealed class LocalAsdTests
{
    static byte[] Package(params (string Name, byte[] Bytes)[] files)
    {
        var m = new MemoryStream();
        using (var zip = new ZipArchive(m, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in files)
                using (var s = zip.CreateEntry("build/native/bin/x64/" + name).Open()) s.Write(bytes);
        return m.ToArray();
    }

    static AsdTools.Pin PinOf(byte[] package, params (string Name, byte[] Bytes)[] files) =>
        new("test", "https://example.invalid/pkg.nupkg", package.Length, Convert.ToBase64String(SHA512.HashData(package)),
            files.ToDictionary(f => f.Name, f => Convert.ToHexStringLower(SHA256.HashData(f.Bytes))));

    [Fact]
    public void The_compiler_is_taken_only_from_its_pinned_package_and_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scskiller-asd-" + Guid.NewGuid().ToString("N"));
        try
        {
            (string, byte[])[] files = [(AsdTools.Exe, "exe"u8.ToArray()), (AsdTools.Dll, "dll"u8.ToArray())];
            var package = Package(files);
            var pin = PinOf(package, files);
            var downloads = 0;
            void Get(string url, string path) { downloads++; File.WriteAllBytes(path, package); }

            Assert.Equal(dir, AsdTools.Ensure(dir, pin, Get));
            Assert.True(AsdTools.Verified(dir, pin));
            Assert.Equal("dll", File.ReadAllText(Path.Combine(dir, AsdTools.Dll)));
            AsdTools.Ensure(dir, pin, Get);
            Assert.Equal(1, downloads);   // there and at its pins: not fetched again
            Assert.Equal([AsdTools.Dll, AsdTools.Exe], Directory.GetFiles(dir).Select(Path.GetFileName).Order());   // the package itself isn't kept

            File.WriteAllText(Path.Combine(dir, AsdTools.Exe), "changed");
            Assert.False(AsdTools.Verified(dir, pin));
            AsdTools.Ensure(dir, pin, Get);   // a changed file: fetched again
            Assert.Equal(2, downloads);

            var other = Path.Combine(dir, "other");
            Assert.Throws<InvalidDataException>(() => AsdTools.Ensure(other, pin with { Sha512 = Convert.ToBase64String(new byte[64]) }, Get));
            Assert.Throws<InvalidDataException>(() => AsdTools.Ensure(other, pin with { Size = package.Length + 1 }, Get));
            Assert.Throws<InvalidDataException>(() => AsdTools.Ensure(other, pin with { Files = new Dictionary<string, string> { [AsdTools.Exe] = new string('0', 64) } }, Get));
            Assert.Throws<InvalidDataException>(() => AsdTools.Ensure(other, pin with { Files = new Dictionary<string, string> { ["missing.dll"] = new string('0', 64) } }, Get));
            Assert.Empty(Directory.GetFiles(other));   // nothing a failed attempt fetched or extracted stays
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // D3D12StateObjectCompiler 1.721.3-preview, list --adapters, on an RTX 3070 with driver 621.01
    const string Adapters = """
        Available Adapters for replay by Index:
        0:
        	Description: NVIDIA GeForce RTX 3070
        	Adapter GUID: {C48C5D5B-CD79-11F1-AF2A-92F8294F09CF}
        	D3D12 UserMode Driver:
        		D3D12 Supported: <Supported>
        		Version: 32.0.16.2101
        		Compiler: C:\WINDOWS\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_20f49c6eb5c9b39b\nvd3d12scx.dll
        		CompilerWow: C:\WINDOWS\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_20f49c6eb5c9b39b\nvd3d12sc.dll
        	ABI Support:
        		Feature Supported: <Supported>
        		Adapter Family: NVIDIA_AMPERE_GA104
        		MinimumABISupportVersion: 1128466500354048
        		CompilerVersion: 4.597.38117.0
        1:
        	Description: Microsoft Basic Render Driver
        	D3D12 UserMode Driver:
        		Compiler: <Not Found>
        	ABI Support:
        		Feature Supported: <Not Supported>
        		Adapter Family:
        """;

    [Fact]
    public void Reads_the_compilers_adapters_and_compile_summary()
    {
        Assert.Equal([new(0, "NVIDIA GeForce RTX 3070", "NVIDIA_AMPERE_GA104"), new(1, "Microsoft Basic Render Driver", null)],
            AsdTools.ParseAdapters(Adapters.Replace("\n", "\r\n")));
        Assert.Equal((2, 0), AsdTools.ParseCompiled("Compiling pipeline states...\nDone.\nCreate PSO: 2 (0 failed)\nCreate State Object: 0 (0 failed)"));
        Assert.Equal((1200, 7), AsdTools.ParseCompiled("Create PSO: 1200 (7 failed)"));
        Assert.Null(AsdTools.ParseCompiled("Command Failed:\n\tHRESULT: 0x80070006"));
    }

    static string? Tools() => Environment.GetEnvironmentVariable("SCSKILLER_TEST_D3D12_TOOLS") is { Length: > 0 } d
        && File.Exists(Path.Combine(d, AsdTools.Exe)) && File.Exists(Path.Combine(d, AsdTools.Dll)) ? d : null;

    static string Sha1(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    /// <summary>A canonical 'G' payload (proxy.cpp io_gfx): D3D12's default states, a position + texcoord layout, one
    /// R8G8B8A8_UNORM target.</summary>
    static byte[] Graphics(string rs, string vs, string ps)
    {
        var m = new MemoryStream();
        var w = new BinaryWriter(m);
        foreach (var h in new[] { rs, vs, ps, Zero, Zero, Zero }) w.Write(Convert.FromHexString(h));
        w.Write(0); w.Write(0);   // blend: no alpha to coverage, no independent blend
        for (var i = 0; i < 8; i++) { w.Write(0); w.Write(0); w.Write(2); w.Write(1); w.Write(1); w.Write(2); w.Write(1); w.Write(1); w.Write(4); w.Write(0xF); }
        w.Write(uint.MaxValue);   // sample mask
        foreach (var v in new[] { 3, 3, 0, 0, 0, 0, 1, 0, 0, 0, 0 }) w.Write(v);   // solid, cull back, depth clip
        w.Write(0); w.Write(1); w.Write(2); w.Write(0); w.Write(0xFFFF);   // depth off, write all, less, no stencil, masks
        for (var i = 0; i < 2; i++) { w.Write(1); w.Write(1); w.Write(1); w.Write(8); }   // keep, keep, keep, always
        w.Write(2);
        foreach (var (name, format, offset) in new[] { ("POSITION", 6, 0), ("TEXCOORD", 16, 12) })
        {
            w.Write(name.Length); w.Write(Encoding.ASCII.GetBytes(name));
            w.Write(0); w.Write(format); w.Write(0); w.Write(offset); w.Write(0); w.Write(0);
        }
        w.Write(0); w.Write(3);   // no strip cut, triangles
        w.Write(1); w.Write(28); for (var i = 1; i < 8; i++) w.Write(0);   // R8G8B8A8_UNORM
        w.Write(0); w.Write(1); w.Write(0); w.Write(0); w.Write(0);   // no DSV, 1 sample, node 0, no flags
        return m.ToArray();
    }

    [Fact]
    [Trait("Needs", "Gpu")]
    public async Task A_work_folders_psos_and_plan_items_become_the_database_and_compile()
    {
        if (Tools() is not { } tools || GpuBackends.PrimaryAdapter() is not { } gpu) return;
        var dir = Path.Combine(Path.GetTempPath(), "scskiller-sodb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (rs, vs, ps, ps2, cs) = (Hlsl.RootSignature(Hlsl.Rs1), Hlsl.Vs(3, null), Hlsl.Ps(3, null), Hlsl.Ps(4, null), Hlsl.Cs(3));
            var csRs = Hlsl.RootSignature("DescriptorTable(UAV(u0))");
            var gfx = new Rec('G', Graphics(Sha1(rs), Sha1(vs), Sha1(ps)));
            var noShader = new Rec('G', Graphics(Sha1(rs), Sha1(vs), new string('1', 40)));
            var nvGfx = new Rec('G', Graphics(Sha1(rs), Sha1(vs), Sha1(ps2)));
            var main = Path.Combine(dir, "scskiller.db");
            var gen = Path.Combine(dir, "scskiller_gen.db");
            using (var f = File.Create(main))
            {
                foreach (var b in new[] { rs, vs, ps, ps2 }) WriteBlob(f, Sha1(b), b);
                Write(f, gfx.Tag, gfx.Payload);
                Write(f, noShader.Tag, noShader.Payload);
                Write(f, nvGfx.Tag, nvGfx.Payload);
                Write(f, 'N', new NvState(nvGfx.Key, 0, 0, 0, 0).ToRec().Payload);
            }
            LayoutElem[] layout = [new("POSITION", 0, 6, 0), new("TEXCOORD", 0, 16, 12)];
            var stream = new Rec('S', Stream(Sha1(rs), new Dictionary<int, string> { [1] = Sha1(vs), [2] = Sha1(ps) }, layout, 3, [28], 0));
            var views = new Rec('S', Stream(Sha1(rs), new Dictionary<int, string> { [1] = Sha1(vs), [2] = Sha1(ps) }, layout, 3, [28], 0,
                viewInstancing: ViewInstancingBytes([(0, 0), (1, 0)], 0)));
            using (var f = File.Create(gen))
            {
                foreach (var b in new[] { cs, csRs }) WriteBlob(f, Sha1(b), b);
                Write(f, 'C', Compute(Sha1(csRs), Sha1(cs)));
                Write(f, 'P', Item(gfx.Key, Zero, new Dictionary<int, string> { [2] = Sha1(ps2) }, null));   // the template with another PS
                Write(f, gfx.Tag, gfx.Payload);   // in both files: stored once
                Write(f, stream.Tag, stream.Payload);
                Write(f, views.Tag, views.Payload);
                Write(f, 'P', Item(stream.Key, Zero, new Dictionary<int, string> { [2] = Sha1(ps2) }, [layout[0], layout[1] with { Offset = 16 }]));   // another PS and layout
            }
            var sodb = Path.Combine(dir, "game.sodb");
            var r = Sodb.Write(Path.Combine(tools, AsdTools.Dll), sodb, "asdtest.exe", "ASD test", [main, gen]);
            Assert.Equal(new Sodb.Result(6, 1), r);   // G, C, both S and both P; the missing shader's skipped, the NVAPI one left out
            Assert.False(File.Exists(sodb + "-wal"));
            var (_, identity) = await AsdTools.Run(tools, ["get-identity", sodb]);
            Assert.Contains("Exe Filename: asdtest.exe", identity);
            if (AsdTools.ParseAdapters((await AsdTools.Run(tools, ["list", "--adapters"])).Output).FirstOrDefault(a => a.Family != null && a.Name == gpu.Name) is not { } adapter) return;
            var (exit, output) = await AsdTools.Run(tools, ["compile", "--adapter", adapter.Index.ToString(), "--priority", "idle", "--psos", sodb, Path.Combine(dir, "game.psdb")]);
            Assert.Equal(0, exit);
            // built but the view-instanced one: NVIDIA's plugin (driver 621.01) refuses view instancing with these SM 5.1 shaders
            Assert.True(AsdTools.ParseCompiled(output) is ( >= 5, <= 1) and (var built, var refused) && built + refused == 6, output);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    [Trait("Needs", "Gpu")]
    public void A_registration_is_listed_found_by_its_exe_and_removed()
    {
        if (Tools() == null || !AsdInstaller.Present() || GpuBackends.PrimaryAdapter() is not { } gpu) return;
        var installer = "ExoTest-" + Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), "scskiller-asdreg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "asdtest.exe");
        File.WriteAllBytes(exe, []);
        var (sodb, psdb) = (Path.Combine(dir, "game.sodb"), Path.Combine(dir, "game.psdb"));
        File.WriteAllBytes(sodb, []);
        File.WriteAllBytes(psdb, []);
        try
        {
            AsdInstaller.Register(exe, "ASD test", sodb, "TEST_FAMILY", psdb, installer);
            AsdInstaller.Register(exe, "ASD test", sodb, "TEST_FAMILY", psdb, installer);   // again: replaced, not added
            var r = Assert.Single(AsdInstaller.List(installer));
            Assert.Equal((exe, "ASD test", sodb), (r.ExePath, r.Name, r.Sodb));
            Assert.Equal([("TEST_FAMILY", psdb)], r.Psdbs);
            Assert.True(AsdInstaller.OthersCover(exe, "TEST_FAMILY"));   // another installer's, to Exo
            Assert.False(AsdInstaller.OthersCover(exe, "OTHER_FAMILY"));
            Assert.True(AsdInstaller.Remove(exe, installer));
            Assert.False(AsdInstaller.Remove(exe, installer));
            Assert.Empty(AsdInstaller.List(installer));
            Assert.False(AsdInstaller.OthersCover(exe, "TEST_FAMILY"));
        }
        finally
        {
            AsdInstaller.RemoveAll(installer);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A store's database for this adapter's family covers the game (no warm, no database of Exo's); Exo's own
    /// for the same family doesn't.</summary>
    [Fact]
    [Trait("Needs", "Gpu")]
    public async Task Exos_own_registration_is_not_taken_for_a_stores()
    {
        if (Tools() is not { } tools || !AsdInstaller.Present() || GpuBackends.PrimaryAdapter() is not { } gpu) return;
        if (AsdTools.ParseAdapters((await AsdTools.Run(tools, ["list", "--adapters"])).Output).FirstOrDefault(a => a.Family != null && a.Name == gpu.Name) is not { Family: { } family }) return;
        var store = "ExoTest-" + Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), "scskiller-asdcover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var (stores, exos, psdb) = (Path.Combine(dir, "store.exe"), Path.Combine(dir, "exo.exe"), Path.Combine(dir, "game.psdb"));
        foreach (var f in new[] { stores, exos, psdb }) File.WriteAllBytes(f, []);
        try
        {
            AsdInstaller.Register(stores, "ASD store test", "", family, psdb, store);
            AsdInstaller.Register(exos, "ASD exo test", "", family, psdb);
            Assert.True(ShaderDelivery.Covers(stores, gpu));
            Assert.False(ShaderDelivery.Covers(exos, gpu));
            Assert.False(AsdInstaller.OthersCover(exos, family));
        }
        finally
        {
            AsdInstaller.Remove(exos);
            AsdInstaller.RemoveAll(store);
            Directory.Delete(dir, true);
        }
    }

    // Windows allows one installer per name in a process: with the driver-update listener holding Exo's, a refresh
    // that made its own failed with ERROR_ALREADY_EXISTS. Calls now go through the listener's.
    [Fact]
    public void A_listening_installer_still_lists_and_removes()
    {
        if (!AsdInstaller.Present()) return;
        const string installer = "Exo Shaders test listener";
        using (AsdInstaller.Listen(() => { }, installer))
        {
            Assert.Empty(AsdInstaller.List(installer));
            Assert.False(AsdInstaller.Remove(@"C:
othing\here.exe", installer));
        }
        Assert.Empty(AsdInstaller.List(installer));   // and alone again once it stops
    }
}
