using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class StructuralUnionEmissionTests
{
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Source_Generate_And_Round_Trip_Every_Structural_Arm()
    {
        var sources = SourceEmitter.Emit(await EmitterPlanFixture.CreateStructuralUnionPlanAsync());
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(sources);
        var valueType = assembly.GetType("OpenCode.Sdk.Models.StructuralValue", throwOnError: true)!;
        var contextType = assembly.GetType("OpenCode.Sdk.Internal.OpenCodeJsonContext", throwOnError: true)!;
        var context = (JsonSerializerContext)(contextType.GetProperty("Default")?.GetValue(null)
                                              ?? throw new InvalidOperationException("Generated JSON context has no Default instance."));
        var typeInfo = context.GetTypeInfo(valueType)
                       ?? throw new InvalidOperationException("Generated JSON context has no structural value metadata.");
        (string Fixture, string Kind)[] cases =
        [
            ("Serialization.structural-string.json", "Text"),
            ("Serialization.structural-named-number-string.json", "Text"),
            ("Serialization.structural-number.json", "Number"),
            ("Serialization.structural-boolean.json", "Boolean"),
            ("Serialization.structural-string-list.json", "TextList"),
            ("Serialization.structural-unknown.json", "Unknown"),
        ];

        foreach (var (fixture, expectedKind) in cases)
        {
            var payload = new FixtureLoader().Load(fixture);
            var value = JsonSerializer.Deserialize(payload, typeInfo)
                        ?? throw new InvalidOperationException($"Fixture '{fixture}' materialized null.");
            await Assert.That(valueType.GetProperty("Kind")!.GetValue(value)!.ToString()).IsEqualTo(expectedKind);

            var serialized = Serialize(value, typeInfo);
            using var expected = JsonDocument.Parse(payload);
            using var actual = JsonDocument.Parse(serialized);
            await Assert.That(JsonElement.DeepEquals(expected.RootElement, actual.RootElement)).IsTrue();
        }
    }

    /// <summary>
    /// A record's compiler-synthesized PrintMembers reads every public property, and an inactive
    /// arm throws by design; the emitted PrintMembers prints the kind and the active arm only.
    /// </summary>
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Print_The_Active_Arm_Only()
    {
        var sources = SourceEmitter.Emit(await EmitterPlanFixture.CreateStructuralUnionPlanAsync());
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(sources);
        var valueType = assembly.GetType("OpenCode.Sdk.Models.StructuralValue", throwOnError: true)!;
        var contextType = assembly.GetType("OpenCode.Sdk.Internal.OpenCodeJsonContext", throwOnError: true)!;
        var context = (JsonSerializerContext)(contextType.GetProperty("Default")?.GetValue(null)
                                              ?? throw new InvalidOperationException("Generated JSON context has no Default instance."));
        var typeInfo = context.GetTypeInfo(valueType)
                       ?? throw new InvalidOperationException("Generated JSON context has no structural value metadata.");
        (string Fixture, string Printed)[] cases =
        [
            ("Serialization.structural-string.json", "StructuralValue { Kind = Text, Text = hello }"),
            ("Serialization.structural-named-number-string.json", "StructuralValue { Kind = Text, Text = NaN }"),
            ("Serialization.structural-number.json", "StructuralValue { Kind = Number, Number = 42.5 }"),
            ("Serialization.structural-boolean.json", "StructuralValue { Kind = Boolean, Boolean = True }"),
            ("Serialization.structural-string-list.json", "StructuralValue { Kind = TextList, TextList = [a, b] }"),
            ("Serialization.structural-unknown.json", "StructuralValue { Kind = Unknown, Unknown = {\"future\": 1} }"),
        ];

        foreach (var (fixture, printed) in cases)
        {
            var value = JsonSerializer.Deserialize(new FixtureLoader().Load(fixture), typeInfo)
                        ?? throw new InvalidOperationException($"Fixture '{fixture}' materialized null.");

            await Assert.That(value.ToString()).IsEqualTo(printed);
        }
    }

    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Refuse_Malformed_Claimed_Arms_And_Non_Finite_Constructed_Numbers()
    {
        var sources = SourceEmitter.Emit(await EmitterPlanFixture.CreateStructuralUnionPlanAsync());
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(sources);
        var valueType = assembly.GetType("OpenCode.Sdk.Models.StructuralValue", throwOnError: true)!;
        var contextType = assembly.GetType("OpenCode.Sdk.Internal.OpenCodeJsonContext", throwOnError: true)!;
        var context = (JsonSerializerContext)(contextType.GetProperty("Default")?.GetValue(null)
                                              ?? throw new InvalidOperationException("Generated JSON context has no Default instance."));
        var typeInfo = context.GetTypeInfo(valueType)
                       ?? throw new InvalidOperationException("Generated JSON context has no structural value metadata.");
        var malformed = new FixtureLoader().Load("Serialization.structural-malformed-string-list.json");

        _ = await Assert.That(() => JsonSerializer.Deserialize(malformed, typeInfo)).Throws<JsonException>();

        var number = valueType.GetMethod("FromNumber")!.Invoke(null, [double.NaN])
                     ?? throw new InvalidOperationException("Number factory returned null.");
        _ = await Assert.That(() => Serialize(number, typeInfo)).Throws<ArgumentException>();
    }

    private static string Serialize(object value, System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);
}
