using System;

namespace SCSKiller.Core;

public sealed record CarefulCompile(bool On, double? LaunchCompiled, TimeSpan? Estimate, long? Recorded);
