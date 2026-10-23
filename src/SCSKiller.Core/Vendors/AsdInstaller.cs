using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SCSKiller.Core.Vendors;

/// <summary>
/// The installer side of Windows' shader cache registration (Windows SDK 10.0.26100 d3dshadercacheregistration.h), in user
/// scope: a precompiled shader database (PSDB) registered for an exe path is what D3D12 loads when that exe creates its
/// pipelines, with no change to the game. Measured on this API with an unsigned process under a standard user's token
/// (admin group deny-only, medium integrity): it registers, and a test exe created with Windows' own D3D12 (SDK 616,
/// no Agility SDK) then took its PSOs from the database. Registrations live under
/// HKCU\Software\Microsoft\DirectX\ShaderCache\&lt;installer&gt;\&lt;application name&gt;, written in a registry
/// transaction. <see cref="ShaderDelivery"/> is the reading side.
/// </summary>
public static unsafe class AsdInstaller
{
    /// <summary>The installer name Exo registers under.</summary>
    public const string Exo = "Exo";

    // Windows allows one installer per name in a process (another fails with ERROR_ALREADY_EXISTS): while the listener
    // holds Exo's, every call goes through it.
    static readonly object Gate = new();
    static Installer? _listening;

    sealed class Lease(Installer installer, bool owned) : IDisposable
    {
        public Installer Installer { get; } = installer;
        public void Dispose() { if (owned) Installer.Dispose(); }
    }

    static Lease Take(string name)
    {
        lock (Gate) if (_listening is { } live && live.Name == name) return new(live, false);
        return new(new Installer(name), true);
    }

    public sealed record Registration(string ExePath, string Name, string? Sodb, IReadOnlyList<(string Family, string Psdb)> Psdbs);

