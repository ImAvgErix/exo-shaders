using SCSKiller.Core.App;

namespace SCSKiller.Core.BlackSpace;

/// <summary>The 32-byte key base a BlackSpace game's encrypted .paz entries open with (<see cref="PazCrypto.Decrypt"/>), per
/// game: the user enters it, it is kept only once it opens one of the game's root signatures. Stored as hex in the game's
/// folder (paz.key). No key ships with SCSKiller.</summary>
public sealed class BlackSpaceKeys(string dataDir)
{
    public const int Size = 32;

    static readonly Lock Gate = new();

    string KeyFile(Game g) => Path.Combine(new AppStore(dataDir).GameDir(g.Id), "paz.key");

    /// <summary>The key file's write time: a stored or changed key redoes Detect.</summary>
    public string KeyStamp(Game game) => File.Exists(KeyFile(game)) ? File.GetLastWriteTimeUtc(KeyFile(game)).Ticks.ToString() : "";

    public byte[]? Stored(Game game)
    {
        lock (Gate)
        {
            try { return File.Exists(KeyFile(game)) ? Parse(File.ReadAllText(KeyFile(game))) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>Stores <paramref name="key"/> (64 hex digits, "0x" allowed) when <paramref name="opens"/> takes it; false if not.</summary>
    public bool Set(Game game, string key, Func<byte[], bool> opens)
    {
        if (Parse(key) is not { } bytes || !opens(bytes)) return false;
        var file = KeyFile(game);
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", Convert.ToHexString(bytes));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        return true;
    }

    internal static byte[]? Parse(string text)
    {
        var hex = text.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return hex.Length == 2 * Size && hex.All(char.IsAsciiHexDigit) ? Convert.FromHexString(hex) : null;
    }
}
