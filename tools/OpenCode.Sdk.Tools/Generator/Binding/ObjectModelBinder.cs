using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class ObjectModelBinder(SchemaBindingContext context)
{
    private readonly SchemaBindingContext _context = context;

    public ObjectModelPlan? Bind(string key, string name, ObjectNode node, IReadOnlyList<string> implementedNames, bool isRequestReachable)
    {
        var errors = _context.Errors;
        if (!TryAdmitAdditionalProperties(key, node, errors, out var emitsExtensionData))
        {
            return null;
        }

        var properties = new List<ModelPropertyPlan>(node.Properties.Count);
        foreach (var property in node.Properties)
        {
            var type = _context.Types.Bind(key, property.Name, property.Schema);
            if (type is null)
            {
                continue;
            }

            // Before optional widening, IsNullable still means exactly "admits JSON null".
            // After widening it means "optional or nullable", so the tri-state wrapper must
            // read the two facts while they are still separable (ADR-0004).
            var emitsOptionalWrapper = isRequestReachable && !property.IsRequired && type.IsNullable;
            if (!property.IsRequired)
            {
                type = type with
                {
                    IsNullable = true
                };
            }

            var literal = property.Schema as LiteralNode;
            properties.Add(new ModelPropertyPlan
            {
                WireName = property.Name,
                Name = CSharpNamePolicy.ToPascalCase(property.Name),
                Type = type,
                IsRequired = property.IsRequired,
                IsLiteral = literal is not null,
                EmitsOptionalWrapper = emitsOptionalWrapper,
                LiteralKind = literal?.Kind,
                LiteralValue = literal?.Value,
                Description = property.Schema.Description ?? AliasDescription(property.Schema, _context.Document.Schemas),
            });
        }

        if (properties.Count != node.Properties.Count)
        {
            return null;
        }

        var duplicate = properties.GroupBy(static property => property.Name, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            errors.Add(BindingErrorCategory.Naming, key, $"multiple properties map to C# name '{duplicate.Key}'");
            return null;
        }

        if (emitsExtensionData && TakesExtensionDataMemberName(key, properties, errors))
        {
            return null;
        }

        return new ObjectModelPlan
        {
            Name = name,
            Namespace = GeneratedNamespace.Models,
            Description = node.Description,
            Properties = properties,
            ImplementedUnionNames = implementedNames,
            EmitsExtensionData = emitsExtensionData,
        };
    }

    /// <summary>
    /// Reads a primitive alias's description when the referencing member has none of its own.
    /// </summary>
    private static string? AliasDescription(SchemaNode schema, IReadOnlyDictionary<string, SchemaNode> schemas) =>
        schema is RefNode reference && schemas.TryGetValue(reference.Target, out var target) && target is PrimitiveNode
            ? target.Description
            : null;

    /// <summary>
    /// Only an unrestricted bag rides beside named properties without loss. The out value says
    /// whether the admitted object carries extension data.
    /// </summary>
    private static bool TryAdmitAdditionalProperties(string key, ObjectNode node, BindingErrorCollector errors, out bool emitsExtensionData)
    {
        emitsExtensionData = false;
        if (node.AdditionalProperties is not AdditionalPropertiesKind.Schema)
        {
            return true;
        }

        if (node.AdditionalPropertiesSchema is not (UnrestrictedNode or NullableNode { Inner: UnrestrictedNode }))
        {
            errors.Add(BindingErrorCategory.Schema, string.Concat(key, "/additionalProperties"),
                "named properties and typed additional properties cannot be represented without data loss; only an unrestricted bag rides as extension data");
            return false;
        }

        emitsExtensionData = true;
        return true;
    }

    /// <summary>A named property must not shadow either extension-data member.</summary>
    private static bool TakesExtensionDataMemberName(string key, IEnumerable<ModelPropertyPlan> properties, BindingErrorCollector errors)
    {
        var taken = properties.FirstOrDefault(static property =>
            string.Equals(property.Name, ObjectModelPlan.ExtensionDataMemberName, StringComparison.Ordinal)
            || string.Equals(property.Name, ObjectModelPlan.ExtensionDataBagName, StringComparison.Ordinal));
        if (taken is null)
        {
            return false;
        }

        errors.Add(BindingErrorCategory.Naming, key,
            $"a property maps to C# name '{taken.Name}', which the extension-data member takes");
        return true;
    }
}
