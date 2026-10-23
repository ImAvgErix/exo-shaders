namespace SCSKiller.Core.App;

/// <summary>When a file or folder was made. Null when the file system doesn't tell (a caller then can't judge by it).</summary>
public static class FileTimes
{
    public static DateTime? CreatedUtc(string path) => File.GetCreationTimeUtc(path);

    public static DateTime? CreatedUtc(FileSystemInfo f) => f.CreationTimeUtc;
}
