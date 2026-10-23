namespace SCSKiller.Core.Vulkan;

/// <summary>A pipeline of a Fossilize archive (graphics, compute or ray tracing). <see cref="Library"/>: created as a
/// pipeline library (VK_PIPELINE_CREATE_LIBRARY_BIT_KHR); <see cref="RayQuery"/>: one of its shaders traces rays inline;
/// <see cref="Needs"/>: the names of the pipelines of its kind it refers to (libraries, base pipeline), which a replay
/// must create first; <see cref="DescriptorHeap"/>: created for descriptor heaps.</summary>
public sealed record VkPipeline(Foz.Entry Entry, bool Library, bool RayQuery, IReadOnlyList<string> Needs)
{
    public string Name => Entry.Name;

    public bool DescriptorHeap { get; init; }

    public FozOrigin Origin { get; init; }
}
