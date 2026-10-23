using System.IO.Compression;
using System.Security.Cryptography;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Vendors;

/// <summary>
/// Microsoft's D3D12StateObjectCompiler (the Agility SDK's NuGet package, Microsoft.Direct3D.D3D12), fetched on this PC the
/// first time a local precompiled shader database is built, never bundled: whether its bin\ may be redistributed isn't
/// settled. The package is taken only at its pinned size and SHA-512 (nuget.org's packageHash), and the two files used from
/// it only at their pinned SHA-256, checked again at every use. The exe compiles a state object database with the GPU
/// driver's own compiler plugin; its DLL writes the database (D3D12CompilerGetInterface), so neither needs the package's
/// D3D12Core.dll nor Windows' developer mode.
/// </summary>
public static class AsdTools
{
    /// <summary>A package and the files used from its <c>build/native/bin/x64/</c> (name, SHA-256 lowercase hex).</summary>
    public sealed record Pin(string Version, string Url, long Size, string Sha512, IReadOnlyDictionary<string, string> Files);

    public const string Exe = "D3D12StateObjectCompiler.exe", Dll = "D3D12StateObjectCompiler.dll";

    /// <summary>1.721.3-preview: the first with register / list-registrations; 1.619.5 is the oldest with the compiler.</summary>
    public static readonly Pin Current = new("1.721.3-preview",
        "https://api.nuget.org/v3-flatcontainer/microsoft.direct3d.d3d12/1.721.3-preview/microsoft.direct3d.d3d12.1.721.3-preview.nupkg",
        35353560, "gjDoWFJUy6ukTpxkBGDSvLxAJdGaVEG2xTDOju9Byw2GoM4Z+FNFEIzsKV39CNp5PRmofto8AtIaP+MUL9wGcQ==",
        new Dictionary<string, string>
        {
            [Exe] = "5ebf1bb3f73a8dadeb2e9597e1ba6a84384275c3b0177637fc8462f2c1c18890",
            [Dll] = "6f927abbe0ebc071ac7668ece85ce069c99e688ecab6ae204a58a3359ab6f0bd",
        });

    const string Entry = "build/native/bin/x64/";

    public static string Dir => DirOverride ?? Path.Combine(AppStore.DefaultDir, "tools", "d3d12-" + Current.Version);
    // the tests' own folder: they never read the app's data folder
    internal static string? DirOverride;
    static readonly Lock Gate = new();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>The folder holding <see cref="Exe"/> and <see cref="Dll"/> at their pins, downloaded there if they aren't.
    /// Throws when the download fails or doesn't match.</summary>
    public static string Ensure() => Ensure(Dir, Current, Download);

