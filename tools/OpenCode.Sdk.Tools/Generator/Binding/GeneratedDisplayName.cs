using System.Text.RegularExpressions;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Renders a generated identifier as the prose the emitted XML documentation reads.</summary>
internal static class GeneratedDisplayName
{
    internal static readonly IReadOnlyList<string> BrandSpellings = Array.AsReadOnly(["OpenCode", "OAuth", "MCP"]);

    public static string Of(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var words = new List<string>();
        var pattern = "(?<brand>(?i:" + string.Join('|', BrandSpellings) + @"))(?=\P{Ll}|$)\p{Nd}*|\p{Lu}?\p{Ll}+\p{Nd}*|\p{Lu}+(?!\p{Ll})\p{Nd}*|\p{L}[\p{L}\p{Nd}]*|\p{Nd}+";
        foreach (Match match in Regex.Matches(name, pattern, RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout))
        {
            var token = match.Value;
            var brand = BrandSpellings.FirstOrDefault(spelling => string.Equals(spelling, match.Groups["brand"].Value, StringComparison.OrdinalIgnoreCase));
            var branded = brand is null ? null : string.Concat(brand, token.AsSpan(brand.Length));
            var word = branded ?? (token.Any(char.IsDigit) ? token : token.ToLowerInvariant());
            if (words.Count == 0 || !string.Equals(words[^1], word, StringComparison.OrdinalIgnoreCase))
            {
                words.Add(word);
            }
        }

        return string.Join(' ', words);
    }

    public static string WithIndefiniteArticle(string name)
    {
        var phrase = Of(name);
        var first = phrase.Split(' ')[0].ToLowerInvariant();
        var article = first switch
        {
            "user" or "users" or "union" or "unions" or "unit" or "units" or "unicode" or "unique" or "universal" or "university" or "one" => "a",
            "heir" or "heirs" or "honest" or "honor" or "honors" or "hour" or "hours" or "mcp" => "an",
            _ => first.Length > 0 && "aeiou".Contains(first[0], StringComparison.Ordinal) ? "an" : "a",
        };
        return $"{article} {phrase}";
    }

    public static string MemberSummary(string name) =>
        name == "Value" ? "Gets the value." : $"Gets the {Of(name)} value.";
}
