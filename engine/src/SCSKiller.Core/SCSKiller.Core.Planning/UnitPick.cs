using System.Collections.Generic;

namespace SCSKiller.Core.Planning;

public sealed record UnitPick(List<PsoDb.LayoutElem> Layout, uint Topology, string Shape);
