using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization.Metadata;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The composed request pipeline behind every operation: an immutable policy roster —
/// decoration, buffering, transport — carries each message, and the materializer maps the
/// buffered result onto the operation's envelope. Construction owns option validation and
/// decides transport ownership; the planes are the narrative behind the generated-facing
/// entry points.
/// </summary>
internal sealed class Pipeline : IDisposable
{
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly TransportPolicy _transport;
    private readonly ClientLifetime _lifetime;
    private readonly PipelineExchange _exchange;
    private readonly StreamPlane _streamPlane;
    private readonly TimeSpan _idleWindow;
    private readonly Lock _disposalGate = new();
    private readonly List<IDisposable> _ownedResources = [];
    private bool _disposalStarted;

    internal Pipeline(HttpClient httpClient, bool ownsHttpClient, OpenCodeClientOptions options,
        IEventStreamFramer? framer = null, ArrayPool<byte>? bufferPool = null,
        TimeProvider? time = null)
    {
        _lifetime = new ClientLifetime(_lifetimeSource.Token);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        var endpoint = options.Endpoint ?? throw new ArgumentException("OpenCodeClientOptions.Endpoint is required.", nameof(options));

        // Basic credentials ride the wire as "username:password", so a blank name or a colon
        // inside it would corrupt the header instead of failing a login.
        var username = options.Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("The username cannot be empty or whitespace.", nameof(options));
        }

        if (username.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("The username cannot contain a colon.", nameof(options));
        }

        var endpointBase = EndpointPolicy.Normalize(endpoint);
        framer ??= new ServerSentEventFramer();
        _idleWindow = options.EventStreamIdleTimeout ?? StreamReconnect.DefaultIdleWindow;
#pragma warning disable RS0030 // The client pipeline composition root chooses the system clock only when no internal provider was supplied.
        time ??= TimeProvider.System;
#pragma warning restore RS0030

