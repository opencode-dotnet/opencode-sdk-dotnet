using System.Globalization;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Validates reason-bearing documentation rows against final C# names and binds their plain text.</summary>
internal static partial class DescriptionCurationBinder
{
    public static EmitPlan Apply(EmitPlan plan, IReadOnlyList<DescriptionCuration> rows, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(errors);

        if (rows.Count is 0)
        {
            return ResolveOwners(plan, errors);
        }

        var priorErrors = errors.Count;
        if (!CurationRowContract.Validate(nameof(GenerationCuration.Descriptions), rows, errors))
        {
            return plan;
        }

        Validate(plan, rows, errors);
        if (errors.Count != priorErrors)
        {
            return plan;
        }

        var byType = rows.GroupBy(static row => row.Type, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var bound = plan with
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
                Operations = [.. client.Operations.Select(operation =>
                {
                    var row = byType.GetValueOrDefault(client.Name)?.SingleOrDefault(row => row.Member == operation.MethodName);
                    return operation with
                    {
                        CuratedSummary = row?.Summary,
                        CuratedRemarks = row?.Remarks,
                        Envelope = operation.Envelope is { } envelope ? envelope with
                        {
                            Documentation = Bind(envelope.ResponseTypeName, envelope.Documentation, byType),
                        } : null,
                        QueryRequest = operation.QueryRequest is { RidesRequestBody: false } query ? query with
                        {
                            Documentation = Bind(query.TypeName, query.Documentation, byType),
                        } : operation.QueryRequest,
                    };
                })],
            })],
        };
        return ResolveOwners(bound, errors);
    }

    private static EmitPlan ResolveOwners(EmitPlan plan, BindingErrorCollector errors)
    {
        var owners = InheritedMemberOwners(plan, plan.Unions.ToDictionary(static union => union.Name, StringComparer.Ordinal), errors);
        return plan with
        {
            Models = [.. plan.Models.Select(model => model is ObjectModelPlan concrete
                ? concrete with { InheritedMemberOwners = owners[model.Name] } : model)],
        };
    }

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> InheritedMemberOwners(EmitPlan plan,
        IReadOnlyDictionary<string, UnionPlan> unions,
        BindingErrorCollector? errors = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(unions);
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var model in plan.Models.OfType<ObjectModelPlan>().OrderBy(static model => model.Name, StringComparer.Ordinal))
        {
            var owners = plan.HoistedInterfaces.Where(carrier => carrier.ImplementedBy.Contains(model.Name, StringComparer.Ordinal)
                    || model.ImplementedHoistedInterfaceNames.Contains(carrier.Name, StringComparer.Ordinal))
                .ToDictionary(static carrier => carrier.Name, static carrier => carrier.Documentation, StringComparer.Ordinal);
            var pending = new Queue<(IReadOnlyList<UnionChainLink> Chain, int Index)>(model.ImplementedUnionNames.Concat(plan.Unions
                .Where(union => union.Variants.Any(variant => string.Equals(variant.TypeName, model.Name, StringComparison.Ordinal))
                    || string.Equals(union.PrefixVariant?.TypeName, model.Name, StringComparison.Ordinal))
                .Select(static union => union.Name)).Where(unions.ContainsKey).Select(name => (unions[name].Chain, 0)));
            while (pending.TryDequeue(out var position))
            {
                var link = position.Chain[position.Index];
                if (!link.IsTerminal && owners.TryAdd(link.Name, unions[link.Name].Documentation)
                    && position.Index + 1 < position.Chain.Count)
                {
                    pending.Enqueue((position.Chain, position.Index + 1));
                }
            }

            var candidates = owners.SelectMany(owner => owner.Value.Members.Select(member =>
            {
                var property = model.ExplicitHoistedImplementations.FirstOrDefault(bridge =>
                    string.Equals(bridge.InterfaceName, owner.Key, StringComparison.Ordinal)
                    && string.Equals(bridge.MemberName, member.Key, StringComparison.Ordinal))?.PropertyName ?? member.Key;
                return (Property: property, Reference: owner.Key + "." + member.Key, Documentation: member.Value);
            })).Where(candidate => !model.Documentation.Members.ContainsKey(candidate.Property)
                && model.Properties.Any(property => string.Equals(property.Name, candidate.Property, StringComparison.Ordinal)));
            var members = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in candidates.GroupBy(static candidate => candidate.Property, StringComparer.Ordinal))
            {
                var ordered = group.OrderBy(static candidate => candidate.Reference, StringComparer.Ordinal).ToArray();
                if (ordered.Select(static candidate => candidate.Documentation).Distinct().Count() > 1)
                {
                    errors?.Add(BindingErrorCategory.Curation, model.Name + "." + group.Key,
                        "description has conflicting curated owners: " + string.Join(", ", ordered.Select(static candidate => candidate.Reference)));
                }

                members.Add(group.Key, ordered[0].Reference);
            }

            result.Add(model.Name, members);
        }

        return result;
    }

    private static void Validate(EmitPlan plan,
        IReadOnlyList<DescriptionCuration> rows, BindingErrorCollector errors)
    {
        var targets = DescriptionTargetCatalog.Create(plan);
        foreach (var row in rows)
        {
            var subject = row.Type + (row.Member is null ? string.Empty : "." + row.Member);
            if (!targets.TryGetValue(row.Type, out var members))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "description type does not exist in the generated plan");
            }
            else if (row.Member is not null && !members.Contains(row.Member))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "description member does not exist as a declared property or operation method on the generated type");
            }

            if (row.Summary is null)
            {
                if (string.IsNullOrWhiteSpace(row.Remarks) || !plan.Clients.Any(client => client.Name == row.Type
                    && client.Operations.Any(operation => operation.MethodName == row.Member)))
                {
                    errors.Add(BindingErrorCategory.Curation, subject,
                        "description curation must declare a summary unless an operation method carries non-empty remarks");
                }
            }
            else if (string.IsNullOrWhiteSpace(row.Summary))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "description curation must declare a non-empty summary");
            }

            ValidateSources(row, subject, errors);
        }
    }

    private static void ValidateSources(DescriptionCuration row, string subject, BindingErrorCollector errors)
    {
        if (row.Sources is null || row.Sources.Count is 0 or > 4)
        {
            var problem = row.Sources switch
            {
                null => "sources[0] must declare a sources list",
                { Count: 0 } => "sources[0] must declare 1 to 4 entries",
                _ => "sources[4] exceeds the maximum of 4 entries",
            };
            errors.Add(BindingErrorCategory.Curation, subject, "description " + problem);
            return;
        }

        var locations = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < row.Sources.Count; index++)
        {
            var source = row.Sources[index];
            string? problem = null;
            if (source is null)
            {
                problem = "must declare a source entry";
            }
            else if ((source.Location is null) == (source.Canon is null))
            {
                problem = "must declare exactly one of location or canon";
            }
            else if (source.Canon is { } canon && (canon.Contains("..", StringComparison.Ordinal) || !CanonLocation().IsMatch(canon)))
            {
                problem = "canon must be a docs/architecture/-relative .md path with a heading slug and no '..'";
            }
            else if (source.Location is { } declared && !SourceLocation().IsMatch(declared))
            {
                problem = "location must be a packages/-relative path with a positive line";
            }
            else if (source.Location is { } location && !locations.Add(location))
            {
                problem = $"repeats location '{source.Location}'";
            }
            else if (string.IsNullOrWhiteSpace(source.Excerpt))
            {
                problem = "excerpt must be non-empty";
            }
            else if (source.Excerpt.Contains('\n', StringComparison.Ordinal) || source.Excerpt.Contains('\r', StringComparison.Ordinal))
            {
                problem = "excerpt must be one line";
            }
            else if (source.Excerpt.Length is < 12 or > 200)
            {
                problem = "excerpt must contain 12 to 200 characters";
            }

            if (problem is not null)
            {
                errors.Add(BindingErrorCategory.Curation, subject,
                    string.Create(CultureInfo.InvariantCulture, $"description sources[{index}] {problem}"));
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\Apackages/(?:(?!\.{1,2}(?:/|:))[^/\\:\s]+/)*(?!\.{1,2}:)[^/\\:\s]+:[1-9][0-9]*\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial System.Text.RegularExpressions.Regex SourceLocation();

    [System.Text.RegularExpressions.GeneratedRegex(@"\Adocs/architecture/(?:[^/\\:#\s]+/)*[^/\\:#\s]+\.md#[\p{L}\p{M}\p{N}_-]+\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial System.Text.RegularExpressions.Regex CanonLocation();

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
            SummaryIsCurated = type?.Summary is not null || prior.SummaryIsCurated,
            Remarks = AppendRemarks(prior.Remarks, type?.Remarks),
            CuratedRemarks = type?.Remarks ?? prior.CuratedRemarks,
            Members = rows.Where(static row => row.Member is not null && row.Summary is not null).ToDictionary(static row => row.Member!,
                // The filter proves the summary; remarks-only operations bind separately.
                static row => new DocumentationPlan(row.Summary!, row.Remarks), StringComparer.Ordinal),
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
