using System.Collections.Generic;

namespace SCSKiller.Core.Planning;

public sealed record RehydrateResult(int Referenced, int AlreadyPresent, int Found, IReadOnlyList<string> Missing, bool? ContentHashMatches, long OutputBytes, int FoundInMiddleware = 0)
{
	public bool Complete => Missing.Count == 0;
}
