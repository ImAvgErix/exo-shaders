using System.Security;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Installed Ubisoft Connect games from Ubisoft\Launcher\Installs\&lt;id&gt; ("InstallDir") in HKLM and HKCU,
/// 64- and 32-bit views (as Exo Launcher reads them), titled from the launcher's "Uplay Install &lt;id&gt;" uninstall
/// entry; the exe is picked from the install dir the same heuristic way Steam/Epic do (Connect doesn't keep a per-game
/// manifest as convenient as GOG's or Epic's, just the install path).</summary>
public sealed class UbisoftSource : IGameSource
{
    public Store Store => Store.Other;

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var path in new[] { @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", @"SOFTWARE\Ubisoft\Launcher\Installs" })
            {
                try
                {
                    using var root = hive.OpenSubKey(path);
                    if (root == null) continue;
                    foreach (var id in root.GetSubKeyNames())
                    {
                        using var key = root.OpenSubKey(id);
                        if (key?.GetValue("InstallDir") is not string { Length: > 0 } install || !Directory.Exists(install) || ids.Contains(id)) continue;
                        var dir = GameFiles.DirKey(install);
                        if (dirs.Contains(dir) || GameFiles.FindExe(install) is not { } exe) continue;
                        ids.Add(id);
                        dirs.Add(dir);
                        games.Add(new Game($"ubisoft:{id}", Title(id) ?? GameFiles.FolderName(install), Store.Other, install, exe));
                    }
                }
                catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        return games;
    }

    static string? Title(string id) =>
        EaSource.ReadRegistry($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uplay Install {id}\DisplayName") is { } name
        && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
}
