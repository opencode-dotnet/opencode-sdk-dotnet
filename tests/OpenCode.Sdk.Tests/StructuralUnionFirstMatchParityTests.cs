using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// Upstream decodes the three same-token unions with Effect's union decode: the first member in
/// declaration order that decodes takes the value. <c>tools/oracles/first-match-union.ts</c> runs
/// that decode over the corpus beside the expected file and records which member took each value,
/// or that no member did; the generated converters must agree value for value. A member index is
/// the arm at that position, because the carrier keeps the document's member order; a value no
/// member takes stays raw as <c>Unknown</c>.
/// </summary>
public sealed class StructuralUnionFirstMatchParityTests
{
    [Test]
    public async Task Converters_Should_Pick_The_Member_Upstreams_Decode_Picks()
    {
        using var verdicts = JsonDocument.Parse(new FixtureLoader().LoadJson("Unions.first-match-expected.json"));
        var disagreements = new List<string>();

        foreach (var verdict in verdicts.RootElement.EnumerateArray())
        {
            var union = verdict.GetProperty("union").GetString();
            var payload = verdict.GetProperty("value").GetRawText();
            var member = verdict.GetProperty("member");
            var expected = member.ValueKind is JsonValueKind.Null ? "Unknown" : ArmAt(union, member.GetInt32());
            var actual = union switch
            {
                "reference" => Read(payload, OpenCodeJsonContext.Default.ConfigReferenceEntry).Kind.ToString(),
                "lsp" => Read(payload, OpenCodeJsonContext.Default.ConfigLspEntry).Kind.ToString(),
                "migration" => Read(payload, OpenCodeJsonContext.Default.ExperimentalMigrationV1Status).Kind.ToString(),
                _ => throw new InvalidOperationException($"The verdict file names an unknown union '{union}'."),
            };
            if (!StringComparer.Ordinal.Equals(expected, actual))
            {
                disagreements.Add($"{union} {payload}: upstream {expected}, SDK {actual}");
            }
        }

        await Assert.That(verdicts.RootElement.GetArrayLength()).IsGreaterThan(0);
        await Assert.That(disagreements).IsEmpty();
    }

    private static string ArmAt(string? union, int member) => union switch
    {
        "reference" => ((ConfigReferenceEntryKind)member).ToString(),
        "lsp" => ((ConfigLspEntryKind)member).ToString(),
        "migration" => ((ExperimentalMigrationV1StatusKind)member).ToString(),
        _ => throw new InvalidOperationException($"The verdict file names an unknown union '{union}'."),
    };

    private static T Read<T>(string payload, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(payload, typeInfo) ?? throw new InvalidOperationException($"{payload} materialized null.");
}
