using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Serialization;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// Holds the bound query types to upstream's own generated client at the pin (ADR-0037). The
/// document projects some numeric query parameters as strings; upstream's client takes the number
/// its server decodes. The input types come from <c>packages/client/src/effect/api/api.ts</c>, and
/// for the endpoints upstream's codegen leaves out of that client (<c>effectOmitEndpoints</c>: the
/// raw-byte file routes) from the same codegen's <c>promise/generated/types.ts</c>; the routes come
/// from <c>promise/generated/client.ts</c>. A parameter the SDK binds as a string where that client
/// takes a number, or as an integer where it takes anything else, fails here, so a refresh that
/// changes either side is reviewed.
/// </summary>
public sealed partial class UpstreamClientQueryTypeTests
{
    private const string ClientRoot = "external/opencode/packages/client/src";

    /// <summary>
    /// Upstream type names that are not components of the document, with what they carry. A name
    /// missing from both fails the oracle rather than being guessed.
    /// </summary>
    private static readonly Dictionary<string, bool> NamedTypeIsNumeric = new(StringComparer.Ordinal)
    {
        // The message id brand; the document names the same string component Session.Message.ID.
        ["SessionMessage.ID"] = false,
    };

    [Test]
    public async Task Every_Bound_Query_Parameter_Should_Take_The_Type_Upstreams_Client_Takes()
    {
        var fileSystem = new RealFileSystem();
        var root = new RepositoryRoot(fileSystem).Locate();
        var api = await ReadAsync(fileSystem, root, "effect/api/api.ts");
        var client = await ReadAsync(fileSystem, root, "promise/generated/client.ts");
        var promiseTypes = await ReadAsync(fileSystem, root, "promise/generated/types.ts");
        var routes = ParseRoutes(client);
        var inputs = ParseInputs(api).Concat(ParseInputs(promiseTypes)).GroupBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Value, StringComparer.Ordinal);
        var (document, _, _) = await BindingTestHost.LoadPinnedInputsAsync();
        var plan = await new BindingTestHost().BindPinnedAsync();

        var problems = new List<string>();
        var checkedCount = 0;
        foreach (var operation in plan.Clients.SelectMany(static client => client.Operations).Where(static operation => operation.QueryRequest is not null))
        {
            var properties = operation.QueryRequest!.Properties
                .Where(static property => property.Kind is QueryValueKind.Text or QueryValueKind.Int32 or QueryValueKind.Int64)
                .ToArray();
            if (properties.Length is 0)
            {
                continue;
            }

            if (!routes.TryGetValue(RouteKey(operation.HttpMethod, operation.RouteTemplate), out var route)
                || !inputs.TryGetValue(route.Input, out var fields))
            {
                problems.Add($"{operation.OperationId}: upstream's client has no input for {operation.HttpMethod.ToUpperInvariant()} {operation.RouteTemplate}");
                continue;
            }

            foreach (var property in properties)
            {
                checkedCount++;
                var subject = $"{operation.OperationId}:{property.WireName}";
                if (!route.Query.TryGetValue(property.WireName, out var field) || !fields.TryGetValue(field, out var type))
                {
                    problems.Add($"{subject}: upstream's {route.Input}Input carries no field for the query parameter");
                }
                else if (IsNumeric(type, document) is not { } numeric)
                {
                    problems.Add($"{subject}: upstream's type '{type}' is neither a known number nor a known string");
                }
                else if (numeric != property.Kind is QueryValueKind.Int32 or QueryValueKind.Int64)
                {
                    problems.Add($"{subject}: upstream's client takes '{type}', the SDK binds {property.Kind}");
                }
            }
        }