        // The options are read exactly once, here: the policies hold an immutable snapshot,
        // so mutating the options object after construction never changes a built client. Only a
        // null password means no credential, as in the pinned client; any other string is sent
        // as written, since a server configured with a whitespace password runs with that one.
        var password = options.Password;
        foreach (var name in options.Headers.Keys)
        {
            var conflict = name switch
            {
                _ when password is not null && string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) => nameof(options.Password),
                _ when options.Location is not null && string.Equals(name, "x-opencode-directory", StringComparison.OrdinalIgnoreCase) => nameof(options.Location),
                _ => null,
            };
            if (conflict is not null)
            {
                throw new ArgumentException(
                    $"OpenCodeClientOptions.Headers contains {name}, which conflicts with OpenCodeClientOptions.{conflict}.", nameof(options));
            }
        }

        var authorization = password is null
            ? null
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        _transport = new TransportPolicy(httpClient, ownsHttpClient);
        PipelinePolicy[] policies =
        [
            new RequestDecorationPolicy(authorization, options.Location, UserAgentPolicy.Resolve(),
                RequestDecorationPolicy.SnapshotHeaders(options.Headers)),
            new ResponseBufferingPolicy(bufferPool ?? ResponseBufferingPolicy.DefaultPool, time),
            _transport,
        ];

        // The same snapshot, published for the one door that cannot ride these policies: a PTY
        // WebSocket upgrade builds its own socket and therefore needs the endpoint, the
        // credential, and the ambient location the policies keep to themselves.
        Connection = new ConnectionSnapshot(endpointBase, authorization, options.Location, time);
        _exchange = new PipelineExchange(Connection, options.NetworkTimeout, policies, _lifetime.Token);
        _streamPlane = new StreamPlane(_exchange, framer, _lifetime, Connection);
    }

    /// <summary>Gets the construction-time connection facts a non-HTTP door addresses the server with.</summary>
    public ConnectionSnapshot Connection { get; }

    internal StreamPlane StreamPlane => _streamPlane;
    internal ClientLifetime Lifetime => _lifetime;
    internal TimeSpan IdleWindow => _idleWindow;

    public static Pipeline Create(OpenCodeClientOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Endpoint is null)
        {
            throw new ArgumentException("OpenCodeClientOptions.Endpoint is required.", nameof(options));
        }

        // Validate before constructing the owned client so a refused endpoint leaks nothing.
        _ = EndpointPolicy.Normalize(options.Endpoint);
        var httpClient = TransportPolicy.CreateOwnedHttpClient(options.Endpoint);
        try
        {
            return new Pipeline(httpClient, ownsHttpClient: true, options, time: time);
        }
        catch
        {
            // Credential validation still throws inside the constructor; the owned
            // transport must not outlive a refused construction.
            httpClient.Dispose();
            throw;
        }
    }

    public static Pipeline Create(HttpClient httpClient, OpenCodeClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Endpoint is null)
        {
            throw new ArgumentException("A caller-supplied HttpClient requires OpenCodeClientOptions.Endpoint.", nameof(options));
        }

        return new Pipeline(httpClient, ownsHttpClient: false, options);
    }

    public Task<TResponse> ExecuteAsync<TResponse>(string operationId, HttpMethod method, string route, ResponseAdapter<TResponse> adapter,
        OpenCodeRequestOptions? options, CancellationToken cancellationToken)
        where TResponse : OpenCodeResponse =>
        ExecuteAsync(operationId, method, route, adapter, options, declaredHeaders: null, cancellationToken);

    /// <summary>
    /// The bodiless overload a generated internal-raw method calls when its operation declares
    /// header parameters. The headers ride the message rather than the route or the body, so the
    /// decoration policy applies them without learning anything about the operation. Only the two
    /// pinned connect-token operations declare a header and neither carries a body, so no
    /// body-bearing counterpart exists; one would surface as a generated-code compile failure
    /// rather than silently dropping the headers.
    /// </summary>
    public Task<TResponse> ExecuteAsync<TResponse>(string operationId, HttpMethod method, string route, ResponseAdapter<TResponse> adapter,
        OpenCodeRequestOptions? options, IReadOnlyList<DeclaredHeader>? declaredHeaders, CancellationToken cancellationToken)
        where TResponse : OpenCodeResponse
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var request = OperationRequest.Describe<object>(operationId, method, route, body: null, bodyTypeInfo: null,
            content: null, options, declaredHeaders);
        return ExecuteCoreAsync(request, adapter, cancellationToken);
    }

    public Task<TResponse> ExecuteAsync<TBody, TResponse>(string operationId, HttpMethod method, string route, TBody body,
        JsonTypeInfo<TBody> bodyTypeInfo, ResponseAdapter<TResponse> adapter, OpenCodeRequestOptions? options,
        CancellationToken cancellationToken)
        where TBody : class
        where TResponse : OpenCodeResponse
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(bodyTypeInfo);
        var request = OperationRequest.Describe(operationId, method, route, body, bodyTypeInfo, content: null, options, declaredHeaders: null);
        return ExecuteCoreAsync(request, adapter, cancellationToken);
    }

    /// <summary>
    /// The overload a generated method calls when its operation takes raw bytes: the caller's
    /// stream is the body, sent as it is (<see cref="CallerStreamContent"/>).
    /// </summary>
    public Task<TResponse> ExecuteAsync<TResponse>(string operationId, HttpMethod method, string route, Stream content,
        ResponseAdapter<TResponse> adapter, OpenCodeRequestOptions? options, CancellationToken cancellationToken)
        where TResponse : OpenCodeResponse
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(content);
        var request = OperationRequest.Describe<object>(operationId, method, route, body: null, bodyTypeInfo: null,
            content, options, declaredHeaders: null);
        return ExecuteCoreAsync(request, adapter, cancellationToken);
    }

    /// <summary>
    /// Opens a streaming operation: the status and content-type walls answer before the
    /// body is read, then each event frame's payload is yielded as it arrives. The
    /// one-shot buffer is never involved, and a stream always throws on an error status —
    /// it has no envelope for one to ride. Reconnecting operations are refused: their hand-written
    /// family door owns the shared source. Other streams keep per-call location and an opening-only timeout.
    /// </summary>
    public IAsyncEnumerable<TPayload> ExecuteStreamAsync<TPayload, TCause>(string operationId, HttpMethod method, string route,
        IStreamAdapter<TPayload, TCause> adapter, OpenCodeRequestOptions? requestOptions, CancellationToken cancellationToken)
        where TCause : IReadOnlyList<Models.IOpenCodeStreamFailureCause>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        _lifetime.ThrowIfDisposed();
        var request = OperationRequest.Describe<object>(operationId, method, route, body: null, bodyTypeInfo: null,
            content: null, requestOptions, declaredHeaders: null);
        request.Validate();
        ArgumentNullException.ThrowIfNull(adapter);

        ErrorBehaviorPolicy.RefuseNoThrow(requestOptions, "Streams");

        var reconnecting = request.Traits.Reconnects;
        if (reconnecting && requestOptions?.NetworkTimeout is not null)
        {
            throw new ArgumentException(FailureClassification.EventStreamNetworkTimeoutMessage, nameof(requestOptions));
        }

        if (reconnecting)
        {
            throw new InvalidOperationException($"Operation '{operationId}' must be opened through its shared event feed.");
        }

        return _streamPlane.ExecuteStreamCoreAsync(request, adapter, attempt: null, cancellationToken);
    }

    /// <summary>
    /// Lets a hand-written family door own a resource whose life ends with the root client, before the transport.
    /// Registration and disposal share one admission boundary, even while an earlier resource's cleanup is still running.
    /// </summary>
    internal void RegisterOwned(IDisposable resource)
    {
        lock (_disposalGate)
        {
            if (_disposalStarted)
            {
                throw FailureClassification.DisposedClient();
            }

            _ownedResources.Add(resource);
        }
    }

    public void Dispose()
    {
        IDisposable[] resources;
        lock (_disposalGate)
        {
            if (_disposalStarted)
            {
                return;
            }

            _disposalStarted = true;
            resources = [.. _ownedResources];
            _ownedResources.Clear();
        }

        void DisposeOwned(int index)
        {
            if (index < 0)
            {
                return;
            }

            try
            {
                resources[index].Dispose();
            }
            finally
            {
                DisposeOwned(index - 1);
            }
        }

        try
        {
            DisposeOwned(resources.Length - 1);
        }
        finally
        {
            if (_lifetime.TryBeginDispose())
            {
                try
                {
                    _lifetimeSource.Cancel();
                }
                finally
                {
                    try
                    {
                        _transport.Dispose();
                    }
                    finally
                    {
                        _lifetimeSource.Dispose();
                    }
                }
            }
        }
    }

    private async Task<TResponse> ExecuteCoreAsync<TResponse>(OperationRequest request, ResponseAdapter<TResponse> adapter,
        CancellationToken cancellationToken)
        where TResponse : OpenCodeResponse
    {
        _lifetime.ThrowIfDisposed();
        request.Validate();
        ArgumentNullException.ThrowIfNull(adapter);

        // Refused before sending: an undefined value must never silently pick an error channel.
        var errorBehavior = ErrorBehaviorPolicy.Resolve(request.Options);

        TResponse adapted;
        using (var message = _exchange.CreateMessage(request, bufferBody: true, request.Options?.NetworkTimeout, cancellationToken))
        {
            await _exchange.SendThroughPoliciesAsync(message).ConfigureAwait(false);
            adapted = ResponseMaterializer.Materialize(message, adapter);
        }

        return adapted.IsError && errorBehavior is ErrorBehavior.Default
            ? throw CreateApiException(adapted)
            : adapted;
    }

    private OpenCodeApiException CreateApiException(OpenCodeResponse response) =>
        OpenCodeErrorReader.CreateApiException(response.Status, response.Error, response.RawBody, Connection.Authorization is not null);
}
