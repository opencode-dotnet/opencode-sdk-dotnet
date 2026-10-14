namespace OpenCode.Sdk.Internal;

/// <summary>
/// One operation's trip through the policy pipeline. The planes construct it, the policies
/// write their results onto it, and disposing it releases the request and any response —
/// the message owns both lifetimes so no policy or plane carries a dispose obligation.
/// </summary>
internal sealed class PipelineMessage : IDisposable
{
    /// <summary>Gets the decorated request; built by the plane, sent by <see cref="TransportPolicy"/>.</summary>
    public required HttpRequestMessage Request { get; init; }

    /// <summary>The default per-read progress window; internal until the public network-timeout option lands.</summary>
    internal static readonly TimeSpan DefaultNetworkTimeout = TimeSpan.FromSeconds(100);

    /// <summary>Gets the caller's token, inspected first by every failure classification.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Gets the progress window the send and each buffered read must fit inside; read by
    /// <see cref="ResponseBufferingPolicy"/>, which re-arms it on every read that progresses.
    /// </summary>
    public TimeSpan NetworkTimeout { get; init; } = DefaultNetworkTimeout;

    /// <summary>
    /// Gets the token network I/O runs under: the caller's token until
    /// <see cref="ResponseBufferingPolicy"/> links the progress window over it; read by
    /// <see cref="TransportPolicy"/>. Failure classification always inspects
    /// <see cref="CancellationToken"/>, never this.
    /// </summary>
    public CancellationToken NetworkToken { get; internal set; }

    /// <summary>
    /// Gets whether a success body buffers. The stream plane sets <see langword="false"/> so a
    /// live event stream stays open; error statuses buffer regardless.
    /// </summary>
    public bool BufferBody { get; init; } = true;

    /// <summary>
    /// Gets the caller's per-call location override, or <see langword="null"/> for none;
    /// written by <see cref="Pipeline"/> from <see cref="OpenCodeRequestOptions.Location"/>,
    /// read by <see cref="RequestDecorationPolicy"/> to merge over the ambient snapshot member
    /// by member.
    /// </summary>
    public LocationSelector? PerCallLocation { get; init; }

    /// <summary>
    /// Gets the headers this operation declares in the pinned document, or <see langword="null"/>
    /// when it declares none; written by <see cref="Pipeline"/> from the value a generated
    /// internal-raw method collected, read by <see cref="RequestDecorationPolicy"/>, which adds
    /// each entry without knowing which family or header name it carries.
    /// </summary>
    public IReadOnlyList<DeclaredHeader>? DeclaredHeaders { get; init; }

    /// <summary>Gets the response; written by <see cref="TransportPolicy"/> after a classified send.</summary>
    public HttpResponseMessage? Response { get; internal set; }

    /// <summary>
    /// Gets the buffered body; written by <see cref="ResponseBufferingPolicy"/>, consumed by
    /// <see cref="ResponseMaterializer"/>.
    /// </summary>
    public ResponseBody? Body { get; internal set; }

    /// <summary>
    /// Gets or sets the progress window the buffering policy arms for the exchange; null outside
    /// it. A request body that streams writes through <see cref="RestartProgressWindow"/>.
    /// </summary>
    internal ProgressWindow? ProgressWindow { get; set; }

    /// <summary>Restarts the progress window after the exchange progressed; a no-op outside one.</summary>
    public void RestartProgressWindow() => ProgressWindow?.Restart();

    public void Dispose()
    {
        Body?.Dispose();
        Response?.Dispose();
        Request.Dispose();
    }
}
