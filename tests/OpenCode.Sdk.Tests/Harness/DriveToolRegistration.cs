using System.Text.Json;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// One direct (non-codemode, un-namespaced) simulated tool registration for <c>tool.attach</c>
/// (protocol <c>ToolRegistration</c>, simulation.ts at the pin). The schemas are cloned
/// elements owned by this record; they are written into the wire message with
/// <see cref="JsonElement.WriteTo"/>, never as encoded strings.
/// </summary>
internal sealed record DriveToolRegistration
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required JsonElement InputSchema { get; init; }

    public JsonElement? OutputSchema { get; init; }

    /// <summary>
    /// Reads a registration from its embedded JSON form: <c>name</c>, <c>description</c>,
    /// <c>inputSchema</c>, and optional <c>outputSchema</c>, cloned out of the parsed document.
    /// </summary>
    public static DriveToolRegistration Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new DriveToolRegistration
        {
            Name = root.GetProperty("name").GetString()
                ?? throw new InvalidOperationException("The tool registration has no name."),
            Description = root.GetProperty("description").GetString()
                ?? throw new InvalidOperationException("The tool registration has no description."),
            InputSchema = root.GetProperty("inputSchema").Clone(),
            OutputSchema = root.TryGetProperty("outputSchema", out var output) ? output.Clone() : null,
        };
    }
}
