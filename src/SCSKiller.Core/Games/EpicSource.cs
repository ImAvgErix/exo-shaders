using System.Text.Json;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Installed Epic games from every record of an install on this PC: the launcher's installations.jsonl
/// (com.epicgames.eglv2, which wins over a leftover .item for the same app), the classic launcher's
/// %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item, and Legendary's installed.json (Exo Launcher, Heroic).
/// Ids epic:&lt;AppName&gt; whichever record it came from; a Legendary install of a folder already listed is left out.</summary>
public sealed class EpicSource(string? manifestsDir = null, string? launcherDir = null, IEnumerable<string>? legendaryFiles = null) : IGameSource
{
    public Store Store => Store.Epic;

    public static string DefaultManifestsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    /// <summary>The launcher's data folder: the one HKCU\Software\Epic Games\EpicGamesLauncher AppDataPath names when it
    /// exists, else %LOCALAPPDATA%\com.epicgames.eglv2.</summary>
    public static string DefaultLauncherDir => EpicInstalls.RegistryDataDir()
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "com.epicgames.eglv2");

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // folder: a Legendary record of a folder the launcher already lists is that game (the launcher's own records may share one)
        void Add(string app, string? title, string install, string? launchExe, string? version, bool folder = false)
        {
            string key;
            try { key = GameFiles.DirKey(install); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (folder && dirs.Contains(key) || ids.Contains($"epic:{app}")) return;
            if (GameFiles.FindExe(install, string.IsNullOrEmpty(launchExe) ? null : launchExe) is not { } exe) return;
            dirs.Add(key);
            ids.Add($"epic:{app}");
            games.Add(new Game($"epic:{app}", string.IsNullOrWhiteSpace(title) ? app : title.Trim(), Store.Epic, install, exe, version));
        }
        var installs = Installs(launcherDir ?? (manifestsDir == null ? DefaultLauncherDir : null));
        foreach (var i in installs)
            // complete=false is a first download still running (no version yet) or an update being applied (the game is
            // on disk at its last version, so it stays listed)
            if (i.Complete || i.Version != null) Add(i.App, i.Title, i.Dir, i.Exe, i.Version);
        var current = installs.Select(i => i.App).ToHashSet();   // the launcher leaves the classic one's manifests behind
        var dir = manifestsDir ?? DefaultManifestsDir;
        if (Directory.Exists(dir))
            foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(item));
                    var r = doc.RootElement;
                    if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                    if (Str(r, "AppName") is not { } app || Str(r, "InstallLocation") is not { } install || current.Contains(app)) continue;
                    if (Str(r, "MainGameAppName") is { Length: > 0 } main && main != app) continue;   // add-on sharing the main game's install (Legendary names a base game's own)
                    Add(app, Str(r, "DisplayName"), install, Str(r, "LaunchExecutable"), Str(r, "AppVersionString"));
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }   // half-written manifest while the launcher installs
            }
        foreach (var l in EpicInstalls.LegendaryInstalls(manifestsDir == null ? legendaryFiles ?? EpicInstalls.LegendaryInstalledFiles() : legendaryFiles ?? []))
            Add(l.AppName, l.Title, l.Dir, l.Executable, l.Version, folder: true);
        return games;
    }

    internal sealed record Install(string App, string Namespace, string ItemId, string Dir, string? Exe, string Title, string? Version, bool Complete);

    /// <summary>The launcher's base-game installs, finished or not. installations.jsonl is a journal: a header line, then a full snapshot per
    /// change, the last one per installationId current; one marked removed or deleted is dropped. Titles and add-ons come from its catalog cache.</summary>
    internal static List<Install> Installs(string? launcherDir)
    {
        var journal = launcherDir == null ? null : Path.Combine(launcherDir, "installations.jsonl");
        if (journal == null || !File.Exists(journal)) return [];
        var last = new Dictionary<string, JsonElement>();
        try
        {
            using var reader = new StreamReader(new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement is not { ValueKind: JsonValueKind.Object } e || Str(e, "installationId") is not { } id) continue;
                    if (Flag(e, "removed") || Flag(e, "deleted") || Str(e, "op") is "remove" or "delete") last.Remove(id);
                    else last[id] = e.Clone();
                }
                catch (JsonException) { }   // the line the launcher is appending
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        var catalog = Catalog(launcherDir!);
        var installs = new List<Install>();
        foreach (var e in last.Values)
        {
            if (Str(e, "artifactId") is not { Length: > 0 } app || Str(e, "dir") is not { Length: > 0 } dir) continue;
            if (Str(e, "parentInstallationId") is { Length: > 0 }) continue;   // an add-on in its game's install
            var itemId = Str(e, "itemId") ?? "";
            var known = catalog.TryGetValue(itemId, out var item);
            if (known && item.TryGetProperty("mainGameItem", out var main) && main.ValueKind != JsonValueKind.Null) continue;   // an add-on
            dir = Path.TrimEndingDirectorySeparator(dir);
            var title = known && Str(item, "title") is { Length: > 0 } t ? t : null;
            installs.Add(new Install(app, Str(e, "namespace") ?? "", itemId, dir, Str(e, "launchExecutable") is { Length: > 0 } exe ? exe : null,
                title ?? (Path.GetFileName(dir) is { Length: > 0 } folder ? folder : app), Str(e, "version") is { Length: > 0 } v ? v : null,
                e.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.True));
        }
        return installs;
    }

    static Dictionary<string, JsonElement> Catalog(string launcherDir)
    {
        var items = new Dictionary<string, JsonElement>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(launcherDir, "cache", "prod", "catalog-cache.json")));
            if (doc.RootElement is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("items", out var all) && all.ValueKind == JsonValueKind.Object)
                foreach (var p in all.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                        items[p.Name] = item.Clone();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return items;
    }

    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Flag(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Epic install records SCSKiller's launcher readers don't cover, read the way Exo Launcher reads them:
/// Legendary's installed.json, and the registry's name for the launcher's data folder.</summary>
public static class EpicInstalls
{
    public sealed record LegendaryInstall(string AppName, string? Title, string Dir, string? Executable, string? Version);

    /// <summary>The folder HKCU\Software\Epic Games\EpicGamesLauncher AppDataPath names, when it exists.</summary>
    public static string? RegistryDataDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\EpicGamesLauncher");
            if (key?.GetValue("AppDataPath") is string { Length: > 0 } dir && Directory.Exists(dir)) return dir;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Legendary's installed.json candidates, in the order Exo Launcher looks for its user.json: LEGENDARY_CONFIG_PATH,
    /// XDG_CONFIG_HOME, %USERPROFILE%\.config, %APPDATA%, %LOCALAPPDATA% (each also under heroic\legendaryConfig), and
    /// %LOCALAPPDATA%\ExoLauncher (\tools).</summary>
    public static IEnumerable<string> LegendaryInstalledFiles()
    {
        var dirs = new List<string>();
        if (Environment.GetEnvironmentVariable("LEGENDARY_CONFIG_PATH") is { } config && !string.IsNullOrWhiteSpace(config)) dirs.Add(config);
        if (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { } xdg && !string.IsNullOrWhiteSpace(xdg))
        {
            dirs.Add(Path.Combine(xdg, "legendary"));
            dirs.Add(Path.Combine(xdg, "heroic", "legendaryConfig", "legendary"));
        }
        if (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } profile)
        {
            dirs.Add(Path.Combine(profile, ".config", "legendary"));
            dirs.Add(Path.Combine(profile, ".config", "heroic", "legendaryConfig", "legendary"));
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
            if (Environment.GetFolderPath(folder) is { Length: > 0 } data)
            {
                dirs.Add(Path.Combine(data, "legendary"));
                dirs.Add(Path.Combine(data, "heroic", "legendaryConfig", "legendary"));
            }
        if (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } local)
        {
            dirs.Add(Path.Combine(local, "ExoLauncher", "legendary"));
            dirs.Add(Path.Combine(local, "ExoLauncher", "tools", "legendary"));
        }
        return dirs.Select(d => Path.Combine(d, "installed.json")).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Legendary's installed.json: {appName: {app_name, title, install_path, executable, version, is_dlc}}. DLC is left out.</summary>
    public static IReadOnlyList<LegendaryInstall> LegendaryInstalls(IEnumerable<string> files)
    {
        var installs = new List<LegendaryInstall>();
        foreach (var file in files)
        {
            try
            {
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var e = p.Value;
                    if (e.ValueKind != JsonValueKind.Object || e.TryGetProperty("is_dlc", out var dlc) && dlc.ValueKind == JsonValueKind.True) continue;
                    var app = Text(e, "app_name") ?? p.Name;
                    if (app.Length > 0 && Text(e, "install_path") is { Length: > 0 } install && (Text(e, "platform") is not { } platform || platform.StartsWith("Win", StringComparison.OrdinalIgnoreCase)))
                        installs.Add(new LegendaryInstall(app, Text(e, "title"), install, Text(e, "executable"), Text(e, "version")));
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return installs;
    }

    internal static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
