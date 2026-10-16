using System;
using System.Collections.Generic;

namespace SCSKiller.Core.App;

public sealed record NewsTarget(AppVersion Version, IReadOnlyCollection<string> Ent, GpuVendor Gpu, Func<string, bool> HasGame);