    /// <summary><paramref name="download"/>(url, path): the package to a file, replaceable for tests.</summary>
    internal static string Ensure(string dir, Pin pin, Action<string, string> download)
    {
        lock (Gate)
        {
            if (Verified(dir, pin)) return dir;
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, $"package.{Guid.NewGuid():N}.download");   // per attempt: only bytes this attempt checked are used
            try
            {
                download(pin.Url, tmp);
                using (var f = File.OpenRead(tmp))
                    if (f.Length != pin.Size || Convert.ToBase64String(SHA512.HashData(f)) != pin.Sha512)
                        throw new InvalidDataException($"the D3D12 package from {pin.Url} isn't the expected build (size or SHA-512 mismatch)");
                using var zip = ZipFile.OpenRead(tmp);
                foreach (var (name, sha) in pin.Files)
                {
                    var entry = zip.GetEntry(Entry + name) ?? throw new InvalidDataException($"the D3D12 package has no {Entry}{name}");
                    var part = Path.Combine(dir, $"{name}.{Guid.NewGuid():N}.download");
                    try
                    {
                        entry.ExtractToFile(part);
                        if (Sha256(part) != sha) throw new InvalidDataException($"{name} in the D3D12 package isn't the expected build (SHA-256 mismatch)");
                        File.Move(part, Path.Combine(dir, name), overwrite: true);
                    }
                    finally { File.Delete(part); }
                }
                if (!Verified(dir, pin)) throw new InvalidDataException("the D3D12 compiler changed after it was verified");
                return dir;
            }
            finally { File.Delete(tmp); }
        }
    }

    /// <summary>An adapter the compiler lists (<c>list --adapters</c>): its index there, name, and the GPU family its
    /// driver's compiler plugin builds for; null family: the driver has no plugin, or no ABI support.</summary>
    public sealed record Adapter(int Index, string Name, string? Family);

    /// <summary>The adapters in <c>list --adapters</c>'s output.</summary>
    public static IReadOnlyList<Adapter> ParseAdapters(string output)
    {
        var list = new List<Adapter>();
        int index = -1;
        string name = "", family = "";
        bool compiler = false, supported = false;
        void Add() { if (index >= 0) list.Add(new(index, name, compiler && supported && family.Length > 0 ? family : null)); }
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 1 && line[^1] == ':' && int.TryParse(line[..^1], out var i))
            {
                Add();
                (index, name, family, compiler, supported) = (i, "", "", false, false);
                continue;
            }
            var colon = line.IndexOf(':');
            if (index < 0 || colon < 0) continue;
            var (key, value) = (line[..colon].Trim(), line[(colon + 1)..].Trim());
            switch (key)
            {
                case "Description": name = value; break;
                case "Compiler": compiler = value.Length > 0 && !value.StartsWith('<'); break;
                case "Feature Supported": supported = value == "<Supported>"; break;
                case "Adapter Family": family = value; break;
            }
        }
        Add();
        return list;
    }

    /// <summary>PSOs the compile built and the ones it couldn't (its "Create PSO: N (M failed)" line, N of N + M built); null when it printed none.</summary>
    public static (int Created, int Failed)? ParseCompiled(string output)
    {
        var m = System.Text.RegularExpressions.Regex.Match(output, @"Create PSO: (\d+) \((\d+) failed\)");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// <summary>Runs the compiler in <paramref name="dir"/> with <paramref name="args"/> at idle priority; its exit code and
    /// output. A cancel kills it. Its files are held open (no writes, no deletes) from their check to its exit.</summary>
    public static async Task<(int Exit, string Output)> Run(string dir, IEnumerable<string> args, CancellationToken ct = default)
    {
        var held = new List<FileStream>();
        try
        {
            foreach (var (name, sha) in Current.Files)
            {
                var f = new FileStream(Path.Combine(dir, name), FileMode.Open, FileAccess.Read, FileShare.Read);
                held.Add(f);
                if (Convert.ToHexStringLower(SHA256.HashData(f)) != sha) throw new InvalidDataException("the D3D12 compiler changed after it was verified");
            }
            return await Start(dir, args, ct);
        }
        finally { foreach (var f in held) f.Dispose(); }
    }

    static async Task<(int Exit, string Output)> Start(string dir, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(dir, Exe)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new IOException("the D3D12 compiler didn't start");
        try { p.PriorityClass = System.Diagnostics.ProcessPriorityClass.Idle; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        var output = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (p.ExitCode, await output + await error);
    }

    /// <summary>Every file of <paramref name="pin"/> in <paramref name="dir"/> at its SHA-256.</summary>
    public static bool Verified(string dir, Pin pin) => pin.Files.All(f => File.Exists(Path.Combine(dir, f.Key)) && Sha256(Path.Combine(dir, f.Key)) == f.Value);

    static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    static void Download(string url, string path)
    {
        try
        {
            using var r = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult().EnsureSuccessStatusCode();
            using var from = r.Content.ReadAsStream();
            using var to = File.Create(path);
            var buf = new byte[1 << 16];
            for (int n; (n = from.Read(buf)) > 0;)
            {
                to.Write(buf, 0, n);
                if (to.Length > Current.Size) throw new InvalidDataException($"{url} is larger than the pinned package");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new IOException($"downloading {url} failed: {e.Message}", e); }
    }
}
