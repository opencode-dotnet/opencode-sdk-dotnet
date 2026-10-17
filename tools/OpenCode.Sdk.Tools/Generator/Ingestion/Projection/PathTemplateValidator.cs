using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal static class PathTemplateValidator
{
    public static void Check(string path, IReadOnlyList<SpecParameter> parameters, string location, IngestionErrorCollector errors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentNullException.ThrowIfNull(errors);

        var tokens = ReadTokens(path, location, errors);
        var declared = parameters
            .Where(static parameter => parameter.Location is SpecParameterLocation.Path)
            .Select(static parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var token in tokens.Where(token => !declared.Contains(token)).Order(StringComparer.Ordinal))
        {
            errors.Add(location, $"path token '{token}' is not declared as a path parameter");
        }

        foreach (var name in declared.Where(name => !tokens.Contains(name)).Order(StringComparer.Ordinal))
        {
            errors.Add(location, $"path parameter '{name}' is absent from the path template");
        }
    }

    private static HashSet<string> ReadTokens(string path, string location, IngestionErrorCollector errors)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        while (index < path.Length)
        {
            var open = path.IndexOf('{', index, StringComparison.Ordinal);
            var closeBeforeOpen = path.IndexOf('}', index, StringComparison.Ordinal);
            if (closeBeforeOpen >= 0 && (open < 0 || closeBeforeOpen < open))
            {
                errors.Add(location, $"path '{path}' contains an unmatched closing brace");
                index = closeBeforeOpen + 1;
                continue;
            }

            if (open < 0)
            {
                break;
            }

            var close = path.IndexOf('}', open + 1, StringComparison.Ordinal);
            if (close < 0)
            {
                errors.Add(location, $"path '{path}' contains an unmatched opening brace");
                break;
            }

            var token = path[(open + 1)..close];
            if (token.Length == 0 || token.Contains('{', StringComparison.Ordinal))
            {
                errors.Add(location, $"path '{path}' contains a malformed template token");
            }
            else if (!tokens.Add(token))
            {
                // Route builders substitute each token exactly once; a repeated placeholder
                // would leave raw braces in the built route.
                errors.Add(location, $"path token '{token}' must appear exactly once in the template");
            }

            index = close + 1;
        }

        return tokens;
    }
}
