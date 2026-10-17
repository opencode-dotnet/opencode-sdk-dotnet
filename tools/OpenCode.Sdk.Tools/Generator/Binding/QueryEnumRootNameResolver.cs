using System.Collections.ObjectModel;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal static class QueryEnumRootNameResolver
{
    /// <summary>Names promoted inline query enums by <c>{RequestTypeName}{PropertyName}</c>; component enums keep their identity.</summary>
    /// <remarks>
    /// An inline query enum that ingestion promoted into the graph carries no schema identity
    /// of its own — its key is the operation's parameter pointer — so it is named from the
    /// request record it is a member of, mechanically <c>{RequestTypeName}{PropertyName}</c>.
    /// Each promoted key embeds one operation id and parameter pointer, so two selected
    /// operations cannot claim the same key. A reasoned schemaNames row still overrides.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Resolve(SpecDocument document, IReadOnlyList<SpecOperation> selected)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var operation in selected)
        {
            foreach (var parameter in operation.Parameters.Where(static parameter =>
                         parameter.Location is SpecParameterLocation.Query))
            {
                var key = QueryEnumShapePolicy.ResolveModelKey(parameter.Schema, document.Schemas);
                if (key is null || !key.Contains('#', StringComparison.Ordinal))
                {
                    continue;
                }

                names[key] = $"{OperationNamePolicy.RequestTypeName(operation)}{CSharpNamePolicy.ToPascalCase(parameter.Name)}";
            }
        }

        return new ReadOnlyDictionary<string, string>(names);
    }
}
