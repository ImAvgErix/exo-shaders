using System;

namespace SCSKiller.Core;

public sealed record Hitch(TimeSpan At, double Ms, HitchCause Cause);
