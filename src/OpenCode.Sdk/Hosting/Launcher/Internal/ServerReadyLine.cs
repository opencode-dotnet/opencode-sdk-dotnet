using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Parses the stdio readiness contract: the server's first stdout line is one JSON object whose
/// <c>url</c> member carries the bound endpoint, printed only after full boot (upstream
/// server-process.ts; the reference decode requires only the string url member, the Ready
/// schema in standalone.ts). Reflection-free on purpose — the SDK's serializer context stays
/// wire-model-only.
/// </summary>
internal static class ServerReadyLine
{
    public static bool TryParse(string line, out Uri endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind is not JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("url", out var url) ||
                url.ValueKind is not JsonValueKind.String)
            {
                return false;
            }

            return Uri.TryCreate(url.GetString(), UriKind.Absolute, out endpoint!) &&
                   (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
                    string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
