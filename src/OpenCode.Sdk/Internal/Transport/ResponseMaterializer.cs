namespace OpenCode.Sdk.Internal;

/// <summary>
/// Materializes a completed pipeline message for both planes through one decoding policy:
/// success bodies materialize from validated UTF-8 when possible, and error bodies stay
/// decoded strings so both error channels retain the exact raw body.
/// </summary>
internal static class ResponseMaterializer
{
    /// <summary>Maps the buffered response onto the operation's typed envelope.</summary>
    public static TResponse Materialize<TResponse>(PipelineMessage message, ResponseAdapter<TResponse> adapter)
        where TResponse : OpenCodeResponse
    {
        var status = (int)message.Response!.StatusCode;
        switch (adapter.Classify(status))
        {
            case StatusVerdict.Success when adapter.BinarySuccess:
                // Raw bytes are not text: no charset decode, no UTF-8 validation.
                var raw = message.Body ?? ResponseBody.Empty;
                return adapter.AdaptBinary(
                    status,
                    raw.Bytes.AsSpan(0, raw.Length),
                    message.Response.Content?.Headers.ContentType?.ToString());
            case StatusVerdict.Success:
                var encodedBody = Decode(message);
                return encodedBody.DecodedBody is { } decoded
                    ? adapter.Adapt(status, decoded)
                    : adapter.AdaptSuccess(status, encodedBody.Utf8Body.Span);
            case StatusVerdict.NoContentSuccess:
                // An unexpected body was drained into the buffer and is ignored here (canon).
                return adapter.AdaptSuccess(status, []);
            case StatusVerdict.DeclaredError:
            case StatusVerdict.UndeclaredError:
                return adapter.Adapt(status, Decode(message).GetDecodedBody());
            case StatusVerdict.UndeclaredSuccess:
                throw StatusVerdictFailures.UndeclaredSuccess(status);
            default:
                throw new InvalidOperationException("The adapter produced an unknown status verdict.");
        }
    }

    /// <summary>Reads a buffered error body as the decoded string both error channels retain.</summary>
    public static string ReadErrorBody(PipelineMessage message) => Decode(message).GetDecodedBody();

    private static EncodedResponseBody Decode(PipelineMessage message)
    {
        var body = message.Body ?? ResponseBody.Empty;
        try
        {
            return ResponseEncodingPolicy.Decode(
                body.Bytes,
                body.Length,
                message.Response!.Content?.Headers.ContentType?.CharSet);
        }
        catch (Exception exception) when (FailureClassification.Handles(exception, FailurePhase.ResponseBodyRead))
        {
            // The declared-charset refusal surfaces here as an InvalidOperationException and
            // stays a body-read failure, exactly as it was when decoding rode the read itself.
            throw FailureClassification.Map(exception, FailurePhase.ResponseBodyRead, message.CancellationToken);
        }
    }
}
