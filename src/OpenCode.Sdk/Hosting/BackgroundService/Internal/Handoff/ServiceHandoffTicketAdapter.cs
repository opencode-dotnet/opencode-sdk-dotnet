using System.Text;
using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Adapts <c>POST /api/experimental/persistent-pty/handoff</c> onto
/// <see cref="ServiceHandoffTicketResponse"/>, keeping the ticket raw. The status map is the
/// generated door's (<c>PersistentPtyHandoffResponseAdapter</c>): 200 succeeds, 400/401/503 are the
/// declared errors. A body that is not JSON is the one success-path failure, the pinned client's
/// <c>response.json()</c> rejecting; a body without a <c>handoff</c> member is reported, not thrown,
/// because upstream refuses it with its own message and no concurrent re-check.
/// </summary>
internal sealed class ServiceHandoffTicketAdapter : ResponseAdapter<ServiceHandoffTicketResponse>
{
    private static readonly string[] Status400Tags = ["InvalidRequestError"];
    private static readonly string[] Status401Tags = ["UnauthorizedError"];
    private static readonly string[] Status503Tags = ["ServiceUnavailableError"];

    private ServiceHandoffTicketAdapter()
    {
    }

    /// <summary>Gets the shared adapter instance.</summary>
    public static ServiceHandoffTicketAdapter Instance { get; } = new();

    /// <inheritdoc />
    public override StatusVerdict Classify(int status) => status switch
    {
        200 => StatusVerdict.Success,
        >= 200 and < 300 => StatusVerdict.UndeclaredSuccess,
        400 or 401 or 503 => StatusVerdict.DeclaredError,
        _ => StatusVerdict.UndeclaredError,
    };

    /// <inheritdoc />
    public override ServiceHandoffTicketResponse AdaptSuccess(int status, ReadOnlySpan<byte> utf8Body) =>
        ReadTicket(status, utf8Body);

    /// <inheritdoc />
    public override ServiceHandoffTicketResponse Adapt(int status, string rawBody)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        return status switch
        {
            200 => ReadTicket(status, Encoding.UTF8.GetBytes(rawBody)),
            >= 200 and < 300 => throw StatusVerdictFailures.UndeclaredSuccess(status),
            400 => Refused(status, rawBody, Status400Tags),
            401 => Refused(status, rawBody, Status401Tags),
            503 => Refused(status, rawBody, Status503Tags),
            _ => Refused(status, rawBody, allowedTags: null),
        };
    }

    private static ServiceHandoffTicketResponse ReadTicket(int status, ReadOnlySpan<byte> utf8Body)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Body);
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.Read())
            {
                throw new JsonException("The handoff response must contain only one JSON value.");
            }

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("handoff", out var handoff))
            {
                return new ServiceHandoffTicketResponse { Status = status, HasHandoffMember = false };
            }

            return new ServiceHandoffTicketResponse
            {
                Status = status,
                HasHandoffMember = true,
                Handoff = handoff.ValueKind == JsonValueKind.Null ? null : handoff.Clone(),
            };
        }
        catch (JsonException exception)
        {
            throw new OpenCodeTransportException("The opencode API returned a malformed success body.", exception);
        }
    }

    private static ServiceHandoffTicketResponse Refused(int status, string rawBody, string[]? allowedTags) =>
        new()
        {
            Status = status,
            IsError = true,
            Error = ReadTolerantError(rawBody, allowedTags),
            RawBody = rawBody,
        };
}
