using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SCSKiller.Core.Vendors;

/// <summary>
/// Windows' Advanced Shader Delivery, read only. A store's installer (Gaming Services) registers a game's exe with
/// precompiled shader databases (PSDBs) built for each GPU family, and D3D12 uses them when the game creates its
/// pipelines (Windows SDK 10.0.26100 d3dshadercacheregistration.h; registrations live under
/// HKLM\Software\Microsoft\DirectX\ShaderCache). A game whose exe has a PSDB for this adapter's family needs no warm:
/// compiling it again would only fill the driver's cache with copies (the warm runs from a staged exe path, which the
/// PSDB isn't registered for). Nothing here registers or removes anything.
/// </summary>
public static class ShaderDelivery
{
    /// <summary>The status reason of a game it covers (ScsKiller); the page shows it as precompiled.</summary>
    public const string Reason = "Windows installs its shaders already compiled for this graphics card";

    /// <summary>NVIDIA's first driver with it, on RTX cards (Microsoft, October 21, 2026).</summary>
    public static readonly Version NvidiaDriver = new(621, 1);

    /// <summary>The Gaming Services version it needs, which delivers and registers the databases.</summary>
    public static readonly Version GamingServices = new(37, 113, 11003, 0);

    /// <summary>Whether this PC can take it, and the one thing missing when it can't.</summary>
    public sealed record Readiness(bool Works, string Text);

    static readonly ConcurrentDictionary<string, (bool Covered, long At)> Covered = new(StringComparer.OrdinalIgnoreCase);
    static string _coveredFor = "";

    /// <summary>
    /// What this PC needs for it: Windows' API, Gaming Services (no store app), on NVIDIA an RTX card with driver
    /// 621.01 or newer. A PC with all of them and nothing delivered yet works: a game installed from the Microsoft Store
    /// or the Xbox app gets its databases with it.
    /// </summary>
    public static Readiness Check(GpuInfo gpu) => Check(gpu, ApiPresent(), GamingServicesVersion());

