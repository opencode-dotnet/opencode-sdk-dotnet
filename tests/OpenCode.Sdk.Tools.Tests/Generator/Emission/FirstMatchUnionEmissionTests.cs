using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

/// <summary>
/// One compilation carries all three first-match shapes; the cases file is the same corpus shape
/// the SDK's upstream-oracle test reads, so a disagreement names the union, the value and both
/// kinds.
/// </summary>
public sealed class FirstMatchUnionEmissionTests
{
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Select_The_First_Object_Arm_Whose_Claim_Holds()
    {
        var assembly = await CompileAsync();
        using var cases = JsonDocument.Parse(new FixtureLoader().Load("Serialization.first-match-cases.json"));
        var failures = new List<string>();

        foreach (var entry in cases.RootElement.EnumerateArray())
        {
            var union = entry.GetProperty("union").GetString()!;
            var payload = entry.GetProperty("value").GetRawText();
            var typeInfo = TypeInfo(assembly, union);
            var value = JsonSerializer.Deserialize(payload, typeInfo)
                        ?? throw new InvalidOperationException($"{union} {payload} materialized null.");
            var kind = typeInfo.Type.GetProperty("Kind")!.GetValue(value)!.ToString();
            if (!StringComparer.Ordinal.Equals(kind, entry.GetProperty("kind").GetString()))
            {
                failures.Add($"{union} {payload}: expected {entry.GetProperty("kind").GetString()}, read {kind}");
                continue;
            }

            if (entry.GetProperty("roundTrip").GetBoolean())
            {
                using var written = JsonDocument.Parse(Serialize(value, typeInfo));
                if (!JsonElement.DeepEquals(entry.GetProperty("value"), written.RootElement))
                {
                    failures.Add($"{union} {payload}: wrote {written.RootElement.GetRawText()}");
                }
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Refuse_A_Value_Whose_Claimed_Arm_Does_Not_Parse()
    {
        var assembly = await CompileAsync();
        var typeInfo = TypeInfo(assembly, "JobStatus");

        _ = await Assert
            .That(() => JsonSerializer.Deserialize("""{"status":"error","error":5}""", typeInfo))
            .Throws<JsonException>();
    }

    private static async Task<Assembly> CompileAsync()
    {
        var sources = SourceEmitter.Emit(await EmitterPlanFixture.CreateFirstMatchUnionPlanAsync());
        return await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(sources);
    }

    private static string Serialize(object value, JsonTypeInfo typeInfo) => JsonSerializer.Serialize(value, typeInfo);

    private static JsonTypeInfo TypeInfo(Assembly assembly, string union)
    {
        var contextType = assembly.GetType("OpenCode.Sdk.Internal.OpenCodeJsonContext", throwOnError: true)!;
        var context = (JsonSerializerContext)(contextType.GetProperty("Default")?.GetValue(null)
                                              ?? throw new InvalidOperationException("Generated JSON context has no Default instance."));
        var type = assembly.GetType($"OpenCode.Sdk.Models.{union}", throwOnError: true)!;
        return context.GetTypeInfo(type) ?? throw new InvalidOperationException($"Generated JSON context has no {union} metadata.");
    }
}
