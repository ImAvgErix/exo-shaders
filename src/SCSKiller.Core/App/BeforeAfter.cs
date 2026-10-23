using System.Globalization;

namespace SCSKiller.Core.App;

/// <summary>The before and after recording's pure parts: whether a run counts, the comparison, the share text.</summary>
public static partial class BeforeAfter
{
    /// <summary>Play a run needs to count; the countdown asks for <see cref="BeforeAfterRecord.Minutes"/>.</summary>
    public static readonly TimeSpan MinPlay = TimeSpan.FromMinutes(3);

    /// <summary>With fewer shader stutters than this in the before run, the game was already smooth.</summary>
    public const int SmoothStutters = 3;

    // Every user-facing line of the report and the share text
    const string NoFrames = "The {0} run has no frame times";
    const string NoCreates = "The {0} run has no pipeline log";
    const string TooShort = "The {0} run had {1} of play, play at least {2}.";
    const string LibraryLoads = "The after run loaded {0} more pipelines from the game's own cache, not counted for SCSKiller";
    const string OtherSettings = "The runs had different resolutions or display formats: {0} before, {1} after";
    const string OverlayOnce = "The in-game countdown was on in one run only";
    const string Crashed = "The game didn't close normally in the before run";
    public const string MaybeWarm = "AMD's DirectX 11 driver cache can't be checked, so the before run may not have been fully cold";
    public const string VulkanNotCold = "Some of NVIDIA's Vulkan cache couldn't be set aside, so the before run may not have been fully cold";
    const string MaybeHelped = "The game's DirectX 11 driver cache couldn't be told from another app's, so the after run may have used the before run's";
    const string FrameGenBoth = "Frame generation was {0} in both runs.";
    const string FrameGenOne = "Frame generation was on in the {0} run only.";
    const string Share = "Shader compilation stutter in {0}: {1} → {2} in {3} of play after SCSKiller precompiled its shaders.";
    const string ShareReady = " {0}% of pipelines were ready instead of {1}%.";
    const string ShareSmooth = "{0} was already smooth here: SCSKiller took it from {1}% to {2}% of pipelines ready.";
    const string ShareSmoothNoShare = "{0} was already smooth here: {1} → {2} shader compilation stutters in {3} of play with SCSKiller.";
    const string ShareDx11 = "{0} on DirectX 11: {1} → {2} stutters of 50 ms or more in {3} of play, before and after SCSKiller precompiled its shaders.";
    public const string Dx11Label = "DirectX 11: SCSKiller can't tell shader compilation stutters from other stutters";
    const string ShareLink = " scskiller.com";
    const int ShareMax = 280, LinkWeight = 23;   // X's limit; X counts any link as 23

    /// <summary>The game page's button with no pair: hidden only with the setting off, greyed with the reason as its note
    /// while <see cref="ScsKiller.BeforeAfterBlocked"/> says why a pair can't start.</summary>
    public static (bool Shown, bool Enabled, string Note) StartButton(bool on, string? blocked) => (on, on && blocked == null, blocked ?? "");

    /// <summary>Why the run doesn't count, one line for the button's note; null = it counts.</summary>
    public static string? RunProblem(SessionSnapshot? run, bool before)
    {
        var which = before ? "before" : "after";
        if (run == null || run.Frames == 0) return F(NoFrames, which);
        if (run.Creates == null) return F(NoCreates, which);
        if (run.PlaySeconds < MinPlay.TotalSeconds)
            return F(TooShort, which, run.PlaySeconds < 60 ? "under a minute" : $"{(int)(run.PlaySeconds / 60)} min",
                $"{(int)MinPlay.TotalMinutes} min");
        return null;
    }

    public static RunStats Stats(SessionSnapshot s)
    {
        var shader = s.Hitches.Where(h => h.Cause == HitchCause.Shader).Select(h => h.Ms).ToList();
        var play = s.Hitches.Where(h => h.Cause is HitchCause.Shader or HitchCause.Other).Select(h => h.Ms).ToList();
        return new RunStats(s.PlaySeconds, shader.Count, shader.Sum() / 1000, shader.DefaultIfEmpty(0).Max(),
            s.Creates?.ReadyShare, s.Creates?.PlayCompiles ?? 0, s.Low1PctFps, s.AvgFps, s.Hitches.Count(h => h.Cause == HitchCause.Other),
            play.Count, play.Sum() / 1000, play.DefaultIfEmpty(0).Max());
    }

