namespace SCSKiller.Core.Planning;

/// <summary>An engine's view instancing: the views every PSO it builds with view instancing declares (each a viewport and
/// render-target array index), and the D3D12_VIEW_INSTANCING_FLAGS. <paramref name="Libraries"/>: the shader libraries
/// (shader maps' <c>Library</c>) whose shaders get it; null: every shader.</summary>
public sealed record ViRule(string Note, uint[][] Views, uint Flags = 0, string[]? Libraries = null)
{
    /// <summary>The view-instancing subobject these views make (<see cref="PsoDb.ViewInstancingBytes"/>).</summary>
    public byte[] Bytes() => PsoDb.ViewInstancingBytes([.. Views.Select(v => (v[0], v[1]))], Flags);
}
