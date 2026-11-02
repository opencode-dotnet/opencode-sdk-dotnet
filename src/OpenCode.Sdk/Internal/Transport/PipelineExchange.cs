#if !NETSTANDARD2_0
using System.Buffers;
#endif
using System.Net.Http.Headers;
#if NETSTANDARD2_0
using System.Text.Json;
#else
using System.Text.Json.Serialization.Metadata;
#endif

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
#if !NETSTANDARD2_0
    private JsonBodyWriter? _idleBodyWriter;

    /// <summary>Gets the pool a request body rents from; a test observes it.</summary>
    internal ArrayPool<byte> BodyPool { get; init; } = ResponseBufferingPolicy.DefaultPool;
#endif

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
#if NETSTANDARD2_0
                var json = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(operation.Body, bodyTypeInfo));
#else
                var json = WriteBody(operation.Body, bodyTypeInfo);
#endif
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

#if !NETSTANDARD2_0
    private PooledJsonContent WriteBody(object? body, JsonTypeInfo typeInfo)
    {
        var writer = Interlocked.Exchange(ref _idleBodyWriter, null);
        if (writer is null || !ReferenceEquals(writer.Options, typeInfo.Options))
        {
            writer = JsonBodyWriter.Create(typeInfo.Options);
        }

        var content = writer.Write(body, typeInfo, BodyPool);
        Volatile.Write(ref _idleBodyWriter, writer);
        return content;
    }
#endif
}
