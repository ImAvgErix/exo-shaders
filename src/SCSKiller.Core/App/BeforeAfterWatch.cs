namespace SCSKiller.Core.App;

/// <summary>Follows the games' before and after stages between list updates: when a run has played its minutes (sound
/// once) and when a pair the app saw in progress is done (show its report).</summary>
public sealed class BeforeAfterWatch
{
    readonly Dictionary<string, (BeforeAfterStage? Stage, bool Sounded)> seen = [];

    public BeforeAfterWatch(IEnumerable<GameState> games)
    {
        foreach (var g in games) seen[g.Game.Id] = (g.BeforeAfter?.Stage, false);
    }

    public (bool Sound, bool Report) Changed(GameState g)
    {
        var id = g.Game.Id;
        var ba = g.BeforeAfter;
        var known = seen.TryGetValue(id, out var was);
        var sounded = known && was.Stage == ba?.Stage && was.Sounded;
        var sound = false;
        if (ba is { Stage: BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying } playing && !sounded && playing.PlayedSeconds >= playing.Minutes * 60)
            sound = sounded = true;
        seen[id] = (ba?.Stage, sounded);
        return (sound, known && was.Stage is not (null or BeforeAfterStage.Done) && ba is { Stage: BeforeAfterStage.Done });
    }
}
