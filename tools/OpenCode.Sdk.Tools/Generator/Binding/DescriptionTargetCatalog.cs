using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>The generated types and directly declared properties the documentation channel can address.</summary>
internal static class DescriptionTargetCatalog
{
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Create(EmitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var targets = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var unions = plan.Unions.ToDictionary(static union => union.Name, StringComparer.Ordinal);
        foreach (var model in plan.Models)
        {
            var members = Members(model);
            targets.Add(model.Name, members.ToHashSet(StringComparer.Ordinal));
            if (model is StructuralUnionModelPlan structural)
            {
                targets.Add(structural.KindTypeName, new HashSet<string>(StringComparer.Ordinal));
            }
        }

        foreach (var union in plan.Unions)
        {
            // An inherited marker is not redeclared, so its documentation belongs to the outer interface.
            targets.Add(union.Name, union.HoistedMembers.Select(static member => member.Name)
                .Concat(DeclaresMarker(union, unions) ? [union.MarkerName] : []).ToHashSet(StringComparer.Ordinal));
            targets.Add(union.UnknownTypeName, new[] { union.MarkerName, "Payload" }
                .Concat(union.FixedMarker is { } marker ? [marker.Name] : []).ToHashSet(StringComparer.Ordinal));
        }

        foreach (var carrier in plan.HoistedInterfaces)
        {
            targets.Add(carrier.Name, carrier.Members.Select(static member => member.Name).ToHashSet(StringComparer.Ordinal));
        }

        foreach (var client in plan.Clients)
        {
            targets.Add(client.Name, client.SubClients.Select(static reference => reference.PropertyName).ToHashSet(StringComparer.Ordinal));
            foreach (var operation in client.Operations)
            {
                if (operation.QueryRequest is { RidesRequestBody: false } query)
                {
                    targets.Add(query.TypeName, query.Properties.Where(static property => !property.IsInherited)
                        .Select(static property => property.PropertyName).ToHashSet(StringComparer.Ordinal));
                }

                if (operation.Envelope is { } envelope)
                {
                    var members = new HashSet<string>(StringComparer.Ordinal);
                    if (envelope.PayloadName is { } payload)
                    {
                        _ = members.Add(payload);
                    }

                    if (envelope.Kind is EnvelopeKind.CursorList)
                    {
                        _ = members.Add("Cursor");
                    }

                    if (envelope.LocationTypeName is not null)
                    {
                        _ = members.Add("Location");
                    }

                    if (envelope.Kind is EnvelopeKind.Binary)
                    {
                        _ = members.Add("ContentType");
                    }

                    targets.Add(envelope.ResponseTypeName, members);
                }
            }
        }

        return targets;
    }

    /// <summary>The members a model's generated type declares directly, which a description row can name.</summary>
    private static IEnumerable<string> Members(ModelPlan model) => model switch
    {
        ObjectModelPlan record => record.Properties.Select(static property => property.Name)
            .Concat(record.RequestQueryProperties.Select(static property => property.PropertyName))
            .Concat(record.EmitsExtensionData ? [ObjectModelPlan.ExtensionDataMemberName] : []),
        EnumModelPlan enumeration => enumeration.Values.Select(static value => value.Name).Append(ExtensibleEnumMemberPolicy.IsKnown),
        StructuralUnionModelPlan union => union.Arms.Select(static arm => arm.Name).Concat(["Kind", "Unknown"]),
        _ => [],
    };

    private static bool DeclaresMarker(UnionPlan union, Dictionary<string, UnionPlan> unions)
    {
        var current = union.BaseTypeName;
        while (current is not null && unions.TryGetValue(current, out var outer))
        {
            if (outer.MarkerWireName == union.MarkerWireName && outer.MarkerKind == union.MarkerKind)
            {
                return false;
            }

            current = outer.BaseTypeName;
        }

        return true;
    }
}