    /// <summary>The before run is cold (a run that started with old driver-cache files is played again), unless
    /// <paramref name="notCold"/> says why not, its note (<see cref="MaybeWarm"/>: its clear didn't know the key of a cache
    /// SCSKiller can't watch, AMD's DirectX 11; <see cref="VulkanNotCold"/>).
    /// <paramref name="afterHelped"/>: that key was still unknown at the clear before the compile. A cold
    /// run's ready pipelines are the driver's own reuse within the launch (both vendors cache per shader stage, so a new
    /// pipeline of stages it already compiled is ready), not a cache it started with.</summary>
    public static Comparison Compare(SessionSnapshot before, SessionSnapshot after, string? notCold = null, bool afterHelped = false)
    {
        var (b, a) = (Stats(before), Stats(after));
        bool dx11 = before.D3D11 || after.D3D11;   // either run without create rows: no split claimed for the pair
        var notes = new List<string>();
        if ((after.Creates?.Library ?? 0) - (before.Creates?.Library ?? 0) is var more and > 0) notes.Add(F(LibraryLoads, more));
        if (before.SwapChain != null && after.SwapChain != null && before.SwapChain != after.SwapChain) notes.Add(F(OtherSettings, before.SwapChain, after.SwapChain));
        if (before.Overlay != after.Overlay) notes.Add(OverlayOnce);
        if (!before.CleanExit) notes.Add(Crashed);
        if (notCold != null) notes.Add(notCold);
        else if (afterHelped) notes.Add(MaybeHelped);
        if (before.FrameGen is { } fb && after.FrameGen is { } fa)   // a run whose module loaded but whose frames can't tell says nothing
            notes.Add(fb == fa ? F(FrameGenBoth, fb ? "on" : "off") : F(FrameGenOne, fb ? "before" : "after"));
        return new Comparison(b, a, Math.Abs(b.PlaySeconds - a.PlaySeconds) > 0.1 * Math.Min(b.PlaySeconds, a.PlaySeconds),
            !dx11 && b.ShaderStutters < SmoothStutters, notes, dx11);
    }

    /// <summary>Each run's play in whole minutes, as the card's footer and the share text name them.</summary>
    public static (int Before, int After) PlayMinutes(Comparison c) =>
        ((int)Math.Round(c.Before.PlaySeconds / 60, MidpointRounding.AwayFromZero), (int)Math.Round(c.After.PlaySeconds / 60, MidpointRounding.AwayFromZero));

    /// <summary>Within X's 280 with the link (<see cref="PostLength"/>); a long game name is cut to fit. The counts are the card's.</summary>
    public static string ShareText(string game, Comparison c)
    {
        var (b, a) = PlayMinutes(c);
        var span = b == a ? $"{b} minutes" : $"{b} and {a} minutes";
        var (before, after) = (c.Before.ReadyShare, c.After.ReadyShare);
        string Text(string name) =>
            (c.Dx11 ? F(ShareDx11, name, c.Before.Hitches, c.After.Hitches, span)
            : c.AlreadySmooth
                ? before != null && after != null ? F(ShareSmooth, name, Percent(before.Value), Percent(after.Value))
                    : F(ShareSmoothNoShare, name, c.Before.ShaderStutters, c.After.ShaderStutters, span)
                : F(Share, name, c.Before.ShaderStutters, c.After.ShaderStutters, span)
                    + (before != null && after != null ? F(ShareReady, Percent(after.Value), Percent(before.Value)) : ""))
            + ShareLink;
        var text = Text(game);
        for (int n = game.Length - 1; n >= 1 && PostLength(text) > ShareMax; n--) text = Text(game[..n].TrimEnd() + "…");
        return text;
    }

