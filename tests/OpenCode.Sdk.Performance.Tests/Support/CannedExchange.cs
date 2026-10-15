using System.Globalization;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// One canned exchange with no SDK at all: a bare <see cref="HttpClient"/> send through a family's
/// canned handler, with the body drained into one reused buffer. A family's raw rung runs it over
/// the same handler as its pipeline rungs, so the response and content objects, the send's task and
/// the body read it costs are visible beside them.
/// </summary>
/// <remarks>
/// It is a floor for subtraction only against rungs over the same handler and body kind: the drain
/// loop is its own, and a suspending body's reads cost what they cost here too.
/// </remarks>
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

    /// <summary>Refuses an exchange that does not drain the whole body, so a raw rung never measures less than it claims.</summary>
    /// <param name="bodyBytes">The canned body's length.</param>
    /// <returns>A task that completes once the exchange drained the body.</returns>
    public async Task RequireDrainAsync(int bodyBytes)
    {
        var drained = await SendAndDrainAsync().ConfigureAwait(false);
        if (drained != bodyBytes)
        {
            throw new InvalidOperationException($"The raw exchange drained {drained.ToString(CultureInfo.InvariantCulture)} of the body's {bodyBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
        }
    }
}
