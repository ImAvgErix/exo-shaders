using System.Collections.Generic;

namespace SCSKiller.Core;

public sealed record ShaderInfo(string Sha1, Stage Stage, string ShaderModel, int Size, ResourceCounts Counts, IReadOnlyList<Binding> Bindings, IReadOnlyList<SigElement> Inputs, IReadOnlyList<SigElement> Outputs, int GsInputPrimitive = 0, string? RootSignature = null, bool InlineRayTracing = false, byte[]? EngineHeader = null, int Payload = 0);
