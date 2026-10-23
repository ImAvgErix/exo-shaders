using System.Runtime.Versioning;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Installed Battle.net games from the ordinary Windows uninstall registry (product.db is a protobuf the
/// Battle.net Agent owns; the uninstall entries it also maintains give the same install dir without needing to
/// parse that format), in HKLM and HKCU, 64- and 32-bit views, as Exo Launcher reads them. Filtered to Publisher
/// "Blizzard Entertainment" or a "Battle.net Uninstaller" uninstall command, excluding the Battle.net client itself.
/// The id uses the product code from the entry's own uninstall command (--uid=&lt;code&gt;, e.g. "prometheus" for
/// Overwatch) when present, since that's Blizzard's own stable per-game id.</summary>
public sealed class BattleNetSource : IGameSource
{
    public Store Store => Store.Other;

    public IReadOnlyList<Game> Discover() => OperatingSystem.IsWindows() ? Installed() : [];

    [SupportedOSPlatform("windows")]
    static List<Game> Installed()
    {
        var games = new List<Game>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var path in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var root = hive.OpenSubKey(path);
                    if (root == null) continue;
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        using var key = root.OpenSubKey(sub);
                        if (key == null) continue;
                        var uninst = key.GetValue("UninstallString") as string;
                        var byAgent = uninst != null && uninst.Contains("Battle.net Uninstaller", StringComparison.OrdinalIgnoreCase);
                        if (key.GetValue("Publisher") as string != "Blizzard Entertainment" && !byAgent) continue;
                        if (key.GetValue("DisplayName") is not string name || name == "Battle.net") continue;
                        if (key.GetValue("InstallLocation") is not string { Length: > 0 } install || !Directory.Exists(install)) continue;
                        var code = uninst != null ? Regex.Match(uninst, @"--uid=(\S+)").Groups[1].Value.Trim('"') : "";
                        if (code.Length == 0) code = Regex.Replace(name, @"\W+", "").ToLowerInvariant();
                        var dir = GameFiles.DirKey(install);
                        if (ids.Contains(code) || dirs.Contains(dir)) continue;
                        if (GameFiles.FindExe(install) is not { } exe) continue;
                        ids.Add(code);
                        dirs.Add(dir);
                        games.Add(new Game($"battlenet:{code}", name, Store.Other, install, exe));
                    }
                }
                catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        return games;
    }
}
