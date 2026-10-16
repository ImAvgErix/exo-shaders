namespace SCSKiller.Core.Planning;

public sealed record RootSigRule(StockRule? Stock = null, string[]? Select = null, StagesDesc[]? ByStages = null, SamplerSet? StaticSamplers = null, ReRule? Re = null, uint[][]? Samplers = null);
