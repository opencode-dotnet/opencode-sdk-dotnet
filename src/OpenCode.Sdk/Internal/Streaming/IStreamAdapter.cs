using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Carries one streaming operation's contract across the generated-to-hand-written seam:
/// how each frame's payload deserializes, and how a status refused before the stream opens
/// maps onto a typed error. A stream has no envelope to answer on, so an error always throws.
/// </summary>
/// <typeparam name="TPayload">The type each event frame carries.</typeparam>
/// <typeparam name="TCause">The collection type carried by the declared failure frame.</typeparam>
internal interface IStreamAdapter<TPayload, TCause>
    where TCause : IReadOnlyList<IOpenCodeStreamFailureCause>
{
    /// <summary>
    /// Classifies a status under the operation's pinned contract. Generated from the status
    /// table; the single authority the stream plane switches on before the body opens.
    /// </summary>
    public StatusVerdict Classify(int status);

    /// <summary>
    /// Gets the event name a mid-stream failure frame carries, from the operation's declared
    /// <c>x-effect-stream.failureEvent</c>. A frame answering to this name reports the
    /// stream's failure instead of carrying a payload.
    /// </summary>
    public string FailureEventName { get; }

    /// <summary>Gets the source-generated metadata each frame's payload is read through.</summary>
    public JsonTypeInfo<TPayload> PayloadTypeInfo { get; }

    /// <summary>Gets the source-generated metadata the failure frame's cause is read through.</summary>
    public JsonTypeInfo<TCause> CauseTypeInfo { get; }

    /// <summary>Maps an error status onto its declared tags, tolerating an unparseable body.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="rawBody">The buffered error body.</param>
    /// <returns>The typed error, or <see langword="null"/> when the body could not be parsed.</returns>
    public IOpenCodeError? ReadError(int status, string rawBody);
}
