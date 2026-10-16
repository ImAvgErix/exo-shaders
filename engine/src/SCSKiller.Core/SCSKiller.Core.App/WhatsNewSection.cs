using System.Collections.Generic;

namespace SCSKiller.Core.App;

public sealed record WhatsNewSection(string Version, IReadOnlyList<string> Bullets);
