using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class SpecBinderTests
{
    [Test]
    [Arguments(false, "JSON-encoded strings are not supported by the emitter")]
    [Arguments(true, "tuple schemas are not supported by the emitter")]
    public async Task Bind_Should_Refuse_Unsupported_Type_Shapes_Without_Milestone_Text(bool tuple, string problem)
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithObject("ItemInfo", schema => schema.Property("value", value =>
            {
                if (tuple)
                {
                    value.Type("array").PrefixItems(static item => item.Type("string"), static item => item.Type("integer"))
                        .MinItems(2).MaxItems(2);
                }
                else
                {
                    value.Type("string").ContentSchema("application/json", static inner => inner.Type("string"));
                }
            }, required: true))
            .WithJsonOperation("health.get", "/health", static schema => schema.Ref("ItemInfo"))));
        var exception = RefuseHealth(document);
        await Assert.That(exception.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Schema, "ItemInfo.value", problem),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Preserve_The_Marker_Refusal_For_An_Unnamed_Error_Schema()
    {
        var document = await IngestAsync(SpecScenario.Define(static spec =>
        {
            AddTaggedError(spec, "AError", "A");
            AddTaggedError(spec, "BError", "B");
            spec.WithOperation("health.get", configure: static operation => operation.WithoutResponse(200).Response(204)
                .Response(400, "application/json", static schema => schema.Ref("AError"))
                .Response(500, "application/json", static schema => schema.Ref("BError")));
        }));
        var schemas = document.Schemas.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        schemas["BError"] = (ObjectNode)schemas["BError"] with { LiteralMarkers = [] };
        document = document with { Schemas = schemas };
        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("health.get"),
            Curation(Groups("health", RootGroup()), schemaNames: [SchemaName("AError", "SharedError"), SchemaName("BError", "SharedError")])));

        await Assert.That(exception.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, "BError", "C# type name 'SharedError' collides with schema 'AError'"),
            new BindingError(BindingErrorCategory.Naming, "BError", "error schema has no unique C# type name"),
            new BindingError(BindingErrorCategory.Operation, "health.get", "error responses must reference tagged error schemas: 'BError': a tagged error must declare exactly one required '_tag' literal"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Report_An_Unnamed_Error_Schema_Once()
    {
        var document = await IngestAsync(SpecScenario.Define(static spec => spec
            .WithSchema("AError", static schema => schema.Type("object")
                .Property("_tag", static property => property.Type("string").Enum("A"), required: true))
            .WithSchema("BError", static schema => schema.Type("object")
                .Property("_tag", static property => property.Type("string").Enum("B"), required: true))
            .WithOperation("health.get", configure: static operation => operation.WithoutResponse(200).Response(204)
                .Response(400, "application/json", static schema => schema.Ref("AError"))
                .Response(500, "application/json", static schema => schema.Ref("BError")))));
        var curation = Curation(Groups("health", RootGroup()), schemaNames: [SchemaName("AError", "SharedError"), SchemaName("BError", "SharedError")]);
        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("health.get"), curation));
        await Assert.That(exception.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, "BError", "C# type name 'SharedError' collides with schema 'AError'"),
            new BindingError(BindingErrorCategory.Naming, "BError", "error schema has no unique C# type name"),
        ], CollectionOrdering.Matching);
    }

    private static readonly string[] ParentScenarioLeaves = ["First", "Second", "Other", "Another"];

    [Test]
    public async Task Bind_Should_Keep_The_Error_Union_Independent_Of_A_Component_With_The_Same_Key()
    {
        var document = await IngestAsync(UnionScenario(spec =>
        {
            AddTaggedError(spec, "OpenCodeError", "first");
            AddTaggedError(spec, "SecondError", "second");
            spec.WithSchema("Feed", schema => schema.AnyOf(first => first.Ref("OpenCodeError"), second => second.Ref("SecondError")));
        }));
        var plan = new BindingTestHost().Bind(document, Selection("health.get"), Curation(Groups("health", RootGroup()),
            schemaNames: [SchemaName("OpenCodeError", "FirstError")]));
        var errorUnion = plan.Unions.Single(union => union.Name == "IOpenCodeError");

        await Assert.That(errorUnion.BaseTypeName).IsNull();
        await Assert.That(errorUnion.FixedMarker).IsNull();
        await Assert.That(errorUnion.Variants.Select(variant => variant.TypeName))
            .IsEquivalentTo(["FirstError", "SecondError"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(plan.Models.OfType<ObjectModelPlan>().Single(model => model.Name == "FirstError").ImplementedUnionNames)
            .IsEquivalentTo(["IFeed", "IOpenCodeError"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Stream_Cause_Union_With_A_Union_Parent_By_Name()
    {
        var nested = await IngestAsync(NestedParentScenario("Cause", twoParents: false));
        var stream = await IngestAsync(new StreamOperationScenario());
        var operation = stream.Operations.Single();
        var document = nested with
        {
            Schemas = nested.Schemas.Concat(stream.Schemas).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Operations = [.. nested.Operations, operation with
            {
                Responses = [.. operation.Responses.Select(response => response.EffectStream is null ? response : response with
                {
                    EffectStream = response.EffectStream with { CauseSchema = new ArrayNode { Item = new RefNode { Target = "Cause" } } },
                })],
            }],
        };
        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document,
            Selection("health.get", StreamOperationScenario.OperationId),
            Curation(new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
            {
                ["health"] = RootGroup(),
                ["example"] = RootGroup(),
            })));

        await Assert.That(exception.Errors)
            .IsEquivalentTo([
                new BindingError(BindingErrorCategory.Naming, "Cause",
                    "stream-cause union claim cannot rename a key also referenced by schema 'OuterA'"),
                new BindingError(BindingErrorCategory.Schema, "Cause",
                    "nested union has multiple parents 'IOuterA' and 'IOpenCodeStreamFailureCause'"),
            ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Nested_Union_With_Two_Parents_By_Name()
    {
        var document = await IngestAsync(NestedParentScenario("Inner", twoParents: true));
        var exception = RefuseHealth(document);

        await Assert.That(exception.Errors)
            .IsEquivalentTo([new BindingError(BindingErrorCategory.Schema, "Inner",
                "nested union has multiple parents 'IOuterA' and 'IOuterB'")], CollectionOrdering.Matching);
    }

    private static SpecScenario NestedParentScenario(string nestedKey, bool twoParents) => UnionScenario(spec =>
    {
        foreach (var name in ParentScenarioLeaves)
        {
            spec.WithSchema(name, schema => schema.Type("object")
                .Property("type", property => property.Type("string").Enum(name), required: true));
        }

        spec.WithSchema(nestedKey, schema => schema.AnyOf(first => first.Ref("First"), second => second.Ref("Second")))
            .WithSchema("OuterA", schema => schema.AnyOf(inner => inner.Ref(nestedKey), other => other.Ref("Other")))
            .WithSchema("Feed", schema => schema.Type("object")
                .Property("a", property => property.Ref("OuterA"), required: true));
        if (twoParents)
        {
            spec.WithSchema("OuterB", schema => schema.AnyOf(inner => inner.Ref(nestedKey), other => other.Ref("Another")))
                .WithSchema("Feed", schema => schema.Type("object")
                    .Property("a", property => property.Ref("OuterA"), required: true)
                    .Property("b", property => property.Ref("OuterB"), required: true));
        }
    });

    [Test]
    public async Task Bind_Should_Preserve_Marked_And_Error_Variant_Order()
    {
        var document = await IngestAsync(UnionScenario(spec =>
        {
            AddTaggedError(spec, "AError", "middle");
            AddTaggedError(spec, "BError", "zebra");
            AddTaggedError(spec, "CError", "apple");
            spec.WithSchema("Feed", schema => schema.AnyOf(first => first.Ref("BError"), middle => middle.Ref("AError"), last => last.Ref("CError")));
        }));
        var plan = new BindingTestHost().Bind(document, Selection("health.get"), Curation(Groups("health", RootGroup()),
            schemaNames: [SchemaName("AError", "ZuluError"), SchemaName("BError", "AlphaError"), SchemaName("CError", "MikeError")]));

        await Assert.That(plan.Unions.Single(union => union.Name == "IFeed").Variants.Select(variant => variant.TypeName))
            .IsEquivalentTo(["AlphaError", "ZuluError", "MikeError"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(plan.Unions.Single(union => union.Name == "IFeed").Variants.Select(variant => variant.Tag))
            .IsEquivalentTo(["zebra", "middle", "apple"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(plan.Unions.Single(union => union.Name == "IOpenCodeError").Variants.Select(variant => variant.TypeName))
            .IsEquivalentTo(["AlphaError", "MikeError", "ZuluError"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(plan.Unions.Single(union => union.Name == "IOpenCodeError").Variants.Select(variant => variant.Tag))
            .IsEquivalentTo(["zebra", "apple", "middle"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Retain_Per_Schema_Error_Variants_And_Refusals()
    {
        var document = await IngestAsync(UnionScenario(spec =>
        {
            AddTaggedError(spec, "AAdmitted", "admitted");
            AddTaggedError(spec, "BUnnamed", "unnamed");
            AddTaggedError(spec, "CBadMarker", "bad");
            spec.WithSchema("Feed", schema => schema.Type("object")
                .Property("first", property => property.Ref("AAdmitted"))
                .Property("second", property => property.Ref("BUnnamed"))
                .Property("third", property => property.Ref("CBadMarker")));
        }));
        var schemas = document.Schemas.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        // Exercise the error binder's refusal contract over owned IR, after projection.
        schemas["CBadMarker"] = (ObjectNode)schemas["CBadMarker"] with { LiteralMarkers = [] };
        document = document with { Schemas = schemas };
        var errors = new BindingErrorCollector();
        var reachable = ReachableSchemaCollector.Collect(document, document.Operations, errors);
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { ["AAdmitted"] = "AdmittedError", ["CBadMarker"] = "BadMarkerError" };
        var result = new ErrorUnionBinder(SchemaBindingContext.Create(document, reachable, names, errors)).Bind();
        var expectedRefusals = new Dictionary<string, BindingError>(StringComparer.Ordinal)
        {
            ["BUnnamed"] = new(BindingErrorCategory.Naming, "BUnnamed", "error schema has no unique C# type name"),
            ["CBadMarker"] = new(BindingErrorCategory.Schema, "CBadMarker", "a tagged error must declare exactly one required '_tag' literal"),
        };

        await Assert.That(result.Variants).IsEquivalentTo(new Dictionary<string, UnionVariantPlan>(StringComparer.Ordinal)
        {
            ["AAdmitted"] = new() { TypeName = "AdmittedError", Tag = "admitted", MarkerWireName = "_tag" },
        }, CollectionOrdering.Matching);
        await Assert.That(result.Refusals).IsEquivalentTo(expectedRefusals, CollectionOrdering.Matching);
        await Assert.That(Assert.Throws<BindingException>(errors.ThrowIfAny).Errors)
            .IsEquivalentTo(expectedRefusals.Values, CollectionOrdering.Matching);
    }

    private static void AddTaggedError(SpecDocumentBuilder spec, string key, string tag) => spec
        .WithSchema(key, schema => schema.Type("object")
            .Property("_tag", property => property.Type("string").Enum(tag), required: true));

    public static IEnumerable<object?[]> GroupRefusals() => GroupRefusalRows().Select(static row => new[] { row.fault, row.row, row.expected });

    private static IEnumerable<(string fault, object? row, BindingError[] expected)> GroupRefusalRows() =>
    [
        ("missing", null, [new(BindingErrorCategory.Curation, "widget", "selected operation group has no curation row")]),
        ("orphan", RootGroup(), [new(BindingErrorCategory.Curation, "ghost", "curated group does not exist in the spec")]),
        ("unselected", RootGroup(), [new(BindingErrorCategory.Curation, "idle", "curated group is not selected by the current profile")]),
        ("merge-handle", ClientGroup("Widgets", "WidgetClient", "id"), [new(BindingErrorCategory.Curation, "Widgets", "groups sharing client 'Widgets' must declare identical handle and emission configuration and the same experimentalDiagnostic")]),
        ("merge-emission", ClientGroup("Widgets", null, null, emission: EmissionMode.InternalRaw), [new(BindingErrorCategory.Curation, "Widgets", "groups sharing client 'Widgets' must declare identical handle and emission configuration and the same experimentalDiagnostic")]),
        ("merge-diagnostic", ClientGroup("Widgets", null, null, experimentalDiagnostic: "OPENCODE001"), [new(BindingErrorCategory.Curation, "Widgets", "groups sharing client 'Widgets' must declare identical handle and emission configuration and the same experimentalDiagnostic")]),
        ("shared-diagnostic", ClientGroup("Widgets", null, null, experimentalDiagnostic: "OPENCODE001"), [new(BindingErrorCategory.Curation, "OPENCODE001", "experimentalDiagnostic 'OPENCODE001' is shared by groups that are not one merged family")]),
        ("experimental", RootGroup(), [new(BindingErrorCategory.Curation, "widget", "selected experimental route requires experimentalDiagnostic")]),
        ("stable-diagnostic", RootGroup() with { ExperimentalDiagnostic = "OPENCODE001" }, [new(BindingErrorCategory.Curation, "widget", "experimentalDiagnostic requires a selected experimental route")]),
        ("experimental-invalid", RootGroup() with { ExperimentalDiagnostic = "bad" }, [new(BindingErrorCategory.Curation, "widget", "experimentalDiagnostic 'bad' must match ^OPENCODE[0-9]{3}$")]),
        ("root-client", RootGroup() with { ClientName = "Widgets" }, [new(BindingErrorCategory.Curation, "widget", "root group cannot declare clientName, handleName, or handleParameter")]),
        ("root-handle", RootGroup() with { HandleName = "WidgetClient" }, [new(BindingErrorCategory.Curation, "widget", "root group cannot declare clientName, handleName, or handleParameter"),
            new(BindingErrorCategory.Curation, "widget", "handleName and handleParameter must be declared together")]),
        ("root-parameter", RootGroup() with { HandleParameter = "id" }, [new(BindingErrorCategory.Curation, "widget", "root group cannot declare clientName, handleName, or handleParameter"),
            new(BindingErrorCategory.Curation, "widget", "handleName and handleParameter must be declared together")]),
        ("client", ClientGroup(null!, null, null), [new(BindingErrorCategory.Curation, "widget", "client group must declare clientName")]),
        ("placement", RootGroup() with { Placement = (GroupPlacement)99 }, [new(BindingErrorCategory.Curation, "widget", "placement value '99' is not a recognized group placement")]),
        ("client-name", ClientGroup("bad-name", null, null), [new(BindingErrorCategory.Naming, "widget", "client name 'bad-name' is not a valid C# identifier")]),
        ("handle-name", ClientGroup("Widgets", "bad-name", "id"), [new(BindingErrorCategory.Naming, "widget", "handle name 'bad-name' is not a valid C# identifier")]),
        ("half-handle", ClientGroup("Widgets", "WidgetClient", null), [new(BindingErrorCategory.Curation, "widget", "handleName and handleParameter must be declared together")]),
        ("half-parameter", ClientGroup("Widgets", null, "id"), [new(BindingErrorCategory.Curation, "widget", "handleName and handleParameter must be declared together")]),
        ("blank-parameter", ClientGroup("Widgets", "WidgetClient", " "), [new(BindingErrorCategory.Curation, "widget", "handleParameter cannot be blank")]),
        ("emission", RootGroup() with { Emission = (EmissionMode)99 }, [new(BindingErrorCategory.Curation, "widget", "emission value '99' is not a recognized group emission")]),
        ("root-raw", RootGroup() with { Emission = EmissionMode.InternalRaw }, [new(BindingErrorCategory.Curation, "widget", "root group cannot declare internalRaw emission")]),
        ("unselected-raw", ClientGroup("Widgets", null, null, emission: EmissionMode.InternalRaw), [new(BindingErrorCategory.Curation, "idle", "curated group is not selected by the current profile"),
            new(BindingErrorCategory.Curation, "idle", "internalRaw emission requires at least one selected operation")]),
        ("uncovered", ClientGroup("Widgets", "WidgetClient", "id"), [new(BindingErrorCategory.Curation, "widget", "handle parameter 'id' does not name a required path parameter on any selected operation in the group")]),
    ];

    [Test]
    [MethodDataSource(nameof(GroupRefusals))]
    public async Task Bind_Should_Refuse_Invalid_Group_Configuration(string fault, object? value, BindingError[] expected)
    {
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentNullException.ThrowIfNull(expected);
        var row = (GroupCuration?)value;
        var experimental = fault.StartsWith("experimental", StringComparison.Ordinal) || fault == "shared-diagnostic";
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("widget.get", path: experimental ? "/api/experimental/widget" : "/api/widget")
            .WithOperation("other.get", path: "/api/experimental/other").WithOperation("idle.get")));
        var groups = Groups("widget", RootGroup());
        var key = fault switch { "orphan" => "ghost", "unselected" or "unselected-raw" => "idle", _ => "widget" };
        if (row is null)
        {
            groups.Remove("widget");
        }
        else
        {
            groups[key] = row;
        }
        var selected = document.Operations.Where(operation => operation.OperationId == "widget.get"
            || ((fault.StartsWith("merge-", StringComparison.Ordinal) || fault == "shared-diagnostic") && operation.OperationId == "other.get")).ToArray();
        if (selected.Length == 2)
        {
            groups["other"] = ClientGroup(fault == "shared-diagnostic" ? "Others" : "Widgets", null, null,
                experimentalDiagnostic: fault == "shared-diagnostic" ? "OPENCODE001" : null);
        }
        var errors = new BindingErrorCollector();
        CurationValidator.Validate(new BindingContext(document, ReachableSchemaCollector.Collect(document, selected, errors),
            Curation(groups), errors), selected);
        var identity = expected[0];
        await Assert.That(Assert.Throws<BindingException>(errors.ThrowIfAny).Errors
            .Where(error => error.Category == identity.Category && error.Subject == identity.Subject))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("Unused", "Reviewed", BindingErrorCategory.Curation, "curated schema is not referenced by the selected profile")]
    [Arguments("Used", "bad-name", BindingErrorCategory.Naming, "schema name 'bad-name' is not a valid C# identifier")]
    public async Task Bind_Should_Refuse_Invalid_Schema_Name_Rows(string schema, string name, BindingErrorCategory category, string problem)
    {
        var exception = await BindRefusalsAsync(SpecScenario.Define(static spec => spec
            .WithSchema("Used", static value => value.Type("object")).WithSchema("Unused", static value => value.Type("object"))
            .WithOperation("widget.get", configure: static operation => operation.Response(200, "application/json", static value => value.Ref("Used")))),
            Curation(Groups("widget", RootGroup()), schemaNames: [SchemaName(schema, name)]), "widget.get");
        await Assert.That(exception.Errors.Single(error => error.Category == category && error.Subject == schema))
            .IsEqualTo(new BindingError(category, schema, problem));
    }

    [Test]
    [Arguments("malformed", "transport-owned curation must declare subtreeSha256 as 64 lowercase hex characters")]
    [Arguments("missing", "curated operation does not exist in the spec")]
    [Arguments("selected", "transport-owned operation cannot be selected")]
    [Arguments("fingerprint", "transport-owned operation subtree no longer matches the committed subtreeSha256 (declared '0000000000000000000000000000000000000000000000000000000000000000', computed 'b60dc24f9c42b5480039c7c90df72de484c9e4118d18311b4791a64ea90fa71b'); review the reshaped operation and repin")]
    public async Task Bind_Should_Refuse_Invalid_Transport_Owned_Rows(string fault, string problem)
    {
        var scenario = fault == "fingerprint" ? SpecScenario.Define(static spec => spec
            .WithOperation("pty.connect", path: "/api/pty/connect", configure: static operation => operation.WithoutResponse(200).Response(204))
            .WithOperation("health.get", configure: static operation => operation.WithoutResponse(200).Response(204))) : TransportOwnedScenario();
        var document = await IngestAsync(scenario);
        var hash = TransportOwnedFingerprint.ComputeSha256(document.Operations.Single(static operation => operation.OperationId == "pty.connect"));
        var subject = fault == "missing" ? "missing.get" : "pty.connect";
        var errors = new BindingErrorCollector();
        var selected = document.Operations.Where(operation => operation.OperationId == "health.get" || fault == "selected").ToArray();
        var declared = fault switch { "malformed" => "bad", "fingerprint" => new string('0', 64), _ => hash };
        CurationValidator.Validate(new BindingContext(document, ReachableSchemaCollector.Collect(document, selected, errors),
            Curation(Groups("health", RootGroup()), transportOwned: [TransportOwned(subject, declared)]), errors), selected);
        var expected = new BindingError(BindingErrorCategory.Curation, subject, problem);
        var exception = Assert.Throws<BindingException>(errors.ThrowIfAny);
        await Assert.That(exception.Errors.Single(error => error.Category == expected.Category && error.Subject == expected.Subject))
            .IsEqualTo(expected);
    }

    [Test]
    [Arguments("missing.get", false, "curated operation does not exist in the spec")]
    [Arguments("health.get", false, "declined operation cannot be selected")]
    [Arguments("pty.connect", true, "declined operation cannot also be transport-owned")]
    public async Task Bind_Should_Refuse_Invalid_Declined_Rows(string operationId, bool transport, string problem)
    {
        var document = await IngestAsync(TransportOwnedScenario());
        var hash = TransportOwnedFingerprint.ComputeSha256(document.Operations.Single(static operation => operation.OperationId == "pty.connect"));
        var curation = Curation(Groups("health", RootGroup()), declined: [Declined(operationId)],
            transportOwned: transport ? [TransportOwned("pty.connect", hash)] : []);
        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("health.get"), curation));
        await Assert.That(exception.Errors.Single(error => error.Category == BindingErrorCategory.Curation && error.Subject == operationId))
            .IsEqualTo(new BindingError(BindingErrorCategory.Curation, operationId, problem));
    }

    [Test]
    [Arguments("self", "GadgetError1", "GadgetError1", "schema alias cannot target itself")]
    [Arguments("source", "GhostError", "GadgetError", "aliased schema does not exist in the spec")]
    [Arguments("target", "GadgetError1", "GhostError", "alias target 'GhostError' does not exist in the spec")]
    [Arguments("unused", "LonelyError1", "LonelyError", "aliased schema is not referenced by the selected profile")]
    [Arguments("different", "GadgetError1", "GadgetError", "aliased schemas must be structurally identical")]
    [Arguments("chain", "GadgetError2", "GadgetError1", "aliased schema is not referenced by the selected profile", "schema aliases cannot chain")]
    [Arguments("fold-target", "GadgetErrorAlt", "GadgetError_1", "schema aliases cannot chain: the stabilize-duplicate collapse already folds the target 'GadgetError_1' into 'GadgetError'")]
    [Arguments("fold-base", "GadgetError", "GadgetErrorAlt", "schema aliases cannot chain: the stabilize-duplicate collapse already folds 'GadgetError_1' into 'GadgetError'")]
    public async Task Bind_Should_Refuse_Invalid_Alias_Rows(string fault, string source, string target, string problem, string? secondProblem = null)
    {
        ArgumentNullException.ThrowIfNull(fault);
        var scenario = fault.StartsWith("fold-", StringComparison.Ordinal) ? StabilizeAndCuratedDuplicateScenario() : SpecScenario.Define(spec =>
        {
            DefineDuplicateTagSpec(spec, fault == "different" ? static schema => schema.Type("string") : DefaultDuplicate);
            spec.WithSchema("LonelyError", DefaultDuplicate).WithSchema("LonelyError1", DefaultDuplicate).WithSchema("GadgetError2", DefaultDuplicate);
        });
        var aliases = fault == "chain" ? new[] { Alias(source, target), Alias("GadgetError1", "GadgetError") } : [Alias(source, target)];
        var exception = await BindRefusalsAsync(scenario, GadgetCuration(aliases), "gadget.get");
        BindingError[] expected = secondProblem is null ? [new(BindingErrorCategory.Curation, source, problem)]
            : [new(BindingErrorCategory.Curation, source, problem), new(BindingErrorCategory.Curation, source, secondProblem)];
        await Assert.That(exception.Errors.Where(error => error.Category == BindingErrorCategory.Curation && error.Subject == source))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    private static Action<SchemaBuilder>[] SpecialNumberArms() =>
    [
        value => value.Type("number"),
        value => value.Type("string").Enum("NaN"),
        value => value.Type("string").Enum("Infinity"),
        value => value.Type("string").Enum("-Infinity"),
    ];

    private static EmitPlan BindHealth(SpecDocument document) => new BindingTestHost().Bind(
        document,
        Selection("health.get"),
        Curation(new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["health"] = RootGroup(),
        }));

    private static BindingException RefuseHealth(SpecDocument document) =>
        Assert.Throws<BindingException>(() => _ = BindHealth(document));

    private static async Task AssertThingAsync(EmitPlan plan) =>
        await Assert.That(plan.Models).Contains(static model => model.Name == "Thing");

    private static async Task AssertMarkedTagsAsync(UnionPlan union, string marker, IReadOnlyList<string> tags)
    {
        await Assert.That(union.MarkerWireName).IsEqualTo(marker);
        await Assert.That(union.Variants.Select(static variant => variant.Tag).Order(StringComparer.Ordinal))
            .IsEquivalentTo(tags, StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    private static async Task AssertCurationProblemAsync(BindingException exception, string problem) =>
        await Assert.That(exception.Errors).Contains(error => error.Category == BindingErrorCategory.Curation
            && error.Problem.Contains(problem, StringComparison.Ordinal));

    private static BindingException RefuseHealthGroups(SpecDocument document, IReadOnlyDictionary<string, GroupCuration> groups) =>
        Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(document, Selection("health.get"), Curation(groups)));

    private static EmitPlan BindHealthRows(SpecDocument document) =>
        new BindingTestHost().Bind(document, Selection("health.get"), Curation(Groups("health", RootGroup())));

    private static SpecScenario ChoiceScenario(Action<SpecDocumentBuilder> configure) => SpecScenario.Define(spec =>
    {
        configure(spec);
        _ = spec.WithSchema("Container", schema => schema.Type("object")
                .Property("choice", property => property.Ref("Choice"), required: true))
            .WithOperation("choice.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Container")));
    });

    private static SpecScenario MarkedNestedUnionScenario(Action<SchemaBuilder> wrap) => SpecScenario.Define(spec => spec
        .WithSchema("Alpha", schema => schema.Type("object")
            .Property("type", property => property.Type("string").Enum("alpha"), required: true))
        .WithSchema("WrapOne", schema => schema.Type("object")
            .Property("type", property => property.Type("string").Enum("wrap"), required: true)
            .Property("status", property => property.Type("string").Enum("one"), required: true))
        .WithSchema("WrapTwo", schema => schema.Type("object")
            .Property("type", property => property.Type("string").Enum("wrap"), required: true)
            .Property("status", property => property.Type("string").Enum("two"), required: true))
        .WithSchema("Wrap", wrap)
        .WithSchema("Outer", schema => schema.AnyOf(alpha => alpha.Ref("Alpha"), branch => branch.Ref("Wrap")))
        .WithOperation("health.get", configure: operation => operation
            .Response(200, "application/json", schema => schema.Ref("Outer"))));

    private static async Task<UnionPlan> AssertPrefixEventsAsync(EmitPlan plan)
    {
        var events = plan.Unions.Single(static union => union.Name == "IEvent");
        await AssertMarkedTagsAsync(events, "type", ["created", "deleted"]);
        return events;
    }

    private static async Task AssertChoiceRefusalAsync(SpecDocument document, string problem)
    {
        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document, Selection("choice.get"), Curation(Groups("choice", RootGroup()))));
        await Assert.That(exception.Errors).Contains(error => error.Subject == "Choice"
            && error.Problem.Contains(problem, StringComparison.Ordinal));
    }

    private static async Task AssertGadgetErrorTagsAsync(EmitPlan plan)
    {
        var gadget = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        var status = gadget.ErrorMap.Statuses.Single(static entry => entry.StatusCode == 400);
        await Assert.That(status.Tags.Select(static tag => tag.TypeName))
            .IsEquivalentTo(["GadgetError"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Check_The_Reason_Of_A_Redundant_Alias()
    {
        var document = await IngestAsync(DuplicateTagScenario(duplicateName: "GadgetError_1"));
        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("gadget.get"),
            GadgetCuration(Alias("GadgetError_1", "GadgetError", reason: " "))));
        await Assert.That(exception.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Curation, "GadgetError_1", "schema alias curation must declare a reason"),
            new BindingError(BindingErrorCategory.Curation, "GadgetError_1", "schema alias is redundant: the stabilize-duplicate collapse already folds it into 'GadgetError'"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("Original", false)]
    [Arguments("Canonical", true)]
    [Arguments("Canonical#/properties/nested", true)]
    public async Task Bind_Should_Validate_Schema_Names_On_The_Collapsed_Graph(string key, bool survives)
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("Original", static schema => schema.Type("object").Property("nested", static nested =>
                nested.Type("object").Property("id", static id => id.Type("string"), required: true), required: true))
            .WithSchema("Canonical", static schema => schema.Type("object").Property("nested", static nested =>
                nested.Type("object").Property("id", static id => id.Type("string"), required: true), required: true))
            .WithOperation("widget.get", configure: static operation => operation.Response(200, "application/json", static schema => schema.Ref("Original")))));
        var curation = Curation(Groups("widget", RootGroup()), schemaAliases: [Alias("Original", "Canonical")],
            schemaNames: [SchemaName(key, "ReviewedName")]);

        if (survives)
        {
            var plan = new BindingTestHost().Bind(document, Selection("widget.get"), curation);
            await Assert.That(plan.Models.Select(static model => model.Name)).Contains("ReviewedName");
        }
        else
        {
            var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("widget.get"), curation));
            var error = exception.Errors.Single(static error => error.Subject == "Original");
            await Assert.That(error.Category).IsEqualTo(BindingErrorCategory.Curation);
            await Assert.That(error.Problem).IsEqualTo("curated schema does not exist in the spec");
        }
    }

    /// <summary>Effect's projection of <c>TemplateLiteral(["rpc.", String])</c>.</summary>
    private const string RpcPrefixPattern = "^rpc\\.[\\s\\S]*?$";

    /// <summary>A templated identifier: a prefix marker that discriminates nothing.</summary>
    private const string EventIdPrefixPattern = "^evt_[\\s\\S]*?$";

    private static readonly string[] ExpectedErrorTypeNames =
    [
        "AgentNotFoundError",
        "CommandExecutionError",
        "CommandNotFoundError",
        "ConflictError",
        "FileNotFoundError",
        "ForbiddenError",
        "FormAlreadySettledError",
        "FormInvalidAnswerError",
        "FormNotFoundError",
        "InstructionEntryValueTooLargeError",
        "IntegrationAttemptNotFoundError",
        "IntegrationMethodNotFoundError",
        "IntegrationNotFoundError",
        "InvalidCursorError",
        "InvalidRequestError",
        "LocationNotFoundError",
        "McpServerNotFoundError",
        "MessageNotFoundError",
        "PermissionNotFoundError",
        "PermissionSavedNotFoundError",
        "ProjectNotFoundError",
        "ProviderNotFoundError",
        "PtyNotFoundError",
        "RpcError",
        "RpcInternalError",
        "ServiceUnavailableError",
        "SessionBusyError",
        "SessionNotFoundError",
        "ShellNotFoundError",
        "SkillNotFoundError",
        "UnauthorizedError",
        "UnknownError",
        "VcsInitNotSupportedError",
        "WorktreeError",
    ];

    [Test]
    public async Task Bind_Should_Hoist_The_Durable_Envelope_Of_The_Pinned_Session_Log()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();

        var durable = plan.Unions.Single(static union => union.Name == "ISessionEventDurable");
        // Member/type membership is the contract; declaration order is not asserted here.
        await Assert
            .That(durable.HoistedMembers.Select(static member => member.Name))
            .IsEquivalentTo(["Id", "Created", "Metadata", "Durable", "Location"], CollectionOrdering.Any);
        var envelope = plan.HoistedInterfaces.Single(static carrier => carrier.Name == "IDurableEnvelope");
        // Member/type membership is the contract; declaration order is not asserted here.
        await Assert.That(envelope.Members.Select(static member => member.Name)).IsEquivalentTo(["AggregateId", "Seq", "Version"], CollectionOrdering.Any);
        await Assert.That(envelope.ImplementedBy).Contains("SessionCreatedDurable");
        await Assert.That(envelope.ImplementedBy).Contains("SessionDeletedDurable");
        // The worktree envelope has the same shape but belongs to no union that hoists it.
        await Assert.That(envelope.ImplementedBy).DoesNotContain("WorktreeResolvedDurable");

        // The sync marker sits outside the durable union, so the outer union promises its tag alone.
        await Assert.That(plan.Unions.Single(static union => union.Name == "ISessionLogItem").HoistedMembers).IsEmpty();
        // The nested compaction union inherits 'Id' and 'Metadata' from the message union, its
        // fixed 'type' marker and its own 'status' marker dispatch, and 'reason' binds to a
        // different generated enum per arm — so only the promoted time carrier is left to hoist.
        // Member/type membership is the contract; declaration order is not asserted here.
        await Assert
            .That(plan.Unions.Single(static union => union.Name == "ISessionMessageCompaction").HoistedMembers
                .Select(static member => member.Name))
            .IsEquivalentTo(["Time"], CollectionOrdering.Any);
    }

    [Test]
    public async Task Bind_Should_Create_The_Selected_Pinned_Emit_Plan()
    {
        var (document, selection, curation) = await LoadPinnedInputsAsync();
        var selected = document.Operations.Where(operation => selection.OperationIds.Contains(operation.OperationId, StringComparer.Ordinal)).ToArray();
        var namingErrors = new BindingErrorCollector();
        var reachable = ReachableSchemaCollector.Collect(document, selected, namingErrors);
        var names = SchemaNameResolver.Resolve(document, reachable, selected, curation, namingErrors);

        // A lost name must fail here before its description rows can refuse later binding.
        await Assert.That(names["op:experimental.mcp.add#/requestBody/content/application~1json/schema/properties/config"]).IsEqualTo("IMcpServerConfig");
        await Assert.That(names["Mcp.Server#/properties/status"]).IsEqualTo("IMcpStatus");
        await Assert.That(names["Session.Message.Assistant.Tool#/properties/state"]).IsEqualTo("ISessionMessageToolState");

        var plan = new BindingTestHost().Bind(document, selection, curation);

        await Assert.That(plan.SelectedOperationIds).IsEquivalentTo(selection.OperationIds, StringComparer.Ordinal, CollectionOrdering.Matching);
        // Every operation the pinned profile leaves unselected is declined or transport-owned by
        // decision, so the pinned plan carries no pending operations at all.
        await Assert.That(plan.PendingOperations).IsEmpty();
        await Assert.That(plan.Models).Contains(static model => model.Name == "PromptFileSourceUri");
        await Assert.That(plan.Models).Contains(static model => model.Name == "ConfigInfo");

        var promptFile = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "PromptFileSourceUri");
        var promptUri = promptFile.Properties.Single(static property => property.WireName == "uri").Type;
        await Assert.That(promptUri).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)promptUri).Name).IsEqualTo("string");

        var toolFile = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "ToolFileContent");
        var toolUri = toolFile.Properties.Single(static property => property.WireName == "uri").Type;
        await Assert.That(toolUri).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)toolUri).Name).IsEqualTo("string");

        var sessionMessage = plan.Unions.Single(static union => union.Name == "ISessionMessageInfo");
        await Assert.That(sessionMessage.MarkerWireName).IsEqualTo("type");
        await Assert.That(sessionMessage.Variants).Count().IsEqualTo(11);

        var compaction = plan.Unions.Single(static union => union.Name == "ISessionMessageCompaction");
        await Assert.That(compaction.MarkerWireName).IsEqualTo("status");
        await Assert.That(compaction.BaseTypeName).IsEqualTo("ISessionMessageInfo");
        await Assert.That(compaction.FixedMarker!.WireName).IsEqualTo("type");
        await Assert.That(compaction.FixedMarker.Value).IsEqualTo("compaction");
        await Assert.That(compaction.Variants).Count().IsEqualTo(3);

        await Assert.That(plan.Unions).Contains(static union => union.Name == "ISessionMessageAssistantContent");
        // Each row preserves a shipped interface and its unknown carrier independently.
        await Assert.That(plan.Unions).Contains(static union => union.Name == "IMcpServerConfig");
        await Assert.That(plan.Unions.Single(static union => union.Name == "IMcpServerConfig").UnknownTypeName).IsEqualTo("UnknownMcpServerConfig");
        await Assert.That(plan.Unions).Contains(static union => union.Name == "IMcpStatus");
        await Assert.That(plan.Unions.Single(static union => union.Name == "IMcpStatus").UnknownTypeName).IsEqualTo("UnknownMcpStatus");
        await Assert.That(plan.Unions).Contains(static union => union.Name == "ISessionMessageToolState");
        await Assert.That(plan.Unions.Single(static union => union.Name == "ISessionMessageToolState").UnknownTypeName)
            .IsEqualTo("UnknownSessionMessageToolState");
        await Assert.That(plan.Unions).Contains(static union => union.Name == "IPromptFileSource");
        await Assert.That(plan.Unions).Contains(static union => union.Name == "IToolContent");

        var errors = plan.Unions.Single(static union => union.Name == "IOpenCodeError");
        await Assert.That(errors.Variants.Select(static variant => variant.TypeName).Order(StringComparer.Ordinal))
            .IsEquivalentTo(ExpectedErrorTypeNames, StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Carry_The_Pinned_Global_Event_Stream_Without_Request_Parameters()
    {
        var (document, selection, curation) = await LoadPinnedInputsAsync();

        var plan = new BindingTestHost().Bind(document, selection, curation);

        var operation = plan.Clients.Single(static client => client.Name == "EventsClient").Operations.Single();
        await Assert.That(operation.MethodName).IsEqualTo("SubscribeAsync");
        await Assert.That(operation.HttpMethod).IsEqualTo("get");
        await Assert.That(operation.RouteTemplate).IsEqualTo("/api/event");
        await Assert.That(operation.RouteMemberName).IsEqualTo("Subscribe");
        await Assert.That(operation.Parameters).IsEmpty();
        await Assert.That(operation.QueryRequest).IsNull();
        await Assert.That(operation.RequestBody).IsNull();
        await Assert.That(operation.Stream!.PayloadTypeName).IsEqualTo("IEvent");
        await Assert.That(operation.ErrorMap.Statuses.Select(static status => status.StatusCode).Order())
            .IsEquivalentTo([400, 401], CollectionOrdering.Matching);

        var sessionCreated = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "SessionCreated");
        await Assert.That(sessionCreated.ImplementedUnionNames.Order(StringComparer.Ordinal))
            .IsEquivalentTo(["IEvent", "ISessionEventDurable"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(plan.Models).DoesNotContain(static model => model.Name.StartsWith("Form", StringComparison.Ordinal)
            && model.Name.Length > 0 && model.Name[^1] is '1');
        await Assert.That(plan.Unions).DoesNotContain(static union => union.Name.StartsWith("IForm", StringComparison.Ordinal)
            && union.Name.Length > 0 && union.Name[^1] is '1');
        await Assert.That(plan.Models).DoesNotContain(static model => model.Name == "TuiCommandExecuteDataCommand0");
        await Assert.That(plan.Registry.TypeNames).DoesNotContain(static name => name.Contains("V2", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Collapse_The_Current_Inbox_Duplicate_Schemas()
    {
        var (document, selection, curation) = await LoadPinnedInputsAsync();

        var plan = new BindingTestHost().Bind(document, selection, curation);

        await Assert.That(plan.Models).Contains(static model => model.Name == "SessionInboxUserPayload");
        await Assert.That(plan.Models).Contains(static model => model.Name == "SessionInboxSyntheticPayload");
        await Assert.That(plan.Models).DoesNotContain(static model => model.Name == "SessionInboxUserPayload1");
        await Assert.That(plan.Models).DoesNotContain(static model => model.Name == "SessionInboxSyntheticPayload1");
        await Assert.That(plan.Registry.TypeNames).Contains("SessionInboxUserPayload", StringComparer.Ordinal);
        await Assert.That(plan.Registry.TypeNames).Contains("SessionInboxSyntheticPayload", StringComparer.Ordinal);

        var user = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "SessionInboxItemUser");
        var synthetic = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "SessionInboxItemSynthetic");
        var userPayload = (NamedTypeReferencePlan)user.Properties.Single(static property => property.WireName == "payload").Type;
        var syntheticPayload = (NamedTypeReferencePlan)synthetic.Properties.Single(static property => property.WireName == "payload").Type;
        await Assert.That(userPayload.Name).IsEqualTo("SessionInboxUserPayload");
        await Assert.That(syntheticPayload.Name).IsEqualTo("SessionInboxSyntheticPayload");
    }

    /// <summary>
    /// A member that references a named primitive alias reads the alias's description, and a
    /// reference to a stabilize duplicate that folds onto an undescribed base keeps the duplicate's.
    /// </summary>
    [Test]
    public async Task Bind_Should_Carry_The_Description_Of_A_Referenced_Or_Folded_Primitive_Alias()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("Item.ID", schema => schema.Type("string").Pattern("^itm").Description("The item identifier"))
            .WithSchema("Owner.ID", schema => schema.Type("string").Pattern("^own"))
            .WithSchema("Owner.ID_1", schema => schema.Type("string").Pattern("^own").Description("Owner to hand the item to"))
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Ref("Item.ID"), required: true)
                .Property("owner", property => property.Ref("Owner.ID_1"), required: true)
                .Property("creator", property => property.Ref("Owner.ID"), required: true))
            .WithOperation("widget.item", path: "/api/widget/item", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("ItemInfo")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.item"),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null))));

        var item = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "ItemInfo");
        await Assert.That(item.Properties.Single(static property => property.WireName == "id").Description)
            .IsEqualTo("The item identifier");
        await Assert.That(item.Properties.Single(static property => property.WireName == "owner").Description)
            .IsEqualTo("Owner to hand the item to");
        await Assert.That(item.Properties.Single(static property => property.WireName == "creator").Description).IsNull();
    }

    [Test]
    public async Task Bind_Should_Map_Required_And_Nullable_Independently()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true)
                .Property("note", property => property.Type("string"))
                .Property("flushedAt", property => property.AnyOf(
                    branch => branch.Type("number"),
                    branch => branch.Type("null")), required: true)
                .Property("extra", property => property.Unrestricted()))
            .WithOperation("widget.item", path: "/api/widget/item", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("ItemInfo")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.item"),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null))));

        var item = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "ItemInfo");
        var id = item.Properties.Single(static property => property.WireName == "id");
        await Assert.That(id.IsRequired).IsTrue();
        await Assert.That(id.Type.IsNullable).IsFalse();
        var note = item.Properties.Single(static property => property.WireName == "note");
        await Assert.That(note.IsRequired).IsFalse();
        await Assert.That(note.Type.IsNullable).IsTrue();
        var flushedAt = item.Properties.Single(static property => property.WireName == "flushedAt");
        await Assert.That(flushedAt.IsRequired).IsTrue();
        await Assert.That(flushedAt.Type.IsNullable).IsTrue();
        var extra = item.Properties.Single(static property => property.WireName == "extra");
        await Assert.That(extra.IsRequired).IsFalse();
        await Assert.That(extra.Type.IsNullable).IsTrue();
    }

    /// <summary>
    /// The tri-state wrapper is keyed on one thing only: whether a selected request body reaches
    /// the declaring schema. Optional-but-not-nullable, required-nullable, and response-only
    /// members all stay as they were. <c>NestedPatch</c> is the regression guard for the walk
    /// itself — the response pass reaches it first, so a shared visited set would leave its
    /// members unmarked.
    /// </summary>
    [Test]
    public async Task Bind_Should_Key_The_Tristate_Wrapper_On_Request_Reachability()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("NestedPatch", schema => schema
                .Type("object")
                .Property("depth", property => property.AnyOf(
                    branch => branch.Type("string"),
                    branch => branch.Type("null"))))
            .WithSchema("SharedPatch", schema => schema
                .Type("object")
                .Property("note", property => property.AnyOf(
                    branch => branch.Type("string"),
                    branch => branch.Type("null")))
                .Property("label", property => property.Type("string"))
                .Property("stamp", property => property.AnyOf(
                    branch => branch.Type("string"),
                    branch => branch.Type("null")), required: true)
                .Property("nested", property => property.Ref("NestedPatch"), required: true))
            .WithSchema("Widget.PatchPayload", schema => schema
                .Type("object")
                .Property("shared", property => property.Ref("SharedPatch"), required: true))
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true)
                .Property("memo", property => property.AnyOf(
                    branch => branch.Type("string"),
                    branch => branch.Type("null")))
                .Property("shared", property => property.Ref("SharedPatch"), required: true))
            .WithOperation("widget.item", path: "/api/widget/item", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("ItemInfo")))
            .WithOperation("widget.patch", method: "post", path: "/api/widget/patch", configure: operation => operation
                .RequestBody("application/json", body => body.Ref("Widget.PatchPayload"), required: true)
                .Response(200, "application/json", schema => schema.Ref("ItemInfo")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.item", "widget.patch"),
            Curation(
                Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)),
                operationNames: [OperationName("widget.patch", "PatchWidgetAsync")]));

        var shared = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "SharedPatch");
        await Assert.That(Member(shared, "note").EmitsOptionalWrapper).IsTrue();
        await Assert.That(Member(shared, "label").EmitsOptionalWrapper).IsFalse();
        await Assert.That(Member(shared, "stamp").EmitsOptionalWrapper).IsFalse();

        var nested = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "NestedPatch");
        await Assert.That(Member(nested, "depth").EmitsOptionalWrapper).IsTrue();

        var item = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "ItemInfo");
        await Assert.That(Member(item, "memo").EmitsOptionalWrapper).IsFalse();
        await Assert.That(Member(item, "memo").Type.IsNullable).IsTrue();
    }

    private static ModelPropertyPlan Member(ObjectModelPlan model, string wireName) =>
        model.Properties.Single(property => string.Equals(property.WireName, wireName, StringComparison.Ordinal));

    [Test]
    public async Task Bind_Should_Make_The_Optional_Pinned_Session_Parent_Nullable()
    {
        var (document, selection, curation) = await LoadPinnedInputsAsync();

        var plan = new BindingTestHost().Bind(document, selection, curation);

        var session = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "SessionInfo");
        var parent = session.Properties.Single(static property => property.WireName == "parentID");
        await Assert.That(parent.IsRequired).IsFalse();
        await Assert.That(parent.Type.IsNullable).IsTrue();
    }

    [Test]
    public async Task Bind_Should_Preserve_The_Pinned_Special_Number_Semantics()
    {
        var (document, selection, curation) = await LoadPinnedInputsAsync();

        var plan = new BindingTestHost().Bind(document, selection, curation);

        var shell = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "SessionMessageShell");
        var exit = shell.Properties.Single(static property => property.WireName == "exit");
        await Assert.That(exit.Type).IsTypeOf<SpecialNumberTypeReferencePlan>();
        await Assert.That(exit.Type.IsNullable).IsTrue();
    }

    [Test]
    public async Task Bind_Should_Be_Deterministic_For_The_Selected_Pin()
    {
        var (document, selection, curation) = await LoadPinnedInputsAsync();
        var host = new BindingTestHost();

        var first = host.Bind(document, selection, curation);
        var second = host.Bind(document, selection, curation);

        await Assert.That(first.Models.Select(static model => model.Name))
            .IsEquivalentTo(second.Models.Select(static model => model.Name), StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(first.Unions.Select(static union => union.Name))
            .IsEquivalentTo(second.Unions.Select(static union => union.Name), StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(first.Registry.TypeNames).IsEquivalentTo(second.Registry.TypeNames, StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(first.Clients.Select(static client => client.Name))
            .IsEquivalentTo(second.Clients.Select(static client => client.Name), StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(first.Clients.SelectMany(static client => client.Operations.Select(static operation => operation.MethodName)))
            .IsEquivalentTo(second.Clients.SelectMany(static client => client.Operations.Select(static operation => operation.MethodName)), StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Report_Selection_And_Curation_Errors_Together()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("health.get")));
        var selection = Selection("health.get", "missing.get");
        var curation = Curation(
            new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
            {
                ["health"] = RootGroup(),
            },
            envelopePayloadNames: [EnvelopePayloadName("orphan.get", "Orphan")]);

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(document, selection, curation));

        await Assert.That(exception.Errors).Contains(new BindingError(BindingErrorCategory.Selection,
            "missing.get", "selected operation does not exist in the ingested spec"));
        await Assert.That(exception.Errors).Contains(new BindingError(BindingErrorCategory.Curation,
            "orphan.get", "curated operation does not exist in the spec"));
    }

    [Test]
    public async Task Bind_Should_Apply_A_Reasoned_Operation_Name()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("EventReady", schema => schema
                .Type("object")
                .Property("ready", property => property.Type("boolean"), required: true))
            .WithOperation("event.subscribe", path: "/api/event", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("EventReady")))));
        var curation = Curation(
            Groups("event", ClientGroup(clientName: "Events", handleName: null, handleParameter: null)),
            operationNames: [OperationName("event.subscribe", "SubscribeAsync")]);

        var plan = new BindingTestHost().Bind(document, Selection("event.subscribe"), curation);

        var operation = plan.Clients.Single(static client => client.Name == "EventsClient").Operations.Single();
        await Assert.That(operation.MethodName).IsEqualTo("SubscribeAsync");
        await Assert.That(operation.RouteContainerName).IsEqualTo("Events");
        await Assert.That(operation.RouteMemberName).IsEqualTo("Subscribe");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Group_Without_A_Reason()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("health.get")));
        var curation = Curation(Groups("health", RootGroup() with { Reason = " " }));

        var exception = Assert.Throws<BindingException>(
            () => _ = new BindingTestHost().Bind(document, Selection("health.get"), curation));

        var error = exception.Errors.Single(static error => error.Problem.Contains("reason", StringComparison.Ordinal));
        await Assert.That(error.Subject).IsEqualTo("health");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Bind_Should_Strip_The_Encoded_Projection_Artifact_From_Derived_Names(bool unreachableThing)
    {
        // An unsuffixed component that no selected operation reaches must not toggle the strip.
        var document = await IngestAsync(SpecScenario.Define(spec =>
        {
            _ = spec
                .WithSchema("ThingEncoded", schema => schema
                    .Type("object")
                    .Property("value", property => property.Type("string"), required: true))
                .WithOperation("health.get", configure: operation => operation
                    .Response(200, "application/json", schema => schema.Ref("ThingEncoded")));
            if (unreachableThing)
            {
                _ = spec.WithSchema("Thing", schema => schema
                    .Type("object")
                    .Property("other", property => property.Type("string"), required: true));
            }
        }));
        var plan = BindHealthRows(document);

        await AssertThingAsync(plan);
        await Assert.That(plan.Models).DoesNotContain(static model => model.Name == "ThingEncoded");
    }

    [Test]
    public async Task Bind_Should_Derive_An_Operation_Scoped_Name_From_Its_Identity()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.update", method: "put", path: "/api/widget", configure: operation => operation
                .RequestBody("application/json", body => body
                    .Type("object")
                    .Property("size", property => property
                        .Type("object")
                        .Property("cols", cols => cols.Type("integer"), required: true)
                        .Property("rows", rows => rows.Type("integer"), required: true), required: true), required: true)
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))));
        var curation = Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)));

        var plan = new BindingTestHost().Bind(document, Selection("widget.update"), curation);

        // Inline members derive their names from the current group/action identity.
        await Assert.That(plan.Models).Contains(static model => model.Name == "WidgetUpdateSize");
        await Assert.That(plan.Registry.TypeNames).DoesNotContain(static name => name.Contains("V2", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Keep_A_Component_Whose_Final_Segment_Is_Only_The_Suffix()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("Foo.Encoded", schema => schema
                .Type("object")
                .Property("value", property => property.Type("string"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Foo.Encoded")))));

        var plan = BindHealthRows(document);

        await Assert.That(plan.Models).Contains(static model => model.Name == "FooEncoded");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Reachable_Encoded_Pair_Until_A_Row_Names_One_Side()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("Thing", schema => schema
                .Type("object")
                .Property("value", property => property.Type("string"), required: true))
            .WithSchema("ThingEncoded", schema => schema
                .Type("object")
                .Property("raw", property => property.Type("string"), required: true))
            .WithSchema("Pair", schema => schema
                .Type("object")
                .Property("decoded", property => property.Ref("Thing"), required: true)
                .Property("encoded", property => property.Ref("ThingEncoded"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Pair")))));

        var exception = Assert.Throws<BindingException>(() => BindHealthRows(document));
        var plan = new BindingTestHost().Bind(document, Selection("health.get"),
            Curation(Groups("health", RootGroup()), schemaNames: [SchemaName("ThingEncoded", "ThingEncoded")]));

        await Assert.That(exception.Errors)
            .Contains(new BindingError(BindingErrorCategory.Naming, "ThingEncoded", "C# type name 'Thing' collides with schema 'Thing'"));
        await AssertThingAsync(plan);
        await Assert.That(plan.Models).Contains(static model => model.Name == "ThingEncoded");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Selected_Operation_With_A_Header_Parameter()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("health.get", configure: operation => operation
                .Parameter("x-opencode-ticket", "header", schema => schema.Type("string"), required: true))));
        var curation = Curation(Groups("health", RootGroup()));

        var exception = Assert.Throws<BindingException>(
            () => _ = new BindingTestHost().Bind(document, Selection("health.get"), curation));

        var error = exception.Errors.Single(static error => error.Problem.Contains("header parameter", StringComparison.Ordinal));
        await Assert.That(error.Problem).Contains("x-opencode-ticket");
        await Assert.That(error.Problem).Contains("no runtime channel");
    }

    [Test]
    public async Task Bind_Should_Materialize_A_Base64_String_As_Bytes()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("Snapshot", schema => schema
                .Type("object")
                .Property(
                    "checkpoint",
                    property => property.Type("string").Format("byte").Raw("contentEncoding", "\"base64\""),
                    required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Snapshot")))));
        var curation = Curation(Groups("health", RootGroup()));

        var plan = new BindingTestHost().Bind(document, Selection("health.get"), curation);

        var checkpoint = plan.Models
            .OfType<ObjectModelPlan>()
            .Single(static model => model.Name == "Snapshot")
            .Properties
            .Single(static property => property.WireName == "checkpoint");
        await Assert.That(TypeReferenceNamePolicy.Format(checkpoint.Type)).IsEqualTo("ReadOnlyMemory<byte>");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Content_Encoding_Other_Than_Base64()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("Snapshot", schema => schema
                .Type("object")
                .Property("checkpoint", property => property
                    .Type("string")
                    .Format("byte")
                    .Raw("contentEncoding", "\"base32\""), required: true)
                .AdditionalPropertiesFalse())
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Snapshot")))));
        var curation = Curation(Groups("health", RootGroup()));

        var exception = Assert.Throws<BindingException>(
            () => _ = new BindingTestHost().Bind(document, Selection("health.get"), curation));

        var error = exception.Errors.Single(static error => error.Problem.Contains("content encoding", StringComparison.Ordinal));
        await Assert.That(error.Problem).Contains("base32");
    }

    [Test]
    public async Task Bind_Should_Refuse_Orphaned_And_Duplicated_Operation_Name_Rows()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("health.get")
            .WithOperation("event.subscribe", path: "/api/event")));
        var curation = Curation(
            Groups("health", RootGroup()),
            operationNames:
            [
                OperationName("event.subscribe", "SubscribeAsync"),
                OperationName("health.get", "HealthAsync", reason: " "),
                OperationName("health.get", "OtherHealthAsync"),
                OperationName("missing.get", "MissingAsync"),
            ]);

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            curation));

        await Assert.That(exception.Errors).Contains(static error => error.Subject == "event.subscribe"
            && error.Problem.Contains("not selected", StringComparison.Ordinal));
        await Assert.That(exception.Errors).Contains(static error => error.Subject == "health.get"
            && error.Problem.Contains("duplicated", StringComparison.Ordinal));
        await Assert.That(exception.Errors).Contains(static error => error.Subject == "health.get"
            && error.Problem.Contains("declare a reason", StringComparison.Ordinal));
        await Assert.That(exception.Errors).Contains(static error => error.Subject == "missing.get"
            && error.Problem.Contains("does not exist", StringComparison.Ordinal));
    }

    [Test]
    [Arguments(false, "ListWidgetsAsync", true, "widget.list", BindingErrorCategory.Curation, "operationNames row for 'widget.list' repeats the list naming rule's derived value 'ListWidgetsAsync'")]
    [Arguments(true, "ListAsync", true, "widget.list", BindingErrorCategory.Curation, "operationNames row for 'widget.list' repeats the list naming rule's derived value 'ListAsync'")]
    [Arguments(false, "ListEntriesAsync", false, "widget.list", BindingErrorCategory.Curation, null)]
    [Arguments(true, "ListEntriesAsync", false, "widget.list", BindingErrorCategory.Curation, null)]
    [Arguments(false, "ReviewedAsync", true, "missing.get", BindingErrorCategory.Curation, "curated operation does not exist in the spec")]
    [Arguments(false, "ReviewedAsync", true, "widget.other", BindingErrorCategory.Curation, "curated operation is not selected by the current profile")]
    [Arguments(false, "bad-nameAsync", true, "widget.list", BindingErrorCategory.Naming, "method name 'bad-nameAsync' must be a valid C# identifier ending in 'Async'")]
    [Arguments(false, "", true, "widget.run", BindingErrorCategory.Operation, "operation 'widget.run' closes with 'run', which is not a naming verb, and no operationNames row names it; the HTTP method is never a name source (ADR-0008)")]
    public async Task Bind_Should_Refuse_Invalid_Operation_Name_Rows(bool handle, string methodName, bool refused,
        string operationId, BindingErrorCategory category, string? problem)
    {
        ArgumentNullException.ThrowIfNull(methodName);
        var document = await IngestAsync(SpecScenario.Define(spec =>
        {
            if (operationId is "widget.other" or "widget.run")
            {
                spec.WithOperation(operationId, method: operationId == "widget.run" ? "post" : "get",
                    configure: static operation => operation.WithoutResponse(200).Response(204));
            }
            spec.WithOperation("widget.list", path: handle ? "/api/widget/{id}" : "/api/widget", configure: operation =>
            {
                operation.WithoutResponse(200).Response(204);
                if (handle)
                {
                    operation.Parameter("id", "path", schema => schema.Type("string"), required: true);
                }
            });
        }));
        var curation = Curation(
            Groups("widget", ClientGroup("Widgets", handle ? "WidgetClient" : null, handle ? "id" : null)),
            operationNames: methodName.Length == 0 ? [] : [OperationName(operationId, methodName)]);

        if (refused)
        {
            var selected = operationId == "widget.run" ? operationId : "widget.list";
            var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(document, Selection(selected), curation));
            var error = operationId == "widget.list" && category == BindingErrorCategory.Curation ? exception.Errors.Single()
                : exception.Errors.Single(error => error.Category == category && error.Subject == operationId);
            await Assert.That(error).IsEqualTo(new BindingError(category, operationId, problem!));
        }
        else
        {
            var plan = new BindingTestHost().Bind(document, Selection("widget.list"), curation);
            await Assert.That(plan.Clients.SelectMany(static client => client.Operations).Single().MethodName).IsEqualTo(methodName);
        }
    }

    [Test]
    [Arguments("Reviewed", "method name 'Reviewed' must be a valid C# identifier ending in 'Async'")]
    [Arguments("Async", "method name 'Async' must be a valid C# identifier ending in 'Async'")]
    public async Task Bind_Should_Report_Only_The_Invalid_Curated_Method_Name(string methodName, string problem)
    {
        var document = await IngestAsync(SpecScenario.Define(static spec => spec.WithOperation("widget.list",
            configure: static operation => operation.WithoutResponse(200).Response(204))));
        var curation = Curation(Groups("widget", ClientGroup("Widgets", null, null)),
            operationNames: [OperationName("widget.list", methodName)]);
        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("widget.list"), curation));
        await Assert.That(exception.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, "widget.list", problem),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Refuse_Curated_Operation_Name_Collisions()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("EventReady", schema => schema
                .Type("object")
                .Property("ready", property => property.Type("boolean"), required: true))
            .WithOperation("event.subscribe", path: "/api/event", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("EventReady")))
            .WithOperation("event.watch", path: "/api/event/watch", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("EventReady")))));
        var curation = Curation(
            Groups("event", ClientGroup(clientName: "Events", handleName: null, handleParameter: null)),
            operationNames:
            [
                OperationName("event.subscribe", "SubscribeAsync"),
                OperationName("event.watch", "SubscribeAsync"),
            ]);

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("event.subscribe", "event.watch"),
            curation));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
            && error.Problem.Contains("multiple members", StringComparison.Ordinal));
        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
            && error.Problem.Contains("route member", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_Orphaned_And_Duplicated_Schema_Name_Rows()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("Item", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("item.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Item")))));
        var curation = Curation(
            Groups("item", RootGroup()),
            schemaNames:
            [
                SchemaName("Item", "ReviewedItem", reason: " "),
                SchemaName("Item", "OtherItem"),
                SchemaName("Missing", "MissingItem"),
            ]);

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("item.get"),
            curation));

        await Assert.That(exception.Errors).Contains(static error => error.Subject == "Item"
            && error.Problem.Contains("duplicated", StringComparison.Ordinal));
        await Assert.That(exception.Errors).Contains(static error => error.Subject == "Item"
            && error.Problem.Contains("declare a reason", StringComparison.Ordinal));
        await Assert.That(exception.Errors).Contains(static error => error.Subject == "Missing"
            && error.Problem.Contains("does not exist", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_Named_Object_With_Schema_Valued_Additional_Properties()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("HybridInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true)
                .AdditionalProperties(value => value.Type("string")))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("HybridInfo")))));

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()))));

        var error = exception.Errors.Single(static candidate =>
            candidate is { Category: BindingErrorCategory.Schema, Subject: "HybridInfo/additionalProperties" });
        await Assert.That(error.Problem).Contains("named properties and typed additional properties");
        await Assert.That(error.Problem).Contains("without data loss");
    }

    /// <summary>
    /// The one open shape the binder admits beside named properties: a bag whose value schema
    /// is unrestricted (or unrestricted-or-null) rides as the extension-data member, so no wire
    /// member is lost and the declared members keep their types.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Bind_Should_Carry_An_Unrestricted_Bag_As_Extension_Data(bool valuesAdmitNull)
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("HybridInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true)
                .AllOf(rest => rest.Type("object").AdditionalProperties(value =>
                {
                    if (valuesAdmitNull)
                    {
                        _ = value.AnyOf(any => any.Unrestricted(), nothing => nothing.Type("null"));
                    }
                    else
                    {
                        _ = value.Unrestricted();
                    }
                })))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("HybridInfo")))));

        var plan = new BindingTestHost().Bind(document, Selection("health.get"),
            Curation(Groups("health", RootGroup()), redactedOpenMembers: [OpenMembers("HybridInfo", redact: false)]));

        var hybrid = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "HybridInfo");
        await Assert.That(hybrid.EmitsExtensionData).IsTrue();
        // Member/type membership is the contract; declaration order is not asserted here.
        await Assert.That(hybrid.Properties.Select(static property => property.Name)).IsEquivalentTo(["Id"], CollectionOrdering.Any);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Named_Property_That_Takes_The_Extension_Data_Members_Name()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("HybridInfo", schema => schema
                .Type("object")
                .Property("additionalProperties", property => property.Type("string"))
                .AllOf(rest => rest.Type("object").AdditionalProperties(value => value.Unrestricted())))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("HybridInfo")))));

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()))));

        var error = exception.Errors.Single(static candidate => candidate is { Category: BindingErrorCategory.Naming, Subject: "HybridInfo" });
        await Assert.That(error.Problem).Contains("AdditionalProperties");
        await Assert.That(error.Problem).Contains("extension-data");
    }

    [Test]
    public async Task Bind_Should_Refuse_Operation_Curation_For_Pending_Operation()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("health.get")
            .WithOperation("session.message", path: "/api/session/message", configure: operation => operation
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    .Property("data", property => property.Type("string"), required: true)))));
        EnvelopePayloadNameCuration[] envelopeNames = [EnvelopePayloadName("session.message", "Message")];

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()), envelopePayloadNames: envelopeNames)));

        await Assert.That(exception.Errors.Single(static error => error.Category == BindingErrorCategory.Curation)).IsEqualTo(new BindingError(BindingErrorCategory.Curation,
            "session.message", "curated operation is not selected by the current profile"));
    }

    [Test]
    public async Task Bind_Should_Collapse_Duplicate_References_Into_Semantic_Type_Plans()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("Shared", schema => schema
                .Type("object")
                .Property("value", property => property.Type("string"), required: true))
            .WithSchema("Container", schema => schema
                .Type("object")
                .Property("first", property => property.Ref("Shared"), required: true)
                .Property("items", property => property.Type("array").Items(item => item.Ref("Shared")), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Container")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
            {
                ["health"] = RootGroup(),
            }));

        await Assert.That(plan.Models.Count(static model => model.Name == "Shared")).IsEqualTo(1);
        var container = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Container");
        var items = container.Properties.Single(static property => property.WireName == "items").Type;
        await Assert.That(items).IsTypeOf<ListTypeReferencePlan>();
        await Assert.That(((ListTypeReferencePlan)items).ElementType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)((ListTypeReferencePlan)items).ElementType).Name).IsEqualTo("Shared");
    }

    [Test]
    public async Task Bind_Should_Bind_Nested_Marked_Union_With_The_Discriminating_Marker()
    {
        var document = await IngestAsync(MarkedNestedUnionScenario(schema => schema.AnyOf(
            one => one.Ref("WrapOne"), two => two.Ref("WrapTwo"))));

        var plan = BindHealth(document);

        var outer = plan.Unions.Single(static union => union.Name == "IOuter");
        await AssertMarkedTagsAsync(outer, "type", ["alpha", "wrap"]);

        var nested = plan.Unions.Single(static union => union.Name == "IWrap");
        await Assert.That(nested.MarkerWireName).IsEqualTo("status");
        await Assert.That(nested.BaseTypeName).IsEqualTo("IOuter");
        await Assert.That(nested.FixedMarker!.WireName).IsEqualTo("type");
        await Assert.That(nested.FixedMarker.Value).IsEqualTo("wrap");
    }

    [Test]
    public async Task Bind_Should_Refuse_Union_When_Branches_Share_No_Discriminating_Marker()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("First", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("same"), required: true))
            .WithSchema("Second", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("same"), required: true))
            .WithSchema("Twin", schema => schema.AnyOf(first => first.Ref("First"), second => second.Ref("Second")))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Twin")))));

        var exception = RefuseHealth(document);

        await Assert
            .That(exception.Errors.Single(static error => error.Category == BindingErrorCategory.Schema).Problem)
            .Contains("no discriminating marker");
    }

    [Test]
    public async Task Bind_Should_Refuse_HandleName_Without_HandleParameter()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("session.message", path: "/api/session/{sessionID}/message", configure: operation => operation
                .Parameter("sessionID", "path", schema => schema.Type("string"), required: true)
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    .Property("value", property => property.Type("string"), required: true)))));
        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["session"] = ClientGroup(handleParameter: null),
        };

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("session.message"),
            Curation(groups)));

        await Assert
            .That(exception.Errors.Single(static error => error.Category == BindingErrorCategory.Curation).Problem)
            .Contains("together");
    }

    [Test]
    public async Task Bind_Should_Refuse_HandleParameter_Without_HandleName()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("session.message", path: "/api/session/{sessionID}/message", configure: operation => operation
                .Parameter("sessionID", "path", schema => schema.Type("string"), required: true)
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    .Property("value", property => property.Type("string"), required: true)))));
        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["session"] = ClientGroup(handleName: null),
        };

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("session.message"),
            Curation(groups)));

        await Assert
            .That(exception.Errors.Single(static error => error.Category == BindingErrorCategory.Curation).Problem)
            .Contains("together");
    }

    [Test]
    public async Task Bind_Should_Refuse_HandleParameter_On_Root_Group()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec.WithOperation("health.get")));
        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["health"] = new GroupCuration
            {
                Placement = GroupPlacement.Root,
                HandleParameter = "sessionID",
            },
        };

        var exception = RefuseHealthGroups(document, groups);

        await AssertCurationProblemAsync(exception, "root group cannot declare");
    }

    [Test]
    public async Task Bind_Should_Require_HandleParameter_To_Name_A_Selected_Required_Path_Parameter()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("session.message", path: "/api/session/message/{messageID}", configure: operation => operation
                .Parameter("messageID", "path", schema => schema.Type("string"), required: true)
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    .Property("value", property => property.Type("string"), required: true)))));
        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["session"] = ClientGroup(),
        };

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("session.message"),
            Curation(groups)));

        await Assert
            .That(exception.Errors.Single(static error => error.Category == BindingErrorCategory.Curation).Problem)
            .Contains("required path parameter");
    }

    [Test]
    public async Task Bind_Should_Require_Curation_For_Every_Selected_Group()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec.WithOperation("health.get")));

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(new Dictionary<string, GroupCuration>(StringComparer.Ordinal))));

        await Assert
            .That(exception.Errors.Single(static error => error.Category == BindingErrorCategory.Curation).Problem)
            .Contains("no curation row");
    }

    [Test]
    public async Task Bind_Should_Use_Uri_Only_When_OpenApi_Declares_The_Format()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("Resource", schema => schema
                .Type("object")
                .Property("formatted", property => property.Type("string").Format("uri"), required: true)
                .Property("namedUri", property => property.Type("string"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Resource")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup())));

        var resource = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Resource");
        var formatted = (NamedTypeReferencePlan)resource.Properties.Single(static property => property.WireName == "formatted").Type;
        var namedUri = (NamedTypeReferencePlan)resource.Properties.Single(static property => property.WireName == "namedUri").Type;
        await Assert.That(formatted.Name).IsEqualTo("Uri");
        await Assert.That(namedUri.Name).IsEqualTo("string");
    }

    [Test]
    public async Task Bind_Should_Bind_A_Numeric_Literal_Like_Its_Primitive()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("VersionedItem", schema => schema
                .Type("object")
                .Property("version", property => property.Type("number").Raw("enum", "[3]"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("VersionedItem")))));

        var plan = BindHealth(document);

        var item = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "VersionedItem");
        var version = item.Properties.Single(static property => property.WireName == "version").Type;
        await Assert.That(version).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)version).Name).IsEqualTo("double");
    }

    [Test]
    public async Task Bind_Should_Bind_A_Nested_Marked_OneOf_Union()
    {
        var document = await IngestAsync(MarkedNestedUnionScenario(schema => schema.OneOf(
            one => one.Ref("WrapOne"), two => two.Ref("WrapTwo"))));

        var plan = BindHealth(document);

        var wrap = plan.Unions.Single(static union => union.Name == "IWrap");
        await Assert.That(wrap.MarkerWireName).IsEqualTo("status");
        await Assert.That(wrap.BaseTypeName).IsEqualTo("IOuter");
    }

    [Test]
    public async Task Bind_Should_Bind_An_Open_Enum_As_A_Generated_Extensible_Enum()
    {
        var plan = BindHealth(await IngestAsync(SpecScenario.Define(static spec => spec
            .WithSchema("Item.State", static schema => schema.AnyOf(
                static branch => branch.Type("string").Enum("removed", "pending"),
                static branch => branch.Type("string")))
            .WithSchema("ItemInfo", static schema => schema
                .Type("object")
                .Property("state", static property => property.Ref("Item.State"), required: true))
            .WithOperation("health.get", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("ItemInfo"))))));

        var state = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "ItemInfo")
            .Properties.Single(static property => property.WireName == "state").Type;
        await Assert.That(((NamedTypeReferencePlan)state).Name).IsEqualTo("ItemState");
        await Assert.That(plan.Models.OfType<EnumModelPlan>().Single(static model => model.Name == "ItemState")
                .Values.Select(static value => value.WireValue))
            .IsEquivalentTo(["removed", "pending"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Collapse_A_Union_Of_Refinements_Over_One_Primitive()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("digest", property => property.AnyOf(
                    branch => branch.Type("string").Raw("pattern", "\"^[a-f0-9]{64}$\""),
                    branch => branch.Type("string").Raw("pattern", "\"^[0-9]+$\"")), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("ItemInfo")))));

        var plan = BindHealth(document);

        var item = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "ItemInfo");
        var digest = item.Properties.Single(static property => property.WireName == "digest").Type;
        await Assert.That(digest).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)digest).Name).IsEqualTo("string");
    }

    [Test]
    public async Task Bind_Should_Create_A_Token_Dispatched_Structural_Union_Carrier()
    {
        var document = await IngestAsync(new StructuralUnionScenario());

        var plan = new BindingTestHost().Bind(
            document,
            Selection(StructuralUnionScenario.OperationId),
            Curation(Groups(StructuralUnionScenario.GroupName, RootGroup())));

        var structural = plan.Models.OfType<StructuralUnionModelPlan>().Single();
        await Assert.That(structural.Name).IsEqualTo("StructuralValue");
        await Assert.That(structural.KindTypeName).IsEqualTo("StructuralValueKind");
        await Assert.That(structural.Arms.Select(static arm => arm.Name))
            .IsEquivalentTo(["Text", "Number", "Boolean", "TextList"], StringComparer.Ordinal, CollectionOrdering.Matching);
        // Dictionary/set membership is the contract; enumeration order is not.
        await Assert
            .That(structural.Arms.Single(static arm => arm.Name == "Text").Tokens)
            .IsEquivalentTo([JsonTokenType.String], CollectionOrdering.Any);
        // Dictionary/set membership is the contract; enumeration order is not.
        await Assert
            .That(structural.Arms.Single(static arm => arm.Name == "Number").Tokens)
            .IsEquivalentTo([JsonTokenType.Number], CollectionOrdering.Any);
        // Dictionary/set membership is the contract; enumeration order is not.
        await Assert
            .That(structural.Arms.Single(static arm => arm.Name == "Boolean").Tokens)
            .IsEquivalentTo([JsonTokenType.True, JsonTokenType.False], CollectionOrdering.Any);
        // Dictionary/set membership is the contract; enumeration order is not.
        await Assert
            .That(structural.Arms.Single(static arm => arm.Name == "TextList").Tokens)
            .IsEquivalentTo([JsonTokenType.StartArray], CollectionOrdering.Any);
        await Assert
            .That(structural.Arms.Single(static arm => arm.Name == "Number").Type)
            .IsTypeOf<NamedTypeReferencePlan>();
        await Assert
            .That(((NamedTypeReferencePlan)structural.Arms.Single(static arm => arm.Name == "Number").Type).Name)
            .IsEqualTo("double");
        await Assert.That(plan.Registry.TypeNames).Contains("StructuralValue");
        await Assert.That(plan.Registry.TypeNames).DoesNotContain("StructuralValueKind");
        await Assert.That(plan.Registry.TypeNames).DoesNotContain("string");
        await Assert.That(plan.Registry.TypeNames).DoesNotContain("double");
        await Assert.That(plan.Registry.TypeNames).DoesNotContain("bool");
        await Assert.That(plan.Registry.TypeNames).Contains("IReadOnlyList<string>");
    }

    [Test]
    public async Task Bind_Should_Give_Object_Arms_Sharing_A_Token_A_First_Match_Claim()
    {
        var document = await IngestAsync(new FirstMatchUnionScenario());

        var plan = new BindingTestHost().Bind(
            document,
            Selection(FirstMatchUnionScenario.OperationId),
            Curation(Groups(FirstMatchUnionScenario.GroupName, RootGroup())));

        var unions = plan.Models.OfType<StructuralUnionModelPlan>().ToDictionary(static union => union.Name, StringComparer.Ordinal);
        await Assert.That(Describe(unions["SourceEntry"])).IsEqualTo("Text[String] | GitSource[StartObject]{repository} | LocalSource[StartObject]{path}");
        await Assert.That(Describe(unions["ToolEntry"])).IsEqualTo("ToolOff[StartObject]{disabled; disabled=true} | ToolServer[StartObject]{command}");
        await Assert
            .That(Describe(unions["JobStatus"]))
            .IsEqualTo("JobSettled[StartObject]{status; status=required|completed} | JobRunning[StartObject]{status, progress; status=running} | JobFailed[StartObject]{status, error; status=error}");
        return;

        static string Describe(StructuralUnionModelPlan union) => string.Join(" | ", union.Arms.Select(static arm =>
        {
            var tokens = string.Join(", ", arm.Tokens);
            if (arm.Claim is not { } claim)
            {
                return $"{arm.Name}[{tokens}]";
            }

            var sentinels = claim.Sentinels.Select(static sentinel => $"{sentinel.Property}={string.Join('|', sentinel.Values)}").ToArray();
            var keys = string.Join(", ", claim.RequiredKeys);
            return sentinels.Length is 0
                ? $"{arm.Name}[{tokens}]{{{keys}}}"
                : $"{arm.Name}[{tokens}]{{{keys}; {string.Join("; ", sentinels)}}}";
        }));
    }

    [Test]
    public async Task Bind_Should_Leave_A_Lone_Object_Arm_Without_A_Claim()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("Settings", schema => schema
                .Type("object")
                .Property("name", property => property.Type("string"), required: true))
            .WithSchema("Choice", schema => schema.AnyOf(
                branch => branch.Type("boolean"),
                branch => branch.Ref("Settings")))
            .WithSchema("Container", schema => schema
                .Type("object")
                .Property("choice", property => property.Ref("Choice"), required: true))
            .WithOperation("choice.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Container")))));

        var plan = new BindingTestHost().Bind(document, Selection("choice.get"), Curation(Groups("choice", RootGroup())));

        var union = plan.Models.OfType<StructuralUnionModelPlan>().Single();
        await Assert.That(union.Arms).All().Satisfy(arm => arm.Claim, value => value.IsNull());
    }

    [Test]
    public async Task Bind_Should_Refuse_A_First_Match_Object_Arm_An_Earlier_Arm_Always_Claims()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("First", schema => schema
                .Type("object")
                .Property("first", property => property.Type("string"), required: true))
            .WithSchema("Second", schema => schema
                .Type("object")
                .Property("first", property => property.Type("string"), required: true)
                .Property("second", property => property.Type("string"), required: true))
            .WithSchema("Choice", schema => schema.AnyOf(
                branch => branch.Ref("First"),
                branch => branch.Ref("Second")))
            .WithSchema("Container", schema => schema
                .Type("object")
                .Property("choice", property => property.Ref("Choice"), required: true))
            .WithOperation("choice.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Container")))));

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("choice.get"),
            Curation(Groups("choice", RootGroup()))));

        var problems = string.Join(Environment.NewLine, exception.Errors.Select(static error => $"{error.Subject}: {error.Problem}"));
        await Assert.That(problems).Contains("Choice: structural union branch 1 is unreachable: branch 0 claims every object it claims");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Object_Arm_Beside_An_Open_Object()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("First", schema => schema
                .Type("object")
                .Property("first", property => property.Type("string"), required: true))
            .WithSchema("Choice", schema => schema.AnyOf(
                branch => branch.Ref("First"),
                branch => branch.Type("object").AdditionalProperties(value => value.Type("string"))))
            .WithSchema("Container", schema => schema
                .Type("object")
                .Property("choice", property => property.Ref("Choice"), required: true))
            .WithOperation("choice.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Container")))));

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("choice.get"),
            Curation(Groups("choice", RootGroup()))));

        var problems = string.Join(Environment.NewLine, exception.Errors.Select(static error => $"{error.Subject}: {error.Problem}"));
        await Assert.That(problems).Contains("Choice: structural union branch 1 overlaps earlier branch token kind(s): StartObject");
    }

    [Test]
    public async Task Bind_Should_Require_Text_To_Precede_A_Structural_Special_Number()
    {
        var document = await IngestAsync(ChoiceScenario(spec => spec
            .WithSchema("Choice", schema => schema.AnyOf(
                branch => branch.AnyOf(SpecialNumberArms()),
                branch => branch.Type("boolean")))));

        await AssertChoiceRefusalAsync(document, "requires an earlier text branch");
    }

    [Test]
    public async Task Bind_Should_Count_Resolved_Never_Branches_As_Uninhabitable()
    {
        var document = await IngestAsync(ChoiceScenario(spec => spec
            .WithSchema("Impossible", schema => schema.Raw("not", "{}"))
            .WithSchema("Choice", schema => schema.AnyOf(
                branch => branch.Ref("Impossible"),
                branch => branch.Type("string")))));

        await AssertChoiceRefusalAsync(document, "at least two inhabitable");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Structural_Arm_Colliding_With_Carrier_Members()
    {
        var document = await IngestAsync(ChoiceScenario(spec => spec
            .WithSchema("Kind", schema => schema
                .Type("object")
                .Property("value", property => property.Type("string"), required: true))
            .WithSchema("Choice", schema => schema.AnyOf(
                branch => branch.Type("string"),
                branch => branch.Ref("Kind")))));

        await AssertChoiceRefusalAsync(document, "reserved carrier member");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Structural_Union_Arm_That_Is_A_Base64_String()
    {
        var document = await IngestAsync(ChoiceScenario(spec => spec
            .WithSchema("Choice", schema => schema.AnyOf(
                branch => branch.Type("string").Format("byte").Raw("contentEncoding", "\"base64\""),
                branch => branch.Type("boolean")))));

        await AssertChoiceRefusalAsync(document, "binary arms are not supported");
    }

    [Test]
    public async Task Bind_Should_Read_An_Object_Or_Array_Union_As_JsonElement()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true)
                .Property("payload", property => property.AnyOf(
                    branch => branch.Type("object"),
                    branch => branch.Type("array")), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("ItemInfo")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
            {
                ["health"] = RootGroup(),
            }));

        var item = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "ItemInfo");
        var data = item.Properties.Single(static property => property.WireName == "payload").Type;
        await Assert.That(data).IsEqualTo(new NamedTypeReferencePlan
        {
            Name = "JsonElement",
            IsNullable = false,
            JsonNullRepresentation = JsonNullRepresentation.InBand,
        });
    }

    [Test]
    public async Task Bind_Should_Dispatch_A_Marker_Spanning_Nested_Union_Through_Its_Own_Leaves()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("Created", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("created"), required: true))
            .WithSchema("Renamed", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("renamed"), required: true))
            .WithSchema("Synced", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("synced"), required: true))
            .WithSchema("Durable", schema => schema.OneOf(one => one.Ref("Created"), two => two.Ref("Renamed")))
            .WithSchema("LogItem", schema => schema.AnyOf(one => one.Ref("Durable"), two => two.Ref("Synced")))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("LogItem")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
            {
                ["health"] = RootGroup(),
            }));

        // The nested union discriminates on the parent's own marker, so the parent dispatches
        // straight to its leaves rather than handing the payload to a second converter.
        var outer = plan.Unions.Single(static union => union.Name == "ILogItem");
        await Assert.That(outer.Variants.Select(static variant => variant.Tag).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["created", "renamed", "synced"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(outer.Variants.Select(static variant => variant.TypeName).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["Created", "Renamed", "Synced"], StringComparer.Ordinal, CollectionOrdering.Matching);

        // The grouping survives as an interface the leaves implement.
        var nested = plan.Unions.Single(static union => union.Name == "IDurable");
        await Assert.That(nested.BaseTypeName).IsEqualTo("ILogItem");
        await Assert.That(nested.FixedMarker).IsNull();

        var created = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Created");
        await Assert.That(created.ImplementedUnionNames).IsEquivalentTo(["IDurable"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Let_A_Schema_Belong_To_Every_Union_That_Branches_To_It()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("Alpha", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("alpha"), required: true))
            .WithSchema("Beta", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("beta"), required: true))
            .WithSchema("Shared", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("shared"), required: true))
            .WithSchema("Durable", schema => schema.AnyOf(one => one.Ref("Alpha"), two => two.Ref("Shared")))
            .WithSchema("Live", schema => schema.AnyOf(one => one.Ref("Beta"), two => two.Ref("Shared")))
            .WithSchema("Feed", schema => schema
                .Type("object")
                .Property("durable", property => property.Ref("Durable"), required: true)
                .Property("live", property => property.Ref("Live"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Feed")))));

        var plan = BindHealth(document);

        var shared = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Shared");
        await Assert.That(shared.ImplementedUnionNames.Order(StringComparer.Ordinal))
            .IsEquivalentTo(["IDurable", "ILive"], StringComparer.Ordinal, CollectionOrdering.Matching);

        var alpha = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Alpha");
        await Assert.That(alpha.ImplementedUnionNames).IsEquivalentTo(["IDurable"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Bind_Should_Refuse_A_Tag_Owned_By_Two_Closure_Schemas(bool responseCollision)
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("GoneError", schema => schema
                .Type("object")
                .Property("_tag", property => property.Type("string").Enum("SharedError"), required: true)
                .Property("message", property => property.Type("string"), required: true))
            .WithSchema("LostError", schema => schema
                .Type("object")
                .Property("_tag", property => property.Type("string").Enum("SharedError"), required: true)
                .Property("detail", property => property.Type("string"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("ItemInfo"))
                .Response(404, "application/json", schema => schema.Ref("GoneError"))
                .Response(410, "application/json", schema => schema.Ref("LostError")))));

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("health.get"),
            Curation(Groups("health", RootGroup()), schemaNames: responseCollision ? [SchemaName("ItemInfo", "HealthResponse")] : [])));
        BindingError[] expected = [
            new(BindingErrorCategory.Schema, "OpenCodeError", "multiple error schemas declare tag 'SharedError' (GoneError, LostError)"),
        ];
        if (responseCollision)
        {
            expected = [.. expected, new(BindingErrorCategory.Naming, "health.get", "multiple operations map to response type 'HealthResponse'")];
        }
        await Assert.That(exception.Errors).IsEquivalentTo(expected, CollectionOrdering.Matching)
            .Because(string.Join(" | ", exception.Errors));
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Error_Response_In_The_Name_Data_Dialect()
    {
        // The generator admits '_tag'-tagged errors only; a pin that brings the {name, data}
        // dialect back refuses by name instead of emitting a converter that cannot read it.
        var document = await IngestAsync(MixedErrorStyleScenario(
            static schema => schema
                .Type("object")
                .Property("name", property => property.Type("string").Enum("WorkError"), required: true)
                .Property(
                    "data",
                    property => property
                        .Type("object")
                        .Property("message", inner => inner.Type("string"), required: true),
                    required: true)));

        var exception = RefuseHealth(document);

        await Assert.That(exception.Errors).Contains(static error =>
                error.Problem.Contains("error responses must reference tagged error schemas", StringComparison.Ordinal)
                && error.Problem.Contains("WorkError", StringComparison.Ordinal)
                && error.Problem.Contains(
                    "the error carries a required 'name' literal and no '_tag' literal: the {name, data} error dialect is not admitted",
                    StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Unrecognized_Group_Placement()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    .Property("data", property => property.Type("string"), required: true)))));
        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["health"] = new GroupCuration
            {
                Placement = (GroupPlacement)7
            },
        };

        var exception = RefuseHealthGroups(document, groups);

        await AssertCurationProblemAsync(exception, "not a recognized group placement");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Unrecognized_Group_Emission()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    .Property("data", property => property.Type("string"), required: true)))));
        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["health"] = RootGroup() with
            {
                Emission = (EmissionMode)7,
            },
        };

        var exception = RefuseHealthGroups(document, groups);

        await AssertCurationProblemAsync(exception, "not a recognized group emission");
    }

    [Test]
    public async Task Bind_Should_Refuse_Internal_Raw_Emission_On_A_Root_Group()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec.WithOperation("health.get")));
        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["health"] = RootGroup() with
            {
                Emission = EmissionMode.InternalRaw,
            },
        };

        var exception = RefuseHealthGroups(document, groups);

        await AssertCurationProblemAsync(exception, "root group cannot declare internalRaw emission");
    }

    [Test]
    public async Task Bind_Should_Refuse_Internal_Raw_Emission_On_A_Group_Without_Selected_Operations()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("health.get")
            .WithOperation("pty.get", path: "/api/pty/{ptyID}", configure: operation => operation
                .Parameter("ptyID", "path", schema => schema.Type("string"), required: true))));
        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["health"] = RootGroup(),
            ["pty"] = ClientGroup(clientName: "Ptys", handleName: "PtyClient", handleParameter: "ptyID",
                emission: EmissionMode.InternalRaw),
        };

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(groups)));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Curation
                                                       && error.Subject == "pty"
                                                       && error.Problem.Contains(
                                                           "internalRaw emission requires at least one selected operation",
                                                           StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Accept_A_Matching_Transport_Owned_Fingerprint()
    {
        var plan = await BindTransportOwnedScenarioAsync();

        await Assert.That(plan.SelectedOperationIds.Single()).IsEqualTo("health.get");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Transport_Owned_Fingerprint_Mismatch()
    {
        var baseline = await IngestAsync(TransportOwnedScenario());
        var baselineOperation = baseline.Operations.Single(static candidate => candidate.OperationId == "pty.connect");
        var staleHash = TransportOwnedFingerprint.ComputeSha256(baselineOperation);

        var reshaped = await IngestAsync(TransportOwnedScenario(operation => operation
            .Parameter("newParam", "query", schema => schema.Type("string"))));

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            reshaped,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()), transportOwned: [TransportOwned("pty.connect", staleHash)])));

        await Assert.That(exception.Errors).Contains(error => error.Category == BindingErrorCategory.Curation
                                               && error.Subject == "pty.connect"
                                               && error.Problem.Contains("no longer matches", StringComparison.Ordinal)
                                               && error.Problem.Contains(staleHash, StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Transport_Owned_Row_For_An_Unknown_Operation()
    {
        var document = await IngestAsync(SpecScenario.Define(spec => spec.WithOperation("health.get")));

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()), transportOwned: [TransportOwned("pty.connect", new string('0', 64))])));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Curation
                                                       && error.Subject == "pty.connect"
                                                       && error.Problem.Contains("does not exist in the spec", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Transport_Owned_Row_Without_A_Reason()
    {
        var document = await IngestAsync(TransportOwnedScenario());
        var operation = document.Operations.Single(static candidate => candidate.OperationId == "pty.connect");
        var hash = TransportOwnedFingerprint.ComputeSha256(operation);

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()), transportOwned: [TransportOwned("pty.connect", hash, reason: " ")])));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Curation
                                                       && error.Subject == "pty.connect"
                                                       && error.Problem.Contains("must declare a reason", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Transport_Owned_Row_With_A_Malformed_Hash()
    {
        var document = await IngestAsync(TransportOwnedScenario());

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()), transportOwned: [TransportOwned("pty.connect", "not-a-hash")])));

        // A malformed hash must yield exactly one error: the hex-shape refusal, not a second
        // spurious "no longer matches" error from comparing the malformed value against a
        // freshly computed fingerprint.
        var errors = exception.Errors
            .Where(error => error.Category == BindingErrorCategory.Curation && error.Subject == "pty.connect")
            .ToArray();
        await Assert.That(errors.Length).IsEqualTo(1);
        await Assert.That(errors[0].Problem).Contains("64 lowercase hex", StringComparison.Ordinal);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Duplicated_Transport_Owned_Row()
    {
        var document = await IngestAsync(TransportOwnedScenario());
        var operation = document.Operations.Single(static candidate => candidate.OperationId == "pty.connect");
        var hash = TransportOwnedFingerprint.ComputeSha256(operation);

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(
                Groups("health", RootGroup()),
                transportOwned: [TransportOwned("pty.connect", hash), TransportOwned("pty.connect", hash)])));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Curation
                                                       && error.Subject == "pty.connect"
                                                       && error.Problem.Contains("duplicated", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Move_A_Transport_Owned_Operation_Out_Of_The_Pending_Set()
    {
        var plan = await BindTransportOwnedScenarioAsync();

        await Assert.That(plan.PendingOperations).IsEmpty();
        await Assert.That(plan.TransportOwnedOperationIds).IsEquivalentTo(["pty.connect"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Transport_Owned_Operation_That_Is_Also_Selected()
    {
        var document = await IngestAsync(TransportOwnedScenario());
        var operation = document.Operations.Single(static candidate => candidate.OperationId == "pty.connect");
        var hash = TransportOwnedFingerprint.ComputeSha256(operation);
        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["health"] = RootGroup(),
            ["pty"] = RootGroup(),
        };

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get", "pty.connect"),
            Curation(groups, transportOwned: [TransportOwned("pty.connect", hash)])));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Curation
                                                       && error.Subject == "pty.connect"
                                                       && error.Problem.Contains("transport-owned operation cannot be selected", StringComparison.Ordinal));
    }

    /// <summary>Binds the transport-owned scenario with a matching fingerprint row beside one
    /// selected root operation — the arrangement the accept and pending-set tests share.</summary>
    private static async Task<EmitPlan> BindTransportOwnedScenarioAsync()
    {
        var document = await IngestAsync(TransportOwnedScenario());
        var operation = document.Operations.Single(static candidate => candidate.OperationId == "pty.connect");
        var hash = TransportOwnedFingerprint.ComputeSha256(operation);

        return new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()), transportOwned: [TransportOwned("pty.connect", hash)]));
    }

    /// <summary>A miniature stand-in for <c>pty.connect</c>'s real shape: a path
    /// parameter plus four query parameters, WebSocket-marked, alongside a selected root operation
    /// so <see cref="OperationSelection"/> is never empty.</summary>
    private static SpecScenario TransportOwnedScenario(Action<OperationBuilder>? mutate = null) =>
        SpecScenario.Define(spec => spec
            .WithSchema("TransportOwnedScenarioHealth", schema => schema
                .Type("object")
                .Property("healthy", property => property.Type("boolean"), required: true))
            .WithOperation("pty.connect", path: "/api/pty/{ptyID}/connect", configure: operation =>
            {
                operation
                    .Parameter("ptyID", "path", schema => schema.Type("string"), required: true)
                    .Parameter("location[directory]", "query", schema => schema.Type("string"))
                    .Parameter("location[workspace]", "query", schema => schema.Type("string"))
                    .Parameter("cursor", "query", schema => schema.Type("string"))
                    .Parameter("ticket", "query", schema => schema.Type("string"))
                    .Extension("x-websocket", "true");
                mutate?.Invoke(operation);
            })
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("TransportOwnedScenarioHealth"))));

    [Test]
    public async Task Bind_Should_Move_A_Declined_Operation_Out_Of_The_Pending_Set()
    {
        var document = await IngestAsync(DeclinedScenario());

        var plan = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()), declined: [Declined("widget.tail")]));

        await Assert.That(plan.PendingOperations).IsEmpty();
        var declined = plan.DeclinedOperations.Single();
        await Assert.That(declined.OperationId).IsEqualTo("widget.tail");
        await Assert.That(declined.Reason).Contains("maintainer", StringComparison.Ordinal);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Declined_Operation_That_Is_Also_Selected()
    {
        var document = await IngestAsync(DeclinedScenario());

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()), declined: [Declined("health.get"), Declined("widget.tail")])));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Curation
                                                       && error.Subject == "health.get"
                                                       && error.Problem.Contains("declined operation cannot be selected", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Declined_Row_The_Spec_No_Longer_Declares()
    {
        // Staleness telltale: when upstream removes or renames the walled operation, the row has to
        // be retired consciously instead of standing over an operation nobody can check any more.
        var document = await IngestAsync(DeclinedScenario());

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()), declined: [Declined("widget.tail"), Declined("gadget.gone")])));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Curation
                                                       && error.Subject == "gadget.gone"
                                                       && error.Problem.Contains("does not exist in the spec", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Declined_Row_Without_A_Reason()
    {
        var document = await IngestAsync(DeclinedScenario());

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()), declined: [Declined("widget.tail", reason: " ")])));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Curation
                                                       && error.Subject == "widget.tail"
                                                       && error.Problem.Contains("must declare a reason", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Duplicated_Declined_Row()
    {
        var document = await IngestAsync(DeclinedScenario());

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup()), declined: [Declined("widget.tail"), Declined("widget.tail")])));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Curation
                                                       && error.Subject == "widget.tail"
                                                       && error.Problem.Contains("duplicated", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Operation_Declared_Both_Declined_And_Transport_Owned()
    {
        // Two admission states claiming one operation would double-count it in the marker and leave
        // the reader without a single answer to "why is this operation not generated?".
        var document = await IngestAsync(TransportOwnedScenario());
        var operation = document.Operations.Single(static candidate => candidate.OperationId == "pty.connect");
        var hash = TransportOwnedFingerprint.ComputeSha256(operation);

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(
                Groups("health", RootGroup()),
                transportOwned: [TransportOwned("pty.connect", hash)],
                declined: [Declined("pty.connect")])));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Curation
                                                       && error.Subject == "pty.connect"
                                                       && error.Problem.Contains("cannot also be transport-owned", StringComparison.Ordinal));
    }

    /// <summary>A walled operation (wildcard path, WebSocket-marked) beside one selected root
    /// operation — the only shape a decline is admitted over.</summary>
    private static SpecScenario DeclinedScenario() =>
        SpecScenario.Define(spec => spec
            .WithSchema("DeclinedScenarioHealth", schema => schema
                .Type("object")
                .Property("healthy", property => property.Type("boolean"), required: true))
            .WithOperation("widget.tail", path: "/api/widget/*", configure: operation => operation
                .Extension("x-websocket", "true"))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("DeclinedScenarioHealth"))));

    [Test]
    public async Task Ingest_Should_Refuse_A_Repeated_Path_Token()
    {
        var scenario = SpecScenario.Define(spec => spec
            .WithOperation("health.get", path: "/api/{id}/echo/{id}", configure: operation => operation
                .Parameter("id", "path", schema => schema.Type("string"), required: true)));

        var exception = await Assert
            .That(async () => _ = await IngestAsync(scenario))
            .Throws<IngestionException>();

        await Assert.That(exception!.Errors).Contains(static error =>
            error.Problem.Contains("must appear exactly once", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Collapse_A_Structurally_Identical_Schema_Alias()
    {
        var document = await IngestAsync(DuplicateTagScenario());

        var plan = new BindingTestHost().Bind(
            document,
            Selection("gadget.get"),
            GadgetCuration(Alias("GadgetError1", "GadgetError")));

        await AssertGadgetErrorTagsAsync(plan);
        await Assert.That(plan.Models).DoesNotContain(static model => model.Name == "GadgetError1");
        await Assert.That(plan.Unions.Single(static union => union.Name == "IOpenCodeError").Variants.Select(static variant => variant.TypeName))
            .IsEquivalentTo(["GadgetError"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Collapse_A_Stabilize_Duplicate_Without_A_Curation_Row()
    {
        var document = await IngestAsync(DuplicateTagScenario(duplicateName: "GadgetError_1"));

        var plan = new BindingTestHost().Bind(document, Selection("gadget.get"), GadgetCuration());

        await AssertGadgetErrorTagsAsync(plan);
        await Assert.That(plan.ImplicitAliases.Aliases["GadgetError_1"]).IsEqualTo("GadgetError");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Explicit_Alias_The_Stabilize_Collapse_Already_Implies()
    {
        var document = await IngestAsync(DuplicateTagScenario(duplicateName: "GadgetError_1"));

        await AssertAliasRefusalAsync(document, Alias("GadgetError_1", "GadgetError"), "redundant");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Explicit_Alias_Onto_A_Mechanically_Folded_Target()
    {
        var document = await IngestAsync(StabilizeAndCuratedDuplicateScenario());

        await AssertAliasRefusalAsync(
            document,
            Alias("GadgetErrorAlt", "GadgetError_1"),
            "cannot chain: the stabilize-duplicate collapse already folds the target 'GadgetError_1' into 'GadgetError'");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Explicit_Alias_Over_A_Mechanical_Fold_Base()
    {
        var document = await IngestAsync(StabilizeAndCuratedDuplicateScenario());

        await AssertAliasRefusalAsync(
            document,
            Alias("GadgetError", "GadgetErrorAlt"),
            "cannot chain: the stabilize-duplicate collapse already folds 'GadgetError_1' into 'GadgetError'");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Duplicate_Error_Tag_Without_An_Alias()
    {
        var document = await IngestAsync(DuplicateTagScenario());

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("gadget.get"),
            GadgetCuration()));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Operation
            && error.Problem.Contains("duplicate error tag", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Alias_Whose_Shapes_Differ()
    {
        var document = await IngestAsync(DuplicateTagScenario(duplicate => duplicate
            .Type("object")
            .Property("_tag", property => property.Type("string").Enum("GadgetError"), required: true)
            .Property("message", property => property.Type("string"), required: true)
            .Property("detail", property => property.Type("string"))));

        await AssertAliasRefusalAsync(document, Alias("GadgetError1", "GadgetError"), "structurally identical");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Alias_Whose_Formats_Differ()
    {
        var document = await IngestAsync(DuplicateTagScenario(static duplicate => duplicate
            .Type("object")
            .Property("_tag", property => property.Type("string").Enum("GadgetError"), required: true)
            .Property("message", property => property.Type("string").Format("uri"), required: true)));

        await AssertAliasRefusalAsync(document, Alias("GadgetError1", "GadgetError"), "structurally identical");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Alias_Whose_Schema_Is_Missing()
    {
        var document = await IngestAsync(DuplicateTagScenario());

        await AssertAliasRefusalAsync(document, Alias("GhostError", "GadgetError"), "does not exist", subject: "GhostError");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Alias_Whose_Target_Is_Missing()
    {
        var document = await IngestAsync(DuplicateTagScenario());

        await AssertAliasRefusalAsync(document, Alias("GadgetError1", "GhostError"), "does not exist");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Self_Alias()
    {
        var document = await IngestAsync(DuplicateTagScenario());

        await AssertAliasRefusalAsync(document, Alias("GadgetError1", "GadgetError1"), "itself");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Chained_Alias()
    {
        var document = await IngestAsync(SpecScenario.Define(spec =>
        {
            DefineDuplicateTagSpec(spec, DefaultDuplicate);
            _ = spec.WithSchema("GadgetError2", DefaultDuplicate);
        }));

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("gadget.get"),
            GadgetCuration(Alias("GadgetError2", "GadgetError1"), Alias("GadgetError1", "GadgetError"))));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Curation
            && error.Problem.Contains("chain", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Duplicated_Alias_Source()
    {
        var document = await IngestAsync(DuplicateTagScenario());

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("gadget.get"),
            GadgetCuration(Alias("GadgetError1", "GadgetError"), Alias("GadgetError1", "GadgetError"))));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Curation
            && error.Problem.Contains("duplicated", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Alias_Without_A_Reason()
    {
        var document = await IngestAsync(DuplicateTagScenario());

        await AssertAliasRefusalAsync(document, Alias("GadgetError1", "GadgetError", reason: " "), "reason");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Dormant_Alias()
    {
        var document = await IngestAsync(SpecScenario.Define(spec =>
        {
            DefineDuplicateTagSpec(spec, DefaultDuplicate);
            _ = spec
                .WithSchema("LonelyError", DefaultDuplicate)
                .WithSchema("LonelyError1", DefaultDuplicate);
        }));

        await AssertAliasRefusalAsync(
            document,
            Alias("LonelyError1", "LonelyError"),
            "not referenced",
            subject: "LonelyError1");
    }

    [Test]
    public async Task Bind_Should_Bind_A_Prefix_Tagged_Arm_Beside_Literal_Tags()
    {
        var document = await IngestAsync(PrefixUnionScenario());

        var plan = BindHealth(document);

        var events = await AssertPrefixEventsAsync(plan);
        await Assert.That(events.PrefixVariant).IsNotNull();
        await Assert.That(events.PrefixVariant!.TypeName).IsEqualTo("RpcEvent");
        await Assert.That(events.PrefixVariant.Prefix).IsEqualTo("rpc.");
        await Assert.That(events.PrefixVariant.MarkerWireName).IsEqualTo("type");

        var arm = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "RpcEvent");
        await Assert.That(arm.ImplementedUnionNames).Contains("IEvent");
        var type = arm.Properties.Single(static property => property.WireName == "type");
        await Assert.That(type.IsRequired).IsTrue();
        await Assert.That(type.IsLiteral).IsFalse();
    }

    /// <summary>
    /// A templated identifier discriminates nothing, so the branch that carries one beside its
    /// literal tag stays a variant; only a prefix on the discriminating property makes an arm.
    /// </summary>
    [Test]
    public async Task Bind_Should_Keep_A_Literal_Branch_Carrying_A_Templated_Id_As_A_Variant()
    {
        var document = await IngestAsync(PrefixUnionScenario(static spec => _ = spec
            .WithSchema("Created", static schema => schema
                .Type("object")
                .Property("type", static property => property.Type("string").Enum("created"), required: true)
                .Property("id", static property => property.Type("string").Pattern(EventIdPrefixPattern), required: true))));

        var plan = BindHealth(document);

        var events = await AssertPrefixEventsAsync(plan);
        await Assert.That(events.PrefixVariant!.TypeName).IsEqualTo("RpcEvent");

        var created = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Created");
        await Assert.That(created.ImplementedUnionNames).Contains("IEvent");
    }

    /// <summary>
    /// The nested wall reads the nested union's own discriminator, so leaves that agree on a
    /// templated identifier and differ by a literal tag are variants, not a buried arm.
    /// </summary>
    [Test]
    public async Task Bind_Should_Bind_A_Nested_Marked_Union_Whose_Leaves_Carry_A_Templated_Id()
    {
        var document = await IngestAsync(SpecScenario.Define(static spec => _ = spec
            .WithSchema("Alpha", static schema => schema
                .Type("object")
                .Property("type", static property => property.Type("string").Enum("alpha"), required: true))
            .WithSchema("WrapOne", static schema => WrapLeaf(schema, "one"))
            .WithSchema("WrapTwo", static schema => WrapLeaf(schema, "two"))
            .WithSchema("Wrap", static schema => schema.AnyOf(
                static one => one.Ref("WrapOne"),
                static two => two.Ref("WrapTwo")))
            .WithSchema("Outer", static schema => schema.AnyOf(
                static alpha => alpha.Ref("Alpha"),
                static wrap => wrap.Ref("Wrap")))
            .WithOperation("health.get", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("Outer")))));

        var plan = BindHealth(document);

        var outer = plan.Unions.Single(static union => union.Name == "IOuter");
        await Assert.That(outer.MarkerWireName).IsEqualTo("type");
        await Assert.That(outer.PrefixVariant).IsNull();

        var nested = plan.Unions.Single(static union => union.Name == "IWrap");
        await Assert.That(nested.MarkerWireName).IsEqualTo("status");
        await Assert.That(nested.PrefixVariant).IsNull();
        await Assert.That(nested.Variants.Select(static variant => variant.Tag).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["one", "two"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Admit_A_Literal_Tag_That_Is_A_Prefix_Of_The_Arm_Prefix()
    {
        var problems = await BindProblemsAsync(PrefixUnionScenario(static spec => _ = spec
            .WithSchema("Rpc", static schema => schema
                .Type("object")
                .Property("type", static property => property.Type("string").Enum("rpc"), required: true))
            .WithSchema("Event", static schema => schema.AnyOf(
                static branch => branch.Ref("Created"),
                static branch => branch.Ref("Rpc"),
                static branch => branch.Ref("RpcEvent")))));

        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task Bind_Should_Refuse_Two_Prefix_Tagged_Arms()
    {
        var problems = await BindProblemsAsync(PrefixUnionScenario(static spec => _ = spec
            .WithSchema("PluginEvent", static schema => schema
                .Type("object")
                .Property("type", static property => property.Type("string").Pattern("^plugin\\.[\\s\\S]*?$"), required: true))
            .WithSchema("Event", static schema => schema.AnyOf(
                static branch => branch.Ref("Created"),
                static branch => branch.Ref("RpcEvent"),
                static branch => branch.Ref("PluginEvent")))));

        await Assert
            .That(problems)
            .Contains("marked union declares more than one prefix-tagged arm ('PluginEvent', 'RpcEvent'); at most one is admitted");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Prefix_That_Covers_A_Literal_Tag()
    {
        var problems = await BindProblemsAsync(PrefixUnionScenario(static spec => _ = spec
            .WithSchema("RpcPing", static schema => schema
                .Type("object")
                .Property("type", static property => property.Type("string").Enum("rpc.ping"), required: true))
            .WithSchema("Event", static schema => schema.AnyOf(
                static branch => branch.Ref("Created"),
                static branch => branch.Ref("RpcPing"),
                static branch => branch.Ref("RpcEvent")))));

        await Assert.That(problems).Contains("prefix-tagged arm 'RpcEvent' prefix 'rpc.' is a prefix of literal tag 'rpc.ping' ('RpcPing')");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Prefix_Tagged_Arm_On_A_Non_Discriminating_Property()
    {
        var problems = await BindProblemsAsync(PrefixUnionScenario(static spec => _ = spec
            .WithSchema("RpcEvent", static schema => schema
                .Type("object")
                .Property("kind", static property => property.Type("string").Pattern(RpcPrefixPattern), required: true))));

        await Assert.That(problems).Contains("prefix-tagged arm 'RpcEvent' tags 'kind' but the union discriminates on 'type'");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Prefix_Tagged_Arm_On_A_Non_String_Marker()
    {
        var problems = await BindProblemsAsync(SpecScenario.Define(static spec => _ = spec
            .WithSchema("Accepted", static schema => schema
                .Type("object")
                .Property("tag", static property => property.Type("boolean").BooleanEnum(true), required: true))
            .WithSchema("Rejected", static schema => schema
                .Type("object")
                .Property("tag", static property => property.Type("boolean").BooleanEnum(false), required: true))
            .WithSchema("RpcEvent", static schema => schema
                .Type("object")
                .Property("tag", static property => property.Type("string").Pattern(RpcPrefixPattern), required: true))
            .WithSchema("Event", static schema => schema.AnyOf(
                static branch => branch.Ref("Accepted"),
                static branch => branch.Ref("Rejected"),
                static branch => branch.Ref("RpcEvent")))
            .WithOperation("health.get", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("Event")))));

        await Assert
            .That(problems)
            .Contains("prefix-tagged arm 'RpcEvent' requires a string marker; the union discriminates on a 'Boolean' marker");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Prefix_Tagged_Arm_Inside_A_Nested_Marked_Union()
    {
        var problems = await BindProblemsAsync(SpecScenario.Define(static spec => _ = spec
            .WithSchema("Alpha", static schema => schema
                .Type("object")
                .Property("type", static property => property.Type("string").Enum("alpha"), required: true))
            .WithSchema("WrapOne", static schema => schema
                .Type("object")
                .Property("type", static property => property.Type("string").Enum("wrap"), required: true)
                .Property("status", static property => property.Type("string").Enum("one"), required: true))
            .WithSchema("WrapRpc", static schema => schema
                .Type("object")
                .Property("type", static property => property.Type("string").Enum("wrap"), required: true)
                .Property("status", static property => property.Type("string").Pattern(RpcPrefixPattern), required: true))
            .WithSchema("Wrap", static schema => schema.AnyOf(
                static one => one.Ref("WrapOne"),
                static rpc => rpc.Ref("WrapRpc")))
            .WithSchema("Outer", static schema => schema.AnyOf(
                static alpha => alpha.Ref("Alpha"),
                static wrap => wrap.Ref("Wrap")))
            .WithOperation("health.get", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("Outer")))));

        await Assert
            .That(problems)
            .Contains("prefix-tagged arm 'WrapRpc' must be a direct component branch of the marked union, not a branch of nested union 'IWrap'");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Uninhabited_Prefix_Tagged_Arm()
    {
        var problems = await BindProblemsAsync(PrefixUnionScenario(static spec => _ = spec
            .WithSchema("RpcEvent", static schema => schema
                .Type("object")
                .Property("type", static property => property.Type("string").Pattern(RpcPrefixPattern), required: true)
                .Property("never", static property => property.Raw("not", "{}"), required: true))));

        await Assert.That(problems).Contains("prefix-tagged arm 'RpcEvent' admits no JSON value");
    }

    /// <summary>
    /// A marked union of two literal-tagged branches beside one branch tagged by an Effect
    /// template-literal prefix, so every wall below reads as a single delta from the admitted
    /// shape. <paramref name="extra"/> redeclares whichever component the wall varies.
    /// </summary>
    private static SpecScenario PrefixUnionScenario(Action<SpecDocumentBuilder>? extra = null) =>
        SpecScenario.Define(spec =>
        {
            _ = spec
                .WithSchema("Created", static schema => schema
                    .Type("object")
                    .Property("type", static property => property.Type("string").Enum("created"), required: true))
                .WithSchema("Deleted", static schema => schema
                    .Type("object")
                    .Property("type", static property => property.Type("string").Enum("deleted"), required: true))
                .WithSchema("RpcEvent", static schema => schema
                    .Type("object")
                    .Property("type", static property => property.Type("string").Pattern(RpcPrefixPattern), required: true)
                    .Property("data", static property => property.Type("object"), required: true))
                .WithSchema("Event", static schema => schema.AnyOf(
                    static branch => branch.Ref("Created"),
                    static branch => branch.Ref("Deleted"),
                    static branch => branch.Ref("RpcEvent")))
                .WithOperation("health.get", configure: static operation => operation
                    .Response(200, "application/json", static schema => schema.Ref("Event")));
            extra?.Invoke(spec);
        });

    /// <summary>A nested-union leaf that fixes the outer tag, carries a templated id, and
    /// discriminates on <c>status</c>.</summary>
    private static void WrapLeaf(SchemaBuilder schema, string status) => _ = schema
        .Type("object")
        .Property("type", static property => property.Type("string").Enum("wrap"), required: true)
        .Property("status", property => property.Type("string").Enum(status), required: true)
        .Property("id", static property => property.Type("string").Pattern(EventIdPrefixPattern), required: true);

    /// <summary>
    /// Binds a scenario and joins every refusal it raised, so a wall test asserts the exact
    /// problem text and the admitted shape asserts that binding raised nothing at all.
    /// </summary>
    private static async Task<string> BindProblemsAsync(SpecScenario scenario)
    {
        var document = await IngestAsync(scenario);
        try
        {
            _ = new BindingTestHost().Bind(
                document,
                Selection("health.get"),
                Curation(new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
                {
                    ["health"] = RootGroup(),
                }));
            return string.Empty;
        }
        catch (BindingException exception)
        {
            return string.Join('\n', exception.Errors.Select(static error => error.Problem));
        }
    }

    private static async Task AssertAliasRefusalAsync(SpecDocument document, SchemaAlias alias, string expectedProblem,
        string? subject = null)
    {
        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("gadget.get"),
            GadgetCuration(alias)));

        await Assert.That(exception.Errors).Contains(error => error.Category == BindingErrorCategory.Curation
                                                && string.Equals(error.Subject, subject ?? alias.Schema, StringComparison.Ordinal)
                                                && error.Problem.Contains(expectedProblem, StringComparison.Ordinal));
    }

    /// <summary>
    /// One Effect <c>_tag</c> error and one caller-varied second error schema in the same
    /// closure, so a test varies only the second schema's shape.
    /// </summary>
    private static SpecScenario MixedErrorStyleScenario(Action<SchemaBuilder> second) =>
        SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("GoneError", schema => schema
                .Type("object")
                .Property("_tag", property => property.Type("string").Enum("GoneError"), required: true)
                .Property("message", property => property.Type("string"), required: true))
            .WithSchema("WorkError", second)
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("ItemInfo"))
                .Response(400, "application/json", schema => schema.Ref("WorkError"))
                .Response(404, "application/json", schema => schema.Ref("GoneError"))));

    /// <summary>
    /// Three spellings of one error in the same closure: <c>GadgetError_1</c>, which the stabilize
    /// policy folds into <c>GadgetError</c> mechanically, and <c>GadgetErrorAlt</c>, which only a
    /// curated row can collapse — so a curated row can be pointed at either end of the fold.
    /// </summary>
    private static SpecScenario StabilizeAndCuratedDuplicateScenario() =>
        SpecScenario.Define(static spec => _ = spec
            .WithSchema("GadgetInfo", static schema => schema
                .Type("object")
                .Property("id", static property => property.Type("string"), required: true))
            .WithSchema("GadgetError", DefaultDuplicate)
            .WithSchema("GadgetError_1", DefaultDuplicate)
            .WithSchema("GadgetErrorAlt", DefaultDuplicate)
            .WithOperation("gadget.get", path: "/api/gadget", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("GadgetInfo"))
                .Response(400, "application/json", static schema => schema.AnyOf(
                    static branch => branch.Ref("GadgetError_1"),
                    static branch => branch.Ref("GadgetErrorAlt"),
                    static branch => branch.Ref("GadgetError")))));

    private static SpecScenario DuplicateTagScenario(Action<SchemaBuilder>? duplicate = null,
        string duplicateName = "GadgetError1") =>
        SpecScenario.Define(spec => DefineDuplicateTagSpec(spec, duplicate ?? DefaultDuplicate, duplicateName));

    private static void DefineDuplicateTagSpec(SpecDocumentBuilder spec, Action<SchemaBuilder> duplicate,
        string duplicateName = "GadgetError1") =>
        _ = spec
            .WithSchema("GadgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("GadgetError", DefaultDuplicate)
            .WithSchema(duplicateName, duplicate)
            .WithOperation("gadget.get", path: "/api/gadget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("GadgetInfo"))
                .Response(400, "application/json", schema => schema.AnyOf(
                    branch => branch.Ref(duplicateName),
                    static branch => branch.Ref("GadgetError"))));

    private static void DefaultDuplicate(SchemaBuilder schema) => schema
        .Type("object")
        .Property("_tag", property => property.Type("string").Enum("GadgetError"), required: true)
        .Property("message", property => property.Type("string"), required: true);

    private static GenerationCuration GadgetCuration(params SchemaAlias[] aliases) =>
        Curation(
            Groups("gadget", ClientGroup(clientName: "Gadgets", handleName: null, handleParameter: null)),
            schemaAliases: aliases);

    private static Task<(SpecDocument Document, OperationSelection Selection, GenerationCuration Curation)> LoadPinnedInputsAsync() =>
        BindingTestHost.LoadPinnedInputsAsync();

    private static async Task<SpecDocument> IngestAsync(SpecScenario scenario)
    {
        var context = scenario.Build();
        return await new SpecIngestion(context.FileSystem).IngestAsync(context.SpecPath, CancellationToken.None);
    }
}
