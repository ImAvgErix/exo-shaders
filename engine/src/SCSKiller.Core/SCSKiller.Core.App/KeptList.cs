using System.Collections.Generic;

namespace SCSKiller.Core.App;

public sealed record KeptList(string Build, string? Driver, List<GameState> Games);
