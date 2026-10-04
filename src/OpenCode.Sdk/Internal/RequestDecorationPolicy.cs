using System.Net.Http.Headers;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Decorates every request with the construction-time header snapshot: authorization, the
/// ambient location, and the user agent. Knowledge source: upstream-observed — the location
/// header mirrors the middleware's percent decoding, re-verified at every spec refresh. A
/// per-call <see cref="PipelineMessage.PerCallLocation"/> merges over that snapshot: a set
/// directory wins, an unset one inherits the ambient value, and there is no way to clear the
/// ambient directory for one call. The directory rides the header channel — this is uniform
/// injection, not the query-string per-request channel some operations declare. Routes under a
/// session ID resolve location from that session and ignore the header server-side, so sending it
/// there is a harmless no-op; the one exception is the session form routes under the reserved
/// session ID <c>global</c>, which have no session to resolve and take the location from the
/// request, so there the header selects it. The message may also carry
/// <see cref="PipelineMessage.DeclaredHeaders"/> — headers the pinned document declares as
/// parameters of one operation. Those are applied uniformly, entry by entry: this policy never
/// learns which family or header name it is writing, so no operation's knowledge leaks here.
/// </summary>
internal sealed class RequestDecorationPolicy : PipelinePolicy
{
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly string? _escapedDirectory;
    private readonly ProductInfoHeaderValue _userAgent;

    public RequestDecorationPolicy(AuthenticationHeaderValue? authorization, LocationSelector? location, ProductInfoHeaderValue userAgent)
    {
        ArgumentNullException.ThrowIfNull(userAgent);

        _authorization = authorization;
        _userAgent = userAgent;

        // The server percent-decodes this header. Compute the ambient escape once; per-call
        // overrides are escaped when decorating. This also keeps Unicode paths sendable.
        _escapedDirectory = location?.Directory is { } directory ? Uri.EscapeDataString(directory) : null;
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, ReadOnlyMemory<PipelinePolicy> remaining)
    {
        Decorate(message.Request, message.PerCallLocation, message.DeclaredHeaders);
        await ProcessNextAsync(message, remaining).ConfigureAwait(false);
    }

    private void Decorate(HttpRequestMessage request, LocationSelector? perCall, IReadOnlyList<DeclaredHeader>? declaredHeaders)
    {
        if (_authorization is not null)
        {
            request.Headers.Authorization = _authorization;
        }

        var escapedDirectory = perCall?.Directory is { } directory
            ? Uri.EscapeDataString(directory)
            : _escapedDirectory;

        if (escapedDirectory is not null)
        {
            _ = request.Headers.TryAddWithoutValidation("x-opencode-directory", escapedDirectory);
        }

        // The document already fixed each name and the raw method already dropped the omitted
        // ones, so the values ride unvalidated exactly as the location header does.
        if (declaredHeaders is not null)
        {
            for (var index = 0; index < declaredHeaders.Count; index++)
            {
                var header = declaredHeaders[index];
                _ = request.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        request.Headers.UserAgent.Add(_userAgent);
    }
}