    /// <summary><see cref="Check(GpuInfo)"/> from what was read: Windows' API there, Gaming Services' version (null: none).</summary>
    public static Readiness Check(GpuInfo gpu, bool api, Version? services)
    {
        if (!api) return new(false, "Needs a newer Windows 11");
        if (services is null) return new(false, "Needs Gaming Services from the Microsoft Store");
        if (services < GamingServices) return new(false, $"Needs Gaming Services {GamingServices} or newer");
        if (gpu.Vendor == GpuVendor.Nvidia)
        {
            if (!gpu.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase)) return new(false, "Not on GTX graphics cards");
            // NVIDIA's own number ("621.01"), also from the Windows form DXGI reports ("32.0.16.2101")
            if (Version.TryParse(NvidiaBackend.FromUserModeVersion(gpu.DriverVersion) ?? gpu.DriverVersion, out var driver) && driver < NvidiaDriver)
                return new(false, $"Needs NVIDIA driver {NvidiaDriver.Major}.{NvidiaDriver.Minor:00} or newer");
        }
        return new(true, "On");
    }

    /// <summary>
    /// True when Windows holds a database for this exe built for the adapter's GPU family, as its driver reports it now.
    /// Cached per exe for ten minutes (a game installed meanwhile gets its database with it), and until the adapter or its
    /// driver changes.
    /// </summary>
    public static bool Covers(string exePath, GpuInfo gpu)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        var key = gpu.AdapterLuid.ToString(CultureInfo.InvariantCulture) + "|" + gpu.DriverVersion;
        if (_coveredFor != key) { Covered.Clear(); _coveredFor = key; }
        if (Covered.TryGetValue(exePath, out var known) && Environment.TickCount64 - known.At < 600_000) return known.Covered;
        var covered = Probe(exePath, gpu.AdapterLuid);
        Covered[exePath] = (covered, Environment.TickCount64);
        return covered;
    }

    static bool ApiPresent()
    {
        try
        {
            var clsid = ClsidFactory; var iid = IidFactory;
            unsafe
            {
                if (D3D12GetInterface(&clsid, &iid, out var factory) < 0) return false;
                Release(factory);
                return true;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>Gaming Services' version from its service's image path (WindowsApps\Microsoft.GamingServices_{version}_...).</summary>
    public static Version? GamingServicesVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GamingServices");
            return key?.GetValue("ImagePath") is string image ? VersionInPath(image) : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>The version in a Gaming Services image path ("...\Microsoft.GamingServices_37.113.11003.0_x64__...\x.exe"), 0.0 when it names none.</summary>
    public static Version VersionInPath(string image)
    {
        var at = image.IndexOf("Microsoft.GamingServices_", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return new Version(0, 0);
        var rest = image[(at + "Microsoft.GamingServices_".Length)..];
        var end = rest.IndexOf('_');
        return Version.TryParse(end > 0 ? rest[..end] : rest, out var v) ? v : new Version(0, 0);
    }

    static unsafe bool Probe(string exePath, long luid)
    {
        nint factory = 0, dxgi = 0, adapter = 0, explorer = 0, app = 0;
        try
        {
            var clsid = ClsidFactory; var iid = IidFactory;
            if (D3D12GetInterface(&clsid, &iid, out factory) < 0) return false;
            var dxgiIid = IidDxgiFactory4;
            if (CreateDXGIFactory1(&dxgiIid, out dxgi) < 0) return false;
            var adapterIid = IidDxgiAdapter;
            // IDXGIFactory4::EnumAdapterByLuid
            if (((delegate* unmanaged<nint, long, Guid*, nint*, int>)Vt(dxgi)[26])(dxgi, luid, &adapterIid, &adapter) < 0) return false;
            // ID3DShaderCacheInstallerFactory::CreateExplorer: fails (ERROR_FILE_NOT_FOUND) while nothing was ever registered
            var explorerIid = IidExplorer;
            if (((delegate* unmanaged<nint, nint, Guid*, nint*, int>)Vt(factory)[4])(factory, adapter, &explorerIid, &explorer) < 0) return false;
            var appIid = IidApplication;
            fixed (char* path = exePath)
                if (((delegate* unmanaged<nint, char*, Guid*, nint*, int>)Vt(explorer)[3])(explorer, path, &appIid, &app) < 0) return false;
            // Exo's own (AsdInstaller) isn't a store's: the game is still warmed, and Exo's database rebuilt as it needs
            char* installer = null;
            if (((delegate* unmanaged<nint, char**, int>)Vt(app)[11])(app, &installer) >= 0 && installer != null && new string(installer) == AsdInstaller.Exo) return false;

            // The GPU families this adapter's driver compiles for (none: it can't use a database).
            var count = ((delegate* unmanaged<nint, uint, uint>)Vt(app)[9])(app, 0);
            if (count is 0 or > 16) return false;
            var targets = stackalloc CompilerProperties[(int)count];
            if (((delegate* unmanaged<nint, uint, CompilerProperties*, uint, int>)Vt(app)[10])(app, count, targets, 0) < 0) return false;
            var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++) families.Add(new string(targets[i].AdapterFamily));

            var components = ((delegate* unmanaged<nint, uint>)Vt(app)[7])(app);
            var componentIid = IidComponent;
            var psdbs = stackalloc PsdbProperties[64];
            for (uint c = 0; c < Math.Min(components, 64u); c++)
            {
                nint component = 0;
                if (((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Vt(app)[8])(app, c, &componentIid, &component) < 0) continue;
                try
                {
                    var n = ((delegate* unmanaged<nint, uint>)Vt(component)[6])(component);
                    if (n is 0 or > 64) continue;
                    if (((delegate* unmanaged<nint, uint, PsdbProperties*, int>)Vt(component)[7])(component, n, psdbs) < 0) continue;
                    for (var i = 0; i < n; i++)
                    {
                        var family = Marshal.PtrToStringUni(psdbs[i].AdapterFamily);
                        var file = Marshal.PtrToStringUni(psdbs[i].PsdbPath);
                        if (family != null && families.Contains(family) && file != null && File.Exists(file)) return true;
                    }
                }
                finally { Release(component); }
            }
            return false;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
        finally { Release(app); Release(explorer); Release(adapter); Release(dxgi); Release(factory); }
    }

    static unsafe nint* Vt(nint com) => *(nint**)com;
    static unsafe void Release(nint com) { if (com != 0) ((delegate* unmanaged<nint, uint>)Vt(com)[2])(com); }

    static readonly Guid ClsidFactory = new("16195a0b-607c-41f1-bf03-c7694d60a8d4");
    static readonly Guid IidFactory = new("09b2dfe4-840f-401a-804c-0dd8aadc9e9f");
    static readonly Guid IidExplorer = new("90432322-32f5-487f-9264-e9390fa58b2a");
    static readonly Guid IidApplication = new("fc688ee2-1b35-4913-93be-1ca3fa7df39e");
    static readonly Guid IidComponent = new("eed1bf00-f5c7-4cf7-885c-d0f9c0cb4828");
    static readonly Guid IidDxgiFactory4 = new("1bc6ea02-ef36-464f-bf0c-21ca39e5168a");
    static readonly Guid IidDxgiAdapter = new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct CompilerProperties
    {
        public fixed char AdapterFamily[128];
        public ulong MinimumAbi, MaximumAbi, CompilerVersion, ApplicationProfileVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PsdbProperties
    {
        public nint AdapterFamily, PsdbPath;
    }

    [DllImport("d3d12.dll")] static extern unsafe int D3D12GetInterface(Guid* clsid, Guid* iid, out nint ppv);
    [DllImport("dxgi.dll")] static extern unsafe int CreateDXGIFactory1(Guid* riid, out nint factory);
}
