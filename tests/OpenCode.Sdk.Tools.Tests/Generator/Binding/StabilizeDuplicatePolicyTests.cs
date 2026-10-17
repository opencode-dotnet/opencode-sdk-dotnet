using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// One test per guardrail of the approved stabilize-duplicate collapse: a reachable
/// <c>&lt;base&gt;_&lt;N&gt;</c> folds into <c>&lt;base&gt;</c> when
/// <see cref="SchemaNodeComparer.DeepEquals"/> holds and refuses naming both when it does not;
/// the collapse runs to a fixpoint; it never chains; it leaves every other spelling to the
/// explicit <c>schemaAliases</c> rows and to the duplicate walls that already exist.
/// </summary>
public sealed class StabilizeDuplicatePolicyTests
{
    private static async Task AssertWidgetFoldAsync(BindingErrorCollector errors, StabilizeDuplicateCollapse collapse)
    {
        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(Folds(collapse)).IsEquivalentTo(["Widget_1 -> Widget"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false, BindingErrorCategory.Schema, "stabilize duplicate 'Widget_1' is not structurally identical to its base 'Widget'")]
    [Arguments(true, BindingErrorCategory.Curation, "stabilize duplicate 'Widget_1' folds into 'Widget' mechanically, so its schemaNames row is redundant; remove the row")]
    public async Task Resolve_Should_Refuse_Invalid_Duplicate_Curation(bool identical, BindingErrorCategory category, string problem)
    {
        var document = await BindingTestHost.IngestAsync(TwoWidgetsScenario(schema =>
        {
            Widget(schema);
            if (!identical)
            {
                schema.Property("extra", static property => property.Type("string"));
            }
        }));
        var errors = new BindingErrorCollector();
        var reachable = new ReachableSchemaCollector().Collect(document, document.Operations, errors);
        _ = new StabilizeDuplicatePolicy().Resolve(document, reachable, identical ? [SchemaName("Widget_1", "ReviewedWidget")] : [], errors);
        await Assert.That(Assert.Throws<BindingException>(errors.ThrowIfAny).Errors.Single()).IsEqualTo(new BindingError(category, "Widget_1", problem));
    }

    [Test]
    public async Task Resolve_Should_Fold_A_Reachable_Duplicate_Into_Its_Base_When_The_Shapes_Are_Identical()
    {
        var errors = new BindingErrorCollector();

        var collapse = await ResolveAsync(TwoWidgetsScenario(static schema => Widget(schema)), errors);

        await AssertWidgetFoldAsync(errors, collapse);
    }

    [Test]
    public async Task Resolve_Should_Fold_Objects_Whose_Properties_Are_Declared_In_A_Different_Order()
    {
        var errors = new BindingErrorCollector();

        var reorderedObjects = ReorderedDuplicateScenario("Widget",
            static schema => schema
                .Type("object")
                .Property("id", static property => property.Type("string"), required: true)
                .Property("count", static property => property.Type("integer")),
            static schema => schema
                .Type("object")
                .Property("count", static property => property.Type("integer"))
                .Property("id", static property => property.Type("string"), required: true));

        var collapse = await ResolveAsync(reorderedObjects, errors);

        await AssertWidgetFoldAsync(errors, collapse);
    }

    [Test]
    public async Task Resolve_Should_Fold_Objects_Whose_Required_Literal_Properties_Are_Declared_In_A_Different_Order()
    {
        var errors = new BindingErrorCollector();

        var reorderedLiterals = ReorderedDuplicateScenario("Widget",
            static schema => schema
                .Type("object")
                .Property("type", static property => property.Type("string").Const("widget"), required: true)
                .Property("state", static property => property.Type("string").Const("ready"), required: true),
            static schema => schema
                .Type("object")
                .Property("state", static property => property.Type("string").Const("ready"), required: true)
                .Property("type", static property => property.Type("string").Const("widget"), required: true));

        var collapse = await ResolveAsync(reorderedLiterals, errors);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(Folds(collapse)).IsEquivalentTo(["Widget_1 -> Widget"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Resolve_Should_Fold_Objects_Whose_Required_Prefix_And_Literal_Properties_Are_Declared_In_A_Different_Order()
    {
        var errors = new BindingErrorCollector();

        var reorderedPrefixesAndLiterals = ReorderedDuplicateScenario("Rpc.Event",
            static schema => schema
                .Type("object")
                .Property("type", static property => property.Type("string").Const("event"), required: true)
                .Property("id", static property => property.Type("string").Pattern("^rpc\\.[\\s\\S]*?$"), required: true)
                .Property("source", static property => property.Type("string").Pattern("^plugin\\.[\\s\\S]*?$"), required: true),
            static schema => schema
                .Type("object")
                .Property("source", static property => property.Type("string").Pattern("^plugin\\.[\\s\\S]*?$"), required: true)
                .Property("id", static property => property.Type("string").Pattern("^rpc\\.[\\s\\S]*?$"), required: true)
                .Property("type", static property => property.Type("string").Const("event"), required: true));

        var collapse = await ResolveAsync(reorderedPrefixesAndLiterals, errors);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(Folds(collapse)).IsEquivalentTo(["Rpc.Event_1 -> Rpc.Event"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Resolve_Should_Refuse_A_Duplicate_Naming_Both_Keys_When_The_Shapes_Differ()
    {
        var errors = new BindingErrorCollector();

        var collapse = await ResolveAsync(
            TwoWidgetsScenario(static schema => Widget(schema).Property("extra", static property => property.Type("string"))),
            errors);

        await Assert.That(collapse.Aliases.Count).IsEqualTo(0);
        var refusal = Refusals(errors).Single();
        await Assert.That(refusal.Category).IsEqualTo(BindingErrorCategory.Schema);
        await Assert.That(refusal.Subject).IsEqualTo("Widget_1");
        await Assert.That(refusal.Problem).Contains("'Widget_1'");
        await Assert.That(refusal.Problem).Contains("'Widget'");
    }

    /// <summary>
    /// The projected nodes are identical property for property — only the template-literal
    /// prefix each one tags differs — so the collapse sees the difference only because the
    /// comparer reads prefix markers.
    /// </summary>
    [Test]
    public async Task Resolve_Should_Refuse_A_Duplicate_Whose_Prefix_Marker_Differs()
    {
        var errors = new BindingErrorCollector();

        var collapse = await ResolveAsync(
            SpecScenario.Define(static spec => _ = spec
                .WithSchema("Rpc.Event", static schema => PrefixTagged(schema, "^rpc\\.[\\s\\S]*?$"))
                .WithSchema("Rpc.Event_1", static schema => PrefixTagged(schema, "^plugin\\.[\\s\\S]*?$"))
                .WithSchema("Holder", static schema => schema
                    .Type("object")
                    .Property("a", static property => property.Ref("Rpc.Event"), required: true)
                    .Property("b", static property => property.Ref("Rpc.Event_1"), required: true))
                .WithOperation("holder.get", path: "/api/holder", configure: static operation => operation
                    .Response(200, "application/json", static schema => schema.Ref("Holder")))),
            errors);

        await Assert.That(collapse.Aliases.Count).IsEqualTo(0);
        var refusal = Refusals(errors).Single();
        await Assert.That(refusal.Category).IsEqualTo(BindingErrorCategory.Schema);
        await Assert.That(refusal.Subject).IsEqualTo("Rpc.Event_1");
        await Assert.That(refusal.Problem).Contains("'Rpc.Event_1'");
        await Assert.That(refusal.Problem).Contains("'Rpc.Event'");
    }

    [Test]
    public async Task Resolve_Should_Run_To_A_Fixpoint_When_A_Refused_Duplicate_Breaks_A_Dependent_Pair()
    {
        var errors = new BindingErrorCollector();

        var collapse = await ResolveAsync(
            NestedDuplicateScenario(static schema => Part(schema).Property("extra", static property => property.Type("string"))),
            errors);

        await Assert.That(collapse.Aliases.Count).IsEqualTo(0);
        // Diagnostic membership and multiplicity matter, not report enumeration order.
        await Assert
            .That(Refusals(errors).Select(static error => error.Subject))
            .IsEquivalentTo(["Gadget_1", "Part_1"], CollectionOrdering.Any);
    }

    [Test]
    public async Task Resolve_Should_Fold_A_Pair_Whose_Identity_Depends_On_Another_Fold()
    {
        var errors = new BindingErrorCollector();

        var collapse = await ResolveAsync(NestedDuplicateScenario(static schema => Part(schema)), errors);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert
            .That(Folds(collapse))
            .IsEquivalentTo(["Gadget_1 -> Gadget", "Part_1 -> Part"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Resolve_Should_Decline_To_Chain_When_The_Base_Is_Itself_A_Duplicate()
    {
        var errors = new BindingErrorCollector();

        var collapse = await ResolveAsync(
            SpecScenario.Define(static spec => DefineWidgets(spec, static schema => Widget(schema), "Widget_1", "Widget_1_2")),
            errors);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(Folds(collapse)).IsEquivalentTo(["Widget_1 -> Widget"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Resolve_Should_Leave_A_Duplicate_Without_The_Stabilize_Suffix_To_The_Explicit_Alias_Rows()
    {
        var errors = new BindingErrorCollector();

        var collapse = await ResolveAsync(
            SpecScenario.Define(static spec => DefineWidgets(spec, static schema => Widget(schema), "WidgetEncoded", "Widget1")),
            errors);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(collapse.Aliases.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_Should_Not_Fold_A_Suffix_That_Is_Not_A_Positive_Integer()
    {
        var errors = new BindingErrorCollector();

        var collapse = await ResolveAsync(
            SpecScenario.Define(static spec => DefineWidgets(spec, static schema => Widget(schema), "Widget_0", "Widget_01", "Widget_x")),
            errors);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(collapse.Aliases.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_Should_Not_Fold_A_Duplicate_Whose_Base_Is_Absent()
    {
        var errors = new BindingErrorCollector();

        var collapse = await ResolveAsync(
            SpecScenario.Define(static spec => DefineWidgets(spec, static schema => Widget(schema), "Gizmo_1")),
            errors);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(collapse.Aliases.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_Should_Ignore_A_Duplicate_The_Selected_Profile_Never_Reaches()
    {
        var errors = new BindingErrorCollector();

        var collapse = await ResolveAsync(
            SpecScenario.Define(static spec =>
            {
                DefineWidgets(spec, static schema => Widget(schema));
                _ = spec
                    .WithSchema("Lonely", static schema => Widget(schema))
                    .WithSchema("Lonely_1", static schema => Widget(schema));
            }),
            errors);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(collapse.Aliases.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_Should_Fold_Equivalent_Pinned_Duplicates_And_Retain_The_Curated_Move_Contract()
    {
        var errors = new BindingErrorCollector();
        var (document, selection, curation) = await BindingTestHost.LoadPinnedInputsAsync();
        var operations = document
            .Operations.Where(operation => selection.OperationIds.Contains(operation.OperationId, StringComparer.Ordinal))
            .ToArray();
        var reachable = new ReachableSchemaCollector().Collect(document, operations, errors);

        var collapse = new StabilizeDuplicatePolicy().Resolve(document, reachable, curation.SchemaNames, errors);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(collapse.Aliases.Count).IsGreaterThan(0);
        await Assert.That(collapse.Aliases).DoesNotContainKey("Session.Inbox.MovePayload_1");
        await Assert.That(Folds(collapse)).IsEquivalentTo(ReachableStabilizeDuplicates(document, reachable)
            .Except(["Session.Inbox.MovePayload_1 -> Session.Inbox.MovePayload"], StringComparer.Ordinal), CollectionOrdering.Matching);
    }

    /// <summary>
    /// Re-derives the convention independently of the policy: every reachable component key
    /// spelled <c>&lt;base&gt;_&lt;N&gt;</c> over an existing, unsuffixed base. The pinned
    /// assertion covers every candidate except the explicitly distinct move contract.
    /// </summary>
    private static IReadOnlyList<string> ReachableStabilizeDuplicates(SpecDocument document, ReachableSchemaSet reachable)
    {
        var expected = new List<string>();
        foreach (var key in reachable.GraphKeys)
        {
            if (StabilizeBaseOf(key) is { } baseKey && StabilizeBaseOf(baseKey) is null && document.Schemas.ContainsKey(baseKey))
            {
                expected.Add($"{key} -> {baseKey}");
            }
        }

        return [.. expected.Order(StringComparer.Ordinal)];
    }

    private static string? StabilizeBaseOf(string key)
    {
        if (key.Contains('#', StringComparison.Ordinal))
        {
            return null;
        }

        var separator = key.AsSpan().LastIndexOf('_');
        if (separator <= 0 || separator == key.Length - 1)
        {
            return null;
        }

        var suffix = key[(separator + 1)..];
        return suffix[0] is not '0' && suffix.All(char.IsAsciiDigit) ? key[..separator] : null;
    }

    private static async Task<StabilizeDuplicateCollapse> ResolveAsync(SpecScenario scenario, BindingErrorCollector errors)
    {
        var document = await BindingTestHost.IngestAsync(scenario);
        var reachable = new ReachableSchemaCollector().Collect(document, document.Operations, errors);
        return new StabilizeDuplicatePolicy().Resolve(document, reachable, [], errors);
    }

    private static IReadOnlyList<string> Folds(StabilizeDuplicateCollapse collapse) =>
        [.. collapse.Aliases.Select(static pair => $"{pair.Key} -> {pair.Value}").Order(StringComparer.Ordinal)];

    private static IReadOnlyList<BindingError> Refusals(BindingErrorCollector errors)
    {
        var exception = Assert.Throws<BindingException>(errors.ThrowIfAny);
        return exception.Errors;
    }

    /// <summary>One reachable duplicate of <c>Widget</c> under the shape the caller supplies.</summary>
    private static SpecScenario TwoWidgetsScenario(Action<SchemaBuilder> duplicate) =>
        SpecScenario.Define(spec => DefineWidgets(spec, duplicate, "Widget_1"));

    /// <summary>A reachable base and stabilize twin with independently configured property order.</summary>
    private static SpecScenario ReorderedDuplicateScenario(
        string baseName,
        Action<SchemaBuilder> configureBase,
        Action<SchemaBuilder> configureDuplicate)
    {
        var duplicateName = $"{baseName}_1";
        var resource = baseName[(baseName.AsSpan().LastIndexOf('.') + 1)..].ToLowerInvariant();
        return SpecScenario.Define(spec => _ = spec
            .WithSchema(baseName, configureBase)
            .WithSchema(duplicateName, configureDuplicate)
            .WithOperation($"{resource}.get", path: $"/api/{resource}", configure: operation => operation
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    .Property("primary", property => property.Ref(baseName), required: true)
                    .Property("secondary", property => property.Ref(duplicateName), required: true))));
    }

    /// <summary>
    /// A duplicate whose identity runs through a second duplicate: <c>Gadget_1</c> matches
    /// <c>Gadget</c> only while <c>Part_1</c> resolves to <c>Part</c>.
    /// </summary>
    private static SpecScenario NestedDuplicateScenario(Action<SchemaBuilder> duplicatePart) =>
        SpecScenario.Define(spec => _ = spec
            .WithSchema("Part", static schema => Part(schema))
            .WithSchema("Part_1", duplicatePart)
            .WithSchema("Gadget", static schema => Gadget(schema, "Part"))
            .WithSchema("Gadget_1", static schema => Gadget(schema, "Part_1"))
            .WithOperation("gadget.get", path: "/api/gadget", configure: static operation => operation
                .Response(200, "application/json", static schema => schema
                    .Type("object")
                    .Property("primary", static property => property.Ref("Gadget"), required: true)
                    .Property("secondary", static property => property.Ref("Gadget_1"), required: true)
                    .Property("leaf", static property => property.Ref("Part_1"), required: true))));

    private static void DefineWidgets(SpecDocumentBuilder spec, Action<SchemaBuilder> duplicate, params string[] duplicateNames)
    {
        _ = spec.WithSchema("Widget", static schema => Widget(schema));
        foreach (var name in duplicateNames)
        {
            _ = spec.WithSchema(name, duplicate);
        }

        _ = spec.WithOperation("widget.get", path: "/api/widget", configure: operation => operation
            .Response(200, "application/json", schema =>
            {
                _ = schema.Type("object").Property("primary", static property => property.Ref("Widget"), required: true);
                foreach (var name in duplicateNames)
                {
                    _ = schema.Property(name, property => property.Ref(name), required: true);
                }
            }));
    }

    private static SchemaBuilder Widget(SchemaBuilder schema) => schema
        .Type("object")
        .Property("id", static property => property.Type("string"), required: true);

    private static void PrefixTagged(SchemaBuilder schema, string pattern) => _ = schema
        .Type("object")
        .Property("type", property => property.Type("string").Pattern(pattern), required: true);

    private static SchemaBuilder Part(SchemaBuilder schema) => schema
        .Type("object")
        .Property("label", static property => property.Type("string"), required: true);

    private static void Gadget(SchemaBuilder schema, string partName) => _ = schema
        .Type("object")
        .Property("part", property => property.Ref(partName), required: true);
}
