using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Derives documentation only from unambiguous roles in the bound wire graph.</summary>
internal static class WireDocumentationPolicy
{
    public static EmitPlan Apply(EmitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var boundOperations = plan.Clients.SelectMany(static client => client.Operations).ToArray();
        var candidates = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var objects = plan.Models.OfType<ObjectModelPlan>().ToArray();
        var unions = plan.Unions.ToDictionary(static union => union.Name, StringComparer.Ordinal);
        var eventMembers = boundOperations.Where(static operation => operation.Stream is not null)
            .Select(static operation => operation.Stream!.PayloadTypeName)
            .Where(unions.ContainsKey)
            .Select(name => unions[name])
            .Where(static union => union.MarkerWireName is "type" && union.MarkerKind is LiteralKind.String)
            .SelectMany(union => UnionMemberCollector.Collect(union, unions))
            .ToHashSet(StringComparer.Ordinal);
        var uses = DocumentationReferenceCounter.Count(plan);
        foreach (var model in objects.Where(model => eventMembers.Contains(model.Name)))
        {
            var marker = model.Properties.SingleOrDefault(static property => property.WireName is "type"
                && property.IsLiteral && property.LiteralKind is LiteralKind.String);
            if (marker?.LiteralValue is not { } tag)
            {
                continue;
            }

            // An event record used anywhere besides its unions has a second role and keeps today's text.
            if (uses.GetValueOrDefault(model.Name) is 1)
            {
                Add(candidates, model.Name, $"The '{tag}' event.");
            }
            var data = model.Properties.SingleOrDefault(static property => property.WireName is "data");
            if (data?.Type is NamedTypeReferencePlan payload && uses.GetValueOrDefault(payload.Name) is 1)
            {
                Add(candidates, payload.Name, $"The data of the '{tag}' event.");
            }
        }

        foreach (var variant in plan.Unions
                     .Where(static union => union.ConceptName is ErrorMarkerPolicy.ErrorConceptName)
                     .SelectMany(static union => union.Variants)
                     .Where(static variant => !variant.IsNestedUnion)
                     .DistinctBy(static variant => variant.TypeName, StringComparer.Ordinal))
        {
            Add(candidates, variant.TypeName, $"The '{variant.Tag}' error.");
        }

        foreach (var group in boundOperations.Where(static operation => operation.RequestBody is { IsBinary: false })
                     .GroupBy(static operation => operation.RequestBody!.TypeName, StringComparer.Ordinal)
                     .Where(group => group.Count() is 1 && uses.GetValueOrDefault(group.Key) is 1))
        {
            Add(candidates, group.Key, OperationDocumentationPolicy.Summary("body", group.Single()));
        }

        foreach (var operation in boundOperations.Where(static operation => operation.QueryRequest is { RidesRequestBody: true }))
        {
            Add(candidates, operation.QueryRequest!.TypeName, OperationDocumentationPolicy.Summary("query", operation));
        }

        return plan with
        {
            Models = [.. plan.Models.Select(model => model with
            {
                Documentation = model.Documentation with
                {
                    Summary = model.Description is null && candidates.TryGetValue(model.Name, out var summaries) && summaries.Count is 1
                        ? summaries[0]
                        : null,
                },
            })],
            Clients = OperationDocumentationPolicy.Apply(plan.Clients),
        };
    }

    private static void Add(Dictionary<string, List<string>> candidates, string name, string summary)
    {
        if (!candidates.TryGetValue(name, out var summaries))
        {
            summaries = [];
            candidates.Add(name, summaries);
        }

        summaries.Add(summary);
    }
}
