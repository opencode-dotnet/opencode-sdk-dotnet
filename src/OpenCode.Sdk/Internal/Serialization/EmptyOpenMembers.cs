using System.Collections.ObjectModel;
using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The read-only view an open model answers for its additional properties while the serializer
/// has not created its bag: System.Text.Json creates the bag on the first wire member no named
/// property claims, so a body that carries none allocates nothing and every such model shares
/// this one empty view.
/// </summary>
internal static class EmptyOpenMembers
{
    public static IReadOnlyDictionary<string, JsonElement> Instance { get; } =
        new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>(StringComparer.Ordinal));
}
