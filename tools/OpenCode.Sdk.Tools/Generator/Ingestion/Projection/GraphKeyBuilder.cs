using System.Globalization;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal static class GraphKeyBuilder
{
    public static string Root(string wireNameOrOpId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wireNameOrOpId);

        return wireNameOrOpId;
    }

    public static string Append(string parentPointer, string segment)
    {
        ArgumentNullException.ThrowIfNull(parentPointer);
        ArgumentNullException.ThrowIfNull(segment);

        var escaped = segment
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

        return $"{parentPointer}/{escaped}";
    }

    public static string UnionBranch(string parentPointer, string keyword, int index, LiteralMarker? marker)
    {
        ArgumentNullException.ThrowIfNull(parentPointer);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var branchIdentity = marker is null
            ? index.ToString(CultureInfo.InvariantCulture)
            : $"{marker.PropertyName}={marker.Value}";
        return Append(Append(parentPointer, keyword), branchIdentity);
    }
}
