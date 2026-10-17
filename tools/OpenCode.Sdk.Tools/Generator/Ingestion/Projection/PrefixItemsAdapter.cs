using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal sealed class PrefixItemsAdapter
{
    private readonly SchemaProjector _schemaProjector;

    public PrefixItemsAdapter(SchemaProjector schemaProjector)
    {
        _schemaProjector = schemaProjector ?? throw new ArgumentNullException(nameof(schemaProjector));
    }

    public SchemaNode? Project(OpenApiSchema schema, string root, string pointer, string location, ProjectionState state)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentNullException.ThrowIfNull(state);

        if (schema.UnrecognizedKeywords is null
            || !schema.UnrecognizedKeywords.TryGetValue("prefixItems", out var rawPrefixItems)
            || rawPrefixItems is not JsonArray prefixItems)
        {
            state.Errors.Add(location, "prefixItems was admitted but its raw array was unavailable");
            return null;
        }

        if (schema.Items is not null)
        {
            return ProjectNonEmptyArray(schema, prefixItems, root, pointer, location, state);
        }

        if (!ValidateArity(schema, prefixItems.Count, root, pointer, state.Errors))
        {
            return null;
        }

        var items = new List<SchemaNode>(prefixItems.Count);
        for (var index = 0; index < prefixItems.Count; index++)
        {
            var itemPointer = GraphKeyBuilder.Append(GraphKeyBuilder.Append(pointer, "prefixItems"), index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var itemLocation = string.Concat(root, itemPointer);
            if (prefixItems[index] is not { } rawItem)
            {
                state.Errors.Add(itemLocation, "prefixItems entries cannot be null");
                continue;
            }

            var parsed = OpenApiModelFactory.Parse<OpenApiSchema>(rawItem.ToJsonString(), OpenApiSpecVersion.OpenApi3_1, state.Document,
                out var diagnostic, "json");
            foreach (var error in diagnostic.Errors)
            {
                state.Errors.Add(itemLocation, error.Message);
            }

            if (diagnostic.Errors.Count > 0)
            {
                continue;
            }

            if (parsed is null)
            {
                state.Errors.Add(itemLocation, "prefixItems entry did not produce a schema");
                continue;
            }

            var projected = _schemaProjector.Project(parsed, root, itemPointer, state);
            if (projected is not null)
            {
                items.Add(projected);
            }
        }

        return items.Count != prefixItems.Count
            ? null
            : new TupleNode
            {
                Description = schema.Description,
                Format = schema.Format,
                Items = items,
            };
    }

    private ArrayNode? ProjectNonEmptyArray(
        OpenApiSchema schema,
        JsonArray prefixItems,
        string root,
        string pointer,
        string location,
        ProjectionState state)
    {
        // The v2 dialect encodes a non-empty homogeneous array as a single-element
        // prefixItems paired with an items schema referencing the same target (Effect's
        // NonEmptyArray). The prefix element adds only a minimum-length validation, which
        // the projection ignores like every other validation keyword, so the schema is an
        // ordinary array of the shared target. Every other items+prefixItems combination
        // stays refused.
        if (prefixItems.Count != 1
            || prefixItems[0] is not JsonObject prefixElement
            || prefixElement.Count != 1
            || !prefixElement.TryGetPropertyValue("$ref", out var rawReference)
            || rawReference is not JsonValue referenceValue
            || !referenceValue.TryGetValue<string>(out var reference)
            || schema.Items is not OpenApiSchemaReference itemsReference
            || !string.Equals(reference, "#/components/schemas/" + itemsReference.Reference.Id, StringComparison.Ordinal))
        {
            state.Errors.Add(location, "items with prefixItems is supported only as a single prefix element sharing the items $ref target");
            return null;
        }

        return _schemaProjector.ProjectArrayItems(schema, itemsReference, root, pointer, state);
    }

    private static bool ValidateArity(OpenApiSchema schema, int arity, string root, string pointer, IngestionErrorCollector errors)
    {
        var isValid = true;
        var arityText = arity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (schema.MinItems != arity)
        {
            errors.Add(string.Concat(root, GraphKeyBuilder.Append(pointer, "minItems")), $"minItems must equal tuple arity {arityText}");
            isValid = false;
        }

        if (schema.MaxItems == arity)
        {
            return isValid;
        }

        errors.Add(string.Concat(root, GraphKeyBuilder.Append(pointer, "maxItems")), $"maxItems must equal tuple arity {arityText}");

        return false;
    }
}
