using System;
using System.Collections.Generic;
using System.Threading;

namespace SCSKiller.Core;

public interface IEngineReader
{
	EngineInfo? Detect(Game game);

	string DetectStamp(Game game, EngineInfo? engine)
	{
		return "";
	}

	string IndexStamp(Game game)
	{
		return "";
	}

	int ReaderVersion(EngineInfo engine)
	{
		return 1;
	}

	ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct);

	ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
	{
		return Index(game, engine, log, ct);
	}

	void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct);
}
