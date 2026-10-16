using System.Collections.Generic;

namespace SCSKiller.Core;

public sealed record MiddlewareTag(string Label, IReadOnlyList<string> Dlls, int Pipelines);
