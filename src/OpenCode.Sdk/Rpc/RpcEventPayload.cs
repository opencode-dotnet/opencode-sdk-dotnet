using OpenCode.Sdk.Models;

namespace OpenCode.Sdk;

/// <summary>
/// One event of an RPC definition: the shared feed's event beside its data read with the event's JSON metadata.
/// </summary>
/// <remarks>
/// This projects upstream's payload, whose envelope itself has typed data, without copying the shared envelope.
/// </remarks>
/// <typeparam name="TData">The event's data type.</typeparam>
public sealed record RpcEventPayload<TData>
{
    /// <summary>Gets the feed's shared event instance; treat it as read-only.</summary>
    public required EventRpc Event { get; init; }

    /// <summary>
    /// Gets the event's data read with <see cref="RpcEventDefinition{TData}.Data"/>; a null read is refused.
    /// </summary>
    public required TData Data { get; init; }
}
