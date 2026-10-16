using System;

namespace SCSKiller.Core;

public interface IWarmer
{
	IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress);
}