    /// <summary>The share text's length as X counts it (docs.x.com, counting characters): a link 23, a code point in Latin
    /// and general punctuation 1, any other 2 (arrows, the ellipsis, CJK, emoji). Anything shaped like a domain counts as a
    /// link, a game name's "GOG.com" too: X's own list of domains is longer, so this errs on the short side.</summary>
    public static int PostLength(string text) =>
        Domain().Replace(text, new string('x', LinkWeight)).EnumerateRunes()
            .Sum(r => r.Value is <= 4351 or >= 8192 and <= 8205 or >= 8208 and <= 8223 or >= 8242 and <= 8247 ? 1 : 2);

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}\b")]
    private static partial System.Text.RegularExpressions.Regex Domain();

    /// <summary>A share in whole percent, 100 only when every pipeline was ready.</summary>
    static int Percent(double share) => share >= 1 ? 100 : Math.Min(99, (int)Math.Round(share * 100));

    static string F(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);

    /// <summary>The report card's text: its subtitle, headline tiles, detail rows and notes. <paramref name="vendor"/>: what
    /// the D3D11 recorder recorded (input layouts on AMD only).</summary>
    public static ReportText Report(string game, BeforeAfterResult r, GpuVendor vendor)
    {
        var c = r.Compare;
        var subtitle = c.Dx11 ? Dx11Label
            : c.AlreadySmooth ? "Already smooth on this PC: few shader compilation stutters even before the compile"
            : "Shader compilation stutter before and after SCSKiller compiled its shaders";
        (string, string)[] tiles = c.Dx11   // no ready share and no shader split: every frame of 50 ms or more
            ? [("STUTTERS", $"{c.Before.Hitches:N0}  →  {c.After.Hitches:N0}"),
                ("TIME FROZEN", $"{c.Before.HitchSeconds:0.0} s  →  {c.After.HitchSeconds:0.0} s"),
                ("WORST STUTTER", $"{Ms(c.Before.WorstHitchMs)}  →  {Ms(c.After.WorstHitchMs)}")]
            : [("SHADER COMPILATION STUTTERS", $"{c.Before.ShaderStutters:N0}  →  {c.After.ShaderStutters:N0}"),
                ("READY FROM THE CACHE", $"{Pct(c.Before.ReadyShare)}  →  {Pct(c.After.ReadyShare)}"),
                ("WORST STUTTER", $"{Ms(c.Before.WorstStutterMs)}  →  {Ms(c.After.WorstStutterMs)}")];
        var rows = new List<ReportRow> { new("Play time", Clock(c.Before.PlaySeconds), Clock(c.After.PlaySeconds)) };
        if (c.Dx11)
        {
            rows.Add(new("Stutters of 50 ms or more", Count(c.Before.Hitches, c.Before), Count(c.After.Hitches, c.After)));
            rows.Add(new("Time frozen by them", $"{c.Before.HitchSeconds:0.0} s", $"{c.After.HitchSeconds:0.0} s"));
            rows.Add(new("Worst stutter", Ms(c.Before.WorstHitchMs), Ms(c.After.WorstHitchMs)));
        }
        else
        {
            rows.Add(new("Shader compilation stutters", Count(c.Before.ShaderStutters, c.Before), Count(c.After.ShaderStutters, c.After)));
            rows.Add(new("Ready from the cache", Pct(c.Before.ReadyShare), Pct(c.After.ReadyShare)));
            // state objects aren't in the ready share
            if (r.Before.Creates is { } bc && r.After.Creates is { } ac && bc.StateObjectsReady + bc.StateObjectsCompiled + ac.StateObjectsReady + ac.StateObjectsCompiled > 0)
                rows.Add(new("Ray tracing ready from the cache", Rt(bc), Rt(ac)));
            rows.Add(new("Pipelines compiled while playing", Count(c.Before.PlayCompiles, c.Before), Count(c.After.PlayCompiles, c.After)));
            rows.Add(new("Time frozen by shader compilation stutters", $"{c.Before.FrozenSeconds:0.0} s", $"{c.After.FrozenSeconds:0.0} s"));
            rows.Add(new("Worst shader compilation stutter", Ms(c.Before.WorstStutterMs), Ms(c.After.WorstStutterMs)));
        }
        rows.Add(new("1% low", $"{c.Before.Low1PctFps:0} fps", $"{c.After.Low1PctFps:0} fps"));
        rows.Add(new("Average", $"{c.Before.AvgFps:0} fps", $"{c.After.AvgFps:0} fps",
            "1% low and average count from the game's first frame, menus and loading included, so a faster start alone raises them."));
        if (!c.Dx11)
            rows.Add(new("Other hitches (not shader-related)", Count(c.Before.OtherHitches, c.Before), Count(c.After.OtherHitches, c.After),
                "Streaming hitches can drop on a second run: Windows keeps the game's files in memory."));

        var notes = new List<string>(c.Notes);
        if (c.Dx11) notes.Add("On DirectX 11 stutters on loading screens count too, and streaming stutters can drop on a second run.");
        if (c.AlreadySmooth) notes.Add($"{game} barely stuttered from shaders on this PC even before the compile.");
        // the D3D11 recorder records input layouts on AMD only, frame times only on NVIDIA
        notes.Add(c.Dx11 && vendor != GpuVendor.Amd ? "Compiled from the game's files." : "Compiled from the game's files and your before run's recording.");
        notes.Add("Frame times and this report stay on your PC unless you share the image.");
        return new ReportText(subtitle, tiles, rows, notes);

        string Count(long n, RunStats run) => $"{n:N0}" + (c.PerTenMinutes && run.PlaySeconds > 0 ? $"  ({n * 600 / run.PlaySeconds:0.#} per 10 min)" : "");
    }

    /// <summary>The card's footer: each run's play, "same PC", the resolution when both runs had it, and <paramref name="gpu"/>
    /// when shown.</summary>
    public static string Footer(BeforeAfterResult r, string? gpu)
    {
        var (b, a) = PlayMinutes(r.Compare);
        var size = Size(r.Before.SwapChain) is { } s && s == Size(r.After.SwapChain) ? s : null;
        return string.Join("  ·  ", new[]
        {
            b == a ? $"{b} min of play each" : $"{b} and {a} min of play", "same PC", size,   // the swap chain's size is all SCSKiller knows of the settings
            gpu,
        }.OfType<string>());
    }

    /// <summary>"2560×1440" from the csv's swap chain line ("2560x1440 fmt 28").</summary>
    static string? Size(string? swapChain) => swapChain?.Split(' ')[0].Replace('x', '×');

    public static string Clock(double seconds) => $"{(int)(seconds / 60)}:{(int)seconds % 60:00}";

    // neither composer takes an image from a link: the app puts it on the clipboard
    public static string PostOnX(string shareText) => $"https://x.com/intent/post?text={Uri.EscapeDataString(WithoutLink(shareText))}&url={Uri.EscapeDataString(Link)}";

    /// <summary>A text post: a link post has no body to paste the image into.</summary>
    public static string PostOnReddit(string shareText) =>
        $"https://www.reddit.com/submit?selftext=true&title={Uri.EscapeDataString(WithoutLink(shareText))}&text={Uri.EscapeDataString(Link)}";

    /// <summary>X shows the url param as the link: the text's own trailing link would show twice.</summary>
    static string WithoutLink(string text)
    {
        text = text.TrimEnd();
        foreach (var link in new[] { Link, "scskiller.com" })
            if (text.EndsWith(link, StringComparison.OrdinalIgnoreCase)) return text[..^link.Length].TrimEnd();
        return text;
    }

    /// <summary>The saved image's file name: "&lt;game&gt; before and after", without characters a file name can't have.</summary>
    public static string ImageName(string game) =>
        string.Join(" ", $"{game} before and after".Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();

    static string Rt(CreateStats c) => $"{c.StateObjectsReady:N0} of {c.StateObjectsReady + c.StateObjectsCompiled:N0}";
    static string Pct(double? share) => share is { } s ? $"{s * 100:0}%" : Format.Dash;
    static string Ms(double ms) => ms <= 0 ? "none" : ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms";

    public const string Link = "https://scskiller.com";
}
