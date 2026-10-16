using System.Collections.Generic;

namespace SCSKiller.Core.Games;

public sealed record AddedLibrary(List<string>? Folders = null, List<string>? Removed = null, Dictionary<string, string>? Names = null);
