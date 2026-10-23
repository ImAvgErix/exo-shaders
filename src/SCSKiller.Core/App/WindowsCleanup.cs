using Microsoft.Win32;

namespace SCSKiller.Core.App;

/// <summary>
/// Keeps Windows' automatic cleanup out of D3DSCache, where NVIDIA's driver from 621 keeps the compiled D3D12 pipelines.
/// Read from cleanmgr.exe, D3DSCache.dll and StorSvc.dll (Windows 11 26300): the SilentCleanup task
/// (<c>cleanmgr /autocleanstoragesense</c>) and Storage Service's own run (<c>cleanmgr /autorunall</c>) take a VolumeCaches
/// handler only when bit 0 of its <c>Autorun</c> value is set (no value: not taken). Disk Cleanup opened by hand goes by the
/// user's checkboxes (<c>StateFlags</c>), never by Autorun. On an automatic run while the drive is short of space the
/// DirectX Shader Cache handler deletes every cache in D3DSCache; otherwise only those of apps that are gone.
/// Clearing that one bit takes the handler out of automatic runs and nothing else; setting it again puts back what
/// Windows had.
/// </summary>
public static class WindowsCleanup
{
    public const string HandlerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\D3D Shader Cache";
    public const string AutorunValue = "Autorun";

    /// <summary>The handler's registration and its Autorun value (null: none).</summary>
    public sealed record State(bool Registered, int? Autorun)
    {
        /// <summary>Windows' automatic cleanup runs the handler.</summary>
        public bool Automatic => Registered && Autorun is { } a && (a & 1) != 0;

        /// <summary>The bit is cleared, so setting it again restores Windows' value.</summary>
        public bool CanUndo => Registered && Autorun is { } a && (a & 1) == 0;

        public string Text => Automatic ? "Can delete compiled shaders" : "Leaves compiled shaders";
    }

    public static State Check()
    {
        try
        {
            using var key = Hklm().OpenSubKey(HandlerKey);
            return new(key != null, key?.GetValue(AutorunValue) is int a ? a : null);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return new(false, null); }
    }

    /// <summary>The Autorun value to write: bit 0 cleared to keep D3DSCache, set again to allow. Null: nothing to change.</summary>
    public static int? Next(State s, bool keep) =>
        keep ? s.Automatic ? s.Autorun!.Value & ~1 : null
             : s.CanUndo ? s.Autorun!.Value | 1 : null;

    /// <summary>Admin. Keeps D3DSCache out of the automatic cleanup, or (keep false) lets it in again; the outcome line.</summary>
    public static string Set(bool keep)
    {
        using var key = Hklm().OpenSubKey(HandlerKey, writable: true);
        var now = new State(key != null, key?.GetValue(AutorunValue) is int a ? a : null);
        if (Next(now, keep) is not { } value)
            return keep ? "Windows' automatic cleanup already leaves compiled shaders." : "Nothing to undo: Windows' setting is as it was.";
        key!.SetValue(AutorunValue, value, RegistryValueKind.DWord);
        return keep ? "Windows' automatic cleanup now leaves compiled shaders." : "Windows' automatic cleanup can delete compiled shaders again.";
    }

    static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
}
