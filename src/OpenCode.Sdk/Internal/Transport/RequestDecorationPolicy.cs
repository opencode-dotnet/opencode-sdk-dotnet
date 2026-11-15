using System.Net.Http.Headers;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Decorates every request in four steps: SDK User-Agent; <see cref="OpenCodeClientOptions.Headers"/>;
/// SDK authorization, merged location and declared headers; <see cref="OpenCodeRequestOptions.Headers"/>.
/// Later steps replace earlier names. Client-wide values are snapshotted at construction.
/// Knowledge source: upstream-observed — the location
/// header mirrors the middleware's percent decoding, re-verified at every spec refresh. A
/// per-call location in <see cref="PipelineMessage.Options"/> merges over that snapshot: a set
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
    private readonly string _userAgent;
    private readonly IReadOnlyDictionary<string, string>? _headers;

    public RequestDecorationPolicy(AuthenticationHeaderValue? authorization, LocationSelector? location, ProductInfoHeaderValue userAgent,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(userAgent);

        _authorization = authorization;
        _userAgent = userAgent.ToString();
        _headers = headers is { Count: > 0 } ? headers : null;

        // The server percent-decodes this header. Compute the ambient escape once; per-call
        // overrides are escaped when decorating. This also keeps Unicode paths sendable.
        _escapedDirectory = location?.Directory is { } directory ? Uri.EscapeDataString(directory) : null;
    }

    public override ValueTask ProcessAsync(PipelineMessage message, ReadOnlyMemory<PipelinePolicy> remaining)
    {
        Decorate(message.Request, message.Options, message.DeclaredHeaders);
        return ProcessNextAsync(message, remaining);
    }

    /// <summary>Validates and copies one caller set, joining case-equivalent names in enumeration order.</summary>
    public static IReadOnlyDictionary<string, string> SnapshotHeaders(IEnumerable<KeyValuePair<string, string>> headers,
        string member, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (string.IsNullOrEmpty(header.Key) || !header.Key.All(static character => character is
                (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or
                '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
            {
                throw new ArgumentException(member + " contains an invalid HTTP token name.", parameterName);
            }

            if (header.Value is null)
            {
                throw new ArgumentException(member + " contains a null value.", parameterName);
            }

            if (header.Value.Contains('\r', StringComparison.Ordinal) || header.Value.Contains('\n', StringComparison.Ordinal))
            {
                throw new ArgumentException(member + " contains a value with CR or LF.", parameterName);
            }

            snapshot[header.Key] = snapshot.TryGetValue(header.Key, out var preceding) ? preceding + ", " + header.Value : header.Value;
        }

        return snapshot;
    }

    private void Decorate(HttpRequestMessage request, OpenCodeRequestOptions? perCall, IReadOnlyList<DeclaredHeader>? declaredHeaders)
    {
        _ = request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        ApplyHeaders(request, _headers);
        if (_authorization is not null)
        {
            request.Headers.Authorization = _authorization;
        }

        var escapedDirectory = perCall?.Location?.Directory is { } directory
            ? Uri.EscapeDataString(directory)
            : _escapedDirectory;

        if (escapedDirectory is not null)
        {
            AddOrReplaceRequestHeader(request, "x-opencode-directory", escapedDirectory);
        }

        // The document already fixed each name and the raw method already dropped the omitted
        // ones, so the values ride unvalidated exactly as the location header does.
        if (declaredHeaders is not null)
        {
            for (var index = 0; index < declaredHeaders.Count; index++)
            {
                var header = declaredHeaders[index];
                AddOrReplaceRequestHeader(request, header.Name, header.Value);
            }
        }

        ApplyHeaders(request, perCall?.Headers);
    }

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var header in headers)
        {
            ReplaceHeader(request, header.Key, header.Value);
        }
    }

    private static void ReplaceHeader(HttpRequestMessage request, string name, string value)
    {
        // Let the BCL classify ownership, including content headers added by future runtimes.
        HttpHeaders target;
        if (request.Headers.TryAddWithoutValidation(name, value))
        {
            target = request.Headers;
        }
        else if (request.Content is { } content)
        {
            target = content.Headers;
        }
        else
        {
            return;
        }

        _ = target.Remove(name);
        _ = target.TryAddWithoutValidation(name, value);
    }

    private static void AddOrReplaceRequestHeader(HttpRequestMessage request, string name, string value)
    {
        if (request.Headers.Contains(name))
        {
            _ = request.Headers.Remove(name);
        }

        _ = request.Headers.TryAddWithoutValidation(name, value);
    }
}
