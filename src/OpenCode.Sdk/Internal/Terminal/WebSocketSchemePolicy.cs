using System.Diagnostics;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Turns a normalized HTTP endpoint base into the WebSocket base addressing the same server. Both
/// terminal families upgrade against the endpoint the pipeline published, so the swap is stated
/// once here rather than copied into each family's connect-URI builder.
/// </summary>
internal static class WebSocketSchemePolicy
{
    private const string HttpScheme = "http:";

    private const string HttpsScheme = "https:";

    /// <summary>Swaps the endpoint base's scheme for the WebSocket scheme addressing the same server.</summary>
    /// <param name="endpointBase">The normalized request base the pipeline published.</param>
    /// <returns>The same base under <c>ws</c> or <c>wss</c>.</returns>
    public static string ToWebSocketScheme(string endpointBase)
    {
        ArgumentNullException.ThrowIfNull(endpointBase);

        // EndpointPolicy already refused every scheme but these two, so the swap is total.
        if (endpointBase.StartsWith(HttpsScheme, StringComparison.Ordinal))
        {
            return "wss:" + endpointBase[HttpsScheme.Length..];
        }

        Debug.Assert(endpointBase.StartsWith(HttpScheme, StringComparison.Ordinal), "The endpoint base is HTTP or HTTPS.");
        return "ws:" + endpointBase[HttpScheme.Length..];
    }
}
