namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// A streaming operation's contract. A stream yields its payloads directly rather than through
/// a response envelope, so it carries no envelope plan; the pipeline owns per-call options.
/// </summary>
internal sealed record StreamPlan
{
    /// <summary>Gets the type each dispatched frame carries, read from the frame's JSON-encoded data field.</summary>
    public required string PayloadTypeName { get; init; }

    /// <summary>Gets the payload union's concrete unknown carrier, or null for a non-union payload.</summary>
    public string? UnknownPayloadTypeName { get; init; }

    /// <summary>Gets the generated adapter carrying this operation's stream contract.</summary>
    public required string AdapterTypeName { get; init; }

    /// <summary>Gets the event name a mid-stream failure frame carries, from <c>x-effect-stream.failureEvent</c>.</summary>
    public required string FailureEventName { get; init; }

    /// <summary>Gets the source-generated type used to materialize the failure frame's cause array.</summary>
    public required string CauseTypeName { get; init; }
}
