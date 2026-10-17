using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Owns the one rule that names the wire property carrying an error's tag, so the error-union
/// binder, the per-status error map, and the emitted converter never spell it themselves: every
/// typed error is tagged by a required literal <c>_tag</c>. A schema without one is refused by
/// name, and a schema that carries a required literal <c>name</c> instead is refused as the
/// <c>{name, data}</c> error dialect, which the generator does not admit.
/// </summary>
internal static class ErrorMarkerPolicy
{
    /// <summary>The wire property every typed error carries its tag under.</summary>
    public const string WireName = ErrorStyleClassifier.WireName;

    /// <summary>The concept the error spine's union is named after; stream failure causes share the marker but not the concept.</summary>
    public const string ErrorConceptName = "OpenCodeError";

    /// <summary>The C# property exposing the wire tag on every typed error.</summary>
    public const string MarkerMemberName = "Tag";

    /// <summary>The shared interface implemented by every admitted error variant.</summary>
    public const string InterfaceTypeName = "I" + ErrorConceptName;

    /// <summary>The carrier preserving errors whose tags this pin does not declare.</summary>
    public const string UnknownTypeName = "Unknown" + ErrorConceptName;

    private const string NameDialectWireName = "name";

    /// <summary>
    /// Resolves the single literal marker an error schema dispatches on. A schema that does not
    /// carry exactly one required <c>_tag</c> literal resolves to <see langword="null"/> and
    /// states why.
    /// </summary>
    public static LiteralMarker? Resolve(ObjectNode node, out string problem)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.ErrorStyle is not ErrorStyle.EffectTag)
        {
            problem = node.LiteralMarkers.Any(static marker => string.Equals(marker.PropertyName, NameDialectWireName, StringComparison.Ordinal))
                ? $"the error carries a required '{NameDialectWireName}' literal and no '{WireName}' literal: the {{name, data}} error dialect is not admitted, so every typed error must be tagged by '{WireName}'"
                : $"error style '{node.ErrorStyle}' declares no tag marker property";
            return null;
        }

        var markers = node.LiteralMarkers.Where(static marker => string.Equals(marker.PropertyName, WireName, StringComparison.Ordinal)).ToArray();
        if (markers is not [var marker])
        {
            problem = $"a tagged error must declare exactly one required '{WireName}' literal";
            return null;
        }

        problem = string.Empty;
        return marker;
    }
}
