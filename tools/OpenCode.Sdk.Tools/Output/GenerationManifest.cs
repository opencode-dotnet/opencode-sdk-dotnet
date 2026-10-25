using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Output;

internal sealed record GenerationManifest
{
    [JsonPropertyName("files")]
    public required IReadOnlyList<string> Files
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>
    /// Gets the stabilize duplicates the binder folded without a curation row, each mapped to
    /// the base it collapses into. The section is written even when empty and sorts ordinally,
    /// so a duplicate arriving or retiring upstream is a one-line diff in a committed file. The
    /// property is nullable for the deserialization path only: an omitted section leaves the
    /// initializer's empty map standing, but a committed manifest carrying an explicit JSON null
    /// hands one through this accessor, which normalizes it. The getter never returns
    /// <see langword="null"/>, and nothing reads the section back — it exists to be diffed.
    /// </summary>
    [JsonPropertyName("implicitAliases")]
    public IReadOnlyDictionary<string, string>? ImplicitAliases
    {
        get;
        init
        {
            var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (schema, aliasOf) in value ?? ReadOnlyDictionary<string, string>.Empty)
            {
                sorted[schema] = aliasOf;
            }

            field = sorted;
        }
    } = new SortedDictionary<string, string>(StringComparer.Ordinal);
}
