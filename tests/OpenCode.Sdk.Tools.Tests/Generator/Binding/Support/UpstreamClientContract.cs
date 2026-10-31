using System.Text.RegularExpressions;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding.Support;

/// <summary>
/// Reads the input contract of upstream's generated client at the pin: which input type each
/// route takes, which input field fills each query key, and the type text of every input field.
/// Routes come from <c>promise/generated/client.ts</c>; input types from <c>effect/api/api.ts</c>
/// and, for the endpoints upstream's codegen leaves out of that client, from the same codegen's
/// <c>promise/generated/types.ts</c>. The grammar is the codegen's own output, and anything outside
/// it reads as unknown rather than as a guess.
/// </summary>
internal sealed partial class UpstreamClientContract
{
    private readonly Dictionary<string, UpstreamRoute> _routes;
    private readonly Dictionary<string, Dictionary<string, string>> _inputs;

    private UpstreamClientContract(Dictionary<string, UpstreamRoute> routes, Dictionary<string, Dictionary<string, string>> inputs)
    {
        _routes = routes;
        _inputs = inputs;
    }

    /// <summary>Reads the routes of a promise client and the input types of the given sources, the first source winning a name both declare.</summary>
    public static UpstreamClientContract Parse(string client, params string[] typeSources)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(typeSources);

