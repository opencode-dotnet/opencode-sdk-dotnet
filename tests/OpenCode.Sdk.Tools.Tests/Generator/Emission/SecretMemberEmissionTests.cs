using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

/// <summary>
/// A model with a secret member prints itself (ADR-0028). The override must keep the compiler's
/// own shape exactly — the same members in the same order, the same spacing, an absent value
/// empty — so the only difference from the synthesized ToString is the masked value; a masked
/// open bag prints the marker only while it holds an open member. A union
/// flagged to mask its unknown payload prints that payload, and only that, as the marker.
/// </summary>
public sealed class SecretMemberEmissionTests
{
    [Test]
    public async Task Emit_Should_Override_ToString_Only_On_A_Model_With_A_Secret_Member()
    {
        var sources = ModelEmitter.Emit(Redact(EmitterPlanFixture.CreateModelSnapshot(), "ExampleItem", "note"));

        await Assert.That(EmitterSnapshot.Content(sources, "Models/ExampleItem.cs"))
            .Contains("public override string ToString() => RecordPrinter.Format(nameof(ExampleItem), (\"ID\", ID), (\"Note\", RecordPrinter.Redact(Note))");
        await Assert.That(EmitterSnapshot.Content(sources, "Models/OpenSettings.cs")).DoesNotContain("ToString");
    }

    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    [Arguments("ExampleItem", "note", """{"id":"i","note":"hunter2","peak":1,"requiredNullable":null,"requiredTags":["a"]}""", "Note = hunter2")]
    [Arguments("ExampleItem", "note", """{"id":"i","peak":1,"requiredNullable":null,"requiredTags":[]}""", null)]
    [Arguments("OpenSettings", "timeout", """{"timeout":5,"extra":1}""", "Timeout = 5")]
    public async Task Emit_Should_Print_The_Compilers_Shape_With_Only_The_Secret_Masked(string model, string member, string payload, string? printedSecret)
    {
        var plain = await PrintAsync(EmitterPlanFixture.Create(), model, payload);
        var masked = await PrintAsync(Redact(EmitterPlanFixture.Create(), model, member), model, payload);

        var expected = printedSecret is null
            ? plain
            : plain.Replace(printedSecret, printedSecret[..printedSecret.IndexOf('=', StringComparison.Ordinal)] + "= [REDACTED]", StringComparison.Ordinal);
        await Assert.That(masked).IsEqualTo(expected);
        if (printedSecret is not null)
        {
            await Assert.That(plain).Contains(printedSecret);
        }
    }

    [Test]
    public async Task Emit_Should_Print_The_Open_Members_Through_RedactEntries_On_A_Flagged_Open_Model()
    {
        var sources = ModelEmitter.Emit(RedactOpen(EmitterPlanFixture.CreateModelSnapshot(), "OpenSettings"));

        await Assert.That(EmitterSnapshot.Content(sources, "Models/OpenSettings.cs")).Contains(
            "public override string ToString() => RecordPrinter.Format(nameof(OpenSettings), (\"Timeout\", Timeout), (\"AdditionalProperties\", RecordPrinter.RedactEntries(AdditionalProperties)));");
    }

    [Test]
    public async Task Emit_Should_Refuse_A_Model_That_Masks_Extension_Data_It_Does_Not_Carry()
    {
        var plan = RedactOpen(EmitterPlanFixture.CreateModelSnapshot(), "ExampleItem");

        var exception = Assert.Throws<InvalidOperationException>(() => _ = ModelEmitter.Emit(plan));

        await Assert.That(exception.Message).IsEqualTo("Model 'ExampleItem' masks extension data it does not carry.");
    }

    /// <summary>
    /// The compiler prints the view's type name; the override prints the marker for a bag holding
    /// an open member and nothing for an empty one, and every other member as the compiler does.
    /// </summary>
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    [Arguments("""{"timeout":5,"apiKey":"hunter2"}""", "[REDACTED]")]
    [Arguments("""{"timeout":5}""", "")]
    public async Task Emit_Should_Print_The_Compilers_Shape_With_Only_The_Open_Members_Masked(string payload, string printedBag)
    {
        var plain = await PrintAsync(EmitterPlanFixture.Create(), "OpenSettings", payload);
        var masked = await PrintAsync(RedactOpen(EmitterPlanFixture.Create(), "OpenSettings"), "OpenSettings", payload);

        // The bag is the record's last member, so its printed value runs up to the closing brace.
        const string label = "AdditionalProperties = ";
        var start = plain.IndexOf(label, StringComparison.Ordinal) + label.Length;
        var end = plain.LastIndexOf(" }", StringComparison.Ordinal);
        await Assert.That(plain).StartsWith("OpenSettings { Timeout = 5, AdditionalProperties = ");
        await Assert.That(masked).IsEqualTo(plain[..start] + printedBag + plain[end..]);
        await Assert.That(masked).DoesNotContain("hunter2");
    }

