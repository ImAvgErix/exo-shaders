using System.Diagnostics;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

/// <summary>
/// Windows' automatic cleanup and D3DSCache (WindowsCleanup). It fails if a handler that automatic runs take (Autorun bit
/// 0) reads as kept, or one they skip as exposed; if keeping or undoing changes any other bit, or writes when there is
/// nothing to change (undo with no Autorun value, keep when already kept); or if the CLI changes anything on an argument
/// it doesn't know. Nothing here writes the registry.
/// </summary>
public sealed class WindowsCleanupTests
{
    [Fact]
    public void Only_bit_0_of_Autorun_lets_the_automatic_cleanup_run_the_handler()
    {
        Assert.True(new WindowsCleanup.State(true, 1).Automatic);
        Assert.True(new WindowsCleanup.State(true, 3).Automatic);
        Assert.Equal("Can delete compiled shaders", new WindowsCleanup.State(true, 1).Text);
        Assert.False(new WindowsCleanup.State(true, 0).Automatic);
        Assert.False(new WindowsCleanup.State(true, 2).Automatic);
        Assert.False(new WindowsCleanup.State(true, null).Automatic);   // cleanmgr reads a missing value as 0
        Assert.False(new WindowsCleanup.State(false, 1).Automatic);     // no handler: nothing deletes it
        Assert.Equal("Leaves compiled shaders", new WindowsCleanup.State(true, 0).Text);

        Assert.True(new WindowsCleanup.State(true, 0).CanUndo);
        Assert.False(new WindowsCleanup.State(true, 1).CanUndo);
        Assert.False(new WindowsCleanup.State(true, null).CanUndo);
        Assert.False(new WindowsCleanup.State(false, 0).CanUndo);
    }

    [Fact]
    public void Keeping_and_undoing_flip_bit_0_alone_and_only_when_needed()
    {
        Assert.Equal(0, WindowsCleanup.Next(new(true, 1), keep: true));
        Assert.Equal(2, WindowsCleanup.Next(new(true, 3), keep: true));
        Assert.Equal(1, WindowsCleanup.Next(new(true, 0), keep: false));
        Assert.Equal(3, WindowsCleanup.Next(new(true, 2), keep: false));
        Assert.Null(WindowsCleanup.Next(new(true, 0), keep: true));      // kept already
        Assert.Null(WindowsCleanup.Next(new(true, 1), keep: false));     // nothing to undo
        Assert.Null(WindowsCleanup.Next(new(true, null), keep: false));  // never set: undo makes no value
        Assert.Null(WindowsCleanup.Next(new(true, null), keep: true));
        Assert.Null(WindowsCleanup.Next(new(false, null), keep: true));  // no handler: no key to write
        foreach (var a in new[] { 0, 1, 2, 3, 0x7f })
            if (WindowsCleanup.Next(new(true, a), keep: true) is { } kept)
                Assert.Equal(a, WindowsCleanup.Next(new(true, kept), keep: false));   // undo gives back Windows' value
    }

    [Fact]
    public void The_cli_refuses_an_argument_it_does_not_know_before_touching_anything()
    {
        if (Elevated.CliExe() is not { } cli) return;
        var root = Directory.CreateTempSubdirectory("scskiller-cleanup-").FullName;
        try
        {
            var file = Path.Combine(root, "result.json");
            var before = WindowsCleanup.Check();
            var psi = new ProcessStartInfo(cli, ["cleanup-guard", "bogus", Elevated.YesArg, Elevated.ResultArg, file]) { RedirectStandardError = true, RedirectStandardOutput = true };
            psi.Environment["SCSKILLER_DATA_DIR"] = Path.Combine(root, "data");
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(new Elevated.Result(false, "cleanup-guard [on|off]"), Elevated.ReadResult(file, p.ExitCode));
            Assert.Equal(before, WindowsCleanup.Check());
        }
        finally { Directory.Delete(root, true); }
    }
}
