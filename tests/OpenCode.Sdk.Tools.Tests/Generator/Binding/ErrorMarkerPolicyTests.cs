using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class ErrorMarkerPolicyTests
{
    [Test]
    public async Task Resolve_Should_Refuse_A_Style_That_Declares_No_Marker_By_Name()
    {
        var node = Error(ErrorStyle.None, new LiteralMarker
        {
            PropertyName = "kind",
            Kind = LiteralKind.String,
            Value = "Boom",
        });

        var marker = ErrorMarkerPolicy.Resolve(node, out var problem);

        await Assert.That(marker).IsNull();
        await Assert.That(problem).IsEqualTo("error style 'None' declares no tag marker property");
    }

    [Test]
    public async Task Resolve_Should_Refuse_The_Name_Data_Dialect_By_Name()
    {
        var node = Error(ErrorStyle.None, new LiteralMarker
        {
            PropertyName = "name",
            Kind = LiteralKind.String,
            Value = "WorktreeError",
        });

        var marker = ErrorMarkerPolicy.Resolve(node, out var problem);

        await Assert.That(marker).IsNull();
        await Assert.That(problem).IsEqualTo(
            "the error carries a required 'name' literal and no '_tag' literal: the {name, data} error dialect is not admitted, so every typed error must be tagged by '_tag'");
    }

    [Test]
    public async Task Resolve_Should_Refuse_A_Tagged_Error_Whose_Tag_Literal_Is_Absent()
    {
        var node = Error(ErrorStyle.EffectTag, new LiteralMarker
        {
            PropertyName = "name",
            Kind = LiteralKind.String,
            Value = "Boom",
        });

        var marker = ErrorMarkerPolicy.Resolve(node, out var problem);

        await Assert.That(marker).IsNull();
        await Assert.That(problem).IsEqualTo("a tagged error must declare exactly one required '_tag' literal");
    }

    [Test]
    public async Task Resolve_Should_Return_The_Tag_Marker_It_Declares()
    {
        var node = Error(
            ErrorStyle.EffectTag,
            new LiteralMarker
            {
                PropertyName = "name",
                Kind = LiteralKind.String,
                Value = "WorktreeError",
            },
            new LiteralMarker
            {
                PropertyName = "_tag",
                Kind = LiteralKind.String,
                Value = "WorktreeError",
            });

        var marker = ErrorMarkerPolicy.Resolve(node, out var problem);

        await Assert.That(marker!.PropertyName).IsEqualTo("_tag");
        await Assert.That(marker.Value).IsEqualTo("WorktreeError");
        await Assert.That(problem).IsEmpty();
    }

    private static ObjectNode Error(ErrorStyle style, params LiteralMarker[] markers) =>
        new()
        {
            Properties = [],
            AdditionalProperties = AdditionalPropertiesKind.Forbidden,
            LiteralMarkers = markers,
            ErrorStyle = style,
        };
}