        var inputs = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (name, fields) in typeSources.SelectMany(ParseInputs))
        {
            _ = inputs.TryAdd(name, fields);
        }

        return new UpstreamClientContract(ParseRoutes(client), inputs);
    }

    /// <summary>Gets the type text of the input field that fills one query key of a route, or why there is none.</summary>
    public bool TryGetQueryType(string method, string path, string queryKey, out string type, out string problem)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(queryKey);

        type = string.Empty;
        if (!_routes.TryGetValue(RouteKey(method, path), out var route))
        {
            problem = $"upstream's client has no route for {method.ToUpperInvariant()} {path}";
            return false;
        }

        if (!_inputs.TryGetValue(route.Input, out var fields))
        {
            problem = $"upstream's client declares no {route.Input}Input type";
            return false;
        }

        if (!route.Query.TryGetValue(queryKey, out var field) || !fields.TryGetValue(field, out var fieldType))
        {
            problem = $"upstream's {route.Input}Input carries no field for the query key '{queryKey}'";
            return false;
        }

        type = fieldType;
        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// Classifies a field's type text as a number (<see langword="true"/>), as not a number
    /// (<see langword="false"/>), or as unknown (<see langword="null"/>). A union is a number only when
    /// every member is, and not one only when no member is; <c>null</c> and <c>undefined</c> members
    /// are ignored. A named type is classified by <paramref name="resolveNamed"/>; an indexed access
    /// into an object literal is classified by the field it selects.
    /// </summary>
    public static bool? IsNumeric(string type, Func<string, bool?> resolveNamed)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(resolveNamed);

        var members = TopLevelSplit(type.Trim(), '|')
            .Select(static member => member.Trim())
            .Where(static member => member is not ("null" or "undefined"))
            .Select(member => IsNumericMember(member, resolveNamed))
            .ToArray();
        if (members.Length is 0 || members.Any(static member => member is null))
        {
            return null;
        }

        if (members.All(static member => member is true))
        {
            return true;
        }

        return members.All(static member => member is false) ? false : null;
    }

    private static bool? IsNumericMember(string member, Func<string, bool?> resolveNamed)
    {
        if (member is "number")
        {
            return true;
        }

        if (member is "string" or "boolean" || IsStringLiteral(member))
        {
            return false;
        }

        var indexed = IndexedAccess().Match(member);
        if (indexed.Success)
        {
            return Fields(indexed.Groups["shape"].Value).TryGetValue(indexed.Groups["field"].Value, out var inner)
                ? IsNumeric(inner, resolveNamed)
                : null;
        }

        return NamedType().IsMatch(member) ? resolveNamed(member) : null;
    }

    /// <summary>A whole double-quoted literal, its escaped characters included and nothing after its closing quote.</summary>
    private static bool IsStringLiteral(string member) => StringLiteral().IsMatch(member);

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

    private static IEnumerable<(string Name, Dictionary<string, string> Fields)> ParseInputs(string source)
    {
        foreach (Match match in InputType().Matches(source))
        {
            var start = match.Index + match.Length;
            yield return (match.Groups["name"].Value, Fields(source[start..ClosingBrace(source, start)]));
        }
    }

    private static int ClosingBrace(string source, int start)
    {
        var depth = 1;
        foreach (var (index, character) in Scan(source, start))
        {
            depth += character switch
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
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in TopLevelFields(body))
        {
            var field = Field().Match(segment.Trim().TrimEnd(';'));
            if (field.Success)
            {
                fields[field.Groups["name"].Value] = Whitespace().Replace(field.Groups["type"].Value, " ")
                    .Replace(" | undefined", string.Empty, StringComparison.Ordinal)
                    .Trim();
            }
        }

        return fields;
    }

    private static IEnumerable<string> TopLevelFields(string body)
    {
        var starts = new List<int>();
        var depth = 0;
        foreach (var (index, character) in Scan(body, 0))
        {
            depth += Nesting(character);
            if (depth is 0 && string.CompareOrdinal(body, index, "readonly ", 0, "readonly ".Length) is 0
                           && (index is 0 || !char.IsLetterOrDigit(body[index - 1])))
            {
                starts.Add(index);
            }
        }

        starts.Add(body.Length);
        return starts.Zip(starts.Skip(1), (start, end) => body[start..end]);
    }

    private static IEnumerable<string> TopLevelSplit(string text, char separator)
    {
        var depth = 0;
        var start = 0;
        foreach (var (index, character) in Scan(text, 0))
        {
            depth += Nesting(character);
            if (depth is 0 && character == separator)
            {
                yield return text[start..index];
                start = index + 1;
            }
        }

        yield return text[start..];
    }

    private static int Nesting(char character) => character switch
    {
        '{' or '(' or '[' or '<' => 1,
        '}' or ')' or ']' or '>' => -1,
        _ => 0,
    };

    /// <summary>The characters outside double-quoted literals, with their positions; a backslash escapes the next character inside a literal.</summary>
    private static IEnumerable<(int Index, char Character)> Scan(string text, int start)
    {
        var quoted = false;
        var index = start;
        while (index < text.Length)
        {
            var character = text[index];
            if (quoted)
            {
                // An escape skips the character after it, so an escaped quote never ends the literal.
                index += character is '\\' ? 2 : 1;
                quoted = character is not '"';
                continue;
            }

            if (character is '"')
            {
                quoted = true;
            }
            else
            {
                yield return (index, character);
            }

            index++;
        }
    }

    // These read the pinned upstream client and test constants, never untrusted input, in time linear in
    // it. A wall-clock match timeout would only fail them on a stalled CI runner.
    [GeneratedRegex(
        @"\(\s*input\??: (?<input>\w+)Input,\s*requestOptions\?: RequestOptions,?\s*\)(?:: [^=]*?)? =>\s*\w+<[^\n]*?>\(\s*\{\s*method: ""(?<method>\w+)"",\s*path: `(?<path>[^`]*)`,(?:\s*query: \{(?<query>[^}]*)\},)?",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex RouteCall();

    [GeneratedRegex(
        @"(?<key>\w+|""[^""]+""): input\??\.?\[""(?<field>\w+)""\]",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex QueryEntry();

    [GeneratedRegex(
        @"\$\{encodeURIComponent\(input(?:\.(?<name>\w+)|\[""(?<name>\w+)""\])\)\}",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex PathValue();

    [GeneratedRegex(@"\$\{encodePath\([^)]*\)\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex WildcardTail();

    [GeneratedRegex(@"(?:export )?type (?<name>\w+)Input = \{", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex InputType();

    [GeneratedRegex(@"^readonly (?<name>\w+)\??:\s*(?<type>[\s\S]*)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex Field();

    [GeneratedRegex(@"^\{(?<shape>[\s\S]*)\}\[""(?<field>\w+)""\]$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex IndexedAccess();

    [GeneratedRegex(@"^""(?:[^""\\]|\\.)*""$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex NamedType();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex Whitespace();

    private sealed record UpstreamRoute(string Input, Dictionary<string, string> Query);
}
