using System.Collections.Generic;

namespace SCSKiller.Core;

public interface IGameSource
{
	Store Store { get; }

	IReadOnlyList<Game> Discover();
}
