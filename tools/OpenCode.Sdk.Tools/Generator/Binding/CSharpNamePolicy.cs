using System.Collections.Frozen;
using System.Text;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal static class CSharpNamePolicy
{
    /// <summary>
    /// Brand spellings the plain PascalCase rule would flatten (ADR-0004): a word that spells one,
    /// in any case, is written the brand's way, so an operation-derived <c>oauth</c> reads
    /// <c>OAuth</c> exactly as the schema-derived names already do.
    /// </summary>
    private static readonly FrozenDictionary<string, string> BrandSpellings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["oauth"] = "OAuth" }
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static string ToPascalCase(string wireName)
    {
        var result = PascalWords(wireName);

        // A letter prefix survives the public PascalCase naming rule. An underscore guard
        // is stripped by formatting and leaves numeric wire values as invalid identifiers.
        if (char.IsDigit(result[0]))
        {
            result = string.Concat("Value", result);
        }

        return result;
    }

    /// <summary>
    /// Pascal-cases a fragment that is appended to a non-empty stem — a JSON pointer segment, and
    /// above all a promoted union branch's ordinal. A leading digit needs no identifier guard in
    /// that position, and applying <see cref="ToPascalCase"/>'s guard would instead place an
    /// interior underscore inside the finished name. That spelling does not survive: the
    /// post-generation format pass rewrites it (IDE1006, and Sonar S101 reports it with no fix),
    /// which renames the declared type without renaming the file the emitter wrote it to, so the
    /// generated tree fails MA0048 rather than merely looking unusual.
    /// </summary>
    public static string ToPascalCaseFragment(string wireName) => PascalWords(wireName);

    private static string PascalWords(string wireName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wireName);

        var words = SplitWords(wireName);
        var result = new StringBuilder(wireName.Length);
        foreach (var word in words)
        {
            AppendPascalWord(result, word);
        }

        if (result.Length is 0)
        {
            throw new ArgumentException("Wire name must contain at least one letter or digit.", nameof(wireName));
        }

        return result.ToString();
    }

    public static string ToCamelCase(string wireName)
    {
        var pascal = ToPascalCase(wireName);
        return string.Concat(char.ToLowerInvariant(pascal[0]), pascal[1..]);
    }

    /// <summary>
    /// Names an internal-raw client after its public family name (ADR-0021). The family's
    /// public name stays free for the hand-written door, so <c>PtysClient</c> becomes
    /// <c>PtysRawClient</c>; a curated name not ending in <c>Client</c> takes the suffix.
    /// </summary>
    public static string ToRawClientName(string clientName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        const string suffix = "Client";
        return clientName.EndsWith(suffix, StringComparison.Ordinal)
            ? string.Concat(clientName.AsSpan(0, clientName.Length - suffix.Length), "Raw", suffix)
            : string.Concat(clientName, "Raw");
    }

    /// <summary>Names a union's emitted interface; unions are interfaces because a schema can
    /// belong to more than one of them (ADR-0011).</summary>
    public static string ToUnionInterfaceName(string conceptName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conceptName);

        return string.Concat("I", conceptName);
    }

    /// <summary>The inverse of <see cref="ToUnionInterfaceName"/>; names the members around a union.</summary>
    public static string ToUnionConceptName(string interfaceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);

        return interfaceName[0] is 'I' ? interfaceName[1..] : interfaceName;
    }

    public static bool IsValidIdentifier(string candidate)
    {
        if (string.IsNullOrEmpty(candidate) || !(char.IsLetter(candidate[0]) || candidate[0] is '_'))
        {
            return false;
        }

        // Reserved keywords would emit broken source; contextual keywords are legal identifiers.
        if (Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(candidate) is not Microsoft.CodeAnalysis.CSharp.SyntaxKind.None)
        {
            return false;
        }

        return candidate.Skip(1).All(static character => char.IsLetterOrDigit(character) || character is '_');
    }

    public static IReadOnlyList<string> SplitWords(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var words = new List<string>();
        var start = -1;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (!char.IsLetterOrDigit(current))
            {
                AddWord(value, start, index, words);
                start = -1;
                continue;
            }

            if (start < 0)
            {
                start = index;
                continue;
            }

            var previous = value[index - 1];
            var nextIsLower = index + 1 < value.Length && char.IsLower(value[index + 1]);
            var upperBoundary = char.IsUpper(current)
                                && (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower));
            var digitBoundary = char.IsDigit(current) != char.IsDigit(previous)
                                && (char.IsDigit(current) || char.IsDigit(previous));
            var boundary = upperBoundary || digitBoundary;
            if (!boundary)
            {
                continue;
            }

            AddWord(value, start, index, words);
            start = index;
        }

        AddWord(value, start, value.Length, words);
        return Array.AsReadOnly([.. words]);
    }

    private static void AppendPascalWord(StringBuilder result, string word)
    {
        if (BrandSpellings.TryGetValue(word, out var brand))
        {
            _ = result.Append(brand);
            return;
        }

        _ = result.Append(char.ToUpperInvariant(word[0]));
        if (word.Length > 1)
        {
            _ = result.Append(word[1..].ToLowerInvariant());
        }
    }

    private static void AddWord(string value, int start, int end, List<string> words)
    {
        if (start >= 0 && end > start)
        {
            words.Add(value[start..end]);
        }
    }
}