    /// <summary>Whether Windows has the registration API (Windows 11 24H2 and newer).</summary>
    public static bool Present()
    {
        try
        {
            var clsid = ClsidFactory; var iid = IidFactory;
            if (D3D12GetInterface(&clsid, &iid, out var f) < 0) return false;
            Release(f);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary><paramref name="installer"/>'s registrations.</summary>
    public static IReadOnlyList<Registration> List(string installer = Exo)
    {
        using var lease = Take(installer);
        var i = lease.Installer;
        var all = new List<Registration>();
        foreach (var app in i.Apps()) { all.Add(Read(app)); Release(app); }
        return all;
    }

    /// <summary>Registers <paramref name="psdb"/> (for <paramref name="family"/>) and <paramref name="sodb"/> for
    /// <paramref name="exePath"/>, replacing what <paramref name="installer"/> had registered for it.</summary>
    public static void Register(string exePath, string name, string sodb, string family, string psdb, string installer = Exo)
    {
        Remove(exePath, installer);
        using var lease = Take(installer);
        var i = lease.Installer;
        nint app = 0, component = 0;
        try
        {
            fixed (char* exe = exePath) fixed (char* file = Path.GetFileName(exePath)) fixed (char* n = name)
            fixed (char* comp = "default") fixed (char* s = sodb) fixed (char* fam = family) fixed (char* p = psdb)
            {
                var desc = new AppDesc { Exe = file, Name = n };
                var appIid = IidApplication;
                Check(((delegate* unmanaged<nint, char*, AppDesc*, Guid*, nint*, int>)Vt(i.Ptr)[7])(i.Ptr, exe, &desc, &appIid, &app), "RegisterApplication");
                var props = new PsdbProperties { Family = fam, Path = p };
                var compIid = IidComponent;
                Check(((delegate* unmanaged<nint, char*, char*, uint, PsdbProperties*, Guid*, nint*, int>)Vt(app)[5])(app, comp, s, 1, &props, &compIid, &component), "RegisterComponent");
            }
        }
        finally { Release(component); Release(app); }
    }

    /// <summary>Takes out what <paramref name="installer"/> registered for <paramref name="exePath"/>; false: there was none.</summary>
    public static bool Remove(string exePath, string installer = Exo)
    {
        var any = false;
        using var lease = Take(installer);
        var i = lease.Installer;
        foreach (var app in i.Apps())
        {
            try
            {
                if (string.Equals(Str(app, 3), exePath, StringComparison.OrdinalIgnoreCase))
                {
                    Check(((delegate* unmanaged<nint, nint, int>)Vt(i.Ptr)[8])(i.Ptr, app), "RemoveApplication");
                    any = true;
                }
            }
            finally { Release(app); }
        }
        return any;
    }

    /// <summary>Everything <paramref name="installer"/> registered (Exo Shaders' uninstall).</summary>
    public static void RemoveAll(string installer = Exo)
    {
        using var lease = Take(installer);
        var i = lease.Installer;
        Check(((delegate* unmanaged<nint, int>)Vt(i.Ptr)[11])(i.Ptr), "ClearAllState");
    }

    /// <summary>Calls <paramref name="onUpdate"/> (on the thread pool) whenever Windows reports a GPU driver update, until
    /// disposed: the API's own listener, which lives as long as its installer.</summary>
    public static IDisposable Listen(Action onUpdate, string installer = Exo)
    {
        var i = new Installer(installer, onUpdate);
        try
        {
            Check(((delegate* unmanaged<nint, int>)Vt(i.Ptr)[3])(i.Ptr), "RegisterDriverUpdateListener");
            lock (Gate) _listening = i;
            return i;
        }
        catch { i.Dispose(); throw; }
    }

    /// <summary>True when another installer (a store's, in system or user scope) registered a database for
    /// <paramref name="exePath"/> built for <paramref name="family"/> whose file is there: Exo never registers over one.
    /// Read from the registry, where the explorer API returns a single application per exe.</summary>
    public static bool OthersCover(string exePath, string family)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            try
            {
                using var root = hive.OpenSubKey(@"Software\Microsoft\DirectX\ShaderCache");
                if (root == null) continue;
                foreach (var inst in root.GetSubKeyNames())
                {
                    if (hive == Registry.CurrentUser && inst.Equals(Exo, StringComparison.OrdinalIgnoreCase)) continue;
                    using var installerKey = root.OpenSubKey(inst);
                    foreach (var appName in installerKey?.GetSubKeyNames() ?? [])
                    {
                        using var app = installerKey!.OpenSubKey(appName);
                        if (app?.GetValue("ExePath") is not string exe || !string.Equals(exe, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                        using var comps = app.OpenSubKey("Components");
                        foreach (var c in comps?.GetSubKeyNames() ?? [])
                        {
                            using var fam = comps!.OpenSubKey($@"{c}\AdapterFamilies\{family}");
                            if (fam?.GetValue("PSDBPath") is string p && File.Exists(p)) return true;
                        }
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return false;
    }

    /// <summary>
    /// A store's state object database for the same game installed elsewhere: another installer registered an exe of the
    /// same file name (not <paramref name="exePath"/> itself) for an application of the same name, and its SODB is there.
    /// The SODB is driver- and path-independent (every pipeline the game builds, by content), so Exo can build this PC's
    /// database from it for this copy. Both names must agree: games share generic exe names. Null when none.
    /// </summary>
    public static string? StoreSodbFor(string exePath, string gameName)
    {
        var file = Path.GetFileName(exePath);
        var name = Plain(gameName);
        if (file.Length == 0 || name.Length == 0) return null;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            try
            {
                using var root = hive.OpenSubKey(@"Software\Microsoft\DirectX\ShaderCache");
                if (root == null) continue;
                foreach (var inst in root.GetSubKeyNames())
                {
                    if (hive == Registry.CurrentUser && inst.Equals(Exo, StringComparison.OrdinalIgnoreCase)) continue;
                    using var installerKey = root.OpenSubKey(inst);
                    foreach (var appName in installerKey?.GetSubKeyNames() ?? [])
                    {
                        using var app = installerKey!.OpenSubKey(appName);
                        var exe = app?.GetValue("ExePath") as string;
                        var exeFile = app?.GetValue("ExeFilename") as string ?? Path.GetFileName(exe ?? "");
                        if (exe == null || string.Equals(exe, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.Equals(exeFile, file, StringComparison.OrdinalIgnoreCase)) continue;
                        var theirs = Plain(app!.GetValue("Name") as string ?? appName);
                        if (theirs.Length == 0 || !(theirs.Contains(name) || name.Contains(theirs))) continue;
                        using var comps = app.OpenSubKey("Components");
                        foreach (var c in comps?.GetSubKeyNames() ?? [])
                        {
                            using var comp = comps!.OpenSubKey(c);
                            if (comp?.GetValue("SODBPath") is string sodb && File.Exists(sodb)) return sodb;
                        }
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    /// <summary>A game's name as letters and digits only, lowercase ("Call of Duty®: Black Ops 7" and "Call Of Duty Black
    /// Ops 7" read the same).</summary>
    static string Plain(string text) => new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    static Registration Read(nint app)
    {
        var exe = Str(app, 3) ?? "";
        AppDesc d = default;
        var name = ((delegate* unmanaged<nint, AppDesc*, int>)Vt(app)[4])(app, &d) >= 0 && d.Name != null ? new string(d.Name) : "";
        string? sodb = null;
        var psdbs = new List<(string, string)>();
        var count = ((delegate* unmanaged<nint, uint>)Vt(app)[7])(app);
        var compIid = IidComponent;
        var props = stackalloc PsdbProperties[16];
        for (uint c = 0; c < Math.Min(count, 64u); c++)
        {
            nint comp = 0;
            if (((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Vt(app)[8])(app, c, &compIid, &comp) < 0) continue;
            try
            {
                sodb ??= Str(comp, 4);
                var n = Math.Min(((delegate* unmanaged<nint, uint>)Vt(comp)[6])(comp), 16u);
                if (n > 0 && ((delegate* unmanaged<nint, uint, PsdbProperties*, int>)Vt(comp)[7])(comp, n, props) >= 0)
                    for (var k = 0; k < n; k++) psdbs.Add((new string(props[k].Family), new string(props[k].Path)));
            }
            finally { Release(comp); }
        }
        return new(exe, name, sodb, psdbs);
    }

    /// <summary>A string an interface returns through its only argument (GetExePath, GetStateObjectDatabasePath).</summary>
    static string? Str(nint com, int slot)
    {
        char* s = null;
        return ((delegate* unmanaged<nint, char**, int>)Vt(com)[slot])(com, &s) >= 0 && s != null ? new string(s) : null;
    }

    /// <summary>An ID3DShaderCacheInstaller and the client it was made with. The client (ID3DShaderCacheInstallerClient,
    /// three methods and no IUnknown) is native memory naming this object, so the installer can call it as long as it lives.</summary>
    sealed class Installer : IDisposable
    {
        public readonly nint Ptr;
        readonly string _name;
        readonly Action? _onUpdate;
        public string Name => _name;
        readonly GCHandle _self;
        readonly nint* _client;   // { vtable, GCHandle }

        public Installer(string name, Action? onUpdate = null)
        {
            (_name, _onUpdate) = (name, onUpdate);
            _self = GCHandle.Alloc(this);
            _client = (nint*)NativeMemory.Alloc(2, (nuint)sizeof(nint));
            _client[0] = (nint)ClientVtable;
            _client[1] = GCHandle.ToIntPtr(_self);
            try
            {
                var clsid = ClsidFactory; var iid = IidFactory; var instIid = IidInstaller;
                Check(D3D12GetInterface(&clsid, &iid, out var f), "D3D12GetInterface(CLSID_D3DShaderCacheInstallerFactory)");
                try
                {
                    nint ptr;
                    Check(((delegate* unmanaged<nint, nint, Guid*, nint*, int>)Vt(f)[3])(f, (nint)_client, &instIid, &ptr), "CreateInstaller");
                    Ptr = ptr;
                }
                finally { Release(f); }
            }
            catch { Free(); throw; }
        }

        public IEnumerable<nint> Apps()
        {
            var n = ((delegate* unmanaged<nint, uint>)Vt(Ptr)[9])(Ptr);
            var iid = IidApplication;
            var apps = new List<nint>();
            for (uint k = 0; k < n; k++)
            {
                nint app;
                if (((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Vt(Ptr)[10])(Ptr, k, &iid, &app) >= 0) apps.Add(app);
            }
            return apps;
        }

        public void Dispose()
        {
            lock (Gate) if (_listening == this) _listening = null;
            if (_onUpdate != null) ((delegate* unmanaged<nint, int>)Vt(Ptr)[4])(Ptr);   // UnregisterDriverUpdateListener
            Release(Ptr);
            Free();
        }

        void Free()
        {
            NativeMemory.Free(_client);
            _self.Free();
        }

        static Installer Of(nint* client) => (Installer)GCHandle.FromIntPtr(client[1]).Target!;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        static int GetName(nint* client, nuint* length, char* name)
        {
            var s = Of(client)._name;
            var need = (nuint)s.Length + 1;
            if (name == null || *length < need) { *length = need; return name == null ? 0 : unchecked((int)0x8007007A); }   // ERROR_INSUFFICIENT_BUFFER
            s.AsSpan().CopyTo(new Span<char>(name, s.Length));
            name[s.Length] = '\0';
            *length = need;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        static int GetScope(nint* client) => 0;   // D3D_SHADER_CACHE_APP_REGISTRATION_SCOPE_USER

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        static int HandleDriverUpdate(nint* client, nint installer)
        {
            if (Of(client)._onUpdate is { } on) ThreadPool.QueueUserWorkItem(_ => on());
            return 0;
        }

        static readonly nint* ClientVtable = Vtable();

        static nint* Vtable()
        {
            var v = (nint*)NativeMemory.Alloc(3, (nuint)sizeof(nint));   // for the process's life
            v[0] = (nint)(delegate* unmanaged[Stdcall]<nint*, nuint*, char*, int>)&GetName;
            v[1] = (nint)(delegate* unmanaged[Stdcall]<nint*, int>)&GetScope;
            v[2] = (nint)(delegate* unmanaged[Stdcall]<nint*, nint, int>)&HandleDriverUpdate;
            return v;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AppDesc { public char* Exe, Name; public ulong Version; public char* Engine; public ulong EngineVersion; }

    [StructLayout(LayoutKind.Sequential)]
    struct PsdbProperties { public char* Family, Path; }

    static void Check(int hr, string what) { if (hr < 0) throw new IOException($"{what} failed (0x{hr:X8})", hr); }
    static nint* Vt(nint com) => *(nint**)com;
    static void Release(nint com) { if (com != 0) ((delegate* unmanaged<nint, uint>)Vt(com)[2])(com); }

    static readonly Guid ClsidFactory = new("16195a0b-607c-41f1-bf03-c7694d60a8d4");
    static readonly Guid IidFactory = new("09b2dfe4-840f-401a-804c-0dd8aadc9e9f");
    static readonly Guid IidInstaller = new("bbe30de1-6318-4526-ae17-776693191bb4");
    static readonly Guid IidApplication = new("fc688ee2-1b35-4913-93be-1ca3fa7df39e");
    static readonly Guid IidComponent = new("eed1bf00-f5c7-4cf7-885c-d0f9c0cb4828");

    [DllImport("d3d12.dll")] static extern int D3D12GetInterface(Guid* clsid, Guid* iid, out nint ppv);
}
