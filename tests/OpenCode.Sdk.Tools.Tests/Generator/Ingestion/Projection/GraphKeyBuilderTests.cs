using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Projection;

public sealed class GraphKeyBuilderTests
{
    [Test]
    public async Task Append_Should_Escape_Slash_In_Segment()
    {
        var keys = new GraphKeyBuilder();

        var pointer = keys.Append(string.Empty, "a/b");

        await Assert.That(pointer).IsEqualTo("/a~1b");
    }

    [Test]
    public async Task Append_Should_Escape_Tilde_In_Segment()
    {
        var keys = new GraphKeyBuilder();

        var pointer = keys.Append(string.Empty, "a~b");

        await Assert.That(pointer).IsEqualTo("/a~0b");
    }

    [Test]
    public async Task UnionBranch_Should_Use_And_Escape_Literal_Marker_Identity()
    {
        var keys = new GraphKeyBuilder();
        var marker = new LiteralMarker
        {
            PropertyName = "type/name",
            Kind = LiteralKind.String,
            Value = "created~new",
        };

        var pointer = keys.UnionBranch(string.Empty, "anyOf", 0, marker);

        await Assert.That(pointer).IsEqualTo("/anyOf/type~1name=created~0new");
    }

    [Test]
    public async Task UnionBranch_Should_Use_Ordinal_For_Unmarked_Branch()
    {
        var keys = new GraphKeyBuilder();

        var pointer = keys.UnionBranch(string.Empty, "oneOf", 3, marker: null);

        await Assert.That(pointer).IsEqualTo("/oneOf/3");
    }
}
