using System;
using System.Threading;

namespace SCSKiller.Core;

public interface IPlanner
{
	PlanCheck Check(Game game, EngineInfo engine, Recording? recording, VendorCaps caps);

	Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir, IProgress<string>? log, CancellationToken ct, bool maximum = false, bool rayTracing = true);

	void Materialize(Plan plan, Game game, EngineInfo engine, IEngineReader reader, Recording? recording, string workDir, CancellationToken ct);
}
