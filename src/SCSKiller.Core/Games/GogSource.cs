using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Installed GOG games (GOG Galaxy or the standalone offline installer both write the same registry +
/// info file) from SOFTWARE\GOG.com\Games\&lt;id&gt; ("path") in HKLM and HKCU, 64- and 32-bit views (as Exo Launcher
/// reads them), the installs Exo Launcher makes itself through gogdl (its installed-games.json catalog) and Heroic's
/// gog_store\installed.json, plus the goggame-&lt;id&gt;.info the installer drops in that folder: its playTasks has the
/// real game exe (the registry's own "exe" value, and the task marked "launcher", is the prelauncher, not the D3D12
/// process). An id with no "game"-category task (a DLC or expansion registered under its own id, sharing the base
/// game's folder) is skipped. One game per install folder.</summary>
public sealed class GogSource(IEnumerable<(string Id, string? Name, string Dir, string? Exe)>? entries = null) : IGameSource
{
    public Store Store => Store.Other;

    public IReadOnlyList<Game> Discover() => OperatingSystem.IsWindows() ? Installed() : [];

    [SupportedOSPlatform("windows")]
    List<Game> Installed()
    {
        var games = new List<Game>();
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name, at, exeHint) in entries ?? Entries())
        {
            string install;
            try { install = GameFiles.DirKey(at); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (id.Length == 0 || !Directory.Exists(install) || dirs.Contains(install) || ids.Contains(id)) continue;
            if (Read(id, name ?? GameFiles.FolderName(install), install, exeHint) is not { } game) continue;
            dirs.Add(install);
            ids.Add(id);
            games.Add(game);
        }
        return games;
    }

    static Game? Read(string id, string name, string install, string? exeHint)
    {
        var infoPath = Path.Combine(install, $"goggame-{id}.info");
        string? exe = null, version = null;
        if (File.Exists(infoPath))
        {
            try { (name, exe, version) = ParseInfo(File.ReadAllText(infoPath), install, name); }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException) { }
            if (exe == null) return null;   // info file exists but has no "game" playTask: DLC/component entry
        }
        else
        {
            try { exe = exeHint is { Length: > 0 } ? Path.GetFullPath(Path.Combine(install, exeHint)) : null; }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { exe = null; }
            if (exe == null || !File.Exists(exe) || !GameFiles.Inside(install, exe)) exe = GameFiles.FindExe(install);
        }
        if (exe == null || !File.Exists(exe)) return null;
        return new Game($"gog:{id}", name, Store.Other, install, exe, version);
    }

    static List<(string, string?, string, string?)> Entries()
    {
        var list = new List<(string, string?, string, string?)>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var path in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
            {
                try
                {
                    using var root = hive.OpenSubKey(path);
                    if (root == null) continue;
                    foreach (var id in root.GetSubKeyNames())
                    {
                        using var key = root.OpenSubKey(id);
                        if (key?.GetValue("path") is string { Length: > 0 } install)
                            list.Add((id, key.GetValue("gameName") as string, install, key.GetValue("exe") as string));
                    }
                }
                catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
            }
        list.AddRange(ExoCatalog());
        list.AddRange(Heroic());
        return list;
    }

    /// <summary>Exo Launcher's durable record of the installs it made itself (gogdl): %LOCALAPPDATA%\ExoLauncher\installed-games.json
    /// (EXO_LAUNCHER_DATA_DIR overrides the folder), {version, games: [{id: "gog:&lt;product id&gt;", title, store: "gog", installPath}]}.</summary>
    internal static List<(string, string?, string, string?)> ExoCatalog(string? path = null)
    {
        var list = new List<(string, string?, string, string?)>();
        if (path == null)
        {
            var dataDir = Environment.GetEnvironmentVariable("EXO_LAUNCHER_DATA_DIR");
            path = Path.Combine(!string.IsNullOrWhiteSpace(dataDir) ? dataDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher"), "installed-games.json");
        }
        try
        {
            if (!File.Exists(path)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (Prop(doc.RootElement, "games") is not { ValueKind: JsonValueKind.Array } games) return list;
            foreach (var g in games.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                if (Str(g, "store") is { } store && store.Equals("gog", StringComparison.OrdinalIgnoreCase)
                    && Str(g, "id") is { } id && id.StartsWith("gog:", StringComparison.OrdinalIgnoreCase)
                    && Str(g, "installPath") is { } install && Path.IsPathFullyQualified(install))
                    list.Add((id[4..], Str(g, "title"), install, null));
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Heroic's gog_store\installed.json: {installed: [{appName, install_path, platform}]}.</summary>
    static List<(string, string?, string, string?)> Heroic()
    {
        var list = new List<(string, string?, string, string?)>();
        foreach (var path in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "heroic", "gog_store", "installed.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "heroic", "gog_store", "installed.json"),
        })
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (Prop(doc.RootElement, "installed") is not { ValueKind: JsonValueKind.Array } installed) continue;
                foreach (var g in installed.EnumerateArray())
                    if (g.ValueKind == JsonValueKind.Object && Str(g, "appName") is { Length: > 0 } app && Str(g, "install_path") is { Length: > 0 } install
                        && (Str(g, "platform") is not { } platform || platform.Equals("windows", StringComparison.OrdinalIgnoreCase)))
                        list.Add((app, null, install, null));
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    static string? Str(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    internal static (string Name, string? Exe, string? Version) ParseInfo(string json, string install, string fallbackName)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var name = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : fallbackName;
        var version = r.TryGetProperty("buildId", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        string? exe = null;
        if (r.TryGetProperty("playTasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
            foreach (var t in tasks.EnumerateArray())
                if (t.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String && cat.GetString() == "game"
                    && t.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    exe = Path.GetFullPath(GameFiles.Find(install, p.GetString()!));
                    break;
                }
        return (name, exe, version);
    }
}
