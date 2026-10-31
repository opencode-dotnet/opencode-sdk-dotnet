using System.Collections;
using static OpenCode.Sdk.Internal.WindowsPlatform;

namespace OpenCode.Sdk.Internal;

/// <summary>The pinned client's exact-key environment spread and platform-specific spawn resolution.</summary>
internal static class ServiceContenderEnvironmentComposer
{
    /// <summary>Overwrites exact spellings, retaining null markers until spawn resolution.</summary>
    public static Dictionary<string, string?> Overlay(IDictionary inherited, IReadOnlyDictionary<string, string?> overlay)
    {
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in inherited)
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                merged[key] = value;
            }
        }

        foreach (var entry in overlay)
        {
            merged[entry.Key] = entry.Value;
        }

        return merged;
    }

    /// <summary>On Windows, selects each uppercase group's first ordinal spelling before dropping nulls; elsewhere keeps exact spellings.</summary>
    public static Dictionary<string, string> Resolve(IReadOnlyDictionary<string, string?> merged)
    {
        var result = new Dictionary<string, string>(IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var seen = IsWindows ? new HashSet<string>(StringComparer.Ordinal) : null;
        IEnumerable<KeyValuePair<string, string?>> entries = IsWindows
            ? merged.OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            : merged;
        foreach (var entry in entries)
        {
            if ((seen is null || seen.Add(entry.Key.ToUpperInvariant())) && entry.Value is { } value)
            {
                result.Add(entry.Key, value);
            }
        }

        return result;
    }
}
