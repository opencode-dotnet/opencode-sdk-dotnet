using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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
    private readonly TimeSpan _networkTimeout;
    private readonly PipelinePolicy[] _policies;
    private readonly TransportPolicy _transport;
    private bool _disposed;

    internal Pipeline(HttpClient httpClient, bool ownsHttpClient, IOpenCodeClientOptions options,
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
        _networkTimeout = networkTimeout ?? PipelineMessage.DefaultNetworkTimeout;

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
            new ResponseBufferingPolicy(bufferPool ?? ArrayPool<byte>.Shared, time ?? TimeProvider.System),
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

    public Task<TResponse> ExecuteAsync<TResponse>(HttpMethod method, string route, ResponseAdapter<TResponse> adapter,
        OpenCodeRequestOptions? options, CancellationToken cancellationToken)
        where TResponse : OpenCodeResponse =>
        ExecuteCoreAsync<object, TResponse>(method, route, body: null, bodyTypeInfo: null, adapter, options,
            declaredHeaders: null, cancellationToken);

    /// <summary>
    /// The bodiless overload a generated internal-raw method calls when its operation declares
    /// header parameters. The headers ride the message rather than the route or the body, so the
    /// decoration policy applies them without learning anything about the operation. Only the two
    /// pinned connect-token operations declare a header and neither carries a body, so no
    /// body-bearing counterpart exists; one would surface as a generated-code compile failure
    /// rather than silently dropping the headers.
    /// </summary>
    public Task<TResponse> ExecuteAsync<TResponse>(HttpMethod method, string route, ResponseAdapter<TResponse> adapter,
        OpenCodeRequestOptions? options, IReadOnlyList<DeclaredHeader>? declaredHeaders, CancellationToken cancellationToken)
        where TResponse : OpenCodeResponse =>
        ExecuteCoreAsync<object, TResponse>(method, route, body: null, bodyTypeInfo: null, adapter, options,
            declaredHeaders, cancellationToken);

    public Task<TResponse> ExecuteAsync<TBody, TResponse>(HttpMethod method, string route, TBody body,
        JsonTypeInfo<TBody> bodyTypeInfo, ResponseAdapter<TResponse> adapter, OpenCodeRequestOptions? options,
        CancellationToken cancellationToken)
        where TBody : class
        where TResponse : OpenCodeResponse
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(bodyTypeInfo);
        return ExecuteCoreAsync(method, route, body, bodyTypeInfo, adapter, options, declaredHeaders: null, cancellationToken);
    }

    /// <summary>
    /// The overload a generated method calls when its operation takes raw bytes: the caller's
    /// stream is the body, sent as it is (<see cref="CallerStreamContent"/>).
    /// </summary>
    public Task<TResponse> ExecuteAsync<TResponse>(HttpMethod method, string route, Stream content,
        ResponseAdapter<TResponse> adapter, OpenCodeRequestOptions? options, CancellationToken cancellationToken)
        where TResponse : OpenCodeResponse
    {
        ArgumentNullException.ThrowIfNull(content);
        return ExecuteCoreAsync<object, TResponse>(method, route, body: null, bodyTypeInfo: null, adapter, options,
            declaredHeaders: null, cancellationToken, content);
    }

    /// <summary>
    /// Opens a streaming operation: the status and content-type walls answer before the
    /// body is read, then each event frame's payload is yielded as it arrives. The
    /// one-shot buffer is never involved, and a stream always throws on an error status —
    /// it has no envelope for one to ride.
    /// </summary>
    public IAsyncEnumerable<TPayload> ExecuteStreamAsync<TPayload, TCause>(HttpMethod method, string route,
        IStreamAdapter<TPayload, TCause> adapter, CancellationToken cancellationToken)
        where TCause : IReadOnlyList<Models.IOpenCodeStreamFailureCause>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(adapter);

        if (route[0] is not '/')
        {
            throw new ArgumentException("Routes must start with '/'.", nameof(route));
        }

        return ExecuteStreamCoreAsync(method, route, adapter, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.Dispose();
    }

    private async Task<TResponse> ExecuteCoreAsync<TBody, TResponse>(HttpMethod method, string route, TBody? body,
        JsonTypeInfo<TBody>? bodyTypeInfo, ResponseAdapter<TResponse> adapter, OpenCodeRequestOptions? options,
        IReadOnlyList<DeclaredHeader>? declaredHeaders, CancellationToken cancellationToken, Stream? content = null)
        where TBody : class
        where TResponse : OpenCodeResponse
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(adapter);

        if (route[0] is not '/')
        {
            throw new ArgumentException("Routes must start with '/'.", nameof(route));
        }

        // Refused before sending: an undefined value must never silently pick an error channel.
        var errorBehavior = options?.ErrorBehavior ?? ErrorBehavior.Default;
        if (errorBehavior is not (ErrorBehavior.Default or ErrorBehavior.NoThrow))
        {
            throw new ArgumentOutOfRangeException(nameof(options), errorBehavior, "Unknown ErrorBehavior value.");
        }

        TResponse adapted;
        using (var message = CreateMessage(method, route, body, bodyTypeInfo, options?.Location, declaredHeaders, cancellationToken))
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

    private async IAsyncEnumerable<TPayload> ExecuteStreamCoreAsync<TPayload, TCause>(HttpMethod method, string route,
        IStreamAdapter<TPayload, TCause> adapter, [EnumeratorCancellation] CancellationToken cancellationToken)
        where TCause : IReadOnlyList<Models.IOpenCodeStreamFailureCause>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var message = CreateStreamMessage(method, route, cancellationToken);
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

