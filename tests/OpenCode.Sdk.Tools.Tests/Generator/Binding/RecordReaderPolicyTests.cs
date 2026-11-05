using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.UnionHoistPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class RecordReaderPolicyTests(PinnedBinding pinned)
{
    private static readonly string[] ExistingArmReaders = [
        "SessionMessageAgentSelected", "SessionMessageAssistant", "SessionMessageAssistantReasoning",
        "SessionMessageAssistantText", "SessionMessageAssistantTool", "SessionMessageIdle", "SessionMessageLocationSwitched",
        "SessionMessageModelSelected", "SessionMessageShell", "SessionMessageSkill", "SessionMessageSynthetic", "SessionMessageSystem",
        "SessionMessageToolStateCompleted", "SessionMessageToolStateError", "SessionMessageToolStateRunning", "SessionMessageToolStateStreaming", "SessionMessageUser",
        "ToolFileContent", "ToolTextContent",
    ];

    [Test]
    public async Task Select_Should_Validate_Every_Production_Call_Site_Against_The_Full_Pin()
    {
        var armNames = pinned.Plan.ReaderArms.Values.SelectMany(static names => names).ToHashSet(StringComparer.Ordinal);
        foreach (var record in RecordReaderPolicy.NestedCallSites.Keys)
        {
            await Assert.That(pinned.Plan.ReaderRecords.Keys).Contains(record);
        }
        var selected = RecordReaderPolicy.Select(pinned.Plan.Models, pinned.Plan.Unions, out _,
            callSites: RecordReaderPolicy.NestedCallSites);
        await Assert.That(selected.Keys.Where(name => !armNames.Contains(name))).IsEquivalentTo(ExpectedNestedReaders(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Report_A_Production_Call_Site_Refusal_Alongside_A_Selection_Error()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithObject("LocationRef", schema => schema.Property("directory", property => property.Type("string"), required: true))
            .WithObject("SessionTextDelta", schema => schema
                .Property("type", property => property.Type("string").Enum("session.text.delta"), required: true)
                .Property("location", property => property.Ref("LocationRef")))
            .WithObject("OtherEvent", schema => schema
                .Property("type", property => property.Type("string").Enum("other"), required: true))
            .WithSchema("Event", schema => schema.AnyOf(delta => delta.Ref("SessionTextDelta"), other => other.Ref("OtherEvent")))
            .WithJsonOperation("health.get", "/health", schema => schema.Ref("Event"))));
        var exception = await Assert.That(() => new BindingTestHost().Bind(document,
                BindingScenarioData.Selection("health.get", "missing.get"),
                BindingScenarioData.Curation(BindingScenarioData.Groups("health", BindingScenarioData.RootGroup()))))
            .Throws<BindingException>();
        await Assert.That(exception!.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Selection, "missing.get", "selected operation does not exist in the ingested spec"),
            new BindingError(BindingErrorCategory.Naming, "SessionTextDelta", "Nested reader call site 'SessionTextDelta.Data' must name a direct closed-record member of a selected record."),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Select_Should_Collect_Call_Site_Refusals_Alongside_Other_Binding_Errors()
    {
        var plan = RecordReaderPlanFixture.CreateNested();
        var errors = new BindingErrorCollector();
        errors.Add(BindingErrorCategory.Selection, "other", "another binding error");
        var sites = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["NestedRecord"] = new HashSet<string>(StringComparer.Ordinal) { "Missing" },
        };
        _ = RecordReaderPolicy.Select(plan.Models, plan.Unions, out _, RecordReaderPlanFixture.Rollout, sites, errors);
        await Assert.That(errors.Snapshot()).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Selection, "other", "another binding error"),
            new BindingError(BindingErrorCategory.Naming, "NestedRecord", "Nested reader call site 'NestedRecord.Missing' must name a direct closed-record member of a selected record."),
        ], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Select_Should_Reach_A_Depth_Two_Call_Site_In_Either_Order(bool reverse)
    {
        var plan = RecordReaderPlanFixture.CreateNested();
        var entries = new KeyValuePair<string, IReadOnlySet<string>>[]
        {
            new("NestedRecord", new HashSet<string>(StringComparer.Ordinal) { "Child" }),
            new("NestedValue", new HashSet<string>(StringComparer.Ordinal) { "Note" }),
        };
        var sites = (reverse ? entries.Reverse() : entries).ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
        var models = plan.Models.Select(model => model is ObjectModelPlan { Name: "NestedValue" } record
            ? record with { Properties = [Property("note", Named("ExampleItem", true), false)] } : model).ToArray();
        var selected = RecordReaderPolicy.Select(models, plan.Unions, out _, RecordReaderPlanFixture.Rollout, sites);
        await Assert.That(selected.Keys).IsEquivalentTo(["DenseRecord", "ExampleItem", "NestedRecord", "NestedValue", "SiblingRecord"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Select_Should_Refuse_An_Explicit_Unreached_Key()
    {
        var plan = RecordReaderPlanFixture.CreateNested();
        var sites = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["NestedValue"] = new HashSet<string>(StringComparer.Ordinal) { "Note" },
        };
        var exception = await Assert.That(() => RecordReaderPolicy.Select(plan.Models, plan.Unions, out _, RecordReaderPlanFixture.Rollout, sites))
            .Throws<BindingException>();
        await Assert.That(exception!.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, "NestedValue", "Nested reader call site 'NestedValue.Note' must name a direct closed-record member of a selected record."),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Select_Should_Drop_An_Unreached_Default_Key()
    {
        var plan = RecordReaderPlanFixture.CreateNested();
        var selected = RecordReaderPolicy.Select(plan.Models, plan.Unions, out _, RecordReaderPlanFixture.Rollout);
        await Assert.That(selected.Keys).IsEquivalentTo(["DenseRecord", "NestedRecord", "SiblingRecord"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Select_Should_Claim_Nested_Readers_Without_Adding_Union_Arms()
    {
        var plan = RecordReaderPlanFixture.CreateNested();
        await Assert.That(plan.ReaderRecords.Keys).IsEquivalentTo(["DenseRecord", "NestedRecord", "NestedValue", "SiblingRecord"], CollectionOrdering.Matching);
        await Assert.That(plan.ReaderArms["IToolContent"]).IsEquivalentTo(["DenseRecord", "NestedRecord", "SiblingRecord"], CollectionOrdering.Matching);
        var inventory = GeneratedTypeNameInventory.Create(new OwnershipCandidates { Models = plan.Models, Unions = plan.Unions, HoistedInterfaces = [] },
            [], [], new Dictionary<string, string>(StringComparer.Ordinal), plan.ReaderRecords);
        await Assert.That(inventory.Claims.Where(static claim => claim.Name == "NestedValueJsonReader").Select(static claim => claim.Subject))
            .IsEquivalentTo(["NestedValue"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("unselected")]
    [Arguments("scalar")]
    [Arguments("union")]
    [Arguments("open")]
    [Arguments("list")]
    [Arguments("dictionary")]
    [Arguments("optional")]
    public async Task Select_Should_Refuse_An_Invalid_Nested_Call_Site(string shape)
    {
        var plan = RecordReaderPlanFixture.CreateNested();
        var recordName = shape == "unselected" ? "CreatedEvent" : "NestedRecord";
        var memberName = shape switch { "missing" => "Missing", "unselected" => "Item", _ => "Child" };
        TypeReferencePlan type = shape switch
        {
            "scalar" => Named("string"),
            "union" => Named("IToolContent"),
            "list" => new ListTypeReferencePlan { ElementType = Named("NestedValue"), IsNullable = false, JsonNullRepresentation = JsonNullRepresentation.ClrNull },
            "dictionary" => new DictionaryTypeReferencePlan { ValueType = Named("NestedValue"), IsNullable = false, JsonNullRepresentation = JsonNullRepresentation.ClrNull },
            _ => Named("NestedValue"),
        };
        var models = plan.Models.Select(model => model switch
        {
            ObjectModelPlan { Name: "NestedRecord" } record => record with
            { Properties = [.. record.Properties.Select(property => property.Name == "Child" ? property with { Type = type, EmitsOptionalWrapper = shape == "optional" } : property)] },
            ObjectModelPlan { Name: "NestedValue" } value when shape == "open" => value with { EmitsExtensionData = true },
            _ => model,
        }).ToArray();
        var sites = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [recordName] = new HashSet<string>(StringComparer.Ordinal) { memberName },
        };
        var exception = await Assert.That(() => RecordReaderPolicy.Select(models, plan.Unions, out _, RecordReaderPlanFixture.Rollout, sites))
            .Throws<BindingException>();
        await Assert.That(exception!.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, recordName, $"Nested reader call site '{recordName}.{memberName}' must name a direct closed-record member of a selected record."),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Select_Should_Not_Convert_An_Unlisted_Variant_In_A_Rollout_Union()
    {
        var selected = RecordReaderPolicy.Select([Record("Unlisted"), Record("ToolFileContent")],
            [Union("IToolContent", Arm("Unlisted"), Arm("ToolFileContent"))], out var arms);
        await Assert.That(selected.Keys).IsEquivalentTo(["ToolFileContent"], CollectionOrdering.Matching);
        await Assert.That(arms["IToolContent"]).IsEquivalentTo(["ToolFileContent"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Carry_The_Selected_Readers_Into_Normal_Emission()
    {
        var sources = Tools.Generator.Emission.SourceEmitter.Emit(pinned.Plan);
        await Assert.That(pinned.Plan.ReaderRecords.Keys).IsEquivalentTo(ExpectedReaders(), CollectionOrdering.Matching);
        await Assert.That(pinned.Plan.ReaderArms.Where(static entry => entry.Value.Count > 0).Select(static entry => entry.Key))
            .IsEquivalentTo(["IEvent", "ISessionMessageAssistantContent", "ISessionMessageInfo", "ISessionMessageToolState", "IToolContent"], CollectionOrdering.Matching);
        await Assert.That(sources.Select(static source => source.RelativePath)
            .Where(static path => path.EndsWith("JsonReader.cs", StringComparison.Ordinal)))
            .IsEquivalentTo(ExpectedReaders().Select(name => pinned.Plan.Placement.HomeOf(name).Slice + "/Internal/" + name + "JsonReader.cs").Order(StringComparer.Ordinal), CollectionOrdering.Matching);
        await Assert.That(pinned.Plan.ReaderCallSites.OrderBy(static site => site.Key, StringComparer.Ordinal)
            .SelectMany(static site => site.Value.Order(StringComparer.Ordinal).Select(member => site.Key + "." + member)))
            .IsEquivalentTo(ExpectedCallSites(), CollectionOrdering.Matching);
        foreach (var name in new[] { "SessionEventDurable", "SessionLogItem" })
        {
            foreach (var arm in SharedArms())
            {
                await Assert.That(EmitterSnapshot.Content(sources, "Sessions/Internal/" + name + "JsonConverter.cs"))
                    .Contains(".Of<" + arm + ">()");
                await Assert.That(pinned.Plan.ReaderArms["I" + name]).DoesNotContain(arm);
            }
        }
    }

    [Test]
    public async Task Select_Should_Select_Exactly_The_Rollout_Arms_And_Nested_Records()
    {
        var selected = RecordReaderPolicy.Select(pinned.Plan.Models, pinned.Plan.Unions, out var arms);
        await Assert.That(selected.Keys).IsEquivalentTo(ExpectedReaders(), CollectionOrdering.Matching);
        await Assert.That(arms.Where(static entry => entry.Value.Count > 0).Select(static entry => entry.Key))
            .IsEquivalentTo(["IEvent", "ISessionMessageAssistantContent", "ISessionMessageInfo", "ISessionMessageToolState", "IToolContent"], CollectionOrdering.Matching);
        await Assert.That(arms["ISessionMessageInfo"]).IsEquivalentTo([
            "SessionMessageAgentSelected", "SessionMessageAssistant", "SessionMessageIdle", "SessionMessageLocationSwitched",
            "SessionMessageModelSelected", "SessionMessageShell", "SessionMessageSkill", "SessionMessageSynthetic", "SessionMessageSystem", "SessionMessageUser",
        ], CollectionOrdering.Matching);
        await Assert.That(arms["ISessionMessageAssistantContent"]).IsEquivalentTo([
            "SessionMessageAssistantReasoning", "SessionMessageAssistantText", "SessionMessageAssistantTool",
        ], CollectionOrdering.Matching);
        await Assert.That(arms["IEvent"]).IsEquivalentTo(ExpectedEventArms(), CollectionOrdering.Matching);
        await Assert.That(arms["ISessionMessageToolState"]).IsEquivalentTo([
            "SessionMessageToolStateCompleted", "SessionMessageToolStateError", "SessionMessageToolStateRunning",
            "SessionMessageToolStateStreaming",
        ], CollectionOrdering.Matching);
        await Assert.That(arms["IToolContent"]).IsEquivalentTo(["ToolFileContent", "ToolTextContent"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Select_Should_Exclude_Open_Prefix_And_Nested_Arms_And_Keep_Wiring_Per_Union()
    {
        var open = Variant("Open", "open") with { EmitsExtensionData = true };
        var record = Variant("Selected", "selected");
        var selectedUnion = Union("IToolContent", Arm("Selected"), Arm("Open"), NestedArm("INested"),
            Arm("Mismatch") with { MarkerWireName = "other" }) with
        {
            PrefixVariant = new() { TypeName = "Prefix", Prefix = "rpc.", MarkerWireName = "type" },
        };
        var otherUnion = Union("IOther", Arm("Selected"));
        var selected = RecordReaderPolicy.Select([open, record], [selectedUnion, otherUnion], out var arms,
            new HashSet<(string, string)> { ("IToolContent", "Selected"), ("IToolContent", "Open"), ("IToolContent", "INested"), ("IToolContent", "Mismatch") });
        await Assert.That(selected.Keys).IsEquivalentTo(["Selected"], CollectionOrdering.Matching);
        await Assert.That(arms.Keys).IsEquivalentTo(["IOther", "IToolContent"], CollectionOrdering.Matching);
        await Assert.That(arms["IToolContent"]).IsEquivalentTo(["Selected"], CollectionOrdering.Matching);
        await Assert.That(arms["IOther"]).IsEmpty();
    }

    [Test]
    public async Task Select_Should_Refuse_A_Selected_Arm_Without_Its_Bound_Record()
    {
        var exception = await Assert.That(() => RecordReaderPolicy.Select([], [Union("IToolContent", Arm("Missing"))], out _,
                new HashSet<(string, string)> { ("IToolContent", "Missing") }))
            .Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).IsEqualTo("Selected reader arm 'IToolContent.Missing' has no bound object model.");
    }

    [Test]
    public async Task Select_Should_Order_Records_Independently_Of_Variant_Order()
    {
        var selected = RecordReaderPolicy.Select([Record("Zulu"), Record("Alpha")],
            [Union("IToolContent", Arm("Zulu"), Arm("Alpha"))], out var arms,
            new HashSet<(string, string)> { ("IToolContent", "Zulu"), ("IToolContent", "Alpha") });
        await Assert.That(selected.Keys).IsEquivalentTo(["Alpha", "Zulu"], CollectionOrdering.Matching);
        await Assert.That(arms["IToolContent"]).IsEquivalentTo(["Alpha", "Zulu"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Create_Should_Claim_Readers_Only_For_An_Explicit_Selection_And_Refuse_A_Collision()
    {
        var record = Record("Value");
        var candidates = new OwnershipCandidates { Models = [record, Record("ValueJsonReader")], Unions = [], HoistedInterfaces = [] };
        var schemaNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var empty = GeneratedTypeNameInventory.Create(candidates, [], [], schemaNames);
        await Assert.That(empty.Claims.Where(static claim => claim.Role == GeneratedTypeNameInventory.ClaimRole.Derived)
            .Select(static claim => claim.Name)).DoesNotContain("ValueJsonReader");
        var inventory = GeneratedTypeNameInventory.Create(candidates, [], [], schemaNames,
            new Dictionary<string, ObjectModelPlan>(StringComparer.Ordinal) { [record.Name] = record });
        var claim = inventory.Claims.Single(static claim => claim.Name == "ValueJsonReader" && claim.Role == GeneratedTypeNameInventory.ClaimRole.Derived);
        await Assert.That(claim.Subject).IsEqualTo("Value");
        await Assert.That(inventory.OtherClaims(claim).Select(static other => other.Subject))
            .IsEquivalentTo(["ValueJsonReader"], CollectionOrdering.Matching);
        var errors = new BindingErrorCollector();
        _ = OperationPlanBinder.CheckTypeNameCollisions(inventory, [], errors);
        await Assert.That(errors.Snapshot()).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, "ValueJsonReader", "type name 'ValueJsonReader' collides with another generated type"),
        ], CollectionOrdering.Matching);
    }

    private static IEnumerable<string> ExpectedReaders() => ExistingArmReaders.Concat(ExpectedEventArms()).Concat(ExpectedNestedReaders())
        .Order(StringComparer.Ordinal);

    private static string[] ExpectedEventArms() => [
        "AgentUpdated", "CommandUpdated", "EventServerConnected", "IntegrationUpdated", "McpResourcesChanged", "McpStatusChanged",
        "ModelUpdated", "ModelsDevRefreshed", "PermissionAsked", "PermissionReplied", "PluginUpdated", "ProjectUpdated", "ProviderUpdated",
        "ReferenceUpdated", "SessionCreated", "SessionExecutionStarted", "SessionExecutionSucceeded", "SessionInboxDelivered", "SessionInboxEnqueued",
        "SessionInstructionsUpdated", "SessionReasoningDelta", "SessionReasoningEnded", "SessionReasoningStarted", "SessionStepEnded", "SessionStepStarted",
        "SessionStepStreamed", "SessionTextDelta", "SessionTextEnded", "SessionTextStarted", "SessionToolCalled", "SessionToolInputDelta",
        "SessionToolInputEnded", "SessionToolInputStarted", "SessionToolSuccess", "SessionUsageUpdated", "SkillUpdated", "VcsBranchUpdated", "WebsearchUpdated",
    ];

    private static string[] SharedArms() => [
        "SessionCreated", "SessionExecutionStarted", "SessionExecutionSucceeded", "SessionInboxDelivered", "SessionInboxEnqueued",
        "SessionInstructionsUpdated", "SessionReasoningEnded", "SessionReasoningStarted", "SessionStepEnded", "SessionStepStarted", "SessionStepStreamed",
        "SessionTextEnded", "SessionTextStarted", "SessionToolCalled", "SessionToolInputEnded", "SessionToolInputStarted", "SessionToolSuccess",
    ];

    private static string[] ExpectedNestedReaders() => [
        "LocationPublicRef", "LocationRef", "McpResourcesChangedData", "McpStatusChangedData", "ModelRef", "PermissionAskedData", "PermissionRepliedData", "PermissionSourceTool", "ProjectUpdatedData",
        "SessionCreatedData", "SessionCreatedDurable", "SessionExecutionStartedData", "SessionExecutionStartedDurable",
        "SessionExecutionSucceededData", "SessionExecutionSucceededDurable", "SessionInboxDeliveredData", "SessionInboxDeliveredDurable",
        "SessionInboxEnqueuedData", "SessionInboxEnqueuedDurable", "SessionInstructionsUpdatedData", "SessionInstructionsUpdatedDurable",
        "SessionMessageAssistantReasoningTime", "SessionMessageAssistantTime", "SessionMessageAssistantToolTime", "SessionReasoningDeltaData", "SessionReasoningEndedData",
        "SessionReasoningEndedDurable", "SessionReasoningStartedData", "SessionReasoningStartedDurable", "SessionStepEndedData", "SessionStepEndedDurable",
        "SessionStepStartedData", "SessionStepStartedDurable", "SessionStepStreamedData", "SessionStepStreamedDurable", "SessionTextDeltaData",
        "SessionTextEndedData", "SessionTextEndedDurable", "SessionTextStartedData", "SessionTextStartedDurable", "SessionToolCalledData", "SessionToolCalledDurable",
        "SessionToolInputDeltaData", "SessionToolInputEndedData", "SessionToolInputEndedDurable", "SessionToolInputStartedData", "SessionToolInputStartedDurable",
        "SessionToolSuccessData", "SessionToolSuccessDurable", "SessionUsageUpdatedData", "TokenUsageInfo", "TokenUsageInfoCache", "VcsBranchUpdatedData",
    ];

    private static string[] ExpectedCallSites() => [
        "AgentUpdated.Location", "CommandUpdated.Location", "EventServerConnected.Location", "IntegrationUpdated.Location",
        "McpResourcesChanged.Data", "McpResourcesChanged.Location", "McpStatusChanged.Data", "McpStatusChanged.Location",
        "ModelUpdated.Location", "ModelsDevRefreshed.Location", "PermissionAsked.Data", "PermissionAsked.Location",
        "PermissionAskedData.Source",
        "PermissionReplied.Data", "PermissionReplied.Location", "PluginUpdated.Location", "ProjectUpdated.Data", "ProjectUpdated.Location",
        "ProviderUpdated.Location", "ReferenceUpdated.Location", "SessionCreated.Data", "SessionCreated.Durable", "SessionCreated.Location",
        "SessionCreatedData.Location", "SessionCreatedData.Model",
        "SessionExecutionStarted.Data", "SessionExecutionStarted.Durable", "SessionExecutionStarted.Location",
        "SessionExecutionSucceeded.Data", "SessionExecutionSucceeded.Durable", "SessionExecutionSucceeded.Location",
        "SessionInboxDelivered.Data", "SessionInboxDelivered.Durable", "SessionInboxDelivered.Location",
        "SessionInboxEnqueued.Data", "SessionInboxEnqueued.Durable", "SessionInboxEnqueued.Location",
        "SessionInstructionsUpdated.Data", "SessionInstructionsUpdated.Durable", "SessionInstructionsUpdated.Location",
        "SessionMessageAssistant.Model", "SessionMessageAssistant.Time", "SessionMessageAssistant.Tokens",
        "SessionMessageAssistantReasoning.Time", "SessionMessageAssistantTool.Time", "SessionReasoningDelta.Data", "SessionReasoningDelta.Location",
        "SessionReasoningEnded.Data", "SessionReasoningEnded.Durable", "SessionReasoningEnded.Location",
        "SessionReasoningStarted.Data", "SessionReasoningStarted.Durable", "SessionReasoningStarted.Location",
        "SessionStepEnded.Data", "SessionStepEnded.Durable", "SessionStepEnded.Location",
        "SessionStepEndedData.Tokens",
        "SessionStepStarted.Data", "SessionStepStarted.Durable", "SessionStepStarted.Location",
        "SessionStepStartedData.Model",
        "SessionStepStreamed.Data", "SessionStepStreamed.Durable", "SessionStepStreamed.Location", "SessionTextDelta.Data", "SessionTextDelta.Location",
        "SessionTextEnded.Data", "SessionTextEnded.Durable", "SessionTextEnded.Location",
        "SessionTextStarted.Data", "SessionTextStarted.Durable", "SessionTextStarted.Location",
        "SessionToolCalled.Data", "SessionToolCalled.Durable", "SessionToolCalled.Location", "SessionToolInputDelta.Data", "SessionToolInputDelta.Location",
        "SessionToolInputEnded.Data", "SessionToolInputEnded.Durable", "SessionToolInputEnded.Location",
        "SessionToolInputStarted.Data", "SessionToolInputStarted.Durable", "SessionToolInputStarted.Location",
        "SessionToolSuccess.Data", "SessionToolSuccess.Durable", "SessionToolSuccess.Location", "SessionUsageUpdated.Data", "SessionUsageUpdated.Location",
        "SessionUsageUpdatedData.Tokens", "SkillUpdated.Location", "TokenUsageInfo.Cache", "VcsBranchUpdated.Data", "VcsBranchUpdated.Location", "WebsearchUpdated.Location",
    ];
}
