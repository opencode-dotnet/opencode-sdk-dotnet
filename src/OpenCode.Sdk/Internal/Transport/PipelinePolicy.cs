namespace OpenCode.Sdk.Internal;

/// <summary>
/// One lifecycle stage of the request pipeline. Policies compose as an immutable array and
/// pass the remaining slice forward, so each policy owns exactly what happens before and
/// after the stages behind it; the last policy is the transport and passes nothing on.
/// </summary>
internal abstract class PipelinePolicy
{
    /// <summary>Processes the message, forwarding through <paramref name="remaining"/> when the stage continues.</summary>
    public abstract ValueTask ProcessAsync(PipelineMessage message, ReadOnlyMemory<PipelinePolicy> remaining);

    /// <summary>Runs the rest of the pipeline behind this policy.</summary>
    protected static ValueTask ProcessNextAsync(PipelineMessage message, ReadOnlyMemory<PipelinePolicy> remaining) =>
        remaining.Span[0].ProcessAsync(message, remaining[1..]);
}
