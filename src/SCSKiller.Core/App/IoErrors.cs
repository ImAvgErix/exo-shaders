namespace SCSKiller.Core.App;

/// <summary>What an I/O error says about the file.</summary>
internal static class IoErrors
{
    /// <summary>Another process has the file open (a sharing or lock violation).</summary>
    internal static bool HeldElsewhere(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;

    /// <summary>The file can't be taken now: held elsewhere, or access denied.</summary>
    internal static bool Busy(Exception e) => e switch
    {
        UnauthorizedAccessException => true,
        IOException io => HeldElsewhere(io),
        _ => false,
    };
}
