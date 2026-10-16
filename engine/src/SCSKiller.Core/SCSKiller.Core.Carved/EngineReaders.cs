using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace SCSKiller.Core.Carved;

public sealed class EngineReaders(params (string Family, IEngineReader Reader)[] readers) : IEngineReader
{
	private IEnumerable<IEngineReader> Distinct => readers.Select(((string Family, IEngineReader Reader) r) => r.Reader).Distinct();

	public T? Get<T>() where T : class, IEngineReader
	{
		return readers.Select(((string Family, IEngineReader Reader) r) => r.Reader).OfType<T>().FirstOrDefault();
	}

	public EngineInfo? Detect(Game game)
	{
		EngineInfo engineInfo = null;
		Exception ex = null;
		foreach (IEngineReader item in Distinct)
		{
			EngineInfo engineInfo2;
			try
			{
				engineInfo2 = item.Detect(game);
			}
			catch (Exception ex2)
			{
				if (ex == null)
				{
					ex = ex2;
				}
				continue;
			}
			if (!(engineInfo2 == null))
			{
				if (engineInfo2.Unsupported == null && !engineInfo2.Encrypted)
				{
					return engineInfo2;
				}
				if ((object)engineInfo == null)
				{
					engineInfo = engineInfo2;
				}
			}
		}
		if (engineInfo == null && ex != null)
		{
			ExceptionDispatchInfo.Throw(ex);
		}
		return engineInfo;
	}

	public string IndexStamp(Game game)
	{
		return string.Concat(Distinct.Select((IEngineReader r) => r.IndexStamp(game)));
	}

	public string DetectStamp(Game game, EngineInfo? engine)
	{
		string text;
		if (!(engine == null))
		{
			text = readers.FirstOrDefault(((string Family, IEngineReader Reader) r) => r.Family == engine.Family).Reader?.DetectStamp(game, engine);
			if (text == null)
			{
				return "";
			}
		}
		else
		{
			text = string.Concat(Distinct.Select((IEngineReader r) => r.DetectStamp(game, null)));
		}
		return text;
	}

	public int ReaderVersion(EngineInfo engine)
	{
		return readers.FirstOrDefault(((string Family, IEngineReader Reader) r) => r.Family == engine.Family).Reader?.ReaderVersion(engine) ?? 1;
	}

	private IEngineReader Of(EngineInfo e)
	{
		return readers.FirstOrDefault(((string Family, IEngineReader Reader) r) => r.Family == e.Family).Reader ?? throw new NotSupportedException("no reader for " + e.Family);
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		return Of(engine).Index(game, engine, log, ct);
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
	{
		return Of(engine).Index(game, engine, log, progress, ct);
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		Of(engine).ReadShaders(game, engine, sha1s, sink, ct);
	}
}
