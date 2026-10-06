using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

/// <summary>
/// Recognizes a schema that spells a typed error: a required literal <c>_tag</c>. The marker name
/// appears here as ingestion's recognition key and again in <c>Binding/ErrorMarkerPolicy</c>, which
/// owns the binding-side marker the emitted converter scans for; the two must spell the same name.
/// Any other shape classifies as no error, and the binder refuses it wherever an error response
/// references it.
/// </summary>
internal sealed class ErrorStyleClassifier
{
    public ErrorStyle Classify(IReadOnlyList<SpecProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        return properties.Any(static property => property is { Name: "_tag", IsRequired: true, Schema: LiteralNode })
            ? ErrorStyle.EffectTag
            : ErrorStyle.None;
    }
}
