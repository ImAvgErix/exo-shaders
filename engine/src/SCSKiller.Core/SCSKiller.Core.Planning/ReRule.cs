namespace SCSKiller.Core.Planning;

public sealed record ReRule(uint CbvBase, uint[] CbvSizes, uint[] SrvSizes, uint[] UavSizes, uint ComputeUavs, bool HeapIndexed = false, bool DenyMesh = true, bool CbvUavAbove = false, uint BindlessSpaces = 0u);
