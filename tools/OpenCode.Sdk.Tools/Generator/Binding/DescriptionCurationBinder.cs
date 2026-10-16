using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Validates reason-bearing documentation rows against final C# names and binds their plain text.</summary>
internal static class DescriptionCurationBinder
{
    public static EmitPlan Apply(EmitPlan plan, IReadOnlyList<DescriptionCuration> rows, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(errors);

        if (rows.Count is 0)
        {
            return plan;
        }

        var priorErrors = errors.Count;
        if (!CurationRowContract.Validate(nameof(GenerationCuration.Descriptions), rows, errors))
        {
            return plan;
        }

        Validate(DescriptionTargetCatalog.Create(plan), rows, errors);
        if (errors.Count != priorErrors)
        {
            return plan;
        }

        var byType = rows.GroupBy(static row => row.Type, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        return plan with
        {
            Models = [.. plan.Models.Select(model => BindModel(model, byType))],
            Unions = [.. plan.Unions.Select(union => union with
            {
                Documentation = Bind(union.Name, union.Documentation, byType),
                UnknownDocumentation = Bind(union.UnknownTypeName, union.UnknownDocumentation, byType),
            })],
            HoistedInterfaces = [.. plan.HoistedInterfaces.Select(carrier => carrier with
            {
                Documentation = Bind(carrier.Name, carrier.Documentation, byType),
            })],
            Clients = [.. plan.Clients.Select(client => client with
            {
                Documentation = Bind(client.Name, client.Documentation, byType),
                Operations = [.. client.Operations.Select(operation => operation with
                {
                    Envelope = operation.Envelope is { } envelope ? envelope with
                    {
                        Documentation = Bind(envelope.ResponseTypeName, envelope.Documentation, byType),
                    } : null,
                    QueryRequest = operation.QueryRequest is { RidesRequestBody: false } query ? query with
                    {
                        Documentation = Bind(query.TypeName, query.Documentation, byType),
                    } : operation.QueryRequest,
                })],
            })],
        };
    }

    private static void Validate(IReadOnlyDictionary<string, IReadOnlySet<string>> targets,
        IReadOnlyList<DescriptionCuration> rows, BindingErrorCollector errors)
    {
        foreach (var row in rows)
        {
            var subject = row.Type + (row.Member is null ? string.Empty : "." + row.Member);
            if (!targets.TryGetValue(row.Type, out var members))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "description type does not exist in the generated plan");
            }
            else if (row.Member is not null && !members.Contains(row.Member))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "description member does not exist as a declared property on the generated type");
            }

            if (string.IsNullOrWhiteSpace(row.Summary))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "description curation must declare a non-empty summary");
            }

        }
    }

    private static ModelPlan BindModel(ModelPlan model, IReadOnlyDictionary<string, DescriptionCuration[]> byType)
    {
        var documented = model with { Documentation = Bind(model.Name, model.Documentation, byType) };
        return documented is StructuralUnionModelPlan structural ? structural with
        {
            KindDocumentation = Bind(structural.KindTypeName, structural.KindDocumentation, byType),
        } : documented;
    }

    private static TypeDocumentationPlan Bind(string name, TypeDocumentationPlan prior,
        IReadOnlyDictionary<string, DescriptionCuration[]> byType)
    {
        if (!byType.TryGetValue(name, out var rows))
        {
            return prior;
        }

        var type = rows.SingleOrDefault(static row => row.Member is null);
        return prior with
        {
            Summary = type?.Summary ?? prior.Summary,
            Remarks = AppendRemarks(prior.Remarks, type?.Remarks),
            Members = rows.Where(static row => row.Member is not null).ToDictionary(static row => row.Member!,
                static row => new DocumentationPlan(row.Summary, row.Remarks), StringComparer.Ordinal),
        };
    }

    private static string? AppendRemarks(string? prior, string? added)
    {
        if (string.IsNullOrWhiteSpace(added))
        {
            return prior;
        }

        return string.IsNullOrWhiteSpace(prior) ? added : prior + " " + added;
    }
}
