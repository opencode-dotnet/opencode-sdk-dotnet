namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// The harness floor of one canned exchange with no SDK at all: a bare <see cref="HttpClient"/>
/// send through the family's canned handler, with the body drained into one reused buffer. A
/// family's raw-send rung runs it, so the handler's own allocations (the response, its content,
/// the cloned media type, the send's task) can be subtracted from the pipeline rungs directly.
/// </summary>
internal sealed class CannedExchange
{
    private const int DrainBufferBytes = 16 * 1024;

    private readonly HttpClient _client;
    private readonly HttpMethod _method;
    private readonly Uri _uri;
    private readonly byte[] _drain = new byte[DrainBufferBytes];

    public CannedExchange(HttpClient client, HttpMethod method, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(uri);
        _client = client;
        _method = method;
        _uri = uri;
    }

    /// <summary>Sends one request and drains its body.</summary>
    /// <returns>The body bytes read.</returns>
    public async Task<int> SendAndDrainAsync()
    {
        using var request = new HttpRequestMessage(_method, _uri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var total = 0;
        int read;
        while ((read = await body.ReadAsync(_drain.AsMemory()).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return total;
    }
}