#if !NET
        using var cancellationRegistration = RegisterStreamCancellation(response, cancellationToken);
#endif

        // The response owns the body stream, so disposing it here would only duplicate the
        // disposal the message performs when enumeration ends.
        var body = await ReadBodyStreamAsync(response, cancellationToken).ConfigureAwait(false);
        var frames = _framer.ReadAsync(body, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var enumeration = frames.ConfigureAwait(false);
        while (true)
        {
            bool moved;

            // A yield cannot sit inside a try with a catch, so the read is guarded on its
            // own: a connection dying mid-stream is a transport failure here exactly as it
            // is on the one-shot path.
            try
            {
                moved = await frames.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (FailureClassification.Handles(exception, FailurePhase.EventStreamRead))
            {
                throw FailureClassification.Map(exception, FailurePhase.EventStreamRead, cancellationToken);
            }

            if (!moved)
            {
                break;
            }

            yield return FrameDispatch.ReadPayload(frames.Current, adapter);
        }
    }

    private ValueTask SendThroughPoliciesAsync(PipelineMessage message) =>
        _policies[0].ProcessAsync(message, _policies.AsMemory(1));

    private PipelineMessage CreateMessage<TBody>(HttpMethod method, string route, TBody? body,
        JsonTypeInfo<TBody>? bodyTypeInfo, LocationSelector? perCallLocation,
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
                Request = request,
                CancellationToken = cancellationToken,
                NetworkToken = cancellationToken,
                NetworkTimeout = _networkTimeout,
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

    private PipelineMessage CreateStreamMessage(HttpMethod method, string route, CancellationToken cancellationToken) =>
        new()
        {
            Request = new HttpRequestMessage(method, new Uri(_endpointBase + route, UriKind.Absolute)),
            CancellationToken = cancellationToken,
            NetworkToken = cancellationToken,
            NetworkTimeout = _networkTimeout,
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

    private static async Task<Stream> ReadBodyStreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FailureClassification.Handles(exception, FailurePhase.EventStreamRead))
        {
            throw FailureClassification.Map(exception, FailurePhase.EventStreamRead, cancellationToken);
        }
    }

#if !NET
    private static CancellationTokenRegistration RegisterStreamCancellation(
        HttpResponseMessage response, CancellationToken cancellationToken) =>
        cancellationToken.Register(
            static state => ((HttpResponseMessage)state).Dispose(),
            response,
            useSynchronizationContext: false);
#endif
}
