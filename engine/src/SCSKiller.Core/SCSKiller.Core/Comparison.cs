using System.Collections.Generic;

namespace SCSKiller.Core;

public sealed record Comparison(RunStats Before, RunStats After, bool PerTenMinutes, bool AlreadySmooth, IReadOnlyList<string> Notes, bool Dx11 = false);
