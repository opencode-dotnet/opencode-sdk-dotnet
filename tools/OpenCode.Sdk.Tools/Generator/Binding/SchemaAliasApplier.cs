using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Collapses schema aliases before binding: every reference to an aliased key is rewritten to
/// its target and the aliased schema leaves the graph, so downstream machinery never sees the
/// duplicate. Container kinds without a rewrite rule pass through unchanged; a reference they
/// might carry then dangles and fails reachability loudly.
/// </summary>
internal sealed class SchemaAliasApplier
{
    public SpecDocument Apply(SpecDocument document, StabilizeDuplicateCollapse collapse, IReadOnlyList<SchemaAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(document);

        var map = Compose(collapse, aliases);
        if (map.Count is 0)
        {
            return document;
        }

        var fold = new Fold(map, document.Schemas);
        var schemas = new Dictionary<string, SchemaNode>(StringComparer.Ordinal);
        foreach (var (key, schema) in document.Schemas)
        {
            if (!map.ContainsKey(key))
            {
                schemas[key] = Rewrite(schema, fold);
            }
        }

        return new SpecDocument
        {
            Operations = [.. document.Operations.Select(operation => RewriteOperation(operation, fold))],
            Schemas = schemas,
        };
    }

    /// <summary>
    /// Composes one collapse map: the mechanical stabilize rows first, curated rows last, so a
    /// curated row is the deliberate act a reviewer reads. The curation validator refuses a
    /// curated row the mechanical policy already implies, so the two never disagree over a key.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Compose(StabilizeDuplicateCollapse collapse,
        IReadOnlyList<SchemaAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(collapse);
        ArgumentNullException.ThrowIfNull(aliases);

        var map = new Dictionary<string, string>(collapse.Aliases, StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            map[alias.Schema] = alias.AliasOf;
        }

        return map;
    }

    private SpecOperation RewriteOperation(SpecOperation operation, Fold map) =>
        operation with
        {
            Parameters =
            [
                .. operation.Parameters.Select(parameter => parameter with
                {
                    Schema = Rewrite(parameter.Schema, map)
                })
            ],
            RequestBody = operation.RequestBody is null
                ? null
                : operation.RequestBody with
                {
                    Schema = Rewrite(operation.RequestBody.Schema, map)
                },
            Responses =
            [
                .. operation.Responses.Select(response => response with
                {
                    Schema = response.Schema is null ? null : Rewrite(response.Schema, map),
                    EffectStream = RewriteEffectStream(response.EffectStream, map),
                }),
            ],
        };

    private SpecEffectStreamContract? RewriteEffectStream(SpecEffectStreamContract? contract, Fold map)
    {
        if (contract is null)
        {
            return null;
        }

        var cause = contract.CauseSchema;
        var error = contract.ErrorSchema;
        return contract with
        {
            CauseSchema = cause is null ? null : Rewrite(cause, map),
            ErrorSchema = error is null ? null : Rewrite(error, map),
        };
    }

    private SchemaNode Rewrite(SchemaNode node, Fold map) => node switch
    {
        RefNode reference when map.Targets.TryGetValue(reference.Target, out var target) => reference with
        {
            Target = target,
            Description = reference.Description ?? map.Schemas.GetValueOrDefault(reference.Target)?.Description,
        },
        NullableNode nullable => nullable with
        {
            Inner = Rewrite(nullable.Inner, map)
        },
        ArrayNode array => array with
        {
            Item = Rewrite(array.Item, map)
        },
        TupleNode tuple => tuple with
        {
            Items = [.. tuple.Items.Select(item => Rewrite(item, map))]
        },
        DictionaryNode dictionary => dictionary with
        {
            Value = Rewrite(dictionary.Value, map)
        },
        UnionNode union => RewriteUnion(union, map),
        ObjectNode target => target with
        {
            Properties =
            [
                .. target.Properties.Select(property => property with
                {
                    Schema = Rewrite(property.Schema, map)
                })
            ],
            AdditionalPropertiesSchema = target.AdditionalPropertiesSchema is null
                ? null
                : Rewrite(target.AdditionalPropertiesSchema, map),
        },
        _ => node,
    };

    /// <summary>Collapsed branches can duplicate a reference; the duplicates fold away mechanically.</summary>
    private UnionNode RewriteUnion(UnionNode union, Fold map)
    {
        var branches = new List<SchemaNode>(union.Branches.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in union.Branches.Select(entry => Rewrite(entry, map)))
        {
            if (branch is RefNode reference && !seen.Add(reference.Target))
            {
                continue;
            }

            branches.Add(branch);
        }

        return union with
        {
            Branches = branches
        };
    }

    /// <summary>
    /// One collapse: each aliased key's target, and the graph before the collapse. A reference that
    /// folds onto its target keeps the folded schema's description when it carries none of its own,
    /// since a stabilize duplicate is often the use site's own description of a shared type.
    /// </summary>
    private sealed record Fold(IReadOnlyDictionary<string, string> Targets, IReadOnlyDictionary<string, SchemaNode> Schemas);
}