    [Test]
    public async Task Emit_Should_Override_ToString_Only_On_A_Flagged_Unknown_Carrier()
    {
        var sources = UnionEmitter.Emit([.. EmitterPlanFixture.CreateUnionSnapshot().Select(static union => MaskUnknown(union, "IExamplePhase"))]);

        await Assert.That(EmitterSnapshot.Content(sources, "Models/UnknownExamplePhase.cs")).Contains(
            "public override string ToString() => RecordPrinter.Format(nameof(UnknownExamplePhase), (\"Status\", Status), (\"Type\", Type), (\"Payload\", RecordPrinter.Redact(Payload)));");
        await Assert.That(EmitterSnapshot.Content(sources, "Models/UnknownExampleEvent.cs")).DoesNotContain("ToString");
    }

    [Test]
    public async Task Emit_Should_Mask_Only_The_Unknown_Arm_Of_A_Flagged_Structural_Union()
    {
        var plan = await EmitterPlanFixture.CreateStructuralUnionPlanAsync();

        var masked = StructuralUnionEmitter.Emit([.. MaskStructural(plan).Models.OfType<StructuralUnionModelPlan>()]);
        var plain = StructuralUnionEmitter.Emit([.. plan.Models.OfType<StructuralUnionModelPlan>()]);

        await Assert.That(EmitterSnapshot.Content(masked, "Models/StructuralValue.cs")).Contains(
            "StructuralUnionPrinter.Format(nameof(StructuralValue), Kind.ToString(), Kind is StructuralValueKind.Unknown ? RecordPrinter.Redacted : _value);");
        await Assert.That(EmitterSnapshot.Content(plain, "Models/StructuralValue.cs")).Contains(
            "StructuralUnionPrinter.Format(nameof(StructuralValue), Kind.ToString(), _value);");
    }

    /// <summary>
    /// The nested carrier is the widest shape: its own marker, the fixed outer marker, the payload,
    /// and explicitly answered hoisted members the compiler's print never shows.
    /// </summary>
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Print_The_Compilers_Unknown_Carrier_Shape_With_Only_The_Payload_Masked()
    {
        using var payload = JsonDocument.Parse(new FixtureLoader().Load("Serialization.unknown-phase-with-secret.json"));
        var plan = WithErrorSpine(EmitterPlanFixture.CreateHoistPlan());

        var plain = await PrintUnknownAsync(plan, "UnknownExamplePhase", "paused", payload.RootElement);
        var masked = await PrintUnknownAsync(plan with { Unions = [.. plan.Unions.Select(static union => MaskUnknown(union, union.Name))] },
            "UnknownExamplePhase", "paused", payload.RootElement);

        await Assert.That(plain).Contains(payload.RootElement.GetRawText());
        await Assert.That(masked).IsEqualTo(plain.Replace(payload.RootElement.GetRawText(), "[REDACTED]", StringComparison.Ordinal));
    }

    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    [Arguments("Serialization.structural-unknown.json", "StructuralValue { Kind = Unknown, Unknown = [REDACTED] }")]
    [Arguments("Serialization.structural-string.json", "StructuralValue { Kind = Text, Text = hello }")]
    public async Task Emit_Should_Print_A_Flagged_Structural_Unions_Unknown_Arm_Masked(string fixture, string printed)
    {
        var plan = MaskStructural(await EmitterPlanFixture.CreateStructuralUnionPlanAsync());
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(SourceEmitter.Emit(plan));
        var valueType = assembly.GetType("OpenCode.Sdk.Models.StructuralValue", throwOnError: true)!;

        var value = JsonSerializer.Deserialize(new FixtureLoader().Load(fixture), TypeInfo(assembly, valueType))
                    ?? throw new InvalidOperationException($"Fixture '{fixture}' materialized null.");

        await Assert.That(value.ToString()).IsEqualTo(printed);
    }

