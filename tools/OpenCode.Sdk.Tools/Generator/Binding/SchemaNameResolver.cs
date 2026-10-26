using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal static partial class SchemaNameResolver
{
    public static IReadOnlyDictionary<string, string> Resolve(SpecDocument document, ReachableSchemaSet reachable,
        IReadOnlyList<SpecOperation> selected, GenerationCuration curation, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reachable);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(errors);

        var responseRoots = reachable.ResponseRootKeys.ToHashSet(StringComparer.Ordinal);
        var requestRoots = RequestBodyRootNameResolver.Resolve(selected, errors);
        var queryEnumRoots = QueryEnumRootNameResolver.Resolve(document, selected);
        var payloadRoots = EnvelopePayloadRootNameResolver.Resolve(document, selected, errors);
        var effectStreamTypes = EffectStreamRootNameResolver.Resolve(document, selected);
        var artifacts = new ProjectionArtifactNamePolicy(document.Schemas.Keys);
        var curatedNames = curation
            .SchemaNames
            .DistinctBy(static row => row.Schema, StringComparer.Ordinal)
            .ToDictionary(static row => row.Schema, static row => row.DotNetName, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in reachable.GraphKeys)
        {
            if (responseRoots.Contains(key) || !document.Schemas.TryGetValue(key, out var schema)
                                            || !IsNominal(schema, document.Schemas))
            {
                continue;
            }

            CheckOrdinalBranches(key, schema, document.Schemas, errors);
            string name;
            if (curatedNames.TryGetValue(key, out var curatedName))
            {
                name = curatedName;
            }
            else if (effectStreamTypes.TryGetValue(key, out var effectStreamName))
            {
                name = effectStreamName;
            }
            else if (requestRoots.TryGetValue(key, out var requestName))
            {
                name = requestName;
            }
            else if (queryEnumRoots.TryGetValue(key, out var queryEnumName))
            {
                name = queryEnumName;
            }
            else if (payloadRoots.TryGetValue(key, out var payloadName))
            {
                // A promoted payload that happens to be a Marked union still carries the same
                // interface-name convention every other Marked union does (ADR-0011); the
                // operation-scoped stem is the concept name that convention wraps, not the
                // surfaced type name itself.
                name = schema is UnionNode { Classification: UnionClassification.Marked }
                    ? CSharpNamePolicy.ToUnionInterfaceName(payloadName)
                    : payloadName;
            }
            else
            {
                name = schema is UnionNode { Classification: UnionClassification.Marked }
                    ? CSharpNamePolicy.ToUnionInterfaceName(ResolveDefault(key, artifacts))
                    : ResolveDefault(key, artifacts);
                if (OrdinalPath().IsMatch(key[(key.IndexOf('#', StringComparison.Ordinal) + 1)..]))
                {
                    errors.Add(BindingErrorCategory.Curation, key,
                        $"schema '{key}' has mechanical name '{name}' that spells a union branch ordinal; add a schemaNames row that names the schema",
                        BindingErrorKind.MissingRow);
                }
            }

            if (owners.TryGetValue(name, out var existing))
            {
                errors.Add(BindingErrorCategory.Naming, key, $"C# type name '{name}' collides with schema '{existing}'");
                continue;
            }

            owners.Add(name, key);
            result.Add(key, name);
        }

        return new ReadOnlyDictionary<string, string>(result);
    }

    /// <summary>
    /// A union that carries no choice is not a type of its own — it binds to what its branches
    /// already are (<see cref="UnstructuredUnionPolicy"/>), so it never claims a C# name.
    /// </summary>
    internal static bool IsNominal(SchemaNode schema, IReadOnlyDictionary<string, SchemaNode> graph) => schema switch
    {
        UnionNode { Classification: UnionClassification.Structural } union => UnstructuredUnionPolicy.Collapse(union, graph) is null,
        ObjectNode or EnumNode or UnionNode => true,
        _ => false,
    };

    private static void CheckOrdinalBranches(string key, SchemaNode schema, IReadOnlyDictionary<string, SchemaNode> graph,
        BindingErrorCollector errors)
    {
        if (schema is not UnionNode union)
        {
            return;
        }

        var ordinalBranches = union.Branches.OfType<RefNode>()
            .Where(reference => reference.Target.StartsWith(key + "/", StringComparison.Ordinal)
                                && OrdinalBranch().IsMatch(reference.Target[key.Length..])
                                && graph.TryGetValue(reference.Target, out var branch) && IsNominal(branch, graph))
            .Select(static reference => reference.Target).ToArray();
        if (ordinalBranches.Length > 1)
        {
            errors.Add(BindingErrorCategory.Naming, key,
                $"union carries more than one inline branch identified by ordinal ('{string.Join("', '", ordinalBranches)}'); a reorder would swap their names silently");
        }
    }

    [GeneratedRegex(@"/(?:anyOf|oneOf)/[0-9]+(?:/|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex OrdinalPath();

    [GeneratedRegex(@"^/(?:anyOf|oneOf)/[0-9]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex OrdinalBranch();

    private static string ResolveDefault(string key, ProjectionArtifactNamePolicy artifacts)
    {
        var root = GetRoot(key);
        var result = new System.Text.StringBuilder(CSharpNamePolicy.ToPascalCase(NormalizeRoot(root, artifacts)));
        var hash = key.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
        {
            return result.ToString();
        }

        var segments = key[(hash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        while (index < segments.Length - 1)
        {
            if (string.Equals(segments[index], "properties", StringComparison.Ordinal))
            {
                _ = result.Append(CSharpNamePolicy.ToPascalCaseFragment(DecodePointer(segments[index + 1])));
                index += 2;
                continue;
            }

            // A promoted union branch is keyed by its marker ("anyOf/type=inline") or, for
            // unmarked branches, by ordinal; the branch name appends the marker value so
            // sibling branches (and their root) never collide: Prompt.FileSource#/anyOf/
            // type=inline -> PromptFileSourceInline. An ordinal appends as a bare digit
            // (ToPascalCaseFragment): the whole-name digit guard belongs to the stem, and an
            // interior underscore is a spelling the format pass rewrites out from under the
            // file the emitter named.
            if (string.Equals(segments[index], "anyOf", StringComparison.Ordinal)
                || string.Equals(segments[index], "oneOf", StringComparison.Ordinal))
            {
                var branch = DecodePointer(segments[index + 1]);
                var separator = branch.IndexOf('=', StringComparison.Ordinal);
                _ = result.Append(CSharpNamePolicy.ToPascalCaseFragment(separator >= 0 ? branch[(separator + 1)..] : branch));
                index += 2;
                continue;
            }

            index++;
        }

        return result.ToString();
    }

    private static string GetRoot(string key)
    {
        var hash = key.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? key : key[..hash];
    }

    /// <summary>Component roots normalize projection artifacts; operation roots retain their group/action identity.</summary>
    private static string NormalizeRoot(string root, ProjectionArtifactNamePolicy artifacts)
    {
        const string operationPrefix = "op:";
        if (!root.StartsWith(operationPrefix, StringComparison.Ordinal))
        {
            return artifacts.Normalize(root);
        }

        return root[operationPrefix.Length..];
    }

    private static string DecodePointer(string segment) => segment
        .Replace("~1", "/", StringComparison.Ordinal)
        .Replace("~0", "~", StringComparison.Ordinal);
}
