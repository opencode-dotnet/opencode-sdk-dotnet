using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

/// <summary>
/// Recognizes a schema that spells a typed error: a required literal <c>_tag</c>.
/// Any other shape classifies as no error, and the binder refuses it wherever an error response
/// references it.
/// </summary>
internal sealed class ErrorStyleClassifier
{
    public const string WireName = "_tag";

    public ErrorStyle Classify(IReadOnlyList<SpecProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        return properties.Any(static property => property is { Name: WireName, IsRequired: true, Schema: LiteralNode })
            ? ErrorStyle.EffectTag
            : ErrorStyle.None;
    }
}