        await Assert.That(problems).IsEmpty().Because(string.Join(Environment.NewLine, problems));
        await Assert.That(checkedCount).IsGreaterThan(0);
    }

    /// <summary>
    /// A type row's evidence is a file the source watch pins, and the pinned file still declares
    /// the decode the row quotes.
    /// </summary>
    [Test]
    public async Task Every_Type_Row_Should_Quote_A_Decode_In_A_Watched_File()
    {
        var fileSystem = new RealFileSystem();
        var root = new RepositoryRoot(fileSystem).Locate();
        var (_, _, curation) = await BindingTestHost.LoadPinnedInputsAsync();
        var watchText = await fileSystem.File.ReadAllTextAsync(fileSystem.Path.Combine(root, "spec", "source-watch.json"), CancellationToken.None);
        var watched = JsonSerializer.Deserialize(watchText, ToolJsonContext.Default.SourceWatch)!.Sources
            .Select(static source => source.Path)
            .ToHashSet(StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var row in curation.QueryParameterTypes)
        {
            var subject = $"{row.OperationId}:{row.Parameter}";
            if (!watched.Contains(row.Evidence.Path))
            {
                problems.Add($"{subject}: '{row.Evidence.Path}' is not in the source watch");
                continue;
            }

            var source = await fileSystem.File.ReadAllTextAsync(
                fileSystem.Path.Combine(root, "external", "opencode", row.Evidence.Path), CancellationToken.None);
            if (!source.Contains(row.Evidence.Anchor, StringComparison.Ordinal))
            {
                problems.Add($"{subject}: '{row.Evidence.Path}' no longer declares '{row.Evidence.Anchor}'");
            }
        }

        await Assert.That(curation.QueryParameterTypes).IsNotEmpty();
        await Assert.That(problems).IsEmpty().Because(string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Whether an upstream input type is a number: the literal <c>number</c>; a string, boolean or
    /// string literal union is not; a named type is what the document's component of that name is,
    /// or what <see cref="NamedTypeIsNumeric"/> records. Anything else is unknown.
    /// </summary>
    private static bool? IsNumeric(string type, SpecDocument document)
    {
        // The promise types spell a field as an indexed access into the route's input shape.
        var indexed = IndexedAccess().Match(type);
        if (indexed.Success)
        {
            return Fields(indexed.Groups["shape"].Value).TryGetValue(indexed.Groups["field"].Value, out var inner)
                ? IsNumeric(inner, document)
                : null;
        }

        var value = type.EndsWith(" | null", StringComparison.Ordinal) ? type[..^" | null".Length] : type;
        if (value is "number")
        {
            return true;
        }

        if (value is "string" or "boolean" || value is ['"', ..])
        {
            return false;
        }

        if (document.Schemas.TryGetValue(value, out var schema))
        {
            return schema switch
            {
                PrimitiveNode { Kind: PrimitiveKind.Integer or PrimitiveKind.Number } => true,
                PrimitiveNode { Kind: PrimitiveKind.String } or EnumNode => false,
                _ => null,
            };
        }

        return NamedTypeIsNumeric.TryGetValue(value, out var numeric) ? numeric : null;
    }

    private static async Task<string> ReadAsync(RealFileSystem fileSystem, string root, string path)
    {
        var full = fileSystem.Path.Combine(root, ClientRoot, path);
        if (!fileSystem.File.Exists(full))
        {
            throw new InvalidOperationException(
                $"Upstream's client is missing at '{full}'. Run `git submodule update --init --depth 1 external/opencode` first.");
        }

        return await fileSystem.File.ReadAllTextAsync(full, CancellationToken.None);
    }

    /// <summary>Reads each route of upstream's promise client: its input type, and which input field fills each query key.</summary>
    private static Dictionary<string, UpstreamRoute> ParseRoutes(string client)
    {
        var routes = new Dictionary<string, UpstreamRoute>(StringComparer.Ordinal);
        foreach (var groups in RouteCall().Matches(client).Select(static match => match.Groups))
        {
            var query = QueryEntry().Matches(groups["query"].Value)
                .ToDictionary(static entry => entry.Groups["key"].Value.Trim('"'), static entry => entry.Groups["field"].Value, StringComparer.Ordinal);
            routes[RouteKey(groups["method"].Value, NormalizePath(groups["path"].Value))] = new UpstreamRoute(groups["input"].Value, query);
        }

        return routes;
    }

    /// <summary>Upstream writes a route value as an interpolated <c>encodeURIComponent(input.x)</c> and a wildcard tail as <c>encodePath(...)</c>.</summary>
    private static string NormalizePath(string path) =>
        PathValue().Replace(WildcardTail().Replace(path, "*"), static match => "{" + match.Groups["name"].Value + "}");

    private static string RouteKey(string method, string path) => method.ToUpperInvariant() + " " + path;

    /// <summary>Reads every <c>XInput</c> object type: its top-level fields and their types, as written.</summary>
    private static IEnumerable<KeyValuePair<string, Dictionary<string, string>>> ParseInputs(string source)
    {
        foreach (Match match in InputType().Matches(source))
        {
            var start = match.Index + match.Length;
            var end = ClosingBrace(source, start);
            yield return new KeyValuePair<string, Dictionary<string, string>>(match.Groups["name"].Value, Fields(source[start..end]));
        }
    }

    private static int ClosingBrace(string source, int start)
    {
        var depth = 1;
        for (var index = start; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };
            if (depth is 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException("An upstream input type has no closing brace.");
    }

    /// <summary>A field starts at a top-level <c>readonly</c>; its type runs to the next one, whitespace folded and <c>| undefined</c> dropped.</summary>
    private static Dictionary<string, string> Fields(string body)
    {
        var starts = new List<int>();
        var depth = 0;
        var quoted = false;
        for (var index = 0; index < body.Length; index++)
        {
            var character = body[index];
            if (character is '"')
            {
                quoted = !quoted;
            }
            else if (!quoted)
            {
                depth += character switch
                {
                    '{' or '(' or '[' or '<' => 1,
                    '}' or ')' or ']' or '>' => -1,
                    _ => 0,
                };
                if (depth is 0 && string.CompareOrdinal(body, index, "readonly ", 0, "readonly ".Length) is 0
                               && (index is 0 || !char.IsLetterOrDigit(body[index - 1])))
                {
                    starts.Add(index);
                }
            }
        }

        starts.Add(body.Length);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < starts.Count; index++)
        {
            var field = Field().Match(body[starts[index]..starts[index + 1]].Trim().TrimEnd(';'));
            if (field.Success)
            {
                fields[field.Groups["name"].Value] = Whitespace().Replace(field.Groups["type"].Value, " ")
                    .Replace(" | undefined", string.Empty, StringComparison.Ordinal)
                    .Trim();
            }
        }

        return fields;
    }

    [GeneratedRegex(
        @"\(input\??: (?<input>\w+)Input, requestOptions\?: RequestOptions\)(?:: [^=]*?)? =>\s*\w+<[^\n]*?>\(\s*\{\s*method: ""(?<method>\w+)"",\s*path: `(?<path>[^`]*)`,(?:\s*query: \{(?<query>[^}]*)\},)?",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RouteCall();

    [GeneratedRegex(@"(?<key>\w+|""[^""]+""): input\??\.?\[""(?<field>\w+)""\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QueryEntry();

    [GeneratedRegex(@"\$\{encodeURIComponent\(input(?:\.(?<name>\w+)|\[""(?<name>\w+)""\])\)\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PathValue();

    [GeneratedRegex(@"\$\{encodePath\([^)]*\)\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WildcardTail();

    [GeneratedRegex(@"(?:export )?type (?<name>\w+)Input = \{", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InputType();

    [GeneratedRegex(@"^readonly (?<name>\w+)\??:\s*(?<type>[\s\S]*)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Field();

    [GeneratedRegex(@"^\{(?<shape>[\s\S]*)\}\[""(?<field>\w+)""\]$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IndexedAccess();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();

    private sealed record UpstreamRoute(string Input, Dictionary<string, string> Query);
}
