using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Decides the slice every generated model lives in. The rule is declared, never inferred from
/// reach, so a model's folder follows from its name:
/// <list type="number">
/// <item>a body request lives with the family whose operation sends it;</item>
/// <item>a union's unknown carrier and a structural union's kind follow their union;</item>
/// <item>an owner row places a name group no family prefix covers;</item>
/// <item>otherwise the longest family prefix of the name decides.</item>
/// </list>
/// A model none of them places is reported unplaced, and generation refuses until a row places it.
/// Sessions models then split into the curated areas their names match.
/// </summary>
internal static class ModelOwnershipPolicy
{
    public static ModelPlacement Place(OwnershipCandidates candidates, IReadOnlyList<ClientPlan> clients, GenerationCuration curation,
        BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(errors);

        var families = clients
            .SelectMany(static client => client.Operations)
            .Select(static operation => operation.RouteContainerName)
            .ToHashSet(StringComparer.Ordinal);
        var prefixes = FamilyPrefixTable.Create(families, curation, errors);
        var ownerRows = ModelOwnerRows.Create(curation.ModelOwners, families, errors);
        var areaRows = SessionAreaRows.Create(curation.SessionAreas, families, errors);
        var bodyFamilies = BodyRequestFamilies(clients, errors);

        var homes = new Dictionary<string, TypeHome>(StringComparer.Ordinal);
        var unplaced = new List<string>();
        foreach (var subject in Subjects(candidates))
        {
            var slice = bodyFamilies.TryGetValue(subject.Name, out var bodyFamily)
                ? bodyFamily
                : ownerRows.OwnerOf(subject, errors) ?? prefixes.FamilyOf(subject.Concept);
            if (slice is null)
            {
                unplaced.Add(subject.Name);
                continue;
            }

            // An alias a row confirms still places the model; one a row overrides does not.
            prefixes.RecordPlacement(subject.Concept, slice);

            // A body request sits at its family root, so it takes no model area.
            var area = string.Equals(slice, SessionAreaRows.AreaFamily, StringComparison.Ordinal) && bodyFamily is null
                ? areaRows.AreaOf(subject, errors)
                : null;
            homes.Add(subject.Name, new TypeHome { Slice = slice, Area = area });
        }

        AddKitMembers(candidates, homes);
        prefixes.ReportDeadAliases(errors);
        ownerRows.ReportDeadRows(errors);
        areaRows.ReportDeadPatterns(errors);
        return new ModelPlacement
        {
            Homes = homes,
            Unplaced = [.. unplaced.Order(StringComparer.Ordinal)],
            BodyRequests = [.. bodyFamilies.Keys.Where(homes.ContainsKey).Order(StringComparer.Ordinal)],
        };
    }

    private static IEnumerable<OwnershipSubject> Subjects(OwnershipCandidates candidates) => candidates.Models
        .Select(static model => new OwnershipSubject(model.Name, model.Name))
        .Concat(candidates.Unions.Select(static union => new OwnershipSubject(union.Name, union.ConceptName)))
        .Concat(candidates.HoistedInterfaces.Select(static hoisted =>
            new OwnershipSubject(hoisted.Name, CSharpNamePolicy.ToUnionConceptName(hoisted.Name))));

    /// <summary>Kit members follow their union, so a kit never splits across folders.</summary>
    private static void AddKitMembers(OwnershipCandidates candidates, Dictionary<string, TypeHome> homes)
    {
        foreach (var union in candidates.Unions.Where(union => homes.ContainsKey(union.Name)))
        {
            homes[union.UnknownTypeName] = homes[union.Name];
        }

        foreach (var structural in candidates.Models.OfType<StructuralUnionModelPlan>().Where(model => homes.ContainsKey(model.Name)))
        {
            homes[structural.KindTypeName] = homes[structural.Name];
        }
    }

    private static Dictionary<string, string> BodyRequestFamilies(IReadOnlyList<ClientPlan> clients, BindingErrorCollector errors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var operation in clients
                     .SelectMany(static client => client.Operations)
                     .Where(static operation => operation.RequestBody is { IsBinary: false }))
        {
            var typeName = operation.RequestBody!.TypeName;
            if (result.TryGetValue(typeName, out var family) && !string.Equals(family, operation.RouteContainerName, StringComparison.Ordinal))
            {
                errors.Add(BindingErrorCategory.Curation, typeName,
                    $"a body request sent by both '{family}' and '{operation.RouteContainerName}' has no single family root");
                continue;
            }

            result[typeName] = operation.RouteContainerName;
        }

        return result;
    }
}
