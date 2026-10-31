using System.IO.Abstractions;
using System.Text.RegularExpressions;

namespace OpenCode.Sdk.Tools.Tests.Support;

/// <summary>
/// Proves a declined row's upstream omission against the pinned tree: the endpoint key must be an
/// element of both first-party client omit lists, each read from its one <c>new Set([...])</c>
/// declaration as plain string literals. A list that is missing, declared twice, commented out,
/// or holds anything but string literals refuses, so a key outside the lists or inside an expression proves nothing.
/// </summary>
internal static partial class UpstreamOmissionOracle
{
    public const string ClientPath = "packages/protocol/src/client.ts";

    private static readonly string[] ListNames = ["promiseOmitEndpoints", "effectOmitEndpoints"];

    /// <summary>Each problem with the given keys, as <c>key: problem</c>; none when every key stands.</summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(IFileSystem fileSystem, string root, string[] keys)
    {
        // A pin whose curation names no omitted key has nothing to prove.
        if (keys.Length == 0)
        {
            return [];
        }

        var path = fileSystem.Path.Combine(root, "external", "opencode", ClientPath);
        if (!fileSystem.File.Exists(path))
        {
            return [.. keys.Select(static key => $"{key}: {ClientPath} does not exist under external/opencode")];
        }

        var source = await fileSystem.File.ReadAllTextAsync(path, CancellationToken.None);
        var problems = new List<string>();
        foreach (var key in keys)
        {
            foreach (var name in ListNames)
            {
                if (ReadList(source, name) is not { } elements)
                {
                    problems.Add($"{key}: {name} is not one Set literal of plain strings");
                }
                else if (!elements.Contains(key))
                {
                    problems.Add($"{key}: {name} does not name the key");
                }
            }
        }

        return problems;
    }

    private static HashSet<string>? ReadList(string source, string name)
    {
        Match[] matches = [.. SetLiteral().Matches(source).Where(match => match.Groups["name"].Value == name)];
        if (matches.Length != 1)
        {
            return null;
        }

        // A block comment opened above the declaration and not yet closed has commented it out.
        var above = source[..matches[0].Index];
        if (above.LastIndexOf("/*", StringComparison.Ordinal) > above.LastIndexOf("*/", StringComparison.Ordinal))
        {
            return null;
        }

        // Anything left beside the literals and their commas means the list is no longer plain strings.
        var body = matches[0].Groups["body"].Value;
        return StringElement().Replace(body, string.Empty).All(static character => character == ',' || char.IsWhiteSpace(character))
            ? [.. StringElement().Matches(body).Select(static match => match.Groups["key"].Value)]
            : null;
    }

    [GeneratedRegex(@"^export const (?<name>\w+) = new Set\(\[(?<body>[^\]]*)\]\)", RegexOptions.Multiline, Timeout.Infinite)]
    private static partial Regex SetLiteral();

    [GeneratedRegex("\"(?<key>[^\"]*)\"", RegexOptions.None, Timeout.Infinite)]
    private static partial Regex StringElement();
}
