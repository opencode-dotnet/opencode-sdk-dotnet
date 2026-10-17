using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;
using static OpenCode.Sdk.Tools.Tests.Support.UnionHoistPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class HoistedMemberEmissionTests
{
    [Test]
    [Arguments(true, "Owner remarks. Every declared variant carries it; an unrecognized variant preserved as UnknownExampleEvent returns null through this interface.")]
    [Arguments(false, "Owner remarks. Every declared variant carries it; an unrecognized variant preserved as UnknownExampleEvent returns null through this interface. An ordinary optional property may also be absent.")]
    public async Task Emit_Should_Describe_A_Curated_Hoisted_Members_Requiredness(bool isRequired, string expected)
    {
        var plan = HoistedEmitPlan(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [Variant("CreatedEvent", "created", Property("id", Named("string", isNullable: !isRequired), isRequired)),
                Variant("DeletedEvent", "deleted", Property("id", Named("string", isNullable: !isRequired), isRequired))],
            Curation(Groups()));
        var union = plan.Unions.Single() with
        {
            Documentation = new()
            {
                Members = new Dictionary<string, DocumentationPlan>(StringComparer.Ordinal)
                {
                    ["Id"] = new("The shared identifier.", "Owner remarks."),
                },
            },
        };
        var source = EmitterSnapshot.Content(UnionEmitter.Emit([union], plan.Placement), "Example/Models/IExampleEvent.cs");
        var root = await CSharpSyntaxTree.ParseText(source).GetRootAsync();
        var property = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Single(static property => property.Identifier.ValueText == "Id");

        await Assert.That(RemarksOf(property)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Emit_Should_Inherit_Only_Curated_Public_Members_Unless_Directly_Documented(bool direct)
    {
        var plan = EmitterPlanFixture.CreateHoistPlan();
        var text = new DocumentationPlan("A <curated> & value.", "Owner remarks.");
        plan = plan with
        {
            HoistedInterfaces = [.. plan.HoistedInterfaces.Select(carrier => carrier with
            {
                Documentation = new() { Members = new Dictionary<string, DocumentationPlan>(StringComparer.Ordinal) { ["Seq"] = text } },
            })],
            Unions = [.. plan.Unions.Select(union => union with
            {
                Documentation = new() { Members = new Dictionary<string, DocumentationPlan>(StringComparer.Ordinal)
                {
                    [union.Name == "IExampleEvent" ? "Created" : "Phase"] = text,
                } },
            })],
            Models = [.. plan.Models.Select(model => model is ObjectModelPlan { Name: "CreatedEvent" } concrete ? concrete with
            {
                Properties = [.. concrete.Properties.Select(property => property.Name == "Created" ? property with { Name = "Born" } : property)],
                ExplicitHoistedImplementations = [.. concrete.ExplicitHoistedImplementations.Select(bridge =>
                    bridge.MemberName == "Created" ? bridge with { PropertyName = "Born" } : bridge)],
            } : model).Select(model => model.Name == "CreatedEventDurable" && direct ? model with
            {
                Documentation = new() { Members = new Dictionary<string, DocumentationPlan>(StringComparer.Ordinal) { ["Seq"] = new("Direct summary.", null) } },
            } : model)],
        };
        var sources = SourceEmitter.Emit(plan);
        var roots = await Task.WhenAll(sources.Select(source => CSharpSyntaxTree.ParseText(System.Text.Encoding.UTF8.GetString(source.Utf8Source.Span)).GetRootAsync()));
        var inherited = roots.SelectMany(root => root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(static property => property.GetLeadingTrivia().ToFullString().Contains("<inheritdoc", StringComparison.Ordinal))
            .Select(property => ((TypeDeclarationSyntax)property.Parent!).Identifier.ValueText + "." + property.Identifier.ValueText
                + ": " + property.GetLeadingTrivia().ToFullString().Trim()))
            .Order(StringComparer.Ordinal).ToArray();
        string[] expected =
        [
            "CreatedEvent.Born: /// <inheritdoc cref=\"IExampleEvent.Created\" />",
            "DeletedEvent.Created: /// <inheritdoc cref=\"IExampleEvent.Created\" />",
            "DeletedEventDurable.Seq: /// <inheritdoc cref=\"IDurableEnvelope.Seq\" />",
            "PhaseEndedEvent.Created: /// <inheritdoc cref=\"IExampleEvent.Created\" />",
            "PhaseEndedEvent.Phase: /// <inheritdoc cref=\"IExamplePhase.Phase\" />",
            "PhaseEndedEventDurable.Seq: /// <inheritdoc cref=\"IDurableEnvelope.Seq\" />",
            "PhaseStartedEvent.Created: /// <inheritdoc cref=\"IExampleEvent.Created\" />",
            "PhaseStartedEvent.Phase: /// <inheritdoc cref=\"IExamplePhase.Phase\" />",
            "PhaseStartedEventDurable.Seq: /// <inheritdoc cref=\"IDurableEnvelope.Seq\" />",
        ];
        if (!direct)
        {
            expected = [expected[0], "CreatedEventDurable.Seq: /// <inheritdoc cref=\"IDurableEnvelope.Seq\" />", .. expected[1..]];
        }

        await Assert.That(inherited).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(EmitterSnapshot.Content(sources, "Example/Models/IExampleEvent.cs")).Contains("A &lt;curated&gt; &amp; value.");
        var member = roots.SelectMany(static root => root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            .Single(static property => property.Identifier.ValueText == "Created"
                && property.Parent is InterfaceDeclarationSyntax { Identifier.ValueText: "IExampleEvent" });
        await Assert.That(RemarksOf(member)).IsEqualTo("Owner remarks. Every declared variant carries it; an unrecognized variant preserved as UnknownExampleEvent returns null through this interface.");
        await Assert.That(EmitterSnapshot.Content(sources, "Example/Models/CreatedEventDurable.cs"))
            .Contains(direct ? "/// Direct summary." : "/// <inheritdoc cref=\"IDurableEnvelope.Seq\" />");
    }

    [Test]
    public async Task Emit_Should_Render_The_Carrier_Fallback_And_Value_Grammatically()
    {
        var plan = EmitterPlanFixture.CreateHoistPlan();
        var carrier = plan.HoistedInterfaces[0] with
        {
            Description = null,
            Members = [new HoistedMemberPlan { Name = "Value", WireName = "value", IsRequired = false, Type = Named("string") }],
        };
        var source = EmitterSnapshot.Create(HoistedInterfaceEmitter.Emit([carrier], plan.Placement));
        await Assert.That(source).Contains("Represents an i durable envelope value.");
        await Assert.That(source).Contains(EmitterPlanFixture.ValueSummary);
    }

    [Test]
    public async Task Emit_Should_Render_The_Hoisted_Carrier_And_Its_Implementations()
    {
        var sources = EmitterSnapshot.Create(SourceEmitter.Emit(EmitterPlanFixture.CreateHoistPlan()));

        await Verify(sources);
    }

    [Test]
    public async Task Emit_Should_Declare_The_Hoisted_Carrier_As_A_Plain_Interface()
    {
        var carrier = HoistedSource("Example/Models/IDurableEnvelope.cs");

        await Assert.That(carrier).Contains("public interface IDurableEnvelope");
        await Assert.That(carrier).Contains("public long Seq { get; }");
        await Assert.That(carrier).Contains("public double Version { get; }");
        // The carrier has no converter and no unknown arm: nothing deserializes into it.
        await Assert.That(carrier).DoesNotContain("JsonConverter");
    }

    [Test]
    public async Task Emit_Should_Declare_Every_Hoisted_Union_Member_Nullable_And_Say_Why()
    {
        var union = HoistedSource("Example/Models/IExampleEvent.cs");

        await Assert.That(union).Contains("public IDurableEnvelope? Durable { get; }");
        await Assert.That(union).Contains("public double? Created { get; }");
        await Assert.That(union).Contains("public string? Id { get; }");
        await Assert.That(union).Contains("null when the payload is an unrecognized variant preserved as");
        await Assert.That(union).Contains("UnknownExampleEvent");
    }

    [Test]
    public async Task Emit_Should_Make_The_Unknown_Carrier_Answer_Null_For_Every_Hoisted_Member_In_Its_Chain()
    {
        var carrier = HoistedSource("Example/Models/UnknownExamplePhase.cs");

        await Assert.That(carrier).Contains("IDurableEnvelope? IExampleEvent.Durable => null;");
        await Assert.That(carrier).Contains("double? IExampleEvent.Created => null;");
        await Assert.That(carrier).Contains("string? IExampleEvent.Id => null;");
        await Assert.That(carrier).Contains("string? IExamplePhase.Phase => null;");
    }

    [Test]
    public async Task Emit_Should_Keep_A_Variant_Property_Concrete_And_Answer_The_Interface_Explicitly()
    {
        var variant = HoistedSource("Example/Models/CreatedEvent.cs");

        await Assert.That(variant).Contains("public required CreatedEventDurable Durable { get; init; }");
        await Assert.That(variant).Contains("IDurableEnvelope? IExampleEvent.Durable => Durable;");
        await Assert.That(variant).Contains("double? IExampleEvent.Created => Created;");
        // A reference type already answers the nullable annotation, so no explicit member appears.
        await Assert.That(variant).DoesNotContain("string? IExampleEvent.Id");
    }

    [Test]
    public async Task Emit_Should_Let_A_Promoted_Record_Implement_The_Carrier_Beside_Its_Own_Identity()
    {
        var promoted = HoistedSource("Example/Models/CreatedEventDurable.cs");

        await Assert.That(promoted).Contains("public sealed record CreatedEventDurable : IDurableEnvelope");
        await Assert.That(promoted).Contains("public required long Seq { get; init; }");
    }

    [Test]
    public async Task Emit_Should_Keep_The_Hoisted_Carrier_Out_Of_The_Serializer_Registry()
    {
        var registry = HoistedSource("Internal/Serialization/OpenCodeJsonContext.cs");

        await Assert.That(registry).DoesNotContain("IDurableEnvelope");
        await Assert.That(registry).Contains("CreatedEventDurable");
    }

    /// <summary>
    /// The rendered half of the tri-state carrier case: the interface declares the unwrapped
    /// member and each record answers it from the wrapper, so the tree compiles.
    /// </summary>
    [Test]
    public async Task Emit_Should_Answer_A_Tristate_Carrier_Member_From_The_Wrapper()
    {
        var sources = SourceEmitter.Emit(HoistedEmitPlan(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("durable", Named("CreatedEventDurable"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("durable", Named("DeletedEventDurable"), isRequired: true)),
                Record("CreatedEventDurable", Property("note", Named("string", isNullable: true), isRequired: false,
                    emitsOptionalWrapper: true)),
                Record("DeletedEventDurable", Property("note", Named("string", isNullable: true), isRequired: false,
                    emitsOptionalWrapper: true)),
            ],
            Curation(Groups())));

        var carrier = EmitterSnapshot.Content(sources, "Example/Models/IExampleEventDurable.cs");
        var record = EmitterSnapshot.Content(sources, "Example/Models/CreatedEventDurable.cs");

        await Assert.That(carrier).Contains("public string? Note { get; }");
        await Assert.That(record).Contains("public Optional<string?> Note { get; init; }");
        await Assert.That(record).Contains("string? IExampleEventDurable.Note => Note.Value;");
    }

    /// <summary>The member's <c>&lt;remarks&gt;</c> text, read from its emitted documentation comment.</summary>
    private static string RemarksOf(PropertyDeclarationSyntax property) =>
        XElement.Parse("<doc>" + property.GetLeadingTrivia().ToFullString().Replace("///", "", StringComparison.Ordinal) + "</doc>")
            .Element("remarks")!.Value.Trim();

    private static string HoistedSource(string relativePath) =>
        EmitterSnapshot.Content(SourceEmitter.Emit(EmitterPlanFixture.CreateHoistPlan()), relativePath);
}
