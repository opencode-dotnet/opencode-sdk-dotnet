using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Dispatches one framed event against a streaming operation's contract: the declared
/// failure name throws the typed stream failure, the default name yields the payload, and
/// any other name is refused — the contract leaves every other name undeclared.
/// </summary>
internal static class FrameDispatch
{
    public static TPayload ReadPayload<TPayload, TCause>(ServerSentEvent frame, IStreamAdapter<TPayload, TCause> adapter)
        where TCause : IReadOnlyList<IOpenCodeStreamFailureCause>
    {
        ArgumentNullException.ThrowIfNull(adapter);

        if (string.Equals(frame.Name, adapter.FailureEventName, StringComparison.Ordinal))
        {
            throw new OpenCodeStreamFailureException(ReadCause(frame.Data.Span, adapter.CauseTypeInfo));
        }

        if (!string.Equals(frame.Name, ServerSentEvent.DefaultName, StringComparison.Ordinal))
        {
            throw new OpenCodeTransportException($"The opencode event stream produced an undeclared frame named '{frame.Name}'.");
        }

        return ReadFramePayload(frame.Data.Span, adapter.PayloadTypeInfo, adapter.UnknownPayloadTypeInfo);
    }

    private static TCause ReadCause<TCause>(ReadOnlySpan<byte> utf8Payload, JsonTypeInfo<TCause> typeInfo)
        where TCause : IReadOnlyList<IOpenCodeStreamFailureCause>
    {
        try
        {
            return JsonSerializer.Deserialize(utf8Payload, typeInfo)
                   ?? throw new OpenCodeTransportException("The opencode event stream produced a null failure cause.");
        }
        catch (JsonException exception)
        {
            throw new OpenCodeTransportException("The opencode event stream produced an unmaterializable failure cause.", exception);
        }
    }

    /// <summary>Reads the payload, preserving an unreadable known shape through an admitted unknown arm.</summary>
    private static TPayload ReadFramePayload<TPayload>(
        ReadOnlySpan<byte> utf8Payload, JsonTypeInfo<TPayload> typeInfo, JsonTypeInfo? unknownTypeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8Payload, typeInfo)
                   ?? throw new OpenCodeTransportException("The opencode event stream produced a null frame payload.");
        }
        catch (JsonException exception)
        {
            var failure = new OpenCodeTransportException("The opencode event stream produced a malformed frame payload.", exception);
            if (unknownTypeInfo is null)
            {
                throw failure;
            }

            try
            {
                return JsonSerializer.Deserialize(utf8Payload, unknownTypeInfo) is TPayload payload
                    ? payload
                    : throw failure;
            }
            catch (JsonException)
            {
                throw failure;
            }
        }
    }
}
