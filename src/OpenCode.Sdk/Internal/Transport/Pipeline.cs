using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
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
    private const string EventStreamMediaType = "text/event-stream";

    /// <summary>
    /// JSON is UTF-8 by definition (RFC 8259); the content type carries no charset. The
    /// instance is shared across requests and <see cref="MediaTypeHeaderValue"/> is mutable,
    /// so no code may ever write to it.
    /// </summary>
    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");

    private readonly string _endpointBase;
    private readonly IEventStreamFramer _framer;
    private readonly TimeSpan? _clientNetworkTimeout;
    private readonly TimeSpan _eventStreamIdleWindow;
    private readonly PipelinePolicy[] _policies;
    private readonly TransportPolicy _transport;
    private readonly TimeProvider _time;

    /// <summary>
    /// Cancelled when the pipeline is disposed, so an HTTP exchange or stream ends at once rather than at
    /// its next read or attempt. The token is kept apart from its source: a stream may still link to
    /// it after the source is disposed, and a cancelled token links as cancelled.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private bool _disposed;

    internal Pipeline(HttpClient httpClient, bool ownsHttpClient, OpenCodeClientOptions options,
        IEventStreamFramer? framer = null, ArrayPool<byte>? bufferPool = null, TimeSpan? networkTimeout = null,
        TimeProvider? time = null)
    {
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

        _endpointBase = EndpointPolicy.Normalize(endpoint);
        _framer = framer ?? new ServerSentEventFramer();
        _clientNetworkTimeout = networkTimeout ?? options.NetworkTimeout;
        _eventStreamIdleWindow = options.EventStreamIdleTimeout ?? StreamReconnect.DefaultIdleWindow;
        _time = time ?? TimeProvider.System;
        _lifetimeToken = _lifetime.Token;

        // The options are read exactly once, here: the policies hold an immutable snapshot,
        // so mutating the options object after construction never changes a built client. Only a
        // null password means no credential, as in the pinned client; any other string is sent
        // as written, since a server configured with a whitespace password runs with that one.
        var password = options.Password;
        var authorization = password is null
            ? null
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        _transport = new TransportPolicy(httpClient, ownsHttpClient);
        _policies =
        [
            new RequestDecorationPolicy(authorization, options.Location, UserAgentPolicy.Resolve()),
            new ResponseBufferingPolicy(bufferPool ?? ResponseBufferingPolicy.DefaultPool, _time),
            _transport,
        ];

        // The same snapshot, published for the one door that cannot ride these policies: a PTY
        // WebSocket upgrade builds its own socket and therefore needs the endpoint, the
        // credential, and the ambient location the policies keep to themselves.
        Connection = new ConnectionSnapshot(_endpointBase, authorization, options.Location);
    }

    /// <summary>Gets the construction-time connection facts a non-HTTP door addresses the server with.</summary>
    public ConnectionSnapshot Connection { get; }

    /// <summary>
    /// Gets whether every request this pipeline sends carries a Basic credential. Read from the same
    /// construction-time snapshot the decoration policy holds, so it answers what was actually sent
    /// rather than what the options object says now; only the failure message consults it.
    /// </summary>
    private bool CredentialSent => Connection.Authorization is not null;

    public static Pipeline Create(OpenCodeClientOptions options)
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
            return new Pipeline(httpClient, ownsHttpClient: true, options);
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
        where TResponse : OpenCodeResponse
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return ExecuteCoreAsync<object, TResponse>(operationId, method, route, body: null, bodyTypeInfo: null, adapter, options,
            declaredHeaders: null, cancellationToken);
    }

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
        return ExecuteCoreAsync<object, TResponse>(operationId, method, route, body: null, bodyTypeInfo: null, adapter, options,
            declaredHeaders, cancellationToken);
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
        return ExecuteCoreAsync(operationId, method, route, body, bodyTypeInfo, adapter, options, declaredHeaders: null, cancellationToken);
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
        return ExecuteCoreAsync<object, TResponse>(operationId, method, route, body: null, bodyTypeInfo: null, adapter, options,
            declaredHeaders: null, cancellationToken, content);
    }

    /// <summary>
    /// Opens a streaming operation: the status and content-type walls answer before the
    /// body is read, then each event frame's payload is yielded as it arrives. The
    /// one-shot buffer is never involved, and a stream always throws on an error status —
    /// it has no envelope for one to ride. An operation that reconnects
    /// (<see cref="StreamReconnect"/>) runs one connection after another instead of ending
    /// with the first.
    /// </summary>
    public IAsyncEnumerable<TPayload> ExecuteStreamAsync<TPayload, TCause>(string operationId, HttpMethod method, string route,
        IStreamAdapter<TPayload, TCause> adapter, CancellationToken cancellationToken)
        where TCause : IReadOnlyList<Models.IOpenCodeStreamFailureCause> =>
        ExecuteStreamAsync(operationId, method, route, adapter, options: null, cancellationToken);

    /// <summary>Opens a stream with per-call location and opening timeout; stream failures always throw.</summary>
    public IAsyncEnumerable<TPayload> ExecuteStreamAsync<TPayload, TCause>(string operationId, HttpMethod method, string route,
        IStreamAdapter<TPayload, TCause> adapter, OpenCodeRequestOptions? options, CancellationToken cancellationToken)
        where TCause : IReadOnlyList<Models.IOpenCodeStreamFailureCause>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ValidateRequest(method, route);
        ArgumentNullException.ThrowIfNull(adapter);

        if (route[0] is not '/')
        {
            throw new ArgumentException("Routes must start with '/'.", nameof(route));
        }

        ErrorBehaviorPolicy.RefuseNoThrow(options, "Streams");

        var reconnecting = StreamReconnect.Reconnecting.Contains(operationId);
        if (reconnecting && options?.NetworkTimeout is not null)
        {
            throw new ArgumentException("Reconnecting streams use their own connection and idle windows; NetworkTimeout is not supported.", nameof(options));
        }

        return reconnecting
            ? ExecuteReconnectingStreamAsync(operationId, method, route, adapter, options?.Location, cancellationToken)
            : ExecuteStreamCoreAsync(operationId, method, route, adapter, options?.Location, options?.NetworkTimeout, attempt: null, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _lifetime.Cancel();
        }
        finally
        {
            try
            {
                _transport.Dispose();
            }
            finally
            {
                _lifetime.Dispose();
            }
        }
    }

    private async Task<TResponse> ExecuteCoreAsync<TBody, TResponse>(string operationId, HttpMethod method, string route, TBody? body,
        JsonTypeInfo<TBody>? bodyTypeInfo, ResponseAdapter<TResponse> adapter, OpenCodeRequestOptions? options,
        IReadOnlyList<DeclaredHeader>? declaredHeaders, CancellationToken cancellationToken, Stream? content = null)
        where TBody : class
        where TResponse : OpenCodeResponse
    {
        ValidateRequest(method, route);
        ArgumentNullException.ThrowIfNull(adapter);

        if (route[0] is not '/')
        {
            throw new ArgumentException("Routes must start with '/'.", nameof(route));
        }

        // Refused before sending: an undefined value must never silently pick an error channel.
        var errorBehavior = ErrorBehaviorPolicy.Resolve(options);

        TResponse adapted;
        using (var message = CreateMessage(operationId, method, route, body, bodyTypeInfo, options?.Location, options?.NetworkTimeout, declaredHeaders, cancellationToken))
        {
            if (content is not null)
            {
                message.Request.Content = new CallerStreamContent(content, message);
            }

            await SendThroughPoliciesAsync(message).ConfigureAwait(false);
            adapted = ResponseMaterializer.Materialize(message, adapter);
        }

        return adapted.IsError && errorBehavior is ErrorBehavior.Default
            ? throw CreateApiException(adapted)
            : adapted;
    }

    private void ValidateRequest(HttpMethod method, string route)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw FailureClassification.DisposedClient();
        }
    }

    /// <summary>
    /// Runs one connection after another for a reconnecting operation, as the pinned client does.
    /// Each attempt runs under its own window: 2 seconds to answer and deliver
    /// <c>server.connected</c> first, then the client's idle window without a byte once connected,
    /// stopped while the consumer holds an event. Any failure is followed by a new attempt one
    /// second later. The give-up window runs from the first attempt and again from each drop, stops
    /// once an attempt connects, and when it passes it cuts whatever is in flight and throws the
    /// last failure. The caller's token and a disposed client end the stream at once.
    /// </summary>
    private async IAsyncEnumerable<TPayload> ExecuteReconnectingStreamAsync<TPayload, TCause>(string operationId, HttpMethod method,
        string route, IStreamAdapter<TPayload, TCause> adapter, LocationSelector? perCallLocation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TCause : IReadOnlyList<Models.IOpenCodeStreamFailureCause>
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        using var giveUp = new ProgressWindow(StreamReconnect.GiveUp, _time, lifetime.Token);
        giveUp.Restart();
        ExceptionDispatchInfo? lastFailure = null;
        while (true)
        {
            ThrowIfDisposed();
            var established = false;
            Exception failure;
            var frames = ExecuteAttemptAsync(operationId, method, route, adapter, perCallLocation, giveUp, cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (frames.ConfigureAwait(false))
            {
                while (true)
                {
                    try
                    {
                        if (!await frames.MoveNextAsync().ConfigureAwait(false))
                        {
                            throw new OpenCodeTransportException("The opencode event stream ended.");
                        }
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                        break;
                    }

                    // An attempt yields only once established: its first event was server.connected.
                    established = true;
                    yield return frames.Current;
                }
            }

            lastFailure = EndAttempt(failure, lastFailure, giveUp, cancellationToken);
            if (established)
            {
                giveUp.Restart();
            }

            await WaitToReconnectAsync(giveUp, lastFailure, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One connection of a reconnecting stream under its own window (<see cref="StreamAttempt"/>):
    /// it fails unless its first event is <c>server.connected</c>, stops the give-up window once it
    /// is, and ends when its connection ends. A window of its own passing surfaces as a transport
    /// failure; the give-up window and the caller's token pass through as cancellation.
    /// </summary>
    private async IAsyncEnumerable<TPayload> ExecuteAttemptAsync<TPayload, TCause>(string operationId, HttpMethod method, string route,
        IStreamAdapter<TPayload, TCause> adapter, LocationSelector? perCallLocation, ProgressWindow giveUp,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TCause : IReadOnlyList<Models.IOpenCodeStreamFailureCause>
    {
        using var attempt = new StreamAttempt(_time, _eventStreamIdleWindow, giveUp.Token);
        var frames = ExecuteStreamCoreAsync(operationId, method, route, adapter, perCallLocation, Timeout.InfiniteTimeSpan, attempt, attempt.Token)
            .GetAsyncEnumerator(cancellationToken);
        await using var enumeration = frames.ConfigureAwait(false);
        while (true)
        {
            bool moved;
            try
            {
                moved = await frames.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!giveUp.IsCancellationRequested)
            {
                throw attempt.Describe(exception);
            }

            if (!moved)
            {
                yield break;
            }

            if (!attempt.IsEstablished)
            {
                attempt.Establish();
                giveUp.Stop();
            }

            // The consumer's time with an event is not the server's silence.
            attempt.Pause();
            yield return frames.Current;
            attempt.Resume();
        }
    }

    /// <summary>
    /// Ends the stream when the attempt's failure ends it: the caller cancelled, the client was
    /// disposed, or the give-up window passed. A give-up throws the attempt's own failure when the
    /// attempt failed on its own, and the failure before it when the give-up cut it. Otherwise the
    /// failure is recorded as the last one.
    /// </summary>
    private ExceptionDispatchInfo EndAttempt(Exception failure, ExceptionDispatchInfo? lastFailure, ProgressWindow giveUp,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw FailureClassification.CallerCancellation(failure, "The opencode event stream read was canceled.", cancellationToken);
        }

        ThrowIfDisposed();
        if (giveUp.IsCancellationRequested)
        {
            if (failure is not OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            lastFailure?.Throw();
            var seconds = StreamReconnect.GiveUp.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
            throw new OpenCodeTransportException($"The opencode event stream did not connect within {seconds} seconds.", failure);
        }

        return ExceptionDispatchInfo.Capture(failure);
    }

    /// <summary>
    /// Waits before the next attempt; the give-up window passing during the wait throws the last
    /// failure, and a disposed client ends the wait at once.
    /// </summary>
    private async Task WaitToReconnectAsync(ProgressWindow giveUp, ExceptionDispatchInfo lastFailure, CancellationToken cancellationToken)
    {
        try
        {
#if NET
            await Task.Delay(StreamReconnect.Delay, _time, giveUp.Token).ConfigureAwait(false);
#else
            await _time.Delay(StreamReconnect.Delay, giveUp.Token).ConfigureAwait(false);
#endif
        }
        catch (OperationCanceledException canceled)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw FailureClassification.CallerCancellation(canceled, "The opencode event stream read was canceled.", cancellationToken);
            }

            ThrowIfDisposed();
            lastFailure.Throw();
        }
    }

    private async IAsyncEnumerable<TPayload> ExecuteStreamCoreAsync<TPayload, TCause>(string operationId, HttpMethod method, string route,
        IStreamAdapter<TPayload, TCause> adapter, LocationSelector? perCallLocation, TimeSpan? networkTimeout, StreamAttempt? attempt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TCause : IReadOnlyList<Models.IOpenCodeStreamFailureCause>
    {
        ThrowIfDisposed();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        using var message = CreateStreamMessage(operationId, method, route, perCallLocation, networkTimeout, cancellationToken);
        await SendThroughPoliciesAsync(message).ConfigureAwait(false);
        var response = message.Response!;
        var status = (int)response.StatusCode;
        switch (adapter.Classify(status))
        {
            case StatusVerdict.Success:
                break;
            case StatusVerdict.UndeclaredSuccess:
                throw StatusVerdictFailures.UndeclaredSuccess(status);
            case StatusVerdict.DeclaredError:
            case StatusVerdict.UndeclaredError:
                var rawBody = ResponseMaterializer.ReadErrorBody(message);
                throw OpenCodeErrorReader.CreateApiException(
                    status, adapter.ReadError(status, rawBody), rawBody, CredentialSent);
            default:
                // A stream contract has no no-content success; any other verdict is a
                // generator defect, refused rather than guessed around.
                throw new InvalidOperationException("The stream adapter produced a verdict its contract cannot declare.");
        }

        // A success that is not an event stream cannot be framed, so it fails as a
        // protocol error rather than being read as one frame of garbage. Media types are
        // case-insensitive, so a proxy that rewrites the casing still matches.
        if (!string.Equals(response.Content?.Headers.ContentType?.MediaType, EventStreamMediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenCodeTransportException("The opencode API answered a streaming operation without an event-stream body.");
        }

        // A yield suspends the ownership chain; cancellation must release the response even
        // while no read is running and the consumer has not asked for another frame.
        // API-shape divergence: a registration has no DisposeAsync downlevel.
#if NET
        await using var cancellation = RegisterStreamCancellation(response, lifetime.Token).ConfigureAwait(false);
#else
        using var cancellation = RegisterStreamCancellation(response, lifetime.Token);
#endif

        // The body is read through a view that reports each read bringing bytes to the reconnect
        // loop's idle window; disposing the view disposes the body a moment before the message would.
        // API-shape divergence, not algorithm: a stream has no DisposeAsync downlevel.
        var body = new ActivityStream(await ReadBodyStreamAsync(message, lifetime.Token).ConfigureAwait(false), attempt is null ? null : attempt.OnActivity);
#if NET
        await using var observation = body.ConfigureAwait(false);
#else
        using var observation = body;
#endif
        var frames = _framer.ReadAsync(body, lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        await using var enumeration = frames.ConfigureAwait(false);
        while (true)
        {
            // A yield cannot sit inside a try with a catch, so the read is guarded on its
            // own: a connection dying mid-stream is a transport failure here exactly as it
            // is on the one-shot path.
            try
            {
                var moved = await frames.MoveNextAsync().ConfigureAwait(false);
                if (!moved)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    break;
                }
            }
            catch (Exception exception) when (FailureClassification.Handles(exception, FailurePhase.EventStreamRead))
            {
                throw FailureClassification.Map(exception, FailurePhase.EventStreamRead, cancellationToken, _lifetimeToken);
            }
            catch (Exception exception) when (lifetime.IsCancellationRequested)
            {
                throw FailureClassification.MapFault(exception, FailurePhase.EventStreamRead, cancellationToken, _lifetimeToken);
            }

            attempt?.OnFrame(frames.Current);
            yield return FrameDispatch.ReadPayload(frames.Current, adapter);
        }
    }

    private ValueTask SendThroughPoliciesAsync(PipelineMessage message) =>
        _policies[0].ProcessAsync(message, _policies.AsMemory(1));

    private PipelineMessage CreateMessage<TBody>(string operationId, HttpMethod method, string route, TBody? body,
        JsonTypeInfo<TBody>? bodyTypeInfo, LocationSelector? perCallLocation, TimeSpan? perCallNetworkTimeout,
        IReadOnlyList<DeclaredHeader>? declaredHeaders, CancellationToken cancellationToken)
        where TBody : class
    {
        var request = new HttpRequestMessage(method, new Uri(_endpointBase + route, UriKind.Absolute));
        try
        {
            if (body is not null)
            {
                request.Content = CreateJsonContent(body, bodyTypeInfo!);
            }

            return new PipelineMessage
            {
                OperationId = operationId,
                Request = request,
                CancellationToken = cancellationToken,
                LifetimeToken = _lifetimeToken,
                NetworkToken = cancellationToken,
                NetworkTimeout = NetworkTimeouts.Resolve(operationId, perCallNetworkTimeout, _clientNetworkTimeout),
                PerCallLocation = perCallLocation,
                DeclaredHeaders = declaredHeaders,
            };
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private PipelineMessage CreateStreamMessage(string operationId, HttpMethod method, string route, LocationSelector? perCallLocation,
        TimeSpan? perCallNetworkTimeout, CancellationToken cancellationToken) =>
        new()
        {
            OperationId = operationId,
            Request = new HttpRequestMessage(method, new Uri(_endpointBase + route, UriKind.Absolute)),
            CancellationToken = cancellationToken,
            LifetimeToken = _lifetimeToken,
            NetworkToken = cancellationToken,
            NetworkTimeout = NetworkTimeouts.Resolve(operationId, perCallNetworkTimeout, _clientNetworkTimeout),
            PerCallLocation = perCallLocation,
            BufferBody = false,
        };

    private static ByteArrayContent CreateJsonContent<TBody>(TBody body, JsonTypeInfo<TBody> bodyTypeInfo)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, bodyTypeInfo));
        content.Headers.ContentType = JsonMediaType;
        return content;
    }

    private OpenCodeApiException CreateApiException(OpenCodeResponse response) =>
        OpenCodeErrorReader.CreateApiException(response.Status, response.Error, response.RawBody, CredentialSent);

    private static async Task<Stream> ReadBodyStreamAsync(PipelineMessage message, CancellationToken streamToken)
    {
        try
        {
            return await message.Response!.Content.ReadAsStreamAsync(streamToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FailureClassification.Handles(exception, FailurePhase.EventStreamRead))
        {
            throw FailureClassification.Map(exception, FailurePhase.EventStreamRead, message.CancellationToken, message.LifetimeToken);
        }
        catch (Exception exception) when (streamToken.IsCancellationRequested)
        {
            throw FailureClassification.MapFault(exception, FailurePhase.EventStreamRead, message.CancellationToken, message.LifetimeToken);
        }
    }

    private static CancellationTokenRegistration RegisterStreamCancellation(
        HttpResponseMessage response, CancellationToken cancellationToken) =>
        cancellationToken.UnsafeRegister(
            // The registration always supplies its non-null response as state.
            static state => ((HttpResponseMessage)state!).Dispose(),
            response);
}
