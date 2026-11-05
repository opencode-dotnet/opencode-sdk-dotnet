using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Selects closed record arms and their explicitly listed nested reader call sites.</summary>
internal static class RecordReaderPolicy
{
    private static readonly IReadOnlySet<(string Union, string Variant)> Rollout = new HashSet<(string, string)>
    {
        ("ISessionMessageToolState", "SessionMessageToolStateCompleted"), ("ISessionMessageToolState", "SessionMessageToolStateError"),
        ("ISessionMessageToolState", "SessionMessageToolStateRunning"), ("ISessionMessageToolState", "SessionMessageToolStateStreaming"),
        ("IToolContent", "ToolFileContent"), ("IToolContent", "ToolTextContent"),
        ("IEvent", "SessionTextDelta"), ("IEvent", "SessionReasoningDelta"),
        ("IEvent", "SessionToolInputDelta"), ("IEvent", "SessionTextEnded"),
        ("IEvent", "AgentUpdated"), ("IEvent", "CommandUpdated"),
        ("IEvent", "IntegrationUpdated"), ("IEvent", "McpResourcesChanged"),
        ("IEvent", "McpStatusChanged"), ("IEvent", "ModelUpdated"),
        ("IEvent", "ModelsDevRefreshed"), ("IEvent", "PermissionAsked"),
        ("IEvent", "PermissionReplied"), ("IEvent", "PluginUpdated"),
        ("IEvent", "ProjectUpdated"), ("IEvent", "ProviderUpdated"),
        ("IEvent", "ReferenceUpdated"), ("IEvent", "EventServerConnected"),
        ("IEvent", "SessionCreated"), ("IEvent", "SessionExecutionStarted"),
        ("IEvent", "SessionExecutionSucceeded"), ("IEvent", "SessionInboxDelivered"),
        ("IEvent", "SessionInboxEnqueued"), ("IEvent", "SessionInstructionsUpdated"),
        ("IEvent", "SessionReasoningEnded"), ("IEvent", "SessionReasoningStarted"),
        ("IEvent", "SessionStepEnded"), ("IEvent", "SessionStepStarted"),
        ("IEvent", "SessionStepStreamed"), ("IEvent", "SessionTextStarted"),
        ("IEvent", "SessionToolCalled"), ("IEvent", "SessionToolInputEnded"),
        ("IEvent", "SessionToolInputStarted"), ("IEvent", "SessionToolSuccess"),
        ("IEvent", "SessionUsageUpdated"), ("IEvent", "SkillUpdated"),
        ("IEvent", "VcsBranchUpdated"), ("IEvent", "WebsearchUpdated"),
        ("ISessionMessageInfo", "SessionMessageAgentSelected"), ("ISessionMessageInfo", "SessionMessageAssistant"),
        ("ISessionMessageInfo", "SessionMessageIdle"), ("ISessionMessageInfo", "SessionMessageLocationSwitched"),
        ("ISessionMessageInfo", "SessionMessageModelSelected"), ("ISessionMessageInfo", "SessionMessageShell"),
        ("ISessionMessageInfo", "SessionMessageSkill"), ("ISessionMessageInfo", "SessionMessageSynthetic"),
        ("ISessionMessageInfo", "SessionMessageSystem"), ("ISessionMessageInfo", "SessionMessageUser"),
        ("ISessionMessageAssistantContent", "SessionMessageAssistantReasoning"), ("ISessionMessageAssistantContent", "SessionMessageAssistantText"),
        ("ISessionMessageAssistantContent", "SessionMessageAssistantTool"),
    };

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> NestedCallSites { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["SessionMessageAssistant"] = new HashSet<string>(StringComparer.Ordinal) { "Time", "Model", "Tokens" },
            ["SessionStepStartedData"] = new HashSet<string>(StringComparer.Ordinal) { "Model" },
            ["SessionCreatedData"] = new HashSet<string>(StringComparer.Ordinal) { "Model", "Location" },
            ["SessionStepEndedData"] = new HashSet<string>(StringComparer.Ordinal) { "Tokens" },
            ["SessionUsageUpdatedData"] = new HashSet<string>(StringComparer.Ordinal) { "Tokens" },
            ["PermissionAskedData"] = new HashSet<string>(StringComparer.Ordinal) { "Source" },
            ["TokenUsageInfo"] = new HashSet<string>(StringComparer.Ordinal) { "Cache" },
            ["SessionMessageAssistantTool"] = new HashSet<string>(StringComparer.Ordinal) { "Time" },
            ["SessionMessageAssistantReasoning"] = new HashSet<string>(StringComparer.Ordinal) { "Time" },
            ["SessionTextDelta"] = new HashSet<string>(StringComparer.Ordinal) { "Data", "Location" },
            ["SessionReasoningDelta"] = new HashSet<string>(StringComparer.Ordinal) { "Data", "Location" },
            ["SessionToolInputDelta"] = new HashSet<string>(StringComparer.Ordinal) { "Data", "Location" },
            ["SessionTextEnded"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["AgentUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Location" },
            ["CommandUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Location" },
            ["IntegrationUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Location" },
            ["McpResourcesChanged"] = new HashSet<string>(StringComparer.Ordinal) { "Data", "Location" },
            ["McpStatusChanged"] = new HashSet<string>(StringComparer.Ordinal) { "Data", "Location" },
            ["ModelUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Location" },
            ["ModelsDevRefreshed"] = new HashSet<string>(StringComparer.Ordinal) { "Location" },
            ["PermissionAsked"] = new HashSet<string>(StringComparer.Ordinal) { "Data", "Location" },
            ["PermissionReplied"] = new HashSet<string>(StringComparer.Ordinal) { "Data", "Location" },
            ["PluginUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Location" },
            ["ProjectUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Data", "Location" },
            ["ProviderUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Location" },
            ["ReferenceUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Location" },
            ["EventServerConnected"] = new HashSet<string>(StringComparer.Ordinal) { "Location" },
            ["SessionCreated"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionExecutionStarted"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionExecutionSucceeded"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionInboxDelivered"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionInboxEnqueued"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionInstructionsUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionReasoningEnded"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionReasoningStarted"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionStepEnded"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionStepStarted"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionStepStreamed"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionTextStarted"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionToolCalled"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionToolInputEnded"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionToolInputStarted"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionToolSuccess"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data", "Location" },
            ["SessionUsageUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Data", "Location" },
            ["SkillUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Location" },
            ["VcsBranchUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Data", "Location" },
            ["WebsearchUpdated"] = new HashSet<string>(StringComparer.Ordinal) { "Location" },
        };

    public static IReadOnlyDictionary<string, ObjectModelPlan> Select(IReadOnlyList<ModelPlan> models, IReadOnlyList<UnionPlan> unions,
        out IReadOnlyDictionary<string, IReadOnlySet<string>> readerArms, IReadOnlySet<(string Union, string Variant)>? rollout = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? callSites = null, BindingErrorCollector? errors = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(unions);
        rollout ??= Rollout;
        var objects = models.OfType<ObjectModelPlan>().ToDictionary(static model => model.Name, StringComparer.Ordinal);
        var selected = new SortedDictionary<string, ObjectModelPlan>(StringComparer.Ordinal);
        var arms = new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var union in unions)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            arms.Add(union.Name, names);
            foreach (var name in union.Variants.Where(variant => rollout.Contains((union.Name, variant.TypeName))
                         && !variant.IsNestedUnion && variant.MarkerWireName == union.MarkerWireName)
                         .Select(static variant => variant.TypeName))
            {
                if (!objects.TryGetValue(name, out var model))
                {
                    throw new InvalidOperationException($"Selected reader arm '{union.Name}.{name}' has no bound object model.");
                }

                if (!model.EmitsExtensionData)
                {
                    selected[model.Name] = model;
                    names.Add(model.Name);
                }
            }
        }

        var collected = errors ?? new BindingErrorCollector();
        SelectNested(objects, selected, callSites ?? NestedCallSites, collected);
        foreach (var (record, members) in callSites?.Where(site => !selected.ContainsKey(site.Key)) ?? [])
        {
            foreach (var member in members)
            {
                AddNestedError(collected, record, member);
            }
        }
        if (errors is null)
        {
            collected.ThrowIfAny();
        }
        readerArms = arms;
        return selected;
    }

    private static void SelectNested(Dictionary<string, ObjectModelPlan> objects,
        SortedDictionary<string, ObjectModelPlan> selected, IReadOnlyDictionary<string, IReadOnlySet<string>> callSites, BindingErrorCollector errors)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        int previousCount;
        do
        {
            previousCount = selected.Count;
            foreach (var (record, members) in callSites.Where(site => selected.ContainsKey(site.Key) && visited.Add(site.Key)))
            {
                foreach (var member in members)
                {
                    var property = objects[record].Properties.SingleOrDefault(property => property.Name == member);
                    if (property?.Type is not NamedTypeReferencePlan named || property.EmitsOptionalWrapper
                        || !objects.TryGetValue(named.Name, out var nested) || nested.EmitsExtensionData)
                    {
                        AddNestedError(errors, record, member);
                        continue;
                    }

                    selected[nested.Name] = nested;
                }
            }
        }
        while (selected.Count != previousCount);
    }

    private static void AddNestedError(BindingErrorCollector errors, string record, string member) =>
        errors.Add(BindingErrorCategory.Naming, record,
            $"Nested reader call site '{record}.{member}' must name a direct closed-record member of a selected record.");
}
