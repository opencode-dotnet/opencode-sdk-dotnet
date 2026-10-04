using System.Text.Json;
using OpenCode.Sdk.Internal.BackgroundService.Registration;

namespace OpenCode.Sdk.Internal.BackgroundService.Handoff;

/// <summary>
/// The persistent-terminal handoff sidecar the pinned client keeps at
/// <c>&lt;registration&gt;.pty-handoff</c> (<c>Sidecar</c> in <c>pty-handoff.ts</c>): the source identity
/// (<c>id</c>, <c>pid</c>, <c>url</c>), the handoff ticket exactly as the route answered it (or a
/// JSON null), and an expiry in epoch milliseconds. The reader follows upstream's strict shape guard
/// (<c>read</c> in <c>pty-handoff.ts</c>): any document that is not this shape reads as absent, never as a
/// failure. The ticket stays a <see cref="JsonElement"/>, so members this pin does not model survive
/// and the value rides <c>OPENCODE_PTY_HANDOFF</c> verbatim. A class rather than a record: the ticket
/// is secret-bearing, and a record would print it.
/// </summary>
internal sealed class PtyHandoffSidecar
{
    /// <summary>Gets the daemon's instance id, when the source published one.</summary>
    public required string? SourceId { get; init; }

    /// <summary>
    /// Gets the source pid, or <see langword="null"/> for a JSON number no process can have. The pinned
    /// client's guard admits any number here (<c>typeof pid === "number"</c>), so such a sidecar is
    /// still a sidecar; its source simply never matches a registration, whose pid is an integer.
    /// </summary>
    public required int? SourcePid { get; init; }

    /// <summary>Gets the source url exactly as a JSON string.</summary>
    public required string SourceUrl { get; init; }

    /// <summary>Gets the handoff ticket, or <see langword="null"/> when the sidecar carries a JSON null.</summary>
    public required JsonElement? Handoff { get; init; }

    /// <summary>Gets the sidecar expiry in epoch milliseconds.</summary>
    public required double ExpiresAt { get; init; }

    /// <summary>Decodes a sidecar document.</summary>
    /// <param name="utf8">The file's bytes.</param>
    /// <returns>The sidecar, or null for any document that is not this shape.</returns>
    public static PtyHandoffSidecar? TryRead(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8);
            using var document = JsonDocument.ParseValue(ref reader);
            return Read(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Not JSON, or a string that is not valid UTF-8: not this shape either way.
            return null;
        }
    }

    /// <summary>The pinned client's <c>isHandoff</c> shape guard (<c>pty-handoff.ts</c>), unknown members ignored.</summary>
    /// <param name="element">The candidate ticket.</param>
    /// <returns>True when the four members the pin knows are present and typed.</returns>
    public static bool IsHandoff(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object &&
        HasString(element, "directory") &&
        HasString(element, "instanceID") &&
        HasString(element, "ticket") &&
        TryGetFinite(element, "expiresAt", out _);

    /// <summary><c>Number.isFinite</c>: finite on every target framework, unlike <c>double.IsFinite</c>.</summary>
    /// <param name="value">The number.</param>
    /// <returns>True when the number is neither NaN nor infinite.</returns>
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>Reads a finite JSON number member.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The member name.</param>
    /// <param name="value">The number, when the member is one and finite.</param>
    /// <returns>Whether the member is a finite number.</returns>
    public static bool TryGetFinite(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var member) &&
            member.ValueKind == JsonValueKind.Number &&
            member.TryGetDouble(out value) &&
            IsFinite(value);
    }

    /// <summary>
    /// Encodes the sidecar the way the pinned client's <c>publish</c> writes it: the source object
    /// (the id member omitted when absent, as <c>JSON.stringify</c> omits <c>undefined</c>), the
    /// handoff value, and the expiry.
    /// </summary>
    /// <returns>The UTF-8 document.</returns>
    public byte[] ToUtf8Json()
    {
        // Utf8JsonWriter implements IAsyncDisposable, which is what MA0045 reacts to, but this is a
        // pure in-memory buffering composition with nothing to await (the DriveProtocol.Compose
        // precedent); the method's contract is the synchronous byte[] the publisher consumes.
#pragma warning disable MA0045
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("source");
            if (SourceId is { } id)
            {
                writer.WriteString("id", id);
            }

            writer.WriteNumber("pid", SourcePid ?? throw new InvalidOperationException("Only a sidecar built from a registration is published."));
            writer.WriteString("url", SourceUrl);
            writer.WriteEndObject();
            writer.WritePropertyName("handoff");
            if (Handoff is { } handoff)
            {
                handoff.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteNumber("expiresAt", ExpiresAt);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
#pragma warning restore MA0045
    }

    private static PtyHandoffSidecar? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number ||
            !source.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("handoff", out var handoff) ||
            !TryGetFinite(root, "expiresAt", out var expiresAt))
        {
            return null;
        }

        string? sourceId = null;
        if (source.TryGetProperty("id", out var id))
        {
            if (id.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            sourceId = id.GetString();
        }

        if (handoff.ValueKind != JsonValueKind.Null && !IsHandoff(handoff))
        {
            return null;
        }

        return new PtyHandoffSidecar
        {
            SourceId = sourceId,
            SourcePid = StrictJson.TryGetProcessId(pid, out var sourcePid) ? sourcePid : null,
            SourceUrl = url.GetString()!,
            Handoff = handoff.ValueKind == JsonValueKind.Null ? null : handoff.Clone(),
            ExpiresAt = expiresAt,
        };
    }

    private static bool HasString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;
}
