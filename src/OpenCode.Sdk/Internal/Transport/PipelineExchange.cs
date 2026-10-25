using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>Creates messages and runs them through the client's immutable policy roster.</summary>
internal sealed class PipelineExchange(ConnectionSnapshot connection,
    TimeSpan? clientNetworkTimeout, ReadOnlyMemory<PipelinePolicy> policies,
    CancellationToken lifetimeToken)
{
    /// <summary>
    /// JSON is UTF-8 by definition (RFC 8259); the content type carries no charset. The
    /// instance is shared across requests and <see cref="MediaTypeHeaderValue"/> is mutable,
    /// so no code may ever write to it.
    /// </summary>
    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");
    private readonly TimeSpan? _clientNetworkTimeout = clientNetworkTimeout;

    public PipelineMessage CreateMessage(OperationRequest operation, bool bufferBody, TimeSpan? perCallNetworkTimeout,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(operation.Method, new Uri(connection.EndpointBase + operation.Route, UriKind.Absolute));
        try
        {
            var message = new PipelineMessage
            {
                Request = request,
                CancellationToken = cancellationToken,
                LifetimeToken = lifetimeToken,
                NetworkToken = cancellationToken,
                NetworkTimeout = NetworkTimeouts.Resolve(operation.Traits, perCallNetworkTimeout, _clientNetworkTimeout),
                PerCallLocation = operation.Options?.Location,
                DeclaredHeaders = operation.DeclaredHeaders,
                BufferBody = bufferBody,
            };
            if (operation.Content is { } content)
            {
                request.Content = new CallerStreamContent(content, message);
            }
            else if (operation.BodyTypeInfo is { } bodyTypeInfo)
            {
                var json = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(operation.Body, bodyTypeInfo));
                json.Headers.ContentType = JsonMediaType;
                request.Content = json;
            }

            return message;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    public ValueTask SendThroughPoliciesAsync(PipelineMessage message) =>
        PipelinePolicy.ProcessNextAsync(message, policies);
}
