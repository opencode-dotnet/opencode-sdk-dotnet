namespace OpenCode.Sdk.Tools.Tests.Support;

internal static class ComponentKeyData
{
    public const string InvalidKey = "Bad Key";

    public const string ValidKey = "Session.Info_v-2";

    /// <summary>
    /// A letter from each case, the digit bounds, the three punctuation characters the pattern
    /// admits, characters just outside them, a non-ASCII letter, and a newline, because the
    /// pattern's '$' also matches before one trailing newline.
    /// </summary>
    private const string Alphabet = "aZ09.-_ /:#é\n";

    private const int MaxGeneratedLength = 3;

    private static readonly string[] EdgeKeys =
    [
        "a\r\n", "a\n\n", "\na", "a\r", "\r", "\t", "a\t",
        "`", "{", "@", "[", "^", "\\", "z", "A",
        "ａ", "Ａ", "١", "İ", "\u212A",
        "Session.Info", "Config.InfoEncoded#/properties/formatter",
    ];

    /// <summary>Every string of length 0 to 3 over the alphabet, then the edge keys.</summary>
    public static IReadOnlyList<string> EquivalenceKeys { get; } = BuildEquivalenceKeys();

    private static string[] BuildEquivalenceKeys()
    {
        var keys = new List<string> { string.Empty };
        var previous = new List<string> { string.Empty };
        for (var length = 1; length <= MaxGeneratedLength; length++)
        {
            var current = previous.SelectMany(static prefix => Alphabet.Select(letter => prefix + letter)).ToList();
            keys.AddRange(current);
            previous = current;
        }

        keys.AddRange(EdgeKeys);
        return [.. keys.Distinct(StringComparer.Ordinal)];
    }
}
