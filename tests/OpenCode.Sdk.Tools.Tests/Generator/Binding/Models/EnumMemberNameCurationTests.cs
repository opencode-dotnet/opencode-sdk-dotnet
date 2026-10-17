using System.Text;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding.Models;

/// <summary>
/// The <c>enumMemberNames</c> curation channel: a reason-bearing row names the C# member one
/// enum value becomes, where the mechanical casing would produce a digit-led <c>Value…</c> name;
/// every other value keeps its mechanical name, and a row that names nothing the document has
/// refuses like every other channel.
/// </summary>
public sealed class EnumMemberNameCurationTests
{
    [Test]
    [Arguments("Original", false)]
    [Arguments("Canonical", true)]
    [Arguments("Canonical_1", false)]
    public async Task Bind_Should_Validate_Enum_Rows_On_The_Collapsed_Graph(string key, bool survives)
    {
        var source = key == "Canonical_1" ? key : "Original";
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema(source, static schema => schema.Type("string").Enum("legacy", "auto"))
            .WithSchema("Canonical", static schema => schema.Type("string").Enum("legacy", "auto"))
            .WithOperation("widget.get", configure: operation => operation.Response(200, "application/json", schema => schema.Ref(source)))));
        var curation = Curation(Groups("widget", RootGroup()), schemaAliases: source == "Original" ? [Alias(source, "Canonical")] : [],
            enumMemberNames: [EnumMemberName(key, "legacy", "Previous")]);

        if (survives)
        {
            var plan = new BindingTestHost().Bind(document, Selection("widget.get"), curation);
            await Assert.That(plan.Models.OfType<EnumModelPlan>().Single().Values.Select(static value => value.Name)).Contains("Previous");
        }
        else
        {
            var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("widget.get"), curation));
            await Assert.That(exception.Errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation, key + "/legacy", "curated schema does not exist in the spec"));
        }
    }

    [Test]
    public async Task Bind_Should_Name_A_Digit_Led_Value_Mechanically_Without_A_Row()
    {
        var document = await BindingTestHost.IngestAsync(Scenario());

        var plan = new BindingTestHost().Bind(document, Selection("mcp.get"), Curation(Groups("mcp", RootGroup())));

        var protocol = plan.Models.OfType<EnumModelPlan>().Single(static model => model.Name == "McpProtocol");
        await Assert.That(protocol.Values.Single(static value => value.WireValue == "2026-07-28").Name).IsEqualTo("Value20260728");
        await Assert.That(protocol.Values.Single(static value => value.WireValue == "legacy").Name).IsEqualTo("Legacy");
    }

    [Test]
    public async Task Bind_Should_Apply_A_Reasoned_Member_Name_To_Its_Value_Only()
    {
        var document = await BindingTestHost.IngestAsync(Scenario());
        var curation = Curation(Groups("mcp", RootGroup()),
            enumMemberNames: [EnumMemberName("Mcp.Protocol", "2026-07-28", "Revision20260728")]);

        var plan = new BindingTestHost().Bind(document, Selection("mcp.get"), curation);

        var protocol = plan.Models.OfType<EnumModelPlan>().Single(static model => model.Name == "McpProtocol");
        await Assert.That(protocol.Values.Single(static value => value.WireValue == "2026-07-28").Name).IsEqualTo("Revision20260728");
        await Assert.That(protocol.Values.Single(static value => value.WireValue == "legacy").Name).IsEqualTo("Legacy");
        await Assert.That(protocol.Values.Single(static value => value.WireValue == "auto").Name).IsEqualTo("Auto");
    }

    [Test]
    [Arguments("Mcp.Missing", "2026-07-28", "Revision20260728", BindingErrorCategory.Curation, "Mcp.Missing/2026-07-28", "curated schema does not exist in the spec")]
    [Arguments("Holder", "2026-07-28", "Revision20260728", BindingErrorCategory.Curation, "Holder/2026-07-28", "curated schema is not an enum")]
    [Arguments("Mcp.Protocol", "2099-01-01", "Revision20990101", BindingErrorCategory.Curation, "Mcp.Protocol/2099-01-01", "curated enum does not carry the value '2099-01-01'")]
    [Arguments("Mcp.Protocol", "2026-07-28", "2026", BindingErrorCategory.Naming, "Mcp.Protocol/2026-07-28", "enum member name '2026' is not a valid C# identifier")]
    [Arguments("Mcp.Protocol", "2026-07-28", "Legacy", BindingErrorCategory.Naming, "Mcp.Protocol/2026-07-28", "enum member name 'Legacy' collides with the name of value 'legacy'")]
    [Arguments("Unselected", "old", "Previous", BindingErrorCategory.Curation, "Unselected/old", "curated schema is not referenced by the selected profile")]
    [Arguments("mechanical", "a-b", "AB", BindingErrorCategory.Naming, "Mode", "multiple enum values map to C# name 'AB'")]
    public async Task Bind_Should_Refuse_Invalid_Enum_Member_Names(
        string schema, string value, string dotnetName, BindingErrorCategory category, string subject, string problem)
    {
        var scenario = schema == "mechanical" ? ModeScenario("a-b", "a_b") : Scenario(unselected: schema == "Unselected");
        var curation = Curation(Groups(schema == "mechanical" ? "mode" : "mcp", RootGroup()),
            enumMemberNames: schema == "mechanical" ? [] : [EnumMemberName(schema, value, dotnetName)]);

        var exception = await BindRefusalsAsync(scenario, curation, schema == "mechanical" ? "mode.get" : "mcp.get");

        var expected = new BindingError(category, subject, problem);
        await Assert.That(exception.Errors.Single(error => error.Category == expected.Category && error.Subject == expected.Subject))
            .IsEqualTo(expected);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Row_Without_A_Reason()
    {
        var document = await BindingTestHost.IngestAsync(Scenario());
        var curation = Curation(Groups("mcp", RootGroup()),
            enumMemberNames: [EnumMemberName("Mcp.Protocol", "2026-07-28", "Revision20260728", reason: " ")]);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("mcp.get"), curation));

        await Assert.That(exception.Errors.Any(static error => error.Problem.Contains("must declare a reason", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Duplicated_Row()
    {
        var document = await BindingTestHost.IngestAsync(Scenario());
        var curation = Curation(Groups("mcp", RootGroup()),
            enumMemberNames:
            [
                EnumMemberName("Mcp.Protocol", "2026-07-28", "Revision20260728"),
                EnumMemberName("Mcp.Protocol", "2026-07-28", "Revision2026"),
            ]);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("mcp.get"), curation));

        await Assert.That(exception.Errors.Any(static error => error.Problem.Contains("is duplicated", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// A value whose C# name is a member the extensible enum declares itself would collide with
    /// the generated struct, so binding refuses it and points to the curation row that names it.
    /// </summary>
    [Test]
    [Arguments("is-known", "enum value 'is-known' maps to C# name 'IsKnown', which the generated enum declares itself; name the value through an enumMemberNames row")]
    [Arguments("value", "enum value 'value' maps to C# name 'Value', which the generated enum declares itself; name the value through an enumMemberNames row")]
    [Arguments("values", "enum value 'values' maps to C# name 'Values', which the generated enum declares itself; name the value through an enumMemberNames row")]
    public async Task Bind_Should_Refuse_A_Value_Named_Like_A_Member_The_Enum_Declares(string wireValue, string problem)
    {
        var document = await BindingTestHost.IngestAsync(ModeScenario(wireValue, "auto"));

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("mode.get"), Curation(Groups("mode", RootGroup()))));

        await Assert.That(exception.Errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Naming, "Mode", problem));
    }

    [Test]
    public async Task Bind_Should_Accept_A_Colliding_Value_Once_A_Row_Names_It()
    {
        var document = await BindingTestHost.IngestAsync(ModeScenario("is-known", "auto"));
        var curation = Curation(Groups("mode", RootGroup()), enumMemberNames: [EnumMemberName("Mode", "is-known", "KnownState")]);

        var plan = new BindingTestHost().Bind(document, Selection("mode.get"), curation);

        var mode = plan.Models.OfType<EnumModelPlan>().Single(static model => model.Name == "Mode");
        await Assert.That(mode.Values.Single(static value => value.WireValue == "is-known").Name).IsEqualTo("KnownState");
    }

    /// <summary>
    /// "none" and "none-value" name <c>None</c> and <c>NoneValue</c>. Each spelling is written once,
    /// as a constant of the nested <c>Values</c> class under the value's own name, and the static
    /// property and <c>IsKnown</c> read that constant; a constant shares its name only with its own
    /// value's property, which lives one scope out.
    /// </summary>
    [Test]
    public async Task Emit_Should_Write_Each_Spelling_Once_As_A_Values_Constant()
    {
        var document = await BindingTestHost.IngestAsync(ModeScenario("none", "none-value"));
        var plan = new BindingTestHost().Bind(document, Selection("mode.get"), Curation(Groups("mode", RootGroup())));
        var mode = plan.Models.OfType<EnumModelPlan>().Single(static model => model.Name == "Mode");

        var source = Encoding.UTF8.GetString(ExtensibleEnumEmitter.Emit(mode, plan.Placement)[0].Utf8Source.Span);

        await Assert.That(source).Contains("public const string None = \"none\";");
        await Assert.That(source).Contains("public const string NoneValue = \"none-value\";");
        await Assert.That(source).Contains("public static Mode None { get; } = new(Values.None);");
        await Assert.That(source).Contains("public static Mode NoneValue { get; } = new(Values.NoneValue);");
        await Assert.That(source).Contains("public bool IsKnown => _value is Values.None or Values.NoneValue;");
        await Assert.That(source.Split("\"none\"").Length - 1).IsEqualTo(1);
    }

    private static SpecScenario ModeScenario(params string[] values) =>
        SpecScenario.Define(spec => spec
            .WithSchema("Mode", schema => schema.Type("string").Enum(values))
            .WithSchema("Holder", static schema => schema.Type("object")
                .Property("mode", static property => property.Ref("Mode"), required: true))
            .WithOperation("mode.get", path: "/api/mode", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("Holder"))));

    /// <summary>The pinned shape in miniature: an enum whose first value leads with a digit, reached through one response.</summary>
    private static SpecScenario Scenario(bool unselected = false) =>
        SpecScenario.Define(spec =>
        {
            if (unselected)
            {
                spec.WithSchema("Unselected", static schema => schema.Type("string").Enum("old", "new"));
            }
            spec.WithSchema("Mcp.Protocol", static schema => schema.Type("string").Enum("2026-07-28", "legacy", "auto"))
            .WithSchema("Holder", static schema => schema.Type("object")
                .Property("protocol", static property => property.Ref("Mcp.Protocol"), required: true))
            .WithOperation("mcp.get", path: "/api/mcp", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("Holder")));
        });
}