    /// <summary>The SDK core the plan compiles against reads the error union, which the hoist plan alone leaves out.</summary>
    private static EmitPlan WithErrorSpine(EmitPlan plan)
    {
        var spine = EmitterPlanFixture.Create();
        return plan with
        {
            Models = [.. plan.Models, .. spine.Models.Where(static model => model.Name == "BadRequestError")],
            Unions = [.. plan.Unions, .. spine.Unions.Where(static union => union.Name == "IOpenCodeError")],
            Registry = new RegistryPlan
            {
                TypeNames =
                [
                    .. plan.Registry.TypeNames.Concat(["BadRequestError", "IOpenCodeError", "UnknownOpenCodeError"]).Order(StringComparer.Ordinal),
                ],
            },
        };
    }

    private static UnionPlan MaskUnknown(UnionPlan union, string unionName) =>
        union.Name == unionName ? union with { MasksUnknownPayload = true } : union;

    private static EmitPlan MaskStructural(EmitPlan plan) =>
        plan with
        {
            Models =
            [
                .. plan.Models.Select(static model => model is StructuralUnionModelPlan structural
                    ? structural with { MasksUnknownPayload = true }
                    : model),
            ],
        };

    private static async Task<string> PrintUnknownAsync(EmitPlan plan, string carrierName, string marker, JsonElement payload)
    {
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(SourceEmitter.Emit(plan));
        var carrierType = assembly.GetType($"OpenCode.Sdk.Models.{carrierName}", throwOnError: true)!;
        var carrier = Activator.CreateInstance(carrierType, marker, payload)
                      ?? throw new InvalidOperationException($"{carrierName} constructed null.");
        return carrier.ToString()!;
    }

    private static JsonTypeInfo TypeInfo(Assembly assembly, Type type)
    {
        var contextType = assembly.GetType("OpenCode.Sdk.Internal.Serialization.OpenCodeJsonContext", throwOnError: true)!;
        var context = (JsonSerializerContext)(contextType.GetProperty("Default")?.GetValue(null)
                                              ?? throw new InvalidOperationException("Generated JSON context has no Default instance."));
        return context.GetTypeInfo(type) ?? throw new InvalidOperationException($"Generated JSON context has no {type.Name} metadata.");
    }

    private static EmitPlan Redact(EmitPlan plan, string modelName, string wireName) =>
        plan with
        {
            Models =
            [
                .. plan.Models.Select(model => model is ObjectModelPlan objectModel && objectModel.Name == modelName
                    ? objectModel with
                    {
                        Properties =
                        [
                            .. objectModel.Properties.Select(property => property.WireName == wireName ? property with { IsRedacted = true } : property),
                        ],
                    }
                    : model),
            ],
        };

    private static EmitPlan RedactOpen(EmitPlan plan, string modelName) =>
        plan with
        {
            Models =
            [
                .. plan.Models.Select(model => model is ObjectModelPlan objectModel && objectModel.Name == modelName
                    ? objectModel with { RedactsExtensionData = true }
                    : model),
            ],
        };

    private static async Task<string> PrintAsync(EmitPlan plan, string modelName, string payload)
    {
        // The fixture registers only the models its other tests materialize; this one needs the one it prints.
        var registered = plan with
        {
            Registry = new RegistryPlan { TypeNames = [.. plan.Registry.TypeNames.Append(modelName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] },
        };
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(SourceEmitter.Emit(registered));
        var modelType = assembly.GetType($"OpenCode.Sdk.Models.{modelName}", throwOnError: true)!;
        var contextType = assembly.GetType("OpenCode.Sdk.Internal.Serialization.OpenCodeJsonContext", throwOnError: true)!;
        var context = (JsonSerializerContext)(contextType.GetProperty("Default")?.GetValue(null)
                                              ?? throw new InvalidOperationException("Generated JSON context has no Default instance."));
        var typeInfo = context.GetTypeInfo(modelType)
                       ?? throw new InvalidOperationException($"Generated JSON context has no {modelName} metadata.");
        var value = JsonSerializer.Deserialize(payload, typeInfo)
                    ?? throw new InvalidOperationException("The payload materialized null.");
        return value.ToString()!;
    }
}
