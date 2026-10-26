using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace OpenCode.Sdk.Tests;

internal static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        FillJsonLiteralCache();
        UseProjectRelativeDirectory("Snapshots");
    }

    /// <summary>
    /// Fills System.Text.Json's cached <c>true</c>, <c>false</c> and <c>null</c> elements before any test
    /// runs, so tests reading literals in parallel see one element per literal.
    /// </summary>
    /// <remarks>
    /// The cache is filled without a lock (<c>JsonDocument.CreateForLiteral</c>): two tests reading their first
    /// literal at once could otherwise each get a different element for it.
    /// </remarks>
    private static void FillJsonLiteralCache()
    {
        foreach (var literal in (string[])["true", "false", "null"])
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(literal));
            _ = reader.Read();
            _ = JsonElement.ParseValue(ref reader);
        }
    }
}
