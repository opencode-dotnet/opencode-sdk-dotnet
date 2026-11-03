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
            ["SessionMessageAssistantTool"] = new HashSet<string>(StringComparer.Ordinal) { "Time" },
            ["SessionMessageAssistantReasoning"] = new HashSet<string>(StringComparer.Ordinal) { "Time" },
            ["SessionTextDelta"] = new HashSet<string>(StringComparer.Ordinal) { "Data" },
            ["SessionReasoningDelta"] = new HashSet<string>(StringComparer.Ordinal) { "Data" },
            ["SessionToolInputDelta"] = new HashSet<string>(StringComparer.Ordinal) { "Data" },
            ["SessionTextEnded"] = new HashSet<string>(StringComparer.Ordinal) { "Durable", "Data" },
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
        SelectNested(objects, selected, callSites ?? NestedCallSites.Where(site => selected.ContainsKey(site.Key))
            .ToDictionary(static site => site.Key, static site => site.Value, StringComparer.Ordinal), collected);
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
        var armRecords = selected.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var (record, members) in callSites)
        {
            foreach (var member in members)
            {
                var property = objects.GetValueOrDefault(record)?.Properties.SingleOrDefault(property => property.Name == member);
                if (!armRecords.Contains(record) || property?.Type is not NamedTypeReferencePlan named || property.EmitsOptionalWrapper
                    || !objects.TryGetValue(named.Name, out var nested) || nested.EmitsExtensionData)
                {
                    errors.Add(BindingErrorCategory.Naming, record,
                        $"Nested reader call site '{record}.{member}' must name a direct closed-record member of a selected record.");
                    continue;
                }

                selected[nested.Name] = nested;
            }
        }
    }
}
